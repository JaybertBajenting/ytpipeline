using FluentAssertions;
using Xunit;
using YtPipeline.Core.Models;
using YtPipeline.Core.Services;

namespace YtPipeline.Tests;

public sealed class RunArchiverTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ytpipe_" + Guid.NewGuid().ToString("N"));
    private string Drive => Path.Combine(_root, "drive");

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

    private string TargetFor(Run run) => Path.Combine(Drive, run.Channel, run.Stamp);

    [Fact]
    public void Moving_copies_everything_and_leaves_only_the_manifest_and_thumbnail()
    {
        var (runs, run) = FinishedRun();

        var info = new RunArchiver(runs).MoveOut(run, Drive, _ => { }, CancellationToken.None);

        var target = TargetFor(run);
        info.Location.Should().Be(target);
        File.ReadAllBytes(Path.Combine(target, "video", "final.mp4")).Should().HaveCount(3000);
        File.Exists(Path.Combine(target, "images", "beat00_a.png")).Should().BeTrue();
        File.Exists(Path.Combine(target, "script.json")).Should().BeTrue();

        Directory.EnumerateFiles(run.OutputDir, "*", SearchOption.AllDirectories).Select(Path.GetFileName)
            .Should().BeEquivalentTo("manifest.json", "thumbnail.png");
        runs.LoadManifest(run).Archive!.Location.Should().Be(target);
        // Still listed, so the video keeps its place in the app.
        runs.ListManifests().Should().ContainSingle(m => m.RunId == run.RunId);
    }

    [Fact]
    public void Moving_refuses_to_overwrite_an_existing_copy_and_touches_nothing_local()
    {
        var (runs, run) = FinishedRun();
        Directory.CreateDirectory(TargetFor(run));
        File.WriteAllText(Path.Combine(TargetFor(run), "someone-elses.txt"), "keep me");

        var act = () => new RunArchiver(runs).MoveOut(run, Drive, _ => { }, CancellationToken.None);

        act.Should().Throw<IOException>();
        File.Exists(run.VideoPath).Should().BeTrue();
        File.Exists(Path.Combine(TargetFor(run), "someone-elses.txt")).Should().BeTrue();
        runs.LoadManifest(run).Archive.Should().BeNull();
    }

    [Fact]
    public void A_stopped_copy_removes_its_partial_copy_and_keeps_local_files()
    {
        var (runs, run) = FinishedRun();
        using var stopped = new CancellationTokenSource();
        stopped.Cancel();

        var act = () => new RunArchiver(runs).MoveOut(run, Drive, _ => { }, stopped.Token);

        act.Should().Throw<OperationCanceledException>();
        Directory.Exists(TargetFor(run)).Should().BeFalse();
        File.Exists(run.VideoPath).Should().BeTrue();
        runs.LoadManifest(run).Archive.Should().BeNull();
    }

    [Fact]
    public void Bringing_back_restores_every_file_and_leaves_the_drive_copy_alone()
    {
        var (runs, run) = FinishedRun();
        var archiver = new RunArchiver(runs);
        archiver.MoveOut(run, Drive, _ => { }, CancellationToken.None);

        archiver.BringBack(run, _ => { }, CancellationToken.None);

        File.ReadAllBytes(run.VideoPath).Should().HaveCount(3000);
        File.Exists(Path.Combine(run.ImagesDir, "beat00_a.png")).Should().BeTrue();
        File.Exists(run.ScriptPath).Should().BeTrue();
        runs.LoadManifest(run).Archive.Should().BeNull();
        File.Exists(Path.Combine(TargetFor(run), "video", "final.mp4")).Should().BeTrue();
    }

    [Fact]
    public void Moving_twice_is_refused()
    {
        var (runs, run) = FinishedRun();
        var archiver = new RunArchiver(runs);
        archiver.MoveOut(run, Drive, _ => { }, CancellationToken.None);

        var again = () => archiver.MoveOut(run, Drive, _ => { }, CancellationToken.None);

        again.Should().Throw<InvalidOperationException>().WithMessage("*already in*");
    }
}
