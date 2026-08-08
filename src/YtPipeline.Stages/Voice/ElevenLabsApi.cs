using System.Text.Json.Serialization;
using Refit;

namespace YtPipeline.Stages.Voice;

/// <summary>Minimal Refit surface for the ElevenLabs text-to-speech endpoint.</summary>
public interface IElevenLabsApi
{
    /// <summary>
    /// Returns the mp3 (base64) plus per-character timestamps of the generated speech, so we
    /// can anchor each image to exactly when its narration is spoken. The <c>xi-api-key</c>
    /// header is set on the HttpClient.
    /// </summary>
    [Post("/v1/text-to-speech/{voiceId}/with-timestamps")]
    Task<TtsTimestampsResponse> TextToSpeechWithTimestampsAsync(string voiceId, [Body] TtsRequest body, CancellationToken ct);
}

public sealed class TtsRequest
{
    [JsonPropertyName("text")] public string Text { get; set; } = "";
    [JsonPropertyName("model_id")] public string ModelId { get; set; } = "eleven_multilingual_v2";
    [JsonPropertyName("voice_settings")] public VoiceSettings VoiceSettings { get; set; } = new();
}

public sealed class VoiceSettings
{
    [JsonPropertyName("stability")] public double Stability { get; set; } = 0.5;
    [JsonPropertyName("similarity_boost")] public double SimilarityBoost { get; set; } = 0.75;
}

public sealed class TtsTimestampsResponse
{
    [JsonPropertyName("audio_base64")] public string AudioBase64 { get; set; } = "";
    [JsonPropertyName("alignment")] public TtsAlignment? Alignment { get; set; }
    [JsonPropertyName("normalized_alignment")] public TtsAlignment? NormalizedAlignment { get; set; }
}

public sealed class TtsAlignment
{
    [JsonPropertyName("characters")] public List<string> Characters { get; set; } = new();
    [JsonPropertyName("character_start_times_seconds")] public List<double> CharacterStartTimesSeconds { get; set; } = new();
    [JsonPropertyName("character_end_times_seconds")] public List<double> CharacterEndTimesSeconds { get; set; } = new();
}
