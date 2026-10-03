using Microsoft.Extensions.Logging;
using Serilog;
using YtPipeline.Stages;
using YtPipeline.Stages.Images;
using YtPipeline.Stages.Orchestration;
using YtPipeline.Web;
using Json = YtPipeline.Core.Services.Json;

DotNetEnv.Env.TraversePath().Load();

var builder = WebApplication.CreateBuilder(args);

// Fixed port so the desktop shortcut always lands on the same URL. PORT overrides it,
// which lets a second instance run alongside the main app (dev/preview).
var port = Environment.GetEnvironmentVariable("PORT") is { Length: > 0 } p ? p : "5080";
builder.WebHost.UseUrls($"http://localhost:{port}");

var rootSetting = builder.Configuration["Workspace:Root"];
var workspaceRoot = Path.GetFullPath(
    string.IsNullOrWhiteSpace(rootSetting) ? FindWorkspaceRoot() : rootSetting);
var channelsDir = Path.GetFullPath(
    builder.Configuration["Workspace:ChannelsDir"] is { Length: > 0 } cd ? cd : "channels", workspaceRoot);

// Make sure .env is loaded from the workspace too (the packaged exe's cwd may differ).
var envFile = Path.Combine(workspaceRoot, ".env");
if (File.Exists(envFile)) DotNetEnv.Env.Load(envFile);

// Find the repo root (the dir containing channels/) by walking up from both the current
// directory and the exe's own folder — covers `dotnet run`, double-click, and shortcuts.
static string FindWorkspaceRoot()
{
    foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, "channels")))
                return dir.FullName;
    return Directory.GetCurrentDirectory();
}

// Logging: Serilog to console/file, plus the per-job capture provider for the UI.
var serilog = new LoggerConfiguration()
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft.AspNetCore", Serilog.Events.LogEventLevel.Warning)
    .WriteTo.Console(outputTemplate: "{Timestamp:HH:mm:ss} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
    .WriteTo.File(Path.Combine(workspaceRoot, "logs", "ytweb-.log"), rollingInterval: RollingInterval.Day)
    .CreateLogger();
builder.Logging.ClearProviders();
builder.Logging.AddSerilog(serilog, dispose: true);
builder.Logging.AddProvider(new JobLoggerProvider());

builder.Services.AddYtPipeline(builder.Configuration, workspaceRoot, channelsDir);
builder.Services.AddSingleton<JobService>();

var app = builder.Build();

// Serve the built React app. Prefer the project/published wwwroot (has the real build),
// then fall back to the exe-adjacent one — robust to the current working directory.
var webRoot = new[]
    {
        Path.Combine(builder.Environment.ContentRootPath, "wwwroot"),
        Path.Combine(AppContext.BaseDirectory, "wwwroot"),
    }
    .FirstOrDefault(p => File.Exists(Path.Combine(p, "index.html")));

if (webRoot is not null)
{
    var fp = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(webRoot);
    app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = fp });
    app.UseStaticFiles(new StaticFileOptions
    {
        FileProvider = fp,
        OnPrepareResponse = ctx =>
        {
            // Never cache index.html so a new build's hashed assets are always picked up.
            if (ctx.File.Name.Equals("index.html", StringComparison.OrdinalIgnoreCase))
                ctx.Context.Response.Headers.CacheControl = "no-cache, no-store, must-revalidate";
        },
    });
}

static IResult J(object value) =>
    Results.Content(Json.Serialize(value), "application/json; charset=utf-8");

var pipeline = app.Services.GetRequiredService<Pipeline>();
var jobs = app.Services.GetRequiredService<JobService>();

// --- Settings ---------------------------------------------------------------
var settingsStore = app.Services.GetRequiredService<YtPipeline.Core.Services.SettingsStore>();
var ffmpegOptions = app.Services.GetRequiredService<YtPipeline.Stages.Video.FfmpegOptions>();
var musicOptions = app.Services.GetRequiredService<YtPipeline.Stages.Music.MusicOptions>();

app.MapGet("/api/settings", () => J(settingsStore.Current));

app.MapPut("/api/settings", (YtPipeline.Core.Models.AppSettings incoming) =>
{
    var saved = settingsStore.Save(incoming);

    // Push the audio values onto the live option singletons so the next video assembly
    // uses them without an app restart.
    ffmpegOptions.VoiceGain = saved.VoiceGain;
    musicOptions.Enabled = saved.MusicEnabled;
    musicOptions.Volume = saved.MusicVolume;

    Log.Information("Settings saved — image provider: {Provider}, voice gain: {Gain}",
        saved.ImageProvider, saved.VoiceGain);
    return J(saved);
});

// Provider health: which image backends are quota-benched and when they'll be retried. Exposed
// so the automatic switching is visible in the UI rather than looking like magic.
var healthStore = app.Services.GetRequiredService<YtPipeline.Core.Services.ProviderHealthStore>();

app.MapGet("/api/provider-health", () =>
{
    var cfg = settingsStore.Current;
    return J(new[] { "gflow", "codex" }.Select(p =>
    {
        var s = healthStore.Snapshot(p);
        var until = healthStore.BenchedUntil(p, cfg.QuotaCooldownMinutes);
        return new
        {
            provider = p,
            preferred = p == cfg.ImageProvider,
            standby = p == cfg.ImageFallbackProvider,
            // "Ran dry, and the cooldown has not lapsed" — NOT "cannot be used". Signing Flow into
            // another account resets the real limit, and nothing the CLI exposes reports it, so
            // asking for this provider by hand still runs it.
            benched = until is not null,
            benchedUntilUtc = until,
            retryInMinutes = until is null ? (double?)null
                : Math.Round((until.Value - DateTime.UtcNow).TotalMinutes, 1),
            exhaustedUtc = s.ExhaustedUtc,
            consecutiveProbeFailures = s.ConsecutiveProbeFailures,
            lastError = s.LastError,
        };
    }));
});

// Clear a provider's benching by hand — for when it's known to be back before the cooldown
// lapses, or to undo a bad state.
app.MapDelete("/api/provider-health/{provider}", (string provider) =>
{
    healthStore.Reset(provider.ToLowerInvariant());
    Log.Information("Cleared quota benching for '{Provider}'.", provider);
    return J(new { cleared = provider });
});

/// <summary>Reports whether the selected image provider's CLI is actually runnable.</summary>
app.MapGet("/api/settings/check-gflow", async () =>
{
    var cfg = settingsStore.Current.Gflow;
    var runner = app.Services.GetRequiredService<YtPipeline.Stages.Process.ProcessRunner>();
    try
    {
        // flow-agent's `status` probe starts its backend, so allow a generous timeout.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var result = await runner.RunAsync(cfg.Executable, cfg.CheckArgs, ct: cts.Token);
        var output = result.StdOut.Trim();

        // The exit code only says the CLI ran. `flow status` returns 0 while reporting
        // "extension_connected: False", so trusting it reported gflow healthy during a run in
        // which every single beat failed. Read what the probe actually said instead.
        var status = YtPipeline.Stages.Images.GflowStatus.Parse(output);
        return J(new
        {
            installed = result.Success,
            connected = result.Success && status.CanGenerate,
            state = status.State,
            remedy = result.Success ? status.Remedy : null,
            version = output.Length > 300 ? output[..300] : output,
            error = result.Success ? null : result.StdErr.Trim(),
        });
    }
    catch (Exception ex)
    {
        return J(new
        {
            installed = false, connected = false, state = "unknown",
            remedy = (string?)null, version = (string?)null, error = ex.Message,
        });
    }
});

// --- Publishing credentials (secrets.json) ----------------------------------
// GET never returns a secret value — only whether each one is set. On PUT a null secret means
// "keep what's stored" and "" means "clear it", so an untouched form is harmless and removing
// a credential is still possible.
var secretsStore = app.Services.GetRequiredService<YtPipeline.Core.Services.SecretsStore>();

app.MapGet("/api/secrets", () => J(secretsStore.Redacted()));

app.MapPut("/api/secrets", (YtPipeline.Core.Models.PublishingSecretsUpdate incoming) =>
{
    var saved = secretsStore.Save(incoming);
    Log.Information(
        "App credentials saved — youtube: {Yt}, facebook: {Fb}, instagram: {Ig}",
        saved.Youtube.Enabled, saved.Facebook.Enabled, saved.Instagram.Enabled);
    return J(secretsStore.Redacted());
});

// Per-channel publish targets. Separate from /api/secrets because these are per-destination
// (a Page token belongs to one Page, a YouTube refresh token to one channel) while the app
// credentials above are shared by every channel.
app.MapGet("/api/channels/{id}/publish", (string id) => J(secretsStore.RedactedChannel(id)));

app.MapPut("/api/channels/{id}/publish",
    (string id, YtPipeline.Core.Models.ChannelPublishUpdate incoming) =>
{
    secretsStore.SaveChannel(id, incoming);
    Log.Information("Publish targets saved for channel '{Id}'.", id);
    return J(secretsStore.RedactedChannel(id));
});

// --- Channels ---------------------------------------------------------------
// Returns FULL channel objects: the Channels screen edits every field, and the run
// composer only needs id/name/tagline out of the same payload.
app.MapGet("/api/channels", () =>
    J(pipeline.Channels.List().Select(id =>
    {
        var c = pipeline.Channels.Load(id);
        return Describe(id, c);
    })));

app.MapGet("/api/channels/{id}", (string id) =>
{
    try { return J(Describe(id, pipeline.Channels.Load(id))); }
    catch (FileNotFoundException ex) { return Results.NotFound(new { error = ex.Message }); }
});

app.MapPut("/api/channels/{id}", (string id, YtPipeline.Core.Models.Channel incoming) =>
{
    try
    {
        var saved = pipeline.Channels.Save(id, incoming);
        Log.Information(
            "Channel '{Id}' saved — {Minutes} min target, voice '{Voice}'.",
            id, saved.VideoLengthTargetMinutes,
            string.IsNullOrWhiteSpace(saved.Voice.VoiceId) ? "(global)" : saved.Voice.VoiceId);
        return J(Describe(id, saved));
    }
    catch (FileNotFoundException ex) { return Results.NotFound(new { error = ex.Message }); }
});

// Create. The body is a full channel so the UI can seed persona/tone up front; only the id
// needs to be new and filename-safe.
app.MapPost("/api/channels", (CreateChannelRequest req) =>
{
    if (!YtPipeline.Core.Services.ChannelLoader.IsValidId(req.Id))
        return Results.BadRequest(new
        {
            error = "Channel id must be 2-40 characters of lowercase letters, digits or dashes, " +
                    "not starting or ending with a dash.",
        });

    try
    {
        var seed = req.Channel ?? new YtPipeline.Core.Models.Channel();
        if (string.IsNullOrWhiteSpace(seed.Name)) seed.Name = req.Id;
        var created = pipeline.Channels.Create(req.Id, seed);
        Log.Information("Channel '{Id}' created ({Minutes} min target).",
            req.Id, created.VideoLengthTargetMinutes);
        return J(Describe(req.Id, created));
    }
    catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
    catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
});

// Delete. Removes the channel JSON and forgets its publish targets, but deliberately keeps the
// hand-written <id>.image.md rule and any existing runs — the response says what survived.
app.MapDelete("/api/channels/{id}", (string id) =>
{
    try
    {
        var (ruleKept, rulePath) = pipeline.Channels.Delete(id);
        var forgotTargets = secretsStore.ForgetChannel(id);
        var runs = pipeline.Runs.ListManifests().Count(m => m.Channel == id);

        Log.Warning("Channel '{Id}' deleted. Image rule kept: {Rule}. Orphaned runs: {Runs}.",
            id, ruleKept, runs);

        return J(new
        {
            deleted = id,
            imageRuleKept = ruleKept,
            imageRulePath = ruleKept ? rulePath : null,
            publishTargetsRemoved = forgotTargets,
            orphanedRuns = runs,
        });
    }
    catch (FileNotFoundException ex) { return Results.NotFound(new { error = ex.Message }); }
    catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
});

// Projects a channel for the API. `id` and the loaded image rule are [JsonIgnore]d on the
// model (so they never round-trip into the file), but the UI wants both — hence this shape.
static object Describe(string id, YtPipeline.Core.Models.Channel c) => new
{
    id,
    c.Name,
    c.Tagline,
    c.Persona,
    c.Audience,
    c.Tone,
    c.VideoLengthTargetMinutes,
    c.VideoLengthMinMinutes,
    c.VideoLengthMaxMinutes,
    // Every editable field MUST appear here. The UI round-trips whatever this returns straight
    // back to PUT, so anything omitted is silently dropped the first time a channel is saved —
    // omitting these two would have wiped the whole template library on the next edit.
    c.ScriptVoiceRules,
    c.ScriptTemplates,
    c.ScriptTemplateName,
    c.ScriptStructure,
    c.DescriptionFormat,
    c.SecondsPerImage,
    c.VisualStyle,
    c.ImageProvider,
    // Not edited in the UI, but it must survive a save: without it here, saving Barely An Expert
    // from the Channels page would have written its reference images out of the file.
    c.ReferenceStudy,
    c.VoiceNotes,
    c.Voice,
    c.Music,
    c.Youtube,
    c.Identity,
    // "channel" | "default" | "none". hasImageRule alone was ambiguous: it was true even when
    // the channel had no file of its own and was only inheriting the shared default.
    imageRuleSource = c.ImageRuleSource,
    hasImageRule = c.ImageRuleSource == "channel",
    // Whether the branding art exists on disk. Read-only: the pngs are files beside the channel
    // json, not fields in it, so these come back from the loader and are never written.
    hasAvatar = c.HasAvatar,
    hasBanner = c.HasBanner,
};

// Suggests the channel's handle/byline/About/keywords from its persona and tone. Returns the
// text rather than saving it — the editor drops it into the draft so it can be read and edited
// before the user commits with Save.
app.MapPost("/api/channels/{id}/identity/generate", async (string id, CancellationToken ct) =>
{
    var writer = app.Services.GetRequiredService<YtPipeline.Stages.Script.ChannelIdentityWriter>();
    try
    {
        var channel = pipeline.Channels.Load(id);
        return J(await writer.WriteAsync(channel, ct));
    }
    catch (FileNotFoundException ex) { return Results.NotFound(new { error = ex.Message }); }
    catch (OperationCanceledException) { return Results.StatusCode(499); }
    catch (Exception ex)
    {
        Log.Warning(ex, "Writing the identity for '{Id}' failed.", id);
        return Results.Problem(ex.Message, statusCode: 500);
    }
});

// --- Channel branding art ---------------------------------------------------
// The pngs live beside the channel json (channels/<id>.avatar.png). Generation is a background
// job like any image work, keyed on the channel id so the existing job log and polling apply.

app.MapGet("/api/channels/{id}/brand/{kind}", (string id, string kind) =>
{
    try
    {
        var path = pipeline.Channels.BrandImagePath(id, kind);
        return File.Exists(path) ? Results.File(path, "image/png") : Results.NotFound();
    }
    catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
});

app.MapPost("/api/channels/{id}/brand/{kind}", (string id, string kind) =>
{
    try
    {
        kind = YtPipeline.Core.Models.BrandImage.Normalize(kind);
        pipeline.Channels.Load(id);   // fail fast on an unknown channel, before starting a job

        // Same one-job-at-a-time rule the run stages use: two generations would race on one file.
        if (jobs.ActiveJobFor(id) is { } busy)
            return Results.Conflict(new
            {
                error = $"'{busy.Stage}' is already running for this channel. Wait for it, or stop it first.",
                stage = busy.Stage,
                jobId = busy.Id,
            });

        var job = jobs.Start(id, kind, ct => pipeline.BrandImageAsync(id, kind, ct));
        return J(new { jobId = job.Id });
    }
    catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
    catch (FileNotFoundException ex) { return Results.NotFound(new { error = ex.Message }); }
});

app.MapDelete("/api/channels/{id}/brand/{kind}", (string id, string kind) =>
{
    try
    {
        var path = pipeline.Channels.BrandImagePath(id, kind);
        if (File.Exists(path)) File.Delete(path);
        Log.Information("Brand image '{Kind}' cleared for channel '{Id}'.", kind, id);
        return J(new { deleted = kind });
    }
    catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
});

// --- Runs -------------------------------------------------------------------
app.MapGet("/api/runs", () => J(pipeline.Runs.ListManifests()));

app.MapGet("/api/runs/{id}", (string id) =>
{
    var run = pipeline.Runs.Resolve(id);
    var manifest = pipeline.Runs.LoadManifest(run);
    object? script = File.Exists(run.ScriptPath) ? pipeline.Runs.LoadScript(run) : null;
    var images = ImageSet.List(run.ImagesDir).Select(Path.GetFileName).ToList();
    // Folder size so the delete confirmation can state exactly what would be lost.
    var (fileCount, bytes) = YtPipeline.Core.Services.RunManager.Measure(run.OutputDir);
    // Report any running job for this run so the UI reflects work started from anywhere —
    // another tab, the CLI, or a restart — instead of only what this browser launched.
    var active = jobs.ActiveJobFor(run.RunId);
    return J(new
    {
        manifest,
        script,
        images,
        hasAudio = File.Exists(run.AudioPath),
        hasVideo = File.Exists(run.VideoPath),
        hasThumbnail = File.Exists(run.ThumbnailPath),
        fileCount,
        sizeBytes = bytes,
        activeJob = active is null ? null : new { id = active.Id, stage = active.Stage },
    });
});

// Delete a run and everything under it. Refused while a job for that run is in flight —
// deleting the folder out from under a running stage would leave a half-written mess.
app.MapDelete("/api/runs/{id}", (string id) =>
{
    if (jobs.HasActiveJobFor(id))
        return Results.Conflict(new
        {
            error = "A job is still running for this video. Stop it first, then delete.",
        });

    try
    {
        var (files, bytes, dir) = pipeline.Runs.Delete(id);
        Log.Warning("Run '{Id}' deleted — {Files} files, {MB:F1} MB from {Dir}",
            id, files, bytes / 1024d / 1024d, dir);
        return J(new { deleted = id, files, sizeBytes = bytes });
    }
    catch (FileNotFoundException ex) { return Results.NotFound(new { error = ex.Message }); }
    catch (DirectoryNotFoundException ex) { return Results.NotFound(new { error = ex.Message }); }
});

// --- Stage 1: create a run + generate script (background job) ---------------
app.MapPost("/api/script", (CreateScriptRequest req) =>
{
    if (string.IsNullOrWhiteSpace(req.Channel) || string.IsNullOrWhiteSpace(req.Topic))
        return Results.BadRequest(new { error = "channel and topic are required." });

    var run = pipeline.Runs.Create(req.Channel, req.Topic);
    var job = req.Auto
        ? jobs.Start(run.RunId, "auto", ct => pipeline.AutoThroughVideoAsync(run, ct))
        : jobs.Start(run.RunId, "script", ct => pipeline.ScriptAsync(run, ct));
    return J(new { jobId = job.Id, runId = run.RunId });
});

// --- Stages 2-5 (background jobs) -------------------------------------------
// `music` applies to the video stage only: false re-assembles voice-only (and drops the music
// credit from the description), true forces the normal selection, omitted keeps the run's last
// answer. `model` is the same idea for the voice stage: an ElevenLabs model id for this video,
// "default" to go back to the channel's, omitted to keep what it used last time.
app.MapPost("/api/runs/{id}/stage/{stage}", (string id, string stage, string? privacy, string? provider, bool force = false, bool? music = null, string? model = null) =>
{
    var run = pipeline.Runs.Resolve(id);

    if (model is not null && !model.Trim().Equals("default", StringComparison.OrdinalIgnoreCase)
        && YtPipeline.Core.Models.VoiceModelIds.Clean(model) == "")
        return Results.BadRequest(new { error = $"'{model}' isn't an ElevenLabs model id." });
    var voiceModel = model?.Trim().Equals("default", StringComparison.OrdinalIgnoreCase) == true ? "" : model;

    // A moved video's frames, voice and video aren't on this PC, so no stage can run until it's back.
    if (pipeline.Runs.LoadManifest(run).Archive is { } moved)
        return Results.Conflict(new
        {
            error = $"This video's files are in Google Drive ({moved.Location}). Bring it back first.",
        });

    // One job per run. Two stages writing the same folder race on the same files and, for the
    // image stages, spend quota twice on the same beats.
    if (jobs.ActiveJobFor(run.RunId) is { } busy)
        return Results.Conflict(new
        {
            error = $"'{busy.Stage}' is already running for this video. Wait for it, or stop it first.",
            stage = busy.Stage,
            jobId = busy.Id,
        });

    Func<CancellationToken, Task> work = stage.ToLowerInvariant() switch
    {
        // Retryable: the script is normally generated when the run is created, but if that call
        // fails (expired CLI token, rate limit) the run is otherwise a dead end that can only be
        // deleted and recreated.
        "script" => ct => pipeline.ScriptAsync(run, ct),
        "images" => ct => pipeline.ImagesAsync(run, ct, regenerate: force, provider: provider),
        "voice" => ct => pipeline.VoiceAsync(run, ct, voiceModel),
        "video" => ct => pipeline.VideoAsync(run, ct, music),
        "thumbnail" => ct => pipeline.ThumbnailAsync(run, ct),
        // Description only — the video and its frames are untouched, so this is safe to run on a
        // finished video whose channel format changed after it was written.
        "description" => ct => pipeline.DescriptionAsync(run, ct),
        "upload" => ct => pipeline.UploadAsync(run, privacy ?? "private", ct),
        // Extra distribution targets. A skipped publish (platform not configured) completes the
        // job normally — the reason lands in the manifest and the log.
        "facebook" or "instagram" => async ct =>
        {
            var result = await pipeline.PublishAsync(run, stage.ToLowerInvariant(), ct);
            if (result.Skipped) Log.Warning("{Platform} skipped — {Reason}", result.Platform, result.SkipReason);
        },
        // Everything configured, in one job, so the card's "Upload all" is a single log stream.
        "publish-all" => ct => pipeline.PublishEverywhereAsync(run, privacy ?? "private", ct),
        _ => throw new ArgumentException($"Unknown stage '{stage}'."),
    };
    var job = jobs.Start(run.RunId, stage, work);
    return J(new { jobId = job.Id });
});

// --- Planned publish date (Calendar) ----------------------------------------
// A plan, not a trigger: nothing publishes on a schedule, so this only moves the card on the
// Calendar. Send scheduledUtc: null to clear it.
app.MapPut("/api/runs/{id}/schedule", (string id, ScheduleRequest req) =>
{
    var run = pipeline.Runs.Resolve(id);
    var saved = pipeline.Runs.UpdateManifest(run, m => m.Publish.ScheduledUtc = req.ScheduledUtc);
    Log.Information("Run '{Id}' scheduled for {When}.",
        id, saved.Publish.ScheduledUtc?.ToString("u") ?? "(cleared)");
    return J(saved);
});

// --- Approve a checkpoint ---------------------------------------------------
app.MapPost("/api/runs/{id}/approve", (string id, ApproveRequest req) =>
{
    var run = pipeline.Runs.Resolve(id);
    pipeline.Approve(run, req.Stage);
    return J(pipeline.Runs.LoadManifest(run));
});

// --- Cancel a running job ---------------------------------------------------
app.MapDelete("/api/jobs/{jobId}", (string jobId) =>
{
    var cancelled = jobs.Cancel(jobId);
    return cancelled
        ? J(new { cancelled = true })
        : Results.NotFound(new { error = "job not found or already finished" });
});

// --- Job status + logs ------------------------------------------------------
app.MapGet("/api/jobs/{jobId}", (string jobId, int since) =>
{
    var job = jobs.Get(jobId);
    if (job is null) return Results.NotFound(new { error = "job not found" });
    return J(new
    {
        id = job.Id,
        runId = job.RunId,
        stage = job.Stage,
        status = job.Status.ToString().ToLowerInvariant(),
        error = job.Error,
        logCount = job.LogCount,
        logs = job.Snapshot(since),
    });
});

// --- Artifact files ---------------------------------------------------------
app.MapGet("/api/runs/{id}/image/{name}", (string id, string name) =>
{
    var run = pipeline.Runs.Resolve(id);
    var path = Path.Combine(run.ImagesDir, Path.GetFileName(name));
    return File.Exists(path) ? Results.File(path, "image/png") : Results.NotFound();
});

// Removing one frame is how a single bad image gets redone: the images stage only fills beats
// that have no PNG on disk, so the next run regenerates exactly what was deleted and nothing else.
app.MapDelete("/api/runs/{id}/image/{name}", (string id, string name) =>
{
    var run = pipeline.Runs.Resolve(id);
    // GetFileName strips any directory part, so a crafted name cannot escape the run's folder.
    var safe = Path.GetFileName(name);
    var path = Path.Combine(run.ImagesDir, safe);
    if (!File.Exists(path)) return Results.NotFound();
    File.Delete(path);
    return Results.Ok(new { deleted = safe });
});

// --- Google Drive ------------------------------------------------------------
// Moves a video's folder into Google Drive for desktop, which uploads it. The manifest and thumbnail
// stay, so the video keeps its place in the list, marked as in Drive. Both directions run as jobs:
// they copy hundreds of MB, and the one-job-per-run rule keeps a stage from racing the copy.
app.MapPost("/api/runs/{id}/drive/move", (string id, ILogger<Program> logger,
    YtPipeline.Stages.Process.ProcessRunner process) =>
{
    var run = pipeline.Runs.Resolve(id);
    if (jobs.ActiveJobFor(run.RunId) is { } busy)
        return Results.Conflict(new { error = $"'{busy.Stage}' is running for this video. Wait for it, or stop it first." });
    var s = settingsStore.Current;
    Action<string> log = line => logger.LogInformation("{Line}", line);

    // rclone uploads straight to Google, so the space comes back as soon as its check passes. Copying
    // into the Drive folder is the fallback: it works, but the Drive app keeps a cached copy on this PC.
    if (!string.IsNullOrWhiteSpace(s.RcloneRemote))
    {
        var rclone = new YtPipeline.Stages.Storage.RcloneArchiver(process, pipeline.Runs);
        var mirror = string.IsNullOrWhiteSpace(s.DriveArchiveFolder) ? null : s.DriveArchiveFolder;
        var upload = jobs.Start(run.RunId, "upload to Google Drive", ct =>
            rclone.MoveOutAsync(run, s.RcloneExecutable, s.RcloneRemote, mirror, log, ct));
        return J(new { jobId = upload.Id });
    }
    if (string.IsNullOrWhiteSpace(s.DriveArchiveFolder))
        return Results.BadRequest(new { error = "Set up Google Drive in Settings → Storage first." });

    var archiver = new YtPipeline.Core.Services.RunArchiver(pipeline.Runs);
    var job = jobs.Start(run.RunId, "move to Google Drive", ct => Task.Run(() =>
        archiver.MoveOut(run, s.DriveArchiveFolder, log, ct), ct));
    return J(new { jobId = job.Id });
});

app.MapPost("/api/runs/{id}/drive/restore", (string id, ILogger<Program> logger,
    YtPipeline.Stages.Process.ProcessRunner process) =>
{
    var run = pipeline.Runs.Resolve(id);
    if (jobs.ActiveJobFor(run.RunId) is { } busy)
        return Results.Conflict(new { error = $"'{busy.Stage}' is running for this video. Wait for it, or stop it first." });
    Action<string> log = line => logger.LogInformation("{Line}", line);

    // A video comes back the way it left: an rclone upload records its remote path, a folder copy doesn't.
    if (pipeline.Runs.LoadManifest(run).Archive?.Remote is not null)
    {
        var rclone = new YtPipeline.Stages.Storage.RcloneArchiver(process, pipeline.Runs);
        var exe = settingsStore.Current.RcloneExecutable;
        var download = jobs.Start(run.RunId, "bring back from Google Drive", ct => rclone.BringBackAsync(run, exe, log, ct));
        return J(new { jobId = download.Id });
    }

    var archiver = new YtPipeline.Core.Services.RunArchiver(pipeline.Runs);
    var job = jobs.Start(run.RunId, "bring back from Google Drive", ct => Task.Run(() =>
        archiver.BringBack(run, log, ct), ct));
    return J(new { jobId = job.Id });
});

app.MapGet("/api/runs/{id}/audio", (string id) =>
{
    var run = pipeline.Runs.Resolve(id);
    return File.Exists(run.AudioPath)
        ? Results.File(run.AudioPath, "audio/mpeg", enableRangeProcessing: true)
        : Results.NotFound();
});

app.MapGet("/api/runs/{id}/video", (string id) =>
{
    var run = pipeline.Runs.Resolve(id);
    return File.Exists(run.VideoPath)
        ? Results.File(run.VideoPath, "video/mp4", enableRangeProcessing: true)
        : Results.NotFound();
});

// A browser page can't open a folder, but this server runs on the same PC, so it asks Explorer to.
// The path comes from the run's folder on disk — nothing from the request reaches the command line.
app.MapPost("/api/runs/{id}/reveal", (string id, ILogger<Program> logger) =>
{
    if (!OperatingSystem.IsWindows())
        return Results.BadRequest(new { error = "Opening a folder is only supported on Windows." });

    var run = pipeline.Runs.Resolve(id);

    // A video moved to Google Drive is opened where it now lives.
    if (pipeline.Runs.LoadManifest(run).Archive is { } moved)
    {
        if (string.IsNullOrWhiteSpace(moved.Location))
            return Results.NotFound(new { error = "Set your Google Drive folder in Settings → Storage to open it in File Explorer, or find it at drive.google.com." });
        if (!Directory.Exists(moved.Location))
            return Results.NotFound(new { error = $"{moved.Location} isn't showing yet. Is Google Drive for desktop running? A new upload can take a minute to appear." });
        YtPipeline.Web.ExplorerWindow.Reveal(moved.Location, isFile: false, logger);
        return J(new { opened = moved.Location });
    }

    if (!Directory.Exists(run.OutputDir))
        return Results.NotFound(new { error = "This video's folder no longer exists." });

    // With a finished video, open its folder with final.mp4 highlighted; before that, just the folder.
    var hasVideo = File.Exists(run.VideoPath);
    var target = hasVideo ? run.VideoPath : run.OutputDir;
    YtPipeline.Web.ExplorerWindow.Reveal(target, isFile: hasVideo, logger);
    return J(new { opened = target });
});

app.MapGet("/api/runs/{id}/thumbnail", (string id) =>
{
    var run = pipeline.Runs.Resolve(id);
    return File.Exists(run.ThumbnailPath) ? Results.File(run.ThumbnailPath, "image/png") : Results.NotFound();
});

var url = $"http://localhost:{port}";

// When launched as the packaged app (not `dotnet run`/preview), pop the browser open.
if (!app.Environment.IsDevelopment())
{
    app.Lifetime.ApplicationStarted.Register(() =>
    {
        Console.WriteLine();
        Console.WriteLine("  ▶ YtPipeline is running.");
        Console.WriteLine($"     Opening {url} in your browser…");
        Console.WriteLine("     Close this window to stop the app.");
        Console.WriteLine();
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch { /* headless / no browser — the URL is printed above */ }
    });
}

Log.Information("YtPipeline Web — workspace {Root}", workspaceRoot);
app.Run();

internal sealed record CreateScriptRequest(string Channel, string Topic, bool Auto = false);
internal sealed record ApproveRequest(string Stage);
internal sealed record CreateChannelRequest(string Id, YtPipeline.Core.Models.Channel? Channel);
internal sealed record ScheduleRequest(DateTime? ScheduledUtc);
