using YtPipeline.Core.Models;

namespace YtPipeline.Core.Services;

/// <summary>
/// Owns run ids, run folders, manifest read/write, and script.json read/write.
/// Each run is a single folder <c>videos/&lt;channel&gt;/&lt;timestamp&gt;/</c> holding everything
/// for that video. The orchestrator and every stage talk to runs through this.
/// </summary>
public sealed class RunManager
{
    private readonly string _workspaceRoot;

    public RunManager(string workspaceRoot) => _workspaceRoot = workspaceRoot;

    private string VideosRoot => Path.Combine(_workspaceRoot, "videos");

    /// <summary>Creates a new run, writes its initial manifest, and ensures its folders exist.</summary>
    public Run Create(string channel, string topic)
    {
        var now = DateTime.UtcNow;
        var run = new Run
        {
            RunId = Run.NewRunId(channel, now),
            Channel = channel,
            Topic = topic,
            CreatedUtc = now,
            WorkspaceRoot = _workspaceRoot,
        };

        Directory.CreateDirectory(run.OutputDir);
        Directory.CreateDirectory(run.ImagesDir);
        Directory.CreateDirectory(run.AudioDir);
        Directory.CreateDirectory(run.VideoDir);

        SaveManifest(run, Manifest.FromRun(run));
        return run;
    }

    /// <summary>Rebuilds a <see cref="Run"/> from an existing run id (or "latest").</summary>
    public Run Resolve(string runIdOrLatest)
    {
        var manifestPath = string.Equals(runIdOrLatest, "latest", StringComparison.OrdinalIgnoreCase)
            ? LatestManifestPath() ?? throw new InvalidOperationException("No runs exist yet.")
            : FindManifestPath(runIdOrLatest)
              ?? throw new FileNotFoundException($"Run '{runIdOrLatest}' not found under {VideosRoot}.");

        return RunFromManifest(Json.ReadFile<Manifest>(manifestPath));
    }

    public string? LatestRunId()
    {
        var path = LatestManifestPath();
        return path is null ? null : Json.ReadFile<Manifest>(path).RunId;
    }

    /// <summary>All runs across all channels, most recently updated first.</summary>
    public IReadOnlyList<Manifest> ListManifests() =>
        EnumerateManifests()
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .Select(Json.ReadFile<Manifest>)
            .ToList();

    /// <summary>
    /// Permanently deletes a run's folder (<c>videos/&lt;channel&gt;/&lt;stamp&gt;</c>) and
    /// everything under it — script, frames, voice, video. Returns what was removed so the
    /// caller can report it; generated frames cost real quota, so a silent delete is not enough.
    /// </summary>
    public (int files, long bytes, string dir) Delete(string runId)
    {
        var run = Resolve(runId);
        var dir = run.OutputDir;
        if (!Directory.Exists(dir))
            throw new DirectoryNotFoundException($"Run folder not found: {dir}");

        // Refuse anything that isn't actually inside videos/ — Resolve() builds the path from a
        // manifest on disk, but this is the one destructive call here, so it double-checks.
        var videos = Path.GetFullPath(VideosRoot);
        if (!Path.GetFullPath(dir).StartsWith(videos, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Refusing to delete outside {videos}: {dir}");

        var (files, bytes) = Measure(dir);
        Directory.Delete(dir, recursive: true);
        return (files, bytes, dir);
    }

    /// <summary>File count and total size of a run folder, for delete confirmations.</summary>
    public static (int files, long bytes) Measure(string dir)
    {
        if (!Directory.Exists(dir)) return (0, 0);
        var all = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).ToList();
        return (all.Count, all.Sum(f => new FileInfo(f).Length));
    }

    public Manifest LoadManifest(Run run) => Json.ReadFile<Manifest>(run.ManifestPath);

    public Manifest LoadManifest(string runId)
    {
        var path = FindManifestPath(runId)
            ?? throw new FileNotFoundException($"Run '{runId}' not found under {VideosRoot}.");
        return Json.ReadFile<Manifest>(path);
    }

    public void SaveManifest(Run run, Manifest manifest) =>
        Json.WriteFile(run.ManifestPath, manifest);

    /// <summary>Load, mutate, save the manifest in one shot. Returns the saved manifest.</summary>
    public Manifest UpdateManifest(Run run, Action<Manifest> mutate)
    {
        var manifest = LoadManifest(run);
        mutate(manifest);
        SaveManifest(run, manifest);
        return manifest;
    }

    public void SaveScript(Run run, ScriptDocument script)
    {
        var old = File.Exists(run.ScriptPath) ? LoadScript(run) : null;
        var narrationChanged = old is not null && old.NarrationForVoice() != script.NarrationForVoice();
        var storyboardChanged = old is not null && !old.Beats.Select(b => (b.Index, b.Narration, b.ImagePrompt))
            .SequenceEqual(script.Beats.Select(b => (b.Index, b.Narration, b.ImagePrompt)));
        // Invalidate first. If either write fails, completed flags must never describe assets for
        // a newer script that reached disk before its invalidation was persisted.
        if (narrationChanged || storyboardChanged) UpdateManifest(run, m =>
        {
            if (narrationChanged)
            {
                m.Stages.Voice.Done = false;
                m.Stages.Voice.CompletedUtc = null;
            }
            if (storyboardChanged)
            {
                m.Stages.Images.Done = false;
                m.Stages.Images.CompletedUtc = null;
                m.Stages.Images.Set("needsRegeneration", true);
            }
            m.Stages.Video.Done = false;
            m.Stages.Video.CompletedUtc = null;
            m.Review.ScriptApproved = false;
            m.Review.VideoApproved = false;
        });
        Json.WriteFile(run.ScriptPath, script);
    }

    /// <summary>
    /// Starts or resumes image generation. Old images are moved out of the active folder before
    /// regeneration is cleared, so a failing provider cannot make the next attempt reuse them.
    /// </summary>
    public string? PrepareImageGeneration(Run run, bool regenerate = false)
    {
        var imageDir = CheckedRunChildPath(run, run.ImagesDir);
        var manifest = UpdateManifest(run, m =>
        {
            m.Stages.Images.Done = false;
            m.Stages.Images.CompletedUtc = null;
            if (regenerate) m.Stages.Images.Set("needsRegeneration", true);
            m.Stages.Video.Done = false;
            m.Stages.Video.CompletedUtc = null;
            m.Review.VideoApproved = false;
        });
        if (!manifest.Stages.Images.Get<bool>("needsRegeneration")) return null;

        string? archive = null;
        if (Directory.Exists(imageDir))
        {
            // Move the directory intact: drafts and sidecars also survive. A failed move throws
            // with needsRegeneration still set. Each target is checked before this recursive move.
            archive = CheckedRunChildPath(run, Path.Combine(run.OutputDir, "superseded", "images",
                DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fff") + "_" + Guid.NewGuid().ToString("N")));
            Directory.CreateDirectory(Path.GetDirectoryName(archive)!);
            Directory.Move(imageDir, archive);
        }
        Directory.CreateDirectory(imageDir);
        UpdateManifest(run, m =>
        {
            m.Stages.Images.Clear("needsRegeneration");
            if (archive is not null)
            {
                var previous = m.Stages.Images.Get<List<string>>("supersededDirs") ?? [];
                previous.Add(archive);
                m.Stages.Images.Set("supersededDirs", previous);
            }
        });
        return archive;
    }

    private string CheckedRunChildPath(Run run, string path)
    {
        var videos = Path.GetFullPath(VideosRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                     + Path.DirectorySeparatorChar;
        var output = Path.GetFullPath(run.OutputDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                     + Path.DirectorySeparatorChar;
        var candidate = Path.GetFullPath(path);
        if (!output.StartsWith(videos, StringComparison.OrdinalIgnoreCase)
            || !candidate.StartsWith(output, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Refusing to move image assets outside this run: {candidate}");
        return candidate;
    }

    public ScriptDocument LoadScript(Run run) =>
        Json.ReadFile<ScriptDocument>(run.ScriptPath);

    // --- helpers -------------------------------------------------------------

    private Run RunFromManifest(Manifest m) => new()
    {
        RunId = m.RunId,
        Channel = m.Channel,
        Topic = m.Topic,
        CreatedUtc = m.CreatedUtc,
        WorkspaceRoot = _workspaceRoot,
    };

    /// <summary>Active run manifests, excluding snapshots preserved below a run's folder.</summary>
    private IEnumerable<string> EnumerateManifests()
    {
        if (!Directory.Exists(VideosRoot)) return Enumerable.Empty<string>();
        return Directory.EnumerateDirectories(VideosRoot)
            .SelectMany(channel => Directory.EnumerateDirectories(channel))
            .Select(run => Path.Combine(run, "manifest.json"))
            .Where(File.Exists);
    }

    private string? LatestManifestPath() =>
        EnumerateManifests().OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();

    private string? FindManifestPath(string runId) =>
        EnumerateManifests().FirstOrDefault(p => Json.ReadFile<Manifest>(p).RunId == runId);
}
