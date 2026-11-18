namespace GlassDock.Config;

/// <summary>Compile-time defaults. User-tunable values live in <see cref="DockSettings"/>.</summary>
public static class AppConstants
{
    public const string AppName = "GlassDock";
    public const string SingleInstanceMutex = @"Local\GlassDock.Main";
    public const string OpenSettingsMessage = "GlassDock.OpenSettings";
    public const string QuitMessage = "GlassDock.Quit";

    public static class ExitCodes
    {
        public const int UserQuit = 0;
        public const int AlreadyRunning = 2;
        public const int Crash = 3;
    }

    public static class Logging
    {
        public const int RetainDays = 7;
    }

    public static class Taskbar
    {
        public const int VerifyDelayMs = 500;
        public const int RehidePollMs = 250;
    }

    public static class Explorer
    {
        public const int SettleDelayMs = 800;
        /// <summary>The app id Windows gives File Explorer windows, which carry none of their own.</summary>
        public const string Aumid = "Microsoft.Windows.Explorer";
        public const string ExeName = "explorer.exe";
    }

    public static class Packages
    {
        /// <summary>Packaged apps install to "&lt;drive&gt;:\Program Files\WindowsApps\&lt;package full name&gt;\..." (or "&lt;drive&gt;:\WindowsApps\..." on other drives).</summary>
        public const string RootFolder = "\\WindowsApps\\";
        public const string ManifestName = "AppxManifest.xml";
    }

    public static class CrashLoop
    {
        public const int MaxRestarts = 3;
        public const int WindowSeconds = 60;
    }

    /// <summary>
    /// Exceptions on the UI thread after startup that are logged and survived. More than this many within the
    /// window means the dock is stuck in a failing state, and a restart (crash path, watchdog) is the way out.
    /// </summary>
    public static class UiFaults
    {
        public const int MaxSurvived = 3;
        public const int WindowSeconds = 60;
    }

    public static class Settings
    {
        public const int SaveDebounceMs = 500;
        /// <summary>A settings card whose setting has no effect right now (its parent setting is off).</summary>
        public const double DisabledCardOpacity = 0.45;
        /// <summary>Days of logs the problem report collects.</summary>
        public const int ReportLogDays = 3;
    }

    public static class Input
    {
        public const int HookRefreshMinutes = 5;
        /// <summary>Explorer's taskbar registers its hotkey table entry i under this id + i.</summary>
        public const int ExplorerHotkeyBaseId = 500;
        /// <summary>WM_USER + this, sent to Explorer's taskbar with a hotkey id, unregisters that hotkey.</summary>
        public const int ExplorerUnregisterHotkeyOffset = 231;
        public const uint ExplorerMessageTimeoutMs = 1000;
        /// <summary>Marks synthetic keys so our own hook lets them through.</summary>
        public const nuint InjectTag = 0x474C4453; // "GLDS"
        /// <summary>
        /// Keys that Win+digit/Win+T/Win+B are translated to: F13..F24 first, then unassigned VKs.
        /// Some of these are taken by other software (or Windows itself, e.g. the Copilot key), so
        /// each slot uses the first candidate whose hotkeys can all be registered.
        /// </summary>
        public static readonly int[] PrivateKeyCandidates =
            [.. Enumerable.Range(0x7C, 12), .. Enumerable.Range(0x97, 9), .. Enumerable.Range(0x88, 8)];
        /// <summary>How long each activation step gets to take effect before a stronger one is tried.</summary>
        public const int ActivateStepMs = 200;
        /// <summary>The last step's wait; a fullscreen game restoring its display mode can take this long.</summary>
        public const int ActivateGiveUpMs = 1000;
        public const int ActivatePollMs = 10;
        public const uint WinStaleCheckMs = 2000;
        /// <summary>Longest wait for our injected key to come back through the hook before held-back key-ups are released anyway.</summary>
        public const int EchoTimeoutMs = 300;
        /// <summary>Startup check that each private hotkey really arrives (another program's hook may eat it).</summary>
        public const int ProbeWaitMs = 250;
        public const int ProbeRounds = 4;
    }

    public static class Glass
    {
        public const int OcclusionCheckHz = 10;
        /// <summary>A window must stay away this long before the lens returns (no flicker while dragging along the edge).</summary>
        public const int OcclusionReleaseMs = 500;
        public const int CrossFadeMs = 150;
        /// <summary>Least tint alpha of the band under maximized windows (the dock style's tint colour).</summary>
        public const byte BandMinTintAlpha = 0xC0;

        /// <summary>Liquid glass lens look. Lengths in DIP, scaled to physical pixels per monitor.</summary>
        public static class Lens
        {
            public const double BezelDip = 16;
            public const double RefractionDip = 12;
            public const double Frost = 0.06;
            public const double RimFrost = 0.45;
            public const double Chroma = 0.10;
            public const double RimGlow = 0.10;
            public const double Vibrancy = 1.2;
            public const double BlurDip = 8;
        }
    }

    public static class Icons
    {
        public static readonly int[] PixelSizes = [16, 24, 32, 48, 64, 96, 128, 256];
        public const int CacheMaxEntries = 256;
        public const int WindowIconTimeoutMs = 200;
    }

    public static class Dock
    {
        /// <summary>The update pill's height, as a fraction of the icon size.</summary>
        public const double UpdatePillHeight = 0.72;
        /// <summary>Icon size in DIP for each size preset.</summary>
        public static int IconSizeFor(string size) => size switch { "S" => 32, "L" => 48, _ => 40 };
        public const double ShadowMarginDip = 24;
        public const double LabelHeadroomDip = 34;
        /// <summary>Room beside a left/right dock for hover labels (they open sideways; long names are trimmed to fit).</summary>
        public const double SideLabelHeadroomDip = 220;
        public const double LabelGapDip = 6;
        public const double IndicatorDip = 4;
        /// <summary>Running apps: a short bar; the foreground app: a longer accent bar.</summary>
        public const double IndicatorRunningWidthDip = 10;
        public const double IndicatorActiveWidthDip = 20;
        public const double IndicatorHeightDip = 3.5;
        /// <summary>
        /// Outline-only cell around running app icons (none when the app is not running), drawn as an
        /// inset: a faint shadow along the top edge and a faint light along the bottom, so the icon
        /// looks pressed into the glass. Fractions are of the tile size; shade alphas are of black,
        /// light alphas of white.
        /// </summary>
        public const double CellGapFraction = 0.05;
        public const double CellCornerFraction = 0.24;
        public const double CellIconFraction = 0.72;
        public const double CellEdgeDip = 1;
        public const byte CellShadeTop = 0x1C;
        public const byte CellLightBottom = 0x30;
        public const byte CellShadeActiveTop = 0x2C;
        public const byte CellLightActiveBottom = 0x50;
        public const double TileMinWidthDip = 40;
        /// <summary>Clock text sizes as fractions of the icon size, and the date's opacity.</summary>
        public const double ClockTimeFraction = 0.42;
        public const double ClockDateFraction = 0.34;
        public const double ClockDateOpacity = 0.9;
    }

    public static class Tray
    {
        /// <summary>How often the overflow flyout checks whether an icon's menu is still open; short enough to catch a quick click.</summary>
        public const int MenuWatchMs = 30;
        /// <summary>An icon that shows no menu within this time after a right-click gives the flyout back its normal light dismiss.</summary>
        public const int MenuWaitMs = 1500;
    }

    public static class Animation
    {
        public const int HoverMs = 140;
        public const int PressMs = 90;
        public const int BounceMs = 450;
        public const int LaunchBounceTimeoutMs = 6000;
        public const int IndicatorMs = 180;
    }

    public static class Drag
    {
        public const int RemoveDistanceDip = 90;
        /// <summary>How long neighbours take to slide aside, and the dropped icon to settle.</summary>
        public const int ShiftMs = 170;
        public const double LiftDip = 6;
        public const double Opacity = 0.9;
        public const double RemoveOpacity = 0.35;
    }

    /// <summary>Icons growing under the pointer (design §9.4).</summary>
    public static class Magnify
    {
        /// <summary>Share of the remaining distance to the target scale covered each frame (an exponential ease).</summary>
        public const double EasePerFrame = 0.32;
        /// <summary>Below this difference a scale snaps to its target, so the per-frame loop can stop.</summary>
        public const double SettleEpsilon = 0.002;
        public const double MinScale = 1.1;
        public const double MaxScale = 2.0;
    }

    public static class AutoHide
    {
        /// <summary>The strip along the screen edge that brings the dock out (alpha 1, so it takes the pointer).</summary>
        public const int SensorThicknessPx = 2;
        public const int SlideMs = 180;
        /// <summary>How often a shown dock checks whether anything still keeps it up.</summary>
        public const int CheckMs = 100;
        /// <summary>Poll rate of the hidden dock's spot; finer than CheckMs so the short reveal delay is not rounded up.</summary>
        public const int AreaCheckMs = 40;
        /// <summary>Pointer within this distance of the pill still counts as over the dock.</summary>
        public const double HoverMarginDip = 12;
    }

    /// <summary>Dragging empty dock area to another screen edge (design §9.7).</summary>
    public static class DockMove
    {
        /// <summary>The pointer must travel this far before the dock starts to move, so clicks on the glass stay clicks.</summary>
        public const double StartDistanceDip = 12;
        public const double GhostOutlineDip = 2;
        public const byte GhostFillAlpha = 0x38;
        public const byte GhostOutlineAlpha = 0xC0;
    }

    public static class Startup
    {
        /// <summary>Started at sign-in: how long to wait for Explorer's taskbar before starting anyway.</summary>
        public const int ExplorerWaitMs = 60_000;
        public const int ExplorerPollMs = 250;
    }

    /// <summary>Self-update from the public release repository (docs/design.md, "Updates").</summary>
    public static class Update
    {
        public const string LatestReleaseApi = "https://api.github.com/repos/Mojjiman/GlassDock/releases/latest";
        public const string ReleasesPage = "https://github.com/Mojjiman/GlassDock/releases";
        public const string ApiOverrideEnv = "GLASSDOCK_UPDATE_API";
        /// <summary>The release zip from build/publish.ps1: GlassDock-&lt;version&gt;-win-x64.zip.</summary>
        public const string ZipPrefix = "GlassDock-";
        public const string ZipSuffix = "-win-x64.zip";
        /// <summary>Not right at sign-in, when the network may still be coming up.</summary>
        public const int FirstCheckDelaySeconds = 60;
        public const int CheckIntervalHours = 6;
        public const int HttpTimeoutSeconds = 120;
        public const string NewSuffix = ".new";
        public const string OldSuffix = ".old";
        /// <summary>The updated exe is started with this and the old pid, and waits for it to quit.</summary>
        public const string WaitPidArg = "--wait-pid";
        public const int WaitForOldMs = 15000;
        public const int CleanUpRetrySeconds = 15;
    }

    public static class Fullscreen
    {
        /// <summary>
        /// Entering fullscreen waits this long; leaving is checked at once (see FullscreenMonitor.Kick).
        /// A game's startup mode switches (fullscreen add/remove/add, ABN on/off/on) settle within ~30 ms.
        /// </summary>
        public const int EnterDebounceMs = 50;
        public static readonly string[] IgnoredClasses =
            ["Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "XamlExplorerHostIslandWindow", "Windows.UI.Core.CoreWindow"];
        /// <summary>
        /// Start and search. While one is in front of a fullscreen game the dock shows, as the native
        /// taskbar does when Win is pressed in a game.
        /// </summary>
        public static readonly string[] LauncherProcesses = ["StartMenuExperienceHost.exe", "SearchHost.exe"];
        /// <summary>Longest wait for Start or search to give up the foreground after our Esc.</summary>
        public const int LauncherCloseWaitMs = 500;
        /// <summary>How often a flyout opened over a closing Start checks whether it can take the foreground.</summary>
        public const int LauncherPollMs = 10;
    }

    public static class Widgets
    {
        public const int CpuRefreshMs = 1000;
        public const int MemoryRefreshMs = 2000;
        public const int NetworkRefreshMs = 1000;
        public const int BatteryRefreshMs = 30000;
        /// <summary>Upper bound of the exponential backoff after a widget sampler throws.</summary>
        public const int ErrorBackoffMaxTicks = 60;
        /// <summary>Volume change per wheel notch (0..1 scale).</summary>
        public const float VolumeWheelStep = 0.02f;
        /// <summary>Width of the now-playing title column, in icon sizes; fixed so track changes do not resize the dock.</summary>
        public const double MediaTextWidthIcons = 2.8;
        public const int MediaArtPx = 96;
        public const int MediaReadRetries = 3;
        public const int MediaReadRetryMs = 300;

        public const double VolumeFlyoutWidth = 320;
        public const double VolumeSliderBarDip = 32;
        /// <summary>The volume pill's hit area; taller than the bar so the edges are easy to grab.</summary>
        public const double VolumeSliderHitDip = 40;
        public const double VolumeButtonDip = 36;
        public const double VolumeButtonGlyphSize = 15;

        public const double MediaFlyoutArtDip = 288;
        /// <summary>The art is decoded at twice its size for 200% displays.</summary>
        public const int MediaFlyoutArtPx = 576;
        public const double MediaSeekBarDip = 4;
        public const double MediaSeekHitDip = 16;
        public const double MediaPlayButtonDip = 48;
        public const double MediaPlayGlyphSize = 20;
        public const double MediaSkipButtonDip = 40;
        public const double MediaSkipGlyphSize = 16;
        public const double AppVolumeBarDip = 8;
        public const double AppVolumeHitDip = 24;
        public const double AppVolumeIconDip = 22;
        /// <summary>The timeline is extrapolated between the app's (sparse) position reports.</summary>
        public const int MediaTimelineMs = 500;
        /// <summary>App volume has no change event here, and its session may appear only once playback starts.</summary>
        public const int AppVolumeRefreshMs = 1000;
        /// <summary>The volume flyout's app list scrolls past this height, so many audio apps do not make it screen-tall.</summary>
        public const double MixerMaxHeightDip = 320;
    }

    /// <summary>AI usage widget (Claude and Codex plan limits, Claude Code token counts).</summary>
    public static class AiUsage
    {
        public const int WidgetRefreshMs = 15000;
        /// <summary>With no AI widget left this long, the tracker and its caches are dropped (longer than a dock rebuild).</summary>
        public const int ReleaseDelayMs = 60000;
        public const int TranscriptScanMs = 30000;
        public const int CodexScanMs = 30000;
        /// <summary>The usage endpoint rate-limits third-party callers; other tools poll it about this often.</summary>
        public const int ClaudeLimitsPollMs = 5 * 60 * 1000;
        public const int ClaudeLimitsBackoffMaxMs = 60 * 60 * 1000;
        /// <summary>How often an expired Claude login may be refreshed through <c>claude auth status</c>, and how long that may take.</summary>
        public const int ClaudeLoginRefreshRetryMs = 30 * 60 * 1000;
        public const int ClaudeCliTimeoutMs = 30000;
        public const int HttpTimeoutMs = 10000;
        /// <summary>
        /// Undocumented endpoint that Claude Code's /usage and community tools (CodexBar, Claude Usage Tray)
        /// read with the CLI's OAuth token; five_hour/seven_day each carry utilization (0-100) and resets_at.
        /// </summary>
        public const string ClaudeUsageUrl = "https://api.anthropic.com/api/oauth/usage";
        public const string ClaudeOauthBeta = "oauth-2025-04-20";
        public const string ClaudeUsagePage = "https://claude.ai/settings/usage";
        public const string CodexUsagePage = "https://chatgpt.com/codex/settings/usage";
        public const int FlyoutRefreshMs = 5000;
        public const double ClaudeSessionHours = 5;
        /// <summary>Codex writes rate limits into its session logs; only the newest few files are looked at.</summary>
        public const int CodexFilesToScan = 5;
        public const int CodexTailBytes = 1 << 20;
        public const int TranscriptBufferBytes = 1 << 20;
        /// <summary>Packaged app ids whose icons mark the rows (Get-StartApps: "Claude", and "ChatGPT" which hosts Codex).</summary>
        public const string ClaudeAppId = "Claude_pzs8sxrjxfjjc!Claude";
        public const string CodexAppId = "OpenAI.Codex_2p2nqsd0c76g0!App";
        // Row layout, in icon sizes.
        public const double BadgeIcons = 0.4;
        public const double TextIcons = 0.3;
        public const double ValueIcons = 0.33;
        public const double GapIcons = 0.18;
        public const double RowGapIcons = 0.08;
        public const double BarIcons = 1.7;
        public const double BarThickIcons = 0.12;
        public const byte TrackAlpha = 0x33;
        /// <summary>The bar turns amber, then red, as the window fills.</summary>
        public const double WarnPercent = 60;
        public const double DangerPercent = 85;
        public static readonly System.Windows.Media.Color OkColor = System.Windows.Media.Color.FromRgb(0x34, 0xC7, 0x59);
        public static readonly System.Windows.Media.Color WarnColor = System.Windows.Media.Color.FromRgb(0xFF, 0xB0, 0x20);
        public static readonly System.Windows.Media.Color DangerColor = System.Windows.Media.Color.FromRgb(0xFF, 0x45, 0x3A);
        // Second stops of the flyout's ring gradients (the first stops are the colours above).
        public static readonly System.Windows.Media.Color OkColor2 = System.Windows.Media.Color.FromRgb(0x30, 0xD5, 0xC8);
        public static readonly System.Windows.Media.Color WarnColor2 = System.Windows.Media.Color.FromRgb(0xFF, 0x7A, 0x00);
        public static readonly System.Windows.Media.Color DangerColor2 = System.Windows.Media.Color.FromRgb(0xFF, 0x2D, 0x78);
        public const double RingDip = 96;
        public const double RingStrokeDip = 9;
        public const int RingSweepMs = 700;
    }

    public static class Flyouts
    {
        /// <summary>Deep tint over a flyout's acrylic (alpha leaves a little of the blur showing).</summary>
        public static readonly System.Windows.Media.Color TintDarkTop = System.Windows.Media.Color.FromArgb(0xD0, 0x26, 0x27, 0x30);
        public static readonly System.Windows.Media.Color TintDarkBottom = System.Windows.Media.Color.FromArgb(0xE0, 0x12, 0x12, 0x18);
        public static readonly System.Windows.Media.Color TintLightTop = System.Windows.Media.Color.FromArgb(0xC8, 0xFF, 0xFF, 0xFF);
        public static readonly System.Windows.Media.Color TintLightBottom = System.Windows.Media.Color.FromArgb(0xD8, 0xEE, 0xEF, 0xF4);
        public const double IconButtonDip = 30;
        public const double IconButtonGlyphSize = 13;
        public const byte IconButtonGlyphAlpha = 0xA8;
        public const byte IconButtonHoverAlpha = 0x1A;
        public const byte FilledButtonAlpha = 0x1F;
        public const byte FilledButtonHoverAlpha = 0x33;
        public const byte SliderTrackAlpha = 0x26;
        public const double UpdateFlyoutWidth = 320;
        /// <summary>Slim overlay scrollbar: a pill that widens under the pointer, no arrows or track.</summary>
        public const double ScrollGutterDip = 12;
        public const double ScrollThumbDip = 4;
        public const double ScrollThumbHoverDip = 6;
        public const double ScrollThumbMinDip = 28;
        public const byte ScrollThumbAlpha = 0x40;
        public const byte ScrollThumbHoverAlpha = 0x90;
        public const byte SliderMutedAlpha = 0x60;
        public const byte MutedTextAlpha = 0xA8;
        public const byte FaintTextAlpha = 0x70;
    }

    public static class Groups
    {
        public const string DefaultName = "새 묶음";
        /// <summary>
        /// How close (in slots) the dragged icon must sit over another app to aim at it. Reordering past
        /// an app waits until the icon leaves this zone on the far side, so there is room to aim.
        /// </summary>
        public const double MergeZoneSlots = 0.25;
        /// <summary>Holding still over an app this long arms "drop to group".</summary>
        public const int MergeHoverMs = 400;
        /// <summary>Moving more than this while aiming restarts the hold: only a pause arms grouping.</summary>
        public const double StillDip = 6;
        public const double MergeTargetScale = 1.2;
        public const double AimedScale = 1.06;
        /// <summary>The dragged icon while armed, as if going into the group.</summary>
        public const double SinkScale = 0.82;
        public const double AimedRingOpacity = 0.4;
        public const double RingDip = 2;
        public const double RingOutsetDip = 2;
        public const byte ArmedFillAlpha = 0x38;
        /// <summary>A group's collage fills nearly its whole cell (an app icon uses Dock.CellIconFraction).</summary>
        public const double IconFraction = 0.96;
        public const int MinColumns = 3;
        // The group flyout: a few large icons rather than the stack flyout's dense grid.
        public const int FlyoutColumns = 3;
        public const double FlyoutIconDip = 64;
        public const double FlyoutCellWidthDip = 128;
        public const double FlyoutCellHeightDip = 122;
        public const double FlyoutNameFontSize = 13;
        public const double FlyoutTitleFontSize = 20;
        public const double FlyoutCountFontSize = 13;
        /// <summary>Holding an app this long in the group flyout picks it up for reordering.</summary>
        public const int HoldToReorderMs = 350;
        public const int ReorderShiftMs = 180;
        public const double LiftScale = 1.12;
        public const double LiftOpacity = 0.9;
    }

    public static class Stacks
    {
        public const int Columns = 5;
        public const int VisibleRows = 4;
        public const double CellWidthDip = 96;
        public const double CellHeightDip = 104;
        public const double IconDip = 48;
        /// <summary>Items listed in the flyout; the rest are a click away in Explorer.</summary>
        public const int MaxItems = 80;
        public const int CollageItems = 4;
        public const byte CollageFillAlpha = 0x38;
        public const byte CollageEdgeAlpha = 0x60;
        public const int FlyoutRefreshDebounceMs = 300;
        /// <summary>Longer for the dock icon: a running download rewrites the folder for minutes.</summary>
        public const int IconRefreshDebounceMs = 1500;
    }

    public static class Preview
    {
        public const double MaxCardWidthDip = 240;
        public const double MaxCardHeightDip = 135;
    }
}
