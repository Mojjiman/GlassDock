using System.Windows.Threading;
using GlassDock.Logging;

namespace GlassDock.Stacks;

public sealed record FolderEntry(string Path, string Name, bool IsDirectory, DateTime Modified);

/// <summary>Reads what a stack shows: visible entries of one folder, sorted like the pin asks.</summary>
public static class FolderContents
{
    public const string SortDate = "date";
    public const string SortName = "name";

    /// <summary>Newest first for "date"; folders first, then A→Z for "name". Hidden and system entries are skipped.</summary>
    public static (List<FolderEntry> Items, int Total) Read(string folder, string? sort, int max)
    {
        var dir = new DirectoryInfo(folder);
        var all = new List<FolderEntry>();
        foreach (var e in dir.EnumerateFileSystemInfos())
        {
            if ((e.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0) continue;
            all.Add(new FolderEntry(e.FullName, e.Name, e is DirectoryInfo, e.LastWriteTime));
        }
        IEnumerable<FolderEntry> sorted = sort == SortName
            ? all.OrderByDescending(e => e.IsDirectory).ThenBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase)
            : all.OrderByDescending(e => e.Modified);
        return (sorted.Take(max).ToList(), all.Count);
    }
}

/// <summary>
/// Raises <see cref="Changed"/> on the dispatcher once a folder has been quiet for a moment, so a
/// download that writes for minutes does not trigger a refresh per write.
/// </summary>
public sealed class FolderWatcher : IDisposable
{
    private readonly FileSystemWatcher? _fsw;
    private readonly Timer _debounce;
    private readonly int _debounceMs;
    private readonly Dispatcher _dispatcher;
    private volatile bool _disposed;

    public event Action? Changed;

    public FolderWatcher(string folder, Dispatcher dispatcher, int debounceMs)
    {
        _dispatcher = dispatcher;
        _debounceMs = debounceMs;
        _debounce = new Timer(_ => _dispatcher.BeginInvoke(() => { if (!_disposed) Changed?.Invoke(); }));
        try
        {
            _fsw = new FileSystemWatcher(folder)
            {
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite,
            };
            _fsw.Created += (_, _) => Kick();
            _fsw.Deleted += (_, _) => Kick();
            _fsw.Renamed += (_, _) => Kick();
            _fsw.Changed += (_, _) => Kick();
            // Buffer overflow (many changes at once): the listing is simply re-read.
            _fsw.Error += (_, e) => { Log.Warn($"Folder watcher error for {folder}; refreshing", e.GetException()); Kick(); };
            _fsw.EnableRaisingEvents = true;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Cannot watch {folder}; the stack will not update by itself", ex);
            _fsw?.Dispose();
            _fsw = null;
        }
    }

    private void Kick()
    {
        if (!_disposed) _debounce.Change(_debounceMs, Timeout.Infinite);
    }

    public void Dispose()
    {
        _disposed = true;
        _fsw?.Dispose();
        _debounce.Dispose();
    }
}
