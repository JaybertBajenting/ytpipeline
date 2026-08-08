using FluentAssertions;
using YtPipeline.Core.Models;
using YtPipeline.Core.Services;
using Xunit;

namespace YtPipeline.Tests;

public sealed class SettingsStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ytset_" + Guid.NewGuid().ToString("N"));

    public SettingsStoreTests() => Directory.CreateDirectory(_dir);

    [Fact]
    public void Defaults_apply_when_no_file_exists()
    {
        var store = new SettingsStore(_dir);

        store.Current.ImageProvider.Should().Be(ImageProviders.Codex);
        store.Current.VoiceGain.Should().Be(1.25);
        store.Current.Gflow.Aspect.Should().Be("16:9");
    }

    /// <summary>
    /// Regression: Newtonsoft reuses the list instance from a property initializer and APPENDS
    /// to it, so a saved <c>["image"]</c> used to load back as <c>["image","t2i","image"]</c> —
    /// which produced a broken CLI invocation. The list properties carry
    /// ObjectCreationHandling.Replace to prevent this.
    /// </summary>
    [Fact]
    public void List_settings_replace_defaults_instead_of_appending()
    {
        var saved = new AppSettings
        {
            ImageProvider = ImageProviders.Gflow,
            Gflow = new GflowSettings
            {
                ArgsTemplate = ["image"],          // flow-agent has no t2i subcommand
                CheckArgs = ["status"],            // flow-agent has no --version
                ExtraArgs = ["--jitter", "5-10"],
            },
        };
        new SettingsStore(_dir).Save(saved);

        // Reload from disk through a fresh store — this is where the append bug appeared.
        var reloaded = new SettingsStore(_dir).Current;

        reloaded.Gflow.ArgsTemplate.Should().Equal("image");
        reloaded.Gflow.CheckArgs.Should().Equal("status");
        reloaded.Gflow.ExtraArgs.Should().Equal("--jitter", "5-10");
    }

    [Fact]
    public void Save_round_trips_and_clamps_out_of_range_values()
    {
        var store = new SettingsStore(_dir);
        store.Save(new AppSettings { VoiceGain = 99, MusicVolume = -1, ImageProvider = "nonsense" });

        var reloaded = new SettingsStore(_dir).Current;
        reloaded.VoiceGain.Should().Be(4.0);              // clamped
        reloaded.MusicVolume.Should().Be(0.0);            // clamped
        reloaded.ImageProvider.Should().Be(ImageProviders.Codex);  // unknown falls back
    }

    [Fact]
    public void Settings_file_is_written_without_a_bom()
    {
        new SettingsStore(_dir).Save(new AppSettings());

        var bytes = File.ReadAllBytes(Path.Combine(_dir, "settings.json"));
        bytes.Take(3).Should().NotEqual(new byte[] { 0xEF, 0xBB, 0xBF });
    }

    [Fact]
    public void Corrupt_file_falls_back_to_defaults_instead_of_throwing()
    {
        File.WriteAllText(Path.Combine(_dir, "settings.json"), "{ not valid json");

        var store = new SettingsStore(_dir);
        store.Current.ImageProvider.Should().Be(ImageProviders.Codex);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }
}
