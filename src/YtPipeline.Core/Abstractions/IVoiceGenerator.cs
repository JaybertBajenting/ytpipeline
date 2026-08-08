using YtPipeline.Core.Models;

namespace YtPipeline.Core.Abstractions;

/// <summary>Stage 3: render narration to an mp3. Returns the audio file path.</summary>
public interface IVoiceGenerator
{
    Task<string> GenerateAsync(Run run, ScriptDocument script, Channel channel, CancellationToken ct);
}
