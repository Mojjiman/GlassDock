using System.Text.Json;
using GlassDock.Config;
using GlassDock.Logging;

namespace GlassDock.Taskbar;

/// <summary>
/// On-disk record of what we changed on Explorer's taskbar, written before any change so that the
/// watchdog, the next start or <c>--restore-taskbar</c> can undo it after a crash or power loss.
/// </summary>
public sealed class TaskbarJournal
{
    public int Version { get; set; } = 1;
    public uint OwnerPid { get; set; }
    public long OwnerStartFileTime { get; set; }
    public int OriginalAppBarState { get; set; }
    public List<string> HiddenClasses { get; set; } = [];

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static TaskbarJournal? TryLoad()
    {
        var path = AppPaths.TaskbarJournal;
        if (!File.Exists(path)) return null;
        try
        {
            return JsonSerializer.Deserialize<TaskbarJournal>(File.ReadAllText(path), Options);
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            Log.Error("Taskbar journal unreadable; restoring with defaults (taskbar shown, auto-hide off)", ex);
            return new TaskbarJournal { OriginalAppBarState = Interop.Native.ABS_ALWAYSONTOP };
        }
    }

    public void Save()
    {
        Directory.CreateDirectory(AppPaths.StateDir);
        AppPaths.WriteAtomic(AppPaths.TaskbarJournal, JsonSerializer.Serialize(this, Options));
    }

    public static void Delete()
    {
        try { File.Delete(AppPaths.TaskbarJournal); }
        catch (IOException ex) { Log.Error("Deleting taskbar journal failed", ex); }
    }

    /// <summary>True when the process that wrote this journal is still the one running.</summary>
    public bool IsOwnerAlive()
    {
        if (OwnerPid == 0) return false;
        var start = Interop.Native.GetProcessStartFileTime(OwnerPid);
        return start.HasValue && start.Value == OwnerStartFileTime;
    }
}
