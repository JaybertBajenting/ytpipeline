using YtPipeline.Core.Models;

namespace YtPipeline.Core.Abstractions;

/// <summary>Stage 2: one image per script beat. Returns the generated image paths in beat order.</summary>
public interface IImageGenerator
{
    /// <param name="regenerate">
    /// When true, existing frames are cleared and everything is re-generated from scratch.
    /// When false (default), already-generated frames are kept and only the missing beats are
    /// produced — this is what makes the stage resumable after an interruption.
    /// </param>
    Task<IReadOnlyList<string>> GenerateAsync(
        Run run, ScriptDocument script, Channel channel, CancellationToken ct, bool regenerate = false);

    /// <summary>Generates a single YouTube thumbnail for the run. Returns its path.</summary>
    Task<string> GenerateThumbnailAsync(Run run, ScriptDocument script, Channel channel, CancellationToken ct);

    /// <summary>
    /// Generates the channel's own avatar or banner art into <paramref name="targetPath"/>.
    /// Belongs to the channel rather than any run — see <see cref="BrandImage"/> for the kinds.
    /// </summary>
    Task<string> GenerateBrandImageAsync(Channel channel, string kind, string targetPath, CancellationToken ct);
}
