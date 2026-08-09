using Newtonsoft.Json;

namespace YtPipeline.Core.Models;

/// <summary>
/// User-editable settings persisted to <c>settings.json</c> in the workspace root and
/// editable from the Settings tab in the UI. These OVERLAY the values in appsettings.json,
/// so the shipped defaults still apply on a fresh install.
/// </summary>
public sealed class AppSettings
{
    /// <summary>Which backend produces the frame images: <c>codex</c> or <c>gflow</c>.</summary>
    public string ImageProvider { get; set; } = ImageProviders.Codex;

    /// <summary>
    /// Backend that finishes the job when <see cref="ImageProvider"/> stops short — typically
    /// because a daily quota ran out mid-run. Empty disables the fallback.
    /// <para>
    /// Both generators skip beats that already have a frame, so the fallback naturally fills
    /// only the gaps. Note the two renderers won't look identical even on the same style rule,
    /// so a video finished this way can show a seam where the handover happened.
    /// </para>
    /// </summary>
    public string ImageFallbackProvider { get; set; } = "";

    /// <summary>
    /// How long a provider stays benched after reporting its quota exhausted, before the next run
    /// tries it again. Doubles on each retry that finds it still out, capped at 8 hours. Kept
    /// short by default because the point is to discover the real reset, not predict it.
    /// </summary>
    public int QuotaCooldownMinutes { get; set; } = 60;

    public GflowSettings Gflow { get; set; } = new();

    /// <summary>Linear gain on the ElevenLabs voice in the final mix (1.25 = +25%).</summary>
    public double VoiceGain { get; set; } = 1.25;

    /// <summary>Master switch for background music (per-channel <c>music:false</c> still wins).</summary>
    public bool MusicEnabled { get; set; } = true;

    /// <summary>Background music level relative to the voice.</summary>
    public double MusicVolume { get; set; } = 0.15;
}

/// <summary>Known <see cref="AppSettings.ImageProvider"/> values.</summary>
public static class ImageProviders
{
    /// <summary>Codex CLI agent — generates every beat in one run. Fully unattended.</summary>
    public const string Codex = "codex";

    /// <summary>
    /// gflow-cli — drives Google Flow through a headed Chrome window and spends Google AI
    /// Pro / Flow credits. One image per invocation, so beats are generated sequentially.
    /// </summary>
    public const string Gflow = "gflow";
}

/// <summary>Settings for the gflow-cli image provider.</summary>
public sealed class GflowSettings
{
    /// <summary>Executable name or full path. Resolved on PATH if not absolute.</summary>
    public string Executable { get; set; } = "gflow";

    /// <summary>
    /// Args placed before the prompt, forming <c>&lt;exe&gt; image t2i "&lt;prompt&gt;"</c>.
    /// gflow-cli uses <c>image t2i</c>; kodelyx/flow-agent uses just <c>image</c>.
    /// </summary>
    /// <remarks>
    /// <see cref="ObjectCreationHandling.Replace"/> is REQUIRED on every list here: Newtonsoft
    /// otherwise reuses the instance from the property initializer and APPENDS the JSON to it,
    /// so saving <c>["image"]</c> would load back as <c>["image","t2i","image"]</c>.
    /// </remarks>
    [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
    public List<string> ArgsTemplate { get; set; } = new() { "image", "t2i" };

    /// <summary>Flag that carries the output file path (<c>-o</c> on both known CLIs).</summary>
    public string OutputFlag { get; set; } = "-o";

    /// <summary>
    /// Args for the "Test connection" probe. gflow supports <c>--version</c>; flow-agent
    /// does not (it exits 2) but has <c>status</c>, which also reports bridge readiness.
    /// </summary>
    [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
    public List<string> CheckArgs { get; set; } = new() { "--version" };

    /// <summary>
    /// Aspect ratio. MUST mean 16:9 for YouTube — gflow's own default is <c>9:16</c>
    /// (vertical), which would produce sideways frames. gflow uses <c>16:9</c>,
    /// flow-agent uses <c>landscape</c>.
    /// </summary>
    public string Aspect { get; set; } = "16:9";

    /// <summary>
    /// Image model alias. gflow: <c>nano2</c> / <c>nano-pro</c> / <c>imagen4</c>.
    /// flow-agent: <c>harbor_seal</c> / <c>narwhal</c> / <c>gem_pix_2</c>.
    /// Both default here to their Nano Banana Pro equivalent.
    /// </summary>
    public string Model { get; set; } = "nano-pro";

    /// <summary>Optional gflow project to generate into. Empty = gflow's default.</summary>
    public string Project { get; set; } = "";

    /// <summary>Extra flags appended verbatim (e.g. <c>--tool</c>, <c>--jitter</c>).</summary>
    [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
    public List<string> ExtraArgs { get; set; } = new();

    /// <summary>Seconds to allow a single image generation before giving up.</summary>
    public int TimeoutSeconds { get; set; } = 300;

    /// <summary>
    /// How many images to generate at once. flow-agent queues these behind its own
    /// MAX_CONCURRENT_REQUESTS (default 5) and REQUEST_MIN_INTERVAL, so going much above
    /// that buys nothing and only raises the odds of tripping rate limiting. Measured ~3x
    /// throughput at 6. Set 1 for gflow-cli, which drives a single browser window.
    /// </summary>
    public int MaxConcurrent { get; set; } = 4;
}
