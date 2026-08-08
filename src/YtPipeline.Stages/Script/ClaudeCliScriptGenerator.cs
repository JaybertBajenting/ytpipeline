using System.Text;
using FluentValidation;
using Microsoft.Extensions.Logging;
using YtPipeline.Core.Abstractions;
using YtPipeline.Core.Models;
using YtPipeline.Core.Services;
using YtPipeline.Stages.Process;

namespace YtPipeline.Stages.Script;

public sealed class ClaudeOptions
{
    /// <summary>Executable name or full path. Resolved on PATH if not absolute.</summary>
    public string Executable { get; set; } = "claude";

    /// <summary>
    /// Args placed before the prompt. Runs claude in non-interactive print mode on
    /// Opus 4.8 at high effort. Override in appsettings (e.g. <c>--effort xhigh</c> or a different model).
    /// </summary>
    public List<string> Args { get; set; } = new() { "-p", "--model", "claude-opus-4-8", "--effort", "high" };
}

/// <summary>
/// Stage 1. Builds a prompt from channel context + topic, shells out to the Claude Code CLI
/// asking for JSON only, then extracts + validates a <see cref="ScriptDocument"/>.
/// </summary>
public sealed class ClaudeCliScriptGenerator(
    ProcessRunner process,
    IValidator<ScriptDocument> validator,
    ClaudeOptions options,
    ILogger<ClaudeCliScriptGenerator> logger) : IScriptGenerator
{
    /// <summary>Typical voiceover pace, used to convert target minutes into a word range.</summary>
    private const int WordsPerMinuteLow = 130;
    private const int WordsPerMinuteHigh = 150;

    /// <summary>How many times to ask Claude to expand a short script before giving up.</summary>
    private const int MaxExpandAttempts = 2;

    /// <summary>
    /// Word budget for one script, derived from the channel's <c>videoLengthTargetMinutes</c>.
    /// </summary>
    /// <param name="Minutes">The channel's requested runtime.</param>
    /// <param name="Min">Lower bound of the target range (<c>Minutes × 130</c>).</param>
    /// <param name="Max">Upper bound of the target range (<c>Minutes × 150</c>).</param>
    /// <param name="Floor">
    /// Acceptance floor that triggers the expand retry — 8% under <paramref name="Min"/> so a
    /// borderline-passing script isn't regenerated for the sake of a few words.
    /// </param>
    private readonly record struct WordTarget(int Minutes, int Min, int Max, int Floor)
    {
        /// <summary>Roughly how many beats this budget implies at 4–9 words per beat.</summary>
        public int ApproxBeats => Min / 6;

        public static WordTarget For(Channel channel)
        {
            // Guard against a missing or nonsensical channel value: a 0 here would ask Claude
            // for a zero-word script, and an absurdly high one would burn a whole day of images.
            var minutes = Math.Clamp(channel.VideoLengthTargetMinutes, 1, 30);
            var min = minutes * WordsPerMinuteLow;
            var max = minutes * WordsPerMinuteHigh;
            return new WordTarget(minutes, min, max, (int)(min * 0.92));
        }
    }

    public async Task<ScriptDocument> GenerateAsync(Channel channel, string topic, CancellationToken ct)
    {
        var target = WordTarget.For(channel);
        logger.LogInformation(
            "Script target for '{Channel}': {Minutes} min → {Min}–{Max} words (~{Beats} beats).",
            channel.Id, target.Minutes, target.Min, target.Max, target.ApproxBeats);

        var prompt = BuildPrompt(channel, topic);
        var args = new List<string>(options.Args) { prompt };

        var result = await process.RunOrThrowAsync(options.Executable, args, ct: ct);
        var script = await ExtractAndValidate(result.StdOut, ct);

        NormalizeBeatIndices(script);
        if (string.IsNullOrWhiteSpace(script.FullNarration))
            script.FullNarration = script.ComposeNarration();

        // ── Option 2: retry-expand fallback ───────────────────────────────────
        // Count words on the text that will actually be voiced. If Claude ignored the
        // length instruction despite the hardened prompt, send a targeted expand request
        // (beats-only, no image prompts) instead of a full regeneration — saves tokens.
        var words = CountWords(script.NarrationForVoice());
        if (words < target.Floor)
        {
            logger.LogWarning(
                "Script narration is only {Words} words (target {Min}–{Max}). " +
                "Requesting expansion (up to {Max2} attempt(s))…",
                words, target.Min, target.Max, MaxExpandAttempts);

            for (int attempt = 1; attempt <= MaxExpandAttempts; attempt++)
            {
                var expandPrompt = BuildExpandPrompt(script, words, target);
                var expandArgs = new List<string>(options.Args) { expandPrompt };

                ProcessResult expandResult;
                try
                {
                    expandResult = await process.RunOrThrowAsync(options.Executable, expandArgs, ct: ct);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    logger.LogWarning("Expand attempt {Attempt} failed ({Msg}) — keeping current script.", attempt, ex.Message);
                    break;
                }

                ScriptDocument expanded;
                try { expanded = await ExtractAndValidate(expandResult.StdOut, ct); }
                catch (Exception ex)
                {
                    logger.LogWarning("Expand attempt {Attempt} produced invalid JSON ({Msg}) — keeping current script.", attempt, ex.Message);
                    break;
                }

                NormalizeBeatIndices(expanded);
                if (string.IsNullOrWhiteSpace(expanded.FullNarration))
                    expanded.FullNarration = expanded.ComposeNarration();

                var newWords = CountWords(expanded.NarrationForVoice());
                logger.LogInformation(
                    "Expand attempt {Attempt}: {Old} → {New} words.", attempt, words, newWords);

                // Only accept if Claude actually made it longer.
                if (newWords > words)
                {
                    script = expanded;
                    words = newWords;
                }

                if (words >= target.Floor) break;
            }

            if (words < target.Floor)
                logger.LogWarning(
                    "Script still short ({Words} words) after expansion — proceeding with what we have.", words);
        }

        logger.LogInformation("Script '{Title}' — {Beats} beats, {Words} words.", script.Title, script.Beats.Count, words);
        return script;
    }

    /// <summary>Extracts JSON from raw Claude output, parses and validates it.</summary>
    private async Task<ScriptDocument> ExtractAndValidate(string raw, CancellationToken ct)
    {
        var script = ExtractScript(raw);
        var validation = await validator.ValidateAsync(script, ct);
        if (!validation.IsValid)
            throw new InvalidOperationException(
                "Generated script failed validation:\n" +
                string.Join('\n', validation.Errors.Select(e => $"  - {e.PropertyName}: {e.ErrorMessage}")));
        return script;
    }

    /// <summary>Public so the CLI <c>--dry-run</c> can print the prompt without calling claude.</summary>
    public string BuildPrompt(Channel channel, string topic)
    {
        var target = WordTarget.For(channel);
        var sb = new StringBuilder();

        // ── LENGTH: stated first so Claude reads it before anything else ──────
        sb.AppendLine("=== WORD COUNT — READ THIS FIRST ===");
        sb.AppendLine($"The narration MUST be {target.Min}–{target.Max} words. This is a HARD requirement, not a guideline.");
        sb.AppendLine($"At a typical voiceover pace of {WordsPerMinuteLow}–{WordsPerMinuteHigh} words per minute, that is a video of about {target.Minutes} minute(s).");
        sb.AppendLine($"A script under {target.Floor} words will be REJECTED and you will be asked to expand it anyway — so write the full length now.");
        sb.AppendLine($"Do NOT overshoot either: staying under {target.Max} words matters just as much, because every beat becomes a generated image.");
        sb.AppendLine();

        sb.AppendLine("Write a script for a YouTube video. No persona, no host character, no channel branding, no narrator name — just a clear, engaging, well-paced spoken script about the topic itself.");
        sb.AppendLine();
        sb.AppendLine($"TOPIC: {topic}");
        sb.AppendLine();
        sb.AppendLine("Requirements:");
        sb.AppendLine($"- LENGTH (MANDATORY — scripts under {target.Floor} words will be rejected): the narration must total {target.Min}–{target.Max} spoken words. Do NOT write a short script. Expand every section with detail, examples, context, and explanation until you hit the word count.");
        sb.AppendLine($"- PACING (CRITICAL — this must be a FAST-paced edit): break the narration into VERY short beats. Each beat should be only about 1.5 to 2.5 seconds of speech — roughly 4 to 9 spoken words, i.e. a single short phrase, NOT a whole sentence. NEVER let a beat exceed ~10 words. Split long sentences into several beats at natural pause points (commas, conjunctions, clauses). There is exactly one image per beat, so the more beats you make, the more the visuals change — aim for a brand-new visual every couple of seconds. A {target.Min}–{target.Max} word script should therefore have roughly {target.ApproxBeats} beats.");
        sb.AppendLine("- The beats' narration, read in order start to finish, must read as the complete, natural voiceover (they are concatenated and spoken as-is).");
        sb.AppendLine("- Open with a hook, build the topic clearly in the middle, and end with a short wrap-up.");
        sb.AppendLine("- Write a strong, clickable title (<= 100 chars) and a useful YouTube description.");
        sb.AppendLine();
        sb.AppendLine("Return ONLY a single JSON object (no markdown, no prose, no code fences) with this exact shape:");
        sb.AppendLine($$"""
        {
          "title": "string, <= 100 chars",
          "description": "string, a few sentences for the YouTube description",
          "tags": ["string", "..."],
          "beats": [
            { "index": 0, "narration": "what is spoken over this beat", "imagePrompt": "what to draw for this beat" }
          ],
          "fullNarration": "the entire narration as one continuous string — MUST total {{target.Min}}–{{target.Max}} words"
        }
        """);
        sb.AppendLine("Every beat = exactly one visual idea = one image. imagePrompt is a plain description of what to draw for that beat (no art-style notes needed).");
        sb.AppendLine();
        sb.AppendLine("=== SELF-CHECK BEFORE RETURNING ===");
        sb.AppendLine($"Count the words in your fullNarration. If the count is below {target.Min}, do NOT return yet — keep writing and expanding until it reaches {target.Min}+.");
        return sb.ToString();
    }

    /// <summary>
    /// Builds a targeted expand prompt. Sends only beat narrations (not image prompts) to
    /// save tokens — Claude only needs the text to know what to expand.
    /// </summary>
    private static string BuildExpandPrompt(ScriptDocument script, int actualWords, WordTarget target)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"The script below has only {actualWords} words of narration. It must reach {target.Min}–{target.Max} words.");
        sb.AppendLine();
        sb.AppendLine("Expand it by:");
        sb.AppendLine("  • Adding new beats between existing ones to deepen thin sections");
        sb.AppendLine("  • Adding more examples, context, statistics, or explanations");
        sb.AppendLine("  • Never padding with filler — every new beat must add real substance");
        sb.AppendLine("Keep all existing beats, the same topic, and the same overall flow.");
        sb.AppendLine("Each beat must still be 4–9 words (short phrases for fast-paced editing).");
        sb.AppendLine();
        sb.AppendLine("=== CURRENT BEATS (narration only) ===");
        foreach (var beat in script.Beats.OrderBy(b => b.Index))
            sb.AppendLine($"  {beat.Index}: {beat.Narration}");
        sb.AppendLine();
        sb.AppendLine($"Return ONLY the complete updated JSON (same shape as the original). fullNarration must total {target.Min}–{target.Max} words.");
        sb.AppendLine("""
        {
          "title": "...",
          "description": "...",
          "tags": [...],
          "beats": [{ "index": 0, "narration": "...", "imagePrompt": "..." }, ...],
          "fullNarration": "complete narration as one string"
        }
        """);
        sb.AppendLine($"SELF-CHECK: count the words in fullNarration before returning. If below {target.Min}, keep expanding.");
        return sb.ToString();
    }

    private static int CountWords(string text) =>
        string.IsNullOrWhiteSpace(text) ? 0
        : text.Split([' ', '\t', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries).Length;

    /// <summary>
    /// Pulls the JSON object out of the CLI output even if it is wrapped in prose or a
    /// ```json fence — the first balanced {...} block is parsed.
    /// </summary>
    internal static ScriptDocument ExtractScript(string raw)
    {
        var json = ExtractFirstJsonObject(raw)
            ?? throw new InvalidOperationException(
                "Could not find a JSON object in the Claude CLI output. Raw output:\n" + raw);

        try
        {
            return Json.Deserialize<ScriptDocument>(json);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "Found a JSON block but failed to parse it as a ScriptDocument: " + ex.Message + "\n" + json, ex);
        }
    }

    internal static string? ExtractFirstJsonObject(string text)
    {
        int start = text.IndexOf('{');
        if (start < 0) return null;

        int depth = 0;
        bool inString = false, escape = false;
        for (int i = start; i < text.Length; i++)
        {
            char c = text[i];
            if (escape) { escape = false; continue; }
            if (c == '\\' && inString) { escape = true; continue; }
            if (c == '"') { inString = !inString; continue; }
            if (inString) continue;

            if (c == '{') depth++;
            else if (c == '}' && --depth == 0)
                return text.Substring(start, i - start + 1);
        }
        return null;
    }

    private static void NormalizeBeatIndices(ScriptDocument script)
    {
        var ordered = script.Beats.OrderBy(b => b.Index).ToList();
        for (int i = 0; i < ordered.Count; i++) ordered[i].Index = i;
        script.Beats = ordered;
    }
}
