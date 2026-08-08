using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using YtPipeline.Core.Abstractions;
using YtPipeline.Core.Models;
using YtPipeline.Core.Services;
using Channel = YtPipeline.Core.Models.Channel;

namespace YtPipeline.Stages.Publish;

/// <summary>
/// Publishes the rendered mp4 as an Instagram Reel: <c>POST /{ig-user-id}/media</c> to create a
/// container, wait for Meta to finish transcoding, then <c>POST /{ig-user-id}/media_publish</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is complete but inert until hosting is wired up.</b> Instagram has no direct-upload
/// path on this flow — Meta's servers <i>fetch</i> the file from a <c>video_url</c> — so a render
/// sitting on a local drive cannot be published. The publisher therefore requires the channel's
/// <see cref="ChannelPublishTargets.PublicVideoBaseUrl"/> to be set, and additionally verifies the
/// composed URL actually serves the file before creating a container.
/// </para>
/// <para>
/// That check is the safety catch: nothing here uploads the mp4 anywhere. Until something copies
/// renders to that public location, this reports a skip explaining exactly what's missing rather
/// than handing Meta a URL that 404s.
/// </para>
/// </remarks>
public sealed class InstagramVideoPublisher(
    IHttpClientFactory httpFactory,
    SecretsStore secrets,
    MetaGraphOptions options,
    ILogger<InstagramVideoPublisher> logger) : ISocialPublisher
{
    public string Platform => "instagram";

    /// <summary>Instagram caption cap. Trimmed rather than letting Meta reject the container.</summary>
    private const int MaxCaptionLength = 2200;

    public async Task<SocialPublishResult> PublishAsync(
        Run run, Channel channel, ScriptDocument script, string videoPath, CancellationToken ct)
    {
        var app = secrets.Current.Instagram;
        var target = secrets.Current.Channels.TryGetValue(channel.Id, out var t)
            ? t
            : new ChannelPublishTargets();

        if (!app.Enabled)
            return SocialPublishResult.Skip(Platform, "Instagram publishing is turned off in Settings → Publishing.");
        if (string.IsNullOrWhiteSpace(target.InstagramUserId))
            return SocialPublishResult.Skip(Platform, $"No IG user ID set for channel '{channel.Id}' (Channels → Publish destinations).");
        if (string.IsNullOrWhiteSpace(target.InstagramAccessToken))
            return SocialPublishResult.Skip(Platform, $"No Instagram access token stored for channel '{channel.Id}'.");

        // The hosting gate. Empty base URL = hosting not wired up yet, which is the expected
        // state, so this is a skip rather than an error.
        if (string.IsNullOrWhiteSpace(target.PublicVideoBaseUrl))
            return SocialPublishResult.Skip(Platform,
                "No public video base URL for this channel. Instagram makes Meta fetch the mp4 from " +
                "a URL — it cannot read local files — so the render has to be hosted somewhere " +
                "reachable first. Set the URL on the channel once hosting is in place.");

        if (!File.Exists(videoPath)) throw new FileNotFoundException("Video not found.", videoPath);

        var videoUrl = $"{target.PublicVideoBaseUrl.TrimEnd('/')}/{run.RunId}.mp4";

        using var http = httpFactory.CreateClient(nameof(InstagramVideoPublisher));
        http.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);

        // Verify before involving Meta: a container created against an unreachable URL fails
        // several minutes later with a generic error, which is a miserable way to learn the file
        // was never published to the host.
        var reachable = await IsReachableAsync(http, videoUrl, ct);
        if (!reachable)
            return SocialPublishResult.Skip(Platform,
                $"Nothing is served at {videoUrl}. The rendered video still has to be copied to " +
                "that public location before Instagram can fetch it — nothing in the pipeline " +
                "uploads it there yet.");

        logger.LogInformation("Creating Instagram media container for {Url}", videoUrl);

        var creationId = await CreateContainerAsync(http, target, videoUrl, script, channel, ct);
        await WaitForContainerAsync(http, target, creationId, ct);

        var mediaId = await PublishContainerAsync(http, target, creationId, ct);
        var link = $"https://www.instagram.com/reel/{mediaId}";
        logger.LogInformation("Published to Instagram: {Id}", mediaId);
        return SocialPublishResult.Ok(Platform, mediaId, link);
    }

    private static async Task<bool> IsReachableAsync(HttpClient http, string url, CancellationToken ct)
    {
        try
        {
            using var head = new HttpRequestMessage(HttpMethod.Head, url);
            using var res = await http.SendAsync(head, ct);
            return res.IsSuccessStatusCode;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch
        {
            // DNS failure, refused connection, bad TLS — all mean "not reachable" for our purposes.
            return false;
        }
    }

    private async Task<string> CreateContainerAsync(
        HttpClient http, ChannelPublishTargets target, string videoUrl,
        ScriptDocument script, Channel channel, CancellationToken ct)
    {
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["media_type"] = "REELS",
            ["video_url"] = videoUrl,
            ["caption"] = Caption(script, channel),
            ["access_token"] = target.InstagramAccessToken,
        });

        var url = options.Endpoint($"{target.InstagramUserId}/media");
        using var res = await http.PostAsync(url, form, ct);
        var body = await res.Content.ReadAsStringAsync(ct);

        if (!res.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"Instagram rejected the container ({(int)res.StatusCode}): {FacebookVideoPublisher.Describe(body)}");

        return JObject.Parse(body)["id"]?.ToString()
            ?? throw new InvalidOperationException($"Instagram returned no container id: {body}");
    }

    /// <summary>
    /// Polls the container until Meta reports FINISHED. Publishing an IN_PROGRESS container fails,
    /// and transcode time scales with video length, hence the generous timeout.
    /// </summary>
    private async Task WaitForContainerAsync(
        HttpClient http, ChannelPublishTargets target, string creationId, CancellationToken ct)
    {
        var deadline = Stopwatch.StartNew();
        var limit = TimeSpan.FromSeconds(options.ContainerReadyTimeoutSeconds);
        var delay = TimeSpan.FromSeconds(5);

        while (true)
        {
            var url = options.Endpoint($"{creationId}?fields=status_code,status&access_token={Uri.EscapeDataString(target.InstagramAccessToken)}");
            using var res = await http.GetAsync(url, ct);
            var body = await res.Content.ReadAsStringAsync(ct);

            if (!res.IsSuccessStatusCode)
                throw new InvalidOperationException(
                    $"Could not read container status ({(int)res.StatusCode}): {FacebookVideoPublisher.Describe(body)}");

            var status = JObject.Parse(body)["status_code"]?.ToString();
            switch (status)
            {
                case "FINISHED":
                    logger.LogInformation("Container {Id} ready after {Sec:F0}s.", creationId, deadline.Elapsed.TotalSeconds);
                    return;
                case "ERROR":
                    throw new InvalidOperationException(
                        $"Instagram failed to process the video: {JObject.Parse(body)["status"]}");
                case "EXPIRED":
                    throw new InvalidOperationException("The media container expired before it could be published.");
            }

            if (deadline.Elapsed > limit)
                throw new TimeoutException(
                    $"Container {creationId} was still {status ?? "unknown"} after " +
                    $"{limit.TotalSeconds:F0}s. Meta is still transcoding — raise " +
                    $"{nameof(MetaGraphOptions.ContainerReadyTimeoutSeconds)} or try again.");

            logger.LogInformation("Container {Id} is {Status} — waiting…", creationId, status);
            await Task.Delay(delay, ct);
        }
    }

    private async Task<string> PublishContainerAsync(
        HttpClient http, ChannelPublishTargets target, string creationId, CancellationToken ct)
    {
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["creation_id"] = creationId,
            ["access_token"] = target.InstagramAccessToken,
        });

        var url = options.Endpoint($"{target.InstagramUserId}/media_publish");
        using var res = await http.PostAsync(url, form, ct);
        var body = await res.Content.ReadAsStringAsync(ct);

        if (!res.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"Instagram refused to publish ({(int)res.StatusCode}): {FacebookVideoPublisher.Describe(body)}");

        return JObject.Parse(body)["id"]?.ToString()
            ?? throw new InvalidOperationException($"Instagram published but returned no media id: {body}");
    }

    private static string Caption(ScriptDocument script, Channel channel)
    {
        var text = script.Description + channel.Youtube.DescriptionFooter;
        return text.Length <= MaxCaptionLength ? text : text[..MaxCaptionLength];
    }
}
