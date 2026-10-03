using YtPipeline.Core.Models;

namespace YtPipeline.Core.Services;

/// <summary>
/// Moves a run's files to another folder — in practice Google Drive for desktop — and back again,
/// and holds the bookkeeping every way of moving a run shares.
/// </summary>
/// <remarks>
/// The destination is normally on another drive (G: for Drive, D: for the workspace), where a folder
/// can't simply be moved, so files are copied, checked, and only then removed. The manifest and the
/// thumbnail stay behind: the manifest is how the app finds a run at all, and the thumbnail keeps the
/// video recognisable in the grid.
/// </remarks>
public sealed class RunArchiver(RunManager runs)
{
    private const string ManifestName = "manifest.json";

    /// <summary>Every file in the run's folder — what a move has to carry.</summary>
    public static List<string> FilesOf(Run run) =>
        Directory.EnumerateFiles(run.OutputDir, "*", SearchOption.AllDirectories).ToList();

    /// <summary>Throws if the run's files have already been moved somewhere.</summary>
    public void EnsureNotMoved(Run run)
    {
        if (runs.LoadManifest(run).Archive is { } already)
            throw new InvalidOperationException($"This video is already in {already.Remote ?? already.Location}.");
    }

    /// <summary>
    /// Copies every file of <paramref name="run"/> to <c>destinationRoot/&lt;channel&gt;/&lt;stamp&gt;</c>,
    /// records the move in the manifest, then deletes the local copies except the manifest and thumbnail.
    /// </summary>
    public ArchiveInfo MoveOut(Run run, string destinationRoot, Action<string> progress, CancellationToken ct)
    {
        EnsureNotMoved(run);
        if (string.IsNullOrWhiteSpace(destinationRoot))
            throw new InvalidOperationException("Set your Google Drive folder in Settings first.");

        var root = Path.GetPathRoot(Path.GetFullPath(destinationRoot));
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
            throw new DirectoryNotFoundException($"{root} isn't available — is Google Drive for desktop running?");

        var source = run.OutputDir;
        var target = Path.Combine(destinationRoot, run.Channel, run.Stamp);
        if (Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any())
            throw new IOException($"{target} already exists, so nothing was moved rather than overwrite it.");

        var files = FilesOf(run);
        var total = files.Sum(f => new FileInfo(f).Length);
        progress($"Copying {files.Count} files ({Mb(total)}) to {target}");

        try
        {
            Copy(source, target, files, total, progress, ct);
            Verify(source, target, files);
        }
        catch
        {
            // Nothing local has been touched yet; remove the half-made copy so a retry can start clean.
            try { if (Directory.Exists(target)) Directory.Delete(target, recursive: true); } catch { }
            throw;
        }
        progress("Every file arrived at full size.");

        var info = new ArchiveInfo { Location = target, MovedUtc = DateTime.UtcNow, Files = files.Count, Bytes = total };
        RecordMoveAndFreeSpace(run, info, files, progress);
        return info;
    }

    /// <summary>
    /// Records where the files went, then deletes the local copies except the manifest and thumbnail.
    /// Call only once the destination has been checked.
    /// </summary>
    public void RecordMoveAndFreeSpace(Run run, ArchiveInfo info, IReadOnlyList<string> files, Action<string> progress)
    {
        // Recorded before anything is deleted, so an interrupted clean-up still leaves a run that
        // correctly says where its files are.
        runs.UpdateManifest(run, m => m.Archive = info);

        var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Path.GetFullPath(run.ManifestPath),
            Path.GetFullPath(run.ThumbnailPath),
        };
        foreach (var file in files)
            if (!keep.Contains(Path.GetFullPath(file))) File.Delete(file);
        foreach (var dir in Directory.EnumerateDirectories(run.OutputDir, "*", SearchOption.AllDirectories)
                     .OrderByDescending(d => d.Length))
            if (!Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir);

        progress($"Freed {Mb(info.Bytes)} on this PC. The manifest and thumbnail stay so the video keeps its place in the list.");
    }

    /// <summary>
    /// Copies a moved run's files back and clears the move. The copy in the destination is left alone —
    /// removing it is the user's decision, not this app's.
    /// </summary>
    public void BringBack(Run run, Action<string> progress, CancellationToken ct)
    {
        var info = runs.LoadManifest(run).Archive
            ?? throw new InvalidOperationException("This video's files are already on this PC.");
        if (!Directory.Exists(info.Location))
            throw new DirectoryNotFoundException($"Can't find {info.Location} — is Google Drive for desktop running?");

        // The local manifest is newer than the one in Drive (it records the move), so it is never overwritten.
        var files = Directory.EnumerateFiles(info.Location, "*", SearchOption.AllDirectories)
            .Where(f => !string.Equals(Path.GetRelativePath(info.Location, f), ManifestName, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var total = files.Sum(f => new FileInfo(f).Length);
        progress($"Copying {files.Count} files ({Mb(total)}) back from {info.Location}");

        Copy(info.Location, run.OutputDir, files, total, progress, ct);
        Verify(info.Location, run.OutputDir, files);
        RecordBroughtBack(run, progress);
    }

    /// <summary>Clears the move once the files are verifiably back on this PC.</summary>
    public void RecordBroughtBack(Run run, Action<string> progress)
    {
        runs.UpdateManifest(run, m => m.Archive = null);
        progress("Back on this PC. The copy in Google Drive was left as it is — delete it there if you no longer need it.");
    }

    private static void Copy(string from, string to, List<string> files, long total, Action<string> progress,
        CancellationToken ct)
    {
        long copied = 0;
        var nextReport = 25;
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            var dest = Path.Combine(to, Path.GetRelativePath(from, file));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(file, dest, overwrite: true);

            copied += new FileInfo(file).Length;
            var percent = total == 0 ? 100 : (int)(copied * 100 / total);
            if (percent >= nextReport && nextReport < 100)
            {
                progress($"{percent}% copied");
                nextReport = (percent / 25 + 1) * 25;
            }
        }
    }

    /// <summary>Throws unless every file exists at the destination at exactly its original size.</summary>
    private static void Verify(string from, string to, List<string> files)
    {
        foreach (var file in files)
        {
            var dest = Path.Combine(to, Path.GetRelativePath(from, file));
            if (!File.Exists(dest) || new FileInfo(dest).Length != new FileInfo(file).Length)
                throw new IOException($"{dest} did not arrive intact, so nothing was removed.");
        }
    }

    private static string Mb(long bytes) => $"{bytes / 1048576d:F0} MB";
}
