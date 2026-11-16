using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text;
using GlassDock.Config;

namespace GlassDock.Logging;

public enum LogLevel { Debug, Info, Warning, Error }

/// <summary>
/// Minimal file logger. Writes are queued and flushed by a background thread so a slow disk
/// never blocks the UI thread or the keyboard-hook thread.
/// </summary>
public static class Log
{
    private static readonly BlockingCollection<string> Queue = new(new ConcurrentQueue<string>());
    private static Thread? _writer;
    private static string _directory = "";
    private static string _processTag = "main";

    public static LogLevel MinLevel { get; set; } = LogLevel.Info;

    public static string Directory => _directory;

    public static void Initialize(string processTag)
    {
        _processTag = processTag;
        _directory = AppPaths.LogsDir;
        System.IO.Directory.CreateDirectory(_directory);
        CleanupOldLogs();
        _writer = new Thread(WriterLoop) { IsBackground = true, Name = "GlassDock.Log" };
        _writer.Start();
    }

    public static void Debug(string message, [CallerMemberName] string op = "") => Write(LogLevel.Debug, op, message, null);
    public static void Info(string message, [CallerMemberName] string op = "") => Write(LogLevel.Info, op, message, null);
    public static void Warn(string message, Exception? ex = null, [CallerMemberName] string op = "") => Write(LogLevel.Warning, op, message, ex);
    public static void Error(string message, Exception? ex = null, [CallerMemberName] string op = "") => Write(LogLevel.Error, op, message, ex);

    /// <summary>Logs a failed Win32 call together with its last error code.</summary>
    public static void Win32Failure(string api, [CallerMemberName] string op = "")
    {
        int err = System.Runtime.InteropServices.Marshal.GetLastPInvokeError();
        Write(LogLevel.Warning, op, $"{api} failed, Win32 error {err} (0x{err:X})", null);
    }

    public static void Write(LogLevel level, string op, string message, Exception? ex)
    {
        if (level < MinLevel) return;
        var sb = new StringBuilder(128);
        sb.Append(DateTime.Now.ToString("HH:mm:ss.fff")).Append(' ')
          .Append(level switch { LogLevel.Debug => "DBG", LogLevel.Info => "INF", LogLevel.Warning => "WRN", _ => "ERR" })
          .Append(" [").Append(_processTag).Append(':').Append(Environment.CurrentManagedThreadId).Append("] ")
          .Append(op).Append(": ").Append(message);
        if (ex != null) sb.AppendLine().Append(ex);
        if (!Queue.IsAddingCompleted) Queue.Add(sb.ToString());
    }

    public static void Flush(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (Queue.Count > 0 && DateTime.UtcNow < deadline) Thread.Sleep(10);
    }

    private static string CurrentFile() => Path.Combine(_directory, $"glassdock-{DateTime.Now:yyyyMMdd}.log");

    private static void WriterLoop()
    {
        foreach (var line in Queue.GetConsumingEnumerable())
        {
            try
            {
                // Watchdog and dock write the same file; open/append/close keeps both writers safe.
                using var fs = new FileStream(CurrentFile(), FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                using var sw = new StreamWriter(fs, new UTF8Encoding(false));
                sw.WriteLine(line);
                while (Queue.TryTake(out var more)) sw.WriteLine(more);
            }
            catch (IOException ioEx)
            {
                // Nowhere else to report a log failure; the debugger output is the last resort.
                System.Diagnostics.Debug.WriteLine($"GlassDock log write failed: {ioEx.Message}\n{line}");
            }
        }
    }

    private static void CleanupOldLogs()
    {
        try
        {
            var cutoff = DateTime.Now.AddDays(-AppConstants.Logging.RetainDays);
            foreach (var f in new DirectoryInfo(_directory).GetFiles("glassdock-*.log"))
                if (f.LastWriteTime < cutoff) f.Delete();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Debug.WriteLine($"GlassDock log cleanup failed: {ex.Message}");
        }
    }
}
