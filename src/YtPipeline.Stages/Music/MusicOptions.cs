namespace YtPipeline.Stages.Music;

public sealed class MusicOptions
{
    /// <summary>When false the music stage is skipped entirely. No Claude call, no music in video.</summary>
    public bool Enabled { get; set; } = false;

    /// <summary>Folder containing MP3/WAV tracks to pick from. Each run Claude selects the best fit.</summary>
    public string LibraryPath { get; set; } = "";

    /// <summary>Background music volume relative to full (0.0–1.0). 0.1 = soft bed under voice.</summary>
    public double Volume { get; set; } = 0.1;

    /// <summary>Seconds before the video ends to begin the music fade-out. Default 3 s.</summary>
    public double FadeOutSeconds { get; set; } = 3.0;
}
