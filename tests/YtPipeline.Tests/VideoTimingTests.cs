using FluentAssertions;
using YtPipeline.Core.Models;
using YtPipeline.Core.Services;
using YtPipeline.Stages.Video;
using YtPipeline.Stages.Voice;
using Xunit;

namespace YtPipeline.Tests;

public sealed class VideoTimingTests
{
    [Fact]
    public void SplitProportionally_sums_to_total_audio_length()
    {
        var script = new ScriptDocument
        {
            Beats =
            {
                new ScriptBeat { Index = 0, Narration = "one two three four" },
                new ScriptBeat { Index = 1, Narration = "five six" },
                new ScriptBeat { Index = 2, Narration = "seven" },
            },
        };

        var durations = FfmpegVideoAssembler.SplitProportionally(script, 3, 70.0);

        durations.Sum().Should().BeApproximately(70.0, 0.001);
        durations[0].Should().BeGreaterThan(durations[1]); // more words -> longer hold
        durations[1].Should().BeGreaterThan(durations[2]);
    }

    [Fact]
    public void SplitProportionally_handles_missing_beat_with_minimum_weight()
    {
        var script = new ScriptDocument(); // no beats at all
        var durations = FfmpegVideoAssembler.SplitProportionally(script, 2, 10.0);

        durations.Should().HaveCount(2);
        durations.Sum().Should().BeApproximately(10.0, 0.001);
    }

    [Fact]
    public void BuildDurations_splits_a_beats_time_evenly_across_its_images()
    {
        // Two beats, equal word count -> 50s each over 100s of audio.
        var script = new ScriptDocument
        {
            Beats =
            {
                new ScriptBeat { Index = 0, Narration = "one two three four" },
                new ScriptBeat { Index = 1, Narration = "five six seven eight" },
            },
        };

        // Beat 0 was split into 2 images, beat 1 into 1 image.
        var images = new[] { "beat00_00_a.png", "beat00_01_b.png", "beat01_00_c.png" };
        var durations = FfmpegVideoAssembler.BuildDurations(images, script, 100.0);

        durations.Sum().Should().BeApproximately(100.0, 0.001);
        durations[0].Should().BeApproximately(25.0, 0.5); // 50s / 2 images
        durations[1].Should().BeApproximately(25.0, 0.5);
        durations[2].Should().BeApproximately(50.0, 0.5); // whole beat on one image
    }

    [Fact]
    public void TryAlignedDurations_anchors_images_to_spoken_character_times()
    {
        var root = Path.Combine(Path.GetTempPath(), "ytalign_" + Guid.NewGuid().ToString("N"));
        try
        {
            var run = new Run { RunId = "t_x", Channel = "t", Topic = "x", WorkspaceRoot = root };
            Directory.CreateDirectory(run.AudioDir);

            // narration "AAAA BBBB" — beat0 chars 0-3, space at 4, beat1 starts at char 5.
            // Make each character start at its index (seconds); audio total = 10s.
            Json.WriteFile(run.AlignmentPath, new AudioAlignment
            {
                Text = "AAAA BBBB",
                CharStartTimes = Enumerable.Range(0, 9).Select(i => (double)i).ToList(),
            });

            var script = new ScriptDocument
            {
                Beats =
                {
                    new ScriptBeat { Index = 0, Narration = "AAAA" },
                    new ScriptBeat { Index = 1, Narration = "BBBB" },
                },
            };
            var images = new[] { "beat00_00_a.png", "beat00_01_b.png", "beat01_00_c.png" };

            var d = FfmpegVideoAssembler.TryAlignedDurations(run, images, script, 10.0);

            d.Should().NotBeNull();
            d![0].Should().BeApproximately(2.5, 0.01); // beat0 span 0-5s, first of two images
            d[1].Should().BeApproximately(2.5, 0.01);  // beat0 second image
            d[2].Should().BeApproximately(5.0, 0.01);  // beat1 span 5-10s, single image
            d.Sum().Should().BeApproximately(10.0, 0.01);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void TryAlignedDurations_returns_null_when_no_alignment_file()
    {
        var run = new Run { RunId = "t_x", Channel = "t", Topic = "x", WorkspaceRoot = Path.Combine(Path.GetTempPath(), "nope_" + Guid.NewGuid().ToString("N")) };
        var script = new ScriptDocument { Beats = { new ScriptBeat { Index = 0, Narration = "a" } } };
        FfmpegVideoAssembler.TryAlignedDurations(run, new[] { "beat00_00.png" }, script, 5.0).Should().BeNull();
    }

    [Fact]
    public void BuildDurations_falls_back_to_even_share_for_unlabeled_images()
    {
        var script = new ScriptDocument
        {
            Beats = { new ScriptBeat { Index = 0, Narration = "a b c" } },
        };
        var images = new[] { "img_a.png", "img_b.png", "img_c.png", "img_d.png" };

        var durations = FfmpegVideoAssembler.BuildDurations(images, script, 40.0);

        durations.Sum().Should().BeApproximately(40.0, 0.001);
        durations.Should().AllSatisfy(d => d.Should().BeApproximately(10.0, 0.001));
    }
}
