using System.Text.Json;
using System.Text.Json.Serialization;

namespace GlassDock.Config;

/// <summary>User settings persisted to %APPDATA%\GlassDock\settings.json (schema in docs/design.md §10).</summary>
public sealed class DockSettings
{
    public const int CurrentSchemaVersion = 2;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public GeneralSettings General { get; set; } = new();
    public TaskbarSettings Taskbar { get; set; } = new();
    public DockLayoutSettings Dock { get; set; } = new();
    public FullscreenSettings Fullscreen { get; set; } = new();
    public AutoHideSettings AutoHide { get; set; } = new();
    public AppearanceSettings Appearance { get; set; } = new();
    public ShortcutSettings Shortcuts { get; set; } = new();
    public AppsSettings Apps { get; set; } = new();
    public ClockSettings Clock { get; set; } = new();
    public List<TileSpec> Tiles { get; set; } = TileSpec.Defaults();
    public DiagnosticsSettings Diagnostics { get; set; } = new();

    /// <summary>Keeps properties written by a newer build so an older build does not drop them.</summary>
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }

    public DockSettings Clone() =>
        JsonSerializer.Deserialize<DockSettings>(JsonSerializer.Serialize(this, SettingsStore.JsonOptions), SettingsStore.JsonOptions)!;
}

public sealed class GeneralSettings
{
    /// <summary>Off by default so a development build never registers itself at sign-in.</summary>
    public bool LaunchAtStartup { get; set; }
    public bool FirstRunDone { get; set; }
    /// <summary>Look for a newer release on GitHub now and then; installing always waits for the user.</summary>
    public bool CheckForUpdates { get; set; } = true;
    /// <summary>A release the user chose to skip: no dock button or flyout for it (a later one shows again).</summary>
    public string? SkippedUpdate { get; set; }
}

public sealed class TaskbarSettings
{
    public bool HideNative { get; set; } = true;
    public bool RestoreOnSessionEnd { get; set; } = true;
}

public sealed class DockLayoutSettings
{
    /// <summary>"bottom" | "top" | "left" | "right"</summary>
    public string Edge { get; set; } = "bottom";
    /// <summary>"pill" | "bar"</summary>
    public string Shape { get; set; } = "pill";
    /// <summary>"S" | "M" | "L"</summary>
    public string Size { get; set; } = "M";
    public double EdgeGapDip { get; set; } = 8;
    public double ItemSpacingDip { get; set; } = 6;
    public double PaddingDip { get; set; } = 8;
    public double? CornerRadiusDip { get; set; }
    public double MaxWidthPercent { get; set; } = 92;
    public bool ReserveSpace { get; set; } = true;
    /// <summary>While a window is maximized, the reserved strip around the pill is filled with a full-width glass band.</summary>
    public bool FillMaximizedBand { get; set; } = true;
    public double ReserveGapDip { get; set; } = 6;
    public bool Locked { get; set; }
    public bool ShowLabelsOnHover { get; set; } = true;
}

/// <summary>Design §9.5. While on, the dock reserves no screen space and slides off its edge when not in use.</summary>
public sealed class AutoHideSettings
{
    public bool Enabled { get; set; }
    /// <summary>How long the pointer rests on the screen edge before the dock comes out.</summary>
    public int RevealDelayMs { get; set; } = 80;
    /// <summary>Also come out when the pointer rests where the dock sits, without going all the way to the edge.</summary>
    public bool RevealOnDockArea { get; set; } = true;
    /// <summary>How long the pointer rests on the dock's spot before it comes out; longer than at the edge, since windows have controls there.</summary>
    public int DockAreaRevealDelayMs { get; set; } = 120;
    /// <summary>How long after the pointer leaves the dock (and its menus and flyouts) it goes away.</summary>
    public int HideDelayMs { get; set; } = 600;
    public bool ShowOnWinKey { get; set; } = true;
    /// <summary>After the Win key is released the dock stays this long, unless the pointer comes to it.</summary>
    public int WinKeyLingerMs { get; set; } = 1200;
}

public sealed class FullscreenSettings
{
    public bool HideDock { get; set; } = true;
}

public sealed class AppearanceSettings
{
    /// <summary>"liquidGlass" | "frosted" | "clear" | "midnight" | "black" | "white"</summary>
    public string Style { get; set; } = "liquidGlass";
    /// <summary>"#RRGGBB" or null for the Windows accent colour.</summary>
    public string? AccentColor { get; set; }
    /// <summary>"system" | "light" | "dark"</summary>
    public string Theme { get; set; } = "system";
    public MagnificationSettings Magnification { get; set; } = new();
    public bool Animations { get; set; } = true;
}

public sealed class MagnificationSettings
{
    public bool Enabled { get; set; }
    public double MaxScale { get; set; } = 1.5;
    public double RangeItems { get; set; } = 2.5;
}

public sealed class ShortcutSettings
{
    /// <summary>"dock" = Win+1..9 follow the dock order, "native" = leave them to Explorer.</summary>
    public string WinNumberMode { get; set; } = "dock";
    public bool InterceptWinT { get; set; } = true;
    public bool InterceptWinB { get; set; } = true;
    /// <summary>
    /// Take the shortcut keys over from Explorer as real hotkeys, so they work in front of elevated windows
    /// (games) and the hook can pause in fullscreen. Explorer's own Win+N returns only after it restarts.
    /// </summary>
    public bool TakeOverExplorerHotkeys { get; set; } = true;
    /// <summary>
    /// Pause the hook in fullscreen even while it still translates shortcuts (which then stop working there).
    /// With every shortcut a real hotkey the hook pauses in fullscreen regardless.
    /// </summary>
    public bool SuspendHookInFullscreen { get; set; }
}

public sealed class AppsSettings
{
    public bool GroupWindows { get; set; } = true;
    public bool ShowRunningIndicator { get; set; } = true;
    public bool ShowBadges { get; set; } = true;
    /// <summary>"preview" | "cycle"</summary>
    public string MultiWindowClick { get; set; } = "preview";
    /// <summary>Bar under the foreground app: "accent" | "foreground" | "white" | "#RRGGBB".</summary>
    public string ActiveIndicatorColor { get; set; } = "accent";
    public List<PinnedItem> Items { get; set; } = [];
}

public sealed class PinnedItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    /// <summary>"app" | "folder" | "group"</summary>
    public string Kind { get; set; } = "app";
    public string? Aumid { get; set; }
    /// <summary>Copied shortcut, relative to %APPDATA%\GlassDock.</summary>
    public string? Shortcut { get; set; }
    public string? Target { get; set; }
    /// <summary>Shortcut pins only: the icon the shortcut was given, which wins over the target's own.</summary>
    public string? IconPath { get; set; }
    public int IconIndex { get; set; }
    public string? Arguments { get; set; }
    public string? WorkingDir { get; set; }
    public string? DisplayName { get; set; }
    public string? Path { get; set; }
    /// <summary>Folders only: "date" (newest first, default) | "name".</summary>
    public string? Sort { get; set; }
    /// <summary>Groups only: the apps inside, in display order. A group never contains a group.</summary>
    public List<PinnedItem>? Children { get; set; }
}

public sealed class ClockSettings
{
    public bool Hour12 { get; set; }
    public bool ShowSeconds { get; set; }
    public bool ShowDate { get; set; } = true;
}

public sealed class DiagnosticsSettings
{
    /// <summary>"debug" | "info" | "warning" | "error"</summary>
    public string LogLevel { get; set; } = "info";
}

public sealed class TileSpec
{
    public string Id { get; set; } = "";
    /// <summary>
    /// "start" | "apps" | "spacer" | "tray" | "quickSettings" | "clock" | "notifications" | "showDesktop",
    /// or a widget type from <c>WidgetRegistry</c> ("media", "volume", "cpu", "memory", "network", "battery").
    /// </summary>
    public string Type { get; set; } = "";
    public bool Enabled { get; set; } = true;

    public static List<TileSpec> Defaults() =>
    [
        new() { Id = "t-start", Type = "start" },
        new() { Id = "t-apps", Type = "apps" },
        new() { Id = "t-sp", Type = "spacer" },
        new() { Id = "t-tray", Type = "tray" },
        new() { Id = "t-qs", Type = "quickSettings" },
        new() { Id = "t-clock", Type = "clock" },
        new() { Id = "t-notif", Type = "notifications" },
    ];
}
