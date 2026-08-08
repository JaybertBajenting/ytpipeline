using YtPipeline.Core.Models;

namespace YtPipeline.Core.Abstractions;

/// <summary>Stage 5: upload the finished video to YouTube. Returns the published video id.</summary>
public interface IVideoUploader
{
    Task<string> UploadAsync(Run run, Channel channel, ScriptDocument script,
                             string videoPath, string privacy, CancellationToken ct);
}
