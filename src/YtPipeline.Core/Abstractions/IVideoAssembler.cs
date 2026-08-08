using YtPipeline.Core.Models;

namespace YtPipeline.Core.Abstractions;

/// <summary>Stage 4: mux images + audio into a finished mp4. Returns the video file path.</summary>
public interface IVideoAssembler
{
    /// <param name="musicPaths">
    /// Ordered list of background music file paths to play sequentially.  Empty = no music.
    /// Tracks are played in order; the last track is looped by FFmpeg to cover any remaining
    /// video length.  Built by <c>MusicSelector</c> so the playlist always covers the video.
    /// </param>
    Task<string> AssembleAsync(Run run, ScriptDocument script,
                               IReadOnlyList<string> images, string audioPath,
                               IReadOnlyList<string> musicPaths, CancellationToken ct);
}
