using Microsoft.Extensions.Logging;
using YtPipeline.Core.Abstractions;
using YtPipeline.Core.Models;
using YtPipeline.Core.Services;
using YtPipeline.Stages.Images;
using YtPipeline.Stages.Music;
using YtPipeline.Stages.Script;
using YtPipeline.Stages.Voice;

namespace YtPipeline.Stages.Orchestration;

/// <summary>
/// Chains the five stages, persisting progress to the manifest after each one and
/// stopping at unapproved review checkpoints (script, video).
/// </summary>
public sealed class Pipeline(
    RunManager runs,
    ChannelLoader channels,
    IScriptGenerator scriptGen,
    PhoneticNarrationCleaner phonetics,
    IImageGenerator imageGen,
    IVoiceGenerator voiceGen,
    MusicSelector musicSelector,
    IVideoAssembler videoGen,
    IVideoUploader uploader,
    IEnumerable<ISocialPublisher> publishers,
    ILogger<Pipeline> logger)
{
    public RunManager Runs => runs;
    public ChannelLoader Channels => channels;

    // ----- Stage 1 -----------------------------------------------------------
    public async Task<ScriptDocument> ScriptAsync(Run run, CancellationToken ct)
    {
        var channel = channels.Load(run.Channel);
        logger.LogInformation("[1/5] script — {Channel}: {Topic}", channel.Name, run.Topic);

        // Which skeleton this video is being written to, captured BEFORE generation so the run
        // records what it was actually built from even if a later run rotates onward.
        var template = ClaudeCliScriptGenerator.PickTemplate(channel);
        if (template is not null)
            logger.LogInformation("Script template: '{Name}' ({Count} of {Total} available).",
                template.Name, 1, channel.ScriptTemplates.Count);

        var script = await scriptGen.GenerateAsync(channel, run.Topic, ct);
        // Respell TTS-risky words (names, places, foreign/scientific terms) before the voice stage,
        // so ElevenLabs pronounces them right on its single call — no audio re-generation, no credits.
        await phonetics.ApplyAsync(script, ct);
        runs.SaveScript(run, script);
        runs.UpdateManifest(run, m =>
        {
            m.Stages.Script.MarkDone(run.ScriptPath);
            if (template is not null) m.Stages.Script.Set("template", template.Name);
        });

        // Stamp the template as used so the next video rotates to a different one. Done after a
        // successful generation, so a failed run doesn't burn a slot in the rotation.
        if (template is not null)
        {
            try
            {
                template.LastUsedUtc = DateTime.UtcNow;
                channels.Save(channel.Id, channel);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not record template usage — rotation may repeat.");
            }
        }

        return script;
    }

    // ----- Stage 2 -----------------------------------------------------------
    public async Task ImagesAsync(Run run, CancellationToken ct, bool regenerate = false)
    {
        RequireApproved(run, "script");
        var channel = channels.Load(run.Channel);
        var script = runs.LoadScript(run);
        logger.LogInformation("[2/5] images — {Count} beats{Mode}", script.Beats.Count,
            regenerate ? " (regenerate)" : "");

        var images = await imageGen.GenerateAsync(run, script, channel, ct, regenerate);
        runs.UpdateManifest(run, m =>
        {
            m.Stages.Images.MarkDone(run.ImagesDir);
            m.Stages.Images.Set("count", images.Count);
            m.Stages.Images.Set("dir", run.ImagesDir);
        });
    }

    // ----- Stage 3 -----------------------------------------------------------
    public async Task VoiceAsync(Run run, CancellationToken ct)
    {
        var channel = channels.Load(run.Channel);
        var script = runs.LoadScript(run);
        logger.LogInformation("[3/5] voice");

        var audioPath = await voiceGen.GenerateAsync(run, script, channel, ct);
        var seconds = Mp3Duration.GetSeconds(audioPath);
        runs.UpdateManifest(run, m =>
        {
            m.Stages.Voice.MarkDone(audioPath);
            m.Stages.Voice.Set("seconds", Math.Round(seconds, 1));
        });

        CalibrateSpeakingRate(channel, script, seconds);
    }

    /// <summary>
    /// Learns this channel's real speaking rate from the audio just produced, and warns when the
    /// result missed the requested runtime.
    /// </summary>
    /// <remarks>
    /// The word-count target can only size a video correctly if it knows how fast the voice talks,
    /// and that varies enormously between voices — 112 and 215 wpm on two real channels here. So
    /// the observed rate is written back to the channel and used to budget the next script.
    /// New readings are blended with the old so one unusual script can't swing the estimate.
    /// </remarks>
    private void CalibrateSpeakingRate(Channel channel, ScriptDocument script, double seconds)
    {
        if (seconds < 5) return;   // too short to measure anything meaningful

        var words = script.NarrationForVoice()
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
        if (words < 50) return;

        var observed = words / (seconds / 60.0);
        if (observed is < 60 or > 320)
        {
            logger.LogWarning("Ignoring implausible speaking rate of {Wpm:F0} wpm.", observed);
            return;
        }

        var previous = channel.Voice?.WordsPerMinute;
        var blended = previous is { } p ? (p * 0.5) + (observed * 0.5) : observed;

        // Did we actually land inside the requested window?
        var minutes = seconds / 60.0;
        var lo = channel.VideoLengthMinMinutes > 0 ? channel.VideoLengthMinMinutes : channel.VideoLengthTargetMinutes;
        var hi = channel.VideoLengthMaxMinutes > 0 ? channel.VideoLengthMaxMinutes : lo;
        if (minutes < lo * 0.95 || minutes > hi * 1.05)
        {
            logger.LogWarning(
                "Voiceover is {Mins:F1} min, outside this channel's {Lo}-{Hi} min target. " +
                "The voice reads at {Observed:F0} wpm ({Words} words). Recorded that rate — the " +
                "next script for '{Channel}' will be sized for it.",
                minutes, lo, hi, observed, words, channel.Id);
        }
        else
        {
            logger.LogInformation(
                "Voiceover {Mins:F1} min, within the {Lo}-{Hi} min target ({Observed:F0} wpm).",
                minutes, lo, hi, observed);
        }

        try
        {
            channel.Voice ??= new ChannelVoice();
            channel.Voice.WordsPerMinute = Math.Round(blended, 1);
            channels.Save(channel.Id, channel);
            logger.LogInformation(
                "Speaking rate for '{Channel}' calibrated to {Wpm:F0} wpm{From}.",
                channel.Id, blended, previous is { } q ? $" (was {q:F0})" : "");
        }
        catch (Exception ex)
        {
            // Calibration is an optimisation — never fail a finished voiceover over it.
            logger.LogWarning(ex, "Could not save the calibrated speaking rate.");
        }
    }

    // ----- Stage 4 -----------------------------------------------------------
    public async Task VideoAsync(Run run, CancellationToken ct)
    {
        var channel = channels.Load(run.Channel);
        var script = runs.LoadScript(run);
        var images = LoadImagePaths(run, script);
        logger.LogInformation("[video] assembling final.mp4");

        // Build a music playlist sized to cover the full video length.
        // Returns empty list (no music) if disabled, library is empty, or selection fails.
        var audioSeconds = Mp3Duration.GetSeconds(run.AudioPath);
        // ffmpeg writes with -y, so it truncates the previous final.mp4 the moment it starts. If
        // this attempt then fails, a Done flag left over from an earlier success would point at a
        // broken file and the UI would call it ready. Clear it up front and let success re-set it.
        runs.UpdateManifest(run, m =>
        {
            m.Stages.Video.Done = false;
            m.Stages.Video.CompletedUtc = null;
        });

        var musicPaths = await musicSelector.SelectAsync(channel, script, audioSeconds, ct);

        var musicNames = musicPaths
            .Select(p => MusicSelector.StripLabel(Path.GetFileNameWithoutExtension(p)))
            .ToList();
        var videoPath = await videoGen.AssembleAsync(run, script, images, run.AudioPath, musicPaths, ct);
        runs.UpdateManifest(run, m =>
        {
            m.Stages.Video.MarkDone(videoPath);
            if (musicNames.Count > 0) m.Stages.Video.Set("musicTracks", musicNames);
            m.Publish.Title = script.Title;
            // The music libraries require artist + title in the description or the upload can
            // collect a Content ID claim, so the credit is composed from the tracks actually
            // mixed in rather than left to be remembered at upload time.
            m.Publish.Description = MusicCredit.Append(script.Description, musicPaths);
            m.Publish.Tags = script.Tags;
            if (musicPaths.Count > 0)
                m.Stages.Video.Set("musicCredits", MusicCredit.Lines(musicPaths));
        });

        // Produce the publish thumbnail if we don't already have one.
        if (!File.Exists(run.ThumbnailPath))
            await GenerateThumbnailInternal(run, channel, script, ct);
    }

    /// <summary>Regenerate the YouTube thumbnail on demand.</summary>
    public async Task ThumbnailAsync(Run run, CancellationToken ct)
    {
        var channel = channels.Load(run.Channel);
        var script = runs.LoadScript(run);
        await GenerateThumbnailInternal(run, channel, script, ct);
    }

    private async Task GenerateThumbnailInternal(Run run, Channel channel, ScriptDocument script, CancellationToken ct)
    {
        logger.LogInformation("[thumbnail] generating");
        await imageGen.GenerateThumbnailAsync(run, script, channel, ct);
        runs.UpdateManifest(run, m =>
        {
            m.Publish.HasThumbnail = true;
            if (string.IsNullOrEmpty(m.Publish.Title)) m.Publish.Title = script.Title;
            if (string.IsNullOrEmpty(m.Publish.Description)) m.Publish.Description = script.Description;
            if (m.Publish.Tags.Count == 0) m.Publish.Tags = script.Tags;
        });
    }

    // ----- Stage 5 -----------------------------------------------------------
    public async Task UploadAsync(Run run, string privacy, CancellationToken ct)
    {
        RequireApproved(run, "video");
        var channel = channels.Load(run.Channel);
        var script = runs.LoadScript(run);
        logger.LogInformation("[5/5] upload — privacy={Privacy}", privacy);

        // Publish with the description the video stage composed — it carries the required music
        // credit. Falls back to the script's own text for runs assembled before that existed.
        script.Description = DescriptionForPublish(run, script);

        var videoId = await uploader.UploadAsync(run, channel, script, run.VideoPath, privacy, ct);
        runs.UpdateManifest(run, m =>
        {
            m.Stages.Upload.MarkDone();
            m.Stages.Upload.Set("videoId", videoId);
            m.Stages.Upload.Set("privacy", privacy);
        });
    }

    // ----- Extra distribution targets ----------------------------------------

    /// <summary>
    /// Publishes the rendered video to one social platform (<c>facebook</c>, <c>instagram</c>).
    /// Independent of the YouTube upload — a run can go to Facebook without ever touching
    /// YouTube — but it still requires the video checkpoint, since this posts publicly.
    /// </summary>
    public async Task<SocialPublishResult> PublishAsync(Run run, string platform, CancellationToken ct)
    {
        RequireApproved(run, "video");

        var publisher = publishers.FirstOrDefault(
            p => string.Equals(p.Platform, platform, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException(
                $"Unknown publish target '{platform}'. Known: " +
                string.Join(", ", publishers.Select(p => p.Platform)));

        if (!File.Exists(run.VideoPath))
            throw new InvalidOperationException("Assemble the video before publishing it.");

        var channel = channels.Load(run.Channel);
        var script = runs.LoadScript(run);
        // Same credit requirement applies off YouTube — Facebook also runs rights matching.
        script.Description = DescriptionForPublish(run, script);
        logger.LogInformation("publish → {Platform} — {Channel}", publisher.Platform, channel.Name);

        var result = await publisher.PublishAsync(run, channel, script, run.VideoPath, ct);

        if (result.Skipped)
        {
            // Not an error: the platform simply isn't set up. Recorded so the UI can explain why
            // nothing happened without the user digging through logs.
            logger.LogWarning("{Platform} skipped — {Reason}", publisher.Platform, result.SkipReason);
            runs.UpdateManifest(run, m => Slot(m, publisher.Platform).Set("skipReason", result.SkipReason!));
            return result;
        }

        runs.UpdateManifest(run, m =>
        {
            var slot = Slot(m, publisher.Platform);
            slot.MarkDone();
            slot.Set("postId", result.PostId!);
            if (result.Url is not null) slot.Set("url", result.Url);
            slot.Extra.Remove("skipReason");
        });
        return result;
    }

    /// <summary>
    /// Publishes everywhere that's configured, in one job: YouTube first, then each social
    /// platform. Deliberately tolerant — an unconfigured platform is skipped and a failing one is
    /// logged and stepped over, so one dead token can't stop the others from going out. Throws
    /// only if every target failed outright.
    /// </summary>
    public async Task PublishEverywhereAsync(Run run, string privacy, CancellationToken ct)
    {
        RequireApproved(run, "video");
        var failures = new List<string>();
        var attempted = 0;

        var manifest = runs.LoadManifest(run);
        if (!manifest.Stages.Upload.Done)
        {
            attempted++;
            try { await UploadAsync(run, privacy, ct); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                logger.LogError(ex, "YouTube upload failed — continuing with the other targets.");
                failures.Add($"youtube: {ex.Message}");
            }
        }
        else logger.LogInformation("YouTube: already uploaded, skipping.");

        foreach (var p in publishers)
        {
            attempted++;
            try
            {
                var r = await PublishAsync(run, p.Platform, ct);
                if (r.Skipped) logger.LogInformation("{Platform}: {Reason}", p.Platform, r.SkipReason);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                logger.LogError(ex, "{Platform} failed — continuing.", p.Platform);
                failures.Add($"{p.Platform}: {ex.Message}");
            }
        }

        if (attempted > 0 && failures.Count == attempted)
            throw new InvalidOperationException(
                "Every publish target failed:\n  " + string.Join("\n  ", failures));

        if (failures.Count > 0)
            logger.LogWarning("Finished with {Count} failed target(s): {Failures}",
                failures.Count, string.Join("; ", failures));
    }

    /// <summary>
    /// The description to publish with: the one the video stage composed (which includes the
    /// required music credit), falling back to the script's own text when a run predates it or
    /// was assembled without music.
    /// </summary>
    private string DescriptionForPublish(Run run, ScriptDocument script)
    {
        var stored = runs.LoadManifest(run).Publish.Description;
        return string.IsNullOrWhiteSpace(stored) ? script.Description : stored;
    }

    /// <summary>Manifest slot for a platform's status.</summary>
    private static StageStatus Slot(Manifest m, string platform) =>
        platform.ToLowerInvariant() switch
        {
            "facebook" => m.Stages.Facebook,
            "instagram" => m.Stages.Instagram,
            _ => throw new ArgumentException($"No manifest slot for '{platform}'."),
        };

    // ----- Orchestration -----------------------------------------------------
    public async Task<Run> RunAllAsync(string channelId, string topic, string privacy, CancellationToken ct)
    {
        channels.Load(channelId); // fail fast if the channel is unknown
        var run = runs.Create(channelId, topic);
        logger.LogInformation("Run {RunId} started.", run.RunId);

        await ScriptAsync(run, ct);
        if (!StopForReview(run, "script", "approve the script checkpoint")) return run;

        await VoiceAsync(run, ct);
        await ImagesAsync(run, ct);
        await VideoAsync(run, ct);
        if (!StopForReview(run, "video", "approve the video checkpoint")) return run;

        await UploadAsync(run, privacy, ct);
        logger.LogInformation("Run {RunId} complete.", run.RunId);
        return run;
    }

    /// <summary>
    /// Auto mode: run script → voice → images → video and auto-approve both checkpoints.
    /// Stops before upload (kept manual). The run is created by the caller.
    /// </summary>
    public async Task AutoThroughVideoAsync(Run run, CancellationToken ct)
    {
        logger.LogInformation("Auto mode — script → voice → images → video (auto-approving).");
        await ScriptAsync(run, ct);
        Approve(run, "script");
        await VoiceAsync(run, ct);
        await ImagesAsync(run, ct);
        await VideoAsync(run, ct);
        Approve(run, "video");
        logger.LogInformation("Auto mode complete — review & upload when ready.");
    }

    public void Approve(Run run, string stage)
    {
        runs.UpdateManifest(run, m =>
        {
            switch (stage.ToLowerInvariant())
            {
                case "script": m.Review.ScriptApproved = true; break;
                case "video": m.Review.VideoApproved = true; break;
                default: throw new ArgumentException($"Unknown checkpoint '{stage}'. Use 'script' or 'video'.");
            }
        });
        logger.LogInformation("Approved {Stage} for {RunId}.", stage, run.RunId);
    }

    private bool StopForReview(Run run, string stage, string hint)
    {
        var m = runs.LoadManifest(run);
        var approved = stage == "script" ? m.Review.ScriptApproved : m.Review.VideoApproved;
        if (approved) return true;

        logger.LogWarning("⏸  Checkpoint '{Stage}' not approved — {Hint} to continue.", stage, hint);
        return false;
    }

    private void RequireApproved(Run run, string stage)
    {
        var m = runs.LoadManifest(run);
        var approved = stage == "script" ? m.Review.ScriptApproved : m.Review.VideoApproved;
        if (!approved)
            throw new InvalidOperationException(
                $"Checkpoint '{stage}' is not approved for run {run.RunId}. Approve it first.");
    }

    private static IReadOnlyList<string> LoadImagePaths(Run run, ScriptDocument script)
    {
        var paths = ImageSet.List(run.ImagesDir);
        if (paths.Count == 0)
            throw new FileNotFoundException(
                $"No images found in {run.ImagesDir} — run the images stage first.");
        return paths;
    }
}
