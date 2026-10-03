using YtPipeline.Core.Models;

namespace YtPipeline.Core.Services;

/// <summary>
/// Tracks which image providers are quota-exhausted, and when it's worth trying them again.
/// <para>
/// The reset time is deliberately NOT predicted. Google Flow's allowance refreshes on a rolling
/// window tied to first use, so any hardcoded schedule would be wrong for half the week. Instead
/// the provider is benched for a cooldown, then simply tried again — the next run discovers the
/// truth. Repeated failures back the cooldown off so a long outage doesn't mean hourly retries
/// forever. A provider that STATES its reset (Codex's 429 carries "resets_at") is benched until
/// exactly then instead — that is its own answer, not a prediction.
/// </para>
/// </summary>
public sealed class ProviderHealthStore
{
    private readonly string _path;
    private readonly Lock _gate = new();
    private ProviderHealth _current;

    /// <summary>Substrings that mean "out of quota" rather than "misconfigured" or "broken".</summary>
    private static readonly string[] QuotaMarkers =
    [
        "QUOTA_REACHED",
        "RESOURCE_EXHAUSTED",
        "has been exhausted",
        "quota exceeded",
        "insufficient_quota",
        "rate limit exceeded",
        // Codex's image tool: http 429 {"type":"usage_limit_reached", ..., "resets_at":…}
        "usage_limit_reached",
    ];

    public ProviderHealthStore(string workspaceRoot)
    {
        _path = Path.Combine(workspaceRoot, "provider-health.json");
        _current = Load();
    }

    /// <summary>
    /// True when <paramref name="text"/> is a provider complaining about quota. Deliberately
    /// narrow: a CAPTCHA or sign-in failure must NOT bench a provider, because benching would
    /// quietly hide a problem the user can actually fix.
    /// </summary>
    public static bool LooksLikeQuotaExhaustion(string? text) =>
        !string.IsNullOrWhiteSpace(text)
        && QuotaMarkers.Any(m => text.Contains(m, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Records that a provider is out of quota. Returns true only when this STARTS a new benching
    /// episode, so callers can log the transition once instead of once per failure.
    /// </summary>
    /// <remarks>
    /// A run generates one of these per failed beat — several per second across concurrent lanes.
    /// Treating each as a separate event was wrong twice over: it inflated the backoff counter to
    /// its cap within a single run, and it kept pushing <c>ExhaustedUtc</c> forward so the cooldown
    /// restarted continuously and could never expire. Reports arriving while the provider is
    /// already benched are therefore folded into the existing episode.
    /// </remarks>
    /// <param name="resetsUtc">
    /// When the provider says the limit resets, if it says. Benches until exactly then instead of
    /// guessing with the cooldown — no point retrying Codex an hour later when it named 9:00 tomorrow.
    /// </param>
    public bool MarkExhausted(string provider, string error, int baseCooldownMinutes, DateTime? resetsUtc = null)
    {
        lock (_gate)
        {
            var s = State(provider);
            var now = DateTime.UtcNow;

            if (Until(s, baseCooldownMinutes) is { } until && until > now)
            {
                // Same episode: keep the original timestamp so the cooldown actually elapses.
                s.LastError = Trim(error);
                if (resetsUtc is { } r) s.ResetsUtc = r;
                Persist();
                return false;
            }

            // Either the first discovery, or a retry after the cooldown lapsed that found it
            // still out — the latter is what the backoff ladder is counting.
            s.ConsecutiveProbeFailures = s.ExhaustedUtc is null ? 0 : s.ConsecutiveProbeFailures + 1;
            s.ExhaustedUtc = now;
            s.ResetsUtc = resetsUtc;
            s.LastError = Trim(error);
            Persist();
            return true;
        }
    }

    /// <summary>The provider's own reset time when it gave one, otherwise the cooldown's end.</summary>
    private static DateTime? Until(ProviderQuotaState s, int baseCooldownMinutes) =>
        s.ExhaustedUtc is not { } at ? null
        : s.ResetsUtc ?? at + Cooldown(baseCooldownMinutes, s.ConsecutiveProbeFailures);

    /// <summary>Forgets a provider's benching entirely — used to undo a bad state by hand.</summary>
    public void Reset(string provider)
    {
        lock (_gate)
        {
            if (_current.Providers.Remove(provider)) Persist();
        }
    }

    /// <summary>Clears the benching after a provider succeeds again.</summary>
    public void MarkHealthy(string provider)
    {
        lock (_gate)
        {
            if (!_current.Providers.TryGetValue(provider, out var s) || s.ExhaustedUtc is null) return;
            s.ExhaustedUtc = null;
            s.ResetsUtc = null;
            s.LastError = "";
            s.ConsecutiveProbeFailures = 0;
            Persist();
        }
    }

    /// <summary>
    /// How long this provider stays benched before it's worth retrying. Doubles per consecutive
    /// failed retry, capped, so a genuinely day-long outage settles into occasional checks.
    /// </summary>
    public static TimeSpan Cooldown(int baseMinutes, int consecutiveProbeFailures)
    {
        var minutes = Math.Max(5, baseMinutes) * Math.Pow(2, Math.Clamp(consecutiveProbeFailures, 0, 3));
        return TimeSpan.FromMinutes(Math.Min(minutes, 8 * 60));
    }

    /// <summary>Null when the provider is usable; otherwise when it becomes worth retrying.</summary>
    public DateTime? BenchedUntil(string provider, int baseCooldownMinutes)
    {
        lock (_gate)
        {
            if (!_current.Providers.TryGetValue(provider, out var s)) return null;
            return Until(s, baseCooldownMinutes) is { } until && until > DateTime.UtcNow ? until : null;
        }
    }

    public ProviderQuotaState Snapshot(string provider)
    {
        lock (_gate)
            return _current.Providers.TryGetValue(provider, out var s)
                ? new ProviderQuotaState
                {
                    ExhaustedUtc = s.ExhaustedUtc,
                    LastError = s.LastError,
                    ConsecutiveProbeFailures = s.ConsecutiveProbeFailures,
                    ResetsUtc = s.ResetsUtc,
                }
                : new ProviderQuotaState();
    }

    private ProviderQuotaState State(string provider)
    {
        if (!_current.Providers.TryGetValue(provider, out var s))
            _current.Providers[provider] = s = new ProviderQuotaState();
        return s;
    }

    private static string Trim(string? e) =>
        string.IsNullOrWhiteSpace(e) ? "" :
        e.ReplaceLineEndings(" ").Trim() is var f && f.Length > 400 ? f[..400] : f;

    private void Persist() => Json.WriteFile(_path, _current);

    private ProviderHealth Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                var loaded = Json.ReadFile<ProviderHealth>(_path);
                loaded.Providers ??= new Dictionary<string, ProviderQuotaState>();
                return loaded;
            }
        }
        catch
        {
            // Operational state only — a corrupt file must never stop the app starting.
        }
        return new ProviderHealth();
    }
}
