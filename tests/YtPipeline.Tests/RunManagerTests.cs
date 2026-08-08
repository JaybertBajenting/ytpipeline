using FluentAssertions;
using YtPipeline.Core.Services;
using Xunit;

namespace YtPipeline.Tests;

public sealed class RunManagerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ytpipe_" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Create_writes_manifest_and_asset_folders()
    {
        var runs = new RunManager(_root);
        var run = runs.Create("nullpointer", "How DNS works");

        run.RunId.Should().StartWith("nullpointer_");
        File.Exists(run.ManifestPath).Should().BeTrue();
        Directory.Exists(run.ImagesDir).Should().BeTrue();
        Directory.Exists(run.AudioDir).Should().BeTrue();
        Directory.Exists(run.VideoDir).Should().BeTrue();
    }

    [Fact]
    public void Manifest_round_trips_stage_extra_fields()
    {
        var runs = new RunManager(_root);
        var run = runs.Create("nullpointer", "topic");

        runs.UpdateManifest(run, m =>
        {
            m.Stages.Images.MarkDone(run.ImagesDir);
            m.Stages.Images.Set("count", 5);
        });

        var reloaded = runs.LoadManifest(run.RunId);
        reloaded.Stages.Images.Done.Should().BeTrue();
        reloaded.Stages.Images.Get<int>("count").Should().Be(5);
    }

    [Fact]
    public void Resolve_latest_returns_most_recent_run()
    {
        var runs = new RunManager(_root);
        runs.Create("cavefinance", "a");
        Thread.Sleep(1100); // run ids are second-resolution
        var second = runs.Create("nullpointer", "b");

        runs.Resolve("latest").RunId.Should().Be(second.RunId);
    }

    [Fact]
    public void Manifest_is_written_without_a_bom()
    {
        var runs = new RunManager(_root);
        var run = runs.Create("nullpointer", "topic");

        var bytes = File.ReadAllBytes(run.ManifestPath);
        bytes.Take(3).Should().NotEqual(new byte[] { 0xEF, 0xBB, 0xBF });
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
