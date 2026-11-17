using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows.Media;
using System.Windows.Threading;
using GlassDock.Config;
using GlassDock.Input;
using GlassDock.Logging;
using GlassDock.Stacks;
using ManagedShell.WindowsTasks;
using static GlassDock.Interop.Native;

namespace GlassDock.Apps;

/// <summary>
/// Merges pinned items (our own store) with running windows (ManagedShell) into the ordered list of
/// dock buttons. Order: pins in user order, then running unpinned apps in order of first appearance.
/// </summary>
public sealed class AppsModel
{
    private readonly SettingsStore _settings;
    private readonly IconProvider _icons;
    private readonly Dispatcher _dispatcher;
    private ObservableCollection<ApplicationWindow>? _source;
    private readonly HashSet<ApplicationWindow> _watched = [];
    /// <summary>Per pinned folder (by pin id): keeps its collage icon current.</summary>
    private readonly Dictionary<string, FolderWatcher> _folderWatchers = [];
    private bool _rebuildQueued;

    public ObservableCollection<AppItem> Items { get; } = [];
    public int IconPixelSize { get; set; } = 64;

    public AppsModel(SettingsStore settings, IconProvider icons, Dispatcher dispatcher)
    {
        _settings = settings;
        _icons = icons;
        _dispatcher = dispatcher;
    }

    public void Attach(Tasks tasks)
    {
        if (tasks.GroupedWindows?.SourceCollection is not ObservableCollection<ApplicationWindow> source)
        {
            Log.Error("ManagedShell window collection unavailable; running apps will not show");
            Rebuild();
            return;
        }
        _source = source;
        _source.CollectionChanged += OnWindowsChanged;
        Rebuild();
    }

    private void OnWindowsChanged(object? sender, NotifyCollectionChangedEventArgs e) => QueueRebuild();

    private void OnWindowPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(ApplicationWindow.ShowInTaskbar):
            case nameof(ApplicationWindow.AppUserModelID):
            case nameof(ApplicationWindow.WinFileName):
                QueueRebuild();
                break;
            case nameof(ApplicationWindow.State):
            case nameof(ApplicationWindow.OverlayIcon):
                if (sender is ApplicationWindow w) Items.FirstOrDefault(i => i.Windows.Contains(w))?.RefreshState();
                break;
        }
    }

    public void QueueRebuild()
    {
        if (_rebuildQueued) return;
        _rebuildQueued = true;
        _dispatcher.BeginInvoke(DispatcherPriority.Background, () => { _rebuildQueued = false; Rebuild(); });
    }

    public void Rebuild()
    {
        var pins = _settings.Current.Apps.Items;
        var windows = _source?.Where(w => w.ShowInTaskbar).ToList() ?? [];

        // Watch property changes of every window (ShowInTaskbar may flip later).
        foreach (var w in _source?.ToList() ?? [])
            if (_watched.Add(w)) w.PropertyChanged += OnWindowPropertyChanged;
        foreach (var gone in _watched.Where(w => _source == null || !_source.Contains(w)).ToList())
        {
            gone.PropertyChanged -= OnWindowPropertyChanged;
            _watched.Remove(gone);
        }

        var old = Items.ToDictionary(i => i.Key);
        var next = new List<AppItem>();

        foreach (var pin in pins)
        {
            var key = AppIdentity.KeyForPin(pin);
            if (next.Any(i => i.Key == key)) key = "pin:" + pin.Id; // two pins for the same app stay separate
            var item = old.TryGetValue(key, out var existing) ? existing : new AppItem(key, pin, pin.DisplayName ?? "?");
            item.Pin = pin;
            item.DisplayName = pin.DisplayName ?? item.DisplayName;
            next.Add(item);
        }

        var assigned = new Dictionary<AppItem, List<ApplicationWindow>>();
        foreach (var w in windows)
        {
            var target = next.FirstOrDefault(i => i.Pin != null && Owns(i.Pin, w));
            if (target == null)
            {
                var key = _settings.Current.Apps.GroupWindows ? AppIdentity.KeyForWindow(w.AppUserModelID, w.WinFileName) : "hwnd:" + w.Handle;
                target = next.FirstOrDefault(i => i.Key == key);
                if (target == null)
                {
                    target = old.TryGetValue(key, out var existing) && !existing.IsPinned ? existing : new AppItem(key, null, NameOf(w));
                    target.Pin = null;
                    next.Add(target);
                }
            }
            if (!assigned.TryGetValue(target, out var list)) assigned[target] = list = [];
            list.Add(w);
        }

        // Running unpinned apps keep their previous position relative to each other.
        var prevOrder = Items.Select((it, idx) => (it, idx)).ToDictionary(p => p.it, p => p.idx);
        var pinned = next.Where(i => i.IsPinned).ToList();
        var running = next.Where(i => !i.IsPinned).OrderBy(i => prevOrder.TryGetValue(i, out var p) ? p : int.MaxValue).ToList();
        next = [.. pinned, .. running];

        foreach (var item in next)
        {
            var ws = assigned.TryGetValue(item, out var l) ? l : [];
            SyncWindows(item, ws);
            if (!item.IsPinned && item.Windows.Count > 0 && NameOf(item.Windows[0]) is { Length: > 0 } name)
                item.DisplayName = name;
            item.RefreshState();
            if (item.Icon == null || (item.IsGroup && item.IconKey != GroupIconKey(item))) LoadIcon(item);
        }

        // Apply to the observable collection with minimal churn so animations stay smooth.
        for (int i = 0; i < next.Count; i++)
        {
            int cur = Items.IndexOf(next[i]);
            if (cur == i) continue;
            if (cur < 0) Items.Insert(i, next[i]);
            else Items.Move(cur, i);
        }
        while (Items.Count > next.Count) Items.RemoveAt(Items.Count - 1);
        SyncFolderWatchers();
    }

    /// <summary>The exe behind a running window with this app id (for a web app, the browser that hosts it).</summary>
    public string? ExePathForAumid(string aumid) =>
        Items.SelectMany(i => i.Windows)
            .FirstOrDefault(w => string.Equals(AppIdentity.EffectiveAumid(w.AppUserModelID, w.WinFileName), aumid, StringComparison.OrdinalIgnoreCase))
            ?.WinFileName;

    /// <summary>The app's name as Start shows it, else the exe's description, else the window title.</summary>
    private static string NameOf(ApplicationWindow w) =>
        AppNames.ForAumid(AppIdentity.EffectiveAumid(w.AppUserModelID, w.WinFileName))
        ?? (w.WinFileDescription is { Length: > 0 } d ? d : w.Title);

    /// <summary>A pinned app owns its windows; a group owns the windows of every app inside it.</summary>
    private static bool Owns(PinnedItem pin, ApplicationWindow w) => pin.Kind switch
    {
        "app" => AppIdentity.Matches(pin, w.AppUserModelID, w.WinFileName),
        "group" => pin.Children?.Any(c => c.Kind == "app" && AppIdentity.Matches(c, w.AppUserModelID, w.WinFileName)) == true,
        _ => false,
    };

    private static string GroupIconKey(AppItem item) => string.Join(",", item.GroupChildren.Take(AppConstants.Stacks.CollageItems).Select(c => c.Id));

    private void SyncFolderWatchers()
    {
        var folders = Items.Where(i => i.IsFolder && i.Pin?.Path is { Length: > 0 } p && Directory.Exists(p)).ToDictionary(i => i.Pin!.Id);
        foreach (var id in _folderWatchers.Keys.Where(id => !folders.ContainsKey(id)).ToList())
        {
            _folderWatchers[id].Dispose();
            _folderWatchers.Remove(id);
        }
        foreach (var (id, item) in folders)
        {
            if (_folderWatchers.ContainsKey(id)) continue;
            var w = new FolderWatcher(item.Pin!.Path!, _dispatcher, AppConstants.Stacks.IconRefreshDebounceMs);
            w.Changed += () => { if (Items.FirstOrDefault(i => i.Pin?.Id == id) is { } it) LoadIcon(it); };
            _folderWatchers[id] = w;
        }
    }

    /// <summary>Re-draws a folder's collage (after its sort order changed).</summary>
    public void RefreshFolderIcon(AppItem item)
    {
        if (item.IsFolder) LoadIcon(item);
    }

    public void DisposeWatchers()
    {
        foreach (var w in _folderWatchers.Values) w.Dispose();
        _folderWatchers.Clear();
    }

    private static void SyncWindows(AppItem item, List<ApplicationWindow> ws)
    {
        for (int i = item.Windows.Count - 1; i >= 0; i--)
            if (!ws.Contains(item.Windows[i])) item.Windows.RemoveAt(i);
        foreach (var w in ws)
            if (!item.Windows.Contains(w)) item.Windows.Add(w);
    }

    private async void LoadIcon(AppItem item)
    {
        if (item.IsGroup)
        {
            string key = GroupIconKey(item);
            item.IconKey = key;
            int px = IconProvider.SnapSize(IconPixelSize / 2.0);
            var icons = await Task.WhenAll(item.GroupChildren.Take(AppConstants.Stacks.CollageItems)
                .Select(c => IconSource(c) is { } src ? _icons.GetAsync(src, px) : Task.FromResult<ImageSource?>(null)));
            // A newer change to the group started its own load meanwhile.
            if (item.IconKey == key) item.Icon = StackIcon.Compose(icons);
            return;
        }
        if (item.IsFolder && item.Pin?.Path is { Length: > 0 } folder)
        {
            try
            {
                if (await StackIcon.BuildAsync(_icons, folder, item.Pin.Sort, IconPixelSize) is { } collage) { item.Icon = collage; return; }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                Log.Debug($"Stack icon for {folder} unavailable: {ex.Message}");
            }
        }
        string? source = IconSource(item);
        if (source != null)
        {
            var img = await _icons.GetAsync(source, IconPixelSize);
            if (img != null) { item.Icon = img; return; }
        }
        // Fall back to the window's own icon from ManagedShell.
        item.Icon = item.Windows.FirstOrDefault()?.Icon;
        if (item.Icon == null && item.Windows.FirstOrDefault() is { } w)
        {
            PropertyChangedEventHandler? h = null;
            h = (_, e) => { if (e.PropertyName == nameof(ApplicationWindow.Icon) && w.Icon != null) { item.Icon ??= w.Icon; w.PropertyChanged -= h; } };
            w.PropertyChanged += h;
        }
    }

    private static string? IconSource(AppItem item)
    {
        if (item.Pin is { } pin && IconSource(pin) is { } src) return src;
        var w = item.Windows.FirstOrDefault();
        if (w == null) return null;
        if (AppIdentity.IsRealAumid(w.AppUserModelID)) return @"shell:AppsFolder\" + w.AppUserModelID;
        return string.IsNullOrEmpty(w.WinFileName) ? null : w.WinFileName;
    }

    /// <summary>What the shell should draw for a pin: its AUMID, exe or copied shortcut, or the folder.</summary>
    public static string? IconSource(PinnedItem pin)
    {
        if (pin.Kind == "folder") return pin.Path;
        // An icon picked on the shortcut wins, as on the taskbar (a game launcher shortcut, say, shows the game).
        if (pin.IconPath is { Length: > 0 } icon && File.Exists(icon)) return IconProvider.IconResource(icon, pin.IconIndex);
        if (AppIdentity.IsRealAumid(pin.Aumid)) return @"shell:AppsFolder\" + pin.Aumid;
        // Asking for the .lnk itself would add the shortcut-arrow overlay; use the target.
        if (!string.IsNullOrEmpty(pin.Target) && File.Exists(pin.Target)) return pin.Target;
        if (pin.Shortcut != null) return Path.Combine(AppPaths.Root, pin.Shortcut);
        return null;
    }

    public void ReloadIcons()
    {
        foreach (var i in Items) { i.Icon = null; LoadIcon(i); }
    }

    // ---- actions --------------------------------------------------------------------------------

    /// <summary>Click / Win+N behaviour. <paramref name="preferCycle"/> forces cycling for multi-window apps.</summary>
    public ActivationResult Activate(AppItem item, bool preferCycle)
    {
        if (item.IsFolder || !item.IsRunning)
        {
            Launch(item);
            return ActivationResult.Launched;
        }
        nint fg = GetForegroundWindow();
        if (item.Windows.Count == 1)
        {
            var w = item.Windows[0];
            if (w.Handle == fg && !IsIconic(w.Handle)) { w.Minimize(); return ActivationResult.Minimized; }
            ForegroundActivator.Activate(w.Handle);
            return ActivationResult.Activated;
        }
        if (preferCycle || _settings.Current.Apps.MultiWindowClick == "cycle")
        {
            int cur = item.Windows.ToList().FindIndex(x => x.Handle == fg);
            var next = item.Windows[(cur + 1) % item.Windows.Count];
            ForegroundActivator.Activate(next.Handle);
            return ActivationResult.Activated;
        }
        return ActivationResult.ShowPreview;
    }

    public void Launch(AppItem item, bool elevated = false)
    {
        if (item.IsGroup) return; // its apps are started from the group flyout
        var pin = item.Pin;
        string? aumid = item.Aumid, exe = item.ExePath;
        LaunchBouncing(item.IsFolder ? null : item,
            () => pin != null ? AppLauncher.Launch(pin, elevated) : AppLauncher.LaunchNew(aumid, exe, elevated));
    }

    /// <summary>Starts one app of a group; the group's dock icon bounces for it.</summary>
    public void LaunchChild(AppItem group, PinnedItem child, bool elevated = false) =>
        LaunchBouncing(group, () => AppLauncher.Launch(child, elevated));

    /// <summary>
    /// Bounces <paramref name="bouncing"/> at once, so the click is answered while the shell works,
    /// and stops it again if the launch fails or is cancelled at the UAC prompt.
    /// </summary>
    private static async void LaunchBouncing(AppItem? bouncing, Func<bool> launch)
    {
        if (bouncing != null) Bounce(bouncing);
        // The launcher thread catches and logs every failure as false, so nothing escapes this async void.
        if (!await AppLauncher.InBackground(launch) && bouncing != null) bouncing.IsLaunching = false;
    }

    /// <summary>Click on an app inside a group: start it, or bring its windows forward one by one.</summary>
    public void ActivateChild(AppItem group, PinnedItem child)
    {
        var ws = group.WindowsOf(child);
        if (ws.Count == 0) { LaunchChild(group, child); return; }
        int cur = ws.FindIndex(x => x.Handle == GetForegroundWindow());
        ForegroundActivator.Activate(ws[(cur + 1) % ws.Count].Handle);
    }

    private static void Bounce(AppItem item)
    {
        item.IsLaunching = true;
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(AppConstants.Animation.LaunchBounceTimeoutMs) };
        t.Tick += (_, _) => { t.Stop(); item.IsLaunching = false; };
        t.Start();
    }

    public void Pin(AppItem item, int? index = null)
    {
        if (item.IsPinned) return;
        var w = item.Windows.FirstOrDefault();
        var pin = PinStore.FromRunning(w?.AppUserModelID, w?.WinFileName, item.DisplayName);
        _settings.Update(s =>
        {
            int at = Math.Clamp(index ?? s.Apps.Items.Count, 0, s.Apps.Items.Count);
            s.Apps.Items.Insert(at, pin);
        });
        Rebuild();
    }

    public void AddPin(PinnedItem pin, int index)
    {
        _settings.Update(s => s.Apps.Items.Insert(Math.Clamp(index, 0, s.Apps.Items.Count), pin));
        Rebuild();
    }

    public void Unpin(AppItem item)
    {
        if (item.Pin is not { } pin) return;
        _settings.Update(s => s.Apps.Items.RemoveAll(p => p.Id == pin.Id));
        PinStore.DeletePinFiles(pin);
        foreach (var c in pin.Children ?? []) PinStore.DeletePinFiles(c);
        item.Pin = null;
        Rebuild();
    }

    /// <summary>Moves a pinned item to a new position among the pins (drag-reorder).</summary>
    public void MovePin(AppItem item, int newPinIndex)
    {
        if (item.Pin is not { } pin) return;
        _settings.Update(s =>
        {
            int cur = s.Apps.Items.FindIndex(p => p.Id == pin.Id);
            if (cur < 0) return;
            s.Apps.Items.RemoveAt(cur);
            s.Apps.Items.Insert(Math.Clamp(newPinIndex, 0, s.Apps.Items.Count), pin);
        });
        Rebuild();
    }

    public int PinnedCount => Items.Count(i => i.IsPinned);

    // ---- groups ---------------------------------------------------------------------------------

    /// <summary>Only apps go into groups: a folder already opens as a stack, and groups do not nest.</summary>
    public static bool CanJoinGroup(AppItem item) => !item.IsFolder && !item.IsGroup && (item.IsPinned || item.IsRunning);
    public static bool CanHostGroup(AppItem item) => item.IsGroup || item.Pin?.Kind == "app";

    /// <summary>
    /// Dropped <paramref name="dragged"/> onto <paramref name="target"/>: adds it to the target group, or
    /// turns the target app into a new group holding both, in the target's place.
    /// </summary>
    public void MergeIntoGroup(AppItem dragged, AppItem target)
    {
        if (dragged == target || !CanJoinGroup(dragged) || !CanHostGroup(target) || target.Pin is not { } host) return;
        var w = dragged.Windows.FirstOrDefault();
        var child = dragged.Pin ?? PinStore.FromRunning(w?.AppUserModelID, w?.WinFileName, dragged.DisplayName);
        _settings.Update(s =>
        {
            s.Apps.Items.RemoveAll(p => p.Id == child.Id);
            if (host.Kind == "group") { (host.Children ??= []).Add(child); return; }
            int at = s.Apps.Items.FindIndex(p => p.Id == host.Id);
            if (at < 0) return;
            s.Apps.Items[at] = new PinnedItem { Kind = "group", DisplayName = AppConstants.Groups.DefaultName, Children = [host, child] };
        });
        Log.Info($"Grouped {child.DisplayName} with {target.DisplayName}");
        Rebuild();
    }

    /// <summary>Takes one app out of a group and puts it on the dock right after the group.</summary>
    public void RemoveFromGroup(AppItem group, PinnedItem child)
    {
        if (group.Pin is not { Kind: "group" } g) return;
        _settings.Update(s =>
        {
            int at = s.Apps.Items.FindIndex(p => p.Id == g.Id);
            if (at < 0 || g.Children == null || !g.Children.Remove(child)) return;
            s.Apps.Items.Insert(at + 1, child);
            DissolveIfSmall(s, g);
        });
        Rebuild();
    }

    /// <summary>Puts every app of a group back on the dock in the group's place.</summary>
    public void Ungroup(AppItem group)
    {
        if (group.Pin is not { Kind: "group" } g) return;
        _settings.Update(s =>
        {
            int at = s.Apps.Items.FindIndex(p => p.Id == g.Id);
            if (at < 0) return;
            s.Apps.Items.RemoveAt(at);
            s.Apps.Items.InsertRange(at, g.Children ?? []);
        });
        Rebuild();
    }

    /// <summary>Moves an app to <paramref name="index"/> inside its group; the dock icon shows the first four in this order.</summary>
    public void MoveInGroup(AppItem group, PinnedItem child, int index)
    {
        if (group.Pin is not { Kind: "group", Children: { } children }) return;
        _settings.Update(_ =>
        {
            if (!children.Remove(child)) return;
            children.Insert(Math.Clamp(index, 0, children.Count), child);
        });
        Rebuild();
    }

    public void RenameGroup(AppItem group, string name)
    {
        if (group.Pin is not { Kind: "group" } g || string.IsNullOrWhiteSpace(name) || name.Trim() == g.DisplayName) return;
        _settings.Update(_ => g.DisplayName = name.Trim());
        group.DisplayName = g.DisplayName!;
    }

    /// <summary>A group of one is just that app again; an empty group goes away.</summary>
    private static void DissolveIfSmall(DockSettings s, PinnedItem g)
    {
        if (g.Children is { Count: > 1 }) return;
        int at = s.Apps.Items.FindIndex(p => p.Id == g.Id);
        if (at < 0) return;
        s.Apps.Items.RemoveAt(at);
        if (g.Children is [var only]) s.Apps.Items.Insert(at, only);
    }
}

public enum ActivationResult { Launched, Activated, Minimized, ShowPreview }
