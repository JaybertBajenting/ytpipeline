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
        Run run, ScriptDocument script, Channel channel, CancellationToken ct,
        bool regenerate = false, string? forceProvider = null)
    {
        var cfg = settings.Current;
        var expected = script.Beats.Count;

        // ONE provider draws the whole set. Frames used to fail over to the standby automatically,
        // which finished the job but produced a video whose art style changed partway through —
        // the backends do not draw alike. Now a spent quota pauses instead, and switching backend
        // is something a person asks for.
        var provider = string.IsNullOrWhiteSpace(forceProvider)
            ? ChannelOr(channel, cfg.ImageProvider)
            : forceProvider.Trim();
        if (!string.IsNullOrWhiteSpace(forceProvider))
            logger.LogInformation("Image provider forced to {Provider} for this run.", provider);
        else if (!string.IsNullOrEmpty(channel.ImageProvider))
            logger.LogInformation("{Channel} is drawn by {Provider} (set on the channel).", channel.Name, provider);

        // The bench is a COOLDOWN, and a cooldown only predicts when the account that ran dry will
        // reset. Signing Flow into a different account is a normal way to get more quota and makes
        // that prediction meaningless — and gflow exposes nothing that reports the per-model daily
        // image limit (`flow credits` reports a balance that does not move when images are made,
        // so it cannot answer this). Since nothing can be asked, the honest rule is: a cooldown
        // steers AUTOMATIC choices, and a person naming the provider outranks it. One attempt is
        // self-verifying — it either draws a frame that was needed anyway, or fails immediately
        // with the quota error and re-benches.
        var chosenByHand = !string.IsNullOrWhiteSpace(forceProvider);
        if (health.BenchedUntil(provider, cfg.QuotaCooldownMinutes) is { } benched)
        {
            if (!chosenByHand)
                throw new ImageQuotaPausedException(
                    provider, benched, ImageSet.List(run.ImagesDir).Count, expected);

            logger.LogInformation(
                "{Provider} is benched until {Until:HH:mm} but was asked for explicitly — trying it. " +
                "(Switching accounts resets the real limit, which the cooldown cannot know about.)",
                provider, benched.ToLocalTime());
            health.Reset(provider);
        }

        logger.LogInformation("Image provider: {Provider}", provider);

        // Passes, not attempts. A pass ends when the provider stops making progress — usually
        // because its quota died. Before giving up, ask whether the account signed in RIGHT NOW
        // has credits: if someone switched Flow to a fresh account while this was running, the
        // set should carry on rather than making them press the button again.
        IReadOnlyList<string> images = ImageSet.List(run.ImagesDir);
        for (var pass = 1; pass <= MaxPasses; pass++)
        {
            var before = images.Count;

            try
            {
                // regenerate clears the folder, so it can only ever apply to the first pass —
                // forwarding it later would delete what the previous pass just produced.
                images = await Generator(provider)
                    .GenerateAsync(run, script, channel, ct, regenerate && pass == 1);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                // Whatever it managed before failing is still on disk and still worth keeping.
                images = ImageSet.List(run.ImagesDir);
                if (health.BenchedUntil(provider, cfg.QuotaCooldownMinutes) is null)
                {
                    logger.LogWarning("{Provider} failed: {Message}", provider, ex.Message);

                    // Throw when this pass drew NOTHING, not merely when the folder is empty.
                    // Comparing against zero meant any frames already on disk — even four left
                    // from a previous day — swallowed the error and let the job report success,
                    // so a run that generated nothing at all and knew exactly why looked like it
                    // had worked. Frames made before the failure are still kept: that case breaks
                    // out below with the partial set intact.
                    if (images.Count == before) throw;
                    break;
                }
            }

            if (images.Count >= expected)
            {
                logger.LogInformation("{Provider} completed the set: {Count}/{Expected}.",
                    provider, images.Count, expected);
                return images;
            }

            // Short of the full set. A spent quota is a pause, not a failure — unless the balance
            // says otherwise, in which case go round again and finish the job.
            if (health.BenchedUntil(provider, cfg.QuotaCooldownMinutes) is { } stoppedAt)
            {
                // Go round again ONLY if this pass actually drew something. Progress means the
                // limit lifted mid-run — someone switched accounts — so the set can finish. A pass
                // that produced nothing hit a wall that is still there, and retrying would just
                // burn attempts against it.
                // Not when the provider named its reset time (Codex does): then it has said exactly
                // when it will draw again, and an account switch can't be what happened.
                if (pass < MaxPasses && images.Count > before && health.Snapshot(provider).ResetsUtc is null)
                {
                    logger.LogInformation(
                        "{Provider} ran dry after {Made} new frame(s), but the run made progress — " +
                        "trying again for the remaining {Left}. ({Count}/{Expected} done.)",
                        provider, images.Count - before, expected - images.Count, images.Count, expected);
                    health.Reset(provider);
                    continue;
                }

                throw new ImageQuotaPausedException(provider, stoppedAt, images.Count, expected);
            }

            // Stopped short without a quota bench, and another pass produced nothing new — going
            // round again would just repeat the same failure.
            if (images.Count == before) break;
        }

        logger.LogWarning(
            "Images stage finished short: {Count}/{Expected}. Re-run to fill the rest — " +
            "existing frames are kept.", images.Count, expected);

        return images;
    }

    /// <summary>
    /// How many times a run will resume after the provider stops short. Bounded so a backend that
    /// reports credits but refuses to draw can't spin.
    /// </summary>
    private const int MaxPasses = 4;

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
        var pinned = !string.IsNullOrEmpty(channel.ImageProvider);
        var wanted = pinned ? channel.ImageProvider
            : string.IsNullOrEmpty(cfg.ThumbnailProvider) ? cfg.ImageProvider : cfg.ThumbnailProvider;
        if (wanted != cfg.ImageProvider)
            logger.LogInformation("Thumbnail provider is {Wanted} (Settings frames use {Frames}).", wanted, cfg.ImageProvider);

        // Standby is whatever the wanted backend is NOT, so an explicit thumbnail choice still has
        // somewhere to fall back to even when it matches the configured global fallback. A channel
        // that names its backend has none: the other backend would change the channel's look.
        var standby = pinned ? wanted
            : cfg.ImageFallbackProvider is { Length: > 0 } fb && fb != wanted
                ? fb
                : wanted == ImageProviders.Gflow ? ImageProviders.Codex : ImageProviders.Gflow;

        var benchedUntil = health.BenchedUntil(wanted, cfg.QuotaCooldownMinutes);
        // Nowhere else to go for a channel tied to one backend, so say it's paused, like frames do.
        if (pinned && benchedUntil is { } pausedUntil)
            throw new ImageQuotaPausedException(wanted, pausedUntil, 0, 0);
        var first = benchedUntil is null ? wanted : standby;

        return await WithStandby("Thumbnail", wanted, benchedUntil, first, standby,
            g => g.GenerateThumbnailAsync(run, script, channel, ct));
    }

    /// <summary>
    /// Tries <paramref name="first"/>, then the standby. When both fail the error names both reasons:
    /// on 2026-09-30 a spent Codex limit was hidden behind gflow's "Google Flow is not ready",
    /// which sent the search to the wrong place.
    /// </summary>
    private async Task<string> WithStandby(string what, string wanted, DateTime? wantedBenchedUntil,
        string first, string standby, Func<IImageGenerator, Task<string>> make)
    {
        try { return await make(Generator(first)); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (first != standby)
        {
            logger.LogWarning("{What} via {First} failed: {Reason} Trying {Standby}.", what, first, ex.Message, standby);
            try { return await make(Generator(standby)); }
            catch (Exception ex2) when (ex2 is not OperationCanceledException)
            {
                throw new InvalidOperationException(
                    $"No {what.ToLowerInvariant()} made. {first}: {ex.Message} {standby}: {ex2.Message}", ex2);
            }
        }
        catch (Exception ex) when (wantedBenchedUntil is { } until)
        {
            // The standby went first because the wanted provider is paused. Say that too.
            throw new InvalidOperationException(
                $"No {what.ToLowerInvariant()} made. {wanted} is paused until {until.ToLocalTime():ddd h:mm tt} " +
                $"({health.Snapshot(wanted).LastError}). {standby}: {ex.Message}", ex);
        }
    }

    /// <summary>Channel art is a single image too — same preferred-then-standby handling.</summary>
    public async Task<string> GenerateBrandImageAsync(
        Channel channel, string kind, string targetPath, CancellationToken ct)
    {
        var cfg = settings.Current;
        var preferred = ChannelOr(channel, cfg.ImageProvider);
        var benchedUntil = health.BenchedUntil(preferred, cfg.QuotaCooldownMinutes);
        // A channel that names its backend doesn't fall back; see Channel.ImageProvider.
        var hasStandby = string.IsNullOrEmpty(channel.ImageProvider)
                         && !string.IsNullOrEmpty(cfg.ImageFallbackProvider)
                         && cfg.ImageFallbackProvider != preferred;
        var first = benchedUntil is not null && hasStandby ? cfg.ImageFallbackProvider : preferred;
        // No standby: "standby" = first, so a failure is simply rethrown.
        var standby = hasStandby ? cfg.ImageFallbackProvider : first;

        return await WithStandby(kind, preferred, first == preferred ? null : benchedUntil,
            first, standby, g => g.GenerateBrandImageAsync(channel, kind, targetPath, ct));
    }

    /// <summary>The channel's own backend if it names one, else the Settings choice.</summary>
    internal static string ChannelOr(Channel channel, string settingsProvider) =>
        string.IsNullOrEmpty(channel.ImageProvider) ? settingsProvider : channel.ImageProvider;

    private IImageGenerator Generator(string provider) =>
        provider == ImageProviders.Gflow ? gflow : codex;
}
