using System.Text.Json;
using System.Text.Json.Serialization;
using GlassDock.Logging;

namespace GlassDock.Config;

/// <summary>
/// Owns the live <see cref="DockSettings"/>. All changes go through <see cref="Update"/> so listeners
/// see every change and saving is debounced.
/// </summary>
public sealed class SettingsStore : IDisposable
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly string _path;
    private readonly Timer _saveTimer;
    private readonly object _gate = new();

    public DockSettings Current { get; private set; }

    /// <summary>Raised on the thread that called <see cref="Update"/> (always the UI thread in practice).</summary>
    public event EventHandler<DockSettings>? Changed;

    /// <summary>Non-null when the settings file was unreadable and defaults were loaded instead.</summary>
    public string? LoadProblem { get; private set; }

    public SettingsStore(string path)
    {
        _path = path;
        _saveTimer = new Timer(_ => SaveNow(), null, Timeout.Infinite, Timeout.Infinite);
        Current = Load();
    }

    private DockSettings Load()
    {
        if (!File.Exists(_path))
        {
            Log.Info($"No settings file at {_path}, using defaults");
            return new DockSettings();
        }
        try
        {
            var s = JsonSerializer.Deserialize<DockSettings>(File.ReadAllText(_path), JsonOptions) ?? new DockSettings();
            Migrate(s);
            return s;
        }
        catch (Exception ex) when (ex is JsonException or IOException or NotSupportedException)
        {
            // Never silently reset: keep the broken file next to the new one and tell the user.
            var corrupt = Path.Combine(Path.GetDirectoryName(_path)!, $"settings.corrupt-{DateTime.Now:yyyyMMdd-HHmmss}.json");
            try { File.Move(_path, corrupt); }
            catch (IOException moveEx) { Log.Error("Could not move corrupt settings file aside", moveEx); }
            LoadProblem = $"설정 파일을 읽지 못해 기본값으로 시작했습니다. 원래 파일: {corrupt}";
            Log.Error($"Settings file unreadable, defaults loaded; original moved to {corrupt}", ex);
            return new DockSettings();
        }
    }

    private static void Migrate(DockSettings s)
    {
        if (s.SchemaVersion > DockSettings.CurrentSchemaVersion)
            Log.Warn($"Settings schema {s.SchemaVersion} is newer than this build ({DockSettings.CurrentSchemaVersion}); unknown fields are preserved");
        // Ordered migrations go here when the schema changes.
        if (s.SchemaVersion < 2)
        {
            // v1 suspended the hook in fullscreen by default, which silently broke Win+1..0 in games.
            // Nobody chose that value, so reset it to the new default.
            s.Shortcuts.SuspendHookInFullscreen = false;
        }
        s.Tiles ??= TileSpec.Defaults();
        if (s.SchemaVersion < 3 && !s.Tiles.Any(t => t.Type == "showDesktop"))
        {
            // Show desktop became a default tile, where Windows keeps it: at the far right. A disabled entry means
            // the user hid it, so only a list without one gets it.
            s.Tiles.Add(new TileSpec { Id = TileSpec.ShowDesktopId, Type = "showDesktop" });
        }
        if (s.SchemaVersion < DockSettings.CurrentSchemaVersion) s.SchemaVersion = DockSettings.CurrentSchemaVersion;
        s.Apps.Items ??= [];
    }

    public void Update(Action<DockSettings> mutate)
    {
        lock (_gate) mutate(Current);
        Changed?.Invoke(this, Current);
        _saveTimer.Change(AppConstants.Settings.SaveDebounceMs, Timeout.Infinite);
    }

    public void SaveNow()
    {
        string json;
        lock (_gate) json = JsonSerializer.Serialize(Current, JsonOptions);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            AppPaths.WriteAtomic(_path, json, keepBackup: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error($"Saving settings to {_path} failed", ex);
        }
    }

    public void Dispose()
    {
        _saveTimer.Dispose();
        SaveNow();
    }
}
