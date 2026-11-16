using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using GlassDock.Apps;
using GlassDock.Config;
using GlassDock.Interop;
using GlassDock.Logging;
using static GlassDock.Interop.CoreAudio;

namespace GlassDock.Widgets;

/// <summary>
/// Master volume of the default output device. Event-driven: Core Audio calls back on volume/mute
/// changes (keyboard keys, the system flyout) and on default-device changes, so nothing polls.
/// </summary>
public sealed class VolumeWidget : IWidget, IMMNotificationClient, IAudioEndpointVolumeCallback
{
    private Dispatcher _dispatcher = null!;
    private MetricView _view = null!;
    private IMMDeviceEnumerator? _enumerator;
    private IAudioEndpointVolume? _endpoint;
    private Guid _context = Guid.NewGuid();
    private float _level;
    private bool _muted, _noDevice, _disposed;
    private string? _deviceName;
    private static readonly ShellInterop.PROPERTYKEY PKEY_Device_FriendlyName = new(new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"), 14);

    /// <summary>Raised on the UI thread after the level, mute state or device changed.</summary>
    public event Action? Changed;
    public bool HasDevice => !_noDevice;
    public float Level => _level;
    public bool Muted => _muted;
    public string DeviceName => _deviceName ?? "스피커";

    public string Label => _noDevice ? "출력 장치 없음" : _muted ? "볼륨 음소거됨" : $"볼륨 {Percent}%";
    private int Percent => (int)Math.Round(_level * 100);

    public FrameworkElement CreateView(WidgetContext ctx)
    {
        _dispatcher = Dispatcher.CurrentDispatcher;
        _view = new MetricView(ctx, WidgetGlyphs.Volume3, "볼륨", "100%");
        try
        {
            _enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
            _enumerator.RegisterEndpointNotificationCallback(this);
        }
        catch (COMException ex) { Log.Warn("Core Audio unavailable; volume widget shows no device", ex); }
        Attach();
        return _view;
    }

    private void Attach()
    {
        Detach();
        _noDevice = true;
        if (_enumerator == null) { Show(); return; }
        try
        {
            int hr = _enumerator.GetDefaultAudioEndpoint(eRender, eConsole, out var device);
            if (hr == E_NOTFOUND) { Show(); return; }
            Marshal.ThrowExceptionForHR(hr);
            var iid = IID_IAudioEndpointVolume;
            device.Activate(ref iid, CLSCTX_ALL, 0, out var obj);
            _endpoint = (IAudioEndpointVolume)obj;
            _endpoint.RegisterControlChangeNotify(this);
            _endpoint.GetMasterVolumeLevelScalar(out _level);
            _endpoint.GetMute(out _muted);
            _deviceName = FriendlyName(device);
            _noDevice = false;
        }
        catch (COMException ex) { Log.Warn("Could not open the default audio endpoint", ex); }
        Show();
    }

    private static string? FriendlyName(IMMDevice device)
    {
        try
        {
            device.OpenPropertyStore(STGM_READ, out nint p);
            try { return ShellInterop.GetString((ShellInterop.IPropertyStore)Marshal.GetObjectForIUnknown(p), PKEY_Device_FriendlyName); }
            finally { Marshal.Release(p); }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            Log.Debug($"Audio device name unavailable: {ex.Message}");
            return null;
        }
    }

    private void Detach()
    {
        if (_endpoint == null) return;
        try { _endpoint.UnregisterControlChangeNotify(this); }
        catch (COMException ex) { Log.Debug($"Unregister volume notify failed: {ex.Message}"); }
        _endpoint = null;
    }

    private void Show()
    {
        _view.Value = _noDevice ? "–" : $"{Percent}%";
        _view.Glyph = Glyph;
        _view.Caption = _muted ? "음소거" : "볼륨";
        Changed?.Invoke();
    }

    /// <summary>The glyph for the current level, also used by the volume flyout.</summary>
    public string Glyph => _noDevice || _muted ? WidgetGlyphs.Mute
        : _level <= 0.001f ? WidgetGlyphs.Volume0
        : _level < 0.34f ? WidgetGlyphs.Volume1
        : _level < 0.67f ? WidgetGlyphs.Volume2
        : WidgetGlyphs.Volume3;

    // A click opens the volume flyout (AppHost.ToggleWidgetFlyout), which the dock wires up.

    public void ToggleMute() => Use(e => e.SetMute(!_muted, ref _context));

    /// <summary>Like the system flyout, moving the level unmutes.</summary>
    public void SetLevel(float level)
    {
        level = Math.Clamp(level, 0f, 1f);
        Use(e =>
        {
            e.SetMasterVolumeLevelScalar(level, ref _context);
            if (_muted && level > 0) e.SetMute(false, ref _context);
        });
    }

    public void OnWheel(int delta) => SetLevel(_level + delta / 120f * AppConstants.Widgets.VolumeWheelStep);

    public IEnumerable<(string Header, Action Action)> MenuItems =>
    [
        (_muted ? "음소거 해제" : "음소거", ToggleMute),
        ("소리 설정 열기", () => AppLauncher.Execute("ms-settings:sound", null, null, null)),
        ("볼륨 믹서 열기", () => AppLauncher.Execute("ms-settings:apps-volume", null, null, null)),
    ];

    private void Use(Action<IAudioEndpointVolume> act)
    {
        if (_endpoint == null) return;
        try { act(_endpoint); }
        catch (COMException ex)
        {
            // Unplugged headset etc.: the endpoint is invalid until we re-open the default one.
            Log.Warn("Volume change failed; re-opening the default endpoint", ex);
            Attach();
        }
    }

    // Callbacks arrive on Core Audio threads.
    void IAudioEndpointVolumeCallback.OnNotify(nint data)
    {
        var d = Marshal.PtrToStructure<AUDIO_VOLUME_NOTIFICATION_DATA>(data);
        _dispatcher.BeginInvoke(() =>
        {
            if (_disposed) return;
            _level = d.fMasterVolume;
            _muted = d.bMuted != 0;
            Show();
        });
    }

    void IMMNotificationClient.OnDefaultDeviceChanged(int flow, int role, string? id)
    {
        if (flow != eRender || role != eConsole) return;
        _dispatcher.BeginInvoke(() => { if (!_disposed) Attach(); });
    }

    void IMMNotificationClient.OnDeviceStateChanged(string id, int state) { }
    void IMMNotificationClient.OnDeviceAdded(string id) { }
    void IMMNotificationClient.OnDeviceRemoved(string id) { }
    void IMMNotificationClient.OnPropertyValueChanged(string id, PROPERTYKEY key) { }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Detach();
        if (_enumerator == null) return;
        try { _enumerator.UnregisterEndpointNotificationCallback(this); }
        catch (COMException ex) { Log.Debug($"Unregister endpoint callback failed: {ex.Message}"); }
        _enumerator = null;
    }
}
