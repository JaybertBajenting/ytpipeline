using FluentAssertions;
using YtPipeline.Core.Models;
using YtPipeline.Core.Services;
using YtPipeline.Stages.Images;
using Xunit;

namespace YtPipeline.Tests;

/// <summary>
/// Banner art is drawn wordless unless a channel opts in, because a model asked for text it was
/// not given spells it wrong and puts it somewhere the crop throws away. The opt-in is
/// <see cref="ChannelIdentity.BannerHeadline"/>, and these tests hold both branches: setting it
/// must remove the blanket no-text ban, and leaving it empty must keep the ban exactly as it was
/// for the channels drawn before the field existed.
/// </summary>
public sealed class BrandPromptTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ytbrand_" + Guid.NewGuid().ToString("N"));

    public BrandPromptTests()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "ch.json"),
            """{ "name": "Ch", "visualStyle": "Stick figures on flat colour." }""");
    }

    private Channel Load() => new ChannelLoader(_dir).Load("ch");

    [Fact]
    public void A_headline_is_quoted_verbatim_and_lifts_the_no_text_ban()
    {
        var c = Load();
        c.Identity.BannerHeadline = "Subscribe For A New List Every Week";

        var prompt = BrandPrompt.Build(c, BrandImage.Banner);

        prompt.Should().Contain("Subscribe For A New List Every Week");
        prompt.Should().NotContain("NO text, NO lettering, NO words anywhere in the image.");
    }

    [Fact]
    public void No_headline_keeps_the_ban_that_every_existing_channel_was_drawn_under()
    {
        var prompt = BrandPrompt.Build(Load(), BrandImage.Banner);

        prompt.Should().Contain("NO text, NO lettering, NO words anywhere in the image.");
        // The row-of-subjects composition only makes sense when there is no bar of type to lay out.
        prompt.Should().Contain("ROW of several recurring subjects");
    }

    [Fact]
    public void Whitespace_does_not_count_as_opting_in()
    {
        var c = Load();
        c.Identity.BannerHeadline = "   ";

        BrandPrompt.Build(c, BrandImage.Banner)
            .Should().Contain("NO text, NO lettering, NO words anywhere in the image.");
    }

    [Fact]
    public void The_banner_asks_for_the_size_youtube_actually_wants()
    {
        // 2048x1152 was the old target; YouTube's stated upload size is 2560x1440, and the safe
        // area maths below only lines up against that.
        BrandPrompt.Build(Load(), BrandImage.Banner).Should().Contain("2560x1440");
    }

    [Fact]
    public void The_banner_warns_about_the_vertical_crop_not_just_the_horizontal_one()
    {
        // The crop keeps 60% of the width but only 29% of the height. Stating the width alone is
        // what let a full-height composition look correct right up until it reached a phone.
        var prompt = BrandPrompt.Build(Load(), BrandImage.Banner);

        prompt.Should().Contain("29%");
        prompt.Should().Contain("60%");
    }

    [Fact]
    public void A_headline_never_leaks_into_the_avatar()
    {
        var c = Load();
        c.Identity.BannerHeadline = "Subscribe For A New List Every Week";

        var prompt = BrandPrompt.Build(c, BrandImage.Avatar);

        prompt.Should().NotContain("Subscribe For A New List Every Week");
        prompt.Should().Contain("NO text, NO lettering, NO words anywhere in the image.");
    }

    [Fact]
    public void The_safe_area_is_the_fraction_the_prompt_and_the_editor_both_quote()
    {
        var (w, h) = BrandImage.SafeFraction;

        w.Should().BeApproximately(0.604, 0.001);
        h.Should().BeApproximately(0.294, 0.001);
    }

    [Fact]
    public void Image_dimensions_are_read_from_the_header()
    {
        // gflow returns 1376x768 for "landscape" and has no size flag, so the banner path has to
        // notice the file is under YouTube's 2048x1152 minimum and scale it. Noticing starts here.
        var path = WritePngHeader(1376, 768);

        ImageSize.Read(path).Should().Be((1376, 768));
    }

    [Fact]
    public void A_full_size_banner_is_read_as_needing_nothing()
    {
        ImageSize.Read(WritePngHeader(2560, 1440))
            .Should().Be((BrandImage.BannerWidth, BrandImage.BannerHeight));
    }

    [Theory]
    [InlineData("not a png at all, just text")]
    [InlineData("\x89PNG")]                       // right magic, truncated before IHDR
    public void Anything_unreadable_comes_back_as_zero_rather_than_throwing(string content)
    {
        // Zero means "don't know", and the caller skips resizing. An unreadable file is the image
        // backend's failure to report — this helper must not turn it into an exception of its own.
        var path = Path.Combine(_dir, "junk.png");
        File.WriteAllText(path, content);

        ImageSize.Read(path).Should().Be((0, 0));
    }

    [Fact]
    public void A_missing_file_comes_back_as_zero()
        => ImageSize.Read(Path.Combine(_dir, "nope.png")).Should().Be((0, 0));

    [Fact]
    public void A_jpeg_wearing_a_png_extension_is_still_measured()
    {
        // This is the real case, not a hypothetical: gflow writes JPEG bytes into the
        // "<channel>.banner.png" path it is given. A PNG-only reader called that unreadable, so the
        // resize was skipped and a 1376x768 banner sailed through as if it were fine.
        var path = Path.Combine(_dir, "actually-a-jpeg.png");
        File.WriteAllBytes(path, JpegHeader(1376, 768));

        ImageSize.Read(path).Should().Be((1376, 768));
    }

    /// <summary>SOI, one skippable APP0 segment, then the SOF0 frame header carrying the size.</summary>
    private static byte[] JpegHeader(int width, int height)
    {
        var b = new List<byte> { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x04, 0x00, 0x00 };  // SOI + APP0
        b.AddRange(new byte[] { 0xFF, 0xC0, 0x00, 0x11, 0x08 });                    // SOF0, precision
        b.AddRange(new[] { (byte)(height >> 8), (byte)height });                     // height first
        b.AddRange(new[] { (byte)(width >> 8), (byte)width });
        return b.ToArray();
    }

    /// <summary>The 24 bytes a PNG reader needs: signature, IHDR chunk header, width, height.</summary>
    private string WritePngHeader(int width, int height)
    {
        var bytes = new byte[24];
        new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(bytes, 0);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(16, 4), width);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(20, 4), height);

        var path = Path.Combine(_dir, $"{width}x{height}.png");
        File.WriteAllBytes(path, bytes);
        return path;
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);
}
