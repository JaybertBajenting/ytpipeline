using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using YtPipeline.Core.Abstractions;
using YtPipeline.Core.Models;
using YtPipeline.Core.Services;
using YtPipeline.Stages.Images;
using YtPipeline.Stages.Orchestration;
using YtPipeline.Stages.Video;
using YtPipeline.Stages.Voice;

namespace YtPipeline.Tests;

public sealed class PipelineAssetSafetyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "yt_asset_safety_" + Guid.NewGuid().ToString("N"));
    private readonly RunManager _runs;
    private readonly ChannelLoader _channels;
    private readonly Mock<IImageGenerator> _images = new();
    private readonly Mock<IVoiceGenerator> _voice = new();
    private readonly Mock<IVideoAssembler> _video = new();

    public PipelineAssetSafetyTests()
    {
        _runs = new RunManager(_root);
        var channelDir = Path.Combine(_root, "channels");
        Directory.CreateDirectory(channelDir);
        Json.WriteFile(Path.Combine(channelDir, "test.json"), new Channel { Name = "Test" });
        _channels = new ChannelLoader(channelDir);
    }

    private Pipeline Pipeline() => new(_runs, _channels, null!, null!, _images.Object, _voice.Object,
        null!, _video.Object, null!, [], null!, null!, new FfmpegOptions(), NullLogger<Pipeline>.Instance);

    private Run ReadyRun()
    {
        var run = _runs.Create("test", "topic");
        _runs.SaveScript(run, new ScriptDocument
        {
            Beats =
            [
                new() { Index = 0, Narration = "First explanation.", ImagePrompt = "Old scene" },
                new() { Index = 1, Narration = "Second explanation.", ImagePrompt = "Another scene" },
            ],
        });
        _runs.UpdateManifest(run, m =>
        {
            m.Review.ScriptApproved = true;
            m.Review.VideoApproved = true;
            m.Stages.Images.MarkDone(); m.Stages.Voice.MarkDone(); m.Stages.Video.MarkDone();
        });
        return run;
    }

    [Fact]
    public async Task Provider_failure_cannot_reuse_old_images_and_retry_keeps_new_replacements()
    {
        var run = ReadyRun();
        File.WriteAllText(Path.Combine(run.ImagesDir, "beat00_old.png"), "old frame");
        File.WriteAllText(Path.Combine(run.ImagesDir, "beat01_old.png"), "old second frame");
        var script = _runs.LoadScript(run);
        script.Beats[0].ImagePrompt = "Correct source-based scene";
        _runs.SaveScript(run, script);
        _runs.UpdateManifest(run, m => m.Review.ScriptApproved = true);
        var calls = 0;
        _images.Setup(g => g.GenerateAsync(It.IsAny<Run>(), It.IsAny<ScriptDocument>(), It.IsAny<Channel>(),
                It.IsAny<CancellationToken>(), false, null))
            .Returns((Run r, ScriptDocument s, Channel c, CancellationToken ct, bool regenerate, string? provider) =>
            {
                File.Exists(Path.Combine(r.ImagesDir, "beat00_old.png")).Should().BeFalse();
                var first = Path.Combine(r.ImagesDir, "beat00_new.png");
                if (calls++ == 0)
                {
                    File.WriteAllText(first, "new frame");
                    return Task.FromException<IReadOnlyList<string>>(new InvalidOperationException("provider unavailable"));
                }
                File.ReadAllText(first).Should().Be("new frame");
                File.WriteAllText(Path.Combine(r.ImagesDir, "beat01_new.png"), "new second frame");
                return Task.FromResult(ImageSet.List(r.ImagesDir));
            });

        var pipeline = Pipeline();
        var firstAttempt = () => pipeline.ImagesAsync(run, CancellationToken.None);
        await firstAttempt.Should().ThrowAsync<InvalidOperationException>().WithMessage("provider unavailable");
        var failed = _runs.LoadManifest(run);
        failed.Stages.Images.Done.Should().BeFalse();
        failed.Stages.Video.Done.Should().BeFalse();
        failed.Review.VideoApproved.Should().BeFalse();
        var archive = failed.Stages.Images.Get<List<string>>("supersededDirs")!.Single();
        File.ReadAllText(Path.Combine(archive, "beat00_old.png")).Should().Be("old frame");

        await pipeline.ImagesAsync(run, CancellationToken.None);
        _runs.LoadManifest(run).Stages.Images.Done.Should().BeTrue();
        _runs.LoadManifest(run).Stages.Images.Get<List<string>>("supersededDirs").Should().ContainSingle();
        calls.Should().Be(2);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Rendering_rejects_old_or_truncated_voice_alignment_before_assembling(bool oldText)
    {
        var run = ReadyRun();
        var current = _runs.LoadScript(run).NarrationForVoice();
        Json.WriteFile(run.AlignmentPath, new AudioAlignment
        {
            Text = oldText ? "Older voice narration." : current,
            CharStartTimes = Enumerable.Repeat(0.0, current.Length - 1).ToList(),
        });
        var act = () => Pipeline().VideoAsync(run, CancellationToken.None, music: false);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*alignment does not match*Regenerate the voice*");
        _video.VerifyNoOtherCalls();
        _runs.LoadManifest(run).Stages.Video.Done.Should().BeFalse();
        _runs.LoadManifest(run).Review.VideoApproved.Should().BeFalse();
    }

    [Fact]
    public async Task Voice_generation_preserves_old_alignment_outside_the_active_path_even_if_it_fails()
    {
        var run = ReadyRun();
        File.WriteAllText(run.AlignmentPath, "old timing");
        _voice.Setup(g => g.GenerateAsync(It.IsAny<Run>(), It.IsAny<ScriptDocument>(), It.IsAny<Channel>(),
                It.IsAny<CancellationToken>(), null))
            .Returns((Run r, ScriptDocument s, Channel c, CancellationToken ct, string? model) =>
            {
                File.Exists(r.AlignmentPath).Should().BeFalse();
                return Task.FromException<string>(new InvalidOperationException("TTS failed"));
            });
        var act = () => Pipeline().VoiceAsync(run, CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("TTS failed");
        var previous = Directory.GetFiles(run.AudioDir, "align.json.previous_*").Single();
        File.ReadAllText(previous).Should().Be("old timing");
        _runs.LoadManifest(run).Stages.Voice.Done.Should().BeFalse();
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
