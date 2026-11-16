using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using GlassDock.Config;
using ManagedShell.WindowsTasks;

namespace GlassDock.Apps;

/// <summary>One button in the Apps tile: a pinned app, a running app, or both.</summary>
public sealed class AppItem : INotifyPropertyChanged
{
    private ImageSource? _icon;
    private bool _isActive;
    private bool _needsAttention;
    private bool _isLaunching;
    private ImageSource? _overlay;
    private string _displayName;

    public AppItem(string key, PinnedItem? pin, string displayName)
    {
        Key = key;
        Pin = pin;
        _displayName = displayName;
    }

    public string Key { get; }
    public PinnedItem? Pin { get; set; }
    public bool IsPinned => Pin != null;
    public bool IsFolder => Pin?.Kind == "folder";
    public bool IsGroup => Pin?.Kind == "group";
    public IReadOnlyList<PinnedItem> GroupChildren => Pin?.Children ?? [];
    /// <summary>Groups: which children the current collage icon was drawn from.</summary>
    internal string? IconKey { get; set; }
    public ObservableCollection<ApplicationWindow> Windows { get; } = [];
    public bool IsRunning => Windows.Count > 0;

    /// <summary>Exe path / AUMID of the first window; used to pin or relaunch a running app.</summary>
    public string? ExePath => Windows.FirstOrDefault()?.WinFileName ?? Pin?.Target;
    public string? Aumid => Pin?.Aumid ?? Windows.FirstOrDefault()?.AppUserModelID;

    public string DisplayName { get => _displayName; set => Set(ref _displayName, value); }
    public ImageSource? Icon { get => _icon; set => Set(ref _icon, value); }
    public bool IsActive { get => _isActive; set => Set(ref _isActive, value); }
    public bool NeedsAttention { get => _needsAttention; set => Set(ref _needsAttention, value); }
    public bool IsLaunching { get => _isLaunching; set => Set(ref _isLaunching, value); }
    public ImageSource? OverlayIcon { get => _overlay; set => Set(ref _overlay, value); }

    /// <summary>Windows of one app inside a group.</summary>
    public List<ApplicationWindow> WindowsOf(PinnedItem child) =>
        Windows.Where(w => AppIdentity.Matches(child, w.AppUserModelID, w.WinFileName)).ToList();

    /// <summary>Recomputes state flags from the windows; call after window property changes.</summary>
    public void RefreshState()
    {
        bool active = false, attention = false;
        ImageSource? overlay = null;
        foreach (var w in Windows)
        {
            if (w.State == ApplicationWindow.WindowState.Active) active = true;
            if (w.State == ApplicationWindow.WindowState.Flashing) attention = true;
            overlay ??= w.OverlayIcon;
        }
        IsActive = active;
        NeedsAttention = attention && !active;
        OverlayIcon = overlay;
        if (IsRunning) IsLaunching = false;
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(ExePath));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        OnPropertyChanged(name);
    }

    public void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
