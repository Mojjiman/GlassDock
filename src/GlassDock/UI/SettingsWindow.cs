using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using GlassDock.Config;
using GlassDock.Logging;
using GlassDock.Update;
using GlassDock.Widgets;

namespace GlassDock.UI;

/// <summary>Settings laid out like macOS System Settings: category list on the left, grouped cards on the right.</summary>
public sealed class SettingsWindow : Window
{
    private readonly AppHost _host;
    private readonly ContentControl _page = new();
    private readonly ListBox _nav = new() { BorderThickness = new Thickness(0), Background = Brushes.Transparent };

    private SettingsStore Store => _host.Settings;
    private DockSettings S => _host.Settings.Current;

    public SettingsWindow(AppHost host)
    {
        _host = host;
        Title = "GlassDock 설정";
        Width = 820;
        Height = 600;
        MinWidth = 640;
        MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");

        var pages = new (string title, string glyph, Func<UIElement> build)[]
        {
            ("모양", "", AppearancePage),
            ("도크", "", DockPage),
            ("앱", "", AppsPage),
            ("위젯", "", WidgetsPage),
            ("단축키", "", ShortcutsPage),
            ("일반", "", GeneralPage),
            ("고급", "", AdvancedPage),
        };
        foreach (var (title, glyph, build) in pages)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4, 6, 4, 6) };
            row.Children.Add(new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 16, Width = 28, VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(new TextBlock { Text = title, FontSize = 14, VerticalAlignment = VerticalAlignment.Center });
            _nav.Items.Add(new ListBoxItem { Content = row, Tag = build });
        }
        _nav.SelectionChanged += (_, _) =>
        {
            if (_nav.SelectedItem is ListBoxItem { Tag: Func<UIElement> b })
                _page.Content = new ScrollViewer { Content = b(), VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(24, 16, 24, 24) };
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(200) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        var navHost = new Border { Padding = new Thickness(8, 12, 8, 12), Child = _nav };
        grid.Children.Add(navHost);
        Grid.SetColumn(_page, 1);
        grid.Children.Add(_page);
        Content = grid;
        _nav.SelectedIndex = 0;
    }

    // ---- pages -------------------------------------------------------------------------------------

    private UIElement AppearancePage()
    {
        var p = Page("모양");
        p.Children.Add(Card("스타일", "Liquid Glass는 뒤의 배경을 거의 그대로 보여 줍니다. Clear는 흐림 없이 완전히 투명합니다.",
            Combo(S.Appearance.Style, v => Store.Update(s => s.Appearance.Style = v),
                ("liquidGlass", "Liquid Glass"), ("frosted", "iOS Glass (반투명 흐림)"), ("clear", "Clear (완전 투명)"),
                ("midnight", "Midnight"), ("black", "Black"), ("white", "White"))));
        p.Children.Add(Card("테마", "아이콘과 글자 색. 시스템은 Windows 작업 표시줄 테마를 따릅니다.",
            Combo(S.Appearance.Theme, v => Store.Update(s => s.Appearance.Theme = v), ("system", "시스템"), ("light", "밝게"), ("dark", "어둡게"))));
        p.Children.Add(Card("강조 색", "비워 두면 Windows 강조 색을 씁니다. 예: #3B82F6",
            Text(S.Appearance.AccentColor ?? "", v => Store.Update(s => s.Appearance.AccentColor = string.IsNullOrWhiteSpace(v) ? null : v.Trim()))));
        p.Children.Add(Card("애니메이션", "실행 중 튀어오르기, 나타나기 등", Toggle(S.Appearance.Animations, v => Store.Update(s => s.Appearance.Animations = v))));
        p.Children.Add(Card("확대", "마우스를 올린 앱 아이콘과 그 주변이 커집니다.", Toggle(S.Appearance.Magnification.Enabled, v => Store.Update(s => s.Appearance.Magnification.Enabled = v))));
        p.Children.Add(Card("확대 크기", "가장 커질 때의 배율", Slider(S.Appearance.Magnification.MaxScale, AppConstants.Magnify.MinScale, AppConstants.Magnify.MaxScale, v => Store.Update(s => s.Appearance.Magnification.MaxScale = Math.Round(v, 2)), step: 0.1, format: "0.0")));
        return p;
    }

    private UIElement DockPage()
    {
        var p = Page("도크");
        p.Children.Add(Card("위치", "도크의 빈 곳을 끌어서 다른 가장자리로 옮길 수도 있습니다", Combo(S.Dock.Edge, v => Store.Update(s => s.Dock.Edge = v), ("bottom", "아래"), ("top", "위"), ("left", "왼쪽"), ("right", "오른쪽"))));
        p.Children.Add(Card("형태", "알약 모양 또는 가장자리 전체를 덮는 막대", Combo(S.Dock.Shape, v => Store.Update(s => s.Dock.Shape = v), ("pill", "알약"), ("bar", "가장자리 전체"))));
        p.Children.Add(Card("크기", "아이콘 크기", Combo(S.Dock.Size, v => Store.Update(s => s.Dock.Size = v), ("S", "작게"), ("M", "보통"), ("L", "크게"))));
        p.Children.Add(Card("화면 가장자리와의 거리", "DIP 단위", Slider(S.Dock.EdgeGapDip, 0, 40, v => Store.Update(s => s.Dock.EdgeGapDip = v))));
        p.Children.Add(Card("아이콘 간격", "DIP 단위", Slider(S.Dock.ItemSpacingDip, 0, 24, v => Store.Update(s => s.Dock.ItemSpacingDip = v))));
        p.Children.Add(Card("자동 숨기기", "쓰지 않을 때는 화면 가장자리로 숨고, 포인터를 가장자리에 대거나 Win 키를 누르면 나옵니다.", Toggle(S.AutoHide.Enabled, v => Store.Update(s => s.AutoHide.Enabled = v))));
        p.Children.Add(Card("도크 자리에서 나오기", "자동 숨기기 중에 포인터를 도크가 있던 자리에 잠깐 두면 가장자리까지 내리지 않아도 나옵니다.", Toggle(S.AutoHide.RevealOnDockArea, v => Store.Update(s => s.AutoHide.RevealOnDockArea = v))));
        p.Children.Add(Card("창이 도크를 가리지 않게 공간 확보", "끄면 최대화한 창이 도크 아래까지 내려갑니다.", Toggle(S.Dock.ReserveSpace, v => Store.Update(s => s.Dock.ReserveSpace = v))));
        p.Children.Add(Card("최대화한 창 아래를 유리 띠로 채우기", "창을 최대화하면 도크 옆으로 보이던 바탕화면 자리를 화면 너비의 띠로 채웁니다. 공간 확보가 켜져 있을 때만 씁니다.", Toggle(S.Dock.FillMaximizedBand, v => Store.Update(s => s.Dock.FillMaximizedBand = v))));
        p.Children.Add(Card("마우스를 올리면 이름 표시", "", Toggle(S.Dock.ShowLabelsOnHover, v => Store.Update(s => s.Dock.ShowLabelsOnHover = v))));
        p.Children.Add(Card("도크 잠그기", "끌어서 순서 바꾸기·고정·해제를 막습니다.", Toggle(S.Dock.Locked, v => Store.Update(s => s.Dock.Locked = v))));
        p.Children.Add(Card("전체 화면 앱에서 도크 숨기기", "게임·동영상 전체 화면", Toggle(S.Fullscreen.HideDock, v => Store.Update(s => s.Fullscreen.HideDock = v))));
        return p;
    }

    private UIElement AppsPage()
    {
        var p = Page("앱");
        p.Children.Add(Card("같은 앱의 창 묶기", "", Toggle(S.Apps.GroupWindows, v => Store.Update(s => s.Apps.GroupWindows = v))));
        p.Children.Add(Card("창이 여러 개인 앱을 클릭하면", "", Combo(S.Apps.MultiWindowClick, v => Store.Update(s => s.Apps.MultiWindowClick = v), ("preview", "미리보기 열기"), ("cycle", "다음 창으로 전환"))));
        p.Children.Add(Card("실행 중 표시 점", "", Toggle(S.Apps.ShowRunningIndicator, v => Store.Update(s => s.Apps.ShowRunningIndicator = v))));
        p.Children.Add(Card("사용 중인 앱 표시 막대 색", "배경화면과 겹쳐 잘 안 보이면 바꾸세요. settings.json에는 #RRGGBB도 쓸 수 있습니다.",
            Combo(S.Apps.ActiveIndicatorColor, v => Store.Update(s => s.Apps.ActiveIndicatorColor = v), ("accent", "강조 색"), ("foreground", "글자 색"), ("white", "흰색"))));
        p.Children.Add(Card("알림 배지 (Telegram, Discord 등)", "앱이 작업 표시줄에 보내는 오버레이 아이콘을 표시합니다.", Toggle(S.Apps.ShowBadges, v => Store.Update(s => s.Apps.ShowBadges = v))));
        p.Children.Add(Card("시계: 12시간제", "", Toggle(S.Clock.Hour12, v => Store.Update(s => s.Clock.Hour12 = v))));
        p.Children.Add(Card("시계: 초 표시", "", Toggle(S.Clock.ShowSeconds, v => Store.Update(s => s.Clock.ShowSeconds = v))));
        p.Children.Add(Card("시계: 날짜 표시", "", Toggle(S.Clock.ShowDate, v => Store.Update(s => s.Clock.ShowDate = v))));
        return p;
    }

    private UIElement WidgetsPage()
    {
        var p = Page("위젯");
        var present = S.Tiles.Select(t => t.Type).ToHashSet();
        foreach (var d in WidgetRegistry.All)
        {
            bool available = d.IsAvailable();
            var toggle = Toggle(present.Contains(d.Type), on => { if (on) _host.AddWidget(d.Type); else _host.RemoveWidget(d.Type); });
            toggle.IsEnabled = available;
            p.Children.Add(Card(d.DisplayName, available ? d.Description : d.Description + " (이 PC에는 없음)", toggle));
        }
        p.Children.Add(Note("도크에서 위젯을 오른쪽 클릭하면 옮기거나 제거할 수 있습니다. 도크 빈 곳을 오른쪽 클릭해도 위젯을 추가할 수 있습니다."));
        return p;
    }

    private UIElement ShortcutsPage()
    {
        var p = Page("단축키");
        p.Children.Add(Card("Win+1 ~ Win+0",
            "도크 순서: 도크의 N번째 앱을 실행하거나 전환합니다(Shift=새 창, Ctrl+Shift=관리자, Alt=메뉴).",
            Combo(S.Shortcuts.WinNumberMode, v => Store.Update(s => s.Shortcuts.WinNumberMode = v), ("dock", "도크 순서"), ("native", "Windows 원래 순서"))));
        p.Children.Add(Card("Win+T로 도크에 키보드 포커스", "← → 이동, Enter 실행, Esc 돌아가기", Toggle(S.Shortcuts.InterceptWinT, v => Store.Update(s => s.Shortcuts.InterceptWinT = v))));
        p.Children.Add(Card("Win+B로 트레이 아이콘 열기", "", Toggle(S.Shortcuts.InterceptWinB, v => Store.Update(s => s.Shortcuts.InterceptWinB = v))));
        p.Children.Add(Card("단축키를 탐색기에서 넘겨받기",
            "관리자 권한으로 도는 게임 안에서도 위 단축키가 동작하고, 전체 화면에서는 키보드 훅을 멈춥니다. 도크를 끈 뒤 Windows 원래 Win+숫자는 탐색기를 다시 시작하거나 다시 로그인해야 돌아옵니다.",
            Toggle(S.Shortcuts.TakeOverExplorerHotkeys, v => Store.Update(s => s.Shortcuts.TakeOverExplorerHotkeys = v))));
        p.Children.Add(Card("전체 화면 앱에서 키보드 훅 항상 멈추기", "단축키를 넘겨받지 못했을 때만 의미가 있습니다. 켜면 그때는 전체 화면 게임 안에서 Win+1 ~ Win+0이 동작하지 않습니다.", Toggle(S.Shortcuts.SuspendHookInFullscreen, v => Store.Update(s => s.Shortcuts.SuspendHookInFullscreen = v))));
        p.Children.Add(Note("그 밖의 단축키(Win, Win+X, Win+A, Win+N, Win+D, Alt+Tab 등)는 Windows가 그대로 처리합니다."));
        return p;
    }

    private UIElement GeneralPage()
    {
        var p = Page("일반");
        p.Children.Add(Card("로그인할 때 GlassDock 실행", "작업 관리자의 시작 앱 목록에도 나타납니다.", Toggle(S.General.LaunchAtStartup, v => Store.Update(s => s.General.LaunchAtStartup = v))));
        p.Children.Add(Card("Windows 작업 표시줄 숨기기", "끄면 원래 작업 표시줄이 바로 돌아옵니다.", Toggle(S.Taskbar.HideNative, v => Store.Update(s => s.Taskbar.HideNative = v))));
        p.Children.Add(Card("로그아웃·종료할 때 작업 표시줄 복원", "GlassDock 없이 로그인해도 작업 표시줄이 보이게 합니다.", Toggle(S.Taskbar.RestoreOnSessionEnd, v => Store.Update(s => s.Taskbar.RestoreOnSessionEnd = v))));
        p.Children.Add(UpdateCard());
        p.Children.Add(Card("업데이트 자동 확인", "새 버전이 나오면 도크 위에 알려 줍니다. 설치는 직접 눌렀을 때만 합니다.", Toggle(S.General.CheckForUpdates, v => Store.Update(s => s.General.CheckForUpdates = v))));
        return p;
    }

    /// <summary>Current version, the newer one if any, and one button that checks or installs.</summary>
    private Border UpdateCard()
    {
        var updates = _host.Updates;
        var button = Button("", () => { });
        var card = Card("업데이트", "", button);
        var description = new TextBlock { FontSize = 12, Opacity = 0.7, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) };
        ((StackPanel)((Grid)card.Child).Children[0]).Children.Add(description);
        bool checking = false;

        void Show(string? note = null)
        {
            var info = updates.Available;
            description.Text = note ?? (info != null
                ? $"지금 {UpdateService.Current} · 새 버전 {info.Version}이 있어요" + (UpdateService.CanInstall ? "" : " (개발 빌드라 설치는 안 돼요)")
                : $"지금 {UpdateService.Current}");
            button.Content = info != null && UpdateService.CanInstall ? "업데이트" : "확인";
            button.IsEnabled = !checking;
        }

        button.Click += async (_, _) =>
        {
            checking = true;
            if (updates.Available != null && UpdateService.CanInstall)
            {
                Show("받는 중…");
                if (!await _host.InstallUpdateAsync(new Progress<double>(p => description.Text = $"받는 중… {(int)Math.Round(p * 100)}%")))
                {
                    checking = false;
                    Show("업데이트하지 못했어요. 로그 폴더의 기록을 보내 주세요.");
                }
                return;
            }
            Show("확인하는 중…");
            var found = await updates.CheckAsync();
            checking = false;
            Show(found == null && updates.Available == null ? $"지금 {UpdateService.Current} · 최신 버전이에요" : null);
        };
        Show();
        return card;
    }

    private UIElement AdvancedPage()
    {
        var p = Page("고급");
        p.Children.Add(Card("로그 폴더", Log.Directory, Button("열기", () => OpenFolder(Log.Directory))));
        p.Children.Add(Card("설정 폴더", AppPaths.Root, Button("열기", () => OpenFolder(AppPaths.Root))));
        p.Children.Add(Card("GlassDock 종료", "작업 표시줄을 복원하고 끝냅니다.", Button("종료", _host.Quit)));
        p.Children.Add(Note($"GlassDock {typeof(SettingsWindow).Assembly.GetName().Version} · 작업 표시줄 복구 명령: GlassDock.exe --restore-taskbar"));
        return p;
    }

    private static void OpenFolder(string path)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = false }); }
        catch (System.ComponentModel.Win32Exception ex) { Log.Error($"Opening {path} failed", ex); }
    }

    // ---- building blocks ---------------------------------------------------------------------------

    private static StackPanel Page(string title)
    {
        var p = new StackPanel();
        p.Children.Add(new TextBlock { Text = title, FontSize = 26, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 16) });
        return p;
    }

    private static Border Card(string title, string description, FrameworkElement control)
    {
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0) };
        text.Children.Add(new TextBlock { Text = title, FontSize = 14 });
        if (!string.IsNullOrEmpty(description))
            text.Children.Add(new TextBlock { Text = description, FontSize = 12, Opacity = 0.7, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) });
        g.Children.Add(text);
        control.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(control, 1);
        g.Children.Add(control);
        var card = new Border { Padding = new Thickness(16, 12, 16, 12), Margin = new Thickness(0, 0, 0, 4), CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1), Child = g };
        // Fluent theme brushes switch with light/dark; SystemColors stays light gray under white dark-theme text.
        card.SetResourceReference(Border.BackgroundProperty, "CardBackgroundFillColorDefaultBrush");
        card.SetResourceReference(Border.BorderBrushProperty, "CardStrokeColorDefaultBrush");
        return card;
    }

    private static TextBlock Note(string text) => new() { Text = text, FontSize = 12, Opacity = 0.65, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4, 12, 4, 0) };

    private static ComboBox Combo(string current, Action<string> set, params (string value, string label)[] options)
    {
        var cb = new ComboBox { MinWidth = 180 };
        foreach (var (v, l) in options) cb.Items.Add(new ComboBoxItem { Content = l, Tag = v });
        cb.SelectedItem = cb.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == current) ?? cb.Items[0];
        cb.SelectionChanged += (_, _) => { if (cb.SelectedItem is ComboBoxItem { Tag: string v }) set(v); };
        return cb;
    }

    private static CheckBox Toggle(bool current, Action<bool> set)
    {
        var t = new CheckBox { IsChecked = current };
        t.Checked += (_, _) => set(true);
        t.Unchecked += (_, _) => set(false);
        return t;
    }

    private static FrameworkElement Slider(double current, double min, double max, Action<double> set, double step = 1, string format = "0")
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        var label = new TextBlock { Text = current.ToString(format), Width = 32, VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Right, Margin = new Thickness(0, 0, 8, 0) };
        var s = new Slider { Minimum = min, Maximum = max, Value = current, Width = 180, IsSnapToTickEnabled = true, TickFrequency = step };
        s.ValueChanged += (_, e) => { label.Text = e.NewValue.ToString(format); set(e.NewValue); };
        panel.Children.Add(label);
        panel.Children.Add(s);
        return panel;
    }

    private static TextBox Text(string current, Action<string> set)
    {
        var tb = new TextBox { Text = current, MinWidth = 180 };
        tb.LostFocus += (_, _) => set(tb.Text);
        return tb;
    }

    private static Button Button(string label, Action click)
    {
        var b = new Button { Content = label, MinWidth = 90 };
        b.Click += (_, _) => click();
        return b;
    }
}
