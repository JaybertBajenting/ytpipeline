using System.Globalization;
using System.Text.RegularExpressions;
using YtPipeline.Core.Models;
using YtPipeline.Stages.Voice;

namespace YtPipeline.Stages.Video;

/// <summary>Replaces estimated countdown chapters with the exact narrated boundaries.</summary>
public static partial class ChapterTimestamps
{
    public static string? TryAlign(ScriptDocument script, AudioAlignment alignment)
    {
        var text = script.NarrationForVoice();
        if (alignment.Text != text || alignment.CharStartTimes.Count != text.Length) return null;
        var chapters = ChapterLine().Matches(script.Description);
        var markers = Countdown().Matches(text);
        if (chapters.Count < 2 || markers.Count != chapters.Count) return null;
        var seconds = new List<int>();
        var previousNumber = int.MaxValue;
        foreach (Match marker in markers)
        {
            var token = marker.Groups[1].Value.ToLowerInvariant();
            var number = token switch
            {
                "ten" => 10, "nine" => 9, "eight" => 8, "seven" => 7,
                "six" => 6, "five" => 5, "four" => 4, "three" => 3,
                "two" => 2, "one" => 1, _ => int.Parse(token, CultureInfo.InvariantCulture)
            };
            if (previousNumber != int.MaxValue && number != previousNumber - 1) return null;
            previousNumber = number;
            var time = alignment.CharStartTimes[marker.Index];
            if (!double.IsFinite(time) || time < 0) return null;
            var second = seconds.Count == 0 ? 0 : (int)Math.Floor(time);
            if (seconds.Count > 0 && second <= seconds[^1]) return null;
            seconds.Add(second);
        }
        var index = 0;
        return ChapterLine().Replace(script.Description, match =>
        {
            var second = seconds[index++];
            return $"{second / 60:00}:{second % 60:00}{match.Groups[1].Value}{match.Groups[2].Value}";
        });
    }

    [GeneratedRegex(@"^\d+:\d{2}(?::\d{2})?([ \t]+)([^\r\n]+)(?=\r?$)", RegexOptions.Multiline)]
    private static partial Regex ChapterLine();

    [GeneratedRegex(@"\bNumber\s+(ten|nine|eight|seven|six|five|four|three|two|one|10|[1-9])\b", RegexOptions.IgnoreCase)]
    private static partial Regex Countdown();
}
