using FluentValidation;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Refit;
using YtPipeline.Core.Abstractions;
using YtPipeline.Core.Models;
using YtPipeline.Core.Services;
using YtPipeline.Core.Validation;
using YtPipeline.Stages.Images;
using YtPipeline.Stages.Music;
using YtPipeline.Stages.Orchestration;
using YtPipeline.Stages.Process;
using YtPipeline.Stages.Publish;
using YtPipeline.Stages.Script;
using YtPipeline.Stages.Upload;
using YtPipeline.Stages.Video;
using YtPipeline.Stages.Voice;

namespace YtPipeline.Stages;

/// <summary>
/// Single place that registers the whole pipeline (services, stage options, stage
/// implementations, the ElevenLabs HTTP client). Shared by the CLI and Web hosts so both
/// wire up identically. Logging is left to each host. Secrets come from environment
/// variables (load a .env first if you use one).
/// </summary>
public static class PipelineServiceRegistration
{
    public static IServiceCollection AddYtPipeline(
        this IServiceCollection services, IConfiguration config, string workspaceRoot, string channelsDir)
    {
        services.AddSingleton(new RunManager(workspaceRoot));
        services.AddSingleton(new ChannelLoader(channelsDir));
        services.AddSingleton<ProcessRunner>();
        services.AddSingleton<IValidator<ScriptDocument>, ScriptDocumentValidator>();

        // User-editable settings (settings.json). Loaded first so the values below can be
        // overlaid onto the appsettings.json defaults.
        var settingsStore = new SettingsStore(workspaceRoot);
        services.AddSingleton(settingsStore);

        // Publishing credentials (secrets.json) — kept out of settings.json so the latter
        // stays free of anything that shouldn't be shared.
        services.AddSingleton(new SecretsStore(workspaceRoot));

        // Which providers are quota-benched, and when to retry them. Operational state, so it
        // lives beside settings rather than inside them.
        services.AddSingleton(new ProviderHealthStore(workspaceRoot));

        // Stage options. NOTE: the config binder APPENDS to existing List<T> values, so we
        // clear the constructor defaults before binding to avoid duplicated CLI args.
        var claude = new ClaudeOptions();
        BindSection(config.GetSection("Claude"), claude, claude.Args);
        services.AddSingleton(claude);

        var codex = new CodexOptions();
        BindSection(config.GetSection("Codex"), codex, codex.ArgsTemplate);
        services.AddSingleton(codex);

        // Audio options: appsettings.json provides the defaults, settings.json overrides them
        // so the Settings tab is the single source of truth once the user has touched it.
        var ffmpeg = config.GetSection("Ffmpeg").Get<FfmpegOptions>() ?? new FfmpegOptions();
        ffmpeg.VoiceGain = settingsStore.Current.VoiceGain;
        services.AddSingleton(ffmpeg);

        var music = config.GetSection("Music").Get<MusicOptions>() ?? new MusicOptions();
        music.Enabled = settingsStore.Current.MusicEnabled;
        music.Volume = settingsStore.Current.MusicVolume;
        services.AddSingleton(music);

        var eleven = config.GetSection("ElevenLabs").Get<ElevenLabsOptions>() ?? new ElevenLabsOptions();
        eleven.VoiceId = Environment.GetEnvironmentVariable("ELEVENLABS_VOICE_ID") ?? eleven.VoiceId;
        services.AddSingleton(eleven);

        var youtube = config.GetSection("YouTube").Get<YouTubeOptions>() ?? new YouTubeOptions();
        youtube.ClientSecretsPath = Path.GetFullPath(
            Environment.GetEnvironmentVariable("YOUTUBE_CLIENT_SECRETS") ?? youtube.ClientSecretsPath, workspaceRoot);
        youtube.TokenStoreDir = Path.GetFullPath(youtube.TokenStoreDir, workspaceRoot);
        services.AddSingleton(youtube);

        // Stage 1 & 2 are also registered concretely so the CLI --dry-run can build their prompts.
        services.AddSingleton<ClaudeCliScriptGenerator>();
        services.AddSingleton<IScriptGenerator>(sp => sp.GetRequiredService<ClaudeCliScriptGenerator>());
        services.AddSingleton<PhoneticNarrationCleaner>();
        // Not a stage — writes a channel's About text on demand from the Channels screen.
        services.AddSingleton<ChannelIdentityWriter>();
        services.AddSingleton<MusicSelector>();
        // Both image backends are registered; the router picks per call from Settings.
        services.AddSingleton<CodexCliImageGenerator>();
        services.AddSingleton<GflowCliImageGenerator>();
        services.AddSingleton<IImageGenerator, ImageGeneratorRouter>();
        services.AddSingleton<IVoiceGenerator, ElevenLabsVoiceGenerator>();
        services.AddSingleton<IVideoAssembler, FfmpegVideoAssembler>();
        services.AddSingleton<IVideoUploader, YouTubeUploader>();

        // Extra distribution targets. Registered as an IEnumerable so Pipeline can resolve one by
        // its Platform key and adding a platform means adding one registration here.
        var meta = config.GetSection("MetaGraph").Get<MetaGraphOptions>() ?? new MetaGraphOptions();
        services.AddSingleton(meta);
        services.AddHttpClient();
        services.AddSingleton<ISocialPublisher, FacebookVideoPublisher>();
        services.AddSingleton<ISocialPublisher, InstagramVideoPublisher>();

        services.AddSingleton<Pipeline>();

        // ElevenLabs HTTP client (Refit).
        var elevenBaseUrl = config["ElevenLabs:BaseUrl"] ?? "https://api.elevenlabs.io";
        var elevenApiKey = Environment.GetEnvironmentVariable("ELEVENLABS_API_KEY") ?? "";
        services.AddRefitClient<IElevenLabsApi>()
            .ConfigureHttpClient(c =>
            {
                c.BaseAddress = new Uri(elevenBaseUrl);
                if (!string.IsNullOrEmpty(elevenApiKey)) c.DefaultRequestHeaders.Add("xi-api-key", elevenApiKey);
                c.Timeout = TimeSpan.FromMinutes(5);
            });

        return services;
    }

    private static void BindSection(IConfigurationSection section, object target, System.Collections.IList listToReset)
    {
        if (!section.Exists()) return;
        listToReset.Clear();      // avoid the binder appending to constructor defaults
        section.Bind(target);
    }
}
