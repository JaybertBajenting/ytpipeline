using FluentAssertions;
using Xunit;
using YtPipeline.Core.Models;
using YtPipeline.Stages.Video;
using YtPipeline.Stages.Voice;

namespace YtPipeline.Tests;

public sealed class ChapterTimestampsTests
{
    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void Uses_spoken_offsets_even_when_a_sentence_crosses_beats(string newline)
    {
        var script = new ScriptDocument
        {
            Description = string.Join(newline, "Subscribe.", "", "00:00 First subject", "02:30 Second subject", "", "Source: example"),
            Beats =
            [
                new() { Index = 0, Narration = "So, let's start. Number two," },
                new() { Index = 1, Narration = "the first subject. Some explanation." },
                new() { Index = 2, Narration = "Number one, the second subject." }
            ]
        };
        var text = script.NarrationForVoice();
        var alignment = new AudioAlignment { Text = text, CharStartTimes = Enumerable.Repeat(0.0, text.Length).ToList() };
        alignment.CharStartTimes[text.IndexOf("Number one", StringComparison.Ordinal)] = 73.85;
        ChapterTimestamps.TryAlign(script, alignment).Should().Be(
            string.Join(newline, "Subscribe.", "", "00:00 First subject", "01:13 Second subject", "", "Source: example"));
    }

    [Fact]
    public void Rejects_alignment_from_an_older_narration()
    {
        var script = new ScriptDocument { Beats = [new() { Narration = "Number two. Number one." }], Description = "00:00 A\n01:00 B" };
        var alignment = new AudioAlignment { Text = "An old script", CharStartTimes = Enumerable.Repeat(0.0, 13).ToList() };
        ChapterTimestamps.TryAlign(script, alignment).Should().BeNull();
    }

    [Fact]
    public void Does_not_rewrite_nonmatching_chapter_lists()
    {
        var script = new ScriptDocument { Beats = [new() { Narration = "Number two. Number one." }], Description = "00:00 A\n01:00 B\n02:00 C" };
        var text = script.NarrationForVoice();
        ChapterTimestamps.TryAlign(script, new() { Text = text, CharStartTimes = Enumerable.Range(0,text.Length).Select(i => (double)i).ToList() }).Should().BeNull();
    }
}
