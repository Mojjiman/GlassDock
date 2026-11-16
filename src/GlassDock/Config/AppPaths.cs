namespace GlassDock.Config;

public static class AppPaths
{
    public static string Root { get; } =
        Environment.GetEnvironmentVariable("GLASSDOCK_HOME") is { Length: > 0 } overridden
            ? overridden
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppConstants.AppName);

    public static string SettingsFile => Path.Combine(Root, "settings.json");
    public static string StateDir => Path.Combine(Root, "state");
    public static string TaskbarJournal => Path.Combine(StateDir, "taskbar-journal.json");
    public static string CrashLoopFile => Path.Combine(StateDir, "restarts.json");
    public static string PinsDir => Path.Combine(Root, "pins");
    public static string LogsDir => Path.Combine(Root, "logs");

    public static string ExePath => Environment.ProcessPath ?? throw new InvalidOperationException("Process path unknown");

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(StateDir);
        Directory.CreateDirectory(PinsDir);
    }

    /// <summary>Writes a file atomically: temp file then replace, so a crash never leaves half a file.</summary>
    public static void WriteAtomic(string path, string content, bool keepBackup = false)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, content, new System.Text.UTF8Encoding(false));
        if (File.Exists(path))
            File.Replace(tmp, path, keepBackup ? path + ".bak" : null, ignoreMetadataErrors: true);
        else
            File.Move(tmp, path);
    }
}
