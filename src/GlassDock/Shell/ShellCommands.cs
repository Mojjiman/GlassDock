using GlassDock.Input;
using static GlassDock.Interop.Native;

namespace GlassDock.Shell;

/// <summary>
/// Opens native Windows UI. Uses tagged synthetic shortcuts, which our hook ignores and Explorer's
/// hidden taskbar handles exactly as if the user typed them.
/// </summary>
public static class ShellCommands
{
    public static void OpenStart() => InputInjector.Tap(VK_LWIN);
    public static void OpenWinXMenu() => InputInjector.Chord(VK_LWIN, 'X');
    public static void OpenNotificationCenter() => InputInjector.Chord(VK_LWIN, 'N');
    public static void OpenQuickSettings() => InputInjector.Chord(VK_LWIN, 'A');
    public static void ShowDesktop() => InputInjector.Chord(VK_LWIN, 'D');
    public static void OpenSearch() => InputInjector.Chord(VK_LWIN, 'S');
    public static void OpenTaskView() => InputInjector.Chord(VK_LWIN, 0x09 /* VK_TAB */);
}
