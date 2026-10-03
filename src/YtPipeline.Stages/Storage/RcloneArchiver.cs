using System.Text.RegularExpressions;
using YtPipeline.Core.Models;
using YtPipeline.Core.Services;
using YtPipeline.Stages.Process;

namespace YtPipeline.Stages.Storage;

/// <summary>
/// Moves a run's files to Google Drive — or any rclone remote — by uploading them directly, and back.
/// </summary>
/// <remarks>
/// Copying into Google Drive for desktop's folder frees nothing: the Drive app keeps its own copy in
/// its cache and only clears it when the disk runs low (2026-09-28: five moved videos left 2.2 GB in
/// that cache). rclone talks to Google directly, so once its check passes the local files can go and
/// the space comes back straight away.
/// </remarks>
public sealed class RcloneArchiver(ProcessRunner process, RunManager runs)
{
    // One progress line every 10 s; NOTICE level keeps rclone's per-file chatter out of the job log.
    private static readonly string[] StatsArgs = ["--stats", "10s", "--stats-one-line", "--stats-log-level", "NOTICE"];

    // Anchored to the run folder's root: the local manifest records the move and must never be
    // overwritten by the older copy that was uploaded with the files.
    private static readonly string[] SkipManifest = ["--exclude", "/manifest.json"];

    public async Task<ArchiveInfo> MoveOutAsync(
        Run run, string rclone, string remoteRoot, string? localMirror, Action<string> progress, CancellationToken ct)
    {
        var archiver = new RunArchiver(runs);
        archiver.EnsureNotMoved(run);
        if (string.IsNullOrWhiteSpace(remoteRoot))
            throw new InvalidOperationException("Set the rclone remote in Settings → Storage first.");

        var remote = $"{remoteRoot.TrimEnd('/', '\\')}/{run.Channel}/{run.Stamp}";
        var files = RunArchiver.FilesOf(run);
        var total = files.Sum(f => new FileInfo(f).Length);
        progress($"Uploading {files.Count} files ({Mb(total)}) to {remote}");

        // copy, not move: nothing on this PC is touched until the check below has passed. A failed or
        // stopped upload can simply be started again — rclone skips what already arrived.
        await RunRclone(rclone, ["copy", run.OutputDir, remote, .. StatsArgs], progress, ct);
        progress("Upload finished. Checking every file against the copy in Google Drive…");
        await RunRclone(rclone, ["check", run.OutputDir, remote, "--one-way"], null, ct);
        progress("Every file matches the copy in Google Drive.");

        var info = new ArchiveInfo
        {
            Remote = remote,
            // Where Google Drive for desktop shows the same files, for "Open in Google Drive". Empty when
            // that folder isn't set; the files are in Drive either way.
            Location = string.IsNullOrWhiteSpace(localMirror) ? "" : Path.Combine(localMirror, run.Channel, run.Stamp),
            MovedUtc = DateTime.UtcNow,
            Files = files.Count,
            Bytes = total,
        };
        archiver.RecordMoveAndFreeSpace(run, info, files, progress);
        return info;
    }

    /// <summary>Downloads a moved run's files, checks them, and clears the move. The Drive copy stays.</summary>
    public async Task BringBackAsync(Run run, string rclone, Action<string> progress, CancellationToken ct)
    {
        var info = runs.LoadManifest(run).Archive
            ?? throw new InvalidOperationException("This video's files are already on this PC.");
        var remote = info.Remote
            ?? throw new InvalidOperationException("This video was moved through the Google Drive folder, not rclone.");

        progress($"Downloading {info.Files} files ({Mb(info.Bytes)}) from {remote}");
        await RunRclone(rclone, ["copy", remote, run.OutputDir, .. SkipManifest, .. StatsArgs], progress, ct);
        await RunRclone(rclone, ["check", remote, run.OutputDir, "--one-way", .. SkipManifest], null, ct);
        new RunArchiver(runs).RecordBroughtBack(run, progress);
    }

    private async Task RunRclone(string exe, string[] args, Action<string>? progress, CancellationToken ct)
    {
        var result = await process.RunAsync(exe, args, ct: ct,
            onLine: progress is null ? null : line => { if (StatsLine(line) is { } stats) progress(stats); });
        if (result.Success) return;

        // rclone puts the reason on its last error line ("didn't find section in config file",
        // "3 differences found"); the exit code alone says nothing.
        var why = ExplainMissingRemote(result.StdErr)
                  ?? result.StdErr.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Length > 0)
                  ?? $"exit code {result.ExitCode}";
        throw new InvalidOperationException($"rclone {args[0]} failed, and nothing on this PC was deleted: {why}");
    }

    /// <summary>
    /// Plain words for "rclone doesn't know this remote", or null for any other failure.
    /// </summary>
    /// <remarks>
    /// Seen 2026-09-30: signing in with <c>rclone config</c> from a terminal the Claude desktop app
    /// started saved rclone.conf into that app's private copy of AppData (Windows redirects a packaged
    /// app's AppData writes). Everything started from there could read it, so it worked until the app
    /// was next opened from Explorer, and then every Move and Bring back failed with rclone's own
    /// "didn't find section in config file". %USERPROFILE%\.config\rclone is outside AppData, and
    /// rclone reads it when AppData has no rclone.conf.
    /// </remarks>
    internal static string? ExplainMissingRemote(string stderr)
    {
        var section = Regex.Match(stderr, "didn't find section in config file \\(\"([^\"]+)\"\\)");
        if (!section.Success) return null;
        var remote = section.Groups[1].Value;

        var missing = Regex.Match(stderr, "Config file \"([^\"]+)\" not found");
        if (missing.Success)
            return $"rclone can't find its Google Drive sign-in. There's no rclone.conf at {missing.Groups[1].Value.Replace(@"\\", @"\")}. " +
                   $"Put it in {Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "rclone")} instead, " +
                   "or sign in again with \"rclone config\" in a terminal you open yourself — one started by the Claude app saves it where only Claude can see it.";

        return $"rclone has no remote called \"{remote}\". Check the rclone remote in Settings → Storage against \"rclone listremotes\".";
    }

    /// <summary>The progress part of an rclone stats line — "120 MiB / 400 MiB, 30%, 5 MiB/s, ETA 56s" — or null.</summary>
    internal static string? StatsLine(string line)
    {
        if (!line.Contains("ETA", StringComparison.Ordinal) || !line.Contains('%')) return null;
        var at = line.IndexOf("NOTICE:", StringComparison.Ordinal);
        return (at >= 0 ? line[(at + "NOTICE:".Length)..] : line).Trim();
    }

    private static string Mb(long bytes) => $"{bytes / 1048576d:F0} MB";
}
