using System.Text;
using System.Text.RegularExpressions;

namespace YtPipeline.Core.Services;

/// <summary>
/// Builds the attribution block for the background music actually used in a video.
/// <para>
/// The free-use libraries these tracks come from require the artist and track title in the
/// video description; without it a Content ID claim can land on the upload. That makes the
/// credit part of publishing, not an optional nicety — so it's composed from the tracks the
/// video stage really mixed in rather than left to be remembered by hand.
/// </para>
/// </summary>
public static partial class MusicCredit
{
    /// <summary>
    /// Turns music file paths into "artist - title" credit lines.
    /// <para>
    /// Library filenames look like <c>massobeats - remedy (freetouse.com).mp3</c>. The artist and
    /// title are kept; the trailing source tag in parentheses is dropped, because the required
    /// credit is the artist and song name, not the download host.
    /// </para>
    /// </summary>
    public static IReadOnlyList<string> Lines(IEnumerable<string> musicPaths)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var lines = new List<string>();

        foreach (var path in musicPaths ?? [])
        {
            if (string.IsNullOrWhiteSpace(path)) continue;

            var name = Path.GetFileNameWithoutExtension(path);
            if (string.IsNullOrWhiteSpace(name)) continue;

            // Drop any trailing "(...)" source tags, then tidy the separator spacing.
            var credit = TrailingParenthetical().Replace(name, "").Trim();
            credit = Whitespace().Replace(credit, " ");

            if (credit.Length > 0 && seen.Add(credit)) lines.Add(credit);
        }

        return lines;
    }

    /// <summary>
    /// Appends a music credit section to a description. Returns the description unchanged when
    /// there are no tracks, and won't add the section twice if it's already present.
    /// </summary>
    public static string Append(string? description, IEnumerable<string> musicPaths)
    {
        var lines = Lines(musicPaths);
        if (lines.Count == 0) return description ?? "";

        var body = description ?? "";
        if (body.Contains(Heading, StringComparison.OrdinalIgnoreCase)) return body;

        var sb = new StringBuilder(body.TrimEnd());
        sb.AppendLine().AppendLine().AppendLine(Heading);
        foreach (var l in lines) sb.AppendLine(l);
        return sb.ToString().TrimEnd();
    }

    /// <summary>Section marker, also used to avoid appending the block twice.</summary>
    public const string Heading = "Music:";

    [GeneratedRegex(@"\s*\([^)]*\)\s*$")]
    private static partial Regex TrailingParenthetical();

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex Whitespace();
}
