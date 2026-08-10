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
    /// <summary>
    /// Legacy single-value length. Superseded by <see cref="VideoLengthMinMinutes"/> /
    /// <see cref="VideoLengthMaxMinutes"/>; kept so older channel files still load, and
    /// re-synced to the range's lower bound on save.
    /// </summary>
    public int VideoLengthTargetMinutes { get; set; } = 5;

    /// <summary>Shortest acceptable runtime, in minutes.</summary>
    public int VideoLengthMinMinutes { get; set; }

    /// <summary>
    /// Longest acceptable runtime, in minutes. Equal to the minimum means "about this long";
    /// a spread (e.g. 3-5) gives the script room without letting the image count run away.
    /// </summary>
    public int VideoLengthMaxMinutes { get; set; }
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

    /// <summary>
    /// Where <see cref="ImageInstructions"/> came from: <c>channel</c> (its own
    /// <c>&lt;id&gt;.image.md</c>), <c>default</c> (the shared <c>_default.image.md</c>), or
    /// <c>none</c>. Needed because a non-empty rule does NOT mean the channel has its own —
    /// conflating the two made the UI claim per-channel files that don't exist.
    /// </summary>
    [JsonIgnore]
    public string ImageRuleSource { get; set; } = "none";
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

    /// <summary>
    /// Measured speaking rate for THIS voice, in words per minute, learned from finished runs.
    /// <para>
    /// Speaking rate is a property of the voice, not a constant: two real voices on this project
    /// came in at 112 and 215 wpm — nearly 2x apart. A fixed assumption therefore cannot size a
    /// script for both, and using one produced a 2-minute video from a 3-minute target. The voice
    /// stage writes the observed rate back here so the next script is budgeted from evidence.
    /// Null until the first run with this voice completes.
    /// </para>
    /// </summary>
    public double? WordsPerMinute { get; set; }
}

public sealed class YoutubeDefaults
{
    public string CategoryId { get; set; } = "27";
    public List<string> DefaultTags { get; set; } = new();
    public string TitlePattern { get; set; } = "{topic}";
    public string DescriptionFooter { get; set; } = "";
}
