using Microsoft.Extensions.Logging;
using YtPipeline.Core.Abstractions;
using YtPipeline.Core.Models;
using YtPipeline.Core.Services;

namespace YtPipeline.Stages.Images;

/// <summary>
/// Dispatches Stage 2 to whichever image backend is selected in Settings. The provider is
/// resolved per call (not at startup) so switching it in the UI takes effect on the next
/// stage run without restarting the app.
/// </summary>
public sealed class ImageGeneratorRouter(
    CodexCliImageGenerator codex,
    GflowCliImageGenerator gflow,
    SettingsStore settings,
    ILogger<ImageGeneratorRouter> logger) : IImageGenerator
{
    public Task<IReadOnlyList<string>> GenerateAsync(
        Run run, ScriptDocument script, Channel channel, CancellationToken ct, bool regenerate = false) =>
        Resolve().GenerateAsync(run, script, channel, ct, regenerate);

    public Task<string> GenerateThumbnailAsync(
        Run run, ScriptDocument script, Channel channel, CancellationToken ct) =>
        Resolve().GenerateThumbnailAsync(run, script, channel, ct);

    private IImageGenerator Resolve()
    {
        var provider = settings.Current.ImageProvider;
        logger.LogInformation("Image provider: {Provider}", provider);
        return provider == ImageProviders.Gflow ? gflow : codex;
    }
}
