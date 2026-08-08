using System.Net.NetworkInformation;
using Microsoft.Extensions.Logging;

namespace YtPipeline.Stages.Process;

/// <summary>
/// Connectivity helper for surviving transient internet loss. Stages that hit the network
/// (Claude, Codex, ElevenLabs, YouTube) use this to PAUSE — rather than fail — when the
/// connection drops, then continue automatically once it returns.
/// </summary>
public static class NetworkGuard
{
    /// <summary>Quick reachability probe. True if we can reach a public host.</summary>
    public static bool IsOnline()
    {
        // Cheap local check first.
        if (!NetworkInterface.GetIsNetworkAvailable()) return false;
        // Confirm real reachability (the adapter can be "up" with no internet).
        foreach (var host in new[] { "1.1.1.1", "8.8.8.8" })
        {
            try
            {
                using var ping = new Ping();
                if (ping.Send(host, 2000).Status == IPStatus.Success) return true;
            }
            catch { /* try next host */ }
        }
        return false;
    }

    /// <summary>
    /// Returns immediately if online; otherwise logs once and polls until connectivity comes
    /// back (or cancellation). This is what turns a "lost internet" failure into a pause/resume.
    /// </summary>
    public static async Task WaitForConnectivityAsync(ILogger logger, CancellationToken ct, int pollSeconds = 5)
    {
        if (IsOnline()) return;

        logger.LogWarning("⏸  Network appears down — pausing and waiting for the connection to come back…");
        var waited = 0;
        while (!IsOnline())
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(TimeSpan.FromSeconds(pollSeconds), ct);
            waited += pollSeconds;
            if (waited % 60 == 0)
                logger.LogWarning("   still offline after {Seconds}s — still waiting…", waited);
        }
        logger.LogInformation("▶  Network is back — resuming.");
    }
}
