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
