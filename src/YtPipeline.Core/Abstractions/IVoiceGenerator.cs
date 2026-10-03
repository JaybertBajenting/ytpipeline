using YtPipeline.Core.Models;

namespace YtPipeline.Core.Abstractions;

/// <summary>Stage 3: render narration to an mp3. Returns the audio file path.</summary>
public interface IVoiceGenerator
{
    /// <param name="model">
    /// Model for this one video. Null follows the channel's model, then the Settings default.
    /// </param>
    Task<string> GenerateAsync(Run run, ScriptDocument script, Channel channel, CancellationToken ct,
        string? model = null);

    /// <summary>The model <see cref="GenerateAsync"/> would use, so the run can record it.</summary>
    string ModelFor(Channel channel, string? model = null);
}
