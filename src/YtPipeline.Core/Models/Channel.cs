using Newtonsoft.Json;

namespace YtPipeline.Core.Models;

/// <summary>
/// Stable per-channel identity loaded from <c>channels/&lt;id&gt;.json</c>.
/// This is the consistent input that keeps generation on-brand across videos.
/// </summary>
public sealed class Channel
{
    /// <summary>Channel id == the json file name without extension (e.g. "nullpointer").</summary>
    [JsonIgnore]
    public string Id { get; set; } = "";

    public string Name { get; set; } = "";
    public string Tagline { get; set; } = "";
    public string Persona { get; set; } = "";
    public string Audience { get; set; } = "";
    public string Tone { get; set; } = "";
    public int VideoLengthTargetMinutes { get; set; } = 5;
    public List<string> ScriptStructure { get; set; } = new();
    public string VisualStyle { get; set; } = "";
    public string VoiceNotes { get; set; } = "";

    /// <summary>
    /// Per-channel ElevenLabs overrides. Any field left empty/null falls back to the global
    /// value (env <c>ELEVENLABS_VOICE_ID</c> / appsettings), so channels that don't set a
    /// voice keep behaving exactly as before.
    /// </summary>
    public ChannelVoice Voice { get; set; } = new();

    /// <summary>
    /// Whether background music is mixed into this channel's videos. Defaults to true;
    /// set <c>"music": false</c> in the channel JSON to ship voice-only videos
    /// (e.g. Caveman Finance). Absent key keeps the default, so other channels are unaffected.
    /// </summary>
    public bool Music { get; set; } = true;

    public YoutubeDefaults Youtube { get; set; } = new();

    /// <summary>
    /// Full image-generation rule for Stage 2, loaded from the sibling <c>&lt;id&gt;.image.md</c>
    /// file if present. Not part of the channel JSON. Falls back to <see cref="VisualStyle"/>.
    /// </summary>
    [JsonIgnore]
    public string ImageInstructions { get; set; } = "";
}

/// <summary>
/// Per-channel voice selection. Empty string / null means "inherit the global setting" —
/// that distinction is why the numeric fields are nullable rather than defaulted.
/// </summary>
public sealed class ChannelVoice
{
    /// <summary>ElevenLabs voice id. Empty = use <c>ELEVENLABS_VOICE_ID</c>.</summary>
    public string VoiceId { get; set; } = "";

    /// <summary>Friendly label shown in the UI (ElevenLabs ids are opaque).</summary>
    public string VoiceName { get; set; } = "";

    /// <summary>ElevenLabs model id. Empty = global default (eleven_multilingual_v2).</summary>
    public string ModelId { get; set; } = "";

    /// <summary>0–1. Null = global default.</summary>
    public double? Stability { get; set; }

    /// <summary>0–1. Null = global default.</summary>
    public double? SimilarityBoost { get; set; }
}

public sealed class YoutubeDefaults
{
    public string CategoryId { get; set; } = "27";
    public List<string> DefaultTags { get; set; } = new();
    public string TitlePattern { get; set; } = "{topic}";
    public string DescriptionFooter { get; set; } = "";
}
