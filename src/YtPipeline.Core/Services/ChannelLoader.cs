using YtPipeline.Core.Models;

namespace YtPipeline.Core.Services;

/// <summary>Loads channel context files from the <c>channels/</c> directory.</summary>
public sealed class ChannelLoader
{
    private readonly string _channelsDir;

    public ChannelLoader(string channelsDir) => _channelsDir = channelsDir;

    public Channel Load(string channelId)
    {
        var path = Path.Combine(_channelsDir, channelId + ".json");
        if (!File.Exists(path))
            throw new FileNotFoundException(
                $"Channel '{channelId}' not found. Expected: {path}. " +
                $"Known channels: {string.Join(", ", List().DefaultIfEmpty("(none)"))}");

        var channel = Json.ReadFile<Channel>(path);
        channel.Id = channelId;
        MigrateLength(channel);

        // Image rule: per-channel "<id>.image.md" if present, else the shared "_default.image.md".
        var rulePath = Path.Combine(_channelsDir, channelId + ".image.md");
        var defaultPath = Path.Combine(_channelsDir, "_default.image.md");
        if (File.Exists(rulePath))
        {
            channel.ImageInstructions = File.ReadAllText(rulePath);
            channel.ImageRuleSource = "channel";
        }
        else if (File.Exists(defaultPath))
        {
            channel.ImageInstructions = File.ReadAllText(defaultPath);
            channel.ImageRuleSource = "default";
        }

        // Thumbnail rule: per-channel override, else the shared default.
        var thumbRule = Path.Combine(_channelsDir, channelId + ".thumbnail.md");
        var thumbDefault = Path.Combine(_channelsDir, "_default.thumbnail.md");
        if (File.Exists(thumbRule)) channel.ThumbnailInstructions = File.ReadAllText(thumbRule);
        else if (File.Exists(thumbDefault)) channel.ThumbnailInstructions = File.ReadAllText(thumbDefault);

        return channel;
    }

    /// <summary>
    /// Writes a channel back to <c>channels/&lt;id&gt;.json</c>. <see cref="Channel.Id"/> and
    /// <see cref="Channel.ImageInstructions"/> are <c>[JsonIgnore]</c>d, so the file keeps its
    /// original shape and the sibling <c>.image.md</c> rule file is left untouched.
    /// </summary>
    public Channel Save(string channelId, Channel channel)
    {
        var path = Path.Combine(_channelsDir, channelId + ".json");
        if (!File.Exists(path))
            throw new FileNotFoundException($"Channel '{channelId}' not found. Expected: {path}");

        Normalize(channel);
        Json.WriteFile(path, channel);   // BOM-less UTF-8
        return Load(channelId);          // re-read so image rules come back attached
    }

    /// <summary>
    /// Creates <c>channels/&lt;id&gt;.json</c>. The id becomes a filename, so it's restricted to
    /// lowercase letters, digits and dashes — anything else is a path-traversal or
    /// case-collision waiting to happen.
    /// </summary>
    public Channel Create(string channelId, Channel channel)
    {
        var id = (channelId ?? "").Trim().ToLowerInvariant();
        if (!IsValidId(id))
            throw new ArgumentException(
                "Channel id must be 2-40 characters of lowercase letters, digits or dashes " +
                $"(got '{channelId}').");

        Directory.CreateDirectory(_channelsDir);
        var path = Path.Combine(_channelsDir, id + ".json");
        if (File.Exists(path))
            throw new InvalidOperationException($"Channel '{id}' already exists.");

        Normalize(channel);
        Json.WriteFile(path, channel);   // BOM-less UTF-8
        return Load(id);
    }

    /// <summary>
    /// Deletes <c>channels/&lt;id&gt;.json</c>. Deliberately leaves the sibling
    /// <c>&lt;id&gt;.image.md</c> and any <c>videos/&lt;id&gt;/</c> runs alone — those are
    /// hand-written rules and finished work, and losing them to a channel delete would be a
    /// nasty surprise. Returns what was left behind so the caller can say so.
    /// </summary>
    public (bool imageRuleKept, string imageRulePath) Delete(string channelId)
    {
        var id = (channelId ?? "").Trim().ToLowerInvariant();
        if (!IsValidId(id)) throw new ArgumentException($"Invalid channel id '{channelId}'.");

        var path = Path.Combine(_channelsDir, id + ".json");
        if (!File.Exists(path))
            throw new FileNotFoundException($"Channel '{id}' not found. Expected: {path}");

        File.Delete(path);

        var rulePath = Path.Combine(_channelsDir, id + ".image.md");
        return (File.Exists(rulePath), rulePath);
    }

    /// <summary>Filename-safe channel id: lowercase alphanumeric plus dashes.</summary>
    public static bool IsValidId(string? id) =>
        !string.IsNullOrWhiteSpace(id)
        && id.Length is >= 2 and <= 40
        && id.All(c => (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '-')
        && id[0] != '-' && id[^1] != '-';

    /// <summary>
    /// Fills the min/max range from the legacy single value when a channel file predates it,
    /// so an untouched older channel keeps behaving exactly as it did.
    /// </summary>
    private static void MigrateLength(Channel c)
    {
        if (c.VideoLengthMinMinutes <= 0 && c.VideoLengthMaxMinutes <= 0)
        {
            var legacy = c.VideoLengthTargetMinutes > 0 ? c.VideoLengthTargetMinutes : 5;
            c.VideoLengthMinMinutes = legacy;
            c.VideoLengthMaxMinutes = legacy;
        }
        // Only one side present — treat it as both ends of a zero-width range.
        else if (c.VideoLengthMinMinutes <= 0) c.VideoLengthMinMinutes = c.VideoLengthMaxMinutes;
        else if (c.VideoLengthMaxMinutes <= 0) c.VideoLengthMaxMinutes = c.VideoLengthMinMinutes;
    }

    /// <summary>Clamps values a bad edit could otherwise push somewhere expensive or broken.</summary>
    private static void Normalize(Channel c)
    {
        MigrateLength(c);

        // 1-30 min: a 0 would ask for a zero-word script, and a huge value would queue
        // thousands of image generations.
        c.VideoLengthMinMinutes = Math.Clamp(c.VideoLengthMinMinutes, 1, 30);
        c.VideoLengthMaxMinutes = Math.Clamp(c.VideoLengthMaxMinutes, 1, 30);

        // An inverted range would produce a word budget with max < min, which the prompt can't
        // satisfy. Widening the max is the harmless direction to fix it.
        if (c.VideoLengthMaxMinutes < c.VideoLengthMinMinutes)
            c.VideoLengthMaxMinutes = c.VideoLengthMinMinutes;

        // Keep the legacy field meaningful for anything still reading it.
        c.VideoLengthTargetMinutes = c.VideoLengthMinMinutes;

        c.Voice ??= new ChannelVoice();
        if (c.Voice.Stability is { } st) c.Voice.Stability = Math.Clamp(st, 0.0, 1.0);
        if (c.Voice.SimilarityBoost is { } sb) c.Voice.SimilarityBoost = Math.Clamp(sb, 0.0, 1.0);

        c.Youtube ??= new YoutubeDefaults();
        c.ScriptStructure ??= new List<string>();
        c.ScriptStructure.RemoveAll(string.IsNullOrWhiteSpace);

        c.ScriptTemplates ??= new List<ScriptTemplate>();
        foreach (var t in c.ScriptTemplates)
        {
            t.Sections ??= new List<string>();
            t.Sections.RemoveAll(string.IsNullOrWhiteSpace);
            if (string.IsNullOrWhiteSpace(t.Name)) t.Name = "unnamed";
        }
        // A template with no sections would produce an unguided script, which is worse than the
        // fallback structure — drop it rather than let rotation land on it.
        c.ScriptTemplates.RemoveAll(t => t.Sections.Count == 0);
    }

    /// <summary>Lists channel ids available in the channels directory.</summary>
    public IEnumerable<string> List()
    {
        if (!Directory.Exists(_channelsDir)) return Enumerable.Empty<string>();
        return Directory.EnumerateFiles(_channelsDir, "*.json")
                        .Select(Path.GetFileNameWithoutExtension)
                        .Where(n => !string.IsNullOrEmpty(n))
                        .Select(n => n!)
                        .OrderBy(n => n);
    }
}
