using Google.Apis.Auth.OAuth2;
using Google.Apis.Services;
using Google.Apis.Upload;
using Google.Apis.Util.Store;
using Google.Apis.YouTube.v3;
using Google.Apis.YouTube.v3.Data;
using Microsoft.Extensions.Logging;
using YtPipeline.Core.Abstractions;
using YtPipeline.Core.Models;
using Channel = YtPipeline.Core.Models.Channel;

namespace YtPipeline.Stages.Upload;

public sealed class YouTubeOptions
{
    /// <summary>Path to the Google OAuth desktop-client secrets json.</summary>
    public string ClientSecretsPath { get; set; } = "client_secret.json";

    /// <summary>Where per-channel refresh tokens are cached.</summary>
    public string TokenStoreDir { get; set; } = ".youtube-tokens";
}

/// <summary>
/// Stage 5. Resumable upload to YouTube Data API v3. One-time OAuth consent per channel;
/// the refresh token is cached under <see cref="YouTubeOptions.TokenStoreDir"/> keyed by channel id.
/// </summary>
public sealed class YouTubeUploader(
    YouTubeOptions options,
    ILogger<YouTubeUploader> logger) : IVideoUploader
{
    public async Task<string> UploadAsync(
        Run run, Channel channel, ScriptDocument script, string videoPath, string privacy, CancellationToken ct)
    {
        if (!File.Exists(videoPath)) throw new FileNotFoundException("Video not found.", videoPath);
        if (!File.Exists(options.ClientSecretsPath))
            throw new FileNotFoundException(
                $"YouTube client secrets not found at {options.ClientSecretsPath}. " +
                "Download a Desktop OAuth client from Google Cloud Console.");

        using var service = await BuildServiceAsync(channel, ct);

        var video = new Google.Apis.YouTube.v3.Data.Video
        {
            Snippet = new VideoSnippet
            {
                Title = script.Title,
                Description = script.Description + channel.Youtube.DescriptionFooter,
                Tags = script.Tags.Concat(channel.Youtube.DefaultTags).Distinct().ToList(),
                CategoryId = channel.Youtube.CategoryId,
            },
            Status = new VideoStatus { PrivacyStatus = privacy },
        };

        await using var fileStream = File.OpenRead(videoPath);
        var insert = service.Videos.Insert(video, "snippet,status", fileStream, "video/*");

        insert.ProgressChanged += p =>
        {
            if (p.Status == UploadStatus.Uploading)
                logger.LogInformation("Uploading… {Mb:F1} MB sent", p.BytesSent / 1024d / 1024d);
            else if (p.Status == UploadStatus.Failed)
                logger.LogError(p.Exception, "Upload failed.");
        };

        var progress = await insert.UploadAsync(ct);
        if (progress.Status != UploadStatus.Completed)
            throw new InvalidOperationException("Upload did not complete.", progress.Exception);

        var videoId = insert.ResponseBody?.Id
            ?? throw new InvalidOperationException("Upload completed but no video id was returned.");

        logger.LogInformation("Uploaded {VideoId} (privacy={Privacy}) https://youtu.be/{VideoId}", videoId, privacy, videoId);
        return videoId;
    }

    private async Task<YouTubeService> BuildServiceAsync(Channel channel, CancellationToken ct)
    {
        await using var secrets = File.OpenRead(options.ClientSecretsPath);
        var credential = await GoogleWebAuthorizationBroker.AuthorizeAsync(
            GoogleClientSecrets.FromStream(secrets).Secrets,
            new[] { YouTubeService.Scope.YoutubeUpload },
            user: channel.Id,
            ct,
            new FileDataStore(options.TokenStoreDir, fullPath: true));

        return new YouTubeService(new BaseClientService.Initializer
        {
            HttpClientInitializer = credential,
            ApplicationName = "YtPipeline",
        });
    }
}
