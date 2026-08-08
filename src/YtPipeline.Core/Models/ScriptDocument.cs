namespace YtPipeline.Core.Models;

/// <summary>
/// The shared currency between stages. Stage 1 produces it; stages 2-5 consume it.
/// </summary>
public sealed class ScriptDocument
{
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public List<string> Tags { get; set; } = new();

    /// <summary>Drives image generation (one per beat) and per-beat timing.</summary>
    public List<ScriptBeat> Beats { get; set; } = new();

    /// <summary>Drives the voiceover. If empty, it is composed from the beats.</summary>
    public string FullNarration { get; set; } = "";

    /// <summary>Concatenated beat narration, used when <see cref="FullNarration"/> is blank.</summary>
    public string ComposeNarration() =>
        string.IsNullOrWhiteSpace(FullNarration)
            ? string.Join(" ", Beats.OrderBy(b => b.Index).Select(b => b.Narration.Trim()))
            : FullNarration;

    /// <summary>
    /// The exact text to send to TTS — ONLY the spoken narration from the beats, in order.
    /// Never includes titles, descriptions, image prompts, or on-screen text cues.
    /// </summary>
    public string NarrationForVoice() =>
        string.Join(" ", Beats.OrderBy(b => b.Index)
                              .Select(b => b.SpokenText.Trim())
                              .Where(n => n.Length > 0)).Trim();
}
