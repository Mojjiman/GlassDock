using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using GlassDock.Apps;
using GlassDock.Config;
using GlassDock.Logging;
using GlassDock.Stacks;

namespace GlassDock.UI;

/// <summary>
/// A pinned folder opened from the dock (design §9.3 "Stacks"): a grid of its newest (or A→Z) items
/// that updates while open. Click opens, drag moves/copies like Explorer, right-click for more.
/// </summary>
public sealed class StackFlyout : FlyoutWindow
{
    private readonly AppItem _item;
    private readonly IconProvider _icons;
    private readonly SettingsStore _settings;
    private readonly Action<AppItem> _sortChanged;
    private readonly WrapPanel _grid = new();
    private readonly TextBlock _footer = new() { FontSize = 11.5, Opacity = 0.65, Margin = new Thickness(4, 8, 4, 0) };
    private readonly Button _sortButton;
    private readonly FolderWatcher? _watcher;
    private int _generation;
    private Point _pressAt;
    private FolderEntry? _pressed;

    public AppItem Item => _item;
    private string Folder => _item.Pin?.Path ?? "";
    private string? Sort => _item.Pin?.Sort;

    public StackFlyout(DockWindow dock, AppItem item, IconProvider icons, SettingsStore settings, Action<AppItem> sortChanged) : base(dock)
    {
        _item = item;
        _icons = icons;
        _settings = settings;
        _sortChanged = sortChanged;
        var c = AppConstants.Stacks.Columns;
        _grid.Width = c * AppConstants.Stacks.CellWidthDip;

        var title = new TextBlock { Text = item.DisplayName, FontSize = 15, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        _sortButton = LinkButton("", ToggleSort);
        var open = LinkButton("탐색기에서 열기", () => { SafeClose(); AppLauncher.Execute(Folder, null, null, null); });
        var header = new DockPanel { Margin = new Thickness(4, 0, 0, 8), LastChildFill = true };
        DockPanel.SetDock(open, System.Windows.Controls.Dock.Right);
        DockPanel.SetDock(_sortButton, System.Windows.Controls.Dock.Right);
        header.Children.Add(open);
        header.Children.Add(_sortButton);
        header.Children.Add(title);

        var scroll = new ScrollViewer
        {
            Content = _grid,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            MaxHeight = AppConstants.Stacks.VisibleRows * AppConstants.Stacks.CellHeightDip,
        };
        var root = new StackPanel { Margin = new Thickness(12, 10, 12, 10) };
        root.Children.Add(header);
        root.Children.Add(scroll);
        root.Children.Add(_footer);
        Content = root;

        if (Directory.Exists(Folder))
        {
            _watcher = new FolderWatcher(Folder, Dispatcher, AppConstants.Stacks.FlyoutRefreshDebounceMs);
            _watcher.Changed += () => _ = LoadAsync();
        }
        Closed += (_, _) => _watcher?.Dispose();
        _ = LoadAsync();
    }

    private void ToggleSort()
    {
        if (_item.Pin is not { } pin) return;
        string next = pin.Sort == FolderContents.SortName ? FolderContents.SortDate : FolderContents.SortName;
        // The pin object is the one in settings, so this also persists the choice.
        _settings.Update(_ => pin.Sort = next);
        _sortChanged(_item);
        _ = LoadAsync();
    }

    private async Task LoadAsync()
    {
        int gen = ++_generation;
        _sortButton.Content = Sort == FolderContents.SortName ? "이름순 ▾" : "최신순 ▾";
        List<FolderEntry> items;
        int total;
        try
        {
            (items, total) = await Task.Run(() => FolderContents.Read(Folder, Sort, AppConstants.Stacks.MaxItems));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            Log.Warn($"Cannot read folder {Folder}", ex);
            if (gen != _generation) return;
            _grid.Children.Clear();
            _footer.Text = Directory.Exists(Folder) ? "폴더를 읽을 수 없습니다." : "폴더를 찾을 수 없습니다.";
            return;
        }
        if (gen != _generation) return; // a newer refresh started meanwhile

        double scale = VisualTreeHelper.GetDpi(Dock).DpiScaleX;
        int px = IconProvider.SnapSize(AppConstants.Stacks.IconDip * scale);
        _grid.Children.Clear();
        foreach (var e in items) _grid.Children.Add(CreateCell(e, px));
        _footer.Text = total == 0 ? "폴더가 비어 있습니다."
            : total > items.Count ? $"항목 {total}개 중 {items.Count}개 표시 · 나머지는 탐색기에서 볼 수 있습니다."
            : $"항목 {total}개";
    }

    private IconCell CreateCell(FolderEntry e, int px)
    {
        var cell = new IconCell(e.Name, $"{e.Name}\n수정한 날짜: {e.Modified:g}", Look);
        _ = SetIconAsync(cell.Icon, e.Path, px);
        cell.MouseLeftButtonDown += (_, ev) => { _pressed = e; _pressAt = ev.GetPosition(this); };
        cell.MouseMove += (_, ev) =>
        {
            if (_pressed != e || ev.LeftButton != MouseButtonState.Pressed) return;
            var d = ev.GetPosition(this) - _pressAt;
            if (Math.Abs(d.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(d.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            _pressed = null;
            StartDrag(cell, e);
        };
        cell.MouseLeftButtonUp += (_, _) =>
        {
            if (_pressed != e) return;
            _pressed = null;
            SafeClose();
            Open(e.Path);
        };
        cell.MouseRightButtonUp += (_, ev) => { ev.Handled = true; ShowEntryMenu(cell, e); };
        return cell;
    }

    /// <summary>Opening a file waits for its app's association handler; off the UI thread so the dock stays live.</summary>
    private void Open(string path)
    {
        string folder = Folder;
        _ = AppLauncher.InBackground(() => AppLauncher.Execute(path, null, folder, null));
    }

    private async Task SetIconAsync(Image img, string path, int px)
    {
        var src = await _icons.GetAsync(path, px, thumbnail: true);
        if (src != null) img.Source = src;
    }

    /// <summary>Same as dragging from Explorer: the drop target decides move, copy or link.</summary>
    private void StartDrag(IconCell cell, FolderEntry e)
    {
        KeepOpenWhileDeactivated = true;
        try
        {
            var data = new DataObject(DataFormats.FileDrop, new[] { e.Path });
            DragDrop.DoDragDrop(cell, data, DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link);
        }
        finally { KeepOpenWhileDeactivated = false; }
        SafeClose();
    }

    private void ShowEntryMenu(IconCell cell, FolderEntry e)
    {
        var menu = new ContextMenu { PlacementTarget = cell };
        Add(menu, "열기", () => { SafeClose(); Open(e.Path); });
        Add(menu, "파일 위치 열기", () => { SafeClose(); AppLauncher.OpenFileLocation(e.Path); });
        Add(menu, "경로 복사", () =>
        {
            try { Clipboard.SetText(e.Path); }
            catch (System.Runtime.InteropServices.COMException ex) { Log.Warn("Copying the path to the clipboard failed", ex); }
        });
        menu.IsOpen = true;
    }

    private static void Add(ContextMenu menu, string header, Action action)
    {
        var mi = new MenuItem { Header = header };
        mi.Click += (_, _) => action();
        menu.Items.Add(mi);
    }
}
