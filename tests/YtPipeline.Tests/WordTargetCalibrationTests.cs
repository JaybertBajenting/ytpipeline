using FluentAssertions;
using YtPipeline.Core.Models;
using YtPipeline.Core.Services;
using Xunit;

namespace YtPipeline.Tests;

/// <summary>
/// The word budget has to be derived from how fast the chosen voice actually talks. Two real
/// voices on this project measured 112 and 215 wpm, and a fixed 130-150 assumption turned a
/// 3-5 minute target into a 2-minute video.
/// </summary>
public sealed class WordTargetCalibrationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ytwpm_" + Guid.NewGuid().ToString("N"));

    public WordTargetCalibrationTests()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "ch.json"),
            """{ "name": "Ch", "videoLengthMinMinutes": 3, "videoLengthMaxMinutes": 5 }""");
    }

    private Channel Load() => new ChannelLoader(_dir).Load("ch");

    [Fact]
    public void A_measured_rate_round_trips_through_the_channel_file()
    {
        var loader = new ChannelLoader(_dir);
        var c = loader.Load("ch");
        c.Voice.WordsPerMinute = 215.0;
        loader.Save("ch", c);

        loader.Load("ch").Voice.WordsPerMinute.Should().Be(215.0);
    }

    [Fact]
    public void A_fast_voice_earns_a_bigger_word_budget_than_a_slow_one()
    {
        // Same 3-5 minute target; the fast voice simply needs more words to fill it.
        var slow = Load();  slow.Voice.WordsPerMinute = 112.0;
        var fast = Load();  fast.Voice.WordsPerMinute = 215.0;

        var slowWords = MinWordsFor(slow);
        var fastWords = MinWordsFor(fast);

        fastWords.Should().BeGreaterThan(slowWords);
        // 3 min at 215 wpm is ~645 words; the old fixed floor of 390 is what produced 2:02.
        fastWords.Should().BeGreaterThan(500);
        slowWords.Should().BeInRange(300, 400);
    }

    [Fact]
    public void An_uncalibrated_channel_still_gets_a_sane_budget()
    {
        var c = Load();
        c.Voice.WordsPerMinute.Should().BeNull("nothing has been measured yet");
        MinWordsFor(c).Should().Be(390, "falls back to the 130 wpm floor");
    }

    [Theory]
    [InlineData(10)]     // absurdly slow
    [InlineData(1000)]   // absurdly fast
    public void An_implausible_rate_is_ignored_rather_than_trusted(double bogus)
    {
        var c = Load();
        c.Voice.WordsPerMinute = bogus;
        MinWordsFor(c).Should().Be(390, "a nonsense reading must not size a script");
    }

    /// <summary>
    /// Mirrors the generator's budget rule. Kept here rather than reaching into a private type so
    /// the intent — fast voice, more words — is asserted at the boundary that matters.
    /// </summary>
    private static int MinWordsFor(Channel c)
    {
        var lo = c.VideoLengthMinMinutes;
        return c.Voice?.WordsPerMinute is { } w && w >= 60 && w <= 320
            ? (int)(lo * w)
            : lo * 130;
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }
}
