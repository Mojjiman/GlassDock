using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using GlassDock.Config;
using GlassDock.Logging;
using U = GlassDock.Config.AppConstants.Update;

namespace GlassDock.Update;

/// <summary>A newer release on GitHub and the zip asset that carries it.</summary>
public sealed record UpdateInfo(Version Version, string Tag, string ZipUrl, long ZipSize, string? Sha256, string PageUrl);

/// <summary>
/// Checks the public release repository for a newer build and installs it in place. The release zip
/// holds a self-contained single-file GlassDock.exe: the running exe is renamed aside (Windows allows
/// renaming a running image, not overwriting it), the new one takes its name, and a new process is
/// started that waits for this one to quit before taking the single-instance mutex.
/// </summary>
public sealed class UpdateService : IDisposable
{
    public static Version Current { get; } = Trim(typeof(UpdateService).Assembly.GetName().Version ?? new Version(0, 0, 0));

    /// <summary>
    /// A single-file publish has no assembly path on disk. A Debug build runs GlassDock.dll through an
    /// apphost, so swapping the exe would leave a mismatched pair; it can check but not install.
    /// </summary>
    public static bool CanInstall { get; } = string.IsNullOrEmpty(typeof(UpdateService).Assembly.Location);

    // After Current: static initializers run in order, and the User-Agent needs the version.
    private static readonly HttpClient Http = CreateClient();
    private readonly Timer _timer;
    private int _busy;

    /// <summary>Tests point this at a local server instead of GitHub.</summary>
    private static string ApiUrl => Environment.GetEnvironmentVariable(U.ApiOverrideEnv) is { Length: > 0 } url ? url : U.LatestReleaseApi;

    public UpdateInfo? Available { get; private set; }
    public event Action? Changed;

    public UpdateService(Func<bool> autoCheckEnabled)
    {
        // Right after an update the old watchdog may still hold the old exe; try once more a little later.
        if (File.Exists(AppPaths.ExePath + U.OldSuffix))
            _ = Task.Delay(TimeSpan.FromSeconds(U.CleanUpRetrySeconds)).ContinueWith(_ => CleanUpAfterUpdate(), TaskScheduler.Default);
        _timer = new Timer(_ => { if (autoCheckEnabled()) _ = CheckInBackgroundAsync(); },
            null, TimeSpan.FromSeconds(U.FirstCheckDelaySeconds), TimeSpan.FromHours(U.CheckIntervalHours));
    }

    /// <summary>The previous exe stays beside the new one until the next start, when it is no longer running.</summary>
    public static void CleanUpAfterUpdate()
    {
        string old = AppPaths.ExePath + U.OldSuffix;
        if (!File.Exists(old)) return;
        try { File.Delete(old); Log.Info($"Removed the previous version ({Path.GetFileName(old)})"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Warn($"Could not remove {old} yet", ex); }
    }

    /// <summary>When the updated build starts, waits for the old process to quit and release the mutex.</summary>
    public static void WaitForPreviousInstance(string[] args)
    {
        int i = Array.IndexOf(args, U.WaitPidArg);
        if (i < 0 || i + 1 >= args.Length || !int.TryParse(args[i + 1], out int pid)) return;
        try
        {
            using var p = Process.GetProcessById(pid);
            if (!p.WaitForExit(U.WaitForOldMs)) Log.Warn($"Previous instance {pid} still running after {U.WaitForOldMs} ms");
        }
        catch (ArgumentException) { Log.Debug($"Previous instance {pid} already exited"); }
    }

    /// <summary>The timer's check: nobody awaits it, so an unexpected failure is logged here instead of lost.</summary>
    private async Task CheckInBackgroundAsync()
    {
        try { await CheckAsync(); }
        catch (Exception ex) { Log.Error("Background update check failed", ex); }
    }

    /// <returns>The newer release, or null when this build is current or the check failed (logged).</returns>
    public async Task<UpdateInfo?> CheckAsync()
    {
        try
        {
            using var doc = JsonDocument.Parse(await Http.GetStringAsync(ApiUrl));
            var root = doc.RootElement;
            string tag = root.GetProperty("tag_name").GetString() ?? "";
            if (!Version.TryParse(tag.TrimStart('v', 'V'), out var version))
            {
                Log.Warn($"Latest release tag '{tag}' is not a version");
                return null;
            }
            version = Trim(version);
            UpdateInfo? info = null;
            if (version > Current)
            {
                foreach (var asset in root.GetProperty("assets").EnumerateArray())
                {
                    string name = asset.GetProperty("name").GetString() ?? "";
                    if (!name.StartsWith(U.ZipPrefix, StringComparison.OrdinalIgnoreCase) || !name.EndsWith(U.ZipSuffix, StringComparison.OrdinalIgnoreCase)) continue;
                    string? digest = asset.TryGetProperty("digest", out var d) ? d.GetString() : null;
                    info = new UpdateInfo(version, tag, asset.GetProperty("browser_download_url").GetString()!, asset.GetProperty("size").GetInt64(),
                        digest is { } dg && dg.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? dg[7..] : null,
                        root.GetProperty("html_url").GetString() ?? U.ReleasesPage);
                    break;
                }
                if (info == null) Log.Warn($"Release {tag} has no {U.ZipPrefix}*{U.ZipSuffix} asset");
            }
            Log.Info(info != null ? $"Update available: {Current} -> {info.Version}" : $"Up to date ({Current}; latest {version})");
            if (!Equals(info, Available)) { Available = info; Changed?.Invoke(); }
            return info;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            Log.Warn($"Update check failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>Downloads and verifies the zip, swaps the exe and starts it. The caller quits on true.</summary>
    public async Task<bool> InstallAsync(UpdateInfo info, IProgress<double>? progress = null)
    {
        if (!CanInstall) { Log.Warn("Not a published build; update install skipped"); return false; }
        if (Interlocked.Exchange(ref _busy, 1) == 1) return false;
        string exe = AppPaths.ExePath;
        string zip = Path.Combine(Path.GetTempPath(), $"GlassDock-update-{info.Version}.zip");
        string staged = exe + U.NewSuffix, old = exe + U.OldSuffix;
        try
        {
            Log.Info($"Downloading {info.ZipUrl}");
            using (var response = await Http.GetAsync(info.ZipUrl, HttpCompletionOption.ResponseHeadersRead))
            {
                response.EnsureSuccessStatusCode();
                await using var input = await response.Content.ReadAsStreamAsync();
                await using var output = File.Create(zip);
                var buffer = new byte[81920];
                long done = 0;
                int n;
                while ((n = await input.ReadAsync(buffer)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, n));
                    done += n;
                    progress?.Report(info.ZipSize > 0 ? (double)done / info.ZipSize : 0);
                }
            }
            if (info.Sha256 != null)
            {
                await using var check = File.OpenRead(zip);
                string actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(check));
                if (!string.Equals(actual, info.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    Log.Error($"Update zip checksum mismatch (expected {info.Sha256}, got {actual})");
                    return false;
                }
            }
            else Log.Warn("Release asset has no checksum; installing without verification");

            using (var archive = ZipFile.OpenRead(zip))
            {
                var entry = archive.Entries.FirstOrDefault(e => e.Name.Equals(Path.GetFileName(exe), StringComparison.OrdinalIgnoreCase));
                if (entry == null) { Log.Error($"{Path.GetFileName(exe)} not found in the update zip"); return false; }
                entry.ExtractToFile(staged, overwrite: true);
            }

            if (File.Exists(old)) File.Delete(old);
            File.Move(exe, old);
            try { File.Move(staged, exe); }
            catch
            {
                File.Move(old, exe); // put the running build back under its name
                throw;
            }
            Process.Start(new ProcessStartInfo(exe, $"{U.WaitPidArg} {Environment.ProcessId}") { UseShellExecute = false });
            Log.Info($"Installed {info.Version}; restarting");
            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException or InvalidDataException or System.ComponentModel.Win32Exception)
        {
            Log.Error("Update install failed", ex);
            return false;
        }
        finally
        {
            TryDelete(zip);
            TryDelete(staged);
            Interlocked.Exchange(ref _busy, 0);
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Debug($"Could not delete {path}: {ex.Message}"); }
    }

    private static Version Trim(Version v) => new(v.Major, v.Minor, Math.Max(0, v.Build));

    private static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(U.HttpTimeoutSeconds) };
        // GitHub's API refuses requests without a User-Agent.
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"GlassDock/{Current}");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return http;
    }

    public void Dispose() => _timer.Dispose();
}
