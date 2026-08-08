using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;
using YtPipeline.Core.Abstractions;
using YtPipeline.Core.Models;
using YtPipeline.Core.Services;
using YtPipeline.Stages.Images;
using YtPipeline.Stages.Music;
using YtPipeline.Stages.Process;
using YtPipeline.Stages.Voice;

namespace YtPipeline.Stages.Video;

public sealed class FfmpegOptions
{
    public string Executable { get; set; } = "ffmpeg";
    public int Width { get; set; } = 3840;   // 4K UHD
    public int Height { get; set; } = 2160;
    public int Fps { get; set; } = 30;
    /// <summary>x264 constant-rate-factor (lower = higher quality / bigger file).</summary>
    public int Crf { get; set; } = 20;
    public string Preset { get; set; } = "medium";

    /// <summary>
    /// Linear gain applied to the ElevenLabs voice before muxing. 1.0 = original level,
    /// 1.25 = +25% louder. Applies whether or not background music is mixed in.
    /// </summary>
    public double VoiceGain { get; set; } = 1.25;
}

/// <summary>
/// Stage 4. Holds each beat image for its share of the narration, then muxes the image
/// sequence with the audio into a 4K H.264 mp4. Image timing is anchored to the actual
/// spoken audio via ElevenLabs per-character alignment; falls back to proportional word-count
/// split when alignment is unavailable.
/// </summary>
public sealed class FfmpegVideoAssembler(
    ProcessRunner process,
    FfmpegOptions options,
    MusicOptions musicOptions,
    ILogger<FfmpegVideoAssembler> logger) : IVideoAssembler
{
    public async Task<string> AssembleAsync(
        Run run, ScriptDocument script, IReadOnlyList<string> images, string audioPath,
        IReadOnlyList<string> musicPaths, CancellationToken ct)
    {
        if (images.Count == 0) throw new InvalidOperationException("No images to assemble.");
        if (!File.Exists(audioPath)) throw new FileNotFoundException("Audio not found.", audioPath);

        var audioSeconds = Mp3Duration.GetSeconds(audioPath);
        var aligned = TryAlignedDurations(run, images, script, audioSeconds);
        var durations = aligned ?? BuildDurations(images, script, audioSeconds);
        logger.LogInformation(aligned is not null
            ? "Timing images from speech alignment (precise sync)."
            : "Timing images by estimated word-count split (no alignment available).");

        Directory.CreateDirectory(run.VideoDir);
        var listPath = Path.Combine(run.VideoDir, "concat.txt");
        WriteConcatList(listPath, images, durations);

        var vf = $"scale={options.Width}:{options.Height}:force_original_aspect_ratio=decrease," +
                 $"pad={options.Width}:{options.Height}:(ow-iw)/2:(oh-ih)/2:color=black," +
                 $"setsar=1,fps={options.Fps}";

        // Filter to tracks that actually exist on disk (defensive — selector already checks).
        var tracks = (musicPaths ?? []).Where(p => !string.IsNullOrWhiteSpace(p) && File.Exists(p)).ToList();
        bool hasMusic = tracks.Count > 0;

        if (hasMusic)
            logger.LogInformation("Background music: {Count} track(s) @ {Vol:P0} volume",
                tracks.Count, musicOptions.Volume);

        // Build FFmpeg args.
        // Input layout: 0 = concat.txt (video frames), 1 = voice.mp3, 2..N = music tracks.
        // The LAST music track gets -stream_loop -1 so it loops to cover any remaining length.
        var args = new List<string> { "-y" };
        args.AddRange(["-f", "concat", "-safe", "0", "-i", listPath]); // 0
        args.AddRange(["-i", audioPath]);                               // 1
        if (hasMusic)
        {
            for (int t = 0; t < tracks.Count; t++)
            {
                if (t == tracks.Count - 1)
                    args.AddRange(["-stream_loop", "-1"]); // loop last track to cover remainder
                args.AddRange(["-i", tracks[t]]);           // 2, 3, 4, …
            }
        }
        args.AddRange(["-vf", vf]);

        if (hasMusic)
        {
            var inv = CultureInfo.InvariantCulture;
            double fadeStart = Math.Max(0, audioSeconds - musicOptions.FadeOutSeconds);
            string vol   = musicOptions.Volume.ToString("0.##", inv);
            string fade  = fadeStart.ToString("0.###", inv);
            string dur   = musicOptions.FadeOutSeconds.ToString("0.#", inv);

            var mf = new StringBuilder();

            // Normalize every music input to a common format (48 kHz stereo) FIRST. This lets
            // tracks from a mixed-format library be concatenated cleanly (concat requires matching
            // sample rate + channel layout) and preserves the music's stereo width in the final mix.
            // Music inputs start at FFmpeg input index 2 (0 = video frames, 1 = voice).
            for (int t = 0; t < tracks.Count; t++)
                mf.Append($"[{t + 2}:a]aresample=48000,aformat=sample_fmts=fltp:channel_layouts=stereo[m{t}];");

            // Single track feeds straight in; multiple tracks are concatenated end-to-end.
            string bgmSource;
            if (tracks.Count == 1)
            {
                bgmSource = "[m0]";
            }
            else
            {
                var labels = string.Concat(Enumerable.Range(0, tracks.Count).Select(t => $"[m{t}]"));
                mf.Append($"{labels}concat=n={tracks.Count}:v=0:a=1[chain];");
                bgmSource = "[chain]";
            }

            // Lower the music and fade it out over the last N seconds.
            mf.Append($"{bgmSource}volume={vol},afade=t=out:st={fade}:d={dur}[bgm];");
            // Voice is mono from TTS — boost it to the desired level and make it stereo so amix
            // outputs a clean stereo track.
            string voiceVol = options.VoiceGain.ToString("0.###", inv);
            mf.Append($"[1:a]volume={voiceVol},aformat=channel_layouts=stereo[voc];");
            // normalize=0 keeps each stream at its own volume instead of dividing by the
            // number of inputs (the default normalize=1 would halve the voice to 50%).
            mf.Append("[voc][bgm]amix=inputs=2:duration=first:dropout_transition=2:normalize=0[aout]");

            args.AddRange(["-filter_complex", mf.ToString(), "-map", "0:v", "-map", "[aout]"]);
        }
        else
        {
            // No music — still boost the voice to the desired level.
            var voiceVol = options.VoiceGain.ToString("0.###", CultureInfo.InvariantCulture);
            args.AddRange(["-af", $"volume={voiceVol}"]);
        }

        args.AddRange(["-c:v", "libx264", "-preset", options.Preset, "-crf", options.Crf.ToString()]);
        args.AddRange(["-pix_fmt", "yuv420p"]);
        args.AddRange(["-c:a", "aac", "-b:a", "192k"]);
        args.Add("-shortest");
        args.Add(run.VideoPath);

        await process.RunOrThrowAsync(options.Executable, args, ct: ct);

        if (!File.Exists(run.VideoPath))
            throw new InvalidOperationException($"FFmpeg reported success but no file at {run.VideoPath}.");

        logger.LogInformation("Video -> {Path} ({Seconds:F1}s)", run.VideoPath, audioSeconds);
        return run.VideoPath;
    }

    /// <summary>
    /// Per-image hold times anchored to the actual spoken audio, using ElevenLabs' per-character
    /// timestamps. Each beat's start time is the moment its narration begins; the images for that
    /// beat split that exact spoken span. Returns null if alignment is missing or any image can't
    /// be mapped to a beat (caller then falls back to the word-count estimate).
    /// </summary>
    internal static double[]? TryAlignedDurations(Run run, IReadOnlyList<string> images, ScriptDocument script, double total)
    {
        if (!File.Exists(run.AlignmentPath)) return null;
        AudioAlignment align;
        try { align = Json.ReadFile<AudioAlignment>(run.AlignmentPath); }
        catch { return null; }
        if (align.CharStartTimes.Count == 0) return null;

        var beats = script.Beats.OrderBy(b => b.Index).ToList();
        if (beats.Count == 0) return null;

        // Every image must map to a real beat for clean alignment; otherwise fall back.
        var beatOf = images.Select(p => ImageSet.BeatIndexOf(p)).ToArray();
        if (beatOf.Any(b => b is null || b < 0 || b >= beats.Count)) return null;

        // Char offset where each beat's narration starts in the sent text (beats joined by " ").
        // MUST use SpokenText — the same text NarrationForVoice() sends to ElevenLabs — so these
        // offsets line up with the returned per-character alignment even when beats were respelled.
        var beatCharStart = new int[beats.Count];
        int cursor = 0;
        for (int i = 0; i < beats.Count; i++)
        {
            beatCharStart[i] = cursor;
            cursor += beats[i].SpokenText.Trim().Length + 1; // +1 for the space separator
        }

        int n = align.CharStartTimes.Count;
        double TimeAt(int c) => c <= 0 ? 0 : c >= n ? total : align.CharStartTimes[c];

        var beatStart = new double[beats.Count];
        for (int i = 0; i < beats.Count; i++) beatStart[i] = Math.Min(TimeAt(beatCharStart[i]), total);
        for (int i = 1; i < beats.Count; i++)
            if (beatStart[i] < beatStart[i - 1]) beatStart[i] = beatStart[i - 1];
        double BeatEnd(int i) => i + 1 < beats.Count ? beatStart[i + 1] : total;

        // Each image gets an equal slice of its beat's spoken span.
        var starts = new double[images.Count];
        var byBeat = new Dictionary<int, List<int>>();
        for (int idx = 0; idx < images.Count; idx++)
        {
            int b = beatOf[idx]!.Value;
            if (!byBeat.TryGetValue(b, out var list)) byBeat[b] = list = new List<int>();
            list.Add(idx);
        }
        foreach (var (b, idxs) in byBeat)
        {
            double s = beatStart[b], span = Math.Max(0, BeatEnd(b) - s);
            for (int j = 0; j < idxs.Count; j++) starts[idxs[j]] = s + span * j / idxs.Count;
        }

        // Images are natural-sorted (beat then sequence), so starts should be non-decreasing.
        for (int i = 1; i < starts.Length; i++) if (starts[i] < starts[i - 1]) starts[i] = starts[i - 1];

        var durations = new double[images.Count];
        for (int i = 0; i < images.Count; i++)
        {
            double end = i + 1 < images.Count ? starts[i + 1] : total;
            durations[i] = Math.Max(0.1, Math.Round(end - starts[i], 3));
        }
        return durations;
    }

    /// <summary>
    /// Per-image hold times for a variable image set. Each image's beat is read from its
    /// filename (<c>beat&lt;NN&gt;</c>); a beat's spoken time (proportional to its word count) is
    /// split equally across the images that illustrate it. Images with no beat label get an
    /// average-length share. Durations sum exactly to the audio length.
    /// </summary>
    internal static double[] BuildDurations(IReadOnlyList<string> images, ScriptDocument script, double totalSeconds)
    {
        int n = images.Count;
        var beatOf = images.Select(ImageSet.BeatIndexOf).ToArray();

        // Average beat length is the fallback weight for unlabeled images.
        double avgWords = script.Beats.Count > 0
            ? Math.Max(1, script.Beats.Average(b => WordCount(b.Narration)))
            : 1;

        // How many images share each labeled beat (so we can split that beat's time evenly).
        var perBeatImageCount = beatOf.Where(b => b.HasValue)
            .GroupBy(b => b!.Value)
            .ToDictionary(g => g.Key, g => g.Count());

        var weights = new double[n];
        for (int i = 0; i < n; i++)
        {
            if (beatOf[i] is int idx)
            {
                var beat = script.Beats.FirstOrDefault(b => b.Index == idx);
                double words = beat is null ? avgWords : Math.Max(1, WordCount(beat.Narration));
                weights[i] = words / perBeatImageCount[idx];
            }
            else
            {
                weights[i] = avgWords; // unlabeled image: treat as its own average-length beat
            }
        }

        double totalWeight = weights.Sum();
        var durations = new double[n];
        double assigned = 0;
        for (int i = 0; i < n; i++)
        {
            durations[i] = Math.Round(totalSeconds * weights[i] / totalWeight, 3);
            assigned += durations[i];
        }
        durations[n - 1] = Math.Max(0.1, durations[n - 1] + (totalSeconds - assigned));
        return durations;
    }

    private static int WordCount(string s) =>
        s.Split(new[] { ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries).Length;

    /// <summary>Distributes total audio seconds across beats by narration word count.</summary>
    internal static double[] SplitProportionally(ScriptDocument script, int imageCount, double totalSeconds)
    {
        var weights = new double[imageCount];
        for (int i = 0; i < imageCount; i++)
        {
            var beat = script.Beats.FirstOrDefault(b => b.Index == i);
            int words = beat is null ? 1 : Math.Max(1, beat.Narration.Split(
                new[] { ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries).Length);
            weights[i] = words;
        }

        double totalWeight = weights.Sum();
        var durations = new double[imageCount];
        double assigned = 0;
        for (int i = 0; i < imageCount; i++)
        {
            durations[i] = Math.Round(totalSeconds * weights[i] / totalWeight, 3);
            assigned += durations[i];
        }
        // Push any rounding remainder onto the last beat so the sum matches the audio exactly.
        durations[imageCount - 1] = Math.Max(0.1, durations[imageCount - 1] + (totalSeconds - assigned));
        return durations;
    }

    private static void WriteConcatList(string listPath, IReadOnlyList<string> images, double[] durations)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < images.Count; i++)
        {
            var p = images[i].Replace('\\', '/');
            sb.Append("file '").Append(p).Append("'\n");
            sb.Append("duration ").Append(durations[i].ToString("0.###", CultureInfo.InvariantCulture)).Append('\n');
        }
        // The concat demuxer ignores the final image's duration unless the file is named again.
        sb.Append("file '").Append(images[^1].Replace('\\', '/')).Append("'\n");
        File.WriteAllText(listPath, sb.ToString(), new UTF8Encoding(false));
    }
}
