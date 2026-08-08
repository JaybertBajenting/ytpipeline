namespace YtPipeline.Stages.Voice;

/// <summary>
/// Per-character speech timing for the generated voiceover (saved as align.json next to the
/// audio). <see cref="CharStartTimes"/>[i] is when character i of <see cref="Text"/> begins.
/// Used to sync each image to the exact moment its narration is spoken.
/// </summary>
public sealed class AudioAlignment
{
    public string Text { get; set; } = "";
    public List<double> CharStartTimes { get; set; } = new();
    public List<double> CharEndTimes { get; set; } = new();
}
