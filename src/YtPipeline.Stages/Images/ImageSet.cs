using System.Text.RegularExpressions;

namespace YtPipeline.Stages.Images;

/// <summary>Helpers for the variable set of PNGs Codex writes for a run.</summary>
public static partial class ImageSet
{
    /// <summary>Lists the run's PNGs, natural-sorted so beat2 precedes beat10.</summary>
    public static IReadOnlyList<string> List(string imagesDir)
    {
        if (!Directory.Exists(imagesDir)) return Array.Empty<string>();
        return Directory.EnumerateFiles(imagesDir, "*.png")
            .OrderBy(Path.GetFileName, NaturalComparer.Instance)
            .ToList();
    }

    /// <summary>Parses the beat index encoded as <c>beat&lt;NN&gt;</c> in a filename, if present.</summary>
    public static int? BeatIndexOf(string path)
    {
        var m = BeatPattern().Match(Path.GetFileName(path));
        return m.Success && int.TryParse(m.Groups[1].Value, out var n) ? n : null;
    }

    [GeneratedRegex(@"beat[_-]?(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex BeatPattern();

    private sealed class NaturalComparer : IComparer<string?>
    {
        public static readonly NaturalComparer Instance = new();

        public int Compare(string? a, string? b)
        {
            a ??= ""; b ??= "";
            int i = 0, j = 0;
            while (i < a.Length && j < b.Length)
            {
                if (char.IsDigit(a[i]) && char.IsDigit(b[j]))
                {
                    int si = i, sj = j;
                    while (i < a.Length && char.IsDigit(a[i])) i++;
                    while (j < b.Length && char.IsDigit(b[j])) j++;
                    var na = a.AsSpan(si, i - si).TrimStart('0');
                    var nb = b.AsSpan(sj, j - sj).TrimStart('0');
                    if (na.Length != nb.Length) return na.Length - nb.Length;
                    int cmp = na.SequenceCompareTo(nb);
                    if (cmp != 0) return cmp;
                }
                else
                {
                    int cmp = char.ToLowerInvariant(a[i]).CompareTo(char.ToLowerInvariant(b[j]));
                    if (cmp != 0) return cmp;
                    i++; j++;
                }
            }
            return (a.Length - i) - (b.Length - j);
        }
    }
}
