using System.Globalization;
using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using YtPipeline.Core.Models;
using YtPipeline.Core.Services;
using YtPipeline.Stages.Music;
using YtPipeline.Stages.Process;
using YtPipeline.Stages.Video;
using YtPipeline.Stages.Voice;

namespace YtPipeline.Tests;

public sealed class AudioRepairTests
{
    [Fact]
    public void Joining_removes_file_tags_and_preserves_every_audio_byte()
    {
        // An ID3-looking sequence INSIDE the audio must survive unchanged.
        byte[] frames = [0xff, 0xfb, 0x90, 0xc0, .. "ID3"u8.ToArray(), 1, 2, 3];
        byte[] header = [.. "ID3"u8.ToArray(), 4, 0, 0, 0, 0, 0, 3, 9, 8, 7];
        byte[] trailer = [.. "TAG"u8.ToArray(), .. new byte[125]];
        var chunk = header.Concat(frames).Concat(trailer).ToArray();
        Mp3ChunkJoiner.WithoutFileTags(chunk).ToArray().Should().Equal(frames);
        chunk.Should().Equal(header.Concat(frames).Concat(trailer));
    }

    [Fact]
    public void Id3_footer_is_removed_but_an_untagged_stream_is_unchanged()
    {
        byte[] frames = [0xff, 0xfb, 0x90, 0xc0, 1, 2, 3];
        byte[] tagged = [.. "ID3"u8.ToArray(), 4, 0, 0x10, 0, 0, 0, 0, .. new byte[10], .. frames];
        Mp3ChunkJoiner.WithoutFileTags(tagged).ToArray().Should().Equal(frames);
        Mp3ChunkJoiner.WithoutFileTags(frames).ToArray().Should().Equal(frames);
    }

    [Theory]
    [InlineData(128)] // invalid syncsafe size
    [InlineData(127)] // tag extends beyond the response
    public void Invalid_tags_fail_instead_of_silently_losing_audio(byte size)
    {
        byte[] bytes = [.. "ID3"u8.ToArray(), 4, 0, 0, 0, 0, 0, size, 0xff, 0xfb];
        Action action = () => Mp3ChunkJoiner.WithoutFileTags(bytes).ToArray();
        action.Should().Throw<InvalidDataException>();
    }

    [FfmpegFact]
    public async Task Generated_chunks_decode_cleanly_and_retain_alignment_offsets()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ytpipe-audio-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var runner = new ProcessRunner(NullLogger<ProcessRunner>.Instance);
            var input = Path.Combine(dir, "chunk.mp3");
            await runner.RunOrThrowAsync(FfmpegFactAttribute.Executable!,
                ["-y", "-f", "lavfi", "-i", "sine=frequency=440:duration=0.2", "-c:a", "libmp3lame", input]);
            var bytes = File.ReadAllBytes(input);
            var script = new ScriptDocument { Beats = [new ScriptBeat { Index = 0, Narration = "First sentence. Second sentence. Third sentence." }] };
            var text = script.NarrationForVoice();
            var chunks = ElevenLabsVoiceGenerator.SplitForRequests(text, 25);
            var api = new Mock<IElevenLabsApi>();
            api.Setup(a => a.TextToSpeechWithTimestampsAsync(It.IsAny<string>(), It.IsAny<TtsRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((string _, TtsRequest request, CancellationToken _) => new TtsTimestampsResponse
                {
                    AudioBase64 = Convert.ToBase64String(bytes),
                    Alignment = new TtsAlignment
                    {
                        CharacterStartTimesSeconds = Enumerable.Repeat(0.0, request.Text.Length).ToList(),
                        CharacterEndTimesSeconds = Enumerable.Repeat(0.1, request.Text.Length).ToList(),
                    },
                });
            var run = new Run { WorkspaceRoot = dir, RunId = "test_1", Channel = "test", Topic = "test" };
            var generator = new ElevenLabsVoiceGenerator(api.Object,
                new ElevenLabsOptions { VoiceId = "test", MaxRequestCharacters = 25 }, new SettingsStore(dir), runner,
                new FfmpegOptions { Executable = FfmpegFactAttribute.Executable! }, NullLogger<ElevenLabsVoiceGenerator>.Instance);
            await generator.GenerateAsync(run, script, new Channel { Id = "test" }, CancellationToken.None);

            var decode = await runner.RunOrThrowAsync(FfmpegFactAttribute.Executable!,
                ["-v", "error", "-i", run.AudioPath, "-f", "null", "-"]);
            decode.StdErr.Should().BeEmpty();
            Mp3Duration.GetSeconds(run.AudioPath).Should().BeApproximately(chunks.Count * Mp3Duration.GetSeconds(bytes), 0.000001);
            var alignment = Json.ReadFile<AudioAlignment>(run.AlignmentPath);
            alignment.Text.Should().Be(text);
            alignment.CharStartTimes.Should().HaveCount(text.Length);
            int cursor = 0;
            for (int i = 0; i < chunks.Count; i++)
            {
                alignment.CharStartTimes[cursor].Should().BeApproximately(i * Mp3Duration.GetSeconds(bytes), 0.000001);
                cursor += chunks[i].Length;
            }
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [FfmpegFact]
    public async Task Limiter_preserves_sample_count_and_leaves_headroom_after_aac_encoding()
    {
        var runner = new ProcessRunner(NullLogger<ProcessRunner>.Instance);
        var dir = Path.Combine(Path.GetTempPath(), "ytpipe-limiter-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            foreach (bool mixMusic in new[] { false, true })
            {
                var output = Path.Combine(dir, mixMusic ? "mix.m4a" : "voice.m4a");
                // A boosted, near-full-scale signal exceeds 0 dBFS before the limiter.
                var filter = mixMusic
                    ? $"asplit[v][m];[v]volume=1.25[voc];[m]volume=0.15[bgm];[voc][bgm]amix=inputs=2:normalize=0,{FfmpegVideoAssembler.OutputLimiter}"
                    : $"volume=1.25,{FfmpegVideoAssembler.OutputLimiter}";
                await runner.RunOrThrowAsync(FfmpegFactAttribute.Executable!,
                    ["-y", "-f", "lavfi", "-i", "aevalsrc=0.95*sin(2*PI*440*t):s=48000:d=1", "-af", filter,
                        "-c:a", "aac", "-b:a", "192k", output]);
                var measured = await runner.RunOrThrowAsync(FfmpegFactAttribute.Executable!,
                    ["-hide_banner", "-i", output, "-af", "astats=metadata=1:reset=0", "-f", "null", "-"]);
                var peak = Regex.Matches(measured.StdErr, @"Peak level dB: (-?[\d.]+)").Last().Groups[1].Value;
                double.Parse(peak, CultureInfo.InvariantCulture).Should().BeLessThan(-0.5);

                // Lookahead compensation must neither delay the pictures nor truncate speech.
                var pcm = await runner.RunOrThrowAsync(FfmpegFactAttribute.Executable!,
                    ["-hide_banner", "-f", "lavfi", "-i", "aevalsrc=0.95*sin(2*PI*440*t):s=48000:d=1", "-af",
                        $"{FfmpegVideoAssembler.OutputLimiter},astats=metadata=1:reset=0", "-f", "null", "-"]);
                pcm.StdErr.Should().Contain("Number of samples: 48000");
            }
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}

/// <summary>Run media integration checks when FFmpeg is installed; CI can set YTPIPE_FFMPEG.</summary>
public sealed class FfmpegFactAttribute : FactAttribute
{
    internal static string? Executable => FindExecutable();

    public FfmpegFactAttribute()
    {
        if (Executable is null) Skip = "FFmpeg is unavailable; set YTPIPE_FFMPEG to run media integration checks.";
    }

    private static string? FindExecutable()
    {
        var configured = Environment.GetEnvironmentVariable("YTPIPE_FFMPEG");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured)) return configured;
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            var candidate = Path.Combine(dir, OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg");
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }
}
