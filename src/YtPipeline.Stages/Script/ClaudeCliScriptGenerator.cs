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
    SettingsStore settings,
    ILogger<ClaudeCliScriptGenerator> logger) : IScriptGenerator
{
    /// <summary>
    /// Last-resort pace when neither the channel nor settings offer anything, used only to convert
    /// target minutes into a word range.
    /// </summary>
    private const int WordsPerMinuteLow = 130;
    private const int WordsPerMinuteHigh = 150;

    /// <summary>How many times to ask Claude to expand a short script before giving up.</summary>
    private const int MaxExpandAttempts = 2;

    /// <summary>
    /// Word budget for one script, derived from the channel's <c>videoLengthTargetMinutes</c>.
    /// </summary>
    /// <param name="MinMinutes">Shortest acceptable runtime.</param>
    /// <param name="MaxMinutes">Longest acceptable runtime.</param>
    /// <param name="Min">Lower bound of the word range (<c>MinMinutes × 130</c>).</param>
    /// <param name="Max">Upper bound of the word range (<c>MaxMinutes × 150</c>).</param>
    /// <param name="Floor">
    /// Acceptance floor that triggers the expand retry — 8% under <paramref name="Min"/> so a
    /// borderline-passing script isn't regenerated for the sake of a few words.
    /// </param>
    private readonly record struct WordTarget(
        int MinMinutes, int MaxMinutes, int Min, int Max, int Floor)
    {
        /// <summary>True when the channel asked for a spread rather than one length.</summary>
        public bool IsRange => MaxMinutes > MinMinutes;

        /// <summary>Human-readable runtime, e.g. "3-5 minutes" or "4 minutes".</summary>
        public string MinutesLabel => IsRange ? $"{MinMinutes}-{MaxMinutes} minutes" : $"{MinMinutes} minutes";

        /// <summary>Roughly how many beats the budget implies at 4–9 words per beat.</summary>
        public int ApproxBeats => Min / 6;

        /// <summary>Upper-bound beat estimate — what the image stage would actually have to make.</summary>
        public int ApproxBeatsMax => Max / 6;

        public static WordTarget For(Channel channel, double defaultWpm = WordsPerMinuteLow)
        {
            // Guard against missing or nonsensical channel values: a 0 would ask Claude for a
            // zero-word script, and an absurdly high one would burn a whole day of images.
            var lo = Math.Clamp(
                channel.VideoLengthMinMinutes > 0
                    ? channel.VideoLengthMinMinutes
                    : channel.VideoLengthTargetMinutes, 1, 30);
            var hi = Math.Clamp(
                channel.VideoLengthMaxMinutes > 0 ? channel.VideoLengthMaxMinutes : lo, 1, 30);
            if (hi < lo) hi = lo;

            // Prefer the rate actually measured for this channel's voice. The 130-150 band is a
            // fallback for a voice that hasn't been heard yet — it was the whole cause of a
            // 3-minute target producing a 2-minute video on a 215 wpm voice.
            int min, max;
            if (channel.Voice?.WordsPerMinute is { } measured && measured >= 60 && measured <= 320)
            {
                // A single measured rate, with a small band so the script isn't asked to hit an
                // exact word count.
                min = (int)(lo * measured * 1.00);
                max = (int)(hi * measured * 1.05);
            }
            else
            {
                // Never measured this voice — use the configured starting assumption.
                var wpm = Math.Clamp(defaultWpm, 60, 320);
                min = (int)(lo * wpm);
                max = (int)(hi * wpm * 1.05);
            }

            return new WordTarget(lo, hi, min, max, (int)(min * 0.92));
        }
    }

    public async Task<ScriptDocument> GenerateAsync(Channel channel, string topic, CancellationToken ct)
    {
        var target = WordTarget.For(channel, settings.Current.DefaultWordsPerMinute);
        logger.LogInformation(
            "Script target for '{Channel}': {Minutes} → {Min}–{Max} words (~{Beats}-{BeatsMax} beats).",
            channel.Id, target.MinutesLabel, target.Min, target.Max,
            target.ApproxBeats, target.ApproxBeatsMax);

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

        // The typed topic is the title, full stop. The prompt asks for it verbatim, but asking is
        // not a guarantee — and the expand path above swaps in a whole fresh Claude response with
        // its own invented title, so this has to be the last word. Everything downstream (publish
        // title, YouTube/Facebook upload, thumbnail and music prompts) reads script.Title.
        var requested = (topic ?? "").Trim();
        if (requested.Length > 0 && !string.Equals(script.Title?.Trim(), requested, StringComparison.Ordinal))
        {
            logger.LogInformation(
                "Title kept as typed: '{Topic}' (model returned '{Returned}').", requested, script.Title);
            script.Title = requested;
        }

        logger.LogInformation("Script '{Title}' — {Beats} beats, {Words} words.", script.Title, script.Beats.Count, words);
        return script;
    }

    /// <summary>
    /// Writes the channel's identity and its chosen structure into the prompt.
    /// </summary>
    /// <remarks>
    /// Sections are handed over as guidance to adapt, not a checklist to satisfy. A rigid reading
    /// forces every topic through every section — a template with a metallurgy step would give a
    /// video about weaving leaves a furnace it has no use for.
    /// </remarks>
    private static void AppendChannelContext(StringBuilder sb, Channel channel)
    {
        var identity = new (string Label, string? Value)[]
        {
            ("Voice and persona", channel.Persona),
            ("Tone", channel.Tone),
            ("Audience", channel.Audience),
            ("Delivery notes", channel.VoiceNotes),
        }.Where(p => !string.IsNullOrWhiteSpace(p.Value)).ToList();

        if (identity.Count > 0)
        {
            sb.AppendLine("=== CHANNEL VOICE (write as though this channel is speaking) ===");
            foreach (var (label, value) in identity) sb.AppendLine($"- {label}: {value!.Trim()}");
            sb.AppendLine();
        }

        // Sent with every template, so rotating structures never costs the channel its voice.
        if (!string.IsNullOrWhiteSpace(channel.ScriptVoiceRules))
        {
            sb.AppendLine("=== SENTENCE RULES (these matter more than any section) ===");
            sb.AppendLine(channel.ScriptVoiceRules.Trim());
            sb.AppendLine();
        }

        var sections = SelectStructure(channel);
        if (sections.Count == 0) return;

        sb.AppendLine("=== STRUCTURE (follow this shape) ===");
        sb.AppendLine("Sections in order. Word budgets are per section and should roughly hold, since");
        sb.AppendLine("together they add up to the total above. Any section that genuinely does not fit");
        sb.AppendLine("this topic should be adapted to the topic's own equivalent, or dropped — do NOT");
        sb.AppendLine("invent content just to fill a section that makes no sense here, and redistribute");
        sb.AppendLine("its words across the sections you keep.");
        sb.AppendLine();
        foreach (var s in sections) sb.AppendLine(s.Trim()).AppendLine();
    }

    /// <summary>
    /// The structure to write to: the least recently used template if the channel has any,
    /// otherwise its single <see cref="Channel.ScriptStructure"/>.
    /// </summary>
    public static IReadOnlyList<string> SelectStructure(Channel channel) =>
        PickTemplate(channel)?.Sections ?? (IReadOnlyList<string>)channel.ScriptStructure;

    /// <summary>
    /// Least-recently-used template, so consecutive videos aren't built to the same skeleton.
    /// Never-used templates come first, so a newly added one is tried before anything repeats.
    /// </summary>
    public static ScriptTemplate? PickTemplate(Channel channel) =>
        channel.ScriptTemplates is { Count: > 0 } list
            ? list.OrderBy(t => t.LastUsedUtc ?? DateTime.MinValue).First()
            : null;

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
        sb.AppendLine($"At a typical voiceover pace of {WordsPerMinuteLow}–{WordsPerMinuteHigh} words per minute, that is a video of about {target.MinutesLabel}.");
        sb.AppendLine($"A script under {target.Floor} words will be REJECTED and you will be asked to expand it anyway — so write the full length now.");
        sb.AppendLine($"Do NOT overshoot either: staying under {target.Max} words matters just as much, because every beat becomes a generated image.");
        sb.AppendLine();

        sb.AppendLine("Write the spoken narration for a YouTube video on the channel described below.");
        sb.AppendLine("Do NOT name the channel, invent a host name, or address the audience as subscribers —");
        sb.AppendLine("the channel context is there to set voice and structure, not to be mentioned aloud.");
        sb.AppendLine();
        sb.AppendLine($"TOPIC: {topic}");
        sb.AppendLine();

        // The channel's identity and format used to be collected here and then never sent, so every
        // channel produced the same generic explainer regardless of how carefully it was configured.
        AppendChannelContext(sb, channel);

        sb.AppendLine("Requirements:");
        sb.AppendLine($"- LENGTH (MANDATORY — scripts under {target.Floor} words will be rejected): the narration must total {target.Min}–{target.Max} spoken words. Do NOT write a short script. Expand every section with detail, examples, context, and explanation until you hit the word count.");
        sb.AppendLine($"- PACING (CRITICAL — this must be a FAST-paced edit): break the narration into VERY short beats. Each beat should be only about 1.5 to 2.5 seconds of speech — roughly 4 to 9 spoken words, i.e. a single short phrase, NOT a whole sentence. NEVER let a beat exceed ~10 words. Split long sentences into several beats at natural pause points (commas, conjunctions, clauses). There is exactly one image per beat, so the more beats you make, the more the visuals change — aim for a brand-new visual every couple of seconds. A {target.Min}–{target.Max} word script should therefore have roughly {target.ApproxBeats}-{target.ApproxBeatsMax} beats.");
        sb.AppendLine("- The beats' narration, read in order start to finish, must read as the complete, natural voiceover (they are concatenated and spoken as-is).");
        sb.AppendLine("- Open with a hook, build the topic clearly in the middle, and end with a short wrap-up.");
        // The typed topic IS the title. Claude used to be asked for a "strong, clickable title" and
        // duly rewrote "Why a Light Switch can kill you" into something else entirely — which is not
        // its call to make. Write the script to the given title instead of inventing a new one.
        sb.AppendLine($"- TITLE: the video is already titled \"{topic}\". Copy that EXACTLY into the title field — same wording, same order, same capitalisation. Do NOT reword, extend, shorten, add a subtitle or parenthetical, or make it more clickable. Write the script so it delivers on that exact title.");
        sb.AppendLine("- Write a useful YouTube description.");
        sb.AppendLine();
        sb.AppendLine("Return ONLY a single JSON object (no markdown, no prose, no code fences) with this exact shape:");
        sb.AppendLine($$"""
        {
          "title": "the TOPIC above, copied verbatim — not a rewritten or improved version of it",
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
