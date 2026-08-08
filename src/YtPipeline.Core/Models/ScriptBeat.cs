using Newtonsoft.Json;

namespace YtPipeline.Core.Models;

/// <summary>One narration beat: a spoken segment paired with the image drawn over it.</summary>
public sealed class ScriptBeat
{
    public int Index { get; set; }

    /// <summary>What is spoken over this beat (human-readable; shown in the UI).</summary>
    public string Narration { get; set; } = "";

    /// <summary>
    /// Optional phonetic respelling of <see cref="Narration"/> for the TTS engine — set only for
    /// beats containing words ElevenLabs would mispronounce (names, places, scientific/foreign
    /// terms). Blank means the narration is spoken as-is. <see cref="SpokenText"/> is what actually
    /// gets voiced AND what the video stage uses for char-offset alignment, so the two stay in sync.
    /// </summary>
    public string NarrationSpoken { get; set; } = "";

    /// <summary>The exact text voiced + aligned for this beat: the phonetic respelling if present, else the narration.</summary>
    [JsonIgnore]
    public string SpokenText =>
        string.IsNullOrWhiteSpace(NarrationSpoken) ? Narration : NarrationSpoken;

    /// <summary>What Codex draws for this beat (channel visual style is prepended at gen time).</summary>
    public string ImagePrompt { get; set; } = "";
}
