using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using GlassDock.Apps;
using GlassDock.Config;
using GlassDock.Input;
using GlassDock.Logging;
using ManagedShell.WindowsTasks;
using static GlassDock.Interop.Native;

namespace GlassDock.UI;

/// <summary>One live DWM thumbnail card per window of an app; click to switch, × to close.</summary>
public sealed class PreviewFlyout : FlyoutWindow
{
    private readonly AppItem _item;
    private readonly StackPanel _cards = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(6) };
    private readonly List<(ApplicationWindow window, Border slot, nint thumb)> _thumbs = [];

    public PreviewFlyout(DockWindow dock, AppItem item) : base(dock)
    {
        _item = item;
        Content = _cards;
        Loaded += (_, _) => BuildCards();
        LayoutUpdated += (_, _) => UpdateDestinations();
        Closed += (_, _) => UnregisterAll();
    }

    private void BuildCards()
    {
        UnregisterAll();
        _cards.Children.Clear();
        foreach (var w in _item.Windows.ToList())
        {
            if (DwmRegisterThumbnail(Hwnd, w.Handle, out nint thumb) != 0) { Log.Debug($"No thumbnail for 0x{w.Handle:X}"); thumb = 0; }
            var size = new Size(AppConstants.Preview.MaxCardWidthDip, AppConstants.Preview.MaxCardHeightDip);
            if (thumb != 0 && DwmQueryThumbnailSourceSize(thumb, out var src) == 0 && src.cx > 0 && src.cy > 0)
            {
                double k = Math.Min(AppConstants.Preview.MaxCardWidthDip / src.cx, AppConstants.Preview.MaxCardHeightDip / src.cy);
                size = new Size(Math.Max(60, src.cx * k), Math.Max(40, src.cy * k));
            }
            var slot = new Border { Width = size.Width, Height = size.Height, Background = Brushes.Transparent };
            _cards.Children.Add(Card(w, slot));
            _thumbs.Add((w, slot, thumb));
        }
    }

    private Border Card(ApplicationWindow w, Border slot)
    {
        var title = new TextBlock { Text = w.Title, TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 4, 0) };
        var close = new Button
        {
            Content = new TextBlock { Text = Glyphs.Close, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 10 },
            Width = 24, Height = 24, Padding = new Thickness(0),
            Background = Brushes.Transparent, BorderThickness = new Thickness(0),
            ToolTip = "창 닫기",
        };
        close.Click += (_, e) =>
        {
            e.Handled = true;
            PostMessage(w.Handle, WM_SYSCOMMAND, SC_CLOSE, 0);
            SafeClose();
        };
        var header = new DockPanel { Height = 26, Width = slot.Width };
        DockPanel.SetDock(close, System.Windows.Controls.Dock.Right);
        header.Children.Add(close);
        if (w.Icon != null) header.Children.Add(new Image { Source = w.Icon, Width = 16, Height = 16 });
        header.Children.Add(title);

        var stack = new StackPanel();
        stack.Children.Add(header);
        stack.Children.Add(slot);
        var card = new Border { Padding = new Thickness(8, 4, 8, 8), Margin = new Thickness(2), CornerRadius = new CornerRadius(8), Child = stack, Background = Brushes.Transparent, Cursor = Cursors.Hand };
        var hover = new SolidColorBrush(Look.IsDark ? Color.FromArgb(0x28, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x1E, 0, 0, 0));
        card.MouseEnter += (_, _) => card.Background = hover;
        card.MouseLeave += (_, _) => card.Background = Brushes.Transparent;
        card.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            SafeClose();
            ForegroundActivator.Activate(w.Handle);
        };
        return card;
    }

    private void UpdateDestinations()
    {
        if (Hwnd == 0 || PresentationSource.FromVisual(this) == null) return;
        double scale = GetDpiForWindow(Hwnd) / 96.0;
        foreach (var (_, slot, thumb) in _thumbs)
        {
            if (thumb == 0 || !slot.IsVisible) continue;
            var p = slot.TransformToAncestor(this).Transform(new Point(0, 0));
            var props = new DWM_THUMBNAIL_PROPERTIES
            {
                dwFlags = DWM_TNP_RECTDESTINATION | DWM_TNP_VISIBLE | DWM_TNP_OPACITY | DWM_TNP_SOURCECLIENTAREAONLY,
                rcDestination = new RECT((int)(p.X * scale), (int)(p.Y * scale), (int)((p.X + slot.ActualWidth) * scale), (int)((p.Y + slot.ActualHeight) * scale)),
                opacity = 255,
                fVisible = true,
                fSourceClientAreaOnly = false,
            };
            DwmUpdateThumbnailProperties(thumb, ref props);
        }
    }

    private void UnregisterAll()
    {
        foreach (var (_, _, thumb) in _thumbs)
            if (thumb != 0) DwmUnregisterThumbnail(thumb);
        _thumbs.Clear();
    }
}
