using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Shell;
using GlassDock.Config;
using GlassDock.Glass;
using GlassDock.Input;
using GlassDock.Logging;
using GlassDock.Shell;
using static GlassDock.Interop.Native;
using F = GlassDock.Config.AppConstants.Flyouts;

namespace GlassDock.UI;

/// <summary>
/// Base for popups next to the dock (tray grid, window previews). Non-layered with the native
/// acrylic system backdrop: cheap, rounded by DWM, and DWM thumbnails can render into it.
/// Closes when it loses activation.
/// </summary>
public class FlyoutWindow : Window
{
    protected readonly DockWindow Dock;
    protected readonly GlassLook Look;
    private RECT _anchor;
    protected nint Hwnd { get; private set; }
    public event Action? FlyoutClosed;
    /// <summary>Set during an OLE drag out of the flyout: the drop target takes activation, which must not close us mid-drag.</summary>
    protected bool KeepOpenWhileDeactivated { get; set; }

    public FlyoutWindow(DockWindow dock)
    {
        Dock = dock;
        Look = dock.Look;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        SizeToContent = SizeToContent.WidthAndHeight;
        Background = Brushes.Transparent;
        Foreground = new SolidColorBrush(Look.IsDark ? Colors.White : Color.FromRgb(0x1A, 0x1A, 0x1A));
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
        UseLayoutRounding = true;
        Left = -32000; // shown off-screen first, moved once its size is known
        WindowChrome.SetWindowChrome(this, new WindowChrome
        {
            GlassFrameThickness = new Thickness(-1),
            CaptionHeight = 0,
            ResizeBorderThickness = new Thickness(0),
            UseAeroCaptionButtons = false,
            CornerRadius = new CornerRadius(0),
        });
        Deactivated += (_, _) => { if (!KeepOpenWhileDeactivated) SafeClose(); };
        KeyDown += (_, e) => { if (e.Key == Key.Escape) SafeClose(); };
        Closing += (_, _) => _closing = true;
        Closed += (_, _) => FlyoutClosed?.Invoke();
    }

    private bool _closing;

    protected static readonly FontFamily IconFont = new("Segoe Fluent Icons, Segoe MDL2 Assets");

    private Color ForegroundColor => ((SolidColorBrush)Foreground).Color;

    /// <summary>
    /// Wraps the flyout's content in a deep tint. The system acrylic alone turns a flat mid-grey over light
    /// windows; the tint keeps a hint of the blur while reading as dark (or bright) glass. DWM clips it to
    /// the rounded corners.
    /// </summary>
    protected Border Tinted(UIElement content)
    {
        var (top, bottom) = Look.IsDark
            ? (AppConstants.Flyouts.TintDarkTop, AppConstants.Flyouts.TintDarkBottom)
            : (AppConstants.Flyouts.TintLightTop, AppConstants.Flyouts.TintLightBottom);
        return new Border { Background = new LinearGradientBrush(top, bottom, 90), Child = content };
    }

    /// <summary>A round glyph button: borderless for flyout headers, or <paramref name="filled"/> with a soft disc.</summary>
    protected Button IconButton(string glyph, string tip, Action click,
        double size = AppConstants.Flyouts.IconButtonDip, double glyphSize = AppConstants.Flyouts.IconButtonGlyphSize, bool filled = false)
    {
        var fg = ForegroundColor;
        var hover = new SolidColorBrush(Color.FromArgb(filled ? AppConstants.Flyouts.FilledButtonHoverAlpha : AppConstants.Flyouts.IconButtonHoverAlpha, fg.R, fg.G, fg.B));
        Brush rest = filled ? new SolidColorBrush(Color.FromArgb(AppConstants.Flyouts.FilledButtonAlpha, fg.R, fg.G, fg.B)) : Brushes.Transparent;
        var face = new Border
        {
            Width = size,
            Height = size,
            CornerRadius = new CornerRadius(size / 2),
            Background = rest,
            Child = new TextBlock
            {
                Text = glyph,
                FontFamily = IconFont,
                FontSize = glyphSize,
                Foreground = filled ? Foreground : new SolidColorBrush(Color.FromArgb(AppConstants.Flyouts.IconButtonGlyphAlpha, fg.R, fg.G, fg.B)),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        var template = new ControlTemplate(typeof(Button)) { VisualTree = new FrameworkElementFactory(typeof(ContentPresenter)) };
        var b = new Button { Content = face, Template = template, ToolTip = tip, Cursor = Cursors.Hand, VerticalAlignment = VerticalAlignment.Center, Focusable = false };
        b.MouseEnter += (_, _) => face.Background = hover;
        b.MouseLeave += (_, _) => face.Background = rest;
        b.Click += (_, _) => click();
        return b;
    }

    /// <summary>
    /// A vertical scroller with a slim pill instead of the theme's scrollbar (arrows and a wide track
    /// look heavy in a small glass flyout). The pill sits in a gutter to the right of the content.
    /// </summary>
    protected ScrollViewer SlimScrollViewer(UIElement content, double maxHeight) => new()
    {
        Content = content,
        MaxHeight = maxHeight,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        Template = SlimScrollTemplate(ForegroundColor),
    };

    /// <summary>The template behind <see cref="SlimScrollViewer"/>; the pill is <paramref name="fg"/> at low alpha.</summary>
    internal static ControlTemplate SlimScrollTemplate(Color fg)
    {
        string rest = Color.FromArgb(F.ScrollThumbAlpha, fg.R, fg.G, fg.B).ToString();
        string hover = Color.FromArgb(F.ScrollThumbHoverAlpha, fg.R, fg.G, fg.B).ToString();
        string xaml = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"""
            <ControlTemplate TargetType="ScrollViewer" xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
              <Grid>
                <ScrollContentPresenter x:Name="PART_ScrollContentPresenter" Margin="0,0,{F.ScrollGutterDip},0" CanContentScroll="{"{"}TemplateBinding CanContentScroll{"}"}" />
                <ScrollBar x:Name="PART_VerticalScrollBar" Orientation="Vertical" HorizontalAlignment="Right" Width="{F.ScrollGutterDip}"
                           Maximum="{"{"}TemplateBinding ScrollableHeight{"}"}" ViewportSize="{"{"}TemplateBinding ViewportHeight{"}"}"
                           Value="{"{"}TemplateBinding VerticalOffset{"}"}" Visibility="{"{"}TemplateBinding ComputedVerticalScrollBarVisibility{"}"}">
                  <ScrollBar.Template>
                    <ControlTemplate TargetType="ScrollBar">
                      <Grid Background="Transparent">
                      <Track x:Name="PART_Track" IsDirectionReversed="True">
                        <Track.Thumb>
                          <Thumb MinHeight="{F.ScrollThumbMinDip}" Cursor="Hand">
                            <Thumb.Template>
                              <ControlTemplate TargetType="Thumb">
                                <Grid Background="Transparent">
                                  <Border x:Name="Pill" Width="{F.ScrollThumbDip}" CornerRadius="{F.ScrollThumbDip / 2}" Background="{rest}" HorizontalAlignment="Center" />
                                </Grid>
                                <ControlTemplate.Triggers>
                                  <Trigger Property="IsMouseOver" Value="True">
                                    <Setter TargetName="Pill" Property="Width" Value="{F.ScrollThumbHoverDip}" />
                                    <Setter TargetName="Pill" Property="CornerRadius" Value="{F.ScrollThumbHoverDip / 2}" />
                                    <Setter TargetName="Pill" Property="Background" Value="{hover}" />
                                  </Trigger>
                                  <Trigger Property="IsDragging" Value="True">
                                    <Setter TargetName="Pill" Property="Width" Value="{F.ScrollThumbHoverDip}" />
                                    <Setter TargetName="Pill" Property="CornerRadius" Value="{F.ScrollThumbHoverDip / 2}" />
                                    <Setter TargetName="Pill" Property="Background" Value="{hover}" />
                                  </Trigger>
                                </ControlTemplate.Triggers>
                              </ControlTemplate>
                            </Thumb.Template>
                          </Thumb>
                        </Track.Thumb>
                      </Track>
                      </Grid>
                    </ControlTemplate>
                  </ScrollBar.Template>
                </ScrollBar>
              </Grid>
            </ControlTemplate>
            """);
        return (ControlTemplate)XamlReader.Parse(xaml);
    }

    protected static void SetGlyph(Button iconButton, string glyph) => ((TextBlock)((Border)iconButton.Content).Child).Text = glyph;

    /// <summary>The flyout's text colour at <paramref name="alpha"/>, for muted text and slider tracks.</summary>
    protected SolidColorBrush ForegroundAlpha(byte alpha)
    {
        var fg = ForegroundColor;
        return new SolidColorBrush(Color.FromArgb(alpha, fg.R, fg.G, fg.B));
    }

    /// <summary>A flat text button for flyout headers.</summary>
    protected Button LinkButton(string text, Action click)
    {
        var b = new Button
        {
            Content = text,
            Padding = new Thickness(8, 3, 8, 3),
            Margin = new Thickness(6, 0, 0, 0),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = Foreground,
            Cursor = Cursors.Hand,
            FontSize = 12,
        };
        b.Click += (_, _) => click();
        return b;
    }

    /// <summary>Close that tolerates re-entry (hiding a window deactivates it, which closes it).</summary>
    public void SafeClose()
    {
        if (_closing) return;
        _closing = true;
        Close();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        Hwnd = new WindowInteropHelper(this).Handle;
        var src = HwndSource.FromHwnd(Hwnd);
        src.CompositionTarget.BackgroundColor = Colors.Transparent;
        int ex = (int)GetWindowLongPtr(Hwnd, GWL_EXSTYLE);
        SetWindowLongPtr(Hwnd, GWL_EXSTYLE, ex | WS_EX_TOOLWINDOW);
        int dark = Look.IsDark ? 1 : 0;
        DwmSetWindowAttribute(Hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
        int backdrop = DWMSBT_TRANSIENTWINDOW;
        DwmSetWindowAttribute(Hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref backdrop, sizeof(int));
        int corner = DWMWCP_ROUND;
        DwmSetWindowAttribute(Hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));
    }

    /// <summary>Opens the flyout centred on <paramref name="anchor"/> (physical px), away from the dock edge.</summary>
    public void ShowAt(RECT anchor)
    {
        _anchor = anchor;
        // Start or search in front (Win, then a click on the dock) would stay on top of the flyout and
        // refuses to give up the foreground. The press on the dock already sent it Esc; if it is still
        // fading out, the flyout shows now and takes the foreground once Start lets go.
        bool launcher = ForegroundActivator.DismissLauncher();
        Dock.TakeForeground();
        Show();
        Activate();
        if (launcher) ForegroundActivator.ActivateOnceLauncherGone(Hwnd, "flyout", () => _closing);
        else if (GetForegroundWindow() != Hwnd) ForegroundActivator.Activate(Hwnd);
        Reposition();
        SizeChanged += (_, _) => Reposition();
    }

    protected void Reposition()
    {
        if (Hwnd == 0) return;
        var display = DisplayInfo.FromHandle(MonitorFromWindow(Dock.Handle, MONITOR_DEFAULTTONEAREST));
        double scale = display.Scale;
        int w = (int)Math.Ceiling(ActualWidth * scale), h = (int)Math.Ceiling(ActualHeight * scale);
        int gap = (int)(10 * scale);
        var pill = Dock.PillScreenRect;
        var b = display.Bounds;
        int x, y;
        if (Dock.Metrics.Vertical)
        {
            // Beside the dock, centred on the anchor's height.
            x = Dock.Metrics.Edge == DockEdge.Left ? pill.Right + gap : pill.Left - w - gap;
            y = Math.Clamp((_anchor.Top + _anchor.Bottom) / 2 - h / 2, b.Top + gap, Math.Max(b.Top + gap, b.Bottom - h - gap));
        }
        else
        {
            x = Math.Clamp((_anchor.Left + _anchor.Right) / 2 - w / 2, b.Left + gap, Math.Max(b.Left + gap, b.Right - w - gap));
            y = Dock.IsBottom ? pill.Top - h - gap : pill.Bottom + gap;
        }
        SetWindowPos(Hwnd, HWND_TOPMOST, x, y, w, h, SWP_NOACTIVATE);
    }
}
