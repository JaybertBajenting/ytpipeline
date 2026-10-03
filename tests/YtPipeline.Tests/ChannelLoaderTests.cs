using FluentAssertions;
using YtPipeline.Core.Services;
using Xunit;

namespace YtPipeline.Tests;

public sealed class ChannelLoaderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ytch_" + Guid.NewGuid().ToString("N"));

    public ChannelLoaderTests()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "nullpointer.json"),
            """{ "name": "NullPointer", "videoLengthTargetMinutes": 5, "youtube": { "categoryId": "27" } }""");
    }

    [Fact]
    public void Load_sets_id_and_reads_fields()
    {
        var loader = new ChannelLoader(_dir);
        var channel = loader.Load("nullpointer");

        channel.Id.Should().Be("nullpointer");
        channel.Name.Should().Be("NullPointer");
        channel.VideoLengthTargetMinutes.Should().Be(5);
        channel.Youtube.CategoryId.Should().Be("27");
    }

    [Fact]
    public void Load_migrates_legacy_single_length_into_the_range()
    {
        // A channel file written before the min/max range existed must keep behaving the same.
        var channel = new ChannelLoader(_dir).Load("nullpointer");

        channel.VideoLengthMinMinutes.Should().Be(5);
        channel.VideoLengthMaxMinutes.Should().Be(5);
    }

    [Theory]
    [InlineData("gflow", "gflow")]
    [InlineData(" Codex ", "codex")]
    [InlineData("midjourney", "")]
    [InlineData("", "")]
    public void Save_keeps_a_known_image_generator_and_clears_anything_else(string saved, string expected)
    {
        var loader = new ChannelLoader(_dir);
        var channel = loader.Load("nullpointer");
        channel.ImageProvider = saved;
        loader.Save("nullpointer", channel).ImageProvider.Should().Be(expected);
        new ChannelLoader(_dir).Load("nullpointer").ImageProvider.Should().Be(expected);
    }

    [Fact]
    public void Save_keeps_the_range_and_resyncs_the_legacy_field()
    {
        var loader = new ChannelLoader(_dir);
        var channel = loader.Load("nullpointer");
        channel.VideoLengthMinMinutes = 3;
        channel.VideoLengthMaxMinutes = 5;

        var saved = loader.Save("nullpointer", channel);

        saved.VideoLengthMinMinutes.Should().Be(3);
        saved.VideoLengthMaxMinutes.Should().Be(5);
        saved.VideoLengthTargetMinutes.Should().Be(3, "the legacy field mirrors the lower bound");
    }

    [Fact]
    public void Save_widens_an_inverted_range_rather_than_producing_max_below_min()
    {
        var loader = new ChannelLoader(_dir);
        var channel = loader.Load("nullpointer");
        channel.VideoLengthMinMinutes = 8;
        channel.VideoLengthMaxMinutes = 2;   // inverted

        var saved = loader.Save("nullpointer", channel);

        saved.VideoLengthMinMinutes.Should().Be(8);
        saved.VideoLengthMaxMinutes.Should().Be(8);
    }

    [Fact]
    public void Save_clamps_a_length_above_the_ceiling()
    {
        var loader = new ChannelLoader(_dir);
        var channel = loader.Load("nullpointer");
        channel.VideoLengthMinMinutes = 4;
        channel.VideoLengthMaxMinutes = 999;   // would queue thousands of images

        var saved = loader.Save("nullpointer", channel);

        saved.VideoLengthMinMinutes.Should().Be(4);
        saved.VideoLengthMaxMinutes.Should().Be(30, "30 minutes is the ceiling");
    }

    [Fact]
    public void Save_treats_a_one_sided_range_as_that_single_length()
    {
        // Only reachable from a hand-written API call — the UI always sends both ends. The rule
        // is "the missing end equals the given one", so a lone max of 999 means "as long as
        // allowed", not "anywhere from 1 to 30".
        var loader = new ChannelLoader(_dir);
        var channel = loader.Load("nullpointer");
        channel.VideoLengthMinMinutes = 0;
        channel.VideoLengthMaxMinutes = 999;

        var saved = loader.Save("nullpointer", channel);

        saved.VideoLengthMinMinutes.Should().Be(30);
        saved.VideoLengthMaxMinutes.Should().Be(30);
    }

    [Fact]
    public void Save_rejects_a_zero_length_that_would_ask_for_an_empty_script()
    {
        var loader = new ChannelLoader(_dir);
        var channel = loader.Load("nullpointer");
        channel.VideoLengthMinMinutes = 0;
        channel.VideoLengthMaxMinutes = 0;

        var saved = loader.Save("nullpointer", channel);

        // Both absent falls back to the legacy field (5), not to zero.
        saved.VideoLengthMinMinutes.Should().Be(5);
        saved.VideoLengthMaxMinutes.Should().Be(5);
    }

    [Fact]
    public void Load_unknown_channel_throws_with_known_list()
    {
        var loader = new ChannelLoader(_dir);
        var act = () => loader.Load("ghost");
        act.Should().Throw<FileNotFoundException>().WithMessage("*nullpointer*");
    }

    [Fact]
    public void List_returns_channel_ids()
    {
        new ChannelLoader(_dir).List().Should().ContainSingle().Which.Should().Be("nullpointer");
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }
}
