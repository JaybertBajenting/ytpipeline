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
    /// <summary>Default hold per image when a channel doesn't set one — the original fast cut.</summary>
    private const double DefaultSecondsPerImage = 2.0;

    private readonly record struct WordTarget(
        int MinMinutes, int MaxMinutes, int Min, int Max, int Floor, int WordsPerBeat, double SecondsPerImage)
    {
        /// <summary>True when the channel asked for a spread rather than one length.</summary>
        public bool IsRange => MaxMinutes > MinMinutes;

        /// <summary>Human-readable runtime, e.g. "3-5 minutes" or "4 minutes".</summary>
        public string MinutesLabel => IsRange ? $"{MinMinutes}-{MaxMinutes} minutes" : $"{MinMinutes} minutes";

        /// <summary>Shortest acceptable beat — a little under the target, so there is some give.</summary>
        public int BeatWordsLow => Math.Max(3, (int)Math.Round(WordsPerBeat * 0.7));

        /// <summary>Longest acceptable beat.</summary>
        public int BeatWordsHigh => Math.Max(BeatWordsLow + 2, (int)Math.Round(WordsPerBeat * 1.3));

        /// <summary>Roughly how many beats the budget implies at this channel's beat length.</summary>
        public int ApproxBeats => Min / Math.Max(1, WordsPerBeat);

        /// <summary>Upper-bound beat estimate — what the image stage would actually have to make.</summary>
        public int ApproxBeatsMax => Max / Math.Max(1, WordsPerBeat);

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
            double rate;
            if (channel.Voice?.WordsPerMinute is { } measured && measured >= 60 && measured <= 320)
            {
                // A single measured rate, with a small band so the script isn't asked to hit an
                // exact word count.
                rate = measured;
                min = (int)(lo * measured * 1.00);
                max = (int)(hi * measured * 1.05);
            }
            else
            {
                // Never measured this voice — use the configured starting assumption.
                rate = Math.Clamp(defaultWpm, 60, 320);
                min = (int)(lo * rate);
                max = (int)(hi * rate * 1.05);
            }

            // How many words fit under one image. A beat IS an image, so this is what sets both
            // the cutting rhythm and the size of the image bill.
            var seconds = channel.SecondsPerImage > 0
                ? Math.Clamp(channel.SecondsPerImage, 0.8, 15)
                : DefaultSecondsPerImage;
            var wordsPerBeat = Math.Max(3, (int)Math.Round(rate * seconds / 60.0));

            return new WordTarget(lo, hi, min, max, (int)(min * 0.92), wordsPerBeat, seconds);
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
        // Actual source transcripts can exceed Windows' command-line limit. Print mode accepts
        // stdin, so the evidence arrives intact instead of being shortened or just named.
        var result = await process.RunOrThrowAsync(options.Executable, options.Args,
            workingDirectory: settings.WorkspaceRoot, stdin: prompt, ct: ct);
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
                var expandPrompt = BuildExpandPrompt(channel, script, words, target);

                ProcessResult expandResult;
                try
                {
                    expandResult = await process.RunOrThrowAsync(options.Executable, options.Args,
                        workingDirectory: settings.WorkspaceRoot, stdin: expandPrompt, ct: ct);
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
    public static ScriptTemplate? PickTemplate(Channel channel)
    {
        if (!string.IsNullOrWhiteSpace(channel.ScriptTemplateName))
            return channel.ScriptTemplates.FirstOrDefault(t =>
                       string.Equals(t.Name, channel.ScriptTemplateName, StringComparison.OrdinalIgnoreCase)
                       && t.Sections.Count > 0)
                   ?? throw new InvalidOperationException(
                       $"Channel '{channel.Id}' selected script template '{channel.ScriptTemplateName}' " +
                       "does not exist or has no sections. Correct scriptTemplateName before generating.");
        return channel.ScriptTemplates is { Count: > 0 } list
            ? list.OrderBy(t => t.LastUsedUtc ?? DateTime.MinValue).First()
            : null;
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
        var target = WordTarget.For(channel, settings.Current.DefaultWordsPerMinute);
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
        sb.Append(ReferenceStudySources.ScriptContext(channel, settings.WorkspaceRoot));

        sb.AppendLine("Requirements:");
        sb.AppendLine($"- LENGTH (MANDATORY — scripts under {target.Floor} words will be rejected): the narration must total {target.Min}–{target.Max} spoken words. Do NOT write a short script. Expand every section with detail, examples, context, and explanation until you hit the word count.");
        // Beat length follows the channel's own cutting rhythm. It used to be hardcoded at 4-9
        // words, which is right for a fast 2-second cut and wrong for a calm one — on a channel
        // that holds each picture for 5 seconds it would have doubled the image bill and made the
        // edit twice as frantic as the format calls for.
        var pace = target.SecondsPerImage <= 2.5
            ? "this must be a FAST-paced edit"
            : $"this is a MEASURED edit — each picture holds for about {target.SecondsPerImage:0.#} seconds";
        sb.AppendLine($"- PACING (CRITICAL — {pace}): break the narration into beats of about {target.WordsPerBeat} spoken words each, and keep every beat between {target.BeatWordsLow} and {target.BeatWordsHigh} words. Split at natural pause points (commas, conjunctions, clauses) rather than mid-phrase. There is exactly one image per beat, so beat length IS how long each picture stays on screen. A {target.Min}–{target.Max} word script should therefore have roughly {target.ApproxBeats}-{target.ApproxBeatsMax} beats.");
        sb.AppendLine("- SENTENCES AND BEATS ARE DIFFERENT: the beat word range sets image changes, not sentence length. Write natural connected sentences first, then split them across beats at clause boundaries. A sentence may span several beats; do not add a full stop at every cut or turn connective explanation into a string of slogans.");
        sb.AppendLine("- The beats' narration, read in order start to finish, must read as the complete, natural voiceover (they are concatenated and spoken as-is).");
        // Deferring to the channel's own structure rather than demanding a hook: a countdown
        // channel opens cold on "Number ten" by design, and a blanket "open with a hook" here
        // contradicted the sections it had just been given.
        sb.AppendLine(SelectStructure(channel).Count > 0
            ? "- Open, build and close exactly as the STRUCTURE above sets out — it overrides any general advice about how videos usually begin or end."
            : "- Open with a hook, build the topic clearly in the middle, and end with a short wrap-up.");
        // The typed topic IS the title. Claude used to be asked for a "strong, clickable title" and
        // duly rewrote "Why a Light Switch can kill you" into something else entirely — which is not
        // its call to make. Write the script to the given title instead of inventing a new one.
        sb.AppendLine($"- TITLE: the video is already titled \"{topic}\". Copy that EXACTLY into the title field — same wording, same order, same capitalisation. Do NOT reword, extend, shorten, add a subtitle or parenthetical, or make it more clickable. Write the script so it delivers on that exact title.");
        // A channel that has a house description format gets it in full; everything else keeps the
        // one-liner it had before, so adding this field changed no existing channel's output.
        var hasDescriptionFormat = !string.IsNullOrWhiteSpace(channel.DescriptionFormat);
        sb.AppendLine(hasDescriptionFormat
            ? "- DESCRIPTION: write it to this channel's house format, set out in full below. It is part of the deliverable, not an afterthought."
            : "- Write a useful YouTube description.");
        sb.AppendLine();

        if (hasDescriptionFormat)
        {
            sb.AppendLine("=== DESCRIPTION FORMAT (the \"description\" field must follow this) ===");
            sb.AppendLine(channel.DescriptionFormat.Trim());
            sb.AppendLine();
        }

        var descriptionHint = hasDescriptionFormat
            ? "string, written to the DESCRIPTION FORMAT above — line breaks as \\n, plain text, no markdown"
            : "string, a few sentences for the YouTube description";

        sb.AppendLine("Return ONLY a single JSON object (no markdown, no prose, no code fences) with this exact shape:");
        sb.AppendLine($$"""
        {
          "title": "the TOPIC above, copied verbatim — not a rewritten or improved version of it",
          "description": "{{descriptionHint}}",
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
    private string BuildExpandPrompt(Channel channel, ScriptDocument script, int actualWords, WordTarget target)
    {
        var sb = new StringBuilder();
        AppendChannelContext(sb, channel);
        sb.Append(ReferenceStudySources.ScriptContext(channel, settings.WorkspaceRoot));
        sb.AppendLine($"The script below has only {actualWords} words of narration. It must reach {target.Min}–{target.Max} words.");
        sb.AppendLine();
        sb.AppendLine("Expand it by:");
        sb.AppendLine("  • Adding new beats between existing ones to deepen thin sections");
        sb.AppendLine("  • Adding more examples, context, statistics, or explanations");
        sb.AppendLine("  • Never padding with filler — every new beat must add real substance");
        sb.AppendLine("Keep all existing beats, the same topic, and the same overall flow.");
        sb.AppendLine($"Keep beats between {target.BeatWordsLow} and {target.BeatWordsHigh} words, about {target.SecondsPerImage:0.#} seconds per image. This is an image-cut budget, not a sentence-length ceiling: let a natural sentence continue across beats without adding full stops.");
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
    /// Rewrites ONLY the YouTube description for a finished script, to the channel's house format.
    /// </summary>
    /// <remarks>
    /// Separate from generation because a description is the one part of a run worth redoing on its
    /// own: the narration is already voiced and every beat already has a frame, so regenerating the
    /// script to fix the text under the video would throw away hours of work and a day of image
    /// quota. It also means a change to the channel's format can be applied to videos that already
    /// exist.
    /// </remarks>
    public async Task<string> RewriteDescriptionAsync(
        Channel channel, ScriptDocument script, CancellationToken ct,
        IReadOnlyList<string>? rejected = null)
    {
        if (string.IsNullOrWhiteSpace(channel.DescriptionFormat))
            throw new InvalidOperationException(
                $"Channel '{channel.Id}' has no description format set, so there is nothing to rewrite to. " +
                "Add one in the channel editor (Publishing → Description format).");

        var prompt = BuildDescriptionPrompt(channel, script, rejected);
        var args = new List<string>(options.Args) { prompt };
        var result = await process.RunOrThrowAsync(options.Executable, args, ct: ct);

        var json = ExtractFirstJsonObject(result.StdOut)
            ?? throw new InvalidOperationException(
                "Claude returned no JSON for the description. Raw output:\n" + result.StdOut);

        var written = Json.Deserialize<DescriptionOnly>(json);
        var description = (written?.Description ?? "").Trim();
        if (description.Length == 0)
            throw new InvalidOperationException("Claude returned an empty description.");

        logger.LogInformation("Rewrote description: {Chars} characters.", description.Length);
        return description;
    }

    /// <summary>The description-rewrite response — just the one field.</summary>
    private sealed class DescriptionOnly
    {
        public string Description { get; set; } = "";
    }

    /// <summary>
    /// Prompt for a description-only rewrite. Hands over the finished narration so every claim in
    /// the description can be traced to something actually said in the video.
    /// </summary>
    private static string BuildDescriptionPrompt(
        Channel channel, ScriptDocument script, IReadOnlyList<string>? rejected = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Write the YouTube description for a video that is already finished and voiced.");
        sb.AppendLine("Do NOT rewrite, summarise or comment on the script — only produce the description.");
        sb.AppendLine();

        // Second and later attempts: name what failed. Every citation is looked up in Crossref and
        // Open Library after this returns, so "could not be found" is a fact, not an opinion.
        if (rejected is { Count: > 0 })
        {
            sb.AppendLine("=== THESE CITATIONS FROM YOUR LAST ATTEMPT DO NOT EXIST ===");
            sb.AppendLine("Each was searched for in Crossref and Open Library and could not be found:");
            foreach (var title in rejected) sb.AppendLine($"  - \"{title}\"");
            sb.AppendLine();
            sb.AppendLine("Do NOT use any of them again, and do not simply reword them. Replace each with a");
            sb.AppendLine("real, well-known publication you are certain of, or leave it out. Returning fewer");
            sb.AppendLine("sources is the correct answer when you are not sure — a short, true list is the goal.");
            sb.AppendLine();
        }
        sb.AppendLine($"TITLE: {script.Title}");
        sb.AppendLine();
        sb.AppendLine("=== DESCRIPTION FORMAT (follow this exactly) ===");
        sb.AppendLine(channel.DescriptionFormat.Trim());
        sb.AppendLine();
        sb.AppendLine("=== ACCURACY — THIS MATTERS MORE THAN FILLING THE FORMAT ===");
        sb.AppendLine("Every source you list will be checked against reality by a person.");
        sb.AppendLine("- Cite ONLY work you are confident exists, with the correct author, year and venue.");
        sb.AppendLine("- NEVER invent a DOI or URL. If you are not certain of a link, leave it out entirely.");
        sb.AppendLine("- If you are unsure of a paper's exact title or journal, name the researcher and the");
        sb.AppendLine("  finding instead, and give no citation details rather than guessed ones.");
        sb.AppendLine("- Prefer few, certain sources over many, shaky ones. A short list that is entirely");
        sb.AppendLine("  correct is the goal; a long list with one invented entry is a failure.");
        sb.AppendLine("- Every entry must correspond to a claim actually made in the narration below.");
        sb.AppendLine();
        sb.AppendLine("=== THE NARRATION AS SPOKEN ===");
        sb.AppendLine(script.NarrationForVoice());
        sb.AppendLine();
        sb.AppendLine("Return ONLY a single JSON object, no prose and no code fences:");
        sb.AppendLine("""{ "description": "the full description, newlines written as \n" }""");
        return sb.ToString();
    }

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
