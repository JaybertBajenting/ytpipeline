using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace YtPipeline.Core.Models;

/// <summary>
/// The single source of truth for a run. Every stage reads it, does its work, and
/// updates it — which is what makes stages resumable and idempotent.
/// </summary>
public sealed class Manifest
{
    public string RunId { get; set; } = "";
    public string Channel { get; set; } = "";
    public string Topic { get; set; } = "";
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    public StageStatuses Stages { get; set; } = new();
    public ReviewStatus Review { get; set; } = new();

    /// <summary>Ready-to-publish metadata produced after the video stage.</summary>
    public PublishInfo Publish { get; set; } = new();

    public static Manifest FromRun(Run run) => new()
    {
        RunId = run.RunId,
        Channel = run.Channel,
        Topic = run.Topic,
        CreatedUtc = run.CreatedUtc,
    };
}

public sealed class StageStatuses
{
    public StageStatus Script { get; set; } = new();
    public StageStatus Images { get; set; } = new();
    public StageStatus Voice { get; set; } = new();
    public StageStatus Video { get; set; } = new();

    /// <summary>YouTube. Kept as <c>Upload</c> for manifest compatibility with existing runs.</summary>
    public StageStatus Upload { get; set; } = new();

    /// <summary>
    /// Extra distribution targets. Independent of <see cref="Upload"/> — a run can go to
    /// Facebook without ever touching YouTube, so these are not pipeline stages in sequence.
    /// Missing from older manifests, which simply deserialise as not-done.
    /// </summary>
    public StageStatus Facebook { get; set; } = new();
    public StageStatus Instagram { get; set; } = new();
}

/// <summary>
/// Status of one stage. Common fields are typed; stage-specific extras
/// (count, seconds, videoId, ...) ride along in <see cref="Extra"/>.
/// </summary>
public sealed class StageStatus
{
    public bool Done { get; set; }
    public string? Path { get; set; }
    public DateTime? CompletedUtc { get; set; }

    /// <summary>Stage-specific fields preserved on round-trip (e.g. count, seconds, dir, videoId).</summary>
    [JsonExtensionData]
    public IDictionary<string, JToken> Extra { get; set; } = new Dictionary<string, JToken>();

    public void Set(string key, object value) => Extra[key] = JToken.FromObject(value);

    public T? Get<T>(string key) =>
        Extra.TryGetValue(key, out var v) ? v.ToObject<T>() : default;

    /// <summary>
    /// Drops a stage-specific field. Re-running a stage that produces less than it did last time
    /// has to remove what it no longer produced — a re-assembly without music that left the old
    /// <c>musicTracks</c> in place would keep listing tracks the mp4 no longer contains.
    /// </summary>
    public void Clear(string key) => Extra.Remove(key);

    public void MarkDone(string? path = null)
    {
        Done = true;
        Path = path ?? Path;
        CompletedUtc = DateTime.UtcNow;
    }
}

public sealed class ReviewStatus
{
    public bool ScriptApproved { get; set; }
    public bool VideoApproved { get; set; }
}

/// <summary>Title, description, tags and thumbnail to hand to YouTube.</summary>
public sealed class PublishInfo
{
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public List<string> Tags { get; set; } = new();
    public bool HasThumbnail { get; set; }

    /// <summary>
    /// When this video is intended to go out, shown on the Calendar. Purely a plan — nothing
    /// publishes on a timer, so a date passing doesn't trigger anything. Null = unscheduled.
    /// </summary>
    public DateTime? ScheduledUtc { get; set; }
}
