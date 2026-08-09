using Microsoft.Extensions.Logging;
using YtPipeline.Core.Abstractions;
using YtPipeline.Core.Models;
using YtPipeline.Core.Services;

namespace YtPipeline.Stages.Images;

/// <summary>
/// Dispatches Stage 2 to whichever image backend is selected in Settings, and — when a fallback
/// is configured — hands the leftovers to the other backend if the first one stops short.
/// <para>
/// The provider is resolved per call (not at startup) so switching it in the UI takes effect on
/// the next stage run without restarting the app.
/// </para>
/// </summary>
public sealed class ImageGeneratorRouter(
    CodexCliImageGenerator codex,
    GflowCliImageGenerator gflow,
    SettingsStore settings,
    ILogger<ImageGeneratorRouter> logger) : IImageGenerator
{
    public async Task<IReadOnlyList<string>> GenerateAsync(
        Run run, ScriptDocument script, Channel channel, CancellationToken ct, bool regenerate = false)
    {
        var cfg = settings.Current;
        var primaryName = cfg.ImageProvider;
        var fallbackName = cfg.ImageFallbackProvider;

        logger.LogInformation("Image provider: {Provider}{Fallback}", primaryName,
            string.IsNullOrEmpty(fallbackName) ? "" : $" (fallback: {fallbackName})");

        var expected = script.Beats.Count;
        IReadOnlyList<string> images;

        try
        {
            images = await Generator(primaryName).GenerateAsync(run, script, channel, ct, regenerate);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (!string.IsNullOrEmpty(fallbackName))
        {
            // A hard failure from the primary (quota gone before it made anything, CLI not signed
            // in) is exactly when the fallback earns its keep. Without a fallback configured the
            // exception propagates as before.
            logger.LogWarning(ex, "{Primary} failed outright — handing over to {Fallback}.",
                primaryName, fallbackName);
            images = Array.Empty<string>();
        }

        if (string.IsNullOrEmpty(fallbackName) || images.Count >= expected)
            return images;

        // Both generators skip beats that already have a frame, so this fills only the gaps.
        // regenerate is deliberately NOT forwarded: it clears the images folder, which would
        // destroy everything the primary just produced.
        logger.LogWarning(
            "{Primary} produced {Got}/{Expected} frames — switching to {Fallback} for the rest.",
            primaryName, images.Count, expected, fallbackName);

        try
        {
            var filled = await Generator(fallbackName)
                .GenerateAsync(run, script, channel, ct, regenerate: false);

            logger.LogInformation(
                "Fallback {Fallback} brought the set to {Got}/{Expected} frames.",
                fallbackName, filled.Count, expected);
            return filled;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            // Keep whatever the primary managed: the stage is resumable, so a partial set is
            // strictly better than throwing away paid frames.
            logger.LogError(ex, "Fallback {Fallback} also failed — keeping {Got} frame(s).",
                fallbackName, images.Count);
            if (images.Count == 0) throw;
            return images;
        }
    }

    /// <summary>Thumbnails are a single image, so there's nothing to split — primary only.</summary>
    public Task<string> GenerateThumbnailAsync(
        Run run, ScriptDocument script, Channel channel, CancellationToken ct) =>
        Generator(settings.Current.ImageProvider).GenerateThumbnailAsync(run, script, channel, ct);

    private IImageGenerator Generator(string provider) =>
        provider == ImageProviders.Gflow ? gflow : codex;
}
