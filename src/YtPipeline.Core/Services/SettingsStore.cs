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
        s.VoiceGain = Math.Clamp(s.VoiceGain, 0.1, 4.0);
        s.MusicVolume = Math.Clamp(s.MusicVolume, 0.0, 1.0);
        s.Gflow ??= new GflowSettings();
        if (string.IsNullOrWhiteSpace(s.Gflow.Executable)) s.Gflow.Executable = "gflow";
        if (s.Gflow.ArgsTemplate.Count == 0) s.Gflow.ArgsTemplate = new List<string> { "image", "t2i" };
        if (string.IsNullOrWhiteSpace(s.Gflow.OutputFlag)) s.Gflow.OutputFlag = "-o";
        if (s.Gflow.CheckArgs.Count == 0) s.Gflow.CheckArgs = new List<string> { "--version" };
        // Never let the aspect fall back to gflow's vertical 9:16 default.
        if (string.IsNullOrWhiteSpace(s.Gflow.Aspect)) s.Gflow.Aspect = "16:9";
        if (string.IsNullOrWhiteSpace(s.Gflow.Model)) s.Gflow.Model = "nano-pro";
        s.Gflow.TimeoutSeconds = Math.Clamp(s.Gflow.TimeoutSeconds, 30, 3600);
        s.Gflow.MaxConcurrent = Math.Clamp(s.Gflow.MaxConcurrent, 1, 8);
    }
}
