using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using GlassDock.Apps;
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

    // Cards that only mean something while another setting allows them; greyed out otherwise.
    // Each page is built anew when shown, so the list is cleared with it.
    private readonly List<(UIElement Card, Func<DockSettings, bool> Enabled)> _dependents = [];

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
            if (_nav.SelectedItem is not ListBoxItem { Tag: Func<UIElement> b }) return;
            _dependents.Clear();
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
        Store.Changed += OnStoreChanged;
        Closed += (_, _) => Store.Changed -= OnStoreChanged;
    }

    private void OnStoreChanged(object? sender, DockSettings s) => Dispatcher.BeginInvoke(() =>
    {
        foreach (var (card, enabled) in _dependents) SetEnabled(card, enabled(S));
    });

    private T DependsOn<T>(T card, Func<DockSettings, bool> enabled) where T : UIElement
    {
        _dependents.Add((card, enabled));
        SetEnabled(card, enabled(S));
        return card;
    }

    private static void SetEnabled(UIElement card, bool on)
    {
        card.IsEnabled = on;
        card.Opacity = on ? 1 : AppConstants.Settings.DisabledCardOpacity;
    }

    // ---- pages -------------------------------------------------------------------------------------
    // Wording: 해요체 throughout, and what the user sees or does rather than how it works inside.

    private UIElement AppearancePage()
    {
        var p = Page("모양");
        p.Children.Add(Card("유리 스타일", "도크가 어떻게 비쳐 보일지 골라요.",
            Combo(S.Appearance.Style, v => Store.Update(s => s.Appearance.Style = v),
                ("liquidGlass", "리퀴드 글래스 (유리처럼 비쳐요)"), ("frosted", "프로스트 (뿌옇게 흐려요)"), ("clear", "클리어 (그대로 비쳐요)"),
                ("midnight", "미드나이트 (어두운 남색)"), ("black", "블랙 (불투명)"), ("white", "화이트 (불투명)"))));
        p.Children.Add(Card("밝게 / 어둡게", "도크의 글자와 아이콘 색이에요.",
            Combo(S.Appearance.Theme, v => Store.Update(s => s.Appearance.Theme = v), ("system", "Windows 설정 따르기"), ("light", "밝게"), ("dark", "어둡게"))));
        p.Children.Add(Card("포인트 색", "지금 쓰는 앱 표시 같은 곳에 쓰는 색이에요. 비워 두면 Windows 색을 따라가요. 예: #3B82F6",
            ColorText(S.Appearance.AccentColor ?? "", v => Store.Update(s => s.Appearance.AccentColor = v))));
        p.Children.Add(Card("움직임 효과", "앱을 열 때 아이콘이 통통 튀고, 도크가 부드럽게 움직여요.", Toggle(S.Appearance.Animations, v => Store.Update(s => s.Appearance.Animations = v))));
        p.Children.Add(Card("아이콘 확대", "마우스를 올리면 그 아이콘과 옆 아이콘이 커져요.", Toggle(S.Appearance.Magnification.Enabled, v => Store.Update(s => s.Appearance.Magnification.Enabled = v))));
        p.Children.Add(DependsOn(Card("확대 정도", "얼마나 크게 키울지 정해요. 아이콘 확대를 켜야 쓸 수 있어요.",
            Slider(S.Appearance.Magnification.MaxScale, AppConstants.Magnify.MinScale, AppConstants.Magnify.MaxScale, v => Store.Update(s => s.Appearance.Magnification.MaxScale = Math.Round(v, 2)), step: 0.1, format: "0.0배", width: 48)),
            s => s.Appearance.Magnification.Enabled));
        return p;
    }

    private UIElement DockPage()
    {
        var p = Page("도크");
        p.Children.Add(Card("도크 위치", "도크의 빈 곳을 잡고 끌어서 옮길 수도 있어요.", Combo(S.Dock.Edge, v => Store.Update(s => s.Dock.Edge = v), ("bottom", "아래"), ("top", "위"), ("left", "왼쪽"), ("right", "오른쪽"))));
        p.Children.Add(Card("도크 모양", "가운데 떠 있는 알약, 또는 화면 끝에서 끝까지 이어진 막대예요.", Combo(S.Dock.Shape, v => Store.Update(s => s.Dock.Shape = v), ("pill", "떠 있는 알약"), ("bar", "꽉 찬 막대"))));
        p.Children.Add(Card("아이콘 크기", "", Combo(S.Dock.Size, v => Store.Update(s => s.Dock.Size = v), ("S", "작게"), ("M", "보통"), ("L", "크게"))));
        p.Children.Add(DependsOn(Card("화면 끝과의 간격", "도크를 화면 끝에서 얼마나 띄울지 정해요. 꽉 찬 막대는 띄우지 않아요.", Slider(S.Dock.EdgeGapDip, 0, 40, v => Store.Update(s => s.Dock.EdgeGapDip = v))),
            s => s.Dock.Shape != "bar"));
        p.Children.Add(Card("아이콘 사이 간격", "아이콘끼리 얼마나 띄울지 정해요.", Slider(S.Dock.ItemSpacingDip, 0, 24, v => Store.Update(s => s.Dock.ItemSpacingDip = v))));
        p.Children.Add(Card("마우스를 올리면 앱 이름 보기", "아이콘 위에 작은 이름표가 떠요.", Toggle(S.Dock.ShowLabelsOnHover, v => Store.Update(s => s.Dock.ShowLabelsOnHover = v))));
        p.Children.Add(Card("도크 잠그기", "실수로 아이콘을 옮기거나 빼지 않게 막아요.", Toggle(S.Dock.Locked, v => Store.Update(s => s.Dock.Locked = v))));

        p.Children.Add(Section("숨기기"));
        p.Children.Add(Card("도크 자동으로 숨기기", "안 쓸 때는 화면 밖으로 숨어요. 마우스를 화면 끝에 대거나 Win 키를 누르면 다시 나와요.", Toggle(S.AutoHide.Enabled, v => Store.Update(s => s.AutoHide.Enabled = v))));
        p.Children.Add(DependsOn(Card("도크 자리에 마우스만 가져가도 나오기", "화면 끝까지 안 내려도, 도크가 있던 자리에 마우스를 잠깐 두면 나와요. 자동으로 숨기기를 켜야 쓸 수 있어요.", Toggle(S.AutoHide.RevealOnDockArea, v => Store.Update(s => s.AutoHide.RevealOnDockArea = v))),
            s => s.AutoHide.Enabled));
        p.Children.Add(Card("전체 화면에서 도크 숨기기", "게임이나 영상을 전체 화면으로 볼 때 도크를 숨겨요. Win 키를 누르면 다시 나와요.", Toggle(S.Fullscreen.HideDock, v => Store.Update(s => s.Fullscreen.HideDock = v))));

        p.Children.Add(Section("최대화한 창"));
        p.Children.Add(DependsOn(Card("창이 도크를 가리지 않게 하기", "켜면 최대화한 창이 도크 바로 위에서 멈춰요. 끄면 창이 도크 뒤까지 꽉 차요. 자동으로 숨기기를 켜면 쓰지 않아요.", Toggle(S.Dock.ReserveSpace, v => Store.Update(s => s.Dock.ReserveSpace = v))),
            s => !s.AutoHide.Enabled));
        p.Children.Add(DependsOn(Card("최대화했을 때 도크 양옆 채우기", "창을 최대화하면 도크 양옆으로 배경화면이 조금 보여요. 그 자리를 유리 띠로 채워서 작업 표시줄처럼 보이게 해요. 바로 위 설정이 켜져 있어야 쓸 수 있어요.", Toggle(S.Dock.FillMaximizedBand, v => Store.Update(s => s.Dock.FillMaximizedBand = v))),
            s => s.Dock.ReserveSpace && !s.AutoHide.Enabled));

        p.Children.Add(Section("시계"));
        p.Children.Add(Card("오전/오후로 보기", "끄면 14:30처럼 24시간으로 보여요.", Toggle(S.Clock.Hour12, v => Store.Update(s => s.Clock.Hour12 = v))));
        p.Children.Add(Card("초까지 보기", "", Toggle(S.Clock.ShowSeconds, v => Store.Update(s => s.Clock.ShowSeconds = v))));
        p.Children.Add(Card("날짜도 보기", "", Toggle(S.Clock.ShowDate, v => Store.Update(s => s.Clock.ShowDate = v))));
        return p;
    }

    private UIElement AppsPage()
    {
        var p = Page("앱");
        p.Children.Add(Card("같은 앱의 창을 아이콘 하나로 모으기", "끄면 창마다 아이콘이 따로 생겨요.", Toggle(S.Apps.GroupWindows, v => Store.Update(s => s.Apps.GroupWindows = v))));
        p.Children.Add(Card("창이 여러 개인 앱을 누르면", "예: 브라우저 창이 3개 열려 있을 때", Combo(S.Apps.MultiWindowClick, v => Store.Update(s => s.Apps.MultiWindowClick = v), ("preview", "창 미리보기 보여 주기"), ("cycle", "다음 창으로 바로 넘어가기"))));
        p.Children.Add(Card("실행 중인 앱 표시", "켜져 있는 앱 아이콘 아래에 작은 표시가 보여요.", Toggle(S.Apps.ShowRunningIndicator, v => Store.Update(s => s.Apps.ShowRunningIndicator = v))));
        p.Children.Add(Card("지금 쓰는 앱 표시 색", "지금 보고 있는 앱 아래 긴 막대의 색이에요. 배경화면에 묻혀 잘 안 보이면 바꿔 보세요.",
            Combo(S.Apps.ActiveIndicatorColor, v => Store.Update(s => s.Apps.ActiveIndicatorColor = v), ("accent", "포인트 색"), ("foreground", "글자 색"), ("white", "흰색"))));
        p.Children.Add(Card("알림 배지 보기", "디스코드·텔레그램처럼 새 메시지가 오면 아이콘에 작은 표시가 붙어요.", Toggle(S.Apps.ShowBadges, v => Store.Update(s => s.Apps.ShowBadges = v))));
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
            p.Children.Add(Card(d.DisplayName, available ? d.Description : d.Description + " (이 PC에는 없어요)", toggle));
        }
        p.Children.Add(Note("위젯은 도크에서 끌어서 옮길 수 있어요. 오른쪽 클릭하면 뺄 수 있고, 도크 빈 곳을 오른쪽 클릭해도 추가할 수 있어요."));
        return p;
    }

    private UIElement ShortcutsPage()
    {
        var p = Page("단축키");
        p.Children.Add(Card("Win+숫자로 앱 열기",
            "Win+1은 도크의 첫 번째 앱, Win+2는 두 번째 앱을 열거나 그 창으로 가요. Shift를 같이 누르면 새 창, Ctrl+Shift는 관리자 권한, Alt는 메뉴예요.",
            Combo(S.Shortcuts.WinNumberMode, v => Store.Update(s => s.Shortcuts.WinNumberMode = v), ("dock", "도크 순서대로"), ("native", "Windows에 맡기기"))));
        p.Children.Add(Card("Win+T로 도크를 키보드로 쓰기", "Win+T를 누른 뒤 ← →로 고르고 Enter로 열어요. Esc를 누르면 돌아가요.", Toggle(S.Shortcuts.InterceptWinT, v => Store.Update(s => s.Shortcuts.InterceptWinT = v))));
        p.Children.Add(Card("Win+B로 숨겨진 아이콘 열기", "도크의 트레이 아이콘 창을 키보드로 열어요.", Toggle(S.Shortcuts.InterceptWinB, v => Store.Update(s => s.Shortcuts.InterceptWinB = v))));
        p.Children.Add(Note("Win, Win+X, Win+D, Alt+Tab 같은 다른 단축키는 Windows 그대로예요. 게임 안에서 단축키가 안 되면 고급 페이지를 보세요."));
        return p;
    }

    private UIElement GeneralPage()
    {
        var p = Page("일반");
        p.Children.Add(Card("컴퓨터를 켜면 자동으로 실행", "Windows에 로그인하면 GlassDock도 같이 켜져요.", Toggle(S.General.LaunchAtStartup, v => Store.Update(s => s.General.LaunchAtStartup = v))));
        p.Children.Add(Card("Windows 작업 표시줄 숨기기", "끄면 원래 작업 표시줄이 바로 다시 보이고, 도크와 함께 쓰게 돼요.", Toggle(S.Taskbar.HideNative, v => Store.Update(s => s.Taskbar.HideNative = v))));
        p.Children.Add(UpdateCard());
        p.Children.Add(Card("새 버전 알림 받기", "새 버전이 나오면 도크에 알려 줘요. 설치는 직접 누를 때만 해요.", Toggle(S.General.CheckForUpdates, v => Store.Update(s => s.General.CheckForUpdates = v))));
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
                ? $"지금 버전 {UpdateService.Current} · 새 버전 {info.Version}가 나왔어요" + (UpdateService.CanInstall ? "" : " (개발 빌드라 설치는 안 돼요)")
                : $"지금 버전 {UpdateService.Current}");
            button.Content = info != null && UpdateService.CanInstall ? "지금 업데이트" : "업데이트 확인";
            button.IsEnabled = !checking;
        }

        button.Click += async (_, _) =>
        {
            checking = true;
            if (updates.Available != null && UpdateService.CanInstall)
            {
                Show("내려받는 중…");
                if (!await _host.InstallUpdateAsync(new Progress<double>(p => description.Text = $"내려받는 중… {(int)Math.Round(p * 100)}%")))
                {
                    checking = false;
                    Show("업데이트하지 못했어요. 잠시 뒤 다시 해 보세요. 계속 안 되면 고급 페이지에서 문제 보고 파일을 만들어 보내 주세요.");
                }
                return;
            }
            Show("확인하는 중…");
            var found = await updates.CheckAsync();
            checking = false;
            Show(found == null && updates.Available == null ? $"지금 버전 {UpdateService.Current} · 최신 버전이에요" : null);
        };
        Show();
        return card;
    }

    private UIElement AdvancedPage()
    {
        var p = Page("고급");
        p.Children.Add(Section("문제가 생겼을 때"));
        p.Children.Add(Card("문제 보고 파일 만들기", "최근 기록(로그)과 설정 파일을 바탕 화면에 zip 파일 하나로 모아요. 그 파일을 보내 주시면 돼요.", ReportButton()));
        p.Children.Add(Card("문제 기록(로그) 폴더", Log.Directory, Button("열기", () => OpenFolder(Log.Directory))));
        p.Children.Add(Card("설정 파일 폴더", "설정이 저장되는 곳이에요. 통째로 복사해 두면 백업이 돼요.", Button("열기", () => OpenFolder(AppPaths.Root))));

        p.Children.Add(Section("게임과 단축키"));
        p.Children.Add(Card("게임 중에도 단축키 쓰기",
            "관리자 권한으로 켜지는 게임 안에서도 Win+숫자가 되게 Windows에서 단축키를 넘겨받아요. GlassDock을 끈 뒤 Windows 원래 Win+숫자는 다시 로그인하거나 탐색기를 다시 시작해야 돌아와요.",
            Toggle(S.Shortcuts.TakeOverExplorerHotkeys, v => Store.Update(s => s.Shortcuts.TakeOverExplorerHotkeys = v))));
        p.Children.Add(Card("전체 화면에서는 키 입력 감시 끄기",
            "게임 보안 프로그램이 키보드 감시를 문제 삼을 때만 켜세요. 켜면, 위 설정으로 단축키를 넘겨받지 못한 경우 전체 화면 게임 안에서 Win+숫자가 안 돼요.",
            Toggle(S.Shortcuts.SuspendHookInFullscreen, v => Store.Update(s => s.Shortcuts.SuspendHookInFullscreen = v))));

        p.Children.Add(Section("작업 표시줄"));
        p.Children.Add(Card("컴퓨터를 끌 때 작업 표시줄 되돌려 놓기", "다음에 GlassDock이 안 켜져도 작업 표시줄이 보이게 해 두는 안전장치예요. 켜 두는 걸 권해요.", Toggle(S.Taskbar.RestoreOnSessionEnd, v => Store.Update(s => s.Taskbar.RestoreOnSessionEnd = v))));
        p.Children.Add(Card("GlassDock 종료", "도크를 끄고 원래 작업 표시줄로 돌아가요.", Button("종료", _host.Quit)));
        p.Children.Add(Note($"GlassDock {typeof(SettingsWindow).Assembly.GetName().Version} · 작업 표시줄이 안 돌아오면 명령 프롬프트에서 GlassDock.exe --restore-taskbar 를 실행하세요."));
        return p;
    }

    private Button ReportButton()
    {
        var button = new Button { Content = "만들기", MinWidth = 90 };
        button.Click += async (_, _) =>
        {
            button.IsEnabled = false;
            string? zip = await Task.Run(ProblemReport.Create);
            button.IsEnabled = true;
            if (zip == null) { MessageBox.Show(this, "문제 보고 파일을 만들지 못했어요. 로그 폴더를 열어 파일을 직접 보내 주세요.", "GlassDock"); return; }
            AppLauncher.OpenFileLocation(zip);
        };
        return button;
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

    private static TextBlock Section(string title) => new() { Text = title, FontSize = 15, FontWeight = FontWeights.SemiBold, Margin = new Thickness(4, 16, 4, 8) };

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

    private static FrameworkElement Slider(double current, double min, double max, Action<double> set, double step = 1, string format = "0", double width = 32)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        var label = new TextBlock { Text = current.ToString(format), Width = width, VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Right, Margin = new Thickness(0, 0, 8, 0) };
        var s = new Slider { Minimum = min, Maximum = max, Value = current, Width = 180, IsSnapToTickEnabled = true, TickFrequency = step };
        s.ValueChanged += (_, e) => { label.Text = e.NewValue.ToString(format); set(e.NewValue); };
        panel.Children.Add(label);
        panel.Children.Add(s);
        return panel;
    }

    /// <summary>
    /// A #RRGGBB box, empty for "none". Saved as soon as what is typed is valid, so closing the window while the
    /// box still has the focus loses nothing; an invalid value gets a red border and is not saved.
    /// </summary>
    private static TextBox ColorText(string current, Action<string?> set)
    {
        var tb = new TextBox { Text = current, MinWidth = 180 };
        var normal = tb.BorderBrush;
        tb.TextChanged += (_, _) =>
        {
            string v = tb.Text.Trim();
            bool ok = v.Length == 0 || HexColor.IsMatch(v);
            tb.BorderBrush = ok ? normal : Brushes.IndianRed;
            tb.ToolTip = ok ? null : "#3B82F6처럼 # 뒤에 여섯 자리로 써 주세요.";
            if (ok) set(v.Length == 0 ? null : v.StartsWith('#') ? v : "#" + v);
        };
        return tb;
    }

    private static readonly System.Text.RegularExpressions.Regex HexColor = new("^#?[0-9A-Fa-f]{6}$");

    private static Button Button(string label, Action click)
    {
        var b = new Button { Content = label, MinWidth = 90 };
        b.Click += (_, _) => click();
        return b;
    }
}
