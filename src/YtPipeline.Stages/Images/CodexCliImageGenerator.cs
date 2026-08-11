using System.Text;
using Microsoft.Extensions.Logging;
using YtPipeline.Core.Abstractions;
using YtPipeline.Core.Models;
using YtPipeline.Stages.Process;

namespace YtPipeline.Stages.Images;

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
    ILogger<CodexCliImageGenerator> logger) : IImageGenerator
{
    /// <summary>How many times to try a Codex run before giving up (handles transient connection drops).</summary>
    private const int MaxAttempts = 3;

    public async Task<IReadOnlyList<string>> GenerateAsync(
        Run run, ScriptDocument script, Channel channel, CancellationToken ct, bool regenerate = false)
    {
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
                await process.RunOrThrowAsync(
                    options.Executable, options.ArgsTemplate,
                    workingDirectory: run.ImagesDir, stdin: prompt, ct: ct);

                var images = ImageSet.List(run.ImagesDir);
                if (images.Count == 0)
                    throw new InvalidOperationException(
                        $"Codex finished but wrote no PNGs into {run.ImagesDir}. " +
                        "Check that Codex is signed in and its image tool is enabled.");

                // If Codex left some beats unfilled, loop to generate just those (resume keeps progress).
                var stillMissing = script.Beats.Select(b => b.Index)
                    .Where(i => !ExistingBeatIndices(run.ImagesDir).Contains(i)).ToList();
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

    /// <summary>Beat indices that already have a <c>beat&lt;NN&gt;</c> PNG on disk.</summary>
    private static HashSet<int> ExistingBeatIndices(string imagesDir) =>
        ImageSet.List(imagesDir)
            .Select(ImageSet.BeatIndexOf)
            .Where(i => i is not null)
            .Select(i => i!.Value)
            .ToHashSet();

    public async Task<string> GenerateThumbnailAsync(Run run, ScriptDocument script, Channel channel, CancellationToken ct)
    {
        Directory.CreateDirectory(run.OutputDir);
        var prompt = BuildThumbnailPrompt(run, script, channel);

        for (int attempt = 1; ; attempt++)
        {
            if (File.Exists(run.ThumbnailPath)) { try { File.Delete(run.ThumbnailPath); } catch { } }
            try
            {
                await process.RunOrThrowAsync(
                    options.Executable, options.ArgsTemplate,
                    workingDirectory: run.OutputDir, stdin: prompt, ct: ct);

                if (!File.Exists(run.ThumbnailPath))
                    throw new InvalidOperationException($"Codex finished but no thumbnail at {run.ThumbnailPath}.");

                logger.LogInformation("Thumbnail -> {Path}", run.ThumbnailPath);
                return run.ThumbnailPath;
            }
            catch (Exception ex) when (attempt < MaxAttempts && !ct.IsCancellationRequested)
            {
                logger.LogWarning("Codex thumbnail failed (attempt {Attempt}/{Max}): {Msg}. Retrying in 5s…",
                    attempt, MaxAttempts, ex.Message);
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
        sb.AppendLine("=== THUMBNAIL RULE (FOLLOW EXACTLY) ===");
        sb.AppendLine(rule.TrimEnd());
        sb.AppendLine();
        sb.AppendLine("=== FOR THIS TITLE ===");
        sb.AppendLine("Read the title above and choose the ONE composition from the rule that fits it. Decide what");
        sb.AppendLine("the everyday object is, and what the consequence or goal is, then draw only those. Do not");
        sb.AppendLine("write the title out — the picture is the hook.");
        sb.AppendLine("Horizontal 16:9, 1280x720.");
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
        sb.AppendLine(rule.TrimEnd());
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
        sb.AppendLine();
        sb.AppendLine("ABSOLUTELY FORBIDDEN: Do NOT draw, render, rasterize, or synthesize images with code of ANY kind.");
        sb.AppendLine("Do NOT write or run Python, PIL/Pillow, matplotlib, numpy, cairo, OpenCV, SVG, HTML/CSS/canvas,");
        sb.AppendLine("ImageMagick, ffmpeg, or any script/program that produces or edits image pixels. Shell commands may");
        sb.AppendLine("ONLY be used to move/rename the files the image_generation tool created. An image drawn by code is");
        sb.AppendLine("UNACCEPTABLE — if you produce one, delete it and regenerate it with the image_generation tool.");
        sb.AppendLine();
        sb.AppendLine("For EACH beat: call the image_generation tool once with a prompt that faithfully follows the STYLE");
        sb.AppendLine("rules above and depicts that beat, then place the resulting PNG into the folder below. It is completely");
        sb.AppendLine("fine and expected to call the image_generation tool many times (once per beat) — do that, do not take");
        sb.AppendLine("shortcuts. Quality matters far more than speed.");
        sb.AppendLine();
        sb.AppendLine("=== FILE OUTPUT (REQUIRED) ===");
        sb.AppendLine("Generate EXACTLY ONE image per beat (one image for each beat listed above — no more, no fewer).");
        sb.AppendLine("Save each generated PNG into this exact folder:");
        sb.AppendLine($"    {run.ImagesDir}");
        sb.AppendLine("Name each file: beat<NN>_<short-label>.png");
        sb.AppendLine("  - <NN> = the 2-digit index of the beat it illustrates (00, 01, 02 ... matching the beat numbers above)");
        sb.AppendLine($"Every image must be horizontal 16:9, {options.Width}x{options.Height}.");
        sb.AppendLine($"Do not ask for confirmation. When finished, the folder must contain exactly {script.Beats.Count} PNGs — one per beat, every one produced by the image_generation tool.");
        return sb.ToString();
    }
}
