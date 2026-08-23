namespace YtPipeline.Core.Models;

/// <summary>
/// The two pieces of channel art YouTube asks for, with the shape it actually wants each in.
/// </summary>
public static class BrandImage
{
    public const string Avatar = "avatar";
    public const string Banner = "banner";

    /// <summary>
    /// Validates and lowercases a kind. Anything else throws — this value reaches a file path, so
    /// it must never be taken on trust from a route segment.
    /// </summary>
    public static string Normalize(string? kind)
    {
        var k = (kind ?? "").Trim().ToLowerInvariant();
        return k is Avatar or Banner
            ? k
            : throw new ArgumentException($"Unknown brand image '{kind}'. Expected '{Avatar}' or '{Banner}'.");
    }

    /// <summary>
    /// Aspect to request from the image backend, in the vocabulary its CLI actually accepts.
    /// <para>
    /// gflow takes NAMES (<c>landscape, 4x3, square, 3x4, portrait</c>) — not ratios. Passing
    /// "1:1"/"16:9" made it exit 2 with "invalid choice" on every call, and because the router
    /// falls back to the standby provider on failure, the art still appeared and the breakage
    /// stayed invisible: every brand image was quietly being drawn by codex.
    /// </para>
    /// </summary>
    public static string Aspect(string kind) => Normalize(kind) == Avatar ? "square" : "landscape";

    /// <summary>The pixel size YouTube wants uploaded — stated in the UI and aimed at in the prompt.</summary>
    public static string SizeLabel(string kind) => Normalize(kind) == Avatar ? "800x800" : "2048x1152";
}
