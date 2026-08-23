using System.Text;
using Microsoft.Extensions.Logging;
using YtPipeline.Core.Models;
using YtPipeline.Core.Services;
using YtPipeline.Stages.Process;
using YtPipeline.Stages.Script;
using YtPipeline.Stages.Voice;

namespace YtPipeline.Stages.Music;

/// <summary>
/// Builds an ordered background-music playlist for a video run.
///
/// Claude ranks tracks from the library by how well their name/vibe fits the script's
/// tone and topic.  We then probe the actual durations of the selected files and include
/// just enough tracks to cover the full video length.  The last track is always looped
/// by FFmpeg (<c>-stream_loop -1</c>), so even a slight underestimate is safe.
///
/// Returns an empty list when music is disabled, the library is empty or missing, or
/// anything goes wrong — the video is assembled without music and the run continues.
/// </summary>
public sealed class MusicSelector(
    ProcessRunner process,
    ClaudeOptions claudeOptions,
    MusicOptions options,
    ILogger<MusicSelector> logger)
{
    private static readonly string[] AudioExtensions = [".mp3", ".wav", ".m4a", ".flac"];

    /// <summary>
    /// Conservative per-track minimum assumed when deciding how many tracks to request
    /// from Claude.  Massobeats tracks are typically 2–4 min; 120 s keeps us safely
    /// over-requesting so we never come up short.
    /// </summary>
    private const double MinTrackSecondsEstimate = 120.0;

    /// <summary>
    /// Selects and orders background music tracks for a video of the given duration.
    /// Returns an empty list (no music) on any failure.
    /// </summary>
    /// <param name="enabled">
    /// Per-run override for <see cref="Channel.Music"/>, letting one video opt in or out without
    /// changing the channel. Null follows the channel. The app-wide switch still wins over both —
    /// turning music off globally means the library isn't used at all.
    /// </param>
    public async Task<IReadOnlyList<string>> SelectAsync(
        Channel channel, ScriptDocument script, double audioSeconds, CancellationToken ct,
        bool? enabled = null)
    {
        if (!options.Enabled) return [];

        if (!(enabled ?? channel.Music))
        {
            logger.LogInformation("Music disabled for channel '{Channel}' — voice-only video.", channel.Name);
            return [];
        }

        if (string.IsNullOrWhiteSpace(options.LibraryPath))
        {
            logger.LogDebug("Music disabled: no LibraryPath configured.");
            return [];
        }

        if (!Directory.Exists(options.LibraryPath))
        {
            logger.LogWarning("Music library path not found: {Path}", options.LibraryPath);
            return [];
        }

        var files = Directory.EnumerateFiles(options.LibraryPath)
            .Where(f => AudioExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (files.Count == 0)
        {
            logger.LogWarning("Music library has no audio files: {Path}", options.LibraryPath);
            return [];
        }

        try
        {
            var shortNames = files.Select(f => StripLabel(Path.GetFileNameWithoutExtension(f))).ToList();

            // Ask Claude for more tracks than we likely need so we have ranked options to
            // probe against.  We use a conservative 120 s/track floor so longer videos
            // always get an adequate pool to draw from.
            int nRequest = Math.Min(files.Count,
                Math.Max(2, (int)Math.Ceiling(audioSeconds / MinTrackSecondsEstimate) + 1));

            var prompt = BuildPrompt(script, shortNames, nRequest);
            var result = await process.RunOrThrowAsync(
                claudeOptions.Executable, claudeOptions.Args, stdin: prompt, ct: ct);

            var json = ClaudeCliScriptGenerator.ExtractFirstJsonObject(result.StdOut);
            if (json is null)
            {
                logger.LogWarning("Music selector: no JSON in Claude output — running without music.");
                return [];
            }

            var response = Json.Deserialize<MusicChoice>(json);
            if (response?.Tracks is null || response.Tracks.Count == 0)
            {
                logger.LogWarning("Music selector: empty track list from Claude — running without music.");
                return [];
            }

            // Resolve Claude's short names back to real file paths (preserve Claude's order).
            var resolved = new List<string>();
            foreach (var chosen in response.Tracks)
            {
                var path = ResolveTrack(files, shortNames, chosen.Trim());
                if (path is not null && !resolved.Contains(path))
                    resolved.Add(path);
            }

            if (resolved.Count == 0)
            {
                logger.LogWarning("Music selector: Claude returned names that matched no files — running without music.");
                return [];
            }

            // Probe actual durations and collect tracks until we cover the video length.
            // FFmpeg loops the last track, so we stop as soon as coverage >= audioSeconds.
            var playlist = new List<string>();
            double covered = 0;
            foreach (var path in resolved)
            {
                playlist.Add(path);
                var dur = ProbeSeconds(path);
                covered += dur;
                if (covered >= audioSeconds) break;
            }

            // If we ran out of Claude's suggestions before covering the video, add more
            // from the library (those not already in the playlist) to keep variety.
            if (covered < audioSeconds)
            {
                foreach (var path in files)
                {
                    if (playlist.Contains(path)) continue;
                    playlist.Add(path);
                    covered += ProbeSeconds(path);
                    if (covered >= audioSeconds) break;
                }
            }

            var names = string.Join(" → ", playlist.Select(p => StripLabel(Path.GetFileNameWithoutExtension(p))));
            logger.LogInformation(
                "Music playlist ({Count} track{S}): {Names}  [covers {Covered:F0}s / video {Total:F0}s]",
                playlist.Count, playlist.Count == 1 ? "" : "s", names, covered, audioSeconds);

            return playlist;
        }
        catch (Exception ex)
        {
            logger.LogWarning("Music selection failed ({Msg}) — running without music.", ex.Message);
            return [];
        }
    }

    // -------------------------------------------------------------------------

    private static string BuildPrompt(ScriptDocument script, List<string> shortNames, int nRequest)
    {
        var beats = script.Beats.OrderBy(b => b.Index).ToList();
        var opening = beats.FirstOrDefault()?.Narration?.Trim() ?? "";
        var closing = beats.LastOrDefault()?.Narration?.Trim() ?? "";

        var sb = new StringBuilder();
        sb.AppendLine("You are building a background music playlist for a YouTube video.");
        sb.AppendLine("The music plays softly under the voiceover — it should fit the emotional tone and topic.");
        sb.AppendLine();
        sb.AppendLine($"Video title: \"{script.Title}\"");
        if (!string.IsNullOrWhiteSpace(opening))
            sb.AppendLine($"Opening narration: \"{opening}\"");
        if (!string.IsNullOrWhiteSpace(closing) && closing != opening)
            sb.AppendLine($"Closing narration: \"{closing}\"");
        sb.AppendLine();
        sb.AppendLine($"Pick exactly {nRequest} DIFFERENT tracks from the list below, ordered from best fit to least.");
        sb.AppendLine("Track 1 will play first, track 2 will continue after it ends, and so on — so choose tracks");
        sb.AppendLine("whose moods flow naturally from one to the next (no jarring mood shifts mid-video).");
        sb.AppendLine();
        sb.AppendLine("Available tracks (lo-fi / chill beat names — use the vibe of the name to match the video's mood):");
        foreach (var name in shortNames)
            sb.AppendLine($"- {name}");
        sb.AppendLine();
        sb.AppendLine($"Return ONLY this JSON (no prose, no markdown, no explanation) with exactly {nRequest} entries:");
        sb.AppendLine("{\"tracks\": [\"<track1>\", \"<track2>\", ...]}");
        sb.AppendLine("Use the exact names from the list above.");
        return sb.ToString();
    }

    /// <summary>Find the library file whose stripped short name matches what Claude returned.</summary>
    private static string? ResolveTrack(List<string> files, List<string> shortNames, string chosen)
    {
        // Exact match first.
        for (int i = 0; i < shortNames.Count; i++)
            if (string.Equals(shortNames[i], chosen, StringComparison.OrdinalIgnoreCase))
                return files[i];

        // Fuzzy: chosen is contained in the short name or vice versa.
        for (int i = 0; i < shortNames.Count; i++)
            if (shortNames[i].Contains(chosen, StringComparison.OrdinalIgnoreCase) ||
                chosen.Contains(shortNames[i], StringComparison.OrdinalIgnoreCase))
                return files[i];

        return null;
    }

    private static double ProbeSeconds(string path)
    {
        try { return Mp3Duration.GetSeconds(path); }
        catch { return 180; } // assume 3 min if probing fails
    }

    /// <summary>
    /// Strips common filename noise so Claude sees clean names.
    /// "massobeats - thoughts (freetouse.com)" → "thoughts"
    /// </summary>
    internal static string StripLabel(string name)
    {
        var parenIdx = name.IndexOf('(');
        if (parenIdx > 0) name = name[..parenIdx].Trim();

        var dashIdx = name.IndexOf(" - ", StringComparison.Ordinal);
        if (dashIdx >= 0) name = name[(dashIdx + 3)..].Trim();

        return name;
    }

    private sealed class MusicChoice
    {
        public List<string>? Tracks { get; set; }
    }
}
