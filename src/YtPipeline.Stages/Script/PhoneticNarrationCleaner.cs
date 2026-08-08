using System.Text;
using Microsoft.Extensions.Logging;
using YtPipeline.Core.Models;
using YtPipeline.Core.Services;
using YtPipeline.Stages.Process;

namespace YtPipeline.Stages.Script;

/// <summary>
/// Post-script pass that protects the voiceover from TTS mispronunciation. Claude scans every
/// beat's narration for words ElevenLabs tends to get wrong (proper names, places, historical /
/// scientific / foreign terms, acronyms, unusual spellings) and respells ONLY those words
/// phonetically in plain English. The respelling is stored on <see cref="ScriptBeat.NarrationSpoken"/>
/// — the human-readable <see cref="ScriptBeat.Narration"/> is left untouched for the UI.
///
/// Costs ZERO extra ElevenLabs credits: the fix happens in text before the single TTS call, so the
/// audio is generated once with the corrected spellings (no regenerate-and-compare retry loop).
/// </summary>
public sealed class PhoneticNarrationCleaner(
    ProcessRunner process,
    ClaudeOptions options,
    ILogger<PhoneticNarrationCleaner> logger)
{
    private sealed class Fix { public int Index { get; set; } public string Spoken { get; set; } = ""; }
    private sealed class FixResponse { public List<Fix> Fixes { get; set; } = new(); }

    /// <summary>
    /// Fills in <see cref="ScriptBeat.NarrationSpoken"/> for beats that need it. Mutates and returns
    /// the same script. Never throws on a bad model response — if the pass fails, the narration is
    /// simply voiced as written (the original behaviour), so this can never block a run.
    /// </summary>
    public async Task<ScriptDocument> ApplyAsync(ScriptDocument script, CancellationToken ct)
    {
        if (script.Beats.Count == 0) return script;

        try
        {
            var prompt = BuildPrompt(script);
            // Prompt piped via stdin (claude -p reads stdin) — robust for long scripts that would
            // overflow the Windows command-line length limit if passed as an argument.
            var result = await process.RunOrThrowAsync(options.Executable, options.Args, stdin: prompt, ct: ct);

            var json = ClaudeCliScriptGenerator.ExtractFirstJsonObject(result.StdOut);
            if (json is null)
            {
                logger.LogWarning("Phonetic pass: no JSON in Claude output — voicing narration as written.");
                return script;
            }

            var response = Json.Deserialize<FixResponse>(json);
            var byIndex = script.Beats.ToDictionary(b => b.Index);
            int applied = 0;
            foreach (var fix in response.Fixes)
            {
                if (string.IsNullOrWhiteSpace(fix.Spoken)) continue;
                if (!byIndex.TryGetValue(fix.Index, out var beat)) continue;
                // Only record a respelling that actually differs from what is written.
                if (fix.Spoken.Trim() == beat.Narration.Trim()) continue;
                beat.NarrationSpoken = fix.Spoken.Trim();
                applied++;
            }

            logger.LogInformation("Phonetic pass: respelled {Count} beat(s) for cleaner TTS pronunciation.", applied);
        }
        catch (Exception ex)
        {
            // Never let pronunciation cleanup break a run — fall back to voicing as written.
            logger.LogWarning("Phonetic pass failed ({Msg}) — voicing narration as written.", ex.Message);
        }

        return script;
    }

    private static string BuildPrompt(ScriptDocument script)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are preparing narration for a text-to-speech engine (ElevenLabs). Some words get mispronounced by TTS: proper names, place names, historical/scientific terms, foreign words, acronyms, Roman numerals, and unusually spelled words.");
        sb.AppendLine();
        sb.AppendLine("Below is a numbered list of narration beats. For EACH beat that contains such a word, rewrite that beat's full text with ONLY the hard word(s) respelled phonetically in plain English so a TTS engine says them correctly.");
        sb.AppendLine();
        sb.AppendLine("Respelling rules:");
        sb.AppendLine("- Use plain-English syllables with hyphens and CAPS for the stressed syllable. Examples: \"Mesopotamia\" -> \"Mess-oh-poh-TAY-mee-uh\"; \"Nietzsche\" -> \"NEE-chuh\"; \"Goethe\" -> \"GUR-tuh\"; \"Versailles\" -> \"vair-SY\"; \"GIF\" -> \"jif\".");
        sb.AppendLine("- Keep EVERY other word exactly the same, in the same order, with the same punctuation and capitalization. Do NOT reword, add, or remove anything.");
        sb.AppendLine("- Do NOT touch common, easily-pronounced English words.");
        sb.AppendLine("- If a beat has no risky words, OMIT it from your output entirely.");
        sb.AppendLine();
        sb.AppendLine("=== BEATS ===");
        foreach (var beat in script.Beats.OrderBy(b => b.Index))
            sb.AppendLine($"[{beat.Index}] {beat.Narration.Trim()}");
        sb.AppendLine();
        sb.AppendLine("Return ONLY a single JSON object (no markdown, no prose, no code fences) of this exact shape, including ONLY the beats you changed:");
        sb.AppendLine("""
        {
          "fixes": [
            { "index": 0, "spoken": "the full beat text with hard words respelled phonetically" }
          ]
        }
        """);
        sb.AppendLine("If no beats need changes, return {\"fixes\": []}.");
        return sb.ToString();
    }
}
