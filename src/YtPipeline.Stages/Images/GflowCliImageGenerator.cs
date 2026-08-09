using System.Text;
using Microsoft.Extensions.Logging;
using YtPipeline.Core.Abstractions;
using YtPipeline.Core.Models;
using YtPipeline.Core.Services;
using YtPipeline.Stages.Process;

namespace YtPipeline.Stages.Images;

/// <summary>
/// Alternative Stage 2 backend that generates frames with <c>gflow-cli</c>, driving Google
/// Flow and spending Google AI Pro / Flow credits.
///
/// Unlike <see cref="CodexCliImageGenerator"/> — an agent handed the whole script that writes
/// every file itself — gflow is a single-image tool, so we invoke it once per beat and place
/// the result ourselves. That makes the stage naturally resumable: each beat is independent,
/// and an interrupted run simply picks up at the first beat with no PNG on disk.
///
/// IMPORTANT: gflow-cli drives a <b>headed</b> Chrome window (Google's auth and reCAPTCHA
/// gates reject headless), so generation is sequential and not fully unattended.
/// </summary>
public sealed class GflowCliImageGenerator(
    ProcessRunner process,
    SettingsStore settings,
    ProviderHealthStore health,
    ILogger<GflowCliImageGenerator> logger) : IImageGenerator
{
    /// <summary>Retries for a single beat before the run gives up.</summary>
    private const int MaxAttemptsPerBeat = 2;

    /// <summary>
    /// Stop dispatching after this many failures in a row. An exhausted daily quota fails every
    /// remaining beat, and grinding through all of them — two attempts and a timeout each — wastes
    /// a long time before a configured fallback provider ever gets a turn. A single transient
    /// hiccup won't trip this, because any success resets the counter.
    /// </summary>
    private const int AbandonAfterConsecutiveFailures = 4;

    public async Task<IReadOnlyList<string>> GenerateAsync(
        Run run, ScriptDocument script, Channel channel, CancellationToken ct, bool regenerate = false)
    {
        Directory.CreateDirectory(run.ImagesDir);
        if (regenerate) ClearPngs(run.ImagesDir);

        var cfg = settings.Current.Gflow;
        var have = ExistingBeatIndices(run.ImagesDir);
        var beats = script.Beats.OrderBy(b => b.Index).ToList();
        var todo = beats.Where(b => !have.Contains(b.Index)).ToList();

        if (todo.Count == 0 && ImageSet.List(run.ImagesDir).Count > 0)
        {
            var existing = ImageSet.List(run.ImagesDir);
            logger.LogInformation("Stage 2 (gflow): all {Count} beats already have images.", existing.Count);
            return existing;
        }

        var lanes = Math.Max(1, cfg.MaxConcurrent);
        logger.LogInformation(
            "Stage 2 (gflow): {Todo} image(s) to generate{Resume}, {Lanes} at a time. " +
            "Keep the signed-in browser open while it works.",
            todo.Count, have.Count > 0 ? $" ({have.Count} already done)" : "", lanes);

        // Wait once up front rather than per-beat — with several in flight, a per-beat check
        // would just serialise them again.
        await NetworkGuard.WaitForConnectivityAsync(logger, ct);

        int failed = 0, done = 0, consecutiveFailures = 0, abandoned = 0;
        using var gate = new SemaphoreSlim(lanes);

        var tasks = todo.Select(async beat =>
        {
            await gate.WaitAsync(ct);
            try
            {
                ct.ThrowIfCancellationRequested();

                // Already given up — drain the remaining lanes without calling gflow again.
                if (Volatile.Read(ref abandoned) != 0) return;

                var target = Path.Combine(run.ImagesDir, $"beat{beat.Index:D2}_gflow.png");
                var ok = await GenerateOneAsync(beat, channel, target, cfg, ct);

                // Progress is reported on completion, so the count reflects real progress
                // rather than dispatch order.
                var n = Interlocked.Increment(ref done);
                if (ok)
                {
                    Volatile.Write(ref consecutiveFailures, 0);
                }
                else
                {
                    Interlocked.Increment(ref failed);
                    if (Interlocked.Increment(ref consecutiveFailures) >= AbandonAfterConsecutiveFailures
                        && Interlocked.Exchange(ref abandoned, 1) == 0)
                    {
                        // Don't guess at the cause here — the per-beat warnings above carry
                        // gflow's own stderr, which distinguishes an exhausted quota from a
                        // browser/session problem. Asserting "probably quota" sent one
                        // investigation down the wrong path already.
                        logger.LogWarning(
                            "{Count} beats failed in a row — abandoning gflow for this run. " +
                            "See the gflow errors above for the reason. {Left} beat(s) left " +
                            "ungenerated; a fallback provider (or a later re-run) will fill them.",
                            AbandonAfterConsecutiveFailures, todo.Count - n);
                    }
                }

                logger.LogInformation("[gflow {Done}/{Total}] beat {Beat} {Status}",
                    n, todo.Count, beat.Index, ok ? "ok" : "FAILED");
            }
            finally { gate.Release(); }
        });

        await Task.WhenAll(tasks);

        var images = ImageSet.List(run.ImagesDir);
        if (images.Count == 0)
            throw new InvalidOperationException(
                $"gflow produced no PNGs in {run.ImagesDir}. Check that gflow-cli is installed and " +
                "you have logged in with `gflow auth login --browser chrome`.");

        if (failed > 0)
            logger.LogWarning(
                "{Failed} beat(s) did not produce an image — re-run the images stage to fill the gaps.", failed);

        logger.LogInformation("Stage 2 (gflow) produced {Count} images.", images.Count);
        return images;
    }

    public async Task<string> GenerateThumbnailAsync(
        Run run, ScriptDocument script, Channel channel, CancellationToken ct)
    {
        Directory.CreateDirectory(run.OutputDir);
        var cfg = settings.Current.Gflow;

        var prompt = new StringBuilder()
            .AppendLine(StyleRule(channel).TrimEnd())
            .AppendLine()
            .AppendLine($"YouTube thumbnail for a video titled \"{script.Title}\".")
            .AppendLine("Single clear focal subject, bold short text, strong contrast, minimal clutter, readable at small sizes.")
            .Append("Horizontal 16:9.")
            .ToString();

        if (File.Exists(run.ThumbnailPath)) { try { File.Delete(run.ThumbnailPath); } catch { } }

        var ok = await RunGflowAsync(prompt, run.ThumbnailPath, cfg, ct);
        if (!ok || !File.Exists(run.ThumbnailPath))
            throw new InvalidOperationException($"gflow finished but no thumbnail at {run.ThumbnailPath}.");

        logger.LogInformation("Thumbnail -> {Path}", run.ThumbnailPath);
        return run.ThumbnailPath;
    }

    // -------------------------------------------------------------------------

    private async Task<bool> GenerateOneAsync(
        ScriptBeat beat, Channel channel, string target, GflowSettings cfg, CancellationToken ct)
    {
        var prompt = BuildBeatPrompt(beat, channel);

        for (int attempt = 1; attempt <= MaxAttemptsPerBeat; attempt++)
        {
            if (await RunGflowAsync(prompt, target, cfg, ct) && File.Exists(target))
                return true;

            if (attempt < MaxAttemptsPerBeat)
            {
                logger.LogWarning("Beat {Beat} produced no file (attempt {Attempt}/{Max}) — retrying.",
                    beat.Index, attempt, MaxAttemptsPerBeat);
                await Task.Delay(TimeSpan.FromSeconds(3), ct);
            }
        }

        logger.LogWarning("Beat {Beat} failed after {Max} attempts — continuing.", beat.Index, MaxAttemptsPerBeat);
        return false;
    }

    /// <summary>Invokes <c>gflow image t2i "&lt;prompt&gt;" -o &lt;target&gt;</c>. Returns false on failure.</summary>
    private async Task<bool> RunGflowAsync(string prompt, string target, GflowSettings cfg, CancellationToken ct)
    {
        var args = new List<string>(cfg.ArgsTemplate)
        {
            prompt,
            string.IsNullOrWhiteSpace(cfg.OutputFlag) ? "-o" : cfg.OutputFlag,
            target,
        };

        // gflow defaults to --aspect 9:16 (vertical) and --model nano2. Both must be set
        // explicitly: vertical frames would be sideways in a 16:9 video.
        if (!string.IsNullOrWhiteSpace(cfg.Aspect)) args.AddRange(["--aspect", cfg.Aspect]);
        if (!string.IsNullOrWhiteSpace(cfg.Model)) args.AddRange(["--model", cfg.Model]);
        if (!string.IsNullOrWhiteSpace(cfg.Project)) args.AddRange(["--project", cfg.Project]);
        args.AddRange(cfg.ExtraArgs);

        // Cap a single generation so one wedged browser can't stall the whole run.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(cfg.TimeoutSeconds));

        try
        {
            var result = await process.RunAsync(cfg.Executable, args, ct: timeout.Token);
            if (result.Success)
            {
                // Any success clears a previous benching, so the provider comes straight back
                // into rotation the moment its quota resets.
                health.MarkHealthy(ImageProviders.Gflow);
                return true;
            }

            var err = Trim(result.StdErr);
            logger.LogWarning("gflow exited {Code}: {Err}", result.ExitCode, err);

            // Bench it ONLY for quota errors. A CAPTCHA or sign-in failure is fixable, and
            // silently routing around it would hide a problem worth telling the user about.
            if (ProviderHealthStore.LooksLikeQuotaExhaustion(err)
                || ProviderHealthStore.LooksLikeQuotaExhaustion(result.StdOut))
            {
                // Logs only on the transition — every concurrent lane reports this, and one line
                // per failed beat buried the rest of the log.
                if (health.MarkExhausted(
                        ImageProviders.Gflow, err, settings.Current.QuotaCooldownMinutes))
                {
                    logger.LogWarning(
                        "gflow reports its daily quota is gone — benching it. The standby provider " +
                        "takes over, and gflow is retried automatically once the cooldown lapses.");
                }
            }
            return false;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // user pressed Stop
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("gflow timed out after {Seconds}s.", cfg.TimeoutSeconds);
            return false;
        }
        catch (Exception ex)
        {
            logger.LogWarning("gflow failed: {Msg}", ex.Message);
            return false;
        }
    }

    /// <summary>
    /// Builds the per-beat prompt. Unlike the Codex backend — one agent run that sees the whole
    /// script — every gflow call is stateless, so each prompt has to carry its own context:
    /// the spoken line (for tone), the visual direction, and an explicit restatement of the
    /// shared visual language, which is what keeps characters consistent across frames.
    /// </summary>
    private static string BuildBeatPrompt(ScriptBeat beat, Channel channel)
    {
        var narration = beat.Narration?.Trim() ?? "";
        var visual = string.IsNullOrWhiteSpace(beat.ImagePrompt) ? narration : beat.ImagePrompt.Trim();

        var sb = new StringBuilder()
            .AppendLine(StyleRule(channel).TrimEnd())
            .AppendLine()
            .AppendLine("=== THIS FRAME ===");

        if (narration.Length > 0)
            sb.AppendLine($"The narrator is saying: {narration}");

        sb.AppendLine($"Draw exactly one image illustrating that line: {visual}")
          .AppendLine()
          .AppendLine("Keep the recurring visual language consistent with the rest of the set: the same")
          .AppendLine("simple stick figures with round heads and dot eyes, the same thick uneven black")
          .Append("outlines, the same flat colour palette. One single clear idea, nothing extra.");

        return sb.ToString();
    }

    private static string StyleRule(Channel channel) =>
        !string.IsNullOrWhiteSpace(channel.ImageInstructions)
            ? channel.ImageInstructions
            : !string.IsNullOrWhiteSpace(channel.VisualStyle)
                ? $"Draw in this style: {channel.VisualStyle}."
                : "Simple, bold, high-contrast illustration.";

    private static HashSet<int> ExistingBeatIndices(string imagesDir) =>
        ImageSet.List(imagesDir)
            .Select(ImageSet.BeatIndexOf)
            .Where(i => i is not null)
            .Select(i => i!.Value)
            .ToHashSet();

    private static void ClearPngs(string dir)
    {
        if (!Directory.Exists(dir)) return;
        foreach (var f in Directory.EnumerateFiles(dir, "*.png"))
            try { File.Delete(f); } catch { /* ignore */ }
    }

    private static string Trim(string s) =>
        s.Length <= 400 ? s.Trim() : s[..400].Trim() + "…";
}
