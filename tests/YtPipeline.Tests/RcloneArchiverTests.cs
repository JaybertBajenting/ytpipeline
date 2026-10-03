using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using YtPipeline.Core.Models;
using YtPipeline.Core.Services;
using YtPipeline.Stages.Process;
using YtPipeline.Stages.Storage;

namespace YtPipeline.Tests;

public sealed class RcloneArchiverTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ytpipe_" + Guid.NewGuid().ToString("N"));

    // rclone treats a plain folder path as a remote, so the real upload/check/download path runs here
    // without touching anyone's Google Drive.
    private string Remote => Path.Combine(_root, "remote");

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private (RunManager runs, Run run) FinishedRun()
    {
        var runs = new RunManager(Path.Combine(_root, "workspace"));
        var run = runs.Create("barely-an-expert", "Test topic");
        File.WriteAllText(run.ScriptPath, "{}");
        File.WriteAllBytes(Path.Combine(run.ImagesDir, "beat00_a.png"), new byte[1000]);
        File.WriteAllBytes(run.AudioPath, new byte[2000]);
        File.WriteAllBytes(run.VideoPath, new byte[3000]);
        File.WriteAllBytes(run.ThumbnailPath, new byte[500]);
        return (runs, run);
    }

    private static RcloneArchiver Archiver(RunManager runs) =>
        new(new ProcessRunner(NullLogger<ProcessRunner>.Instance), runs);

    [RcloneFact]
    public async Task Uploading_checks_then_frees_the_space_and_bringing_back_restores_it()
    {
        var (runs, run) = FinishedRun();
        var archiver = Archiver(runs);

        var info = await archiver.MoveOutAsync(run, RcloneFactAttribute.Executable!, Remote, null, _ => { }, CancellationToken.None);

        info.Remote.Should().Be($"{Remote}/barely-an-expert/{run.Stamp}");
        File.ReadAllBytes(Path.Combine(Remote, "barely-an-expert", run.Stamp, "video", "final.mp4")).Should().HaveCount(3000);
        Directory.EnumerateFiles(run.OutputDir, "*", SearchOption.AllDirectories).Select(Path.GetFileName)
            .Should().BeEquivalentTo("manifest.json", "thumbnail.png");
        runs.LoadManifest(run).Archive!.Remote.Should().Be(info.Remote);

        await archiver.BringBackAsync(run, RcloneFactAttribute.Executable!, _ => { }, CancellationToken.None);

        File.ReadAllBytes(run.VideoPath).Should().HaveCount(3000);
        File.Exists(Path.Combine(run.ImagesDir, "beat00_a.png")).Should().BeTrue();
        runs.LoadManifest(run).Archive.Should().BeNull();
        // The copy in the destination is never deleted by bringing it back.
        File.Exists(Path.Combine(Remote, "barely-an-expert", run.Stamp, "video", "final.mp4")).Should().BeTrue();
    }

    [RcloneFact]
    public async Task A_failed_upload_keeps_every_local_file()
    {
        var (runs, run) = FinishedRun();

        var act = () => Archiver(runs).MoveOutAsync(
            run, RcloneFactAttribute.Executable!, "no-such-remote-configured:YtPipeline", null, _ => { }, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*nothing on this PC was deleted*");
        File.Exists(run.VideoPath).Should().BeTrue();
        File.Exists(Path.Combine(run.ImagesDir, "beat00_a.png")).Should().BeTrue();
        runs.LoadManifest(run).Archive.Should().BeNull();
    }

    [Fact]
    public void Progress_lines_are_reduced_to_the_transfer_figures()
    {
        RcloneArchiver.StatsLine("2026/09/28 18:30:00 NOTICE:   120.000 MiB / 400.000 MiB, 30%, 5.000 MiB/s, ETA 56s")
            .Should().Be("120.000 MiB / 400.000 MiB, 30%, 5.000 MiB/s, ETA 56s");
        RcloneArchiver.StatsLine("2026/09/28 18:30:00 INFO  : images/beat00_a.png: Copied (new)").Should().BeNull();
    }

    [Fact]
    public void A_missing_sign_in_file_is_explained_with_where_to_put_it()
    {
        // Exactly what the app logged on 2026-09-30 when rclone.conf sat in the Claude app's private AppData.
        const string stderr =
            "2026/09/30 13:25:30 NOTICE: Config file \"C:\\\\Users\\\\M4tth3w\\\\AppData\\\\Roaming\\\\rclone\\\\rclone.conf\" not found - using defaults\n" +
            "2026/09/30 13:25:30 CRITICAL: Failed to create file system for \"gdrive:YtPipeline/x/1\": didn't find section in config file (\"gdrive\")\n";

        RcloneArchiver.ExplainMissingRemote(stderr).Should()
            .Contain(@"no rclone.conf at C:\Users\M4tth3w\AppData\Roaming\rclone\rclone.conf")
            .And.Contain(Path.Combine(".config", "rclone"));
    }

    [Fact]
    public void A_config_without_that_remote_names_the_remote()
    {
        const string stderr =
            "2026/09/30 13:25:30 CRITICAL: Failed to create file system for \"gdrve:YtPipeline\": didn't find section in config file (\"gdrve\")\n";
        RcloneArchiver.ExplainMissingRemote(stderr).Should().Contain("no remote called \"gdrve\"");
    }

    [Fact]
    public void Other_failures_keep_rclones_own_words() =>
        RcloneArchiver.ExplainMissingRemote("2026/09/30 ERROR : 3 differences found").Should().BeNull();
}

/// <summary>Runs rclone integration checks when rclone is installed; CI can set YTPIPE_RCLONE.</summary>
public sealed class RcloneFactAttribute : FactAttribute
{
    internal static string? Executable => FindExecutable();

    public RcloneFactAttribute()
    {
        if (Executable is null) Skip = "rclone is unavailable; set YTPIPE_RCLONE to run rclone checks.";
    }

    private static string? FindExecutable()
    {
        var configured = Environment.GetEnvironmentVariable("YTPIPE_RCLONE");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured)) return configured;
        var name = OperatingSystem.IsWindows() ? "rclone.exe" : "rclone";
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            var candidate = Path.Combine(dir, name);
            if (File.Exists(candidate)) return candidate;
        }
        // winget's shim folder, which a process started before the install doesn't have on PATH yet.
        var winget = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft", "WinGet", "Links", name);
        return File.Exists(winget) ? winget : null;
    }
}
