namespace YtPipeline.Stages.Publish;

/// <summary>Shared settings for the Facebook and Instagram Graph API calls.</summary>
public sealed class MetaGraphOptions
{
    public string BaseUrl { get; set; } = "https://graph.facebook.com";

    /// <summary>
    /// Graph API version segment. Meta retires versions roughly two years after release, so this
    /// is configurable rather than baked in: if a call comes back complaining the version is
    /// unsupported, bump this instead of touching code. The publishers surface Meta's error text
    /// verbatim so that's obvious when it happens.
    /// </summary>
    public string Version { get; set; } = "v21.0";

    /// <summary>Seconds to allow a single upload/publish HTTP call.</summary>
    public int TimeoutSeconds { get; set; } = 900;

    /// <summary>
    /// How long to wait for Instagram to finish transcoding a media container before giving up.
    /// Meta processes asynchronously; publishing before it reports FINISHED fails.
    /// </summary>
    public int ContainerReadyTimeoutSeconds { get; set; } = 300;

    public string Endpoint(string path) => $"{BaseUrl.TrimEnd('/')}/{Version}/{path.TrimStart('/')}";
}
