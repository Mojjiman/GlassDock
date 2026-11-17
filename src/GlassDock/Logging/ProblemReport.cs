using System.IO.Compression;
using GlassDock.Config;

namespace GlassDock.Logging;

/// <summary>
/// One zip on the Desktop with the recent logs and settings.json, for friends to send when something goes
/// wrong; finding %APPDATA% was the hard part.
/// </summary>
public static class ProblemReport
{
    /// <returns>The zip's path, or null if it could not be written (logged).</returns>
    public static string? Create()
    {
        string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        string path = Path.Combine(desktop, $"GlassDock-문제보고-{DateTime.Now:yyyyMMdd-HHmm}.zip");
        try
        {
            Log.Flush(TimeSpan.FromSeconds(1));
            var since = DateTime.Now.AddDays(-AppConstants.Settings.ReportLogDays);
            var files = Directory.Exists(Log.Directory)
                ? new DirectoryInfo(Log.Directory).GetFiles().Where(f => f.LastWriteTime >= since).Select(f => (f.FullName, "logs/" + f.Name)).ToList()
                : [];
            if (File.Exists(AppPaths.SettingsFile)) files.Add((AppPaths.SettingsFile, "settings.json"));
            using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
            {
                foreach (var (source, name) in files)
                {
                    // Today's log is still open for writing; share it instead of failing on the lock.
                    using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using var output = zip.CreateEntry(name, CompressionLevel.Optimal).Open();
                    input.CopyTo(output);
                }
            }
            Log.Info($"Problem report written: {path} ({files.Count} files)");
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error($"Could not write the problem report {path}", ex);
            return null;
        }
    }
}
