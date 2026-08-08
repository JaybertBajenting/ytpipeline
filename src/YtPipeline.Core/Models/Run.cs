namespace YtPipeline.Core.Models;

/// <summary>
/// A single video production run, keyed by <c>run_id = {channel}_{yyyyMMdd_HHmmss}</c>.
/// Knows where every artifact for the run lives, relative to the workspace root.
/// </summary>
public sealed class Run
{
    public required string RunId { get; init; }
    public required string Channel { get; init; }
    public required string Topic { get; init; }
    public DateTime CreatedUtc { get; init; } = DateTime.UtcNow;

    /// <summary>Absolute path to the workspace root (the folder holding videos/).</summary>
    public required string WorkspaceRoot { get; init; }

    /// <summary>The timestamp portion of the run id (the part after "{channel}_").</summary>
    public string Stamp =>
        RunId.StartsWith(Channel + "_", StringComparison.Ordinal) ? RunId[(Channel.Length + 1)..] : RunId;

    /// <summary>Everything for one video lives under videos/&lt;channel&gt;/&lt;timestamp&gt;/.</summary>
    public string OutputDir => Path.Combine(WorkspaceRoot, "videos", Channel, Stamp);
    public string ManifestPath => Path.Combine(OutputDir, "manifest.json");
    public string ScriptPath => Path.Combine(OutputDir, "script.json");

    public string ImagesDir => Path.Combine(OutputDir, "images");
    public string AudioDir => Path.Combine(OutputDir, "audio");
    public string AudioPath => Path.Combine(AudioDir, "voice.mp3");
    public string AlignmentPath => Path.Combine(AudioDir, "align.json");
    public string VideoDir => Path.Combine(OutputDir, "video");
    public string VideoPath => Path.Combine(VideoDir, "final.mp4");
    public string ThumbnailPath => Path.Combine(OutputDir, "thumbnail.png");

    public static string NewRunId(string channel, DateTime nowUtc) =>
        $"{channel}_{nowUtc:yyyyMMdd_HHmmss}";
}
