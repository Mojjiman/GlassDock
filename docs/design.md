# GlassDock — Design Report (implementation-ready)

Status: design v1, 2026-11-16. Target: Windows 11 Pro 10.0.26200 x64 (verified on the dev machine: build 26200.9711, one 2560x1440 monitor, `Shell_TrayWnd` style `0x96000000`, ex-style `0x88` = `WS_EX_TOPMOST | WS_EX_TOOLWINDOW`, `ABM_GETSTATE` = 0, i.e. auto-hide currently off).

Conventions used in this report:

- **[VERIFIED]** — checked against source code or documentation during this design (links in the Sources section).
- **[EXPERIMENT En]** — I am not certain of the Windows behaviour. Do not build on it before running spike `En` (section 11, milestone M0). Every such item has a stated fallback.
- "Dock" = one GlassDock bar on one monitor. "Native taskbar" = Explorer's `Shell_TrayWnd` / `Shell_SecondaryTrayWnd`.

---

## 1. Executive summary and recommended stack

### 1.1 The ten decisions that shape everything

| # | Topic | Decision |
|---|---|---|
| 1 | Stack | **C# on .NET 10 (LTS) + WPF**, Win32/COM interop via CsWin32, WinRT via the `net10.0-windows10.0.22621.0` TFM. Built with the `dotnet` CLI only. |
| 2 | Process model | One exe, two processes: `GlassDock.exe` (dock) and `GlassDock.exe --watchdog` (tiny, no WPF loaded) that restores the taskbar if the dock dies. |
| 3 | Native taskbar | **Keep Explorer and its taskbar alive, but hidden**: `ABM_SETSTATE(ABS_AUTOHIDE)` to release its work area + `SWP_HIDEWINDOW` + (if E2 passes) alpha-0 click-through as a flicker guard. State journal on disk, restore on exit / crash / next start. |
| 4 | Work area | Dock registers **its own appbar** (`ABM_NEW`/`ABM_QUERYPOS`/`ABM_SETPOS`) and reserves a full-width strip, even for the floating pill. No reservation in auto-hide mode. |
| 5 | Win+1..9 | **Own handling mapped to the dock's order.** By default the real combinations are taken over from Explorer and registered with `RegisterHotKey` (works in front of elevated windows, section 3.3). Any key that cannot be taken falls back to a `WH_KEYBOARD_LL` hook that *translates* Win+digit into private hotkeys (Win+F13..F24); if that hook is absent too (crash, elevated foreground window) Explorer's native Win+N still runs. |
| 6 | Other shortcuts | Everything else passes through untouched to Explorer, except **Win+T and Win+B**, which would otherwise put keyboard focus on the invisible taskbar; these are redirected to the dock. |
| 7 | Windows / tray data | Use **ManagedShell** (Apache-2.0, the library behind RetroBar and Cairo) for window tracking and tray-icon interception, wrapped behind our own interfaces. Taskbar hiding, appbar, hotkeys are our own code. |
| 8 | Window structure | Per dock: a WPF layered **content window** on top of a Win32 composition **glass window** (owner/owned pair). Flyouts are separate on-demand WPF windows. |
| 9 | Glass | Four tiers. v0.1: **G0 Clear** (true transparency) and **G1 Frosted** (DWM host-backdrop blur, free). v0.3: **G2 Liquid** (refraction shader over the wallpaper, zero idle cost, falls back to G1 while a window is behind the dock). v0.5 optional: **G3** live refraction via screen capture. |
| 10 | Pins | Dock keeps its **own pin store**; first run imports the Windows taskbar pins read-only. Nothing is written back to Explorer. |

### 1.2 Stack comparison

| Criterion | C# .NET 10 + WPF (**chosen**) | WinUI 3 / WinAppSDK unpackaged | C++ Win32 + D2D/DComp | Tauri / Electron |
|---|---|---|---|---|
| Toolchain with only `dotnet` CLI, portable | Yes: SDK zip, no VS | Works with `dotnet build`, but heavier (WinAppSDK runtime, XAML compiler quirks) | No: needs MSVC Build Tools (multi-GB, not portable) | Needs Rust+MSVC (Tauri) or ships Chromium |
| Shell interop (appbar, hooks, COM, WM_COPYDATA) | Good (CsWin32, `HwndSource` hook) | Same P/Invoke, but window model fights you (no layered window, activation, toolwindow quirks) | Best | Poor: everything through a native side-car |
| Per-pixel transparent, no-activate, topmost bar | Proven (`AllowsTransparency`, used by RetroBar/Cairo) | Awkward; lifted compositor cannot sample behind the window except via system backdrop controllers | Best | Possible, limited |
| Glass quality | Good: system compositor for G1, `ShaderEffect` (ps_3_0) for G2 | G1 only via `DesktopAcrylicController`; custom refraction hard | Best | CSS `backdrop-filter` cannot see behind the window |
| Idle RAM | ~90–140 MB (see 1.4) | ~120–200 MB | ~15–40 MB | 150–400 MB |
| Ecosystem to reuse | ManagedShell, RetroBar source as reference | little | ExplorerPatcher/Windhawk (injection-based, not wanted) | none |
| Time to MVP | Shortest | Medium | Longest | Medium, wrong fit |

Reasons for WPF despite its memory cost: the hard problems here are shell integration and many small interactive views (drag-reorder, flyouts, settings UI), not raw rendering. WPF gives layout, animation, drag-drop, data binding and a Fluent-themed settings window for free, and two mature open-source docks/taskbars in the same stack exist to consult. C++ would give the best footprint but cannot be built on this machine without a large non-portable install.

### 1.3 Toolchain and build

- Install the **.NET 10 SDK x64 zip** (binaries, not the installer) to `C:\GimojjiMan\Devs\dotnet\` (user convention). .NET 10 is the current LTS, supported to 2028-11-14; .NET 8 and 9 went out of support on 2026-11-10 **[VERIFIED]**. The machine's existing `C:\Program Files\dotnet` (runtime 6 only) is left alone. `global.json` pins the SDK; `build\build.ps1` calls `C:\GimojjiMan\Devs\dotnet\dotnet.exe` through a single variable.
- TFM: `net10.0-windows10.0.22621.0`, `<SupportedOSPlatformVersion>10.0.19041.0</SupportedOSPlatformVersion>`, `<UseWPF>true</UseWPF>`, `<UseWindowsForms>true</UseWindowsForms>` (ManagedShell uses `System.Windows.Forms.NativeWindow`; without the WinForms framework reference it fails at load — verify on first build).
- Publish: `dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=false` (`build/publish.ps1`). WPF cannot be trimmed or NativeAOT-compiled, so the single exe is ~190 MB (zip ~74 MB). Bundle compression is off on purpose: each process inflates every assembly it loads from a compressed bundle into private memory, which measured 269 MB for the dock and 147 MB for the watchdog, against 153 MB and 7 MB uncompressed. ReadyToRun is off too: it added memory and size with no measurable startup gain. A framework-dependent build (~5 MB + .NET 10 Desktop Runtime) is the dev default.
- NuGet dependencies — **each needs the user's approval before being added** (project rule):
  1. `Microsoft.Windows.CsWin32` (MIT, build-time source generator; removes hand-written P/Invoke signature bugs and supplies COM interface definitions/GUIDs).
  2. `ManagedShell` 0.0.374 (Apache-2.0; ships `net10.0-windows7.0` assets **[VERIFIED]**).
  3. `xunit` + `Microsoft.NET.Test.Sdk` (tests only).
  4. Later, only for optional G3: `Vortice.Windows` (MIT; D3D11/DXGI/DirectComposition bindings).
  If ManagedShell is refused, plan B is to port `TrayService`, `TasksService` and `ApplicationWindow` (about 3–4k lines) under their Apache-2.0 notice; the interfaces in section 5 make that swap local.

### 1.4 Resource budget (acceptance thresholds, measured in M1)

| Metric | Budget | Levers |
|---|---|---|
| Private working set, idle, one dock | ≤ 150 MB (expect 90–140) | `<ConcurrentGarbageCollection>false`, `System.GC.ConserveMemory=7`, `TieredPGO`, settings window created on demand and released, frozen `BitmapSource`s, icon cache with size cap |
| CPU, idle (no pointer over dock), 60 s average | < 0.5 % of one core | No polling timers except the coalesced refresh scheduler; `CompositionTarget.Rendering` subscribed only while animating |
| CPU during magnification animation, 1440p | < 8 % of one core | Layered window limited to pill width + headroom, not full screen width |
| Watchdog process | ≤ 25 MB, 0 % CPU | Blocks in `WaitForSingleObject`; never touches WPF types |

---

## 2. Taskbar hiding and restore

### 2.1 Options considered

| Option | Releases work area | Explorer hotkeys keep working | Invisible on hover | Verdict |
|---|---|---|---|---|
| Kill / replace Explorer shell | n/a | **No** (Win+N, Win+X, Start, tray all die) | Yes | Rejected: violates requirement 2 |
| Inject into Explorer (Windhawk/ExplorerPatcher style) | Yes | Yes | Yes | Rejected: "does not modify Windows", fragile per build, AV-hostile |
| `SW_HIDE` only | **No** — taskbar keeps its 48 px reservation | Yes (expected) | Yes while hidden | Insufficient alone |
| Auto-hide only (`ABM_SETSTATE`) | Yes | Yes | **No** — pops up on edge hover, Win key, flashing apps | Insufficient alone |
| Alpha 0 only (`WS_EX_LAYERED`) | No | Yes | Yes if it applies to the XAML taskbar **[E2]** | Insufficient alone |
| **Auto-hide + `SW_HIDE` + alpha-0/click-through guard** | Yes | Yes (expected) **[E3]** | Yes | **Chosen** |

This is the same family of technique ManagedShell uses (`ABM_SETSTATE` auto-hide, then `SetWindowPos(... SWP_HIDEWINDOW)` on the tray window and every `Shell_SecondaryTrayWnd`, plus a 100 ms timer that re-hides when Explorer re-shows it) **[VERIFIED: ManagedShell `ExplorerHelper.cs`]**. The reference product also documents "sets the Windows taskbar to auto-hide (default) or hides it completely" **[VERIFIED: vendor site]**. We improve on the 100 ms poll because the user requirement is "no flicker/peek".

Known field issue: RetroBar users have reported that some Windows 11 updates interfere with hiding, with the workaround "enable auto-hide in Settings before starting". That suggests `ABM_SETSTATE` from another process is not 100 % reliable on every build → **[EXPERIMENT E1]** and a verification step in code (2.3 step 4).

### 2.2 Module: `TaskbarHider` (`GlassDock.Shell/Taskbar`)

```csharp
public interface ITaskbarHider : IDisposable
{
    TaskbarHideStatus Status { get; }                  // per-window: Hidden / Reappeared / Missing
    void Hide(IReadOnlyCollection<MonitorId> docks);   // idempotent
    void Restore();                                    // idempotent, safe from any thread
    event EventHandler<ExplorerRestartedEventArgs> ExplorerRestarted;
}
```

Supporting types: `TaskbarStateJournal` (disk), `ExplorerWatcher` (process handle + `TaskbarCreated`), `WorkAreaVerifier`.

**Finding the real windows.** Because ManagedShell creates its own top-level window of class `Shell_TrayWnd` (section 7), never use a bare `FindWindow("Shell_TrayWnd")`. Enumerate with `FindWindowEx(NULL, prev, "Shell_TrayWnd", NULL)` and `"Shell_SecondaryTrayWnd"`, keep only windows whose `GetWindowThreadProcessId` process image is `explorer.exe` and is not our own PID. Map each to a monitor with `MonitorFromWindow`.

### 2.3 Hide sequence

1. **Journal first.** Write `%APPDATA%\GlassDock\state\taskbar-journal.json` (temp file + `File.Replace`) before changing anything:
   ```json
   { "version": 1, "ownerPid": 12345, "ownerStartUtc": "2026-11-16T09:00:00Z",
     "originalAppBarState": 0,
     "windows": [ { "class": "Shell_TrayWnd", "monitor": "\\\\.\\DISPLAY1", "originalExStyle": 136 } ] }
   ```
   If a journal already exists and its owner is alive, keep its `original*` values (never overwrite originals with already-modified state).
2. `originalState = SHAppBarMessage(ABM_GETSTATE)`; then `SHAppBarMessage(ABM_SETSTATE)` with `lParam = ABS_AUTOHIDE` (preserve `ABS_ALWAYSONTOP` bit as read). `ABM_SETSTATE` is global: it puts **every** native taskbar into auto-hide.
3. For each native taskbar on a monitor that has a dock:
   - `SetWindowPos(hwnd, NULL, 0,0,0,0, SWP_HIDEWINDOW | SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE)`.
   - If E2 passed: `SetWindowLongPtr(GWL_EXSTYLE, ex | WS_EX_LAYERED | WS_EX_TRANSPARENT)` then `SetLayeredWindowAttributes(hwnd, 0, 0, LWA_ALPHA)`. Purpose: when Explorer re-shows the window before our re-hide lands, nothing is drawn and no clicks are eaten in the bottom 48 px.
   Native taskbars on monitors **without** a dock are left as ordinary auto-hide taskbars (setting `taskbar.hideOnMonitorsWithoutDock`, default `false`), so those monitors still have a taskbar.
4. **Verify** after `AppConstants.Taskbar.VerifyDelayMs` (500): `ABM_GETSTATE` has the auto-hide bit and `GetMonitorInfo().rcWork` no longer excludes the native strip. If not, log a warning, retry once, then surface a one-time notice with a button that opens `ms-settings:taskbar` (the RetroBar workaround). Do not silently continue with a doubly-reserved work area.
5. **Keep it hidden, event-driven:**
   - `SetWinEventHook(EVENT_OBJECT_SHOW, EVENT_OBJECT_SHOW, NULL, cb, explorerPid, 0, WINEVENT_OUTOFCONTEXT)`; in the callback, if `hwnd` is a tracked taskbar window → repeat step 3 immediately.
   - Safety poll every `AppConstants.Taskbar.RehidePollMs` (250) checking `IsWindowVisible` — catches anything the event misses. Log every re-hide with a counter (diagnostics page shows it; a high count means Explorer is fighting us).
   - Re-run on `WM_DISPLAYCHANGE`, `WM_SETTINGCHANGE`, session unlock, resume from sleep.

### 2.4 Restore sequence (`TaskbarRestorer.Restore(journal)`, static, no WPF, used by dock, watchdog and CLI)

1. For each real taskbar window: remove our ex-style bits (restore `originalExStyle` exactly), `SetWindowPos(... SWP_SHOWWINDOW ...)`.
2. `ABM_SETSTATE` back to `originalAppBarState`.
3. Delete the journal. Log the outcome.

Called from: normal exit, `Application.DispatcherUnhandledException` (during startup, or after more than `UiFaults.MaxSurvived` (3) UI-thread exceptions within `UiFaults.WindowSeconds` (60); fewer are logged and survived by `DockMain.Survive`, which closes the open flyout, because restarting for each one brought the Windows taskbar back over a game), `AppDomain.UnhandledException`, `WM_ENDSESSION` (setting `taskbar.restoreOnSessionEnd`, default `true`, so a machine that boots without GlassDock has a normal taskbar; the cost is a brief native taskbar at logon until GlassDock starts), the watchdog, and `GlassDock.exe --restore-taskbar` (also the uninstaller hook).

### 2.5 Crash safety

- **Stale journal on start.** `Program.Main` first calls `TaskbarGuard.RecoverIfStale()`: journal exists and (`ownerPid` not running or its start time differs) → `Restore`, then continue normal start. This covers power loss and the case where both processes were killed. Note that Explorer persists the auto-hide flag (StuckRects3), so after a hard power cut the taskbar stays auto-hide until GlassDock (or `--restore-taskbar`) runs once — document this in the README.
- **Watchdog.** Main starts `GlassDock.exe --watchdog --pid <pid>` and holds its process handle; the watchdog holds main's.
  ```csharp
  // Program.cs — must branch before any WPF type is referenced, so the watchdog never loads PresentationFramework
  [STAThread]
  static int Main(string[] args) =>
      args.Contains("--watchdog")        ? WatchdogMain.Run(args) :
      args.Contains("--restore-taskbar") ? TaskbarRestorer.RunCli()  :
                                           DockMain.Run(args);
  ```
  `WatchdogMain`: `OpenProcess(SYNCHRONIZE | PROCESS_QUERY_LIMITED_INFORMATION)` → `WaitForSingleObject(INFINITE)` → `GetExitCodeProcess`. If the journal still exists (clean exits delete it) → `Restore`. If the exit code is not `ExitCodes.UserQuit` → relaunch main, unless `CrashLoop.MaxRestarts` (3) within `CrashLoop.WindowSeconds` (60) is exceeded, in which case leave the taskbar restored and show a message box with the log path. Main restarts a dead watchdog the same way.
- **Explorer restart.** A hidden top-level window (`ShellHostWindow`; message-only windows do not receive broadcasts) handles `RegisterWindowMessage("TaskbarCreated")`. Pitfall: ManagedShell's `TrayService` itself broadcasts `TaskbarCreated` on start and dispose **[VERIFIED]**, so treat the message as "Explorer restarted" only if the real `Shell_TrayWnd` HWND differs from the stored one. On a real restart, after `AppConstants.Explorer.SettleDelayMs` (800): re-discover windows, re-hide, re-register appbars, re-register shell hook, re-raise the fake tray. `ExplorerWatcher` also waits on Explorer's process handle and, while Explorer is down, watches `EVENT_OBJECT_CREATE`/`SHOW` to hide the new taskbar as early as possible. A brief flash of the native taskbar during an Explorer restart cannot be fully prevented from outside the process; state this as a known limitation.

### 2.6 Work area (decision: own appbar)

- With auto-hide set, the native taskbar reserves nothing; the monitor's work area becomes the full monitor. `AppBarRegistration` (one per dock) then reserves a **full-width strip** at the dock edge: `thickness = edgeGap + dockThickness + reserveGap` (all in settings, physical pixels after DPI scaling). A floating pill still reserves the full-width strip because a work area is a rectangle; this keeps maximized windows from sliding under the pill.
- Setting `dock.reserveSpace` (`true` default). `false`, or `autoHide.enabled = true`, means no appbar reservation: the dock overlays windows.
- Setting `dock.fillMaximizedBand` (`true` default, only with the strip reserved): a maximized window stops at the strip and leaves the wallpaper showing around the pill. While `MaximizedWindowMonitor` sees a visible, uncloaked `IsZoomed` window on the dock's monitor (WinEvent driven: minimize, show/hide/destroy, location change, cloak, throttled at `Glass.OcclusionCheckHz`), a second `GlassBackdropWindow` fills the whole strip with the style's tint at alpha ≥ `Glass.BandMinTintAlpha`, over the host backdrop (or alone for styles without one). It is re-stacked under the pill's glass and the dock window, and the pill switches from the lens to frosted glass, since sharp wallpaper inside a frosted band reads as a hole.
- The appbar HWND is the dock's content window; the reserved rectangle is independent of that window's real bounds (which include transparent headroom for magnification).

```csharp
// AppBarRegistration.Apply — all rects in physical pixels (process is PerMonitorV2)
var abd = new APPBARDATA { cbSize = (uint)sizeof(APPBARDATA), hWnd = hwnd, uCallbackMessage = _callbackMsg };
if (!_registered) { SHAppBarMessage(ABM_NEW, ref abd); _registered = true; }
abd.uEdge = edge;                 // ABE_BOTTOM = 3, ABE_TOP = 1
abd.rc    = FullWidthStrip(monitor.rcMonitor, edge, thickness);
SHAppBarMessage(ABM_QUERYPOS, ref abd);          // shell may move the strip past other appbars
ReapplyThickness(ref abd.rc, edge, thickness);   // QUERYPOS only adjusts the far side
SHAppBarMessage(ABM_SETPOS, ref abd);
_reserved = abd.rc;                               // dock layout anchors to this, not to rcMonitor
```

- Callback handling: `ABN_POSCHANGED` → re-run `Apply`; `ABN_FULLSCREENAPP` → feed `FullscreenMonitor`; `WM_ACTIVATE` → `ABM_ACTIVATE`; `WM_WINDOWPOSCHANGED` → `ABM_WINDOWPOSCHANGED`. `ABM_REMOVE` on exit, on dock removal, and before re-registering after an Explorer restart (the new Explorer has an empty appbar list).
- Our `SHAppBarMessage` calls are sent by shell32 to whatever `FindWindow("Shell_TrayWnd")` returns, i.e. to ManagedShell's fake tray, which forwards unhandled `WM_COPYDATA` appbar messages to the real one **[VERIFIED]**. Start order therefore does not matter, but log the result of every appbar call.

---

## 3. Shortcut preservation, including Win+1..9

### 3.1 What stays native (untouched, zero code)

Because Explorer and its hidden taskbar stay alive, these keep working with no involvement from us: Win (Start), Win+X, Win+A, Win+N, Win+D, Win+M, Win+E, Win+R, Win+L, Win+I, Win+S/Q, Win+V, Win+., Win+Tab, Alt+Tab, Win+arrows, Win+Shift+S, Win+Ctrl+D/arrows, Win+P, Win+K, Win+W, Win+Z, Win+G, Win+PrtScn, Win+Home, Win+comma. The hook (3.3) returns `CallNextHookEx` for all of them. **[EXPERIMENT E3]** confirms each one with the taskbar hidden and the fake tray window present (RetroBar on Windows 11 is the existence proof for Start/Win+X/flyouts).

Cosmetic note: Start, Search and the Win+X menu position themselves relative to the (now invisible) native taskbar at the bottom edge. With a bottom dock they overlap or sit just above it; with a top dock they still open at the bottom. This matches the reference product ("does not replace Start").

### 3.2 Win+number: analysis of the three options

**(a) Leave it to Explorer's hidden taskbar.** Explorer registers Win+digit as hotkeys on its tray thread; `WM_HOTKEY` is delivered regardless of window visibility, so launch/switch should keep working when `Shell_TrayWnd` is hidden (expected, **[E3]**). Problems: the Nth item is the Nth button of *Explorer's* taskbar, not of the dock; for apps with several windows Explorer shows its thumbnail strip anchored to an invisible bar (may appear detached or not at all); Win+Alt+N opens a jump list at the hidden bar. Good enough as a fallback, not as the product behaviour.

**(b) Handle it ourselves.** Two sub-variants exist in the wild:
- **b1 — low-level keyboard hook.** `RegisterHotKey(MOD_WIN, '1')` fails because Explorer owns it; a `WH_KEYBOARD_LL` hook sees the key first and can swallow it (this is how AutoHotkey overrides Win hotkeys). Nothing in Explorer is modified; when our process dies the native behaviour returns by itself.
- **b2 — make Explorer give the hotkeys up.** RetroBar (since v1.20) sends the undocumented `WM_USER + 231` ("unregister hotkey") to the real `Shell_TrayWnd` with hotkey IDs starting at 500, found by scanning `explorer.exe` on disk for the hotkey table, then calls `RegisterHotKey` normally **[VERIFIED: RetroBar `HotkeyManager.cs`, `TrayHotkey.cs`]**. Advantages: real `WM_HOTKEY`, works even when an elevated window has focus. Disadvantages: undocumented message, binary-scanning heuristic, and Explorer's hotkeys are **not restored** until Explorer restarts (RetroBar has a TODO for exactly this) — that conflicts with "restored if the dock quits or crashes".

**(c) Sync dock order to the taskbar pin order so native Win+N matches.** Reading pins is feasible (section 6.1). Writing is not: the pin list is an undocumented binary blob (`Taskband\Favorites`), the undocumented `IPinnedList3` pin API reports success without pinning on 24H2+ (per Firefox source comments **[VERIFIED via search]**), and dock-only concepts (app groups, stacks, widgets between apps) have no taskbar equivalent. Also, the order of *running unpinned* apps is Explorer's own. Rejected as the mechanism; kept only as a one-time, read-only import.

### 3.3 Decision: b2 by default, b1 translation as the per-key fallback

`shortcuts.winNumberMode`: `"dock"` (default) | `"native"`. `shortcuts.takeOverExplorerHotkeys` (default `true`) chooses b2 for every intercepted key (Win+1..0 with Shift/Ctrl/Alt/Ctrl+Shift, Win+T, Win+Shift+T, Win+B).

The original decision was b1 only, with b2 as an experimental opt-in. It changed on 2026-11-16: games run elevated (MabinogiMobile, `elevated=yes` in every log line), so the hook is never called in them and Win+1..0 did nothing. Real hotkeys are delivered whatever window is in front, they need no admin helper process, and they let the hook pause entirely in fullscreen. A `RegisterHotKey` is also the least objectionable input API for anti-cheat software (Discord, OBS and Steam use it). A signed `uiAccess` build would also get past UIPI, but it keeps a hook watching every key in the game, and it needs a trusted certificate plus an install under Program Files. Accepted cost of b2: after GlassDock quits, Explorer's own Win+N returns only when Explorer restarts or the user signs in again; the settings page says so.

**Mechanism for b2** (`ExplorerHotkeys`, `HotkeyService.RegisterOwn`): try `RegisterHotKey` for each key first. Explorer already gave up the keys during an earlier run, and keeps them released until it restarts. For each key that fails, find Explorer's `Shell_TrayWnd` (never ManagedShell's own window of that class) and read the image of its process from disk. Then take the longest run of 8-byte records `{vk,0,0,0,mods,0,0,0}` with `MOD_WIN` set; this run is accepted only if it contains Win+1. Send `WM_USER + 231` (`SendMessageTimeout`, abort if hung) with id `500 + index` for each matching record, then retry. Verified on build 26200: an 80-entry table at file offset 0x227DE0. Win+1..0 are ids 517-526, followed by the Ctrl (0x0A), Shift (0x0C), Alt (0x09) and Ctrl+Shift (0x0E) runs; Win+T and Win+Shift+T are 510-511, and Win+B is 514. Win+B still fails after release (another component holds it), so it stays on the hook. Keys that still fail go to the b1 translation below; the log line `Shortcuts: N real hotkeys, M translated by the hook` gives the split.

**Mechanism for `"dock"`** — module `GlassDock.Shell/Input`: `KeyboardHook`, `HotkeyTranslator`, `HotkeyService`, `InputInjector`, `ForegroundActivator`.

**Mechanism for b1** (keys b2 could not take, or `takeOverExplorerHotkeys` off). The hook does not act on the shortcut itself. It **swallows the digit and injects a private key (F13..F24) while the user is still holding Win**; the system then sees Win+F13, which we own via `RegisterHotKey`. This solves three problems at once:

1. Explorer never sees Win+digit (hooks run before hotkey processing).
2. The Start menu does not open when Win is released, because the system saw another key while Win was down (same principle as AutoHotkey's "menu mask key" **[VERIFIED: AHK docs]**).
3. Our UI thread receives a genuine `WM_HOTKEY`, which carries foreground-activation rights, so `SetForegroundWindow` on the target app is allowed. A bare hook callback has no such right.

Key map (12 private keys, exactly F13..F24):

| Physical | Injected | Registered combos | Dock command |
|---|---|---|---|
| Win+1..9, Win+0 | F13..F22 | `MOD_WIN` alone; +`SHIFT`; +`CONTROL`; +`ALT`; +`CONTROL\|SHIFT` (50 hotkeys) | slot N: activate/launch/cycle; Shift = new instance; Ctrl = last active window; Alt = jump list/context menu; Ctrl+Shift = new instance as admin |
| Win+T, Win+Shift+T | F23 | `MOD_WIN`; +`SHIFT` | keyboard focus to apps, next/previous |
| Win+B | F24 | `MOD_WIN` | focus tray tile, open tray flyout |

Startup applies the shortcuts at `ApplicationIdle`, after the dock's first frame (with the startup entry and the watchdog): reading Explorer's hotkey table and checking the keys took 0.1 to over 1 s. `RegisterVerified` sends each private key once and moves keys whose `WM_HOTKEY` never arrives (another program's hook took it). It skips that check while an elevated window is in front, because UIPI silently drops our injected keys there, so every key looked silent, startup waited 1 s and good keys moved.

All registered with `MOD_NOREPEAT`. The physically held modifiers pass through unchanged, so the injected key naturally forms the right combination. If a `RegisterHotKey` fails (another tool owns Win+F15, say), that slot falls back to "direct dispatch": the hook posts the command to the UI thread and injects the mask key `0xE8` instead; activation then relies on the `ForegroundActivator` chain below. Log which path each slot uses.

```csharp
// KeyboardHook.cs — runs on its own thread with a message loop; no allocation, no logging, no locks in here
private nint HookProc(int nCode, nuint wParam, nint lParam)
{
    if (nCode < 0) return CallNextHookEx(0, nCode, wParam, lParam);
    ref readonly KBDLLHOOKSTRUCT k = ref Unsafe.AsRef<KBDLLHOOKSTRUCT>((void*)lParam);
    if (k.dwExtraInfo == InputInjector.Tag) return CallNextHookEx(0, nCode, wParam, lParam); // our own synthetic keys

    bool down = wParam is WM_KEYDOWN or WM_SYSKEYDOWN;            // Win+Alt+digit arrives as SYSKEYDOWN
    if (k.vkCode is VK_LWIN or VK_RWIN)
    {
        _winDown = down;
        PostThreadMessage(_threadId, down ? MsgWinDown : MsgWinUp, 0, 0);   // auto-hide reveal on Win
        return CallNextHookEx(0, nCode, wParam, lParam);                     // never swallow Win itself
    }
    if (!_winDown) return CallNextHookEx(0, nCode, wParam, lParam);

    int slot = _map.SlotFor(k.vkCode);                           // -1 = not ours
    if (slot < 0) return CallNextHookEx(0, nCode, wParam, lParam);

    if (down)
    {
        if (!_held[slot]) { _held[slot] = true; PostThreadMessage(_threadId, MsgTranslate, (nuint)slot, 0); }
        return 1;                                                // swallow, including auto-repeat
    }
    if (_held[slot]) { _held[slot] = false; return 1; }          // swallow the matching key-up
    return CallNextHookEx(0, nCode, wParam, lParam);
}
// Hook thread loop: on MsgTranslate → SendInput(F13+slot down, up) with dwExtraInfo = Tag.
// Injection happens outside the callback so the callback returns in microseconds.
```

Robustness rules for the hook:

- Dedicated thread, `ThreadPriority.Highest`, own `GetMessage` loop. Windows silently drops a low-level hook whose callback exceeds `LowLevelHooksTimeout` (~300 ms) repeatedly; there is no notification. Mitigation: re-install the hook (`SetWindowsHookEx` new, then `UnhookWindowsHookEx` old) every `AppConstants.Input.HookRefreshMinutes` (5) and on session unlock / resume. Do not use a synthetic "ping" key for health checks: injected input resets the system idle timer.
- `_winDown` can desynchronise (Win+L: the key-up is never seen). Reset it on `WTS_SESSION_UNLOCK` and cross-check with `GetAsyncKeyState(VK_LWIN/VK_RWIN)` whenever a mapped key arrives.
- UIPI: a medium-integrity hook is **not called while an elevated window has focus**. This is why b2 is the default; a key left on the hook still falls back to Explorer's native behaviour there.
- The hook keeps running even when it translates nothing, because it also reports the Win key itself (auto-hide reveal, preview closing on Win release). While a fullscreen app is in front, the hook is unhooked whenever Win+1..0 are all real hotkeys (`HotkeyService.SlotsReal`): it has nothing useful to do there, and it is one less thing for anti-cheat to see. Win+T and Win+B act on the dock, which is hidden in fullscreen anyway. `shortcuts.suspendHookInFullscreen` (default `false`, schema v2) forces the pause even when some digits are still translated; those digits then stop working in fullscreen games. Schema v1 files are migrated to `false`.
- Dev flag `--no-hook`: a breakpoint anywhere in the process freezes the hook thread and makes the whole system's keyboard lag.

**Slot semantics** (`DockCommands.ActivateSlot(n, variant)`): slots count every button of the Apps tile in visual order (pinned apps, running apps, app groups, stacks), like Explorer counts every taskbar button. Plain press: not running → launch; one window → activate, or minimize if it is already foreground; several windows → activate the next one and show the preview flyout while Win stays held (cycle on repeated presses, close flyout on `MsgWinUp`).

**`ForegroundActivator.Activate(hwnd)`** — used for every activation (click, hotkey, preview), logs which step succeeded. It returns at once: the steps run on a `DispatcherTimer` (`Input.ActivatePollMs`, 10 ms), and each gets `Input.ActivateStepMs` (200) to show in `GetForegroundWindow` before the next, the last `Input.ActivateGiveUpMs` (1000). Waiting in place froze the dock for up to 0.7 s, and the elevated fullscreen game often took over 50 ms to come forward, so the old 50 ms confirm logged failures for activations that then worked. A step whose call fails moves on at the next tick. A second request for the same window while one runs is ignored (repeat clicks); one for another window replaces it.
1. `IsIconic` → `ShowWindowAsync(SW_RESTORE)`.
2. `SetForegroundWindow`.
3. `AttachThreadInput(current, foregroundThread, true)` → `BringWindowToTop` + `SetForegroundWindow` → detach.
4. Injected Alt down/up (tagged) then `SetForegroundWindow` (AutoHotkey's last-resort technique).
5. `SwitchToThisWindow(hwnd, TRUE)`.

Mouse clicks on the dock need only step 2 (our window received the last input event). **[EXPERIMENT E4]** measures the translation path: 20/20 activations of another app from the `WM_HOTKEY` handler, Start menu never opens on Win release, behaviour with an elevated foreground window.

**Win+T / Win+B.** `FocusApps` calls `SetForegroundWindow(dockHwnd)` (programmatic activation works on a `WS_EX_NOACTIVATE` window), sets a focus-visible state, arrow keys move, Enter/Space activates, Shift+F10 or Menu key opens the context menu, Esc returns focus to the previously active window (remember it before taking focus).

**Tiles that trigger native UI** use tagged injected input, which the hook ignores and Explorer handles: Start tile = tap `VK_LWIN` (fallback `PostMessage(realTray, WM_SYSCOMMAND, SC_TASKLIST, 0)`); right-click Start = Win+X; Notifications = Win+N; Quick settings = Win+A; Show desktop = Win+D.

---

## 4. Glass rendering

### 4.1 Facts that constrain the design

- **DWM system backdrops** (`DWMWA_SYSTEMBACKDROP_TYPE`: Mica / acrylic "transient") — live, free, but fixed recipe, whole window, 8 px corner radius at most (`DWMWCP_ROUND`). Good for flyouts and the settings window, not for an arbitrary-radius pill. Whether acrylic stays live on a never-active window is unverified **[E5]**.
- **`SetWindowCompositionAttribute`** (`WCA_ACCENT_POLICY` = 19; `ACCENT_ENABLE_TRANSPARENTGRADIENT` 2, `BLURBEHIND` 3, `ACRYLICBLURBEHIND` 4) — undocumented but stable since Windows 10 and used by TranslucentTB. Whole window; shape only via `SetWindowRgn` (aliased corners). Works on Windows 10.
- **`Windows.UI.Composition` host backdrop brush** in a Win32 window (`DesktopWindowTarget`): live, GPU-side in DWM, can be clipped with an antialiased rounded-rectangle geometry. Requires `DWMWA_USE_HOSTBACKDROPBRUSH` (17) on Windows 11 build 22000+ **[VERIFIED: MS docs]**. Important: the host backdrop arrives **already blurred by the shell** with a fixed radius — the WinUI acrylic source uses it directly and only blurs the in-app backdrop itself **[VERIFIED: microsoft-ui-xaml `AcrylicBrush.cpp`]**. So "a little blur" is not available from DWM: the choices are zero blur (true transparency) or DWM's fixed frosted blur.
- `Windows.UI.Composition` supports no displacement-map effect and no custom pixel shaders, so **real refraction cannot be done inside DWM**. Refraction needs pixels in our own shader: either the wallpaper image, or a screen capture.
- **Screen capture for a live backdrop** (`Windows.Graphics.Capture` on the monitor, or DXGI duplication) would capture the dock itself. `SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE)` removes our windows from all captures (Windows 10 2004+) **[VERIFIED: MS docs]** — which also removes the dock from the user's screenshots, recordings and screen sharing while it is set. One to two frames of latency, and continuous GPU work while content behind the dock changes.
- With the work area reserved (decision 4), **what is behind the dock is almost always just the wallpaper**: maximized windows and desktop icons stay out of the strip. Other content appears only when a window is dragged over the strip, in auto-hide/overlay mode, or under a fullscreen app (where the dock is hidden anyway). The reference product's own description fits this: "bends your wallpaper live", with a low-resource mode when nothing is behind it **[VERIFIED: vendor site / review]**.

### 4.2 Tiers

| Tier | What the user sees | Source of backdrop | Cost | Ships |
|---|---|---|---|---|
| **S** Solid | Black / White | none | zero | v0.1 |
| **G0** Clear | Fully see-through pill with tint, rim light, shadow | true per-pixel transparency | zero | v0.1 |
| **G1** Frosted | Live frosted blur of whatever is behind, tint, rim, noise | DWM host backdrop brush (fallback: accent policy) | effectively zero CPU; small DWM GPU cost only when content behind changes | v0.1 |
| **G2** Liquid | Refraction at the bezel, chromatic fringe, specular rim, light blur | wallpaper texture, WPF `ShaderEffect` (ps_3_0) | zero at rest (static inputs, cached); one-off re-render on wallpaper/size change | v0.3 |
| **G3** Liquid live (optional) | Same, but windows behind are refracted too | `Windows.Graphics.Capture` + D3D11 shader, dock excluded from capture only while active | ~1–3 % CPU, moderate GPU while content behind changes | v0.5, experimental |

**Recommendation:** default style "Liquid Glass" = **G2 with automatic fallback to G1** whenever a window overlaps the dock or the dock overlays content (auto-hide, `reserveSpace=false`). That gives real refraction of the wallpaper at no running cost and never shows wrong pixels: when something other than wallpaper is behind the dock, the user gets a correct live frosted blur instead of a refracted wallpaper. G3 is offered later for users who accept its trade-offs.

Style presets (data-driven, `Themes/Presets.json`): Black (S), White (S), Clear (G0), iOS Glass (G1, light tint 18 %, rim 0.6), Midnight (G1, navy tint 55 %), Liquid Glass (G2→G1). Each preset = `{ tier, tintColor, tintOpacity, rimOpacity, specularOpacity, shadow, cornerRadiusMode, lens{...} }`; "custom accent colour" overrides `tintColor`.

### 4.3 Structure: what draws what

- **Content window (WPF, layered)** always draws: tint layer, rim/specular gradients, inner shadow, icons and widgets, and in G2 the lens element. Because these are in WPF, all tiers share the same chrome and the same antialiased edge.
- **Glass window (Win32, behind)** draws only the live frosted backdrop for G1; hidden (`SW_HIDE`) for S, G0 and for G2 at rest.
- Interface in `GlassDock.Core`:
  ```csharp
  public interface IGlassBackdrop : IDisposable
  {
      bool IsAvailable { get; }                         // false → controller picks the next fallback
      void Attach(nint ownerOfContentWindow);
      void SetShape(RectInt32 pillPx, float cornerRadiusPx);   // physical pixels
      void SetVisible(bool visible, TimeSpan fade);
  }
  ```
  Implementations in `GlassDock.Shell/Glass`: `HostBackdropGlass` (preferred), `AccentPolicyGlass` (fallback and Windows 10), `NullGlass`. `GlassController` in the app selects tier and fallback from: preset, `IsBackdropLive` (4.5), power state (4.6), render capability.

**`HostBackdropGlass`** — window: `WS_POPUP`, `WS_EX_NOREDIRECTIONBITMAP | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOPMOST`; no effect graph is needed, so no Win2D / `IGraphicsEffect` interop:

```csharp
[ComImport, Guid("29E691FA-4567-4DCA-B319-D0F207EB6807"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface ICompositorDesktopInterop { void CreateDesktopWindowTarget(nint hwnd, bool isTopmost, out nint target); }

// UI thread needs a DispatcherQueue before `new Compositor()`:
// CreateDispatcherQueueController(DQTYPE_THREAD_CURRENT, DQTAT_COM_STA) from CoreMessaging.dll — coexists with the WPF Dispatcher.
int on = 1;
DwmSetWindowAttribute(hwnd, DWMWA_USE_HOSTBACKDROPBRUSH /*17*/, &on, sizeof(int));
_compositor = new Compositor();
_compositor.As<ICompositorDesktopInterop>().CreateDesktopWindowTarget(hwnd, false, out nint raw);
_target = DesktopWindowTarget.FromAbi(raw);
var sprite = _compositor.CreateSpriteVisual();
sprite.Brush = _compositor.CreateHostBackdropBrush();
var geo = _compositor.CreateRoundedRectangleGeometry();          // Size/CornerRadius set in SetShape
sprite.Clip = _compositor.CreateGeometricClip(geo);              // antialiased pill edge
_target.Root = sprite;
```

**[EXPERIMENT E5]**: the brush is live on a window that is never activated; corner clip is antialiased; what it renders when "Transparency effects" is off or Energy Saver is on (expected: opaque fallback → switch to S). A Microsoft Q&A thread reports the host backdrop brush rendering black in plain Win32 windows on Windows 10 until an accent policy was applied; the Windows 11 attribute above is the documented fix, but treat this as unproven until E5 passes. If E5 fails, `AccentPolicyGlass` is the v0.1 implementation: `ACCENT_ENABLE_ACRYLICBLURBEHIND` on the same glass window plus `SetWindowRgn(CreateRoundRectRgn(...))`; the 1 px aliasing is covered by the WPF rim drawn above it.

### 4.4 G2: the lens shader

- Element: `LiquidLens` (a `Rectangle` the size of the pill) with a `ShaderEffect`. Samplers: `Input` (wallpaper crop, sharp) and `Blurred` (same crop, pre-blurred once on the CPU or via a one-off `BlurEffect` render to `RenderTargetBitmap`). Both crops cover the pill rect expanded by `lens.maxRefractionPx` so displaced lookups stay inside the texture. Wrap the element in `BitmapCache`; WPF re-runs the shader only when an input changes.
- Shader source `Shaders/LiquidLens.hlsl` is compiled at runtime with `D3DCompile` from `d3dcompiler_47.dll` (ships with Windows; no `fxc` on the machine) to `ps_3_0`, cached under `%LOCALAPPDATA%\GlassDock\cache\shaders\<sha256>.ps`. `RenderCapability.IsPixelShaderVersionSupported(3, 0)` false (RDP, software rendering) → G1/G0.

```hlsl
// LiquidLens.hlsl (ps_3_0). c0..c5 bound through ShaderEffect dependency properties — no literals for tunables.
sampler2D Input   : register(s0);   // sharp wallpaper crop
sampler2D Blurred : register(s1);   // pre-blurred wallpaper crop
float2 PillSize   : register(c0);   // px
float  Radius     : register(c1);   // px
float  Bezel      : register(c2);   // px, width of the refracting rim
float  Refraction : register(c3);   // px, max displacement at the edge
float  Frost      : register(c4);   // 0 sharp .. 1 blurred
float2 TexMargin  : register(c5);   // px of extra texture around the pill

float sdRoundRect(float2 p, float2 halfSize, float r) {
    float2 q = abs(p) - halfSize + r;
    return length(max(q, 0)) + min(max(q.x, q.y), 0) - r;
}
float4 main(float2 uv : TEXCOORD) : COLOR {
    float2 p  = (uv - 0.5) * PillSize;                      // px from centre
    float  d  = sdRoundRect(p, PillSize * 0.5, Radius);     // <0 inside
    float  e  = 0.75;                                       // finite-difference step, px
    float2 n  = normalize(float2(
        sdRoundRect(p + float2(e, 0), PillSize * 0.5, Radius) - sdRoundRect(p - float2(e, 0), PillSize * 0.5, Radius),
        sdRoundRect(p + float2(0, e), PillSize * 0.5, Radius) - sdRoundRect(p - float2(0, e), PillSize * 0.5, Radius)) + 1e-5);
    float  t  = saturate(1 + d / Bezel);                    // 0 inner edge of bezel .. 1 at the outline
    float  k  = 1 - sqrt(saturate(1 - t * t));              // convex lens profile
    float2 texSize = PillSize + 2 * TexMargin;
    float2 baseUv  = (p + texSize * 0.5) / texSize;
    float2 off     = -n * Refraction * k / texSize;         // pull samples inward: magnifying rim
    float3 sharp   = float3(tex2D(Input, baseUv + off * 1.06).r, tex2D(Input, baseUv + off).g, tex2D(Input, baseUv + off * 0.94).b);
    float3 col     = lerp(sharp, tex2D(Blurred, baseUv + off).rgb, Frost);
    float  a       = saturate(0.5 - d);                     // 1 px antialiased edge
    return float4(col * a, a);                              // WPF expects premultiplied alpha
}
```
Rim light, specular streak and tint are WPF gradient layers above the lens (cheaper to tune, shared with G0/G1). **[EXPERIMENT E11]** checks ps_3_0 `ShaderEffect` output inside an `AllowsTransparency` window and the runtime compile path.

- `WallpaperSource`: `IDesktopWallpaper` (`GetWallpaper(monitorId)`, `GetPosition`, `GetBackgroundColor`); fall back to `%APPDATA%\Microsoft\Windows\Themes\TranscodedWallpaper`. Compute the fit-mode mapping (Fill/Fit/Stretch/Center/Tile/Span) from wallpaper pixels to monitor pixels, decode only the needed crop at display resolution. Refresh on `WM_SETTINGCHANGE` (`SPI_SETDESKWALLPAPER`), display change, and slideshow ticks. Animated wallpaper hosts (Wallpaper Engine, Lively) make the file stale: setting `glass.assumeStaticWallpaper` (default `true`); when `false`, Liquid uses G1 permanently (or G3).

### 4.5 Knowing what is behind the dock: `BackdropOcclusionMonitor`

Input: the tracked top-level windows (section 6.2) plus `EVENT_OBJECT_LOCATIONCHANGE`, show/hide, minimize, cloak events, throttled to `AppConstants.Glass.OcclusionCheckHz` (10). A window counts if it is visible, not minimized, not cloaked, and its `DWMWA_EXTENDED_FRAME_BOUNDS` intersects the pill rect. Output `IsBackdropLive`: true immediately on first overlap (cross-fade lens → G1 over 150 ms), false after 500 ms without overlap. Overlay modes force it true. Its WinEvent hooks, and those of `MaximizedWindowMonitor`, are removed while the dock is hidden for a fullscreen app: they are system-wide, every event is marshalled through the UI thread, and a game raises them constantly. Re-enabling checks at once.

### 4.6 Idle, low-power and fullscreen

`PowerPolicy` (`GlassDock.Shell/System`) publishes one state consumed by glass, animations and the widget scheduler:

| State | Trigger | Effect |
|---|---|---|
| Active | pointer over dock, drag, flyout open, keyboard nav | animations on, render loop subscribed |
| Rest | none of the above for `Idle.RestAfterMs` (3000) | no render loop; event-driven updates only |
| Saver | Energy Saver on (`PowerManager.EnergySaverStatus`, `GUID_POWER_SAVING_STATUS`), or on battery with `power.reduceOnBattery` | G3→G2, optional G1→S, magnification off, widget intervals ×`Saver.IntervalMultiplier` |
| Suspended | dock hidden (fullscreen / auto-hidden), session locked, display off (`GUID_CONSOLE_DISPLAY_STATE`), remote session | all timers paused, capture stopped; event subscriptions kept |

Also read `UISettings.AdvancedEffectsEnabled` (the "Transparency effects" switch) and `SystemParameters.IsRemoteSession`: either false/true respectively → Solid.

**Fullscreen detection** — `FullscreenMonitor`, per monitor, three sources OR-ed and debounced (`Fullscreen.DebounceMs`, 300):
1. `ABN_FULLSCREENAPP` from our appbar callback (authoritative when it fires; known to be flaky on Windows 11).
2. Own check on `EVENT_SYSTEM_FOREGROUND` and on location changes of the foreground window: window rect ⊇ monitor rect, not cloaked, class not in `Fullscreen.IgnoredClasses` (`Progman`, `WorkerW`, `Shell_TrayWnd`, `XamlExplorerHostIslandWindow`, shell `Windows.UI.Core.CoreWindow` surfaces, our own windows).
3. `SHQueryUserNotificationState` ∈ {`QUNS_BUSY`, `QUNS_RUNNING_D3D_FULL_SCREEN`, `QUNS_PRESENTATION_MODE`} for the primary monitor.

Start or search in front overrides all three (`Fullscreen.LauncherProcesses`: `StartMenuExperienceHost.exe`, `SearchHost.exe`, seen through an `EVENT_SYSTEM_FOREGROUND` hook): the game is still fullscreen behind it, but pressing Win in a game should bring the dock up the way it brings up the native taskbar. The keyboard hook is paused in the game and misses that Win press, so leaving fullscreen this way also reveals an auto-hidden dock. When the game takes the foreground back, the normal enter debounce hides the dock again.

Clicking the dock while Start or search is open: Start refuses every way of taking the foreground and would stay on top of a flyout. A press on the dock sends Esc (once per closing; only while a launcher process is foreground, so it never reaches a game). Start fades for about 200 ms, so the press starts it and the click lands as it ends. The dock must not activate during the press (that cancels the button's capture). `FullscreenMonitor.HoldShown` keeps the dock over the game while the press lasts or a flyout other than the update offer is open, and our own foreground window counts the same. A flyout opened while Start is still fading shows at once and takes the foreground when Start lets go (`ForegroundActivator.ActivateOnceLauncherGone`). `ForegroundActivator.Activate` closes Start first too, for activating apps from the dock.

While fullscreen on a monitor: that dock's windows are hidden (`SW_HIDE`, so nothing is topmost over a game), its edge sensor is disabled unless `fullscreen.revealOnEdge`, `PowerPolicy` → Suspended.

---

## 5. Architecture

### 5.1 Processes and threads

| Thread | Owns | Rules |
|---|---|---|
| **UI** (STA, WPF `Dispatcher` + a `DispatcherQueue` for the system compositor) | all windows, view models, `Compositor`, `WM_HOTKEY`, appbar callbacks, ManagedShell services (they are dispatcher-bound) | never block; all shell queries that can hang (`WM_GETICON`, COM on other apps) go to workers |
| **Input** (dedicated, highest priority, message loop) | `WH_KEYBOARD_LL`, `SendInput` translation | no allocation/logging in the callback; talks to UI via `PostMessage` to `ShellHostWindow` |
| **ShellEvents** (dedicated, message loop) | high-volume `SetWinEventHook`s (`LOCATIONCHANGE`, `NAMECHANGE`, show/hide) | coalesces per HWND, forwards batches with `Dispatcher.BeginInvoke(Background)` |
| **ShellCom** (dedicated STA worker with a queue) | icon extraction, shortcut resolution, jump lists, wallpaper COM | results marshalled back as frozen `BitmapSource` / records |
| Thread pool | widget data sampling, file I/O, settings save | exceptions caught and logged per task |
| **Watchdog process** | one blocked thread | no WPF |

Single instance: named mutex `Local\GlassDock.Main`; a second launch posts a registered message ("open settings") to the first and exits.

### 5.2 Window structure

Per dock (per monitor):

| Window | Tech | Styles | Role |
|---|---|---|---|
| `GlassBackdropWindow` | Win32 + `Windows.UI.Composition` | `WS_POPUP`; `WS_EX_NOREDIRECTIONBITMAP, TOOLWINDOW, NOACTIVATE, LAYERED, TRANSPARENT, TOPMOST` | live backdrop only (G1). Sized exactly to the pill. Click-through. |
| `DockWindow` | WPF, `AllowsTransparency=True`, `WindowStyle=None`, `ShowInTaskbar=False`, `Topmost=True`, `ShowActivated=False` | adds `WS_EX_TOOLWINDOW, WS_EX_NOACTIVATE` in `SourceInitialized`; `WM_MOUSEACTIVATE` → `MA_NOACTIVATE` | all content. Bounds = pill + magnification headroom + shadow margin. **Owner = glass window**, which keeps the pair glued in z-order (owned windows stay above their owner). Is the appbar HWND and the OLE drop target. |
| `EdgeSensorWindow` | Win32, layered, alpha 1 | `TOOLWINDOW, NOACTIVATE, TOPMOST`, `AutoHide.SensorThicknessPx` (2) | only in auto-hide mode: pointer/drag enter reveals the dock |

Shared:

| Window | Role |
|---|---|
| `ShellHostWindow` (hidden top-level Win32) | `TaskbarCreated`, `WM_HOTKEY`, messages from the input thread, `WM_DISPLAYCHANGE`, `WM_SETTINGCHANGE`, `WM_POWERBROADCAST`, `WM_WTSSESSION_CHANGE`, `WM_ENDSESSION` |
| ManagedShell's `Shell_TrayWnd` / `TrayNotifyWnd` + its shell-hook window | tray interception, shell hook, ITaskbarList messages |
| Flyouts: `PreviewFlyout`, `StackFlyout`, `GroupFlyout`, `TrayFlyout`, `CalendarFlyout`, `WidgetPickerFlyout`, `DockContextMenu` | on-demand WPF windows derived from `FlyoutWindow`; one open at a time via `FlyoutManager`; owned by the `DockWindow`; non-layered with `DWMWA_SYSTEMBACKDROP_TYPE = DWMSBT_TRANSIENTWINDOW` + `DWMWCP_ROUND` (native acrylic, cheap). Flyouts that need keyboard or outside-click dismissal are activated (allowed right after a click on our window) and close on deactivation. |
| `SettingsWindow` | normal activatable WPF window, created on demand, released on close |

Why two windows per dock instead of one: a composition visual tree attached to an HWND is drawn above that window's own surface, so WPF content cannot sit on top of a backdrop visual in the same window; Microsoft's own WPF acrylic sample has the same "airspace" limitation and the accepted workaround is exactly this two-window sandwich **[VERIFIED: MS Q&A thread]**. Why the content window is layered: per-pixel hit-testing comes free (alpha-0 pixels are click-through, which the magnification headroom needs). Paint the pill's hit area with at least alpha 1/255 so "Clear" style stays clickable.

Moving/resizing the pair: always in one `BeginDeferWindowPos`/`DeferWindowPos`/`EndDeferWindowPos` batch on the UI thread **[EXPERIMENT E6]** (if tearing shows during slides, animate opacity instead of position for the glass window).

### 5.3 Z-order and topmost

- Dock windows are `HWND_TOPMOST`. Start, Search, notification centre and other system flyouts live in higher window bands and correctly cover the dock.
- On fullscreen: hide (not merely demote). On leaving: `ShowWindow(SW_SHOWNOACTIVATE)` and re-assert `HWND_TOPMOST`.
- Re-assert topmost on `WM_WINDOWPOSCHANGING` only if we were pushed below a non-topmost window; never fight another topmost window in a loop (log instead).
- `DWMWA_EXCLUDED_FROM_PEEK` on dock windows so Aero Peek / Win+comma do not blank the dock.

### 5.4 DPI

- `app.manifest`: `<dpiAwareness>PerMonitorV2</dpiAwareness>`, `<dpiAware>true/pm</dpiAware>`, Windows 10/11 `supportedOS` GUIDs, `longPathAware`.
- WPF scales the content window per monitor. Everything exchanged with Win32 (appbar rects, glass window bounds, thumbnail rects, `Shell_NotifyIconGetRect` answers) is physical pixels: convert through one helper `DpiScale.ToPx(dip, monitorDpi)`; no ad-hoc multiplications.
- On `WM_DPICHANGED` / `DpiChanged`: re-layout, re-run `AppBarRegistration.Apply`, re-request icons at the new pixel size.

### 5.5 Modules and file layout

```
GlassDock/
  global.json  Directory.Build.props  Directory.Packages.props  GlassDock.sln
  build/            build.ps1  publish.ps1  verify.ps1 (acceptance checks)
  src/
    GlassDock.Core/                 no UI, no Win32
      Config/     AppConstants.cs  DockSettings.cs  SettingsStore.cs  SettingsMigrator.cs  SettingsJsonContext.cs
      Logging/    Log.cs  FileLogSink.cs
      Model/      DockItem.cs  AppIdentity.cs  WindowInfo.cs  TileSpec.cs  GlassPreset.cs  MonitorId.cs
      Services/   ITaskbarHider  IAppBar  IWindowTracker  ITrayHost  IPinnedAppStore  IAppLauncher
                  IHotkeyService  IFullscreenMonitor  IPowerPolicy  IGlassBackdrop  IIconProvider
      Layout/     DockLayoutMath.cs (magnification, slot positions — pure, unit-tested)
    GlassDock.Shell/                Win32 / COM / WinRT / ManagedShell adapters
      Interop/    NativeMethods.txt  ComInterfaces.cs  PropertyKeys.cs  DpiScale.cs
      Taskbar/    TaskbarHider.cs  TaskbarRestorer.cs  TaskbarStateJournal.cs  TaskbarGuard.cs
                  ExplorerWatcher.cs  AppBarRegistration.cs  WorkAreaVerifier.cs
      Watchdog/   WatchdogMain.cs  CrashLoopGuard.cs
      Input/      KeyboardHook.cs  HotkeyTranslator.cs  HotkeyService.cs  InputInjector.cs  ForegroundActivator.cs
      Windows/    WindowTracker.cs  WindowEligibility.cs  AppIdentityResolver.cs  VirtualDesktopFilter.cs
                  FullscreenMonitor.cs  ThumbnailHost.cs  WinEventPump.cs
      Apps/       PinnedAppStore.cs  TaskbarPinImporter.cs  ShortcutReader.cs  AppLauncher.cs
                  IconProvider.cs  JumpListReader.cs
      Tray/       TrayHost.cs
      Glass/      HostBackdropGlass.cs  AccentPolicyGlass.cs  NullGlass.cs  WallpaperSource.cs
                  BackdropOcclusionMonitor.cs  ShaderCompiler.cs
      System/     PowerPolicy.cs  SessionMonitor.cs  DisplayTopology.cs  StartupRegistration.cs
    GlassDock.Widgets/
      Framework/  IWidget.cs  WidgetDescriptor.cs  WidgetContext.cs  WidgetRegistry.cs  RefreshScheduler.cs
      BuiltIn/    Start/ Apps/ Tray/ Clock/ Notifications/ Cpu/ Memory/ Network/ Battery/ Volume/ Media/ ...
    GlassDock/                      WPF exe
      Program.cs  DockMain.cs  App.xaml  app.manifest  AppHost.cs (manual composition root)
      Windows/    DockWindow.xaml  GlassBackdropWindow.cs  EdgeSensorWindow.cs  ShellHostWindow.cs  DockInstance.cs
      Flyouts/    FlyoutWindow.cs  FlyoutManager.cs  PreviewFlyout.xaml  StackFlyout.xaml  GroupFlyout.xaml
                  TrayFlyout.xaml  CalendarFlyout.xaml  WidgetPickerFlyout.xaml  DockContextMenu.cs
      Controls/   DockItemsPanel.cs  AppButton.xaml  TileHost.cs  BadgeView.xaml  GlassChrome.xaml  LiquidLens.cs
      ViewModels/ DockViewModel.cs  AppItemViewModel.cs  TileViewModel.cs  ...
      Glass/      GlassController.cs
      Settings/   SettingsWindow.xaml  Pages/*.xaml
      Themes/     Presets.json  Brushes.xaml  Animations.xaml
      Shaders/    LiquidLens.hlsl
  tests/
    GlassDock.Tests/                settings round-trip + migration, layout math, eligibility rules,
                                    translator state machine, Favorites parser fixtures, identity matching
    GlassDock.Spikes/               console app: `spikes e1` .. `spikes e11`
```

Cross-cutting rules for the implementer: every numeric or string constant lives in `AppConstants` (compile-time defaults) or `DockSettings` (user-tunable); every `catch` logs through `Log` with the operation name and HRESULT/`Marshal.GetLastPInvokeError()`; no empty catch blocks; comments explain *why* (pitfalls from this report are good candidates).

---

## 6. App enumeration

### 6.1 Pinned items

**Own store.** `settings.json → apps.items[]` is the source of truth. A pin is:

```jsonc
{ "id": "a1b2", "kind": "app",
  "aumid": "Microsoft.WindowsTerminal_8wekyb3d8bbwe!App",   // optional
  "shortcut": "pins\\a1b2.lnk",                              // optional, relative to %APPDATA%\GlassDock
  "target": "C:\\Program Files\\App\\app.exe", "arguments": "", "workingDir": "",
  "displayName": "App", "iconOverride": null }
```
Launches (`AppsModel.Launch`, stack files) run on one STA thread (`AppLauncher.InBackground`): `ShellExecuteEx` can take seconds (.lnk resolution, packaged activation, a scan, a sleeping disk, the whole UAC prompt for `runas`) and froze the dock on the UI thread. The icon bounces at the click and stops if the launch fails or UAC is cancelled. Dropped/imported `.lnk` files are copied into `%APPDATA%\GlassDock\pins\` so the pin survives the original being deleted. Launch order of preference: AUMID (packaged) via `IApplicationActivationManager::ActivateApplication`; else `ShellExecuteEx` on the stored `.lnk`; else on `target`. "Run as administrator" = verb `runas`. GlassDock itself runs at medium integrity, so launched apps are not elevated by accident.

**First-run import (read-only), `TaskbarPinImporter`:**
1. Files: `%APPDATA%\Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar\*.lnk` (present on the dev machine: 3 files).
2. Order and packaged-app pins: `HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\Taskband` → `Favorites` (binary; value names confirmed on the dev machine: `Favorites`, `FavoritesResolve`, `FavoritesChanges`, `FavoritesVersion`, `FavoritesRemovedChanges`). The blob is believed to be a sequence of records each containing an absolute ITEMIDLIST, in left-to-right order, ending with `0xFF`; resolve each PIDL with `SHCreateItemFromIDList` → `IShellItem2` (`SIGDN_DESKTOPABSOLUTEPARSING`, `PKEY_AppUserModel_ID`). The exact record framing is undocumented **[EXPERIMENT E7]**. Fallback if parsing fails: folder scan in file-time order, and packaged-app pins are simply not imported (logged, and stated in the first-run dialog).
3. Never write to either location.

### 6.2 Running windows

**Source of events** (through ManagedShell `TasksService`, adapter `WindowTracker : IWindowTracker`): `RegisterShellHookWindow` + `RegisterWindowMessage("SHELLHOOK")` handling `HSHELL_WINDOWCREATED` (1), `WINDOWDESTROYED` (2), `WINDOWACTIVATED` (4), `GETMINRECT` (5), `REDRAW` (6), `ENDTASK` (10), `WINDOWREPLACED` (13), `WINDOWREPLACING` (14), `MONITORCHANGED` (16), `RUDEAPPACTIVATED` (0x8004), `FLASH` (0x8006); initial population via `EnumWindows`; cloak tracking via `EVENT_OBJECT_CLOAKED`/`UNCLOAKED` **[VERIFIED: ManagedShell `TasksService.cs`]**. We add on our `WinEventPump`: `EVENT_OBJECT_NAMECHANGE` (titles), `EVENT_SYSTEM_MINIMIZESTART/END`, `EVENT_OBJECT_LOCATIONCHANGE` (occlusion + fullscreen, throttled), `EVENT_SYSTEM_FOREGROUND`.

`HSHELL_GETMINRECT`: answer with the screen rect of the app's dock button so minimize/restore animations fly to the dock (ManagedShell exposes this hook; supply the rect from `DockViewModel`).

**Eligibility** — `WindowEligibility.IsTaskbarWindow(hwnd)`; keep it a pure function over a `WindowFacts` record so it is unit-testable:

```csharp
static bool IsTaskbarWindow(in WindowFacts w)
{
    bool appWindow = (w.ExStyle & WS_EX_APPWINDOW) != 0;    // explicitly forced onto the taskbar
    return w.IsVisible
        && !w.IsCloaked                                     // DWMWA_CLOAKED != 0: suspended UWP ghost or another virtual desktop
        && ((w.ExStyle & WS_EX_TOOLWINDOW) == 0 || appWindow)
        && ((w.ExStyle & WS_EX_NOACTIVATE) == 0 || appWindow)
        && (w.Owner == 0 || appWindow)
        && !IgnoredClasses.Contains(w.ClassName)            // Progman, WorkerW, Shell_TrayWnd, Shell_SecondaryTrayWnd, our own
        && w.ClassName != "Windows.UI.Core.CoreWindow"      // UWP content is represented by its ApplicationFrameWindow
        && w.Width > 0 && w.Height > 0;
}
```
Virtual desktops: `IVirtualDesktopManager::IsWindowOnCurrentVirtualDesktop` (setting `apps.showAllDesktops`, default `false`). ManagedShell's `ApplicationWindow.CanAddToTaskbar`/`ShowInTaskbar` already implement equivalent rules; our function is the authority for tests and for anything ManagedShell misses, and differences found against Explorer's taskbar are bugs to log and fix here.

### 6.3 Identity and grouping (`AppIdentityResolver`)

Resolve one `AppIdentity { Key, Aumid?, ExePath?, IsPackaged }` per window, in order:
1. `SHGetPropertyStoreForWindow(hwnd)` → `PKEY_AppUserModel_ID` (covers windows with an explicit ID and `ApplicationFrameWindow` hosts of UWP apps).
2. `ApplicationFrameWindow` without it: child `Windows.UI.Core.CoreWindow` → its PID → `GetApplicationUserModelId`.
3. `GetApplicationUserModelId(process)` (packaged desktop apps).
4. `QueryFullProcessImageNameW` → `Key = "path:" + normalizedLowerPath`.

Group windows by `Key` (setting `apps.groupWindows`, default `true`). Matching a pin to running windows: same AUMID, else same normalized target path.

**As built (`AppIdentity`, `PackagedApps`).** ManagedShell reports the exe path for windows with no explicit app id, and two common cases arrive that way:
- File Explorer windows set no app id. A window whose exe is `%windir%\explorer.exe` counts as `Microsoft.Windows.Explorer`, which is the id on the pin imported from the taskbar.
- Packaged desktop apps (Spotify, Claude, ChatGPT from the Store) run from `...\WindowsApps\<package full name>\`. Their exe cannot be started directly (ShellExecute fails with access denied), and the folder changes with every update. `PackagedApps` turns such a path into `family!appId`. It gets the family from `PackageFamilyNameFromFullName` of the folder name. It gets the app id from `AppxManifest.xml`: the `Application` whose `Executable` is that exe, else the one whose `Id` is the exe name (Spotify's entry is `SpotifyMigrator.exe`, its window belongs to `Spotify.exe`), else the package's only app. Windows, pins and launches all use that id. Pins created earlier with such a path are rewritten to the id at startup (`PinStore.UpgradePackagedPins`), while that version is still installed.

**Known pitfall — process-level explicit AppIDs.** Apps that call `SetCurrentProcessExplicitAppUserModelID` (Squirrel/Electron apps such as Discord, Chromium profiles) do not expose that ID on the window through any documented API, and their pinned shortcut often targets a stub (`Update.exe --processStart App.exe`). Without help, the running app shows up beside its pin instead of on it. Heuristics, in `PinMatcher` with unit tests: (a) Squirrel pattern: shortcut argument `--processStart X.exe` matches a process named `X.exe` under the shortcut target's directory tree; (b) the shortcut's `PKEY_AppUserModel_RelaunchCommand`/window relaunch properties when present; (c) user override: "Pin" from a running item stores that process's real path, which always matches. Keep a small `apps.identityOverrides` table in settings for stubborn cases. (d) Squirrel version folders: a pin made from a running Squirrel app stores `<root>\app-<version>\App.exe`, and every update moves the app to a new `app-<version>` folder. `AppIdentity.IdentityPath` makes that folder version-free when matching pins to windows, and `CurrentSquirrelPath` launches (and takes the icon from) the newest `app-*` folder that has the file, since the updater leaves the pinned one outdated and later deletes it.

### 6.4 Icons (`IconProvider`, on the ShellCom thread)

- Request size = `iconSizeDip × monitorScale × maxMagnification`, rounded up to the next of 32/48/64/96/128/256.
- Packaged and any AUMID: `SHCreateItemFromParsingName("shell:AppsFolder\\<AUMID>")` → `IShellItemImageFactory::GetImage(size, SIIGBF_ICONONLY | SIIGBF_BIGGERSIZEOK)` → 32-bit premultiplied HBITMAP → frozen `BitmapSource`. Also gives the display name (`SIGDN_NORMALDISPLAY`).
- Shortcut: the pin keeps `IShellLink::GetIconLocation` (`IconPath`/`IconIndex`, read from the copied `.lnk` for older pins) and draws that icon with `SHDefExtractIconW` at the requested size; without one, the image-factory call on the target. Asking for the `.lnk` itself would add the shortcut arrow overlay.
- Running window with no pin: exe path via image factory; fallback `SendMessageTimeout(WM_GETICON, ICON_BIG, ..., SMTO_ABORTIFHUNG, IconTimeoutMs)`, then `GetClassLongPtr(GCLP_HICON)`.
- Cache keyed by `(identityKey, pixelSize)`, LRU with `AppConstants.Icons.CacheMaxBytes`; invalidate on `HSHELL_REDRAW` for window-icon-sourced entries.

### 6.5 Badges, flashing, progress

- **Overlay icons / progress** arrive as ITaskbarList messages to the (fake) taskbar window: `WM_USER+79` overlay icon (`lParam` HICON), `+85` overlay description, `+64` progress value, `+65` progress state, `+60` MarkFullscreenWindow — all handled by ManagedShell and surfaced as `ApplicationWindow.OverlayIcon`, `ProgressValue`, `ProgressState` **[VERIFIED]**. Telegram/Discord unread counts are overlay icons, so `BadgeView` renders the overlay bitmap at the icon's corner; no count parsing.
- **ITaskbarList relay.** ITaskbarList sends to the `TaskbandHWND` property of the topmost `Shell_TrayWnd`. ManagedShell's `TasksService` points it at its own hook window and removes it from Explorer's tray (at start and on every `TaskbarCreated`) **[VERIFIED]**, and it does not forward anything. So AddTab/DeleteTab never reached Explorer. Discord hides to the tray with `setSkipTaskbar(true)` (DeleteTab) and comes back with AddTab; Explorer then left the window out of Alt+Tab and the native taskbar until Discord restarted (Gitea #2). `TaskbarListRelay` (a message-only window) takes the property: every `WM_USER+n` and SHELLHOOK message goes to ManagedShell's hook as before (same thread, so shared-memory handles stay valid), and SHELLHOOK notifications are also posted to Explorer's `MSTaskSwWClass`. It re-attaches after each `TaskbarCreated` and hands the property back before ManagedShell disposes. After a crash, `TaskbarRestorer` points Explorer's tray back at its own task band, since nothing else would until Explorer restarts. `build/taskman-owner.ps1` shows the chain.
- **UWP badge numbers** (`BadgeUpdateManager`) have no public read API for third parties: out of scope; say so in the settings text.
- **Flashing**: `HSHELL_FLASH` → `AppItem.NeedsAttention`; animation per `appearance.attentionStyle` (bounce N times then steady glow); cleared on activation.

### 6.6 Jump lists (v0.3; v0.1 has a plain context menu)

v0.1 context menu: Open / New window, window list, Pin/Unpin, Close window / Close all, End task (confirm), Run as administrator, Open file location, Dock settings.

v0.3 `JumpListReader`:
- Recent/Frequent: documented `IApplicationDocumentLists` (`SetAppID`, `GetList(ADLT_RECENT | ADLT_FREQUENT)`) → `IShellItem`/`IShellLink` entries.
- Tasks and custom categories: no documented read API (`ICustomDestinationList` is write-only **[VERIFIED: MS docs]**). Parse `%APPDATA%\Microsoft\Windows\Recent\CustomDestinations\<hash>.customDestinations-ms`, which is a concatenation of serialized shell links; locate each link header and load it through `IPersistStream` on a `ShellLink` object. The file name is a hash of the AppID (reported as CRC-64 of the upper-cased UTF-16 ID; implicit IDs for apps without an explicit one are derived differently) — **uncertain**; validate against the forensic tool JLECmd's source before relying on it. If the hash cannot be reproduced for an app, show only Recent/Frequent plus our own commands.
- Invoking an entry: `IShellLink` → `IContextMenu::InvokeCommand` default verb, or `ShellExecuteEx` with its target/arguments.

### 6.7 Live previews (`PreviewFlyout` + `ThumbnailHost`)

- One card per window: icon + title (ellipsis) + close button above a thumbnail area. Card size from `DwmQueryThumbnailSourceSize`, fitted into `Preview.MaxCardDip` (240×135) keeping aspect.
- `DwmRegisterThumbnail(flyoutHwnd, sourceHwnd, out h)`; `DwmUpdateThumbnailProperties` with `rcDestination` (physical px inside the flyout), `opacity`, `fVisible`, `fSourceClientAreaOnly = false`; update on layout/DPI change; `DwmUnregisterThumbnail` on close. DWM draws thumbnails above the destination window's own content, so nothing may be laid out over the thumbnail rect.
- The flyout is a non-layered window with a system backdrop (5.2); thumbnails into a layered WPF window are unverified **[EXPERIMENT E8]**.
- Close = `PostMessage(WM_SYSCOMMAND, SC_CLOSE)`. Click = `ForegroundActivator`. Hover-to-peek (undocumented `DwmpActivateLivePreview`) is deliberately left out of scope until asked for.
- Open delay / close grace from settings (`preview.openDelayMs` 350, `preview.closeGraceMs` 250); moving from the app button into the flyout must not close it (treat button + flyout as one hover region).

---

## 7. System tray hosting

### 7.1 Options

| Approach | Verdict |
|---|---|
| Read Explorer's tray toolbar (`TB_GETBUTTON` + cross-process memory) | Dead on Windows 11 22H2+: the tray is XAML, there is no toolbar to read |
| UI Automation over the native overflow flyout | Unreliable; the tree is not populated while the taskbar is hidden; cannot forward right/double clicks faithfully |
| Registry `HKCU\Control Panel\NotifyIconSettings` (29 entries on the dev machine) | Gives exe path, tooltip and a PNG snapshot, but no HWND/ID/callback message → cannot deliver clicks. Useful only for remembering per-icon "pinned/hidden" choices |
| **Own `Shell_TrayWnd` that intercepts `Shell_NotifyIcon`** | **Chosen** — the approach of every working replacement (ManagedShell → RetroBar, Cairo) |

### 7.2 How it works (ManagedShell `TrayService`, adapter `TrayHost : ITrayHost`) **[VERIFIED]**

- `Shell_NotifyIcon` in every app does `FindWindow("Shell_TrayWnd")` and sends `WM_COPYDATA`. ManagedShell creates a top-level `WS_EX_TOPMOST | WS_EX_TOOLWINDOW` window of class `Shell_TrayWnd` with a `TrayNotifyWnd` child, kept first in z-order (re-checked every 100 ms; the real tray is pushed to the bottom), so apps find it first.
- `WM_COPYDATA` `dwData == 1`: `NOTIFYICONDATA` add/modify/delete/setversion → icon model. `dwData == 3`: `Shell_NotifyIconGetRect` → we answer with the icon's on-screen rect, so apps position their flyouts next to our dock. `dwData == 0`: appbar messages → forwarded. Everything unhandled is forwarded to the real Explorer tray, so Explorer's own state stays consistent and reappears intact when we quit.
- Icons that existed before we started: ManagedShell broadcasts `TaskbarCreated`; well-behaved apps re-add their icons.
- Click forwarding: post the icon's callback message to its window in the format its `uVersion` expects (legacy: `wParam = uID`, `lParam = mouse message`; version 4: `wParam = MAKEWPARAM(x, y)`, `lParam = MAKELPARAM(message, uID)`, plus `NIN_SELECT`/`NIN_KEYSELECT`/`WM_CONTEXTMENU`). ManagedShell's `NotifyIcon` exposes `IconMouseEnter/Leave/Move/Down/Up/DoubleClick`; call `SetForegroundWindow` on the icon's window before a right-click so its menu dismisses properly.
- Elevated apps: `Shell_NotifyIcon` adds the callback message to the caller's UIPI filter (per Geoff Chappell's analysis), so our medium-integrity posts should get through **[EXPERIMENT E9]**.

### 7.3 UI: `TrayTile` + `TrayFlyout`

Arrow tile → flyout grid of all icons (left / double / right click forwarded, tooltip on hover, balloon text ignored in v0.1). Optional "promoted" icons shown inline in the dock (`tray.promoted[]`, matched by exe path + GUID/ID). Drag to reorder inside the flyout.

A left click closes the grid before it is forwarded, so the app's window is not covered. A right click (from the press, since some apps open their menu on it) keeps the grid open until the app's menu must be over: a Win32 popup menu (`#32768`) that was seen is gone, or a mouse button goes down on a window other than the grid. The menu window itself is not tracked: Steam's lives in another process than its icon, and the foreground hops between hidden windows while it opens. If the grid stays active for `Tray.MenuWaitMs` with no menu, normal light dismiss returns.

Icons whose window no longer exists are left out of the grid. An app killed without `NIM_DELETE` (Discord restarting for an update) leaves its icon in ManagedShell's list; Explorer drops such icons on hover, but ManagedShell only removes one after a click has been sent to the dead window, so that click did nothing next to the live icon that looked the same.

### 7.4 Limitations to state openly

- Windows 11's system indicators (network, volume, battery, IME, clock) are **not** notify icons and will not appear. They are covered by our own tiles: Quick Settings (injects Win+A), Notifications (Win+N), Clock, and from v0.2 Volume/Battery/Network widgets.
- Apps that ignore `TaskbarCreated` stay invisible until they next touch their icon.
- Balloon notifications (`NIF_INFO`) are still shown by Windows as toasts because everything is forwarded to Explorer.
- Other tools that look up `Shell_TrayWnd` (taskbar tweakers) will find the fake window; `TrayHost.Suspend()` exists for troubleshooting, and the fake window is destroyed on exit so Explorer is first again.
- Do Not Disturb toggle: no public API. v0.1 opens `ms-settings:notifications`; a real toggle is an open question (section 12).

---

## 8. Widgets framework

### 8.1 Model: everything in the dock is a tile

The dock is an ordered list of `TileSpec`s. Built-in system tiles (Start, Apps, Tray, Clock, Notifications) and optional widgets implement the same interface; the Apps tile is the only one that expands into many buttons.

```csharp
public interface IWidget : IDisposable
{
    WidgetDescriptor Descriptor { get; }          // Id, DisplayName, Category, DefaultSize, Sizes, SettingsSchema, MinRefresh
    FrameworkElement CreateView(WidgetContext ctx);
    void OnPowerStateChanged(PowerState state);    // Suspended → stop work; Active/Rest → resume
    FrameworkElement? CreateFlyout() => null;      // optional click-through panel (calendar, mixer, ...)
}

public sealed record WidgetContext(
    string InstanceId, JsonObject Settings, Action<JsonObject> SaveSettings,
    IRefreshScheduler Scheduler, IDockAppearance Appearance,   // light/dark variant, accent, size S/M/L, orientation
    ILog Log, IServiceProvider Services);

public interface IRefreshScheduler
{
    IDisposable Every(TimeSpan interval, Func<CancellationToken, Task> sample, Action applyOnUi);
    IDisposable AlignedToClock(ClockUnit unit, Action applyOnUi);      // fires exactly on second/minute boundaries
}
```

`RefreshScheduler` owns **one** timer per bucket (1 s, 5 s, 30 s, 5 min); widgets register into buckets instead of creating timers. It pauses in `Suspended`, multiplies intervals in `Saver`, runs `sample` on the pool and `applyOnUi` on the dispatcher, and logs + backs off a widget whose sampler throws (`Widgets.ErrorBackoffSeconds`). `WidgetRegistry` discovers built-ins by a static table (no reflection scanning); an external plugin model is deliberately deferred to v0.5.

Picker (v0.3): `WidgetPickerFlyout` — categories on the left, search box, live preview cards, drag a card onto the dock or click "Add". Reorder by dragging tiles in the dock (same drag engine as apps, section 9.3).

### 8.2 Data sources

| Widget | Milestone | API |
|---|---|---|
| Start | v0.1 | injected `VK_LWIN`; right-click → Win+X; optional custom image |
| Apps | v0.1 | section 6 |
| Tray | v0.1 | section 7 |
| Clock (12/24 h, seconds, date) | v0.1 | `AlignedToClock`; click → Win+N (native calendar) in v0.1, own calendar flyout v0.3 |
| Notifications / Quick settings | v0.1 | injected Win+N / Win+A |
| Show desktop | v0.1 | injected Win+D |
| CPU | v0.2 | `GetSystemTimes` deltas (or PDH `\Processor Information(_Total)\% Processor Utility` to match Task Manager) |
| Memory | v0.2 | `GlobalMemoryStatusEx` |
| Network throughput | v0.2 | `GetIfTable2` byte-counter deltas on the default-route interface |
| Storage | v0.2 | `DriveInfo` (5 min bucket) |
| Battery | v0.2 | `GetSystemPowerStatus` + `RegisterPowerSettingNotification(GUID_BATTERY_PERCENTAGE_REMAINING, GUID_ACDC_POWER_SOURCE)` (event-driven) |
| Volume / mute, mic mute | v0.2 | Core Audio `IMMDeviceEnumerator`, `IAudioEndpointVolume` + change callbacks. The wheel steps the level; a click opens `VolumeFlyout`: the device's `PKEY_Device_FriendlyName`, a mute button and a thick accent `PillSlider` that follows the widget's callbacks (moving the level unmutes, as in the system flyout). Below it, an app mixer like EarTrumpet's: `AppAudio.All()` reads every live session on every active output each `AppVolumeRefreshMs`, one row per exe plus system sounds, each with its icon (a click mutes), name (the session's display name, else the exe's file description or product name, else the exe name; some exes ship a blank description) and a thin slider. Rows are ordered when the set of apps changes: apps making sound now (`IAudioMeterInformation` peak > 0) first, then ones with an open stream, then the rest, system sounds last; while the set stays the same the order is kept, so rows do not jump. The list scrolls past `MixerMaxHeightDip`. |
| Now Playing | v0.2 | `GlobalSystemMediaTransportControlsSessionManager.RequestAsync`, `CurrentSessionChanged`, `MediaPropertiesChanged`, `PlaybackInfoChanged`; thumbnail stream → `BitmapImage`. The tile's own buttons play/skip; a click elsewhere opens `MediaFlyout`: the art decoded at `MediaFlyoutArtPx`, a timeline extrapolated from `GetTimelineProperties().LastUpdatedTime` that seeks on release (`TryChangePlaybackPositionAsync`, only when `IsPlaybackPositionEnabled`), and the source app's mixer volume (`AppAudio`: `IAudioSessionManager2` sessions on every active output (an app can play to a non-default device, e.g. SteelSeries Sonar gives each app its own virtual device) whose process has the id's package family, or is the exe the id names (`Spotify.exe`, `MSEdge`, `Chrome`), or failing both is the exe of a dock window with that app id; then `ISimpleAudioVolume`). Chromium plays every tab and installed web app from one audio process, so a web app's slider is the whole browser's and the flyout says so. |
| Recycle bin | v0.3 | `SHQueryRecycleBin`, `SHEmptyRecycleBin`, `SHChangeNotifyRegister` |
| Weather | v0.3 | HTTP to a keyless provider (e.g. Open-Meteo) with a manually chosen city; network use and provider need user sign-off |
| Timer / Pomodoro / Todo / Notes | v0.3 | local JSON under `%APPDATA%\GlassDock\data\` |
| Calendar flyout, world clocks, flip/analog clock | v0.3 | `TimeZoneInfo`; iCal sync later |
| Recently opened | v0.3 | `IApplicationDocumentLists` / Recent folder |
| Per-app mixer, output device switch | v0.4 | `IAudioSessionManager2`; default-device switch needs undocumented `IPolicyConfig` (flag in UI as experimental) |
| Quick toggles Wi-Fi / Bluetooth | v0.4 | `Windows.Devices.Radios.Radio.GetRadiosAsync` / `SetStateAsync` |
| Brightness | v0.4 | WMI `WmiMonitorBrightnessMethods` (internal panels), DDC/CI `SetVCPFeature` (external) |
| Temperature | open | no unprivileged, reliable source (ACPI thermal zone WMI needs admin; LibreHardwareMonitor needs a kernel driver) — section 12 |
| AI usage (Claude, Codex) | v0.2 | Claude plan limits: undocumented `GET api.anthropic.com/api/oauth/usage` (`anthropic-beta: oauth-2025-04-20`, as used by Claude Code's `/usage` and tools like CodexBar) with the CLI's token from `~/.claude/.credentials.json` (`claudeAiOauth.accessToken`/`expiresAt`), never written here; `five_hour`/`seven_day` → `utilization` 0-100 + `resets_at`; polled every 5 min, 429 backs off to 1 h, 401/403 waits for new credentials. The token lasts about 8 hours and only the CLI refreshes it, so someone who uses Claude only in the desktop app would see it stay expired: when it has expired or is refused, the tracker runs `claude auth status` (native `~/.local/bin/claude.exe`, else `claude.exe`/`claude.cmd` on PATH; hidden, output discarded since it holds the account email, 30 s timeout, at most every 30 min), which refreshes and saves the CLI's own login, and reads limits again once the file has changed. Claude Code tokens: transcripts `~/.claude/projects/**/*.jsonl` read incrementally, deduplicated by message id + request id, input + output + cache creation (cache reads left out), for the current 5-hour window and today. Codex: last `payload.rate_limits` (`primary`/`secondary`: `used_percent`, `resets_at`) in the newest `~/.codex/sessions/**/*.jsonl`; a window past its reset counts as 0 %. One shared `AiUsageTracker` serves every tile. The tile shows a row per service (installed app icon, bar, time to reset, %); a click opens `AiUsageFlyout` with a card per service and every window it reports (5 hours, weekly), live while open. `AiUsageTracker.Detect` decides which services count as used: Claude when `.credentials.json` or `projects` exists in `CLAUDE_CONFIG_DIR` (default `~/.claude`), Codex when `sessions` exists in `CODEX_HOME` (default `~/.codex`); it is checked on every refresh. With exactly one found, the tile's two rows are that service's 5-hour and weekly windows (captioned 5시간/주간, both with its icon), the flyout shows only its card and the menu only its usage page; with both or neither, a row and card for each. |
| Web-service widgets (GitHub, etc.) | v0.5 | per-service HTTP + token storage in Windows Credential Manager |

"Any app or folder can be a widget" needs no special case: apps and folders are tiles that can be placed outside the Apps tile.

---

## 9. Layout and interaction

### 9.1 Geometry

- Edges: bottom, top, left, right (`dock.edge`). Left and right lay the dock out vertically (`DockMetrics.Vertical`): every tile is one tile wide, and its length along the dock varies instead (`TileButton.SetLength`). Wide content shrinks to fit rather than widening the dock: the clock drops the year (`M/d`), the media widget shows art over its controls with the title only in the hover label, metric widgets stack glyph over value, and both sit in a `Viewbox` (`DownOnly`). Running bars sit on the side facing the edge, launches bounce away from it, labels open beside the pill (window headroom `Dock.SideLabelHeadroomDip`, long names trimmed), and the glass sheen runs across the short side. The appbar strip spans the whole edge (`ABE_LEFT`/`ABE_RIGHT`).
- Shape: `pill` (centred or start/end aligned, width fits content up to `maxWidthPercent`) or `bar` (full width, radius 0–8).
- Size: preset S/M/L → `iconSizeDip` 32/40/48 and proportional padding; "drag-resize" (v0.4) writes a custom `iconSizeDip`.
- `edgeGapDip` (distance from the screen edge), `itemSpacingDip`, `paddingDip`.
- The pill rect is computed by `DockLayoutMath` (pure). The WPF window is the pill rect inflated by headroom = `iconSize × (maxMagnification − 1)` towards the screen centre plus shadow margin; the glass window is exactly the pill rect.

### 9.2 Click semantics (Apps tile)

| Gesture | Not running | One window | Several windows |
|---|---|---|---|
| Click | launch (bounce until first window or `Launch.BounceTimeoutMs`) | activate; if already foreground, minimize | open preview flyout (setting: cycle instead) |
| Shift+click / middle-click | launch | new instance | new instance |
| Ctrl+Shift+click | launch elevated | new instance elevated | same |
| Ctrl+click | — | same as click | cycle to next window |
| Right-click | context menu / jump list | same | same |
| Hover | name tooltip | preview after delay | previews |
| File dragged over | — | after `DragActivateDelayMs` (500) activate the window so the user can drop into it | open previews, then activate hovered one |

"Already foreground" is well defined because the dock never takes activation on click (`WS_EX_NOACTIVATE`): the previous foreground window is still foreground when the click arrives.

### 9.3 Drag and drop

- **Built-in tiles:** right-click on the tray, quick settings, clock, notifications and show-desktop tiles offers move, "도크에서 숨기기" (sets the spec's `enabled` to false, keeping its place) and "기본 칸 표시", a checkable list (also in the dock menu) that brings hidden ones back; a type missing from `tiles` is appended.
- **Reorder (internal):** mouse capture after `SystemParameters.MinimumHorizontalDragDistance`, dragged item follows the pointer in an adorner, siblings animate apart ("icons part on drop"), drop commits the new order to settings. Dragging an app off the dock beyond `Drag.RemoveDistanceDip` = unpin (poof animation, v0.2). Disabled when `dock.locked`.
- **Pin (external, OLE):** `AllowDrop` on the dock; accept `FileDrop` (`.lnk`, `.exe`, folders, any file) and `Shell IDList Array` (items from the Apps folder / Start). Show an insertion gap while hovering.
- **Group:** while app A sits over the centre of app B (`Groups.MergeZoneSlots`, 0.25 slot on either side), B shows a faint accent ring (aimed). Holding the pointer still there (moving less than `Groups.StillDip`, 6 DIP) for `Groups.MergeHoverMs` (400) arms grouping: B grows to 1.2 with a solid accent ring and a tinted plate, and A shrinks to 0.82 as if going in. Any larger movement restarts the hold, so an icon swept across B on its way elsewhere only reorders; leaving the zone disarms at once. Releasing while armed turns B into a group of both, in B's place, or adds A to B if B already is a group. Next to a groupable app the dragged icon only reorders past it once it has cleared that app's aim zone; other neighbours swap at the midpoint as before. Only apps join groups (pinned, or running and pinned on the spot); folders and groups do not nest. Click or Win+N opens `GroupFlyout`: a deep-tinted panel (`FlyoutWindow.Tinted`, shared with the AI usage flyout) with the name and an app count, then the children as large icons (`Groups.FlyoutIconDip` 64, `Groups.FlyoutColumns` 3) with running bars. Click starts or cycles that app's windows. A click on the name edits it in place (a template-less underline TextBox: the Fluent theme's focused TextBox is white). Holding an app for `Groups.HoldToReorderMs` (350) lifts it; dragging slides the others aside and the drop saves the order (`AppsModel.MoveInGroup`), which is also the collage order on the dock. A `⋯` menu renames or ungroups; right-click takes an app out (placed right after the group); a group down to one app becomes that app again. The dock icon is the same collage as a stack, of the first four children (one large; two side by side; two over one; 2x2), drawn at `Groups.IconFraction` (0.96) of the cell instead of an app's 0.72 so each child is about 40% of the tile; the running bar covers every child's windows.
- **Stacks:** a pinned folder is a `FolderItem`; click opens `StackFlyout` (grid; fan/list variants later), contents via directory enumeration + `FileSystemWatcher`, icon is a mini-collage of the first items.
- Limitation: drags from elevated apps cannot reach a medium-integrity dock (UIPI). Documented, not worked around.

The other tiles (Start, widgets, tray, quick settings, clock, notifications) reorder the same way: a tile dragged past the centre of a neighbour in its row (a tile, a divider, or the apps as one block) swaps with it, and on release `AppHost.ReorderTiles` permutes those tiles within the slots they hold in `tiles` (disabled tiles and the other half of a bar stay put). Tiles move only within their own row; locking the dock turns this off too.

### 9.4 Magnification

Scale for an app icon whose unmagnified centre is `x` with the pointer at `px` (`DockMetrics.MagnifyScale`):
`s(x) = 1 + (maxScale − 1) · max(0, cos(clamp((x − px) / range, −1, 1) · π/2))²`, with `range = magnification.rangeItems × (tileSize + spacing)`. Centres come from the layout slots (`LayoutInformation.GetLayoutSlot`), not from where the icons are drawn, so moving icons never feed back into the pointer distance. The layout is never redone: each icon scales from its edge-side centre and gets a render offset `(extra before − extra after) / 2` along the dock, where an icon's extra is `(s − 1) · tileSize`; the tiles before and after the apps move out by half the total extra, and the glass layers (and the backdrop/lens rectangle) grow by the same amount on each end, so the pill widens symmetrically. Driven from `CompositionTarget.Rendering`, subscribed on pointer enter and unsubscribed once every scale has eased back to 1 (`Magnify.EasePerFrame`, `Magnify.SettleEpsilon`) after the pointer leaves, a drag starts or a flyout opens. Hover labels follow the magnified icon. Icons are requested at max-scale pixel size, so magnified icons stay sharp; the window's headroom includes `tileSize × (maxScale − 1)`. Only app icons magnify. Off in `Saver` state (when that exists).

### 9.5 Auto-hide

- Hidden state: the dock content slides off the edge (`AutoHide.SlideMs`) and the window is `SW_HIDE`; the frosted backdrop hides at once, the lens (drawn in the dock window) slides with the content. `EdgeSensorWindow` (`AutoHide.SensorThicknessPx`, alpha 1, never activates) stays along the whole edge, hidden while a fullscreen app is up.
- Reveal on: the pointer resting on the sensor for `autoHide.revealDelayMs`; with `autoHide.revealOnDockArea`, the pointer resting `autoHide.dockAreaRevealDelayMs` anywhere over the shown pill's spot or between it and the edge (polled every `AutoHide.AreaCheckMs` while hidden, not a sensor window, so clicks there still reach the app below); a file drag arriving on it, the Win key (the keyboard hook posts Win down/up as direct index `KeyboardHook.WinKeyIndex`; the dock stays while Win is held and `autoHide.winKeyLingerMs` after release; needs at least one translated shortcut, since otherwise there is no hook), Win+T/Win+B. Revealing for an app that needs attention is not done yet.
- A shown dock checks every `AutoHide.CheckMs` whether anything keeps it up: the pointer within the pill (plus `AutoHide.HoverMarginDip`, plus the headroom on the away side), a flyout or dock menu open, a drag or dock move, keyboard mode, an external drag over it. `autoHide.hideDelayMs` after the last of these it hides.
- No appbar reservation in this mode (2.6).

### 9.6 Multi-monitor

- `DisplayTopology` enumerates monitors (`EnumDisplayMonitors` + `QueryDisplayConfig` for a stable ID that survives re-ordering) and raises changes on `WM_DISPLAYCHANGE`.
- `monitors.mode`: `primary` (v0.1) | `all` (v0.2) | `selected` (list of IDs). `follow` (dock moves to the monitor with the active window) is a later option.
- One `DockInstance` (window pair + appbar + glass controller + fullscreen state) per monitor, all bound to the same `DockViewModel`; `apps.windowScope`: `all` | `thisMonitor`.
- Native taskbars: hidden on monitors with a dock (2.3).

### 9.7 Position change by dragging

Pressing empty glass (no tile under the pointer, dock not locked) captures the pointer at once: the first step off the pill lands on transparent pixels, which would otherwise pass the move to the window below. Past `DockMove.StartDistanceDip` the dock is being moved: the target edge is the one the pointer is nearest to relative to the screen size (the diagonals split the screen into four triangles), and while it differs from the current edge a click-through `DockGhostWindow` outlines where the pill will land. Release snaps the dock there (`AppHost.MoveDock` writes `dock.edge`, which rebuilds the dock); releasing over the current edge's triangle does nothing. The dock menu has the same choice under "도크 위치", which also works while the dock is locked. Flyouts and menus open away from the edge: above/below a horizontal dock, beside a vertical one.

---

## 10. Settings

### 10.1 Storage

- `%APPDATA%\GlassDock\settings.json` (user settings), `state\` (journal, window placement), `pins\`, `data\` (widget data), `logs\glassdock-YYYYMMDD.log` (retention `Logging.RetainDays`), `%LOCALAPPDATA%\GlassDock\cache\` (icons, shaders).
- `SettingsStore`: immutable `DockSettings` record tree, `System.Text.Json` source generation (`SettingsJsonContext`), `Update(Func<DockSettings, DockSettings>)` → raises `Changed` with old/new → debounced save (`Settings.SaveDebounceMs`, 500) via temp file + `File.Replace` (keeps `settings.json.bak`). Unknown properties are preserved (`JsonExtensionData`) so a newer file survives an older build. A file that fails to parse is renamed `settings.corrupt-<timestamp>.json`, defaults are loaded, and the user is told — never silently reset.
- `schemaVersion` + `SettingsMigrator` (ordered `IMigration` steps, each unit-tested with a fixture file).

### 10.2 Schema (v1, defaults shown)

```jsonc
{
  "schemaVersion": 1,
  "general":   { "launchAtStartup": true, "language": "system", "checkForUpdates": true },
  "taskbar":   { "hideNative": true, "hideOnMonitorsWithoutDock": false, "restoreOnSessionEnd": true },
  "monitors":  { "mode": "primary", "selected": [] },
  "dock": {
    "edge": "bottom", "shape": "pill", "alignment": "center",
    "size": "M", "iconSizeDip": 40, "edgeGapDip": 8, "itemSpacingDip": 6, "paddingDip": 8,
    "maxWidthPercent": 92, "cornerRadiusDip": null,
    "reserveSpace": true, "reserveGapDip": 6, "locked": false, "showLabelsOnHover": true
  },
  "autoHide":   { "enabled": false, "revealDelayMs": 80, "hideDelayMs": 600, "showOnWinKey": true, "winKeyLingerMs": 1200 },
  "fullscreen": { "hideDock": true, "revealOnEdge": false },
  "appearance": {
    "style": "liquidGlass", "accentColor": null, "theme": "system",
    "glass": { "assumeStaticWallpaper": true, "allowCaptureBackdrop": false,
               "lens": { "bezelDip": 10, "refractionDip": 14, "frost": 0.25 } },
    "magnification": { "enabled": false, "maxScale": 1.6, "rangeItems": 2.5 },
    "animations": { "enabled": true, "speed": 1.0, "attentionStyle": "bounce" }
  },
  "power":     { "reduceOnBattery": true },
  "shortcuts": { "winNumberMode": "dock", "interceptWinT": true, "interceptWinB": true, "suspendHookInFullscreen": false },
  "apps": {
    "groupWindows": true, "showAllDesktops": false, "windowScope": "all",
    "multiWindowClick": "preview", "showRunningIndicator": true, "showBadges": true, "activeIndicatorColor": "accent",
    "identityOverrides": [],
    "items": [
      { "id": "a1", "kind": "app",    "aumid": null, "shortcut": "pins\\a1.lnk", "target": "...", "displayName": "..." },
      { "id": "g1", "kind": "group",  "displayName": "Dev", "children": [ { "id": "a2", "kind": "app", ... }, { "id": "a3", ... } ] },
      { "id": "f1", "kind": "folder", "path": "C:\\Users\\...\\Downloads", "sort": "date" }
    ]
  },
  "preview":   { "enabled": true, "openDelayMs": 350, "closeGraceMs": 250 },
  "tray":      { "promoted": [], "hidden": [] },
  "tiles": [
    { "id": "t-start", "type": "start", "settings": { "image": null } },
    { "id": "t-apps",  "type": "apps" },
    { "id": "t-sp",    "type": "spacer" },
    { "id": "t-tray",  "type": "tray" },
    { "id": "t-clock", "type": "clock", "settings": { "hour12": false, "showSeconds": false, "showDate": true } },
    { "id": "t-notif", "type": "notifications" },
    { "id": "t-desk",  "type": "showDesktop" }
  ],
  "diagnostics": { "logLevel": "info" }
}
```

### 10.3 Settings UI

- `SettingsWindow`: WPF with the .NET Fluent theme (`ThemeMode`, still flagged experimental — suppress `WPF0001` in one place with a comment), Mica backdrop. Layout like macOS System Settings: left sidebar (search box + category list with icons), right pane of grouped cards (`SettingsCard` control: title, description, control on the right).
- Pages: General · Appearance (style gallery with live thumbnails, accent, glass sliders, animations, magnification) · Dock (edge, shape, size, spacing, reserve space, auto-hide, lock) · Apps (grouping, click behaviour, previews, badges) · Widgets (list + picker) · Tray & Clock · Shortcuts (mode selector with the elevated-window caveat) · Displays · Advanced (restore taskbar now, reset, open log folder, diagnostics counters: re-hide count, hook reinstalls, glass tier in use) · About.
- Every control binds through `SettingsStore.Update`; all changes apply live. v0.1 ships only General, Appearance, Dock, Advanced.
- As built: 모양 · 도크 (with 숨기기, 최대화한 창, 시계 sections) · 앱 · 위젯 · 단축키 · 일반 · 고급. Wording is 해요체 and says what the user sees, not how it works. A card that has no effect while another setting is off (확대 정도, 도크 자리에서 나오기, 화면 끝과의 간격 in bar shape, 공간 확보 and the band with auto-hide) is greyed out and follows changes live (`DependsOn`). 고급 holds the technical cards (taking over Explorer's hotkeys, hook suspension, restore on session end) and 문제 보고 파일 만들기 (`ProblemReport`: the last `Settings.ReportLogDays` (3) days of logs plus settings.json in one zip on the Desktop). The accent box saves as soon as it holds a valid #RRGGBB and marks anything else red.
- Launch at startup: `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` value `GlassDock` = quoted exe path plus `--at-logon` (`StartupRegistration`); removed on uninstall. With `--at-logon` the dock waits up to 60 s for Explorer's taskbar before starting, and if a dock is already running it exits quietly instead of opening settings. The watchdog does not relaunch the dock while Windows is signing out or shutting down (`SM_SHUTTINGDOWN`). Docks started from the Claude desktop app run inside its MSIX container, whose HKCU and AppData writes Windows never sees at sign-in; test there, but start the everyday dock from Explorer. A Debug build leaves the value alone: its apphost needs the side-by-side SDK's `DOTNET_ROOT`, which a sign-in launch lacks, so pointing it there would leave the next boot without a dock.

### 10.4 Updates

Friends get builds from the public release repository (`Mojjiman/GlassDock` on GitHub), so `UpdateService` checks it:
- **Check.** It runs `Update.FirstCheckDelaySeconds` (60) after start, then every `Update.CheckIntervalHours` (6), only while `general.checkForUpdates` is on (default on). Settings → 일반 → 업데이트 also checks on demand. It reads `GET /repos/Mojjiman/GlassDock/releases/latest`, unauthenticated, with a User-Agent. The tag (`v0.1.1`) is compared with the assembly version as major.minor.build, and the asset named `GlassDock-<ver>-win-x64.zip` (from `build/publish.ps1`) is picked. `GLASSDOCK_UPDATE_API` points the check at a local server for testing.
- **Offer.** The newer version is offered once per session in `UpdateFlyout` above the dock: 지금 업데이트 / 변경 내용 / 나중에. It never opens over a fullscreen app; if one comes to the front, it closes and returns when fullscreen ends. Unlike other flyouts it does not close on deactivation, because it opens while another app has the focus. While a release is pending, the dock also carries an accent "⬆ 업데이트" pill (`UpdateTile`) in front of the clock. It is not a tile spec and does not join tile drags. Clicking it opens the flyout. Right-clicking it offers 지금 업데이트 / 변경 내용 보기 / 이 버전 건너뛰기; skipping stores `general.skippedUpdate`, which hides the pill, flyout and menu item for that version only. The dock menu shows "GlassDock <ver>로 업데이트" and the settings card shows the same choice.
- **Install.** Download to `%TEMP%`. Verify SHA-256 against the asset's `digest` from the API; a mismatch aborts. Extract `GlassDock.exe` to `GlassDock.exe.new`. Rename the running exe to `GlassDock.exe.old`: Windows allows renaming a running image but not overwriting it. Move `.new` into place, start it with `--wait-pid <pid>`, then quit, which restores the taskbar. The new process waits for the old one before taking the single-instance mutex. It deletes `.old` at start and once more `Update.CleanUpRetrySeconds` later, since the old watchdog can still hold it. Settings live in `%APPDATA%` and are untouched.
- **Limits.** Only a single-file publish can install: a Debug build is an apphost plus `GlassDock.dll`, so swapping the exe would mismatch them; it can still check. The first build with the updater has to be installed by hand.
- **Releasing.** Every release goes out in this order:
  1. Bump `<Version>` in `Directory.Build.props`, merge it on Gitea, and run `build/publish.ps1`.
  2. `gh release create v<ver>` on `Mojjiman/GlassDock` with `artifacts/GlassDock-<ver>-win-x64.zip` and Korean notes.
  3. Sync the source into the same GitHub repository (local clone `C:\GimojjiMan\Projects\GlassDock-releases`), since friends build and change it from there. Replace the tracked source (everything except `README.md`, `assets/` and `.github/`, which hold the friend README, images and the Korean issue forms) with `git archive` of the release commit. Commit as `Mojjiman <214612300+Mojjiman@users.noreply.github.com>` so no personal address is published. Open a branch and PR, check that a clean copy builds, then merge.
  4. Confirm `releases/latest` returns the new tag and an asset `digest`.

---

## 11. Milestones and acceptance tests

Verification helpers go in `build\verify.ps1` (P/Invoke one-liners: `IsWindowVisible(realTray)`, `ABM_GETSTATE`, work-area rect via `GetMonitorInfo`, process memory/CPU sampling). "Script" below means that file; "Manual" means a human check.

### M0 — Toolchain and spikes (do first; results recorded in `docs\experiments.md`)

| ID | Question | Pass criterion | If it fails |
|---|---|---|---|
| E1 | Does `ABM_SETSTATE(ABS_AUTOHIDE)` from our process work on 26200? | `ABM_GETSTATE` & 1 and `rcWork == rcMonitor` within 1 s; restoring returns both | Prompt user to enable auto-hide via `ms-settings:taskbar`; keep our hide logic |
| E2 | Hide strategies on the real `Shell_TrayWnd`: (a) `SWP_HIDEWINDOW` — count `EVENT_OBJECT_SHOW` while touching the bottom edge, pressing Win, Win+T, Win+B, flashing a window, changing resolution; (b) `WS_EX_LAYERED\|TRANSPARENT` + alpha 0 — screenshot diff of the taskbar strip, clicks pass through | (a) stays hidden or re-hide < 1 frame visible; (b) strip pixels equal wallpaper | (b) fails → rely on (a) + event re-hide only; note possible 1-frame flash |
| E3 | With taskbar hidden and a topmost fake `Shell_TrayWnd`: do native Win, Win+X, Win+A, Win+N, Win+D, Win+E, Win+R, Win+V, Win+Tab, Alt+Tab, Win+1, Win+Shift+1 still work? | all functional | Any failure is a blocker for that shortcut → investigate before M1 |
| E4 | Translation hook: Win+1 → Win+F13 hotkey | Start never opens on release (50 tries); `WM_HOTKEY` handler activates another app 20/20; with elevated window focused, native Win+1 fires | Direct dispatch + `ForegroundActivator` chain; consider `"exclusive"` mode |
| E5 | Host backdrop brush on a never-activated `NOREDIRECTIONBITMAP` popup with `DWMWA_USE_HOSTBACKDROPBRUSH` | live blur, antialiased rounded clip, stays live when other apps are focused; note behaviour with Transparency off / Energy Saver | `AccentPolicyGlass` |
| E6 | `DeferWindowPos` moving glass + content together at 60 fps | no visible separation | fade instead of slide |
| E7 | Parse `Taskband\Favorites` | order equals the visible taskbar order, including a packaged-app pin | folder-scan import only |
| E8 | DWM thumbnail into (i) layered WPF window, (ii) non-layered backdrop WPF window | thumbnail renders, updates live | use whichever works (design assumes ii) |
| E9 | ManagedShell tray on 26200 | existing icons appear after start; left/right/double click work for 5 common apps and one elevated app; an app flyout opens next to our icon | port/patch TrayService |
| E10 | Baseline cost | empty layered WPF window + ManagedShell services ≤ 110 MB private; magnification mock ≤ 8 % CPU | reduce (drop WinForms-dependent parts, trim caches) or revisit budget with the user |
| E11 | ps_3_0 `ShaderEffect` in a layered window, runtime `D3DCompile` | correct premultiplied output, compile < 200 ms, cached | G2 deferred; Liquid = G1 |

### v0.1 — MVP

Scope: taskbar hide/restore/journal/watchdog; one dock on the primary monitor, bottom or top (via settings), pill or bar, sizes S/M/L; appbar reservation; Apps tile (pin import, pinned + running, grouping, click semantics, running/active indicators, drag-reorder, drop-to-pin, basic context menu, name tooltips, attention flash); single-window preview without close button; Start, Tray (flyout grid with click forwarding), Clock, Notifications tiles; Win+1..9/0 family, Win+T, Win+B; fullscreen hide; styles Black, White, Clear, iOS Glass/Midnight (G1 or accent fallback); settings JSON + minimal settings window; launch at startup; logging.

| # | Acceptance test | How |
|---|---|---|
| 1 | Start GlassDock: native taskbar not visible; pointer at every screen edge for 5 s, press Win, Win+T, Win+B, flash a window — native taskbar never appears | Manual + Script (`IsWindowVisible` sampled at 20 Hz for 30 s = always false) |
| 2 | Work area = monitor minus dock strip; a maximized window ends `reserveGap` above the pill; with dock on top, title bars start below it | Script (rcWork) + Manual |
| 3 | Quit from the dock menu: taskbar visible, auto-hide flag back to its original value, work area original, journal deleted | Script |
| 4 | `taskkill /f` the dock: taskbar restored within 2 s, dock relaunched by the watchdog; kill it 3 times in 60 s → stays down, taskbar restored, message shown | Script |
| 5 | Kill dock **and** watchdog, start GlassDock again: stale journal recovered (log line), then normal start | Script |
| 6 | Restart Explorer (`taskkill /f /im explorer.exe`, start it): dock survives, native taskbar hidden again within 2 s, work area correct, tray icons repopulate | Manual |
| 7 | Win+1..9 act on the dock's 1st..9th button: launch when closed, switch when open, minimize when already active; Win+Shift+N opens a new instance; Start menu never opens as a side effect (20 mixed presses) | Manual |
| 8 | Win, Win+X, Win+A, Win+N, Win+D, Win+E, Win+R, Win+V, Win+L, Win+Tab, Alt+Tab, Win+arrows, Win+Shift+S all behave as without GlassDock | Manual checklist |
| 9 | Win+T focuses the dock, arrows move, Enter activates, Esc returns focus to the previous window; Win+B opens the tray flyout with keyboard focus | Manual |
| 10 | With an elevated window focused, Win+1 still does *something sensible* (native) and nothing hangs | Manual |
| 11 | Apps: open/close Notepad ×3, Terminal, a Store app, an Electron app — buttons appear/disappear correctly, grouped per app, correct hi-res icons, correct active indicator | Manual |
| 12 | Drag an `.exe` and a `.lnk` from Explorer onto the dock → pinned at the drop position; reorder by drag persists across restart | Manual + Script (settings.json) |
| 13 | Tray flyout lists third-party icons; left, right, double click reach the app; an app's own flyout appears adjacent to the dock | Manual |
| 14 | Fullscreen video in a browser and a fullscreen game: dock hidden within 0.5 s, back within 0.5 s after leaving | Manual |
| 15 | Glass: drag a window under a G1 dock → live blur of it; "Clear" shows it unblurred; turning off Windows "Transparency effects" switches to solid | Manual |
| 16 | Idle 60 s: CPU < 0.5 %, private memory ≤ 150 MB; watchdog ≤ 25 MB | Script |
| 17 | Logs contain no unhandled exception; forcing a failure (e.g. unreadable `.lnk`) produces a log line, not a crash or silence | Script + Manual |

### v0.2 — Daily-driver polish

Scope: multi-window previews (title, close, live thumbnails); overlay badges, progress; magnification; auto-hide (edge + Win key); multi-monitor (`all`, per-monitor window scope); unpin by drag-off; full settings UI; widget framework + CPU, Memory, Network, Battery, Volume, Now Playing; Saver/Suspended power states.

Acceptance: previews show every window of an app live and close works; a Telegram/Discord unread overlay appears and clears; auto-hide reveals on edge and on Win, hides on leave, and never leaves a strip; second monitor gets its own dock and its native taskbar is hidden, unplug/replug handled; each widget matches Task Manager / the system mixer within tolerance; with the display off or session locked, CPU is ~0 %; CPU with magnification active < 8 %.

### v0.3 — Parity features

Scope: G2 Liquid Glass with automatic G1 fallback; stacks (folders); app groups; jump lists (Recent/Frequent + tasks where parseable); widget picker with categories/search; Weather, Timer/Pomodoro, Todo, Notes, Recycle bin, Recently opened, calendar flyout, world/flip/analog clocks; all six appearance styles; custom accent.

Acceptance: Liquid shows visible bezel refraction of the wallpaper and switches to live blur within 200 ms when a window is dragged behind, back when clear; wallpaper change/slideshow updates the lens; at rest GPU/CPU cost indistinguishable from Solid; dropping app on app creates a group that survives restart; a pinned Downloads folder opens a stack that updates when a file is added; right-click on Notepad/Explorer/Terminal shows their recent items and tasks.

### v0.4 — Layout freedom and system controls

Scope: left/right edges; drag dock to snap to an edge; drag-resize; lock dock; custom Start image; per-app mixer and output switch; quick toggles; brightness; Do Not Disturb if a safe mechanism is found.

### v0.5 — Extensibility and distribution

Scope: optional G3 live refraction (capture) behind `glass.allowCaptureBackdrop`; web-service widgets with credential storage; plugin SDK decision; installer + uninstaller that calls `--restore-taskbar`; code signing; Windows 10 pass (accent glass, no host backdrop).

---

## 12. Risks and open questions

### Risks

| Risk | Impact | Mitigation |
|---|---|---|
| A Windows update changes how/when Explorer re-shows the taskbar, or ignores `ABM_SETSTATE` (already reported by RetroBar users) | Peek/flicker, or doubled work-area reservation | Layered defence (auto-hide + hide + alpha), verification step with user-visible fallback, re-hide counter in diagnostics, E1/E2 re-run script after each feature update |
| Fake `Shell_TrayWnd` confuses another tool or a future Explorer build | Tray icons missing, odd flyout positions | Everything unhandled is forwarded; `Suspend()` switch; tray tile can be disabled entirely without affecting the rest |
| Low-level hook removed by timeout, or blocked by UIPI / anti-cheat | Win+N silently reverts to native order | Real hotkeys taken over from Explorer by default (no hook involved), periodic re-install, no work in callback, unhook in fullscreen |
| F13–F24 translation collides with a user's own macros | Private hotkey registration fails | Per-slot fallback to direct dispatch; log; make the private key range configurable in `AppConstants`/advanced settings |
| WPF memory exceeds the 150 MB budget once widgets and icon caches fill | Budget miss | E10 baseline early; caps on caches; on-demand settings window; GC conserve setting; worst case re-negotiate budget with the user rather than hide it |
| Layered-window rendering cost at 4K / 200 % with magnification | Stutter | Window limited to pill + headroom; reduce scaling quality during animation; magnification is opt-in |
| Host backdrop brush unavailable or black in some configurations | No G1 | `AccentPolicyGlass` fallback, then Solid |
| `ShaderEffect` falls back to software (RDP, old GPU) | No G2 | Capability check → G1/G0 |
| Process-level AppUserModelIDs cannot be read | Pin and running app shown separately for some apps | `PinMatcher` heuristics, overrides, pin-from-running |
| Jump-list file hashing / format differs from what is documented by third parties | Tasks missing for some apps | Recent/Frequent via documented API always works; degrade quietly per app, log |
| Unsigned exe with a keyboard hook | SmartScreen / AV warnings | Signing in v0.5; hook code small and auditable; no network use in v0.1 |
| Auto-hide flag persists after power loss with GlassDock not starting | User left with an auto-hide taskbar | Journal recovery on next start, `--restore-taskbar`, README note, uninstaller hook |
| Dependency on ManagedShell (single-maintainer project, WinForms use) | Bus factor, memory | Adapter interfaces confine it to three files; Apache-2.0 allows vendoring |

### Open questions for the user or the reviewer

1. **Dependencies:** approve CsWin32, ManagedShell, xunit now; Vortice later only if G3 is wanted?
2. **.NET 10 SDK** to `C:\GimojjiMan\Devs\dotnet\` — OK? Ship self-contained single exe (~80 MB) or framework-dependent?
3. **Win+number semantics:** "Nth button of the dock", taken over from Explorer by default (decided 2026-11-16, section 3.3).
4. **Monitors without a dock:** leave their native taskbar as auto-hide (current design) or hide it too?
5. **Restore at shutdown:** default restores the taskbar on session end (safe, brief native taskbar at next logon). Prefer leaving it hidden across reboots when "launch at startup" is on?
6. **Liquid Glass fidelity:** is "refracted wallpaper, live frosted blur when a window is behind" acceptable for v0.3, with capture-based live refraction (dock invisible in screenshots while active) as a later opt-in?
7. **Network widgets:** weather provider and any other outbound calls need sign-off; v0.1/v0.2 make none.
8. **Temperature and Do Not Disturb** have no clean unprivileged API; defer, or accept an admin helper / undocumented interface?
9. **Windows 10:** nice-to-have only — confirm it may slip to v0.5.
10. **UWP badge counts** cannot be read by third parties; acceptable that only overlay-icon badges (Telegram, Discord, etc.) are shown?

---

## Sources

- ManagedShell (Apache-2.0): https://github.com/cairoshell/ManagedShell — `src/ManagedShell.AppBar/ExplorerHelper.cs`, `src/ManagedShell.WindowsTray/TrayService.cs`, `src/ManagedShell.WindowsTasks/TasksService.cs`; NuGet https://www.nuget.org/packages/ManagedShell
- RetroBar: https://github.com/dremin/RetroBar — `RetroBar/Utilities/HotkeyManager.cs`, `TrayHotkey.cs`; release notes https://github.com/dremin/RetroBar/releases/latest
- WinUI AcrylicBrush source (host backdrop is pre-blurred): https://github.com/microsoft/microsoft-ui-xaml/blob/winui2/main/dev/Materials/Acrylic/AcrylicBrush.cpp
- Microsoft Q&A, blur-behind with the WinRT visual layer in Win32/WPF (black host backdrop, two-window workaround): https://learn.microsoft.com/en-us/answers/a/152907
- `DWMWINDOWATTRIBUTE` (`DWMWA_USE_HOSTBACKDROPBRUSH`, system backdrop, corner preference): https://learn.microsoft.com/en-us/windows/win32/api/dwmapi/ne-dwmapi-dwmwindowattribute
- `SetWindowDisplayAffinity` / `WDA_EXCLUDEFROMCAPTURE`: https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setwindowdisplayaffinity
- `ICustomDestinationList` (write-only): https://learn.microsoft.com/en-us/windows/win32/api/shobjidl_core/nn-shobjidl_core-icustomdestinationlist
- `Shell_NotifyIcon` internals (WM_COPYDATA, UIPI filter): https://www.geoffchappell.com/studies/windows/shell/shell32/api/shlnot/notifyicon.htm
- AutoHotkey menu mask key: https://www.autohotkey.com/docs/v2/lib/A_MenuMaskKey.htm
- Firefox taskbar pinning notes on `IPinnedList3` (24H2 behaviour): https://searchfox.org/firefox-main/source/browser/components/shell/rust/shell_windows/src/taskbar
- Jump list file formats: https://forensics.wiki/jump_lists/
- .NET support policy (.NET 10 LTS to 2028-11-14): https://dotnet.microsoft.com/platform/support/policy/dotnet-core
- Reference product (feature and behaviour statements only; no assets or naming reused): https://lumabar.xyz
