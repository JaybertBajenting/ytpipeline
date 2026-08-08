namespace YtPipeline.Core.Models;

/// <summary>
/// Credentials for the platforms a finished video can be published to. Persisted to
/// <c>secrets.json</c> in the workspace root (gitignored, same as <c>.env</c>) rather than to
/// <c>settings.json</c>, so ordinary settings stay safe to share.
/// </summary>
/// <remarks>
/// <para>
/// Split into two layers, because the platforms are: <b>app-level</b> credentials identify the
/// developer app and are shared by every channel — one Google Cloud OAuth client, one Meta app.
/// <b>Per-channel targets</b> identify where a specific video goes, and each carries its own
/// token: a YouTube refresh token is bound to one channel, and a Facebook Page token is bound
/// to one Page.
/// </para>
/// <para>
/// Per-channel tokens live here rather than in <c>channels/&lt;id&gt;.json</c> precisely because
/// that folder is NOT gitignored — a Page token in a channel file would be a committed secret.
/// </para>
/// </remarks>
public sealed class PublishingSecrets
{
    public YoutubeApp Youtube { get; set; } = new();
    public MetaApp Facebook { get; set; } = new();
    public MetaApp Instagram { get; set; } = new();

    /// <summary>Per-channel destinations, keyed by channel id.</summary>
    public Dictionary<string, ChannelPublishTargets> Channels { get; set; } = new();

    /// <summary>Targets for one channel, creating an empty entry on first access.</summary>
    public ChannelPublishTargets For(string channelId)
    {
        if (!Channels.TryGetValue(channelId, out var t))
            Channels[channelId] = t = new ChannelPublishTargets();
        return t;
    }
}

/// <summary>
/// The Google Cloud OAuth client. One client serves every channel on the account — but note the
/// API quota is per <em>Cloud project</em>, so all channels sharing this client share the
/// ~10,000 units/day (roughly 6 uploads).
/// </summary>
public sealed class YoutubeApp
{
    public bool Enabled { get; set; }
    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";

    /// <summary><c>private</c>, <c>unlisted</c> or <c>public</c>.</summary>
    public string DefaultPrivacy { get; set; } = "private";
}

/// <summary>A Meta developer app. Facebook and Instagram normally share one.</summary>
public sealed class MetaApp
{
    public bool Enabled { get; set; }
    public string AppId { get; set; } = "";
    public string AppSecret { get; set; } = "";
}

/// <summary>Where one channel's videos get published, and the per-destination tokens.</summary>
public sealed class ChannelPublishTargets
{
    /// <summary>Target YouTube channel (<c>UC…</c>). Which channel the OAuth consent picked.</summary>
    public string YoutubeChannelId { get; set; } = "";

    /// <summary>Numeric id of the Facebook Page videos are posted to.</summary>
    public string FacebookPageId { get; set; } = "";

    /// <summary>Long-lived <em>Page</em> access token — a user token will not work.</summary>
    public string FacebookPageAccessToken { get; set; } = "";

    /// <summary>The IG Business account id (<c>ig-user-id</c>), not the @handle.</summary>
    public string InstagramUserId { get; set; } = "";

    public string InstagramAccessToken { get; set; } = "";

    /// <summary>
    /// Public base URL Meta can fetch this channel's rendered mp4 from. Instagram's container
    /// flow takes a <c>video_url</c> and pulls the file itself, so a local path cannot be
    /// published — the render has to be hosted somewhere reachable first.
    /// </summary>
    public string PublicVideoBaseUrl { get; set; } = "";
}

/// <summary>
/// PUT body for the app-level credentials. Secret fields are <b>nullable on purpose</b> — that's
/// what lets one payload express all three intents:
/// <list type="bullet">
///   <item><c>null</c> (or omitted) — leave the stored value alone. What an untouched form sends,
///   so saving an unrelated change never forces you to re-paste a token.</item>
///   <item><c>""</c> — explicitly clear the stored value.</item>
///   <item>anything else — replace it.</item>
/// </list>
/// Collapsing null and "" into one meaning would make a stored credential unremovable.
/// </summary>
public sealed class PublishingSecretsUpdate
{
    public YoutubeUpdate Youtube { get; set; } = new();
    public MetaUpdate Facebook { get; set; } = new();
    public MetaUpdate Instagram { get; set; } = new();

    public sealed class YoutubeUpdate
    {
        public bool Enabled { get; set; }
        public string ClientId { get; set; } = "";
        public string DefaultPrivacy { get; set; } = "private";
        public string? ClientSecret { get; set; }
    }

    public sealed class MetaUpdate
    {
        public bool Enabled { get; set; }
        public string AppId { get; set; } = "";
        public string? AppSecret { get; set; }
    }
}

/// <summary>PUT body for one channel's targets. Same null / "" / value rules for the tokens.</summary>
public sealed class ChannelPublishUpdate
{
    public string YoutubeChannelId { get; set; } = "";
    public string FacebookPageId { get; set; } = "";
    public string InstagramUserId { get; set; } = "";
    public string PublicVideoBaseUrl { get; set; } = "";
    public string? FacebookPageAccessToken { get; set; }
    public string? InstagramAccessToken { get; set; }
}
