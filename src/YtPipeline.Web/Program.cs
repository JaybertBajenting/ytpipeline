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
        return J(new
        {
            installed = result.Success,
            version = output.Length > 300 ? output[..300] : output,
            error = result.Success ? null : result.StdErr.Trim(),
        });
    }
    catch (Exception ex)
    {
        return J(new { installed = false, version = (string?)null, error = ex.Message });
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
    c.ScriptStructure,
    c.VisualStyle,
    c.VoiceNotes,
    c.Voice,
    c.Music,
    c.Youtube,
    hasImageRule = !string.IsNullOrWhiteSpace(c.ImageInstructions),
};

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
app.MapPost("/api/runs/{id}/stage/{stage}", (string id, string stage, string? privacy, bool force = false) =>
{
    var run = pipeline.Runs.Resolve(id);
    Func<CancellationToken, Task> work = stage.ToLowerInvariant() switch
    {
        "images" => ct => pipeline.ImagesAsync(run, ct, regenerate: force),
        "voice" => ct => pipeline.VoiceAsync(run, ct),
        "video" => ct => pipeline.VideoAsync(run, ct),
        "thumbnail" => ct => pipeline.ThumbnailAsync(run, ct),
        "upload" => ct => pipeline.UploadAsync(run, privacy ?? "private", ct),
        // Extra distribution targets. A skipped publish (platform not configured) completes the
        // job normally — the reason lands in the manifest and the log.
        "facebook" or "instagram" => async ct =>
        {
            var result = await pipeline.PublishAsync(run, stage.ToLowerInvariant(), ct);
            if (result.Skipped) Log.Warning("{Platform} skipped — {Reason}", result.Platform, result.SkipReason);
        },
        _ => throw new ArgumentException($"Unknown stage '{stage}'."),
    };
    var job = jobs.Start(run.RunId, stage, work);
    return J(new { jobId = job.Id });
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
