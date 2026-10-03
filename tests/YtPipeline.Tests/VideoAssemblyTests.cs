using System.Globalization;
using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using YtPipeline.Core.Models;
using YtPipeline.Stages.Music;
using YtPipeline.Stages.Process;
using YtPipeline.Stages.Video;

namespace YtPipeline.Tests;

public sealed class VideoAssemblyTests
{
    [FfmpegFact]
    public async Task A_frame_size_change_does_not_drop_any_picture()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ytpipe-assembly-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var ffmpeg = FfmpegFactAttribute.Executable!;
            var runner = new ProcessRunner(NullLogger<ProcessRunner>.Instance);

            // Codex frames differ by a pixel or two. The middle picture's size makes FFmpeg rebuild the
            // filter graph on the way in and again on the way out, which used to discard both the
            // picture before each change and the odd-sized one — whole seconds of video, silently.
            string[] images =
            [
                await Png(runner, ffmpeg, dir, "beat00_red.png", "0xFF0000", "64x36"),
                await Png(runner, ffmpeg, dir, "beat01_green.png", "0x00FF00", "66x34"),
                await Png(runner, ffmpeg, dir, "beat02_blue.png", "0x0000FF", "64x36"),
            ];
            var audio = Path.Combine(dir, "voice.mp3");
            await runner.RunOrThrowAsync(ffmpeg,
                ["-y", "-f", "lavfi", "-i", "sine=frequency=440:duration=3", "-c:a", "libmp3lame", audio]);

            // Equal word counts and no alignment file, so each picture holds for about a second.
            var script = new ScriptDocument
            {
                Beats =
                [
                    new ScriptBeat { Index = 0, Narration = "one two three" },
                    new ScriptBeat { Index = 1, Narration = "four five six" },
                    new ScriptBeat { Index = 2, Narration = "seven eight nine" },
                ],
            };
            var run = new Run { WorkspaceRoot = dir, RunId = "test_1", Channel = "test", Topic = "test" };
            var assembler = new FfmpegVideoAssembler(runner,
                new FfmpegOptions { Executable = ffmpeg, Width = 64, Height = 36, Fps = 30, Preset = "ultrafast" },
                new MusicOptions(), NullLogger<FfmpegVideoAssembler>.Instance);

            var video = await assembler.AssembleAsync(run, script, images, audio, [], CancellationToken.None);

            var copy = await runner.RunOrThrowAsync(ffmpeg,
                ["-hide_banner", "-i", video, "-map", "0:v:0", "-c", "copy", "-f", "null", "-"]);
            var frames = int.Parse(Regex.Matches(copy.StdErr, @"frame=\s*(\d+)").Last().Groups[1].Value,
                CultureInfo.InvariantCulture);
            frames.Should().BeGreaterThanOrEqualTo(85, "three one-second pictures at 30fps is about 90 frames");

            (await ColourOnScreenAt(runner, ffmpeg, video, 0.5)).Should().Be("red");
            (await ColourOnScreenAt(runner, ffmpeg, video, 1.5)).Should().Be("green");
            (await ColourOnScreenAt(runner, ffmpeg, video, 2.5)).Should().Be("blue");
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    private static async Task<string> Png(ProcessRunner runner, string ffmpeg, string dir, string name,
        string colour, string size)
    {
        var path = Path.Combine(dir, name);
        await runner.RunOrThrowAsync(ffmpeg,
            ["-y", "-f", "lavfi", "-i", $"color=c={colour}:s={size}", "-frames:v", "1", path]);
        return path;
    }

    /// <summary>Names the primary colour of the frame showing at <paramref name="seconds"/>.</summary>
    private static async Task<string> ColourOnScreenAt(ProcessRunner runner, string ffmpeg, string video,
        double seconds)
    {
        var probe = await runner.RunOrThrowAsync(ffmpeg,
            ["-hide_banner", "-ss", seconds.ToString(CultureInfo.InvariantCulture), "-i", video,
                "-frames:v", "1", "-vf", "format=yuv420p,showinfo", "-f", "null", "-"]);
        var mean = Regex.Match(probe.StdErr, @"mean:\[(\d+) (\d+) (\d+)\]");
        mean.Success.Should().BeTrue($"showinfo should describe the frame at {seconds}s");
        var u = int.Parse(mean.Groups[2].Value, CultureInfo.InvariantCulture);
        var v = int.Parse(mean.Groups[3].Value, CultureInfo.InvariantCulture);
        // BT.601: red drives V high, blue drives U high, green pulls both low. The odd-sized green
        // frame is letterboxed, and its black bars (U = V = 128) only nudge the means toward the middle.
        return v > 170 ? "red" : u > 170 ? "blue" : u < 100 && v < 100 ? "green" : $"unclear (U={u}, V={v})";
    }
}
