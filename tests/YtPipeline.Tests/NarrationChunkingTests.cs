using FluentAssertions;
using YtPipeline.Stages.Voice;
using Xunit;

namespace YtPipeline.Tests;

/// <summary>
/// ElevenLabs refuses more than 5,000 characters in one eleven_v3 request, and a 7-11 minute
/// script is 9,000-14,000. The split has to be lossless: the video stage locates each beat by its
/// character offset into the same narration string, so a single dropped space would shift every
/// image after the seam.
/// </summary>
public sealed class NarrationChunkingTests
{
    private static string Narration(int sentences) =>
        string.Join(" ", Enumerable.Range(0, sentences)
            .Select(i => $"They kept the fire alive on night number {i} because it was the only thing keeping them warm."));

    [Fact]
    public void Short_narration_is_sent_as_one_request()
    {
        var text = Narration(5);
        ElevenLabsVoiceGenerator.SplitForRequests(text, 5000).Should().ContainSingle().Which.Should().Be(text);
    }

    [Fact]
    public void Chunks_rejoin_to_exactly_the_original_text()
    {
        var text = Narration(200);
        var chunks = ElevenLabsVoiceGenerator.SplitForRequests(text, 5000);

        chunks.Count.Should().BeGreaterThan(1);
        string.Concat(chunks).Should().Be(text);
    }

    [Fact]
    public void No_chunk_exceeds_the_limit()
    {
        var text = Narration(500);
        foreach (var chunk in ElevenLabsVoiceGenerator.SplitForRequests(text, 5000))
            chunk.Length.Should().BeLessThanOrEqualTo(5000);
    }

    [Fact]
    public void Chunks_break_at_sentence_ends()
    {
        foreach (var chunk in ElevenLabsVoiceGenerator.SplitForRequests(Narration(200), 5000).SkipLast(1))
            chunk.TrimEnd().Should().EndWith(".");
    }

    [Fact]
    public void Chunks_are_evened_out_rather_than_greedily_filled()
    {
        // 9,000 characters at a 5,000 limit is two requests; sending 5,000 then 4,000 makes the
        // short tail a noticeably different generation, so the halves are balanced instead.
        var chunks = ElevenLabsVoiceGenerator.SplitForRequests(Narration(95), 5000);

        chunks.Should().HaveCount(2);
        var difference = Math.Abs(chunks[0].Length - chunks[1].Length);
        difference.Should().BeLessThan(1000);
    }

    [Fact]
    public void Text_with_no_sentence_breaks_still_splits_at_a_word_boundary()
    {
        var text = string.Join(" ", Enumerable.Repeat("word", 3000));
        var chunks = ElevenLabsVoiceGenerator.SplitForRequests(text, 5000);

        string.Concat(chunks).Should().Be(text);
        chunks.Should().OnlyContain(c => c.Length <= 5000);
        chunks.SkipLast(1).Should().OnlyContain(c => c.EndsWith(" "));
    }

    [Theory]
    [InlineData("eleven_v3", 5000)]
    [InlineData("ELEVEN_V3", 5000)]
    [InlineData("eleven_multilingual_v2", 10_000)]
    [InlineData("eleven_v4", 10_000)]
    [InlineData("eleven_v4_turbo", 10_000)]
    [InlineData("something_new", 5000)]
    public void Model_decides_the_request_limit(string modelId, int expected) =>
        ElevenLabsVoiceGenerator.RequestCharacterLimit(modelId, 0).Should().Be(expected);

    [Fact]
    public void A_configured_limit_overrides_the_model_default() =>
        ElevenLabsVoiceGenerator.RequestCharacterLimit("eleven_v3", 2500).Should().Be(2500);
}
