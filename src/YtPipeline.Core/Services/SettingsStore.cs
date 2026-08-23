using YtPipeline.Core.Models;

namespace YtPipeline.Core.Services;

/// <summary>
/// Loads and saves the user-editable <see cref="AppSettings"/> (<c>settings.json</c> in the
/// workspace root). Held as a single mutable instance so a save from the Settings tab takes
/// effect on the next stage run — no app restart needed.
/// </summary>
public sealed class SettingsStore
{
    private readonly string _path;
    private readonly Lock _gate = new();
    private AppSettings _current;

    public SettingsStore(string workspaceRoot)
    {
        _path = Path.Combine(workspaceRoot, "settings.json");
        _current = Load();
    }

    /// <summary>The live settings. Treat as read-only; call <see cref="Save"/> to change them.</summary>
    public AppSettings Current
    {
        get { lock (_gate) return _current; }
    }

    public AppSettings Save(AppSettings settings)
    {
        Normalize(settings);
        lock (_gate)
        {
            Json.WriteFile(_path, settings);   // BOM-less UTF-8
            _current = settings;
            return settings;
        }
    }

    private AppSettings Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                var loaded = Json.ReadFile<AppSettings>(_path);
                Normalize(loaded);
                return loaded;
            }
        }
        catch
        {
            // Corrupt or unreadable settings must never stop the app — fall back to defaults.
        }
        return new AppSettings();
    }

    /// <summary>Clamps values to sane ranges so a bad edit can't produce silent or blown-out audio.</summary>
    private static void Normalize(AppSettings s)
    {
        s.ImageProvider = s.ImageProvider?.Trim().ToLowerInvariant() switch
        {
            ImageProviders.Gflow => ImageProviders.Gflow,
            _ => ImageProviders.Codex,
        };

        s.ImageFallbackProvider = s.ImageFallbackProvider?.Trim().ToLowerInvariant() switch
        {
            ImageProviders.Gflow => ImageProviders.Gflow,
            ImageProviders.Codex => ImageProviders.Codex,
            _ => "",
        };
        // A fallback equal to the primary would just re-run the same exhausted backend.
        if (s.ImageFallbackProvider == s.ImageProvider) s.ImageFallbackProvider = "";

        // Empty is meaningful here — it means "follow ImageProvider" — so an unrecognised value
        // clears the override rather than defaulting to a backend the user never chose.
        s.ThumbnailProvider = s.ThumbnailProvider?.Trim().ToLowerInvariant() switch
        {
            ImageProviders.Gflow => ImageProviders.Gflow,
            ImageProviders.Codex => ImageProviders.Codex,
            _ => "",
        };
        // Naming the same backend the frames already use is just the inherited behaviour.
        if (s.ThumbnailProvider == s.ImageProvider) s.ThumbnailProvider = "";

        s.QuotaCooldownMinutes = Math.Clamp(s.QuotaCooldownMinutes, 5, 24 * 60);
        s.DefaultWordsPerMinute = Math.Clamp(s.DefaultWordsPerMinute, 60, 320);
        s.VoiceGain = Math.Clamp(s.VoiceGain, 0.1, 4.0);
        s.MusicVolume = Math.Clamp(s.MusicVolume, 0.0, 1.0);
        s.Gflow ??= new GflowSettings();
        if (string.IsNullOrWhiteSpace(s.Gflow.Executable)) s.Gflow.Executable = "gflow";
        if (s.Gflow.ArgsTemplate.Count == 0) s.Gflow.ArgsTemplate = new List<string> { "image", "t2i" };
        if (string.IsNullOrWhiteSpace(s.Gflow.OutputFlag)) s.Gflow.OutputFlag = "-o";
        if (s.Gflow.CheckArgs.Count == 0) s.Gflow.CheckArgs = new List<string> { "--version" };
        // Never let the aspect fall back to gflow's vertical 9:16 default. The value must be one of
        // gflow's NAMES (landscape|4x3|square|3x4|portrait) — a ratio like "16:9" is rejected with
        // "invalid choice" and every call fails, so the default has to be a name too.
        if (string.IsNullOrWhiteSpace(s.Gflow.Aspect)) s.Gflow.Aspect = "landscape";
        if (string.IsNullOrWhiteSpace(s.Gflow.Model)) s.Gflow.Model = "nano-pro";
        s.Gflow.TimeoutSeconds = Math.Clamp(s.Gflow.TimeoutSeconds, 30, 3600);
        s.Gflow.MaxConcurrent = Math.Clamp(s.Gflow.MaxConcurrent, 1, 8);
    }
}
