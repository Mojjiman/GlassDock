using System.Windows.Media;
using GlassDock.Config;
using GlassDock.Logging;
using Microsoft.Win32;

namespace GlassDock.Glass;

/// <summary>Resolved appearance for one style preset (design §4.2).</summary>
public sealed record GlassLook(
    string Style,
    bool UseBackdrop,
    Color Tint,
    Color RimLight,
    Color RimDark,
    Color Specular,
    Color Foreground,
    Color Accent,
    bool IsDark)
{
    /// <summary>Refract the wallpaper (tier G2); <see cref="UseBackdrop"/> is the fallback while a window is behind the dock.</summary>
    public bool UseLens { get; init; }

    public static GlassLook Resolve(AppearanceSettings a)
    {
        bool dark = a.Theme switch { "light" => false, "dark" => true, _ => SystemIsDark() };
        var accent = ParseColor(a.AccentColor) ?? SystemAccent();
        bool effects = TransparencyEnabled();
        string style = a.Style;
        // "Transparency effects" off in Windows means the user asked for opaque surfaces.
        if (!effects && style is "liquidGlass" or "frosted" or "clear" or "midnight") style = dark ? "black" : "white";

        Color fgLight = Color.FromRgb(0xFF, 0xFF, 0xFF), fgDark = Color.FromRgb(0x1A, 0x1A, 0x1A);
        return style switch
        {
            // Mostly see-through: thin tint so the wallpaper stays visible, strong rim and specular for the glass edge.
            "liquidGlass" => new(style, true,
                dark ? Color.FromArgb(0x30, 0x10, 0x14, 0x1C) : Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF),
                Color.FromArgb(0xB0, 0xFF, 0xFF, 0xFF), Color.FromArgb(0x40, 0x00, 0x00, 0x00),
                Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF), dark ? fgLight : fgDark, accent, dark) { UseLens = true },
            "frosted" => new(style, true,
                dark ? Color.FromArgb(0x70, 0x20, 0x20, 0x24) : Color.FromArgb(0x70, 0xF4, 0xF4, 0xF6),
                Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF), Color.FromArgb(0x30, 0x00, 0x00, 0x00),
                Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF), dark ? fgLight : fgDark, accent, dark),
            "midnight" => new(style, true,
                Color.FromArgb(0x90, 0x0B, 0x14, 0x2E),
                Color.FromArgb(0x70, 0x9F, 0xB8, 0xFF), Color.FromArgb(0x50, 0x00, 0x00, 0x00),
                Color.FromArgb(0x28, 0xFF, 0xFF, 0xFF), fgLight, accent, true),
            // No blur at all: the background shows through untouched. Alpha stays >= 1 so the pill is clickable.
            "clear" => new(style, false,
                dark ? Color.FromArgb(0x22, 0x00, 0x00, 0x00) : Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF),
                Color.FromArgb(0xA0, 0xFF, 0xFF, 0xFF), Color.FromArgb(0x50, 0x00, 0x00, 0x00),
                Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF), dark ? fgLight : fgDark, accent, dark),
            "white" => new(style, false,
                Color.FromArgb(0xF2, 0xFA, 0xFA, 0xFA), Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF), Color.FromArgb(0x30, 0x00, 0x00, 0x00),
                Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), fgDark, accent, false),
            _ /* black */ => new("black", false,
                Color.FromArgb(0xEE, 0x16, 0x16, 0x18), Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF), Color.FromArgb(0x60, 0x00, 0x00, 0x00),
                Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), fgLight, accent, true),
        };
    }

    private static bool SystemIsDark()
    {
        // The taskbar follows the *system* theme, not the app theme.
        var v = ReadDword(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "SystemUsesLightTheme");
        return v is 0;
    }

    private static bool TransparencyEnabled() =>
        ReadDword(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "EnableTransparency") is not 0;

    private static int? ReadDword(string key, string name)
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(key);
            return k?.GetValue(name) is int i ? i : null;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
        {
            Log.Warn($"Reading HKCU\\{key}\\{name} failed", ex);
            return null;
        }
    }

    private static Color SystemAccent()
    {
        try
        {
            var c = new Windows.UI.ViewManagement.UISettings().GetColorValue(Windows.UI.ViewManagement.UIColorType.Accent);
            return Color.FromRgb(c.R, c.G, c.B);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidCastException)
        {
            Log.Warn("Reading the Windows accent colour failed; using default blue", ex);
            return Color.FromRgb(0x00, 0x78, 0xD4);
        }
    }

    public static Color? ParseColor(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return null;
        try { return (Color)ColorConverter.ConvertFromString(hex); }
        catch (FormatException ex) { Log.Warn($"Invalid accent colour '{hex}'", ex); return null; }
    }
}
