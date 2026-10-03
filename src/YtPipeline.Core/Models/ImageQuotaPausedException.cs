namespace YtPipeline.Core.Models;

/// <summary>
/// Raised when the image stage stops because its provider has no quota left, instead of quietly
/// finishing the set on the other backend.
/// </summary>
/// <remarks>
/// The two backends do not draw alike — gflow renders the channel's flat cartoon style, codex
/// re-writes the style rule into its own prompt and produces detailed illustrations. Silently
/// switching mid-run once produced a video whose art changed at beat 147, so a spent quota now
/// pauses the stage and leaves the choice to a person: wait for the reset, or continue on the
/// other backend deliberately. Frames already generated are kept — the stage resumes where it
/// stopped.
/// </remarks>
public sealed class ImageQuotaPausedException(
    string provider, DateTime retryAtUtc, int produced, int expected)
    : Exception(Describe(provider, retryAtUtc, produced, expected))
{
    public string Provider { get; } = provider;
    public DateTime RetryAtUtc { get; } = retryAtUtc;
    public int Produced { get; } = produced;
    public int Expected { get; } = expected;

    private static string Describe(string provider, DateTime retryAtUtc, int produced, int expected)
    {
        var mins = Math.Max(0, (int)Math.Ceiling((retryAtUtc - DateTime.UtcNow).TotalMinutes));
        var progress = expected > 0 ? $"{produced}/{expected} frames done. " : "";
        // "In about 1122 min" is a sum to do; past an hour and a half a clock time reads better.
        var when = mins <= 90 ? $"in about {mins} min" : $"around {retryAtUtc.ToLocalTime():ddd h:mm tt}";
        return $"Image generation paused: {provider} has no quota left. {progress}" +
               $"It should be usable again {when}. " +
               "Wait and continue on the same provider, or continue on the other one deliberately — " +
               "the two do not draw alike, so mixing them inside one video is visible.";
    }
}
