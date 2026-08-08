using YtPipeline.Core.Models;

namespace YtPipeline.Core.Abstractions;

/// <summary>
/// Publishes a finished video to one social platform. Separate from
/// <see cref="IVideoUploader"/> (YouTube) because these platforms don't share an auth model,
/// an upload mechanism, or a notion of "privacy" — trying to force them behind one interface
/// produced more special-casing than it removed.
/// </summary>
public interface ISocialPublisher
{
    /// <summary>Stage key used in the manifest and the API route: <c>facebook</c>, <c>instagram</c>.</summary>
    string Platform { get; }

    /// <summary>
    /// Publishes <paramref name="videoPath"/>. Returns a skipped result — rather than throwing —
    /// when the platform simply isn't configured, so an "publish everywhere" action can report
    /// what it couldn't do without failing the whole run.
    /// </summary>
    Task<SocialPublishResult> PublishAsync(
        Run run, Channel channel, ScriptDocument script, string videoPath, CancellationToken ct);
}

/// <summary>
/// Outcome of one publish attempt. <see cref="Skipped"/> means "not configured / not wired up",
/// which is a normal state here — a thrown exception means the platform was asked to publish and
/// refused.
/// </summary>
public sealed record SocialPublishResult(
    string Platform,
    bool Published,
    string? PostId = null,
    string? Url = null,
    string? SkipReason = null)
{
    public bool Skipped => !Published && SkipReason is not null;

    public static SocialPublishResult Skip(string platform, string reason) =>
        new(platform, Published: false, SkipReason: reason);

    public static SocialPublishResult Ok(string platform, string postId, string? url = null) =>
        new(platform, Published: true, PostId: postId, Url: url);
}
