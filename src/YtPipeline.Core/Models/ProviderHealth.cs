namespace YtPipeline.Core.Models;

/// <summary>
/// Remembers which image providers have hit a quota wall, so a run doesn't waste attempts on a
/// backend that is known to be out. Persisted to <c>provider-health.json</c> in the workspace
/// root — transient operational state, not configuration.
/// </summary>
public sealed class ProviderHealth
{
    /// <summary>Per-provider quota state, keyed by provider id (<c>gflow</c>, <c>codex</c>).</summary>
    public Dictionary<string, ProviderQuotaState> Providers { get; set; } = new();
}

public sealed class ProviderQuotaState
{
    /// <summary>
    /// When the provider last reported its quota exhausted. Null means healthy as far as we know.
    /// </summary>
    public DateTime? ExhaustedUtc { get; set; }

    /// <summary>The provider's own words, kept so the UI can explain the benching.</summary>
    public string LastError { get; set; } = "";

    /// <summary>How many times in a row a retry has found it still exhausted.</summary>
    public int ConsecutiveProbeFailures { get; set; }
}
