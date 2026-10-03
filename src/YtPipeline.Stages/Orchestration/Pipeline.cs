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
    SourceVerifier sources,
    YtPipeline.Stages.Process.ProcessRunner process,
    YtPipeline.Stages.Video.FfmpegOptions ffmpeg,
    ILogger<Pipeline> logger)
{
    public RunManager Runs => runs;
    public ChannelLoader Channels => channels;

    /// <summary>
    /// How many times the description is rewritten while citations fail to verify. Bounded because
    /// a model that cannot find a real source for a claim will not find one on the tenth try
    /// either — at that point the honest move is to publish fewer sources.
    /// </summary>
    private const int MaxDescriptionAttempts = 3;

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
    public async Task ImagesAsync(
        Run run, CancellationToken ct, bool regenerate = false, string? provider = null)
    {
        RequireApproved(run, "script");
        var channel = channels.Load(run.Channel);
        var script = runs.LoadScript(run);
        regenerate |= runs.LoadManifest(run).Stages.Images.Get<bool>("needsRegeneration");
        logger.LogInformation("[2/5] images — {Count} beats{Mode}{Provider}", script.Beats.Count,
            regenerate ? " (regenerate)" : "",
            string.IsNullOrWhiteSpace(provider) ? "" : $" on {provider}");

        var archived = runs.PrepareImageGeneration(run, regenerate);
        if (archived is not null)
            logger.LogInformation("Previous images preserved at {Path}; generating a new set.", archived);
        // Preparation already isolated the old set. Never ask a backend to delete active frames:
        // after a timeout or failed preflight, the next attempt resumes only this new generation.
        var images = await imageGen.GenerateAsync(run, script, channel, ct, regenerate: false, forceProvider: provider);
        var covered = images.Select(ImageSet.BeatIndexOf).Where(i => i.HasValue)
            .Select(i => i!.Value).ToHashSet();
        var missing = script.Beats.Where(b => !covered.Contains(b.Index)).Select(b => b.Index).ToList();
        if (missing.Count > 0)
            throw new InvalidOperationException($"Images are incomplete: {missing.Count} beats remain. Resume image generation before rendering.");
        if (!script.Beats.Select(b => (b.Index, b.Narration, b.ImagePrompt))
                .SequenceEqual(runs.LoadScript(run).Beats.Select(b => (b.Index, b.Narration, b.ImagePrompt))))
            throw new InvalidOperationException("The storyboard changed during image generation. Generate images for the updated script before rendering.");
        runs.UpdateManifest(run, m =>
        {
            m.Stages.Images.MarkDone(run.ImagesDir);
            m.Stages.Images.Set("count", images.Count);
            m.Stages.Images.Set("dir", run.ImagesDir);
        });
    }

    // ----- Stage 3 -----------------------------------------------------------
    /// <param name="model">
    /// ElevenLabs model for this video only. Null keeps the one this video was last voiced with by
    /// choice (so a Redo from the card doesn't quietly switch back); "" goes back to the channel's.
    /// </param>
    public async Task VoiceAsync(Run run, CancellationToken ct, string? model = null)
    {
        var channel = channels.Load(run.Channel);
        var script = runs.LoadScript(run);
        logger.LogInformation("[3/5] voice");

        var choice = VoiceModelChoice(model, runs.LoadManifest(run).Stages.Voice.Get<string>("modelChoice"));
        var modelUsed = voiceGen.ModelFor(channel, choice);
        if (choice is not null)
            logger.LogInformation("Voicing with {Model}, picked for this video.", choice);

        runs.UpdateManifest(run, m =>
        {
            m.Stages.Voice.Done = false;
            m.Stages.Voice.CompletedUtc = null;
            InvalidateVideo(m);
        });
        // Some TTS responses contain no alignment. Preserve the previous file outside the active
        // path so that response cannot accidentally reuse timing from a different voiceover.
        if (File.Exists(run.AlignmentPath))
            File.Move(run.AlignmentPath, run.AlignmentPath + ".previous_" + Guid.NewGuid().ToString("N"));
        var audioPath = await voiceGen.GenerateAsync(run, script, channel, ct, choice);
        if (script.NarrationForVoice() != runs.LoadScript(run).NarrationForVoice())
            throw new InvalidOperationException("The narration changed while generating voice. Generate voice for the updated script before rendering.");
        var seconds = Mp3Duration.GetSeconds(audioPath);
        runs.UpdateManifest(run, m =>
        {
            m.Stages.Voice.MarkDone(audioPath);
            m.Stages.Voice.Set("seconds", Math.Round(seconds, 1));
            // What voiced the mp3 on disk, whatever it came from — the channel's model can change later.
            if (!string.IsNullOrEmpty(modelUsed)) m.Stages.Voice.Set("model", modelUsed);
            if (choice is not null) m.Stages.Voice.Set("modelChoice", choice);
            else m.Stages.Voice.Clear("modelChoice");
        });

        CalibrateSpeakingRate(channel, script, seconds);
        UpdateChapterTimestamps(run, script);
    }

    /// <summary>
    /// This video's own model: the one just asked for, else the one it kept from last time.
    /// "" means "back to the channel's", which clears it. Null = no model of its own.
    /// </summary>
    internal static string? VoiceModelChoice(string? requested, string? remembered)
    {
        if (requested is null) return string.IsNullOrWhiteSpace(remembered) ? null : remembered;
        var clean = VoiceModelIds.Clean(requested);
        return clean == "" ? null : clean;
    }

    private static void InvalidateVideo(Manifest manifest)
    {
        manifest.Stages.Video.Done = false;
        manifest.Stages.Video.CompletedUtc = null;
        manifest.Review.VideoApproved = false;
    }

    private void UpdateChapterTimestamps(Run run, ScriptDocument script)
    {
        if (!File.Exists(run.AlignmentPath)) return;
        var alignment = Json.ReadFile<AudioAlignment>(run.AlignmentPath);
        var description = YtPipeline.Stages.Video.ChapterTimestamps.TryAlign(script, alignment);
        if (description is null || description == script.Description) return;
        script.Description = description;
        runs.SaveScript(run, script);
        runs.UpdateManifest(run, m => m.Publish.Description = description);
        logger.LogInformation("Chapter timestamps updated from the completed narration alignment.");
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
    /// <summary>
    /// Assembles final.mp4.
    /// </summary>
    /// <param name="music">
    /// Per-run override for background music. <c>false</c> assembles a voice-only video,
    /// <c>true</c> forces the normal selection, and <c>null</c> keeps whatever this run chose last
    /// time (falling back to the channel's own <see cref="Channel.Music"/> setting). The choice is
    /// remembered on the manifest so a later re-assembly — from another tab, or the CLI — doesn't
    /// silently put the music back.
    /// </param>
    public async Task VideoAsync(Run run, CancellationToken ct, bool? music = null)
    {
        var channel = channels.Load(run.Channel);
        var script = runs.LoadScript(run);
        // A failed preflight also means the old output cannot be advertised as current. Clear
        // readiness before checking assets; only a successful assembly can restore it.
        var ready = runs.UpdateManifest(run, InvalidateVideo);
        if (!ready.Stages.Voice.Done || !ready.Stages.Images.Done)
            throw new InvalidOperationException("The current script needs completed voice and image stages before rendering. Existing files may belong to the previous version.");
        RequireCurrentVoiceAlignment(run, script);
        UpdateChapterTimestamps(run, script);
        var images = LoadImagePaths(run, script);
        logger.LogInformation("[video] assembling final.mp4");

        // A caller that said nothing inherits this run's previous answer, so "no music" survives
        // a plain re-assemble.
        var wantMusic = music ?? runs.LoadManifest(run).Stages.Video.Get<bool?>("music");

        // Build a music playlist sized to cover the full video length.
        // Returns empty list (no music) if disabled, library is empty, or selection fails.
        var audioSeconds = Mp3Duration.GetSeconds(run.AudioPath);
        IReadOnlyList<string> musicPaths;
        if (wantMusic == false)
        {
            logger.LogInformation("Background music turned off for this video — assembling voice-only.");
            musicPaths = [];
        }
        else
        {
            // wantMusic == true is an explicit ask for this one video, so it overrides a channel
            // that has music switched off. Null just follows the channel as before.
            musicPaths = await musicSelector.SelectAsync(channel, script, audioSeconds, ct, wantMusic);
        }

        var musicNames = musicPaths
            .Select(p => MusicSelector.StripLabel(Path.GetFileNameWithoutExtension(p)))
            .ToList();
        var videoPath = await videoGen.AssembleAsync(run, script, images, run.AudioPath, musicPaths, ct);
        runs.UpdateManifest(run, m =>
        {
            m.Stages.Video.MarkDone(videoPath);
            // Cleared rather than left alone when there's no music: these describe the mp4 that was
            // just written, so stale entries from a previous musical assembly would be a lie.
            if (musicNames.Count > 0) m.Stages.Video.Set("musicTracks", musicNames);
            else m.Stages.Video.Clear("musicTracks");
            // The topic as typed, not whatever the script generator came back with. Enforced at
            // generation too, but repeated here so runs whose script.json was written by older
            // code get the right title from a re-assemble alone — no script regeneration needed.
            m.Publish.Title = string.IsNullOrWhiteSpace(run.Topic) ? script.Title : run.Topic.Trim();
            // The music libraries require artist + title in the description or the upload can
            // collect a Content ID claim, so the credit is composed from the tracks actually
            // mixed in rather than left to be remembered at upload time. Rebuilt from the script's
            // own description each time, so dropping the music also drops the credit.
            m.Publish.Description = MusicCredit.Append(script.Description, musicPaths);
            m.Publish.Tags = script.Tags;
            if (musicPaths.Count > 0)
                m.Stages.Video.Set("musicCredits", MusicCredit.Lines(musicPaths));
            else
                m.Stages.Video.Clear("musicCredits");

            if (wantMusic is { } w) m.Stages.Video.Set("music", w);
            else m.Stages.Video.Clear("music");
        });

        // Produce the publish thumbnail if we don't already have one.
        if (!File.Exists(run.ThumbnailPath))
            await GenerateThumbnailInternal(run, channel, script, ct);
    }

    /// <summary>
    /// Generates a channel's avatar or banner art. Not part of the five stages — this belongs to
    /// the channel, not to any run, and is produced on demand from the Channels screen.
    /// </summary>
    public async Task<string> BrandImageAsync(string channelId, string kind, CancellationToken ct)
    {
        kind = BrandImage.Normalize(kind);
        var channel = channels.Load(channelId);
        var target = channels.BrandImagePath(channelId, kind);
        logger.LogInformation("[brand] {Kind} for {Channel}", kind, channel.Name);
        var path = await imageGen.GenerateBrandImageAsync(channel, kind, target, ct);

        if (kind == BrandImage.Banner) await EnsureBannerIsUploadableAsync(path, ct);
        return path;
    }

    /// <summary>
    /// Scales a banner up to YouTube's size when the backend returned something smaller.
    /// </summary>
    /// <remarks>
    /// gflow's <c>image</c> command takes aspect NAMES and has no size or resolution flag at all,
    /// so "landscape" comes back 1376x768 however large a target the prompt asks for. YouTube's
    /// minimum banner upload is 2048x1152, which means every gflow banner was below the floor and
    /// would have been rejected at upload — silently, because nothing here looked at the pixels.
    /// codex honours the requested size and lands on 2560x1440 already, so this is a no-op there.
    /// <para>
    /// The 1376x768 the provider returns is 1.792:1 against the 1.778:1 YouTube wants, so scaling
    /// to exactly 2560x1440 stretches by 0.8% — invisible, and preferable to cropping content out
    /// of a composition whose edges were deliberately placed against the safe area.
    /// </para>
    /// </remarks>
    private async Task EnsureBannerIsUploadableAsync(string path, CancellationToken ct)
    {
        var (w, h) = ImageSize.Read(path);
        if (w <= 0) return;                                              // not a PNG we can measure
        if (w >= BrandImage.BannerWidth && h >= BrandImage.BannerHeight) return;

        logger.LogInformation(
            "Banner came back {W}x{H}, below YouTube's {Min}x{MinH} minimum — scaling to {TW}x{TH}.",
            w, h, 2048, 1152, BrandImage.BannerWidth, BrandImage.BannerHeight);

        var scaled = path + ".scaled.png";
        await process.RunOrThrowAsync(ffmpeg.Executable, new[]
        {
            "-y", "-i", path,
            "-vf", $"scale={BrandImage.BannerWidth}:{BrandImage.BannerHeight}:flags=lanczos",
            scaled,
        }, ct: ct);

        File.Move(scaled, path, overwrite: true);
    }


    /// <summary>Regenerate the YouTube thumbnail on demand.</summary>
    public async Task ThumbnailAsync(Run run, CancellationToken ct)
    {
        var channel = channels.Load(run.Channel);
        var script = runs.LoadScript(run);
        await GenerateThumbnailInternal(run, channel, script, ct);
    }

    /// <summary>
    /// Rewrites the YouTube description to the channel's house format, leaving the video alone.
    /// </summary>
    /// <remarks>
    /// Runs on its own because the description is the only part of a finished video that is cheap
    /// to redo: the narration is voiced and every beat has a frame, so regenerating the script to
    /// fix the text under the video would discard all of it. This is also how a change to the
    /// channel's format reaches videos that already exist.
    /// </remarks>
    public async Task DescriptionAsync(Run run, CancellationToken ct)
    {
        var channel = channels.Load(run.Channel);
        var script = runs.LoadScript(run);
        logger.LogInformation("[description] rewriting for '{Title}'", script.Title);

        if (scriptGen is not ClaudeCliScriptGenerator claude)
            throw new InvalidOperationException("The description rewrite needs the Claude script generator.");

        // Write, then CHECK — every citation is looked up in Crossref and Open Library. Anything
        // that cannot be found is named back to the writer and the description is written again,
        // because a fabricated citation reads exactly like a real one and no amount of re-reading
        // catches it. Whatever survives is corrected to match the record and given its real link.
        var description = "";
        var rejected = new List<string>();

        for (var attempt = 1; attempt <= MaxDescriptionAttempts; attempt++)
        {
            description = await claude.RewriteDescriptionAsync(channel, script, ct, rejected);
            var checks = await sources.VerifyAsync(description, ct);

            if (checks.Count == 0)
            {
                logger.LogInformation("Description has no citations to verify.");
                break;
            }

            // Only citations the databases answered about, and denied. One that could not be
            // checked at all is left alone rather than rewritten away.
            var bad = checks.Where(c => c.Fabricated).Select(c => c.Entry.Title).ToList();
            var unchecked_ = checks.Count(c => !c.Checked);
            logger.LogInformation(
                "Source check {Attempt}/{Max}: {Good} of {Total} verified{Unchecked}.",
                attempt, MaxDescriptionAttempts, checks.Count(c => c.Found), checks.Count,
                unchecked_ > 0 ? $", {unchecked_} could not be checked and were kept" : "");

            if (bad.Count == 0)
            {
                description = SourceVerifier.ApplyCorrections(description, checks);
                break;
            }

            // Last attempt: keep what verified and drop the rest rather than publishing a citation
            // nothing can find.
            if (attempt == MaxDescriptionAttempts)
            {
                logger.LogWarning(
                    "Dropping {Count} unverifiable citation(s) after {Max} attempts: {Titles}",
                    bad.Count, MaxDescriptionAttempts, string.Join(" | ", bad));
                description = SourceVerifier.ApplyCorrections(description, checks);
                break;
            }

            logger.LogWarning(
                "{Count} citation(s) could not be found — asking for replacements: {Titles}",
                bad.Count, string.Join(" | ", bad));
            rejected.AddRange(bad);
        }

        var previous = script.Description?.Length ?? 0;
        script.Description = description;
        runs.SaveScript(run, script);

        // The manifest carries what actually gets uploaded, and it may already hold a music credit
        // appended at assembly time — so rewrite it from the new text and let the video stage
        // re-append the credit on the next assembly.
        runs.UpdateManifest(run, m => m.Publish.Description = description);

        logger.LogInformation(
            "Description rewritten: {Before} → {After} characters.", previous, description.Length);
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
        script.Title = TitleForPublish(run, script);

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
        script.Title = TitleForPublish(run, script);
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

    /// <summary>
    /// The title to publish with: the topic exactly as it was typed. A script generated before the
    /// title was pinned still carries whatever the model invented, so this is applied at publish
    /// time too rather than trusting <see cref="ScriptDocument.Title"/>.
    /// </summary>
    private static string TitleForPublish(Run run, ScriptDocument script) =>
        string.IsNullOrWhiteSpace(run.Topic) ? script.Title : run.Topic.Trim();

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
        var beatIndices = paths.Select(ImageSet.BeatIndexOf).ToList();
        var required = script.Beats.Select(b => b.Index).ToHashSet();
        var present = beatIndices.Where(i => i.HasValue).Select(i => i!.Value).ToHashSet();
        if (beatIndices.Any(i => i is null || !required.Contains(i.Value)) || !required.SetEquals(present))
            throw new InvalidOperationException("The image set does not cover the current script. Regenerate the missing or outdated beat images before rendering.");
        return paths;
    }

    private static void RequireCurrentVoiceAlignment(Run run, ScriptDocument script)
    {
        if (!File.Exists(run.AlignmentPath)) return; // legacy/unaligned TTS still uses estimated timing
        var alignment = Json.ReadFile<AudioAlignment>(run.AlignmentPath);
        var spoken = script.NarrationForVoice();
        if (!string.Equals(alignment.Text, spoken, StringComparison.Ordinal)
            || alignment.CharStartTimes.Count != spoken.Length)
            throw new InvalidOperationException(
                "The saved voice alignment does not match the current narration. Regenerate the voice " +
                "before rendering; timing from an older script cannot be reused.");
    }
}
