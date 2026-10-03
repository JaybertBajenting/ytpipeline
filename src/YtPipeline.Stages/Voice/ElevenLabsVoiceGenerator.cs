using Microsoft.Extensions.Logging;
using Refit;
using YtPipeline.Core.Abstractions;
using YtPipeline.Core.Models;
using YtPipeline.Core.Services;
using YtPipeline.Stages.Process;
using YtPipeline.Stages.Video;

namespace YtPipeline.Stages.Voice;

public sealed class ElevenLabsOptions
{
    public string VoiceId { get; set; } = "";
    /// <summary>Last-resort model, under Settings → Audio's default. See <see cref="ElevenLabsVoiceGenerator.ModelFor"/>.</summary>
    public string ModelId { get; set; } = "eleven_multilingual_v2";
    public double Stability { get; set; } = 0.5;
    public double SimilarityBoost { get; set; } = 0.75;

    /// <summary>
    /// Characters allowed in ONE text-to-speech request. 0 (the default) picks the limit from the
    /// model instead — see <see cref="ElevenLabsVoiceGenerator.RequestCharacterLimit"/>. Set it
    /// only to override a model whose real limit differs from the one assumed there.
    /// </summary>
    public int MaxRequestCharacters { get; set; }
}

/// <summary>Stage 3. POSTs the full narration to ElevenLabs and saves the mp3.</summary>
public sealed class ElevenLabsVoiceGenerator(
    IElevenLabsApi api,
    ElevenLabsOptions options,
    SettingsStore settings,
    ProcessRunner process,
    FfmpegOptions ffmpeg,
    ILogger<ElevenLabsVoiceGenerator> logger) : IVoiceGenerator
{
    /// <summary>
    /// This video's pick, then the channel's, then the Settings default, then appsettings.json.
    /// Settings is read per call so a change there applies to the next voiceover without a restart.
    /// </summary>
    public string ModelFor(Channel channel, string? model = null) =>
        Pick(model, Pick(channel.Voice?.ModelId, Pick(settings.Current.ElevenLabsModel, options.ModelId)));

    public async Task<string> GenerateAsync(Run run, ScriptDocument script, Channel channel, CancellationToken ct,
        string? model = null)
    {
        // Per-channel voice wins; anything the channel leaves blank falls back to the global
        // option, so a channel with no "voice" block behaves exactly as it did before.
        var cv = channel.Voice ?? new ChannelVoice();
        var voiceId = Pick(cv.VoiceId, options.VoiceId);
        var modelId = ModelFor(channel, model);
        var stability = cv.Stability ?? options.Stability;
        var similarity = cv.SimilarityBoost ?? options.SimilarityBoost;
        // Null leaves the voice at its natural pace, exactly as before this setting existed.
        var speed = cv.Speed;

        if (string.IsNullOrWhiteSpace(voiceId))
            throw new InvalidOperationException(
                $"No voice id for channel '{channel.Id}'. Set one on the channel (Channels → Voice) " +
                "or set ELEVENLABS_VOICE_ID in .env.");

        // Only the spoken narration from the beats — no titles, descriptions, or image text.
        var narration = script.NarrationForVoice();
        if (string.IsNullOrWhiteSpace(narration))
            throw new InvalidOperationException("Script has no narration to voice.");

        logger.LogInformation(
            "Voicing {Words} words with voice {Voice}{Named} (model {Model}, speed {Speed}).",
            narration.Split(' ').Length,
            voiceId,
            string.IsNullOrWhiteSpace(cv.VoiceName) ? "" : $" '{cv.VoiceName}'",
            string.IsNullOrWhiteSpace(modelId) ? "default" : modelId,
            speed is { } s ? s.ToString("0.00") : "natural");

        // One request per API character limit. A 7-11 minute script is 9,000-14,000 characters and
        // eleven_v3 refuses anything over 5,000 ("text_too_long"), so long-form channels have to be
        // split. Chunks are sent independently: eleven_v3 also rejects previous_text/next_text
        // ("Providing previous_text or next_text is not yet supported with the 'eleven_v3' model"),
        // which is why the split lands on sentence ends — a natural place for delivery to reset.
        var limit = RequestCharacterLimit(modelId, options.MaxRequestCharacters);
        var chunks = SplitForRequests(narration, limit);
        if (chunks.Count > 1)
            logger.LogInformation(
                "Narration is {Chars} characters and '{Model}' accepts {Limit} per request — sending {Count} chunks.",
                narration.Length, modelId, limit, chunks.Count);

        Directory.CreateDirectory(run.AudioDir);

        using var audio = new MemoryStream();
        var charStarts = new List<double>();
        var charEnds = new List<double>();
        var haveAlignment = true;
        double offsetSeconds = 0;

        for (int i = 0; i < chunks.Count; i++)
        {
            var request = new TtsRequest
            {
                Text = chunks[i],
                ModelId = modelId,
                VoiceSettings = new VoiceSettings
                {
                    Stability = stability,
                    SimilarityBoost = similarity,
                    Speed = speed,
                },
            };

            // Ask for per-character timestamps so the video stage can sync each image to the
            // exact moment its narration is spoken (instead of estimating by word count).
            TtsTimestampsResponse resp;
            try
            {
                resp = await api.TextToSpeechWithTimestampsAsync(voiceId, request, ct);
            }
            catch (ApiException ex)
            {
                // Refit puts the reason in the response body, and without this all that reached the
                // log was a bare "400" — which is what made a plain text-length limit take an hour
                // to identify.
                throw new InvalidOperationException(
                    $"ElevenLabs rejected the request ({(int)ex.StatusCode} {ex.StatusCode}) for voice '{voiceId}' " +
                    $"using model '{modelId}'{(chunks.Count > 1 ? $" on chunk {i + 1}/{chunks.Count}" : "")}: " +
                    $"{ex.Content}", ex);
            }

            if (string.IsNullOrEmpty(resp.AudioBase64))
                throw new InvalidOperationException(
                    chunks.Count > 1
                        ? $"ElevenLabs returned no audio for chunk {i + 1}/{chunks.Count}."
                        : "ElevenLabs returned no audio.");

            var bytes = Convert.FromBase64String(resp.AudioBase64);

            // Times come back relative to each chunk, so every chunk after the first is shifted by
            // the audio already written. The offset is measured from the mp3 frames rather than the
            // last timestamp, because that is what actually determines when the next chunk plays.
            if (resp.Alignment is { CharacterStartTimesSeconds.Count: > 0 } a)
            {
                foreach (var t in a.CharacterStartTimesSeconds) charStarts.Add(t + offsetSeconds);
                foreach (var t in a.CharacterEndTimesSeconds) charEnds.Add(t + offsetSeconds);
            }
            else
            {
                // A gap in the middle would silently shift every later character, so one missing
                // chunk disqualifies the whole alignment.
                haveAlignment = false;
            }

            // Independent MP3 responses each contain ID3 metadata. In the middle of a raw
            // concatenation FFmpeg treats those tags as corrupt audio and drops a frame at
            // each join. Keep every MPEG frame (and its timing), but remove the per-file tags.
            audio.Write(chunks.Count > 1 ? Mp3ChunkJoiner.WithoutFileTags(bytes) : bytes);
            offsetSeconds += Mp3Duration.GetSeconds(bytes);
        }

        if (chunks.Count > 1)
        {
            // A stream copy rebuilds the duration/seek header for the complete narration.
            // No speech is regenerated or re-encoded, and all chunk offsets stay unchanged.
            var joinedPath = Path.Combine(run.AudioDir, $"voice-joined-{Guid.NewGuid():N}.mp3");
            var remuxPath = Path.ChangeExtension(joinedPath, ".remux.mp3");
            await File.WriteAllBytesAsync(joinedPath, audio.ToArray(), ct);
            try
            {
                await process.RunOrThrowAsync(ffmpeg.Executable,
                    ["-hide_banner", "-y", "-i", joinedPath, "-map", "0:a:0", "-c:a", "copy", remuxPath], ct: ct);
                File.Move(remuxPath, run.AudioPath, overwrite: true);
                File.Delete(joinedPath);
            }
            catch
            {
                // Retain the paid-for generation if local remuxing fails, so it can be
                // recovered without another ElevenLabs request.
                logger.LogError("Voice remux failed. Generated audio is preserved at {Path}.", joinedPath);
                throw;
            }
        }
        else
        {
            await File.WriteAllBytesAsync(run.AudioPath, audio.ToArray(), ct);
        }

        if (haveAlignment && charStarts.Count > 0)
        {
            Json.WriteFile(run.AlignmentPath, new AudioAlignment
            {
                Text = narration,
                CharStartTimes = charStarts,
                CharEndTimes = charEnds,
            });
            logger.LogInformation("Saved speech alignment ({Chars} chars) for precise A/V sync.", charStarts.Count);
        }
        else
        {
            logger.LogWarning("ElevenLabs returned no alignment — video will fall back to estimated timing.");
        }

        var seconds = Mp3Duration.GetSeconds(run.AudioPath);
        logger.LogInformation("Voice -> {Path} ({Seconds:F1}s)", run.AudioPath, seconds);
        return run.AudioPath;
    }

    /// <summary>Channel override if it has one, otherwise the global fallback.</summary>
    private static string Pick(string? channelValue, string globalValue) =>
        string.IsNullOrWhiteSpace(channelValue) ? globalValue : channelValue.Trim();

    /// <summary>
    /// Characters allowed in one request for a model. <paramref name="configured"/> wins when set.
    /// </summary>
    /// <remarks>
    /// 5,000 is the number eleven_v3 states in its own rejection: "Request text length (8910)
    /// exceeds the maximum text length of 5000 characters." eleven_v4 and eleven_v4_turbo said
    /// 10,000 the same way when asked (2026-09-29). The 10,000 for the older models is what
    /// this pipeline has actually voiced in one request (an 8,437-character narration on
    /// eleven_multilingual_v2). Anything unrecognised gets the cautious 5,000 rather than a guess —
    /// splitting a script that did not need splitting costs nothing but a seam.
    /// </remarks>
    internal static int RequestCharacterLimit(string modelId, int configured)
    {
        if (configured > 0) return configured;
        return modelId?.Trim().ToLowerInvariant() switch
        {
            "eleven_v4" or "eleven_v4_turbo"
                or "eleven_multilingual_v2" or "eleven_turbo_v2_5" or "eleven_flash_v2_5"
                or "eleven_turbo_v2" or "eleven_flash_v2" or "eleven_monolingual_v1" => 10_000,
            _ => 5_000,
        };
    }

    /// <summary>
    /// Splits narration into request-sized pieces at sentence ends, so the seam between two
    /// generations lands where a reader would pause anyway.
    /// </summary>
    /// <remarks>
    /// Concatenating the result reproduces the input exactly — the video stage maps each beat to a
    /// character offset in this same text, so losing or adding a single space would shift every
    /// image after it. Pieces are evened out rather than greedily filled: two halves of a script
    /// sound more alike than a full request followed by a short tail.
    /// </remarks>
    internal static IReadOnlyList<string> SplitForRequests(string text, int maxChars)
    {
        if (maxChars <= 0 || text.Length <= maxChars) return new[] { text };

        var parts = (int)Math.Ceiling((double)text.Length / maxChars);
        var target = (int)Math.Ceiling((double)text.Length / parts);

        var chunks = new List<string>();
        var start = 0;
        while (start < text.Length)
        {
            if (text.Length - start <= maxChars) { chunks.Add(text[start..]); break; }
            var cut = FindCut(text, start, Math.Min(target, maxChars), maxChars);
            chunks.Add(text[start..cut]);
            start = cut;
        }
        return chunks;
    }

    /// <summary>
    /// Where to cut a chunk that starts at <paramref name="start"/>: the last sentence end at or
    /// before the ideal length, falling back to the last word break, then to a hard cut.
    /// </summary>
    private static int FindCut(string text, int start, int ideal, int maxChars)
    {
        var hardEnd = Math.Min(start + maxChars, text.Length);
        var want = Math.Min(start + ideal, hardEnd);

        // Never accept a cut so early that it makes a runt chunk — better a long sentence than a
        // one-line generation whose delivery stands out against its neighbours.
        var floor = start + maxChars / 4;

        for (var i = want - 1; i > floor; i--)
            if (IsSentenceEnd(text, i)) return i + 1;

        for (var i = want - 1; i > start; i--)
            if (text[i] == ' ') return i + 1;

        return hardEnd;
    }

    /// <summary>A space that closes a sentence — the character before it ends one.</summary>
    private static bool IsSentenceEnd(string text, int i) =>
        text[i] == ' ' && i > 0 && text[i - 1] is '.' or '!' or '?' or '…';
}
