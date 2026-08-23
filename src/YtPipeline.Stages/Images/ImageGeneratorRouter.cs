using Microsoft.Extensions.Logging;
using YtPipeline.Core.Abstractions;
using YtPipeline.Core.Models;
using YtPipeline.Core.Services;

namespace YtPipeline.Stages.Images;

/// <summary>
/// Dispatches Stage 2 to an image backend, treating <see cref="AppSettings.ImageProvider"/> as the
/// PREFERRED provider rather than a fixed one.
/// <para>
/// When the preferred provider reports its quota exhausted it is benched, and subsequent runs go
/// straight to the standby — no attempts wasted rediscovering the same wall. Once the cooldown
/// expires the preferred provider is tried again automatically, so the real reset is discovered
/// rather than predicted, and it takes over again the moment it works.
/// </para>
/// </summary>
public sealed class ImageGeneratorRouter(
    CodexCliImageGenerator codex,
    GflowCliImageGenerator gflow,
    SettingsStore settings,
    ProviderHealthStore health,
    ILogger<ImageGeneratorRouter> logger) : IImageGenerator
{
    public async Task<IReadOnlyList<string>> GenerateAsync(
        Run run, ScriptDocument script, Channel channel, CancellationToken ct, bool regenerate = false)
    {
        var cfg = settings.Current;
        var preferred = cfg.ImageProvider;
        var standby = cfg.ImageFallbackProvider;
        var expected = script.Beats.Count;

        // ── Is the preferred provider benched? ───────────────────────────────
        var benchedUntil = health.BenchedUntil(preferred, cfg.QuotaCooldownMinutes);
        var order = new List<string>();

        if (benchedUntil is { } until && !string.IsNullOrEmpty(standby))
        {
            logger.LogWarning(
                "{Preferred} is benched (quota) for another {Mins:F0} min — using {Standby} instead. " +
                "It will be retried automatically after that.",
                preferred, (until - DateTime.UtcNow).TotalMinutes, standby);
            order.Add(standby);
            // Still worth a shot at the end: the standby may leave gaps, and by then the
            // cooldown may have lapsed anyway.
            order.Add(preferred);
        }
        else
        {
            if (benchedUntil is not null)
                logger.LogWarning(
                    "{Preferred} is benched (quota) but no standby provider is configured — trying it anyway.",
                    preferred);
            order.Add(preferred);
            if (!string.IsNullOrEmpty(standby)) order.Add(standby);
        }

        logger.LogInformation("Image provider order: {Order}", string.Join(" → ", order));

        IReadOnlyList<string> images = ImageSet.List(run.ImagesDir);
        Exception? last = null;

        for (int i = 0; i < order.Count; i++)
        {
            var name = order[i];
            if (images.Count >= expected) break;

            // regenerate clears the folder, so it may only ever apply to the FIRST attempt —
            // forwarding it later would delete what an earlier provider just produced.
            var clearFirst = regenerate && i == 0;

            try
            {
                images = await Generator(name).GenerateAsync(run, script, channel, ct, clearFirst);
                if (images.Count >= expected)
                {
                    logger.LogInformation("{Name} completed the set: {Count}/{Expected}.",
                        name, images.Count, expected);
                    break;
                }

                logger.LogWarning("{Name} produced {Count}/{Expected} frames.", name, images.Count, expected);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                last = ex;
                logger.LogWarning("{Name} failed: {Message}", name, ex.Message);
                images = ImageSet.List(run.ImagesDir);   // keep whatever it managed first
            }
        }

        if (images.Count == 0 && last is not null) throw last;

        if (images.Count < expected)
            logger.LogWarning(
                "Images stage finished short: {Count}/{Expected}. Re-run to fill the rest — " +
                "existing frames are kept.", images.Count, expected);

        return images;
    }

    /// <summary>
    /// Thumbnails are a single image, so there's nothing to split — preferred, then standby.
    /// <para>
    /// The preferred backend here is <see cref="AppSettings.ThumbnailProvider"/> when it is set,
    /// so the thumbnail can come from a different backend than the frames without changing what
    /// bulk frame work uses.
    /// </para>
    /// </summary>
    public async Task<string> GenerateThumbnailAsync(
        Run run, ScriptDocument script, Channel channel, CancellationToken ct)
    {
        var cfg = settings.Current;
        var wanted = string.IsNullOrEmpty(cfg.ThumbnailProvider) ? cfg.ImageProvider : cfg.ThumbnailProvider;
        if (wanted != cfg.ImageProvider)
            logger.LogInformation("Thumbnail provider is {Wanted} (frames use {Frames}).", wanted, cfg.ImageProvider);

        // Standby is whatever the wanted backend is NOT, so an explicit thumbnail choice still has
        // somewhere to fall back to even when it matches the configured global fallback.
        var standby = cfg.ImageFallbackProvider is { Length: > 0 } fb && fb != wanted
            ? fb
            : wanted == ImageProviders.Gflow ? ImageProviders.Codex : ImageProviders.Gflow;

        var benched = health.BenchedUntil(wanted, cfg.QuotaCooldownMinutes) is not null;
        var first = benched ? standby : wanted;

        try { return await Generator(first).GenerateThumbnailAsync(run, script, channel, ct); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (first != standby)
        {
            logger.LogWarning(ex, "Thumbnail via {First} failed — trying {Standby}.", first, standby);
            return await Generator(standby).GenerateThumbnailAsync(run, script, channel, ct);
        }
    }

    /// <summary>Channel art is a single image too — same preferred-then-standby handling.</summary>
    public async Task<string> GenerateBrandImageAsync(
        Channel channel, string kind, string targetPath, CancellationToken ct)
    {
        var cfg = settings.Current;
        var benched = health.BenchedUntil(cfg.ImageProvider, cfg.QuotaCooldownMinutes) is not null;
        var first = benched && !string.IsNullOrEmpty(cfg.ImageFallbackProvider)
            ? cfg.ImageFallbackProvider
            : cfg.ImageProvider;

        try { return await Generator(first).GenerateBrandImageAsync(channel, kind, targetPath, ct); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (!string.IsNullOrEmpty(cfg.ImageFallbackProvider)
                                   && first != cfg.ImageFallbackProvider)
        {
            logger.LogWarning(ex, "{Kind} via {First} failed — trying {Standby}.",
                kind, first, cfg.ImageFallbackProvider);
            return await Generator(cfg.ImageFallbackProvider)
                .GenerateBrandImageAsync(channel, kind, targetPath, ct);
        }
    }

    private IImageGenerator Generator(string provider) =>
        provider == ImageProviders.Gflow ? gflow : codex;
}
