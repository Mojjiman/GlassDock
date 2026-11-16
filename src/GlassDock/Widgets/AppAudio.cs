using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using GlassDock.Logging;
using static GlassDock.Interop.CoreAudio;
using static GlassDock.Interop.Native;

namespace GlassDock.Widgets;

/// <summary>
/// One app's slider in the Windows volume mixer: every audio session that belongs to the app, on any
/// active output. Apps can play to a device other than the default (the mixer's per-app output, or
/// routers like SteelSeries Sonar that give each app its own virtual device). Chromium browsers play all audio (tabs and installed web apps alike) from one process,
/// so for an Edge or Chrome web app this is the whole browser's volume.
/// </summary>
public sealed class AppAudio
{
    private const int S_OK = 0;
    private readonly List<ISimpleAudioVolume> _volumes;
    private Guid _context = Guid.NewGuid();

    /// <summary>The first matching process's exe, for the app's icon and name when the app id has none.</summary>
    public string? ExePath { get; }
    /// <summary>Matched through the window's host exe (a web app in a browser): the volume is the host's.</summary>
    public bool Hosted { get; }

    /// <summary>The system sounds session (notification and alert sounds), which has no process.</summary>
    public bool IsSystemSounds { get; }
    /// <summary>A name the app gave its session, if any (resource references like <c>@%SystemRoot%...</c> are dropped).</summary>
    public string? SessionName { get; private set; }
    /// <summary>Some session was making sound when the list was read (peak meter above zero).</summary>
    public bool Playing { get; private set; }
    /// <summary>Some session has an open stream, even if it is silent right now.</summary>
    public bool Active { get; private set; }

    private AppAudio(List<ISimpleAudioVolume> volumes, string? exePath, bool hosted, bool systemSounds = false)
    {
        _volumes = volumes;
        ExePath = exePath;
        Hosted = hosted;
        IsSystemSounds = systemSounds;
    }

    /// <summary>Identifies the app across refreshes, so the mixer flyout keeps its rows while the set of apps stays the same.</summary>
    public string Key => IsSystemSounds ? "" : ExePath ?? "";

    /// <summary>
    /// Every app with a live audio session on an active output, one entry per exe, the way the system
    /// mixer lists them, plus the system sounds session.
    /// </summary>
    public static List<AppAudio> All()
    {
        AppAudio? system = null;
        var byExe = new Dictionary<string, AppAudio>(StringComparer.OrdinalIgnoreCase);
        uint self = (uint)Environment.ProcessId;
        ForEachSession("all apps", (session, pid) =>
        {
            AppAudio app;
            if (session.IsSystemSoundsSession() == S_OK) app = system ??= new AppAudio([], null, false, systemSounds: true);
            else if (pid == 0 || pid == self || GetProcessPath(pid) is not { } path) return;
            else if (!byExe.TryGetValue(path, out app!)) byExe[path] = app = new AppAudio([], path, false);
            app.Add(session);
        });
        var apps = new List<AppAudio>();
        if (system != null) apps.Add(system);
        apps.AddRange(byExe.Values);
        return apps;
    }

    private void Add(IAudioSessionControl2 session)
    {
        _volumes.Add((ISimpleAudioVolume)session);
        session.GetState(out int state);
        Active |= state == AudioSessionStateActive;
        try
        {
            ((IAudioMeterInformation)session).GetPeakValue(out float peak);
            Playing |= peak > 0;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            // Only the ordering uses this; the app still gets its row.
            Log.Debug($"Peak meter unavailable for {ExePath ?? "system sounds"}: {ex.Message}");
        }
        if (SessionName == null)
        {
            session.GetDisplayName(out string name);
            if (!string.IsNullOrWhiteSpace(name) && !name.StartsWith('@')) SessionName = name.Trim();
        }
    }

    /// <param name="appId">
    /// A media session's source: a packaged app id (<c>family!app</c>), an exe name (<c>Spotify.exe</c>),
    /// or a desktop app id that names its exe (<c>MSEdge</c>, <c>Chrome</c>).
    /// </param>
    /// <param name="exeHint">The exe of a running window with that app id, for ids that name none of those (web apps).</param>
    public static AppAudio? Find(string appId, string? exeHint)
    {
        int bang = appId.IndexOf('!');
        string? family = bang > 0 ? appId[..bang] : null;
        string idExe = appId.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? appId : appId + ".exe";
        string? hintExe = exeHint != null ? Path.GetFileName(exeHint) : null;
        var found = new List<ISimpleAudioVolume>();
        string? exePath = null;
        bool hosted = false;
        ForEachSession(appId, (session, pid) =>
        {
            if (pid == 0) return;
            string? path = GetProcessPath(pid);
            string? exe = path != null ? Path.GetFileName(path) : null;
            bool byId = (family != null && string.Equals(PackageFamily(pid), family, StringComparison.OrdinalIgnoreCase))
                || (family == null && string.Equals(exe, idExe, StringComparison.OrdinalIgnoreCase));
            bool byHint = !byId && hintExe != null && string.Equals(exe, hintExe, StringComparison.OrdinalIgnoreCase);
            if (!byId && !byHint) return;
            found.Add((ISimpleAudioVolume)session);
            exePath ??= path;
            hosted |= byHint;
        });
        return found.Count > 0 ? new AppAudio(found, exePath, hosted) : null;
    }

    /// <summary>Calls <paramref name="visit"/> with each live session on every active output and its process id (0 when it has none).</summary>
    private static void ForEachSession(string what, Action<IAudioSessionControl2, uint> visit)
    {
        try
        {
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
            enumerator.EnumAudioEndpoints(eRender, DEVICE_STATE_ACTIVE, out var devices);
            devices.GetCount(out int deviceCount);
            for (int d = 0; d < deviceCount; d++)
            {
                devices.Item(d, out var device);
                var iid = IID_IAudioSessionManager2;
                device.Activate(ref iid, CLSCTX_ALL, 0, out var obj);
                ((IAudioSessionManager2)obj).GetSessionEnumerator(out var sessions);
                sessions.GetCount(out int count);
                for (int i = 0; i < count; i++)
                {
                    sessions.GetSession(i, out var session);
                    session.GetState(out int state);
                    if (state == AudioSessionStateExpired) continue;
                    visit(session, session.GetProcessId(out uint pid) < 0 ? 0 : pid);
                }
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            Log.Debug($"Audio sessions for {what} unavailable: {ex.Message}");
        }
    }

    private static string? PackageFamily(uint pid)
    {
        nint h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == 0) return null;
        try
        {
            uint length = 0;
            if (GetPackageFamilyName(h, ref length, null) != ERROR_INSUFFICIENT_BUFFER) return null; // not packaged
            var sb = new StringBuilder((int)length);
            return GetPackageFamilyName(h, ref length, sb) == 0 ? sb.ToString() : null;
        }
        finally { CloseHandle(h); }
    }

    /// <summary>The first session's level; the mixer moves an app's sessions together.</summary>
    public float? Level
    {
        get
        {
            try { _volumes[0].GetMasterVolume(out float v); return v; }
            catch (COMException ex)
            {
                // The session went away (the app restarted or switched devices); the caller looks it up again.
                Log.Debug($"Reading app volume failed: {ex.Message}");
                return null;
            }
        }
    }

    public bool? Muted
    {
        get
        {
            try { _volumes[0].GetMute(out bool m); return m; }
            catch (COMException ex)
            {
                Log.Debug($"Reading app mute failed: {ex.Message}");
                return null;
            }
        }
    }

    public void SetMute(bool mute)
    {
        foreach (var v in _volumes)
        {
            try { v.SetMute(mute, ref _context); }
            catch (COMException ex) { Log.Debug($"Setting app mute failed: {ex.Message}"); }
        }
    }

    public void SetLevel(float level)
    {
        level = Math.Clamp(level, 0f, 1f);
        foreach (var v in _volumes)
        {
            try { v.SetMasterVolume(level, ref _context); }
            catch (COMException ex) { Log.Debug($"Setting app volume failed: {ex.Message}"); }
        }
    }
}
