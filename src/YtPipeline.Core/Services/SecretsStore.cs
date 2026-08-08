using YtPipeline.Core.Models;

namespace YtPipeline.Core.Services;

/// <summary>
/// Loads and saves <see cref="PublishingSecrets"/> (<c>secrets.json</c> in the workspace root).
/// Mirrors <see cref="SettingsStore"/>, with two differences that matter for credentials:
/// <list type="bullet">
///   <item>secret values are never handed back out — the <c>Redacted*</c> projections report only
///   whether each one is set;</item>
///   <item>a <c>null</c> secret on save KEEPS the stored value while <c>""</c> clears it, so the
///   UI can show an empty password box without wiping a working token, and can still remove one
///   deliberately.</item>
/// </list>
/// </summary>
public sealed class SecretsStore
{
    private readonly string _path;
    private readonly Lock _gate = new();
    private PublishingSecrets _current;

    public SecretsStore(string workspaceRoot)
    {
        _path = Path.Combine(workspaceRoot, "secrets.json");
        _current = Load();
    }

    /// <summary>
    /// The live secrets, including plaintext values. For pipeline stages that need to
    /// authenticate — never serialise this to an HTTP response.
    /// </summary>
    public PublishingSecrets Current
    {
        get { lock (_gate) return _current; }
    }

    // ── App-level credentials ────────────────────────────────────────────────

    public PublishingSecrets Save(PublishingSecretsUpdate incoming)
    {
        incoming ??= new PublishingSecretsUpdate();
        lock (_gate)
        {
            var yt = incoming.Youtube ?? new PublishingSecretsUpdate.YoutubeUpdate();
            var fb = incoming.Facebook ?? new PublishingSecretsUpdate.MetaUpdate();
            var ig = incoming.Instagram ?? new PublishingSecretsUpdate.MetaUpdate();

            _current.Youtube = new YoutubeApp
            {
                Enabled = yt.Enabled,
                ClientId = yt.ClientId?.Trim() ?? "",
                DefaultPrivacy = NormalizePrivacy(yt.DefaultPrivacy),
                ClientSecret = MergeSecret(yt.ClientSecret, _current.Youtube.ClientSecret),
            };
            _current.Facebook = MergeMeta(fb, _current.Facebook);
            _current.Instagram = MergeMeta(ig, _current.Instagram);

            Persist();
            return _current;
        }
    }

    /// <summary>Ids and flags in the clear; every real secret reduced to "is it set?".</summary>
    public object Redacted()
    {
        var s = Current;
        return new
        {
            youtube = new
            {
                enabled = s.Youtube.Enabled,
                clientId = s.Youtube.ClientId,
                defaultPrivacy = s.Youtube.DefaultPrivacy,
                clientSecretSaved = Has(s.Youtube.ClientSecret),
            },
            facebook = RedactMeta(s.Facebook),
            instagram = RedactMeta(s.Instagram),
        };
    }

    // ── Per-channel targets ─────────────────────────────────────────────────

    public ChannelPublishTargets SaveChannel(string channelId, ChannelPublishUpdate incoming)
    {
        incoming ??= new ChannelPublishUpdate();
        lock (_gate)
        {
            var old = _current.For(channelId);
            _current.Channels[channelId] = new ChannelPublishTargets
            {
                YoutubeChannelId = incoming.YoutubeChannelId?.Trim() ?? "",
                FacebookPageId = incoming.FacebookPageId?.Trim() ?? "",
                InstagramUserId = incoming.InstagramUserId?.Trim() ?? "",
                PublicVideoBaseUrl = incoming.PublicVideoBaseUrl?.Trim().TrimEnd('/') ?? "",
                FacebookPageAccessToken =
                    MergeSecret(incoming.FacebookPageAccessToken, old.FacebookPageAccessToken),
                InstagramAccessToken =
                    MergeSecret(incoming.InstagramAccessToken, old.InstagramAccessToken),
            };
            Persist();
            return _current.Channels[channelId];
        }
    }

    /// <summary>Safe projection of one channel's targets.</summary>
    public object RedactedChannel(string channelId)
    {
        var t = Current.Channels.TryGetValue(channelId, out var found)
            ? found
            : new ChannelPublishTargets();
        return new
        {
            youtubeChannelId = t.YoutubeChannelId,
            facebookPageId = t.FacebookPageId,
            instagramUserId = t.InstagramUserId,
            publicVideoBaseUrl = t.PublicVideoBaseUrl,
            facebookPageAccessTokenSaved = Has(t.FacebookPageAccessToken),
            instagramAccessTokenSaved = Has(t.InstagramAccessToken),
        };
    }

    /// <summary>Drops a channel's targets — called when the channel itself is deleted.</summary>
    public bool ForgetChannel(string channelId)
    {
        lock (_gate)
        {
            if (!_current.Channels.Remove(channelId)) return false;
            Persist();
            return true;
        }
    }

    // ── Internals ───────────────────────────────────────────────────────────

    private void Persist() => Json.WriteFile(_path, _current);   // BOM-less UTF-8

    private static bool Has(string? v) => !string.IsNullOrWhiteSpace(v);

    /// <summary>
    /// null = keep what's stored (untouched form field); "" = clear it (explicit Clear);
    /// anything else = replace. Whitespace-only counts as clearing, since a token made of
    /// spaces is never what someone meant.
    /// </summary>
    private static string MergeSecret(string? incoming, string existing) => incoming switch
    {
        null => existing,
        _ when string.IsNullOrWhiteSpace(incoming) => "",
        _ => incoming.Trim(),
    };

    private static MetaApp MergeMeta(PublishingSecretsUpdate.MetaUpdate inc, MetaApp old) => new()
    {
        Enabled = inc.Enabled,
        AppId = inc.AppId?.Trim() ?? "",
        AppSecret = MergeSecret(inc.AppSecret, old.AppSecret),
    };

    private static object RedactMeta(MetaApp m) => new
    {
        enabled = m.Enabled,
        appId = m.AppId,
        appSecretSaved = Has(m.AppSecret),
    };

    private static string NormalizePrivacy(string? v) => v?.Trim().ToLowerInvariant() switch
    {
        "public" => "public",
        "unlisted" => "unlisted",
        _ => "private",
    };

    private PublishingSecrets Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                var loaded = Json.ReadFile<PublishingSecrets>(_path);
                loaded.Channels ??= new Dictionary<string, ChannelPublishTargets>();
                return loaded;
            }
        }
        catch
        {
            // A corrupt secrets file must never stop the app starting — the Settings screen is
            // the only way to fix it, so the app has to come up first.
        }
        return new PublishingSecrets();
    }
}
