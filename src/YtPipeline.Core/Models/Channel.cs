using Newtonsoft.Json;

namespace YtPipeline.Core.Models;

/// <summary>
/// Stable per-channel identity loaded from <c>channels/&lt;id&gt;.json</c>.
/// This is the consistent input that keeps generation on-brand across videos.
/// </summary>
public sealed class Channel
{
    /// <summary>Channel id == the json file name without extension (e.g. "nullpointer").</summary>
    [JsonIgnore]
    public string Id { get; set; } = "";

    public string Name { get; set; } = "";
    public string Tagline { get; set; } = "";
    public string Persona { get; set; } = "";
    public string Audience { get; set; } = "";
    public string Tone { get; set; } = "";
    /// <summary>
    /// Legacy single-value length. Superseded by <see cref="VideoLengthMinMinutes"/> /
    /// <see cref="VideoLengthMaxMinutes"/>; kept so older channel files still load, and
    /// re-synced to the range's lower bound on save.
    /// </summary>
    public int VideoLengthTargetMinutes { get; set; } = 5;

    /// <summary>Shortest acceptable runtime, in minutes.</summary>
    public int VideoLengthMinMinutes { get; set; }

    /// <summary>
    /// Longest acceptable runtime, in minutes. Equal to the minimum means "about this long";
    /// a spread (e.g. 3-5) gives the script room without letting the image count run away.
    /// </summary>
    public int VideoLengthMaxMinutes { get; set; }
    /// <summary>
    /// Standing sentence-level rules for this channel — rhythm, person, banned filler. Always sent
    /// with whichever structure is chosen, so the channel's voice survives template rotation
    /// without every template having to repeat the same block.
    /// </summary>
    public string ScriptVoiceRules { get; set; } = "";

    /// <summary>
    /// Fallback structure, used when <see cref="ScriptTemplates"/> is empty.
    /// </summary>
    public List<string> ScriptStructure { get; set; } = new();

    /// <summary>
    /// Named alternative structures for this channel, rotated across videos so consecutive
    /// uploads aren't built to an identical skeleton. The script stage picks the least recently
    /// used one and records the choice on the run.
    /// </summary>
    public List<ScriptTemplate> ScriptTemplates { get; set; } = new();

    /// <summary>Optional named structure override. Empty retains least-recently-used rotation.</summary>
    public string ScriptTemplateName { get; set; } = "";

    /// <summary>
    /// Actual source material to study on every generation. Paths are relative to the workspace
    /// root, or absolute. Null keeps channels without a reference study unchanged.
    /// </summary>
    public ChannelReferenceStudy? ReferenceStudy { get; set; }

    /// <summary>
    /// House format for the YouTube description, written into the script prompt. Empty keeps the
    /// old behaviour — "write a useful YouTube description", i.e. a few sentences.
    /// <para>
    /// This exists because a description is part of a channel's format, not a detail of one video:
    /// a sourced-explainer channel wants a citation block under every upload, and a channel that
    /// doesn't should not be made to invent one. The text lands in the prompt verbatim, so write it
    /// as instructions to the writer, and include the accuracy rules — a model asked for a
    /// bibliography will happily produce plausible DOIs that do not exist.
    /// </para>
    /// </summary>
    public string DescriptionFormat { get; set; } = "";

    /// <summary>
    /// How long one image should stay on screen, in seconds. 0 keeps the original fast-cut
    /// default of roughly two seconds a frame.
    /// <para>
    /// This is what decides how the narration is cut into beats, because there is exactly one
    /// image per beat: a channel that changes picture every 5 seconds needs beats more than twice
    /// as long as one that changes every 2. Getting it wrong is expensive in both directions —
    /// too fast doubles the image bill for a video that should be calm, too slow leaves a
    /// fast-paced channel staring at one frame.
    /// </para>
    /// </summary>
    public double SecondsPerImage { get; set; }

    public string VisualStyle { get; set; } = "";

    /// <summary>
    /// Which backend draws this channel's frames, thumbnails and channel art: <c>gflow</c> or
    /// <c>codex</c>. Empty follows Settings → Images.
    /// <para>
    /// The two don't draw alike, and a channel's look is whichever one drew its first videos.
    /// The Old Humans was drawn by gflow; when Settings switched to codex for Barely An Expert
    /// (2026-09-08), its next video would have come out in codex's style. A channel that names
    /// its backend never falls back to the other one, because that would change the look.
    /// </para>
    /// </summary>
    public string ImageProvider { get; set; } = "";

    public string VoiceNotes { get; set; } = "";

    /// <summary>
    /// Per-channel ElevenLabs overrides. Any field left empty/null falls back to the global
    /// value (env <c>ELEVENLABS_VOICE_ID</c> / appsettings), so channels that don't set a
    /// voice keep behaving exactly as before.
    /// </summary>
    public ChannelVoice Voice { get; set; } = new();

    /// <summary>
    /// Whether background music is mixed into this channel's videos. Defaults to true;
    /// set <c>"music": false</c> in the channel JSON to ship voice-only videos
    /// (e.g. Caveman Finance). Absent key keeps the default, so other channels are unaffected.
    /// </summary>
    public bool Music { get; set; } = true;

    public YoutubeDefaults Youtube { get; set; } = new();

    /// <summary>
    /// How the CHANNEL itself presents on YouTube — handle, About text, keywords, links, and the
    /// avatar/banner art. Distinct from <see cref="Persona"/> and friends, which describe how the
    /// videos are written: this is the shop front, that is the writing brief.
    /// <para>
    /// Recorded here for you to copy into YouTube Studio by hand; nothing is pushed automatically.
    /// </para>
    /// </summary>
    public ChannelIdentity Identity { get; set; } = new();

    /// <summary>Whether <c>channels/&lt;id&gt;.avatar.png</c> exists. Reported, never stored.</summary>
    [JsonIgnore]
    public bool HasAvatar { get; set; }

    /// <summary>Whether <c>channels/&lt;id&gt;.banner.png</c> exists. Reported, never stored.</summary>
    [JsonIgnore]
    public bool HasBanner { get; set; }

    /// <summary>
    /// Full image-generation rule for Stage 2, loaded from the sibling <c>&lt;id&gt;.image.md</c>
    /// file if present. Not part of the channel JSON. Falls back to <see cref="VisualStyle"/>.
    /// </summary>
    [JsonIgnore]
    public string ImageInstructions { get; set; } = "";

    /// <summary>
    /// Where <see cref="ImageInstructions"/> came from: <c>channel</c> (its own
    /// <c>&lt;id&gt;.image.md</c>), <c>default</c> (the shared <c>_default.image.md</c>), or
    /// <c>none</c>. Needed because a non-empty rule does NOT mean the channel has its own —
    /// conflating the two made the UI claim per-channel files that don't exist.
    /// </summary>
    [JsonIgnore]
    public string ImageRuleSource { get; set; } = "none";

    /// <summary>
    /// Thumbnail rule, from <c>&lt;id&gt;.thumbnail.md</c> or the shared
    /// <c>_default.thumbnail.md</c>. Separate from <see cref="ImageInstructions"/> because a
    /// thumbnail has a different job from a frame — frames explain a line of narration, thumbnails
    /// have to land one idea at feed size — and reusing the frame rule produced frames-as-covers.
    /// Empty falls back to the image rule.
    /// </summary>
    [JsonIgnore]
    public string ThumbnailInstructions { get; set; } = "";
}

public sealed class ChannelReferenceStudy
{
    public string SourceUrl { get; set; } = "";
    public List<string> TranscriptPaths { get; set; } = new();
    public List<string> ImagePaths { get; set; } = new();
    public List<string> ThumbnailPaths { get; set; } = new();
}

/// <summary>
/// Per-channel voice selection. Empty string / null means "inherit the global setting" —
/// that distinction is why the numeric fields are nullable rather than defaulted.
/// </summary>
public sealed class ChannelVoice
{
    /// <summary>ElevenLabs voice id. Empty = use <c>ELEVENLABS_VOICE_ID</c>.</summary>
    public string VoiceId { get; set; } = "";

    /// <summary>Friendly label shown in the UI (ElevenLabs ids are opaque).</summary>
    public string VoiceName { get; set; } = "";

    /// <summary>ElevenLabs model id. Empty = global default (eleven_multilingual_v2).</summary>
    public string ModelId { get; set; } = "";

    /// <summary>0–1. Null = global default.</summary>
    public double? Stability { get; set; }

    /// <summary>0–1. Null = global default.</summary>
    public double? SimilarityBoost { get; set; }

    /// <summary>
    /// How fast the voice reads, where 1.0 is its natural pace and lower is slower. Null leaves
    /// it at the voice's default.
    /// <para>
    /// This is the only setting that changes delivery speed. <see cref="WordsPerMinute"/> is a
    /// measurement used to size scripts — lowering that shortens the video without slowing a
    /// single word of it.
    /// </para>
    /// </summary>
    public double? Speed { get; set; }

    /// <summary>
    /// Measured speaking rate for THIS voice, in words per minute, learned from finished runs.
    /// <para>
    /// Speaking rate is a property of the voice, not a constant: two real voices on this project
    /// came in at 112 and 215 wpm — nearly 2x apart. A fixed assumption therefore cannot size a
    /// script for both, and using one produced a 2-minute video from a 3-minute target. The voice
    /// stage writes the observed rate back here so the next script is budgeted from evidence.
    /// Null until the first run with this voice completes.
    /// </para>
    /// </summary>
    public double? WordsPerMinute { get; set; }
}

/// <summary>
/// One alternative script skeleton for a channel. The <see cref="Sections"/> list has the same
/// shape and meaning as <see cref="Channel.ScriptStructure"/> — a standing rules block followed
/// by ordered sections with word budgets.
/// </summary>
public sealed class ScriptTemplate
{
    /// <summary>Short label, used for rotation bookkeeping and shown on the run.</summary>
    public string Name { get; set; } = "";

    /// <summary>When this template was last used, so rotation can pick the stalest one.</summary>
    public DateTime? LastUsedUtc { get; set; }

    public List<string> Sections { get; set; } = new();
}

public sealed class YoutubeDefaults
{
    public string CategoryId { get; set; } = "27";
    public List<string> DefaultTags { get; set; } = new();
    public string TitlePattern { get; set; } = "{topic}";
    public string DescriptionFooter { get; set; } = "";
}

/// <summary>
/// The channel's public identity on YouTube. Field limits mirror YouTube's own so what's written
/// here can be pasted into Studio without being rejected.
/// </summary>
public sealed class ChannelIdentity
{
    /// <summary>Handle without the leading @ — e.g. <c>theprimitiveengineer</c>. 3-30 chars there.</summary>
    public string Handle { get; set; } = "";

    /// <summary>The About-page description. YouTube caps this at 1000 characters.</summary>
    public string Bio { get; set; } = "";

    /// <summary>
    /// One-line summary shown under the channel name in some surfaces. Falls back to
    /// <see cref="Channel.Tagline"/> when empty.
    /// </summary>
    public string Byline { get; set; } = "";

    /// <summary>Channel keywords (Studio → Settings → Channel → Basic info).</summary>
    public List<string> Keywords { get; set; } = new();

    /// <summary>Links shown on the channel page, in order.</summary>
    public List<ChannelLink> Links { get; set; } = new();

    /// <summary>
    /// Optional art direction for the avatar, on top of the channel's own image rule. Empty lets
    /// the generator work from the channel's look alone.
    /// </summary>
    public string AvatarPrompt { get; set; } = "";

    /// <summary>Optional art direction for the banner. Same fallback as <see cref="AvatarPrompt"/>.</summary>
    public string BannerPrompt { get; set; } = "";

    /// <summary>
    /// Wording to letter across the banner, or empty for art only.
    /// <para>
    /// Banner art is drawn wordless by default, because a model asked for text unprompted spells it
    /// wrong and puts it somewhere useless. Setting this opts the channel in: the generator then
    /// asks for exactly this line and nothing else, inside the strip YouTube actually shows. Keep it
    /// short — every extra word is another chance to misspell one, and the strip is only 423px tall.
    /// </para>
    /// </summary>
    public string BannerHeadline { get; set; } = "";
}

public sealed class ChannelLink
{
    public string Label { get; set; } = "";
    public string Url { get; set; } = "";
}
