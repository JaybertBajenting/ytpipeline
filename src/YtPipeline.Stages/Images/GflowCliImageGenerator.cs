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

    /// <summary>
    /// Set once per run when a browser-side failure has already been explained, so the remedy is
    /// logged one time rather than once per failed beat across every concurrent lane.
    /// </summary>
    private int _browserWarned;

    /// <summary>
    /// Chrome and the Flow extension reporting that they cannot reach the page — as opposed to a
    /// spent quota, which is what <see cref="ProviderHealthStore.LooksLikeQuotaExhaustion"/>
    /// matches. These all mean the same thing in practice: the Flow tab is missing, signed out, or
    /// on a host the extension has no permission for.
    /// </summary>
    internal static bool LooksLikeBrowserProblem(string? text) =>
        !string.IsNullOrWhiteSpace(text)
        && new[]
        {
            "CAPTCHA_FAILED",
            "NO_FLOW_TAB",
            "must request permission to access this host",
            "Cannot access contents of url",
        }.Any(m => text.Contains(m, StringComparison.OrdinalIgnoreCase));

    public async Task<IReadOnlyList<string>> GenerateAsync(
        Run run, ScriptDocument script, Channel channel, CancellationToken ct,
        bool regenerate = false, string? forceProvider = null)
    {
        var references = ReferenceStudySources.Images(channel, run.WorkspaceRoot);
        var cfg = settings.Current.Gflow;
        if (references.Count > 0) references = await PrepareReferencesAsync(cfg, references, ct);
        Directory.CreateDirectory(run.ImagesDir);
        if (regenerate) ClearPngs(run.ImagesDir);
        Volatile.Write(ref _browserWarned, 0);   // explain a browser problem once per run

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

        // Ask gflow whether it can generate at all before dispatching hundreds of beats at it.
        // Without this, a disconnected browser extension is discovered one Chrome permission
        // error at a time: a 260-beat run burned 82 identical failures before giving up, and the
        // actual cause — the Flow tab being on the wrong URL — was only visible in the log.
        await RequireGflowReadyAsync(cfg, ct);

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
                var ok = await GenerateOneAsync(beat, channel, target, cfg, ct, references);

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
        var references = ReferenceStudySources.Images(channel, run.WorkspaceRoot, thumbnail: true);
        var cfg = settings.Current.Gflow;
        if (references.Count > 0) references = await PrepareReferencesAsync(cfg, references, ct);
        Directory.CreateDirectory(run.OutputDir);

        var prompt = new StringBuilder()
            .AppendLine(ThumbnailRule(channel).TrimEnd())
            .AppendLine(ReferenceInstruction(channel))
            .AppendLine()
            .AppendLine("=== FOR THIS TITLE ===")
            .AppendLine($"YouTube thumbnail for a video titled \"{script.Title}\".")
            // The RULE decides composition and lettering, not this builder — see the note in
            // CodexCliImageGenerator.BuildThumbnailPrompt. Hardcoding "do not write the title out"
            // here silently blanked the title band on every channel whose rule asks for it.
            .AppendLine("Choose the ONE composition from the rule above that fits this title.")
            .AppendLine("The rule is the authority on lettering — follow exactly what it says to put in the")
            .AppendLine("image, whether that is no words at all, a short fragment, or the complete title.")
            .Append("Horizontal 16:9.")
            .ToString();

        if (File.Exists(run.ThumbnailPath)) { try { File.Delete(run.ThumbnailPath); } catch { } }

        var ok = await RunGflowAsync(prompt, run.ThumbnailPath, cfg, ct, references: references);
        if (!ok || !File.Exists(run.ThumbnailPath))
            throw new InvalidOperationException($"gflow finished but no thumbnail at {run.ThumbnailPath}.");

        logger.LogInformation("Thumbnail -> {Path}", run.ThumbnailPath);
        return run.ThumbnailPath;
    }

    public async Task<string> GenerateBrandImageAsync(
        Channel channel, string kind, string targetPath, CancellationToken ct)
    {
        kind = BrandImage.Normalize(kind);
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
        var cfg = settings.Current.Gflow;

        logger.LogInformation("Generating {Kind} for '{Channel}' ({Size}).",
            kind, channel.Name, BrandImage.SizeLabel(kind));

        // Written straight to a temp file and moved on success, so a failed regeneration leaves the
        // channel's existing art intact instead of deleting it first and coming back empty-handed.
        var temp = targetPath + ".new";
        if (File.Exists(temp)) { try { File.Delete(temp); } catch { } }

        var ok = await RunGflowAsync(BrandPrompt.Build(channel, kind), temp, cfg, ct, BrandImage.Aspect(kind));
        if (!ok || !File.Exists(temp))
        {
            if (File.Exists(temp)) { try { File.Delete(temp); } catch { } }
            throw new InvalidOperationException($"gflow finished but produced no {kind} for '{channel.Id}'.");
        }

        File.Move(temp, targetPath, overwrite: true);
        logger.LogInformation("{Kind} -> {Path}", kind, targetPath);
        return targetPath;
    }

    // -------------------------------------------------------------------------

    private async Task<bool> GenerateOneAsync(
        ScriptBeat beat, Channel channel, string target, GflowSettings cfg, CancellationToken ct,
        IReadOnlyList<string> references)
    {
        var prompt = BuildBeatPrompt(beat, channel);

        for (int attempt = 1; attempt <= MaxAttemptsPerBeat; attempt++)
        {
            if (await RunGflowAsync(prompt, target, cfg, ct, references: references) && File.Exists(target))
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
    /// <param name="aspect">
    /// Overrides the configured aspect for this one call. Needed for channel art, which is square
    /// (avatar) rather than the 16:9 the video frames are locked to.
    /// </param>
    private async Task<bool> RunGflowAsync(
        string prompt, string target, GflowSettings cfg, CancellationToken ct, string? aspect = null,
        IReadOnlyList<string>? references = null)
    {
        var args = BuildGenerationArgs(prompt, target, cfg, aspect, references);

        // Cap a single generation so one wedged browser can't stall the whole run.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(cfg.TimeoutSeconds));

        try
        {
            var result = await process.RunAsync(cfg.Executable, args, ct: timeout.Token);
            if (result.Success)
            {
                // Flow Agent otherwise continues with text-only generation after a failed upload.
                // Refuse that output so it cannot be resumed or rendered as a source-based frame.
                if (references is { Count: > 0 } && references.Any(File.Exists)
                    && !HasReferenceUploadEvidence(result.StdOut, references.Count))
                {
                    if (File.Exists(target)) File.Delete(target);
                    throw new ReferenceAttachmentException(
                        "Flow did not confirm every reference-image upload. Its output was rejected. " +
                        "Check Flow's reference uploads, or select a provider that attaches all source images.");
                }
                // Any success clears a previous benching, so the provider comes straight back
                // into rotation the moment its quota resets.
                health.MarkHealthy(ImageProviders.Gflow);
                return true;
            }

            var err = Trim(result.StdErr);
            if (result.StdErr.Contains("at most one local reference file", StringComparison.OrdinalIgnoreCase))
                throw new ReferenceAttachmentException("Flow rejected multiple local references. Stop this run and update the app to upload references before generation.");
            logger.LogWarning("gflow exited {Code}: {Err}", result.ExitCode, err);

            // A browser-side failure repeats identically on every beat and never clears on its
            // own, so say what fixes it — once — instead of leaving the user to read 80 copies of
            // Chrome's permission error and work out that the tab is on the wrong host.
            if (LooksLikeBrowserProblem(err) && Interlocked.Exchange(ref _browserWarned, 1) == 0)
                logger.LogWarning(
                    "gflow cannot reach the Flow page. This is not a quota problem and will not " +
                    "clear by retrying: open {Url} in Chrome, sign in, and leave that tab open. " +
                    "The extension attaches only to that exact page — a tab on flow.google.com " +
                    "produces this error on every beat.",
                    GflowStatus.FlowTabUrl);

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
        catch (ReferenceAttachmentException) { throw; }
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
            .AppendLine(ReferenceInstruction(channel))
            .AppendLine()
            .AppendLine("=== THIS FRAME ===");

        if (narration.Length > 0)
            sb.AppendLine($"The narrator is saying: {narration}");

        sb.AppendLine($"Draw exactly one image illustrating that line: {visual}");

        // The closing anchor is what keeps characters looking like the same characters across a
        // few hundred separate calls. It used to hardcode "stick figures with round heads and dot
        // eyes", which is true of two channels here and wrong for every other one — a channel is
        // the authority on how it is drawn, so take the anchor from its own visualStyle.
        var anchor = string.IsNullOrWhiteSpace(channel.VisualStyle)
            ? "the same characters, the same outlines and the same flat colour palette"
            : channel.VisualStyle.Trim();

        sb.AppendLine()
          .AppendLine("Keep the recurring visual language consistent with the rest of the set:")
          .AppendLine(anchor)
          .Append("One single clear idea, nothing extra.");

        return sb.ToString();
    }

    internal static IReadOnlyList<string> BuildGenerationArgs(
        string prompt, string target, GflowSettings cfg, string? aspect = null,
        IReadOnlyList<string>? references = null)
    {
        var args = new List<string>(cfg.ArgsTemplate)
        {
            prompt,
            string.IsNullOrWhiteSpace(cfg.OutputFlag) ? "-o" : cfg.OutputFlag,
            target,
        };
        var wantAspect = string.IsNullOrWhiteSpace(aspect) ? cfg.Aspect : aspect;
        if (!string.IsNullOrWhiteSpace(wantAspect)) args.AddRange(["--aspect", wantAspect]);
        if (!string.IsNullOrWhiteSpace(cfg.Model)) args.AddRange(["--model", cfg.Model]);
        // No project flag: `flow image` accepts none as of CLI 2.0.7 — the project moved to the
        // backend's DEFAULT_PROJECT environment variable, and `--project-id` survives only on
        // `upload`/`video`, marked deprecated. Passing cfg.Project here fails the whole call with
        // "unrecognized arguments", so a filled-in Project box must not reach the command line.
        args.AddRange(cfg.ExtraArgs);
        // The installed Flow Agent CLI uploads these local files and sends their media IDs as
        // IMAGE_INPUT_TYPE_REFERENCE. Every beat and thumbnail gets its own complete attachment set.
        if (references is { Count: > 0 })
        {
            args.Add("--ref");
            args.AddRange(references);
        }
        return args;
    }

    internal static bool HasReferenceUploadEvidence(string stdout, int expected) =>
        System.Text.RegularExpressions.Regex.Matches(stdout, @"(?m)^\s*media_id=\S+").Count == expected;

    private static string ReferenceInstruction(Channel channel) =>
        channel.ReferenceStudy is null ? "" :
        $"Actual reference images from {channel.ReferenceStudy.SourceUrl} are attached to this call. " +
        "Study their linework, faces, flat fills, scene density and framing. Use that visual language " +
        "for this original scene, not the reference's specific content. Do not recreate the reference sheet or its labels.";

    private async Task<IReadOnlyList<string>> PrepareReferencesAsync(
        GflowSettings cfg, IReadOnlyList<string> references, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var probe = await process.RunAsync(cfg.Executable, cfg.ArgsTemplate.Concat(["--help"]), ct: timeout.Token);
        if (!probe.Success || !(probe.StdOut + probe.StdErr).Contains("--ref", StringComparison.Ordinal))
            throw new InvalidOperationException(
                "This channel requires actual image references, but the configured Flow command does not " +
                "confirm --ref support. Configure Flow Agent's image command with --ref support or use Codex. " +
                "Generation has not started; text-only fallback is not allowed.");
        // New Flow CLI accepts many media IDs but only one local path. Upload separately;
        // its generation stdout reports OUTPUT media IDs, not reference-upload evidence.
        if (!(probe.StdOut + probe.StdErr).Contains("media ID", StringComparison.OrdinalIgnoreCase))
            return references;
        if (references.Count > 10)
            throw new ReferenceAttachmentException("Flow supports at most ten uploaded references; generation has not started.");
        var ids = new List<string>();
        foreach (var path in references)
        {
            using var uploadTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            uploadTimeout.CancelAfter(TimeSpan.FromSeconds(cfg.TimeoutSeconds));
            var upload = await process.RunAsync(cfg.Executable, ["upload", path], ct: uploadTimeout.Token);
            var id = upload.Success ? ParseUploadedReferenceId(upload.StdOut) : null;
            if (id is null)
                throw new ReferenceAttachmentException($"Flow did not confirm the reference upload for {Path.GetFileName(path)}. Generation has not started. {Trim(upload.StdErr)}");
            ids.Add(id);
        }
        logger.LogInformation("Flow uploaded all {Count} source references; every frame will receive their media IDs.", ids.Count);
        return ids;
    }

    internal static string? ParseUploadedReferenceId(string output)
    {
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(output);
            if (document.RootElement.TryGetProperty("media_id", out var value)
                && value.ValueKind == System.Text.Json.JsonValueKind.String
                && value.GetString() is { Length: > 0 } id && !id.Any(char.IsWhiteSpace)) return id;
        }
        catch (System.Text.Json.JsonException) { }
        return null;
    }

    private sealed class ReferenceAttachmentException(string message) : InvalidOperationException(message);

    /// <summary>
    /// Thumbnail rule, falling back to the frame rule when a channel has none. Kept separate from
    /// <see cref="StyleRule"/> because a thumbnail has a different job from a frame.
    /// </summary>
    private static string ThumbnailRule(Channel channel) =>
        !string.IsNullOrWhiteSpace(channel.ThumbnailInstructions)
            ? channel.ThumbnailInstructions
            : StyleRule(channel);

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

    /// <summary>
    /// Refuses to start when gflow says it cannot generate, naming the fix.
    /// </summary>
    /// <remarks>
    /// Only a probe that ran and clearly reported "not connected" blocks the run. If the probe
    /// itself fails — missing binary, timeout, output we cannot parse — the run proceeds: a check
    /// we could not perform is not evidence of a problem, and refusing to work because a
    /// diagnostic broke would be worse than the thing it diagnoses.
    /// </remarks>
    private async Task RequireGflowReadyAsync(GflowSettings cfg, CancellationToken ct)
    {
        if (cfg.CheckArgs.Count == 0) return;

        GflowStatus status;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(60));
            var probe = await process.RunAsync(cfg.Executable, cfg.CheckArgs, ct: timeout.Token);
            if (!probe.Success) return;
            status = GflowStatus.Parse(probe.StdOut);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not check gflow's status — starting anyway.");
            return;
        }

        if (status.CanGenerate) return;

        Volatile.Write(ref _browserWarned, 1);   // the remedy is in the exception; don't repeat it
        throw new InvalidOperationException(
            $"gflow is not ready to generate (status: {status.State}). {status.Remedy}");
    }

    private static string Trim(string s) =>
        s.Length <= 400 ? s.Trim() : s[..400].Trim() + "…";
}
