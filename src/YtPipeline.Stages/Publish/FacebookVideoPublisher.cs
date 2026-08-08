using System.Net.Http.Headers;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using YtPipeline.Core.Abstractions;
using YtPipeline.Core.Models;
using YtPipeline.Core.Services;
using Channel = YtPipeline.Core.Models.Channel;

namespace YtPipeline.Stages.Publish;

/// <summary>
/// Publishes the rendered mp4 to a Facebook Page via <c>POST /{page-id}/videos</c>.
/// <para>
/// Facebook accepts the file itself as multipart, so — unlike Instagram — a locally rendered
/// video can be published with no hosting involved.
/// </para>
/// </summary>
/// <remarks>
/// Uses the simple (non-resumable) upload. That's fine for these renders, which are tens of MB;
/// Meta recommends its chunked resumable protocol for very large files, which isn't implemented
/// here. If a huge video ever fails mid-upload, that's the reason.
/// </remarks>
public sealed class FacebookVideoPublisher(
    IHttpClientFactory httpFactory,
    SecretsStore secrets,
    MetaGraphOptions options,
    ILogger<FacebookVideoPublisher> logger) : ISocialPublisher
{
    public string Platform => "facebook";

    public async Task<SocialPublishResult> PublishAsync(
        Run run, Channel channel, ScriptDocument script, string videoPath, CancellationToken ct)
    {
        var app = secrets.Current.Facebook;
        var target = secrets.Current.Channels.TryGetValue(channel.Id, out var t)
            ? t
            : new ChannelPublishTargets();

        // Configuration gaps are reported, not thrown: "publish everywhere" should be able to do
        // YouTube and tell you Facebook isn't set up yet.
        if (!app.Enabled)
            return SocialPublishResult.Skip(Platform, "Facebook publishing is turned off in Settings → Publishing.");
        if (string.IsNullOrWhiteSpace(target.FacebookPageId))
            return SocialPublishResult.Skip(Platform, $"No Facebook Page ID set for channel '{channel.Id}' (Channels → Publish destinations).");
        if (string.IsNullOrWhiteSpace(target.FacebookPageAccessToken))
            return SocialPublishResult.Skip(Platform, $"No Page access token stored for channel '{channel.Id}'.");
        if (!File.Exists(videoPath))
            throw new FileNotFoundException("Video not found.", videoPath);

        var bytes = new FileInfo(videoPath).Length;
        logger.LogInformation(
            "Publishing to Facebook Page {PageId} — {Mb:F1} MB from {Path}",
            target.FacebookPageId, bytes / 1024d / 1024d, videoPath);

        using var http = httpFactory.CreateClient(nameof(FacebookVideoPublisher));
        http.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);

        using var form = new MultipartFormDataContent();
        await using var file = File.OpenRead(videoPath);
        var fileContent = new StreamContent(file);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("video/mp4");

        form.Add(fileContent, "source", Path.GetFileName(videoPath));
        form.Add(new StringContent(script.Title), "title");
        form.Add(new StringContent(Caption(script, channel)), "description");
        form.Add(new StringContent(target.FacebookPageAccessToken), "access_token");

        var url = options.Endpoint($"{target.FacebookPageId}/videos");
        using var response = await http.PostAsync(url, form, ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"Facebook rejected the upload ({(int)response.StatusCode}): {Describe(body)}");

        var id = JObject.Parse(body)["id"]?.ToString();
        if (string.IsNullOrWhiteSpace(id))
            throw new InvalidOperationException($"Facebook returned no video id: {body}");

        var link = $"https://www.facebook.com/{id}";
        logger.LogInformation("Published to Facebook: {Url}", link);
        return SocialPublishResult.Ok(Platform, id, link);
    }

    /// <summary>Description plus the channel's sign-off, same text the YouTube upload uses.</summary>
    private static string Caption(ScriptDocument script, Channel channel) =>
        script.Description + channel.Youtube.DescriptionFooter;

    /// <summary>
    /// Meta wraps failures in an <c>error</c> object. Surfacing its message verbatim matters —
    /// it's what tells you a token expired, a permission is missing, or the API version is retired.
    /// </summary>
    internal static string Describe(string body)
    {
        try
        {
            var err = JObject.Parse(body)["error"];
            if (err is null) return body;
            var msg = err["message"]?.ToString();
            var type = err["type"]?.ToString();
            var code = err["code"]?.ToString();
            return $"{msg} (type={type}, code={code})";
        }
        catch
        {
            return body;   // Not JSON — an HTML error page or a proxy response.
        }
    }
}
