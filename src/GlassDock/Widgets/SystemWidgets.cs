using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Windows;
using GlassDock.Apps;
using GlassDock.Config;
using static GlassDock.Interop.Native;

namespace GlassDock.Widgets;

/// <summary>Base for widgets that sample on the shared scheduler and show a <see cref="MetricView"/>.</summary>
public abstract class SampledWidget : IWidget
{
    private IDisposable? _sub;
    protected MetricView View { get; private set; } = null!;

    public abstract string Label { get; }
    protected abstract TimeSpan Interval { get; }
    protected abstract MetricView Build(WidgetContext ctx);
    /// <summary>Runs on the pool; store the result in fields read by <see cref="Apply"/>.</summary>
    protected abstract void Sample();
    /// <summary>Runs on the UI thread.</summary>
    protected abstract void Apply();

    public FrameworkElement CreateView(WidgetContext ctx)
    {
        View = Build(ctx);
        _sub = ctx.Scheduler.Every(Interval, Sample, Apply);
        return View;
    }

    public virtual void OnClick() => OpenTaskManager();

    protected static void OpenTaskManager() => AppLauncher.Execute("taskmgr.exe", null, null, null);

    public void Dispose() => _sub?.Dispose();
}

/// <summary>Total CPU use from GetSystemTimes deltas (kernel time includes idle time).</summary>
public sealed class CpuWidget : SampledWidget
{
    private long _idle, _kernel, _user;
    private volatile int _percent = -1;

    public override string Label => _percent < 0 ? "CPU" : $"CPU 사용률 {_percent}%";
    protected override TimeSpan Interval => TimeSpan.FromMilliseconds(AppConstants.Widgets.CpuRefreshMs);
    protected override MetricView Build(WidgetContext ctx) => new(ctx, WidgetGlyphs.Cpu, "CPU", "100%");

    protected override void Sample()
    {
        if (!GetSystemTimes(out long idle, out long kernel, out long user)) throw new System.ComponentModel.Win32Exception();
        long dIdle = idle - _idle, dTotal = kernel - _kernel + (user - _user);
        bool first = _kernel == 0;
        (_idle, _kernel, _user) = (idle, kernel, user);
        if (first || dTotal <= 0) return;
        _percent = (int)Math.Round(Math.Clamp(100.0 * (dTotal - dIdle) / dTotal, 0, 100));
    }

    protected override void Apply() => View.Value = _percent < 0 ? "–" : $"{_percent}%";
}

public sealed class MemoryWidget : SampledWidget
{
    private volatile int _percent = -1;
    private double _usedGb, _totalGb;

    public override string Label => _percent < 0 ? "메모리" : $"메모리 {_usedGb:0.0} / {_totalGb:0.0} GB 사용 중";
    protected override TimeSpan Interval => TimeSpan.FromMilliseconds(AppConstants.Widgets.MemoryRefreshMs);
    protected override MetricView Build(WidgetContext ctx) => new(ctx, WidgetGlyphs.Ram, "메모리", "100%");

    protected override void Sample()
    {
        var m = new MEMORYSTATUSEX { dwLength = (uint)System.Runtime.InteropServices.Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (!GlobalMemoryStatusEx(ref m)) throw new System.ComponentModel.Win32Exception();
        const double gb = 1024.0 * 1024 * 1024;
        _totalGb = m.ullTotalPhys / gb;
        _usedGb = (m.ullTotalPhys - m.ullAvailPhys) / gb;
        _percent = (int)m.dwMemoryLoad;
    }

    protected override void Apply() => View.Value = _percent < 0 ? "–" : $"{_percent}%";
}

/// <summary>
/// Receive/send rate summed over the interfaces that are up and have a default gateway (the ones that
/// carry internet traffic), so loopback and host-only virtual adapters do not count.
/// </summary>
public sealed class NetworkWidget : SampledWidget
{
    private readonly Dictionary<string, (long rx, long tx)> _last = [];
    private readonly Stopwatch _clock = new();
    private volatile string _down = "–", _up = "–";

    public override string Label => $"네트워크  ↓ {_down}  ↑ {_up}";
    protected override TimeSpan Interval => TimeSpan.FromMilliseconds(AppConstants.Widgets.NetworkRefreshMs);
    protected override MetricView Build(WidgetContext ctx) => new(ctx, WidgetGlyphs.Network, "↑ –", "↓ 999.9 KB/s");

    protected override void Sample()
    {
        double seconds = _clock.IsRunning ? _clock.Elapsed.TotalSeconds : 0;
        _clock.Restart();
        long rx = 0, tx = 0;
        var seen = new HashSet<string>();
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up
                || ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel
                || ni.GetIPProperties().GatewayAddresses.Count == 0) continue;
            var st = ni.GetIPStatistics();
            seen.Add(ni.Id);
            if (_last.TryGetValue(ni.Id, out var prev))
            {
                // Counters reset when an adapter reconnects; a negative delta is not traffic.
                rx += Math.Max(0, st.BytesReceived - prev.rx);
                tx += Math.Max(0, st.BytesSent - prev.tx);
            }
            _last[ni.Id] = (st.BytesReceived, st.BytesSent);
        }
        foreach (var gone in _last.Keys.Where(k => !seen.Contains(k)).ToList()) _last.Remove(gone);
        if (seconds <= 0) return;
        _down = Rate(rx / seconds);
        _up = Rate(tx / seconds);
    }

    protected override void Apply()
    {
        View.Value = $"↓ {_down}";
        View.Caption = $"↑ {_up}";
    }

    internal static string Rate(double bytesPerSecond) => bytesPerSecond switch
    {
        < 1024 => $"{bytesPerSecond:0} B/s",
        < 1024 * 1024 => $"{bytesPerSecond / 1024:0.#} KB/s",
        _ => $"{bytesPerSecond / (1024 * 1024):0.#} MB/s",
    };
}

public sealed class BatteryWidget : SampledWidget
{
    private volatile int _percent = -1;
    private volatile bool _charging, _plugged, _saver;
    private int _secondsLeft = -1;

    public static bool HasBattery() =>
        GetSystemPowerStatus(out var s) && s.BatteryFlag != BATTERY_FLAG_NO_BATTERY && s.BatteryFlag != BATTERY_FLAG_UNKNOWN;

    public override string Label => _percent < 0 ? "배터리" : $"배터리 {_percent}% · {State}";
    protected override TimeSpan Interval => TimeSpan.FromMilliseconds(AppConstants.Widgets.BatteryRefreshMs);
    protected override MetricView Build(WidgetContext ctx) => new(ctx, WidgetGlyphs.BatteryUnknown, "배터리", "100%");

    private string State => _charging ? "충전 중"
        : _plugged ? "전원 연결됨"
        : _secondsLeft > 0 ? $"{_secondsLeft / 3600}시간 {_secondsLeft / 60 % 60}분 남음"
        : _saver ? "절약 모드" : "배터리 사용 중";

    protected override void Sample()
    {
        if (!GetSystemPowerStatus(out var s)) throw new System.ComponentModel.Win32Exception();
        _percent = s.BatteryLifePercent <= 100 ? s.BatteryLifePercent : -1;
        _plugged = s.ACLineStatus == 1;
        _charging = (s.BatteryFlag & BATTERY_FLAG_CHARGING) != 0;
        _saver = s.SystemStatusFlag == 1;
        _secondsLeft = s.BatteryLifeTime == uint.MaxValue ? -1 : (int)s.BatteryLifeTime;
    }

    protected override void Apply()
    {
        View.Value = _percent < 0 ? "–" : $"{_percent}%";
        View.Caption = _charging ? "충전 중" : _plugged ? "전원 연결" : "배터리";
        View.Glyph = _percent < 0 ? WidgetGlyphs.BatteryUnknown : WidgetGlyphs.Battery(_percent, _charging);
    }

    public override void OnClick() => AppLauncher.Execute("ms-settings:batterysaver", null, null, null);
}
