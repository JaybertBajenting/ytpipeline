using System.Text;
using YtPipeline.Core.Models;

namespace YtPipeline.Stages.Images;

/// <summary>
/// Art direction for a channel's avatar and banner. Shared by both image backends so the two
/// produce the same brief and a channel looks the same whichever one was available that day.
/// </summary>
internal static class BrandPrompt
{
    public static string Build(Channel channel, string kind)
    {
        kind = BrandImage.Normalize(kind);
        var sb = new StringBuilder();

        // Style only — deliberately NOT the frame rule by default. <id>.image.md is a brief for
        // video frames ("one image per beat", "match the narration", "mostly white empty space",
        // "every image must be 16:9, not square"), and pasting it here made the first Death by
        // Design avatar come out as an episode frame: a puzzled stickman, a red question mark, and
        // a square request fighting a 16:9 instruction. visualStyle is a pure look descriptor, so
        // it is what channel art is drawn from.
        sb.AppendLine(StyleRule(channel).TrimEnd());
        sb.AppendLine();

        sb.AppendLine(kind == BrandImage.Avatar ? "=== CHANNEL AVATAR ===" : "=== CHANNEL BANNER ===");
        sb.AppendLine($"Artwork for the YouTube channel \"{channel.Name}\".");
        if (!string.IsNullOrWhiteSpace(channel.Tagline))
            sb.AppendLine($"What the channel is about: {channel.Tagline}");
        sb.AppendLine();

        sb.AppendLine("This is CHANNEL ARTWORK, not a frame from a video. It illustrates nothing and explains");
        sb.AppendLine("nothing — no narration to match, no story beat, no scene of someone reacting to something.");
        sb.AppendLine("Do not draw question marks, exclamation marks, arrows, labels or thought bubbles.");
        sb.AppendLine();

        if (kind == BrandImage.Avatar)
        {
            sb.AppendLine("SQUARE, 1:1. If the style notes above mention 16:9, 'horizontal' or 'not square', that");
            sb.AppendLine("applies to video frames only — ignore it here. This image must be square.");
            sb.AppendLine("- ONE simple, bold emblem standing for the channel. By default an OBJECT or symbol —");
            sb.AppendLine("  not a scene, not a montage. If this channel's own direction below asks for a specific");
            sb.AppendLine("  face or character instead, draw that: one head, still filling the square.");
            sb.AppendLine("- It must FILL most of the square — roughly three quarters of the height. A small mark");
            sb.AppendLine("  floating in empty space is wrong.");
            sb.AppendLine("- Centred, with even margin all round: YouTube crops it to a CIRCLE, so anything near a");
            sb.AppendLine("  corner is cut off.");
            sb.AppendLine("- It has to stay readable at 32 pixels. No fine detail, no thin lines, no clutter.");
            sb.AppendLine("- Solid flat background — a colour, or plain white — with strong contrast against the");
            sb.AppendLine("  emblem. Flat either way: no gradient, no vignette, no texture.");
            sb.AppendLine("- NO text, NO lettering, NO words anywhere in the image.");
        }
        else
        {
            var headline = (channel.Identity.BannerHeadline ?? "").Trim();

            sb.AppendLine($"WIDE 16:9 banner artwork, {BrandImage.BannerWidth}x{BrandImage.BannerHeight}.");
            sb.AppendLine("- YouTube crops this HARD, and in BOTH directions. The only part every viewer sees is a");
            sb.AppendLine("  band across the middle: the centre 60% of the WIDTH and just 29% of the HEIGHT.");
            sb.AppendLine("  Concretely, the visible box STARTS one fifth of the way in from the left edge and ENDS");
            sb.AppendLine("  one fifth in from the right; the outer fifth on each side is thrown away, as is the");
            sb.AppendLine("  top third and the bottom third. Everything outside shows on a desktop or a TV only.");
            sb.AppendLine("- Composing this like an ordinary 16:9 picture is THE mistake to avoid: a figure drawn at");
            sb.AppendLine("  full height loses its head and its feet, and the picture that is left means nothing.");
            sb.AppendLine("  Design the middle band first, as a wide short letterbox strip, and treat the rest as");
            sb.AppendLine("  surroundings.");
            sb.AppendLine("- FILL THE FRAME even so. Above and below the band keep drawing the same world, quieter,");
            sb.AppendLine("  so the art is whole on a TV and loses nothing when it is cropped down to the band.");

            if (headline.Length > 0)
            {
                sb.AppendLine("- Lay a SOLID WHITE horizontal bar across that middle band, running from the left edge");
                sb.AppendLine("  to roughly three fifths of the way across. The bar is the only place text may go.");
                sb.AppendLine("- On the white bar, letter EXACTLY this and nothing else:");
                sb.AppendLine();
                sb.AppendLine($"      {headline}");
                sb.AppendLine();
                sb.AppendLine("  Heavy black bold sans-serif, capitalised exactly as written above, on one or two");
                sb.AppendLine("  lines, left-aligned and vertically centred on the bar. Read it back letter by letter");
                sb.AppendLine("  before you finish — a misspelling here is the whole image wasted.");
                sb.AppendLine("- NO other words anywhere: no channel name, no second slogan, no labels on the art, no");
                sb.AppendLine("  watermark, no subscribe button.");
                sb.AppendLine("- Let the artwork BREAK INTO the right-hand end of the white bar and overlap it, so the");
                sb.AppendLine("  bar reads as a label laid over a scene rather than a stripe ruled across one.");
            }
            else
            {
                sb.AppendLine("- Best composition is a ROW of several recurring subjects along that band, evenly");
                sb.AppendLine("  spaced and similar in size — a set of the things this channel is about. Not one small");
                sb.AppendLine("  vignette in the middle of a white field.");
                sb.AppendLine("- Keep it calm and even: this sits BEHIND the channel name and avatar, so nothing");
                sb.AppendLine("  should fight for attention or crowd the dead centre.");
                sb.AppendLine("- NO text, NO lettering, NO words anywhere in the image.");
            }
        }

        // Per-channel override last, so it wins any conflict with the generic rules above.
        var extra = kind == BrandImage.Avatar
            ? channel.Identity.AvatarPrompt
            : channel.Identity.BannerPrompt;
        if (!string.IsNullOrWhiteSpace(extra))
        {
            sb.AppendLine();
            sb.AppendLine("=== SPECIFIC DIRECTION FOR THIS CHANNEL (overrides the above on any conflict) ===");
            sb.AppendLine(extra.Trim());
        }

        sb.AppendLine();
        sb.Append($"Target size {BrandImage.SizeLabel(kind)}.");
        return sb.ToString();
    }

    /// <summary>
    /// The channel's look for BRAND art, which is not the same input the frame prompt uses.
    /// <para>
    /// <c>visualStyle</c> is preferred because it describes only the look. The <c>.image.md</c>
    /// rule is a per-beat frame brief — it carries narration, beat-count and aspect-ratio
    /// instructions that are wrong here — so it is used only as a fallback, and then it is scoped
    /// down to its styling.
    /// </para>
    /// </summary>
    private static string StyleRule(Channel channel)
    {
        if (!string.IsNullOrWhiteSpace(channel.VisualStyle))
            return $"Draw in this style: {channel.VisualStyle.Trim()}";

        if (!string.IsNullOrWhiteSpace(channel.ImageInstructions))
            return new StringBuilder()
                .AppendLine("=== DRAWING STYLE ONLY ===")
                .AppendLine("The notes below were written to brief individual VIDEO FRAMES. Take ONLY the look from")
                .AppendLine("them — line quality, colour palette, shapes, level of detail. IGNORE everything they say")
                .AppendLine("about scripts, narration, beats, how many images to make, aspect ratio, empty space and")
                .AppendLine("composition: this is channel artwork, not a frame from an episode.")
                .AppendLine()
                .AppendLine(channel.ImageInstructions.Trim())
                .ToString();

        return "Simple, bold, high-contrast illustration.";
    }
}
