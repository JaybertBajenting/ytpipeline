using Microsoft.Extensions.Logging;
using YtPipeline.Core.Abstractions;
using YtPipeline.Core.Models;
using YtPipeline.Core.Services;

namespace YtPipeline.Stages.Voice;

public sealed class ElevenLabsOptions
{
    public string VoiceId { get; set; } = "";
    public string ModelId { get; set; } = "eleven_multilingual_v2";
    public double Stability { get; set; } = 0.5;
    public double SimilarityBoost { get; set; } = 0.75;
}

/// <summary>Stage 3. POSTs the full narration to ElevenLabs and saves the mp3.</summary>
public sealed class ElevenLabsVoiceGenerator(
    IElevenLabsApi api,
    ElevenLabsOptions options,
    ILogger<ElevenLabsVoiceGenerator> logger) : IVoiceGenerator
{
    public async Task<string> GenerateAsync(Run run, ScriptDocument script, Channel channel, CancellationToken ct)
    {
        // Per-channel voice wins; anything the channel leaves blank falls back to the global
        // option, so a channel with no "voice" block behaves exactly as it did before.
        var cv = channel.Voice ?? new ChannelVoice();
        var voiceId = Pick(cv.VoiceId, options.VoiceId);
        var modelId = Pick(cv.ModelId, options.ModelId);
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

        var request = new TtsRequest
        {
            Text = narration,
            ModelId = modelId,
            VoiceSettings = new VoiceSettings
            {
                Stability = stability,
                SimilarityBoost = similarity,
                Speed = speed,
            },
        };

        Directory.CreateDirectory(run.AudioDir);

        // Ask for per-character timestamps so the video stage can sync each image to the
        // exact moment its narration is spoken (instead of estimating by word count).
        var resp = await api.TextToSpeechWithTimestampsAsync(voiceId, request, ct);
        if (string.IsNullOrEmpty(resp.AudioBase64))
            throw new InvalidOperationException("ElevenLabs returned no audio.");

        await File.WriteAllBytesAsync(run.AudioPath, Convert.FromBase64String(resp.AudioBase64), ct);

        if (resp.Alignment is { CharacterStartTimesSeconds.Count: > 0 } a)
        {
            Json.WriteFile(run.AlignmentPath, new AudioAlignment
            {
                Text = narration,
                CharStartTimes = a.CharacterStartTimesSeconds,
                CharEndTimes = a.CharacterEndTimesSeconds,
            });
            logger.LogInformation("Saved speech alignment ({Chars} chars) for precise A/V sync.", a.CharacterStartTimesSeconds.Count);
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
}
