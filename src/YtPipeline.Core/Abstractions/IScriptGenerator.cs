using YtPipeline.Core.Models;

namespace YtPipeline.Core.Abstractions;

/// <summary>Stage 1: turn a channel + topic into a validated <see cref="ScriptDocument"/>.</summary>
public interface IScriptGenerator
{
    Task<ScriptDocument> GenerateAsync(Channel channel, string topic, CancellationToken ct);
}
