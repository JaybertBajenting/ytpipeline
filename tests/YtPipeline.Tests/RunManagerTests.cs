using FluentAssertions;
using YtPipeline.Core.Services;
using Xunit;
using YtPipeline.Core.Models;

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

    [Fact]
    public void Run_discovery_ignores_newer_superseded_manifest_snapshots()
    {
        var runs = new RunManager(_root);
        var first = runs.Create("first", "old topic");
        var latest = runs.Create("latest", "current topic");
        File.SetLastWriteTimeUtc(first.ManifestPath, DateTime.UtcNow.AddMinutes(-2));
        File.SetLastWriteTimeUtc(latest.ManifestPath, DateTime.UtcNow.AddMinutes(-1));

        var snapshot = Path.Combine(first.OutputDir, "superseded", "original", "manifest.json");
        Directory.CreateDirectory(Path.GetDirectoryName(snapshot)!);
        var old = runs.LoadManifest(first);
        old.Topic = "superseded topic";
        Json.WriteFile(snapshot, old);

        runs.ListManifests().Select(m => m.RunId).Should().Equal(latest.RunId, first.RunId);
        runs.LatestRunId().Should().Be(latest.RunId);
        runs.Resolve("latest").RunId.Should().Be(latest.RunId);
        runs.LoadManifest(first.RunId).Topic.Should().Be("old topic");
    }

    [Fact]
    public void Saving_changed_narration_invalidates_generated_assets_and_reviews()
    {
        var runs = new RunManager(_root);
        var run = runs.Create("test", "topic");
        var script = new ScriptDocument { Beats = [new() { Index = 0, Narration = "Old fact", ImagePrompt = "A map" }] };
        runs.SaveScript(run, script);
        runs.UpdateManifest(run, m =>
        {
            m.Stages.Voice.MarkDone(); m.Stages.Images.MarkDone(); m.Stages.Video.MarkDone();
            m.Review.ScriptApproved = true; m.Review.VideoApproved = true;
        });
        script.Beats[0].Narration = "Corrected fact";
        runs.SaveScript(run, script);
        var manifest = runs.LoadManifest(run);
        manifest.Stages.Voice.Done.Should().BeFalse();
        manifest.Stages.Images.Done.Should().BeFalse();
        manifest.Stages.Video.Done.Should().BeFalse();
        manifest.Review.ScriptApproved.Should().BeFalse();
        manifest.Review.VideoApproved.Should().BeFalse();
    }

    [Fact]
    public void Updating_only_chapter_metadata_preserves_finished_assets()
    {
        var runs = new RunManager(_root);
        var run = runs.Create("test", "topic");
        var script = new ScriptDocument { Beats = [new() { Index = 0, Narration = "A fact", ImagePrompt = "A map" }] };
        runs.SaveScript(run, script);
        runs.UpdateManifest(run, m => { m.Stages.Voice.MarkDone(); m.Stages.Images.MarkDone(); m.Stages.Video.MarkDone(); });
        script.Description = "00:00 Correct timestamp";
        runs.SaveScript(run, script);
        var manifest = runs.LoadManifest(run);
        manifest.Stages.Voice.Done.Should().BeTrue();
        manifest.Stages.Images.Done.Should().BeTrue();
        manifest.Stages.Video.Done.Should().BeTrue();
    }

    [Fact]
    public void Regeneration_archives_old_images_and_preserves_new_progress_on_resume()
    {
        var runs = new RunManager(_root);
        var run = runs.Create("test", "topic");
        File.WriteAllText(Path.Combine(run.ImagesDir, "beat00_old.png"), "old pixels");
        Directory.CreateDirectory(Path.Combine(run.ImagesDir, "drafts"));
        File.WriteAllText(Path.Combine(run.ImagesDir, "drafts", "note.txt"), "keep this too");

        var archive = runs.PrepareImageGeneration(run, regenerate: true);
        File.ReadAllText(Path.Combine(archive!, "beat00_old.png")).Should().Be("old pixels");
        File.Exists(Path.Combine(archive!, "drafts", "note.txt")).Should().BeTrue();
        Directory.EnumerateFileSystemEntries(run.ImagesDir).Should().BeEmpty();
        runs.LoadManifest(run).Stages.Images.Get<bool>("needsRegeneration").Should().BeFalse();

        File.WriteAllText(Path.Combine(run.ImagesDir, "beat00_new.png"), "new pixels");
        runs.PrepareImageGeneration(run).Should().BeNull();
        File.ReadAllText(Path.Combine(run.ImagesDir, "beat00_new.png")).Should().Be("new pixels");
        runs.LoadManifest(run).Stages.Images.Get<List<string>>("supersededDirs").Should().ContainSingle();
    }

    [Fact]
    public void Failed_archive_keeps_regeneration_required_until_old_files_are_isolated()
    {
        var runs = new RunManager(_root);
        var run = runs.Create("test", "topic");
        var old = Path.Combine(run.ImagesDir, "beat00_old.png");
        File.WriteAllText(old, "old pixels");
        var obstruction = Path.Combine(run.OutputDir, "superseded");
        File.WriteAllText(obstruction, "prevent archive directory creation");

        var act = () => runs.PrepareImageGeneration(run, regenerate: true);
        act.Should().Throw<IOException>();
        runs.LoadManifest(run).Stages.Images.Get<bool>("needsRegeneration").Should().BeTrue();
        File.ReadAllText(old).Should().Be("old pixels");

        File.Delete(obstruction);
        var archive = runs.PrepareImageGeneration(run);
        File.Exists(old).Should().BeFalse();
        File.ReadAllText(Path.Combine(archive!, "beat00_old.png")).Should().Be("old pixels");
    }

    [Fact]
    public void Image_preparation_rejects_paths_outside_the_managers_workspace()
    {
        var runs = new RunManager(_root);
        var outside = new Run
        {
            RunId = "test_stamp", Channel = "test", Topic = "topic", WorkspaceRoot = _root + "_other",
        };
        var act = () => runs.PrepareImageGeneration(outside, regenerate: true);
        act.Should().Throw<InvalidOperationException>().WithMessage("*outside this run*");
        Directory.Exists(outside.WorkspaceRoot).Should().BeFalse();
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
