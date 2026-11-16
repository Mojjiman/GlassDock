using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using GlassDock.Apps;
using GlassDock.Config;
using GlassDock.Input;
using GlassDock.Logging;
using GlassDock.Shell;
using GlassDock.Taskbar;
using GlassDock.UI;
using GlassDock.Update;
using GlassDock.Widgets;
using ManagedShell;
using ManagedShell.Common.Enums;
using ManagedShell.Common.Logging;
using static GlassDock.Interop.Native;

namespace GlassDock;

/// <summary>Composition root: creates services in a safe order and tears them down in reverse.</summary>
public sealed class AppHost : IDisposable
{
    public SettingsStore Settings { get; }
    public AppsModel Apps { get; }
    public FullscreenMonitor Fullscreen { get; private set; } = null!;
    public RefreshScheduler Widgets { get; } = new(Application.Current.Dispatcher);
    public UpdateService Updates { get; private set; } = null!;
    /// <summary>The version whose update flyout was already shown this session (it is offered once).</summary>
    private Version? _offeredUpdate;
    public bool IsFlyoutOpen => _flyout != null;
    /// <summary>A dock context menu is open; an auto-hiding dock stays up meanwhile.</summary>
    public bool IsMenuOpen => _openMenus > 0;
    private int _openMenus;

    private readonly IconProvider _icons = new();
    public IconProvider Icons => _icons;
    private ShellHostWindow? _host;
    private TaskbarHider? _hider;
    private ShellManager? _shell;
    private HotkeyService? _hotkeys;
    private DockWindow? _dock;
    private FlyoutWindow? _flyout;
    private SettingsWindow? _settingsWindow;
    private Process? _watchdog;
    private nint _realTray;
    private bool _quitting;

    public AppHost()
    {
        Settings = new SettingsStore(AppPaths.SettingsFile);
        Log.MinLevel = Settings.Current.Diagnostics.LogLevel switch { "debug" => LogLevel.Debug, "warning" => LogLevel.Warning, "error" => LogLevel.Error, _ => LogLevel.Info };
        Apps = new AppsModel(Settings, _icons, System.Windows.Threading.Dispatcher.CurrentDispatcher);
    }

    public void Start()
    {
        var s = Settings.Current;
        if (!s.General.FirstRunDone)
        {
            var imported = PinStore.ImportTaskbarPins();
            Settings.Update(x => { x.Apps.Items.AddRange(imported); x.General.FirstRunDone = true; });
        }
        if (PinStore.UpgradePackagedPins(s.Apps.Items) + PinStore.RefreshAppNames(s.Apps.Items) + PinStore.ReadShortcutIcons(s.Apps.Items) > 0)
            Settings.Update(_ => { });

        _host = new ShellHostWindow();
        _host.TaskbarCreated += OnTaskbarCreated;
        _host.DisplayChanged += () => { _hider?.Reassert("display change"); _dock?.RefreshWallpaper(); };
        _host.WallpaperChanged += () => _dock?.RefreshWallpaper();
        _host.SettingChanged += OnSettingChanged;
        _host.SessionUnlocked += () => { _hotkeys?.ResetState(); _hider?.Reassert("unlock"); _sessionLocked = false; UpdateWidgetPause(); };
        _host.SessionLocked += () => { _sessionLocked = true; UpdateWidgetPause(); };
        _host.Resumed += () => { _hotkeys?.ResetState(); _hider?.Reassert("resume"); _dock?.PlaceWindow(); };
        _host.SessionEnding += OnSessionEnding;
        _host.OpenSettingsRequested += OpenSettings;
        _host.QuitRequested += Quit;

        // Order matters: hide Explorer's taskbar before ManagedShell creates its fake Shell_TrayWnd, so our
        // appbar-state calls reach the real one directly.
        if (s.Taskbar.HideNative)
        {
            _hider = new TaskbarHider();
            _hider.Hide();
        }
        _realTray = ExplorerTaskbars.FindPrimary();

        ShellLogger.Severity = LogSeverity.Warning;
        ShellLogger.Log += (_, e) => Log.Write(e.Severity >= LogSeverity.Error ? LogLevel.Error : LogLevel.Warning, "ManagedShell", e.Message, e.Exception);
        var config = ShellManager.DefaultShellConfig;
        config.EnableTasksService = true;
        config.AutoStartTasksService = true;
        config.TaskIconSize = IconSize.Large;
        config.EnableTrayService = true;
        config.AutoStartTrayService = true;
        _shell = new ShellManager(config);
        Apps.Attach(_shell.Tasks);

        Fullscreen = new FullscreenMonitor(_shell.FullScreenHelper);
        Fullscreen.Changed += OnFullscreenChanged;

        _dock = new DockWindow(this);
        _dock.Initialize();

        _hotkeys = new HotkeyService(_host.Handle, ShellHostWindow.MsgHotkeyDirect);
        _host.HotkeyHandler = _hotkeys.HandleHotkey;
        _host.DirectHotkeyHandler = _hotkeys.HandleDirect;
        _hotkeys.WinKeyChanged += down => _dock?.OnWinKey(down);
        _hotkeys.Command += OnHotkey;
        _hotkeys.Apply(s.Shortcuts);

        StartupRegistration.Apply(s.General.LaunchAtStartup);
        _applied = s.Clone();
        Settings.Changed += OnSettingsChanged;
        StartWatchdog();
        Updates = new UpdateService(() => Settings.Current.General.CheckForUpdates);
        Updates.Changed += () => Application.Current.Dispatcher.BeginInvoke(() => { _dock?.Rebuild(); OfferUpdate(); });

        if (Settings.LoadProblem != null) MessageBox.Show(Settings.LoadProblem, "GlassDock", MessageBoxButton.OK, MessageBoxImage.Warning);
        Log.Info("GlassDock started");
    }

    // ---- watchdog -------------------------------------------------------------------------------

    private void StartWatchdog()
    {
        if (Debugger.IsAttached) { Log.Info("Debugger attached; watchdog not started"); return; }
        try
        {
            _watchdog = Process.Start(new ProcessStartInfo(AppPaths.ExePath, $"--watchdog --pid {Environment.ProcessId}") { UseShellExecute = false });
            if (_watchdog != null)
            {
                _watchdog.EnableRaisingEvents = true;
                _watchdog.Exited += (_, _) =>
                {
                    if (_quitting) return;
                    Log.Warn("Watchdog exited unexpectedly; restarting it");
                    Application.Current?.Dispatcher.BeginInvoke(StartWatchdog);
                };
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Log.Error("Starting watchdog failed; a crash would leave the taskbar hidden until next start", ex);
        }
    }

    // ---- system events --------------------------------------------------------------------------

    private void OnTaskbarCreated()
    {
        // ManagedShell broadcasts TaskbarCreated itself; only a new real Shell_TrayWnd means Explorer restarted.
        var tray = ExplorerTaskbars.FindPrimary();
        if (tray == 0 || tray == _realTray) return;
        _realTray = tray;
        Log.Info("Explorer restart detected");
        var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(AppConstants.Explorer.SettleDelayMs) };
        t.Tick += (_, _) =>
        {
            t.Stop();
            _hider?.OnExplorerRestarted();
            _dock?.OnExplorerRestarted();
        };
        t.Start();
    }

    private void OnSettingChanged(string? area)
    {
        if (area is "ImmersiveColorSet" or "WindowsThemeElement")
        {
            _dock?.Rebuild();
            return;
        }
        if (area is "TraySettings" or "UserInteractionMode") _hider?.Reassert("setting change");
        _dock?.PlaceWindow();
    }

    private void OnSessionEnding()
    {
        Log.Info("Session ending");
        if (Settings.Current.Taskbar.RestoreOnSessionEnd) _hider?.Restore("session end");
        Settings.SaveNow();
    }

    // ---- updates --------------------------------------------------------------------------------

    /// <summary>A newer release the user has not skipped; the dock shows its button while there is one.</summary>
    public UpdateInfo? PendingUpdate =>
        Updates?.Available is { } info && info.Version.ToString() != Settings.Current.General.SkippedUpdate ? info : null;

    public void ShowUpdateFlyout(DockWindow dock)
    {
        if (PendingUpdate is not { } info) return;
        if (_flyout is UpdateFlyout) { _flyout.SafeClose(); return; }
        ShowFlyout(new UpdateFlyout(dock, info, InstallUpdateAsync), dock.PillScreenRect);
    }

    public void ShowUpdateMenu(TileButton tile, DockWindow dock)
    {
        if (PendingUpdate is not { } info) return;
        dock.HideLabel();
        var menu = new ContextMenu();
        if (UpdateService.CanInstall) Add(menu, "지금 업데이트", () => _ = InstallUpdateAsync());
        Add(menu, "변경 내용 보기", () => AppLauncher.Execute(info.PageUrl, null, null, null));
        Add(menu, "이 버전 건너뛰기", () =>
        {
            Settings.Update(s => s.General.SkippedUpdate = info.Version.ToString());
            _dock?.Rebuild();
        });
        OpenMenu(menu, tile, dock);
    }

    /// <summary>Shows the update flyout once per version, but never over a fullscreen game or another flyout.</summary>
    private void OfferUpdate()
    {
        if (PendingUpdate is not { } info || _offeredUpdate == info.Version || _dock == null || _quitting) return;
        if (Fullscreen.IsFullscreen || _flyout != null || IsMenuOpen) return; // tried again when those end
        _offeredUpdate = info.Version;
        Log.Info($"Offering update {info.Version}");
        _dock.Reveal(Settings.Current.AutoHide.WinKeyLingerMs);
        ShowFlyout(new UpdateFlyout(_dock, info, InstallUpdateAsync), _dock.PillScreenRect);
    }

    /// <returns>False when the download or swap failed (logged); on success the dock is already quitting.</returns>
    public async Task<bool> InstallUpdateAsync(IProgress<double>? progress = null)
    {
        if (Updates.Available is not { } info) return false;
        if (!await Updates.InstallAsync(info, progress)) return false;
        Quit();
        return true;
    }

    private void OnFullscreenChanged(bool full)
    {
        if (full && _flyout is UpdateFlyout offer)
        {
            // It stays open until answered, so it would sit over the game; offer it again afterwards.
            offer.SafeClose();
            _offeredUpdate = null;
        }
        if (!full) Application.Current.Dispatcher.BeginInvoke(OfferUpdate);
        if (!Settings.Current.Fullscreen.HideDock) return;
        _dock?.SetFullscreenHidden(full);
        // The hook was paused in the game, so it missed the Win press that opened Start.
        if (!full && Fullscreen.LauncherOpen) _dock?.Reveal(Settings.Current.AutoHide.WinKeyLingerMs);
        UpdateWidgetPause();
        // Assigned both ways so turning the setting off mid-game resumes the hook on the next change.
        // With Win+1..0 real hotkeys the hook has nothing useful to do in a game, and an idle hook is
        // still one more thing an anti-cheat can object to.
        if (_hotkeys != null) _hotkeys.Suspended = full && (Settings.Current.Shortcuts.SuspendHookInFullscreen || _hotkeys.SlotsReal);
    }

    private DockSettings _applied = new();
    private bool _sessionLocked;

    /// <summary>Widgets stop sampling while nobody can see them.</summary>
    private void UpdateWidgetPause() =>
        Widgets.Paused = _sessionLocked || (Fullscreen?.IsFullscreen == true && Settings.Current.Fullscreen.HideDock);

    private void OnSettingsChanged(object? sender, DockSettings s)
    {
        var prev = _applied;
        _applied = s.Clone();
        if (!Same(prev.Shortcuts, s.Shortcuts)) _hotkeys?.Apply(s.Shortcuts);
        if (prev.General.LaunchAtStartup != s.General.LaunchAtStartup) StartupRegistration.Apply(s.General.LaunchAtStartup);
        if (prev.Taskbar.HideNative != s.Taskbar.HideNative)
        {
            if (s.Taskbar.HideNative) { _hider ??= new TaskbarHider(); _hider.Hide(); }
            else _hider?.Restore("setting turned off");
        }
        bool visual = !Same(prev.Dock, s.Dock) || !Same(prev.AutoHide, s.AutoHide) || !Same(prev.Appearance, s.Appearance) || !Same(prev.Clock, s.Clock) || !Same(prev.Tiles, s.Tiles)
                      || prev.Apps.ShowBadges != s.Apps.ShowBadges || prev.Apps.ShowRunningIndicator != s.Apps.ShowRunningIndicator
                      || prev.Apps.ActiveIndicatorColor != s.Apps.ActiveIndicatorColor;
        if (visual)
        {
            int oldPx = Apps.IconPixelSize;
            _dock?.Rebuild();
            if (Apps.IconPixelSize != oldPx) Apps.ReloadIcons();
        }
        if (prev.Apps.GroupWindows != s.Apps.GroupWindows) Apps.QueueRebuild();
    }

    private static bool Same<T>(T a, T b) =>
        System.Text.Json.JsonSerializer.Serialize(a, SettingsStore.JsonOptions) == System.Text.Json.JsonSerializer.Serialize(b, SettingsStore.JsonOptions);

    // ---- shortcuts ------------------------------------------------------------------------------

    private void OnHotkey(HotkeyCommand cmd)
    {
        if (_dock == null) return;
        Log.Info($"Shortcut {cmd.Action} slot {cmd.Slot + 1} mods {cmd.Modifiers}");
        switch (cmd.Action)
        {
            case HotkeyAction.Slot:
                var buttons = _dock.AppButtons;
                if (cmd.Slot >= buttons.Count) return;
                var item = buttons[cmd.Slot].Item;
                if (cmd.Ctrl && cmd.Shift) Apps.Launch(item, elevated: true);
                else if (cmd.Shift) Apps.Launch(item);
                else if (cmd.Alt) { _dock.EnterKeyboardMode(buttons[cmd.Slot]); ShowAppMenu(item, buttons[cmd.Slot], _dock); }
                else if (item.IsFolder) ToggleStack(item, _dock, buttons[cmd.Slot]);
                else if (item.IsGroup) ToggleGroup(item, _dock, buttons[cmd.Slot]);
                else Apps.Activate(item, preferCycle: true); // like the taskbar: repeated Win+N cycles windows
                break;
            case HotkeyAction.FocusApps:
                _dock.CycleKeyboardFocus(cmd.Shift);
                break;
            case HotkeyAction.FocusTray:
                if (_dock.TrayTile != null) { _dock.EnterKeyboardMode(_dock.TrayTile); ToggleTrayFlyout(_dock); }
                break;
        }
    }

    // ---- clicks, menus, flyouts -----------------------------------------------------------------

    public void OnAppClicked(AppItem item, DockWindow dock, AppButton button)
    {
        if (item.IsFolder) { ToggleStack(item, dock, button); return; }
        if (item.IsGroup) { ToggleGroup(item, dock, button); return; }
        if (Apps.Activate(item, preferCycle: false) == ActivationResult.ShowPreview)
            ShowFlyout(new PreviewFlyout(dock, item), dock.ScreenRectOf(button));
    }

    private void ToggleStack(AppItem item, DockWindow dock, AppButton button)
    {
        if (_flyout is StackFlyout open && open.Item == item) { open.SafeClose(); return; }
        ShowFlyout(new StackFlyout(dock, item, _icons, Settings, Apps.RefreshFolderIcon), dock.ScreenRectOf(button));
    }

    private void ToggleGroup(AppItem item, DockWindow dock, AppButton button, bool rename = false)
    {
        if (_flyout is GroupFlyout open && open.Item == item)
        {
            if (rename) open.BeginRename(); else open.SafeClose();
            return;
        }
        ShowFlyout(new GroupFlyout(dock, item, Apps, _icons, Settings.Current.Apps.ActiveIndicatorColor, rename), dock.ScreenRectOf(button));
    }

    /// <summary>The flyout a widget opens on click; widgets without one keep their own click.</summary>
    public static bool HasFlyout(IWidget widget) => widget is AiUsageWidget or VolumeWidget or MediaWidget;

    public void ToggleWidgetFlyout(DockWindow dock, WidgetTile tile)
    {
        FlyoutWindow? next = tile.Widget switch
        {
            AiUsageWidget => _flyout is AiUsageFlyout ? null : new AiUsageFlyout(dock, _icons),
            VolumeWidget v => _flyout is VolumeFlyout ? null : new VolumeFlyout(dock, v, _icons),
            MediaWidget { Current: not null } m => _flyout is MediaFlyout ? null : new MediaFlyout(dock, m, Apps, _icons),
            _ => null,
        };
        if (next == null) { _flyout?.SafeClose(); return; }
        ShowFlyout(next, dock.ScreenRectOf(tile));
    }

    public void ToggleTrayFlyout(DockWindow dock)
    {
        if (_flyout is TrayFlyout) { _flyout.SafeClose(); return; }
        if (_shell?.NotificationArea == null || dock.TrayTile == null) return;
        ShowFlyout(new TrayFlyout(dock, _shell.NotificationArea), dock.ScreenRectOf(dock.TrayTile));
    }

    private void ShowFlyout(FlyoutWindow f, RECT anchor)
    {
        _flyout?.SafeClose();
        _dock?.HideLabel();
        _flyout = f;
        f.FlyoutClosed += () => { if (_flyout == f) _flyout = null; };
        f.ShowAt(anchor);
    }

    public void ShowAppMenu(AppItem item, AppButton button, DockWindow dock)
    {
        dock.HideLabel();
        var menu = new ContextMenu();
        foreach (var w in item.Windows.ToList())
        {
            var mi = new MenuItem { Header = string.IsNullOrWhiteSpace(w.Title) ? item.DisplayName : w.Title, Icon = w.Icon != null ? new Image { Source = w.Icon, Width = 16, Height = 16 } : null };
            mi.Click += (_, _) => ForegroundActivator.Activate(w.Handle);
            menu.Items.Add(mi);
        }
        if (item.IsRunning) menu.Items.Add(new Separator());

        if (item.IsGroup)
        {
            Add(menu, "펼쳐 보기", () => ToggleGroup(item, dock, button));
            Add(menu, "이름 바꾸기", () => ToggleGroup(item, dock, button, rename: true));
            Add(menu, "묶음 풀기", () => Apps.Ungroup(item));
        }
        else if (!item.IsFolder) Add(menu, item.IsRunning ? "새 창" : "열기", () => Apps.Launch(item));
        else
        {
            Add(menu, "펼쳐 보기", () => ToggleStack(item, dock, button));
            Add(menu, "탐색기에서 열기", () => Apps.Launch(item));
            if (item.Pin is { } pin)
            {
                bool byName = pin.Sort == Stacks.FolderContents.SortName;
                var sortMenu = new MenuItem { Header = "정렬" };
                foreach (var (value, label) in new[] { (Stacks.FolderContents.SortDate, "최신순"), (Stacks.FolderContents.SortName, "이름순") })
                {
                    var mi = new MenuItem { Header = label, IsCheckable = true, IsChecked = (value == Stacks.FolderContents.SortName) == byName };
                    mi.Click += (_, _) => { Settings.Update(_ => pin.Sort = value); Apps.RefreshFolderIcon(item); };
                    sortMenu.Items.Add(mi);
                }
                menu.Items.Add(sortMenu);
            }
        }
        if (!item.IsFolder && !item.IsGroup) Add(menu, "관리자 권한으로 실행", () => Apps.Launch(item, elevated: true));
        if (item.IsGroup) Add(menu, "묶음을 도크에서 제거", () => Apps.Unpin(item));
        else if (item.IsPinned) Add(menu, "도크에서 고정 해제", () => Apps.Unpin(item));
        else Add(menu, "도크에 고정", () => Apps.Pin(item));
        var path = item.IsGroup ? null : item.IsFolder ? item.Pin?.Path : item.ExePath;
        if (!string.IsNullOrEmpty(path) && (File.Exists(path) || Directory.Exists(path))) Add(menu, "파일 위치 열기", () => AppLauncher.OpenFileLocation(path));

        if (item.IsRunning)
        {
            menu.Items.Add(new Separator());
            Add(menu, item.Windows.Count > 1 ? "모든 창 닫기" : "창 닫기", () =>
            {
                foreach (var w in item.Windows.ToList()) PostMessage(w.Handle, WM_SYSCOMMAND, SC_CLOSE, 0);
            });
            Add(menu, "작업 끝내기", () => EndTask(item));
        }
        OpenMenu(menu, button, dock);
    }

    public void ShowDockMenu(DockWindow dock)
    {
        dock.HideLabel();
        var menu = new ContextMenu();
        if (PendingUpdate is { } update && UpdateService.CanInstall)
        {
            Add(menu, $"GlassDock {update.Version}로 업데이트", () => _ = InstallUpdateAsync());
            menu.Items.Add(new Separator());
        }
        Add(menu, "GlassDock 설정", OpenSettings);
        Add(menu, "작업 관리자", () => AppLauncher.Execute("taskmgr.exe", null, null, null));
        Add(menu, "Windows 작업 표시줄 설정", () => AppLauncher.Execute("ms-settings:taskbar", null, null, null));
        menu.Items.Add(new Separator());
        menu.Items.Add(AddWidgetMenu());
        menu.Items.Add(SystemTilesMenu());
        menu.Items.Add(PinFolderMenu());
        menu.Items.Add(EdgeMenu());
        var autoHide = new MenuItem { Header = "자동 숨기기", IsCheckable = true, IsChecked = Settings.Current.AutoHide.Enabled };
        autoHide.Click += (_, _) => Settings.Update(s => s.AutoHide.Enabled = autoHide.IsChecked);
        menu.Items.Add(autoHide);
        var lockItem = new MenuItem { Header = "도크 잠그기", IsCheckable = true, IsChecked = Settings.Current.Dock.Locked };
        lockItem.Click += (_, _) => Settings.Update(s => s.Dock.Locked = lockItem.IsChecked);
        menu.Items.Add(lockItem);
        menu.Items.Add(new Separator());
        Add(menu, "GlassDock 종료", Quit);
        OpenMenu(menu, null, dock);
    }

    /// <summary>The same choice as dragging the dock to another edge; the menu works while the dock is locked.</summary>
    private MenuItem EdgeMenu()
    {
        var root = new MenuItem { Header = "도크 위치" };
        var current = DockMetrics.ParseEdge(Settings.Current.Dock.Edge);
        foreach (var (edge, label) in new[] { (DockEdge.Bottom, "아래"), (DockEdge.Top, "위"), (DockEdge.Left, "왼쪽"), (DockEdge.Right, "오른쪽") })
        {
            var mi = new MenuItem { Header = label, IsCheckable = true, IsChecked = edge == current };
            mi.Click += (_, _) => MoveDock(edge);
            root.Items.Add(mi);
        }
        return root;
    }

    public void MoveDock(DockEdge edge)
    {
        string name = DockMetrics.EdgeName(edge);
        if (Settings.Current.Dock.Edge == name) return;
        Log.Info($"Dock moved to the {name} edge");
        _flyout?.SafeClose();
        Settings.Update(s => s.Dock.Edge = name);
    }

    // ---- folders ----------------------------------------------------------------------------------

    private MenuItem PinFolderMenu()
    {
        var root = new MenuItem { Header = "폴더 고정", IsEnabled = !Settings.Current.Dock.Locked };
        var known = new (string label, string? path)[]
        {
            ("다운로드", Interop.ShellInterop.TryGetKnownFolder(Interop.ShellInterop.FOLDERID_Downloads)),
            ("문서", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)),
            ("사진", Environment.GetFolderPath(Environment.SpecialFolder.MyPictures)),
            ("바탕 화면", Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)),
        };
        var pinned = Settings.Current.Apps.Items.Where(p => p.Kind == "folder").Select(p => p.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var (label, path) in known)
        {
            if (string.IsNullOrEmpty(path) || !Directory.Exists(path)) continue;
            var mi = new MenuItem { Header = label, IsEnabled = !pinned.Contains(path) };
            mi.Click += (_, _) => PinFolder(path);
            root.Items.Add(mi);
        }
        root.Items.Add(new Separator());
        var other = new MenuItem { Header = "다른 폴더 선택…" };
        other.Click += (_, _) =>
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "도크에 고정할 폴더" };
            if (dlg.ShowDialog() == true) PinFolder(dlg.FolderName);
        };
        root.Items.Add(other);
        return root;
    }

    private void PinFolder(string path)
    {
        if (Settings.Current.Apps.Items.Any(p => p.Kind == "folder" && string.Equals(p.Path, path, StringComparison.OrdinalIgnoreCase))) return;
        if (PinStore.FromPath(path) is { } pin) Apps.AddPin(pin, Apps.PinnedCount);
        else Log.Warn($"Folder {path} cannot be pinned");
    }

    // ---- widgets ----------------------------------------------------------------------------------

    public void ShowWidgetMenu(WidgetTile tile, DockWindow dock)
    {
        dock.HideLabel();
        var menu = new ContextMenu();
        var own = tile.Widget.MenuItems.ToList();
        foreach (var (header, action) in own) Add(menu, header, action);
        if (own.Count > 0) menu.Items.Add(new Separator());
        bool locked = Settings.Current.Dock.Locked;
        if (!locked)
        {
            bool vertical = dock.Metrics.Vertical;
            Add(menu, vertical ? "위로 옮기기" : "왼쪽으로 옮기기", () => MoveTile(tile.SpecId, -1));
            Add(menu, vertical ? "아래로 옮기기" : "오른쪽으로 옮기기", () => MoveTile(tile.SpecId, +1));
            Add(menu, "위젯 제거", () => Settings.Update(s => s.Tiles.RemoveAll(t => t.Id == tile.SpecId)));
            menu.Items.Add(new Separator());
            menu.Items.Add(AddWidgetMenu());
        }
        Add(menu, "GlassDock 설정", OpenSettings);
        OpenMenu(menu, tile, dock);
    }

    /// <summary>Right-click menu of the built-in tiles (tray, quick settings, clock, notifications, show desktop).</summary>
    public void ShowTileMenu(string specId, TileButton tile, DockWindow dock)
    {
        dock.HideLabel();
        var menu = new ContextMenu();
        if (!Settings.Current.Dock.Locked)
        {
            bool vertical = dock.Metrics.Vertical;
            Add(menu, vertical ? "위로 옮기기" : "왼쪽으로 옮기기", () => MoveTile(specId, -1));
            Add(menu, vertical ? "아래로 옮기기" : "오른쪽으로 옮기기", () => MoveTile(specId, +1));
            Add(menu, "도크에서 숨기기", () => SetTileShown(specId, null, false));
            menu.Items.Add(new Separator());
            menu.Items.Add(SystemTilesMenu());
        }
        Add(menu, "GlassDock 설정", OpenSettings);
        OpenMenu(menu, tile, dock);
    }

    private static readonly (string type, string label)[] SystemTiles =
    [
        ("start", "시작 버튼"), ("tray", "숨겨진 아이콘"), ("quickSettings", "빠른 설정"),
        ("clock", "시계"), ("notifications", "알림"), ("showDesktop", "바탕 화면 보기"),
    ];

    /// <summary>Checkable list of the built-in tiles, so a hidden one can be brought back.</summary>
    private MenuItem SystemTilesMenu()
    {
        var root = new MenuItem { Header = "기본 칸 표시" };
        var tiles = Settings.Current.Tiles;
        foreach (var (type, label) in SystemTiles)
        {
            bool shown = tiles.Any(t => t.Type == type && t.Enabled);
            var mi = new MenuItem { Header = label, IsCheckable = true, IsChecked = shown, IsEnabled = !Settings.Current.Dock.Locked };
            mi.Click += (_, _) => SetTileShown(null, type, mi.IsChecked);
            root.Items.Add(mi);
        }
        return root;
    }

    /// <summary>
    /// Shows or hides a built-in tile, by spec id or by type. Hiding keeps the tile in the list (disabled) so
    /// it comes back in the same place; a type missing from the list is added at the end.
    /// </summary>
    private void SetTileShown(string? specId, string? type, bool shown) => Settings.Update(s =>
    {
        var spec = s.Tiles.FirstOrDefault(t => specId != null ? t.Id == specId : t.Type == type);
        if (spec == null)
        {
            if (!shown || type == null) return;
            s.Tiles.Add(spec = new TileSpec { Id = $"t-{type}-{Guid.NewGuid():N}"[..16], Type = type });
        }
        spec.Enabled = shown;
        Log.Info($"Tile {spec.Type} {(shown ? "shown" : "hidden")}");
    });

    private MenuItem AddWidgetMenu()
    {
        var root = new MenuItem { Header = "위젯 추가" };
        var present = Settings.Current.Tiles.Select(t => t.Type).ToHashSet();
        foreach (var d in WidgetRegistry.All.Where(d => d.IsAvailable()))
        {
            var mi = new MenuItem { Header = d.DisplayName, ToolTip = d.Description, IsEnabled = !present.Contains(d.Type) && !Settings.Current.Dock.Locked };
            mi.Click += (_, _) => AddWidget(d.Type);
            root.Items.Add(mi);
        }
        return root;
    }

    /// <summary>Adds a widget at the start of the right-hand group (after the spacer), else before the tray.</summary>
    public void AddWidget(string type) => Settings.Update(s =>
    {
        if (s.Tiles.Any(t => t.Type == type)) return;
        int at = s.Tiles.FindIndex(t => t.Type == "spacer");
        at = at >= 0 ? at + 1 : s.Tiles.FindIndex(t => t.Type == "tray");
        if (at < 0) at = s.Tiles.Count;
        s.Tiles.Insert(at, new TileSpec { Id = $"w-{type}-{Guid.NewGuid():N}"[..16], Type = type });
    });

    public void RemoveWidget(string type) => Settings.Update(s => s.Tiles.RemoveAll(t => t.Type == type));

    /// <summary>
    /// Puts the tiles named in <paramref name="ids"/> into that order, in the slots they already occupy in
    /// the tile list; every other tile (disabled ones, the other half of a bar) stays where it is.
    /// </summary>
    public void ReorderTiles(IReadOnlyList<string> ids) => Settings.Update(s =>
    {
        var slots = Enumerable.Range(0, s.Tiles.Count).Where(i => ids.Contains(s.Tiles[i].Id)).ToList();
        var tiles = ids.Select(id => s.Tiles.FirstOrDefault(t => t.Id == id)).ToList();
        if (slots.Count != ids.Count || tiles.Any(t => t == null))
        {
            Log.Warn($"Tile reorder skipped: the tile list no longer matches the dock ({string.Join(",", ids)})");
            return;
        }
        for (int k = 0; k < slots.Count; k++) s.Tiles[slots[k]] = tiles[k]!;
        Log.Info($"Tiles reordered: {string.Join(",", ids)}");
    });

    /// <summary>Moves a tile one visible slot left or right (disabled tiles are skipped).</summary>
    private void MoveTile(string id, int delta) => Settings.Update(s =>
    {
        int from = s.Tiles.FindIndex(t => t.Id == id);
        if (from < 0) return;
        int to = from + delta;
        while (to >= 0 && to < s.Tiles.Count && !s.Tiles[to].Enabled) to += delta;
        if (to < 0 || to >= s.Tiles.Count) return;
        var tile = s.Tiles[from];
        s.Tiles.RemoveAt(from);
        s.Tiles.Insert(to, tile);
    });

    private static void Add(ContextMenu menu, string header, Action action)
    {
        var mi = new MenuItem { Header = header };
        mi.Click += (_, _) => action();
        menu.Items.Add(mi);
    }

    private void OpenMenu(ContextMenu menu, UIElement? target, DockWindow dock)
    {
        dock.TakeForeground();
        // Open away from the screen edge the dock sits on.
        menu.Placement = target == null ? PlacementMode.MousePoint : dock.Metrics.Edge switch
        {
            DockEdge.Top => PlacementMode.Bottom,
            DockEdge.Left => PlacementMode.Right,
            DockEdge.Right => PlacementMode.Left,
            _ => PlacementMode.Top,
        };
        menu.PlacementTarget = target ?? dock;
        _openMenus++;
        menu.Closed += (_, _) => _openMenus--;
        menu.IsOpen = true;
    }

    private void EndTask(AppItem item)
    {
        var pids = item.Windows.Select(w => w.ProcId).Where(p => p.HasValue).Select(p => (int)p!.Value).Distinct().ToList();
        if (pids.Count == 0) return;
        var answer = MessageBox.Show($"'{item.DisplayName}'을(를) 강제로 끝낼까요? 저장하지 않은 내용은 사라집니다.", "작업 끝내기", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes) return;
        foreach (var pid in pids)
        {
            try { using var p = Process.GetProcessById(pid); p.Kill(); Log.Info($"Ended task pid {pid} ({item.DisplayName})"); }
            catch (Exception ex) when (ex is ArgumentException or System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                Log.Warn($"Ending pid {pid} failed", ex);
            }
        }
    }

    public void OpenSettings()
    {
        if (_settingsWindow != null) { _settingsWindow.Activate(); return; }
        _settingsWindow = new SettingsWindow(this);
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    public void RestoreTaskbarNow() => Settings.Update(s => s.Taskbar.HideNative = false);

    public void Quit()
    {
        Log.Info("Quit requested");
        Application.Current.Shutdown(AppConstants.ExitCodes.UserQuit);
    }

    public void Dispose()
    {
        _quitting = true;
        _flyout?.SafeClose();
        _settingsWindow?.Close();
        _hotkeys?.Dispose();
        _dock?.Shutdown();
        Widgets.Dispose();
        Apps.DisposeWatchers();
        Fullscreen?.Dispose();
        Updates?.Dispose();
        // Dispose ManagedShell before restoring: its fake Shell_TrayWnd must be gone so Explorer's is found first.
        _shell?.Dispose();
        _hider?.Restore("quit");
        _host?.Dispose();
        _icons.Dispose();
        Settings.Dispose();
        Log.Info("GlassDock stopped");
    }
}
