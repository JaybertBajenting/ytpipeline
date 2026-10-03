using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using YtPipeline.Core.Models;
using YtPipeline.Core.Services;
using YtPipeline.Stages.Orchestration;
using YtPipeline.Stages.Process;
using YtPipeline.Stages.Video;
using YtPipeline.Stages.Voice;

namespace YtPipeline.Tests;

/// <summary>
/// Which ElevenLabs model voices a video: one picked for the video, else the channel's, else the
/// Settings default, else appsettings.json. Trying a new model on one video must not touch the
/// channel, and redoing that video from the card must not quietly switch it back.
/// </summary>
public sealed class VoiceModelChoiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "yt_voice_model_" + Guid.NewGuid().ToString("N"));

    public VoiceModelChoiceTests() => Directory.CreateDirectory(_root);

    private ElevenLabsVoiceGenerator Generator(string settingsDefault)
    {
        var store = new SettingsStore(_root);
        store.Save(new AppSettings { ElevenLabsModel = settingsDefault });
        return new ElevenLabsVoiceGenerator(Mock.Of<IElevenLabsApi>(),
            new ElevenLabsOptions { ModelId = "eleven_multilingual_v2" }, store,
            new ProcessRunner(NullLogger<ProcessRunner>.Instance), new FfmpegOptions(),
            NullLogger<ElevenLabsVoiceGenerator>.Instance);
    }

    private static Channel WithModel(string model) => new() { Voice = new ChannelVoice { ModelId = model } };

    [Fact]
    public void A_model_picked_for_the_video_beats_the_channel_and_settings() =>
        Generator("eleven_flash_v2_5").ModelFor(WithModel("eleven_v3"), "eleven_v4").Should().Be("eleven_v4");

    [Fact]
    public void The_channel_model_beats_the_settings_default() =>
        Generator("eleven_v4").ModelFor(WithModel("eleven_v3")).Should().Be("eleven_v3");

    [Fact]
    public void A_channel_without_a_model_follows_settings() =>
        Generator("eleven_v4").ModelFor(WithModel("")).Should().Be("eleven_v4");

    [Fact]
    public void An_empty_settings_default_leaves_appsettings_in_charge() =>
        Generator("").ModelFor(new Channel()).Should().Be("eleven_multilingual_v2");

    [Theory]
    [InlineData("eleven_v4", null, "eleven_v4")]            // picked now
    [InlineData(null, "eleven_v4", "eleven_v4")]            // Redo from the card keeps it
    [InlineData("eleven_v3", "eleven_v4", "eleven_v3")]     // a new pick replaces it
    [InlineData("", "eleven_v4", null)]                     // back to the channel's model
    [InlineData(null, null, null)]                          // never picked: follows the channel
    [InlineData("  ELEVEN_V4 ", null, "eleven_v4")]
    public void The_video_keeps_its_own_model_until_told_otherwise(string? requested, string? remembered, string? expected) =>
        Pipeline.VoiceModelChoice(requested, remembered).Should().Be(expected);

    [Theory]
    [InlineData("eleven_v4", "eleven_v4")]
    [InlineData(" Eleven_V4 ", "eleven_v4")]
    [InlineData("eleven v4", "")]
    [InlineData("../../etc", "")]
    [InlineData(null, "")]
    public void Settings_keep_only_real_looking_model_ids(string? saved, string expected)
    {
        var store = new SettingsStore(_root);
        store.Save(new AppSettings { ElevenLabsModel = saved! });
        new SettingsStore(_root).Current.ElevenLabsModel.Should().Be(expected);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
