using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using YtPipeline.Core.Abstractions;
using YtPipeline.Core.Models;
using YtPipeline.Core.Services;
using YtPipeline.Stages.Process;

namespace YtPipeline.Stages.Images;

/// <summary>
/// Codex can't make images right now: it refused the model, or its image limit is spent. Every retry
/// would fail the same way, so none is made.
/// </summary>
public sealed class CodexUnavailableException(string message, Exception? inner = null)
    : InvalidOperationException(message, inner);

public sealed class CodexOptions
{
    public string Executable { get; set; } = "codex";

    /// <summary>
    /// Args for the single Codex run. The full instruction is piped via <b>stdin</b> (the trailing
    /// <c>-</c> tells Codex to read the prompt from stdin) — passing a large multi-line prompt as a
    /// command-line argument breaks on Windows. <c>exec</c> is non-interactive; <c>--skip-git-repo-check</c>
    /// allows running outside a git repo; the bypass flag lets Codex's image tool reach the network
    /// and write files unattended. To tighten the sandbox, replace the bypass flag with <c>"-s", "workspace-write"</c>.
    /// </summary>
    public List<string> ArgsTemplate { get; set; } = new()
        { "exec", "--skip-git-repo-check", "--dangerously-bypass-approvals-and-sandbox", "-" };

    public int Width { get; set; } = 3840;   // 4K UHD source frames
    public int Height { get; set; } = 2160;

    /// <summary>Cost watch: warn if Codex wrote more than this many images.</summary>
    public int WarnImageCount { get; set; } = 120;
}

/// <summary>
/// Stage 2. Hands Codex the channel's image rule + the whole script in one run and lets
/// Codex's built-in image tool produce the frames (one or more per beat, per the rule),
/// writing them into the run's images folder. Returns the PNGs it finds afterward.
/// </summary>
public sealed class CodexCliImageGenerator(
    ProcessRunner process,
    CodexOptions options,
    SettingsStore settings,
    ProviderHealthStore health,
    ILogger<CodexCliImageGenerator> logger) : IImageGenerator
{
    /// <summary>How many times to try a Codex run before giving up (handles transient connection drops).</summary>
    private const int MaxAttempts = 3;

    public async Task<IReadOnlyList<string>> GenerateAsync(
        Run run, ScriptDocument script, Channel channel, CancellationToken ct,
        bool regenerate = false, string? forceProvider = null)
    {
        var references = ReferenceStudySources.Images(channel, run.WorkspaceRoot);
        var args = BuildGenerationArgs(options.ArgsTemplate, references,
            settings.Current.CodexModel, settings.Current.CodexReasoningEffort);
        Directory.CreateDirectory(run.ImagesDir);
        if (regenerate) ClearPngs(run.ImagesDir); // explicit redo: start from a clean slate

        int attempt = 0;
        while (true)
        {
            attempt++;

            // Which beats still need a frame? Skipping the done ones makes the stage resumable:
            // a crash or net drop mid-run leaves finished frames in place, and we only ask Codex
            // for what's missing on the next pass.
            var have = ExistingBeatIndices(run.ImagesDir);
            var missing = script.Beats.Select(b => b.Index).Where(i => !have.Contains(i)).ToList();

            if (missing.Count == 0 && ImageSet.List(run.ImagesDir).Count > 0)
            {
                var done = ImageSet.List(run.ImagesDir);
                logger.LogInformation("Stage 2: all {Count} beats already have images — nothing to generate.", done.Count);
                return done;
            }

            // Pause here (don't fail) if the internet is down; resume when it returns.
            await NetworkGuard.WaitForConnectivityAsync(logger, ct);

            if (have.Count > 0)
                logger.LogInformation("Resuming images: {Have} done, {Missing} to generate.", have.Count, missing.Count);

            var prompt = BuildPrompt(run, script, channel, missing);
            try
            {
                // Prompt via stdin (args end with "-"); cwd = images folder so Codex's copy step lands here.
                var result = await RunCodexAsync(args, run.ImagesDir, prompt, ct);

                var images = ImageSet.List(run.ImagesDir);
                if (images.Count == 0)
                {
                    StopIfOutOfImages(result);
                    throw new InvalidOperationException(
                        $"Codex finished but wrote no PNGs into {run.ImagesDir}.{CodexSaid(result)}");
                }

                // If Codex left some beats unfilled, loop to generate just those (resume keeps progress)
                // — unless it stopped because the image limit ran out, which another pass can't fix.
                var stillMissing = script.Beats.Select(b => b.Index)
                    .Where(i => !ExistingBeatIndices(run.ImagesDir).Contains(i)).ToList();
                if (stillMissing.Count > 0) StopIfOutOfImages(result);
                else health.MarkHealthy(ImageProviders.Codex);
                if (stillMissing.Count > 0 && attempt < MaxAttempts)
                {
                    logger.LogWarning("Codex left {Count} beat(s) without an image — retrying those.", stillMissing.Count);
                    continue;
                }

                if (images.Count > options.WarnImageCount)
                    logger.LogWarning(
                        "Codex produced {Count} images (> {Limit}).", images.Count, options.WarnImageCount);

                logger.LogInformation("Stage 2 produced {Count} images.", images.Count);
                return images;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw; // user pressed Stop — don't swallow
            }
            catch (CodexUnavailableException)
            {
                throw; // a refused model or a spent limit fails every attempt the same way
            }
            catch (Exception ex)
            {
                // If the failure coincides with a network outage, pause and resume WITHOUT
                // counting it as a wasted attempt — finished frames are preserved on disk.
                if (!NetworkGuard.IsOnline())
                {
                    logger.LogWarning("Image run failed while offline ({Msg}). Finished frames are safe; waiting to resume.", ex.Message);
                    await NetworkGuard.WaitForConnectivityAsync(logger, ct);
                    attempt--;
                    continue;
                }
                if (attempt >= MaxAttempts) throw;
                logger.LogWarning("Codex image run failed (attempt {Attempt}/{Max}): {Msg}. Retrying in 5s…",
                    attempt, MaxAttempts, ex.Message);
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
            }
        }
    }

    /// <summary>One Codex run, with a refused model or a spent image limit turned into something you can act on.</summary>
    private async Task<ProcessResult> RunCodexAsync(
        IEnumerable<string> args, string workingDirectory, string prompt, CancellationToken ct)
    {
        try
        {
            return await process.RunOrThrowAsync(options.Executable, args,
                workingDirectory: workingDirectory, stdin: prompt, ct: ct);
        }
        catch (ProcessException ex)
        {
            StopIfOutOfImages(ex.Result);
            if (ExplainRefusedModel(ex.Result.StdErr + "\n" + ex.Result.StdOut, settings.Current.CodexModel) is { } why)
                throw new CodexUnavailableException(why, ex);
            throw;
        }
    }

    /// <summary>
    /// Benches Codex and stops when its image tool reported the usage limit.
    /// </summary>
    /// <remarks>
    /// Codex does not fail when this happens. It exits 0 and says in prose that it couldn't make the
    /// image (2026-09-30: three thumbnail attempts, then three more, each "finished but no
    /// thumbnail"). The tool's own 429 is on stderr, with the reset time:
    /// <c>image generation failed: http 429 … "usage_limit_reached" … "resets_at":1790816429</c>.
    /// Call it only when the expected image is missing: an image made before the limit is still good.
    /// </remarks>
    private void StopIfOutOfImages(ProcessResult result)
    {
        var output = result.StdErr + "\n" + result.StdOut;
        if (!IsOutOfImages(output, out var resetsUtc)) return;

        var line = output.Split('\n').FirstOrDefault(l => l.Contains("usage_limit_reached"))?.Trim()
                   ?? "usage_limit_reached";
        if (health.MarkExhausted(ImageProviders.Codex, line, settings.Current.QuotaCooldownMinutes, resetsUtc))
            logger.LogWarning("Codex's image limit is used up — pausing Codex until {Reset}.",
                resetsUtc?.ToLocalTime().ToString("ddd h:mm tt") ?? "the cooldown ends");
        throw new CodexUnavailableException(OutOfImagesMessage(resetsUtc));
    }

    internal static bool IsOutOfImages(string output, out DateTime? resetsUtc)
    {
        resetsUtc = null;
        if (!output.Contains("usage_limit_reached", StringComparison.Ordinal)) return false;
        // Inside Codex's log the JSON is escaped: \"resets_at\":1790816429
        var m = Regex.Match(output, @"resets_at\\?""\s*:\s*(\d{9,11})");
        if (m.Success) resetsUtc = DateTimeOffset.FromUnixTimeSeconds(long.Parse(m.Groups[1].Value)).UtcDateTime;
        return true;
    }

    internal static string OutOfImagesMessage(DateTime? resetsUtc) =>
        "Codex has used up its image generation limit" +
        (resetsUtc is { } r ? $", which resets {r.ToLocalTime():ddd d MMM 'at' h:mm tt}." : ". Try again later.") +
        " Anything it made before that is kept.";

    /// <summary>Codex's closing message, for when it finished without the image and the log should say why.</summary>
    private static string CodexSaid(ProcessResult result)
    {
        var said = result.StdOut.ReplaceLineEndings(" ").Trim();
        if (said.Length == 0) return "";
        return $" Codex said: \"{(said.Length > 400 ? "…" + said[^400..] : said)}\"";
    }

    /// <summary>
    /// Plain words for Codex refusing the model, or null for any other failure.
    /// </summary>
    /// <remarks>
    /// Codex blames the account even when the CLI is simply older than the model: on 2026-09-30,
    /// CLI 0.155.1 answered gpt-6.1-sol with "not supported when using Codex with a ChatGPT
    /// account" while 0.159.0 ran it fine on the same sign-in. The old CLI also warns
    /// "Model metadata for `…` not found", which is what separates that case from a model the
    /// account really can't use.
    /// </remarks>
    internal static string? ExplainRefusedModel(string output, string model)
    {
        if (!output.Contains("model is not supported", StringComparison.OrdinalIgnoreCase)) return null;

        return output.Contains("Model metadata for", StringComparison.OrdinalIgnoreCase)
            ? $"Codex refused {model} because the Codex program this app runs is older than that model. " +
              "Update it with \"codex update\" in a PowerShell window opened from the Start menu, " +
              "or pick another Codex model in Settings → Images."
            : $"Codex says this account can't use {model}. Pick another Codex model in Settings → Images.";
    }

    /// <summary>Beat indices that already have a <c>beat&lt;NN&gt;</c> PNG on disk.</summary>
    private static HashSet<int> ExistingBeatIndices(string imagesDir) =>
        ImageSet.List(imagesDir)
            .Select(ImageSet.BeatIndexOf)
            .Where(i => i is not null)
            .Select(i => i!.Value)
            .ToHashSet();

    public async Task<string> GenerateThumbnailAsync(Run run, ScriptDocument script, Channel channel, CancellationToken ct)
    {
        var references = ReferenceStudySources.Images(channel, run.WorkspaceRoot, thumbnail: true);
        var args = BuildGenerationArgs(options.ArgsTemplate, references,
            settings.Current.CodexModel, settings.Current.CodexReasoningEffort);
        Directory.CreateDirectory(run.OutputDir);
        var prompt = BuildThumbnailPrompt(run, script, channel);

        for (int attempt = 1; ; attempt++)
        {
            if (File.Exists(run.ThumbnailPath)) { try { File.Delete(run.ThumbnailPath); } catch { } }
            try
            {
                var result = await RunCodexAsync(args, run.OutputDir, prompt, ct);

                if (!File.Exists(run.ThumbnailPath))
                {
                    StopIfOutOfImages(result);
                    throw new InvalidOperationException(
                        $"Codex finished but no thumbnail at {run.ThumbnailPath}.{CodexSaid(result)}");
                }

                health.MarkHealthy(ImageProviders.Codex);
                logger.LogInformation("Thumbnail -> {Path}", run.ThumbnailPath);
                return run.ThumbnailPath;
            }
            catch (Exception ex) when (attempt < MaxAttempts && !ct.IsCancellationRequested
                                       && ex is not CodexUnavailableException)
            {
                logger.LogWarning("Codex thumbnail failed (attempt {Attempt}/{Max}): {Msg}. Retrying in 5s…",
                    attempt, MaxAttempts, ex.Message);
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
            }
        }
    }

    public async Task<string> GenerateBrandImageAsync(
        Channel channel, string kind, string targetPath, CancellationToken ct)
    {
        kind = BrandImage.Normalize(kind);
        var dir = Path.GetDirectoryName(targetPath)!;
        Directory.CreateDirectory(dir);

        logger.LogInformation("Generating {Kind} for '{Channel}' ({Size}).",
            kind, channel.Name, BrandImage.SizeLabel(kind));

        // Written to a temp name and moved on success, so a failed regeneration leaves the
        // channel's existing art alone rather than destroying it up front.
        var temp = targetPath + ".new";
        var prompt = new StringBuilder()
            .AppendLine(BrandPrompt.Build(channel, kind))
            .AppendLine()
            .AppendLine("You MUST create this image with your native image_generation tool. Do NOT draw or synthesize it with")
            .AppendLine("code (no Python/PIL/matplotlib/cairo/SVG/HTML/ImageMagick) — an image produced by code is unacceptable.")
            .AppendLine("After generating it, SAVE the image as a PNG file at this exact path:")
            .AppendLine($"    {temp}")
            .Append("Do not ask for confirmation.")
            .ToString();

        for (int attempt = 1; ; attempt++)
        {
            if (File.Exists(temp)) { try { File.Delete(temp); } catch { } }
            try
            {
                var result = await RunCodexAsync(BuildGenerationArgs(options.ArgsTemplate, [],
                        settings.Current.CodexModel, settings.Current.CodexReasoningEffort),
                    dir, prompt, ct);

                if (!File.Exists(temp))
                {
                    StopIfOutOfImages(result);
                    throw new InvalidOperationException($"Codex finished but no {kind} at {temp}.{CodexSaid(result)}");
                }

                health.MarkHealthy(ImageProviders.Codex);
                File.Move(temp, targetPath, overwrite: true);
                logger.LogInformation("{Kind} -> {Path}", kind, targetPath);
                return targetPath;
            }
            catch (Exception ex) when (attempt < MaxAttempts && !ct.IsCancellationRequested
                                       && ex is not CodexUnavailableException)
            {
                logger.LogWarning("Codex {Kind} failed (attempt {Attempt}/{Max}): {Msg}. Retrying in 5s…",
                    kind, attempt, MaxAttempts, ex.Message);
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
            }
        }
    }

    private static void ClearPngs(string dir)
    {
        if (!Directory.Exists(dir)) return;
        foreach (var f in Directory.EnumerateFiles(dir, "*.png"))
            try { File.Delete(f); } catch { /* ignore */ }
    }

    public string BuildThumbnailPrompt(Run run, ScriptDocument script, Channel channel)
    {
        // A thumbnail gets the THUMBNAIL rule, not the frame rule. Reusing the frame rule made
        // covers that looked like a random frame from the video — explanatory rather than
        // arresting. Falls back to the frame rule only if no thumbnail rule exists.
        var rule = !string.IsNullOrWhiteSpace(channel.ThumbnailInstructions)
            ? channel.ThumbnailInstructions
            : !string.IsNullOrWhiteSpace(channel.ImageInstructions)
                ? channel.ImageInstructions
                : !string.IsNullOrWhiteSpace(channel.VisualStyle)
                    ? $"Draw in this style: {channel.VisualStyle}."
                    : "simple, bold, high-contrast";

        var sb = new StringBuilder();
        sb.AppendLine("Create ONE YouTube thumbnail image.");
        sb.AppendLine($"Video title: \"{script.Title}\"");
        sb.AppendLine();
        AppendReferenceInstructions(sb, channel, run.WorkspaceRoot, thumbnail: true);
        sb.AppendLine("=== THUMBNAIL RULE — COPY THIS ENTIRE BLOCK VERBATIM INTO THE IMAGE PROMPT ===");
        sb.AppendLine(rule.TrimEnd());
        sb.AppendLine("=== END THUMBNAIL RULE ===");
        sb.AppendLine();
        sb.AppendLine("=== FOR THIS TITLE ===");
        // Composition and lettering belong to the RULE, never to this builder. These lines used to
        // hardcode "decide what the everyday object is" and "do not write the title out" — which
        // only ever described _default.thumbnail.md, and that file already says both. For a channel
        // whose own rule demands the complete title in a white band, the hardcoded ban won: the
        // band was drawn correctly and left empty, on every thumbnail, silently.
        sb.AppendLine("Read the title above and choose the ONE composition from the rule that fits it.");
        sb.AppendLine("The rule is the authority on lettering — follow exactly what it says to put in the image,");
        sb.AppendLine("whether that is no words at all, a short fragment, or the complete title verbatim.");
        sb.AppendLine("Horizontal 16:9, 1280x720.");
        sb.AppendLine();
        // Same failure the per-beat prompt had: the agent paraphrases the rule when writing its own
        // image prompt, and the constraints that survive are the ones the image model would have
        // guessed anyway. What gets dropped is whatever is unusual — and "flat stick characters
        // inside a painted, shaded world" is exactly that, because the model's default is to render
        // subject and background at one consistent level of detail. Result: a shaded, textured
        // character in a scene the rule wanted flat, which reads as generic AI art.
        sb.AppendLine("The prompt you pass to image_generation MUST be:");
        sb.AppendLine("  1. the ENTIRE thumbnail-rule block above, copied word for word between its two === markers —");
        sb.AppendLine("     not summarised, not shortened, not trimmed to the parts that look relevant here;");
        sb.AppendLine("  2. then one line: THIS THUMBNAIL: <the title, and the composition you chose>.");
        sb.AppendLine();
        AppendNoAttachments(sb, channel);
        sb.AppendLine(channel.ReferenceStudy is null
            ? "The image model sees ONLY the text you pass in that call — it cannot see this instruction or"
            : "The image model sees the text and reference images attached to that call — it cannot see this instruction or");
        sb.AppendLine("the rule above. Any constraint you leave out is simply not applied. Pay particular attention to");
        sb.AppendLine("anything the rule says about how CHARACTERS are drawn versus how the BACKGROUND is drawn: if it");
        sb.AppendLine("asks for flat characters in a shaded world, that contrast is deliberate and is the channel's");
        sb.AppendLine("whole look. Rendering the characters to match the background is the single most common way");
        sb.AppendLine("this comes out wrong.");
        sb.AppendLine();
        sb.AppendLine("You MUST create this image with your native image_generation tool. Do NOT draw or synthesize it with");
        sb.AppendLine("code (no Python/PIL/matplotlib/cairo/SVG/HTML/ImageMagick) — an image produced by code is unacceptable.");
        sb.AppendLine("After generating it, SAVE the image as a PNG file at this exact path:");
        sb.AppendLine($"    {run.ThumbnailPath}");
        sb.AppendLine("The file name must be exactly thumbnail.png. Do not ask for confirmation.");
        return sb.ToString();
    }

    /// <summary>Public so the CLI <c>--dry-run</c> can print the Codex prompt without calling Codex.</summary>
    public string BuildPrompt(Run run, ScriptDocument script, Channel channel) =>
        BuildPrompt(run, script, channel, script.Beats.Select(b => b.Index).ToList());

    /// <summary>
    /// Builds the Codex prompt for a specific set of beats. On a resume, <paramref name="onlyBeats"/>
    /// contains just the missing beat indices so Codex doesn't redo finished frames.
    /// </summary>
    public string BuildPrompt(Run run, ScriptDocument script, Channel channel, IReadOnlyCollection<int> onlyBeats)
    {
        var rule = string.IsNullOrWhiteSpace(channel.ImageInstructions)
            ? $"Draw in this style: {channel.VisualStyle}. Keep one clear idea per image."
            : channel.ImageInstructions;

        var wanted = new HashSet<int>(onlyBeats);
        var beats = script.Beats.OrderBy(b => b.Index).Where(b => wanted.Contains(b.Index)).ToList();
        bool partial = beats.Count != script.Beats.Count;

        var sb = new StringBuilder();
        AppendReferenceInstructions(sb, channel, run.WorkspaceRoot);
        // Delimited so the instruction below can demand it be copied verbatim. Codex reaches the
        // image model through an agent that writes its own per-image prompt, so unlike gflow —
        // which pastes this whole rule ahead of every single frame — the rule text never reaches
        // the model unless the agent carries it there.
        sb.AppendLine("=== STYLE RULES — COPY THIS ENTIRE BLOCK VERBATIM INTO EVERY IMAGE PROMPT ===");
        sb.AppendLine(rule.TrimEnd());
        sb.AppendLine("=== END STYLE RULES ===");
        sb.AppendLine();
        if (partial)
        {
            sb.AppendLine("=== RESUMING ===");
            sb.AppendLine("Some frames were already generated. Generate images ONLY for the beats listed below " +
                          "(the others already exist — do not touch or recreate them).");
            sb.AppendLine();
        }
        sb.AppendLine("=== SCRIPT (beats) ===");
        foreach (var beat in beats)
        {
            sb.AppendLine($"Beat {beat.Index}: {beat.Narration}");
            if (!string.IsNullOrWhiteSpace(beat.ImagePrompt))
                sb.AppendLine($"    suggested visual: {beat.ImagePrompt}");
        }
        sb.AppendLine();
        sb.AppendLine("=== HOW TO PRODUCE THE IMAGES (READ CAREFULLY) ===");
        sb.AppendLine("You MUST create every image with your native image_generation tool — the same tool you use to actually draw/generate pictures.");
        sb.AppendLine("When using code-mode tools, display results with generatedImage(result); log only output_hint or saved paths.");
        sb.AppendLine("Never print or JSON-stringify the entire image result: its base64 pixels flood the context and waste the generation budget.");
        sb.AppendLine();
        sb.AppendLine("ABSOLUTELY FORBIDDEN: Do NOT draw, render, rasterize, or synthesize images with code of ANY kind.");
        sb.AppendLine("Do NOT use Python, PIL/Pillow, matplotlib, numpy, cairo, OpenCV, SVG, HTML/CSS/canvas,");
        sb.AppendLine("ImageMagick, ffmpeg, or any script/program to produce or edit image pixels. Read-only inspection of");
        sb.AppendLine("file metadata (dimensions, format and opacity) is allowed; it must not modify image pixels.");
        sb.AppendLine("Shell commands may copy/move/rename the files the image_generation tool created. An image drawn by code is");
        sb.AppendLine("UNACCEPTABLE — if you produce one, delete it and regenerate it with the image_generation tool.");
        sb.AppendLine();
        var studied = channel.ReferenceStudy is not null;
        sb.AppendLine("For EACH beat, produce one accepted FINAL PNG with the image_generation tool. Inspect the result");
        sb.AppendLine(studied
            ? "and regenerate with that native tool when needed to correct visual or source-matching failures."
            : "and regenerate with that native tool when needed to correct anything that breaks the style rules.");
        sb.AppendLine("Keep only the accepted final PNG per beat in the output folder. Every tool call MUST use this prompt:");
        sb.AppendLine("  1. the ENTIRE style-rules block above, copied word for word between its two === markers —");
        sb.AppendLine("     not summarised, not shortened, not trimmed to the parts that look relevant to this beat;");
        sb.AppendLine("  2. then one line: THIS FRAME: <a single sentence describing what this beat shows>.");
        sb.AppendLine();
        AppendNoAttachments(sb, channel);
        sb.AppendLine(studied
            ? "The image model sees the text and actual reference images attached to that one call. It cannot see this instruction,"
            : "The image model sees ONLY the text you pass in that one call. It cannot see this instruction,");
        sb.AppendLine("the rules above, or anything you sent for an earlier beat — every call starts blank. So any");
        sb.AppendLine("constraint you leave out is simply not applied, and the frame comes back in the model's own");
        sb.AppendLine(studied
            ? "default style instead of this channel's. Keep the complete rule and all required source images"
            : "default style instead of this channel's. Keep the complete rule");
        sb.AppendLine("present on every native image tool call, including later beats and retries.");
        sb.AppendLine();
        sb.AppendLine("Do not shorten the rules as you go to save effort. It is completely fine and expected to call");
        sb.AppendLine("the image_generation tool many times (including quality corrections) with a long prompt each time — do that, do");
        sb.AppendLine("not take shortcuts. Quality matters far more than speed.");
        sb.AppendLine();
        sb.AppendLine("=== FILE OUTPUT (REQUIRED) ===");
        sb.AppendLine("Save EXACTLY ONE accepted FINAL PNG per beat listed above; rejected attempts must stay outside this folder.");
        sb.AppendLine("Save each generated PNG into this exact folder:");
        sb.AppendLine($"    {run.ImagesDir}");
        sb.AppendLine("Name each file: beat<NN>_<short-label>.png");
        sb.AppendLine("  - <NN> = the 2-digit index of the beat it illustrates (00, 01, 02 ... matching the beat numbers above)");
        sb.AppendLine("Every image must be horizontal, approximately 16:9. Preserve the native image tool's dimensions,");
        sb.AppendLine("including valid output such as 1672x941; do not reject or regenerate an otherwise good frame just");
        sb.AppendLine("because its pixel dimensions differ from the video target. Do not resize, crop or pad it with code.");
        sb.AppendLine($"The final video assembler normalizes frames to the configured target ({options.Width}x{options.Height}).");
        // ~5% of codex frames came back RGBA with a transparent background. A transparent frame has
        // no flat colour field at all, and the video stage composites it onto black — so the beat
        // arrives as a subject floating in a void. The image model does not think of alpha as a
        // background choice unless told, so tell it.
        sb.AppendLine("Every image must be FULLY OPAQUE, with no alpha channel and no transparency anywhere —");
        sb.AppendLine("the background is a colour that is actually painted in, not left empty. A transparent");
        sb.AppendLine("background is a failed frame: it composites onto black in the finished video.");
        sb.AppendLine($"Do not ask for confirmation. When finished, the folder must contain exactly {script.Beats.Count} PNGs — one per beat, every one produced by the image_generation tool.");
        return sb.ToString();
    }

    /// <summary>
    /// For a channel without a reference study: the rule text is the whole style, so nothing may be
    /// attached to the image call.
    /// </summary>
    /// <remarks>
    /// Codex attaches images of its own accord. On 2026-09-06 it passed The Old Humans' avatar, banner
    /// and old thumbnail as referenced_image_paths for a new thumbnail, and it re-feeds its own
    /// earlier outputs when "correcting" a frame. Either one pulls the look away from the channel's
    /// rule. Reference images belong only to channels that configure them (Barely An Expert).
    /// </remarks>
    private static void AppendNoAttachments(StringBuilder sb, Channel channel)
    {
        if (channel.ReferenceStudy is not null) return;
        sb.AppendLine("This channel uses NO reference images. Never pass referenced_image_paths or any image input to");
        sb.AppendLine("the image_generation tool: not the channel's avatar, banner or earlier thumbnails, not earlier");
        sb.AppendLine("frames or your own previous outputs, and not any other channel's reference files. To correct a");
        sb.AppendLine("result, generate it again from the full rule text.");
        sb.AppendLine();
    }

    /// <summary>Native CLI attachments make the agent actually see the reference pixels.</summary>
    internal static IReadOnlyList<string> BuildGenerationArgs(
        IReadOnlyList<string> template, IReadOnlyList<string> references,
        string? model = null, string? effort = null)
    {
        var args = template.ToList();
        if (model is not null && effort is not null)
        {
            // Replace the shipped startup defaults so a Settings change takes effect on
            // the next generation, without changing the user's global Codex config.
            for (var i = args.Count - 2; i >= 0; i--)
            {
                if (args[i] == "--model" ||
                    (args[i] == "-c" && args[i + 1].StartsWith("model_reasoning_effort=", StringComparison.Ordinal)))
                    args.RemoveRange(i, 2);
            }
            var promptIndex = args.IndexOf("-");
            if (promptIndex < 0) promptIndex = args.Count;
            args.InsertRange(promptIndex, ["--model", model, "-c", $"model_reasoning_effort=\"{effort}\""]);
        }
        var insertion = args.IndexOf("-");
        if (insertion < 0) insertion = args.Count;
        foreach (var path in references)
        {
            args.Insert(insertion++, "--image");
            args.Insert(insertion++, path);
        }
        return args;
    }

    private static void AppendReferenceInstructions(
        StringBuilder sb, Channel channel, string workspaceRoot, bool thumbnail = false)
    {
        var references = ReferenceStudySources.Images(channel, workspaceRoot, thumbnail);
        if (references.Count == 0) return;
        sb.AppendLine("=== ACTUAL SOURCE IMAGES — REQUIRED ON EVERY IMAGE TOOL CALL ===")
            .AppendLine($"Reference channel: {channel.ReferenceStudy!.SourceUrl}")
            .AppendLine("The attached files contain real source images. Inspect each source with view_image before")
            .AppendLine("generating. Match their linework, face construction, palette, scene density and framing;")
            .AppendLine("create original scenes for this narration. Do not reproduce their labels, watermark or artwork.")
            .AppendLine("For EVERY individual frame/thumbnail call, pass ALL these absolute paths to the native image")
            .AppendLine("tool's referenced_image_paths parameter as image inputs, in addition to the full style rule:")
            .AppendLine(System.Text.Json.JsonSerializer.Serialize(references))
            .AppendLine("Initial CLI attachments only let you inspect the sources. They do not replace the required")
            .AppendLine("referenced_image_paths on each image generation call. A prose mention or a previous generated")
            .AppendLine("frame is not a source attachment. Do not omit references on later beats, retries, or the thumbnail.")
            .AppendLine("If the native image tool cannot accept these files as reference images, STOP and report that")
            .AppendLine("failure; do not generate text-only substitutes. The sources set the style, not the new scene content.")
            .AppendLine("Inspect each generated result beside the actual sources before saving it as a finished frame.")
            .AppendLine("Reject mismatched facial anatomy, extra texture, dense shading or scene composition that conflicts")
            .AppendLine("with the source evidence and channel rule; correct the image using the same original source inputs.")
            .AppendLine("=== END ACTUAL SOURCE IMAGES ===")
            .AppendLine();
    }
}
