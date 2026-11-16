using System.Windows;
using System.Windows.Controls;
using GlassDock.Apps;
using GlassDock.Update;
using F = GlassDock.Config.AppConstants.Flyouts;

namespace GlassDock.UI;

/// <summary>Offers a newer release once per version: install now, see what changed, or later.</summary>
public sealed class UpdateFlyout : FlyoutWindow
{
    private readonly TextBlock _status;
    private readonly StackPanel _buttons;

    public UpdateFlyout(DockWindow dock, UpdateInfo info, Func<IProgress<double>, Task<bool>> install) : base(dock)
    {
        // It opens on its own, usually while another app has the focus (Windows may refuse to hand it
        // over), so losing activation must not close it; only its buttons do.
        KeepOpenWhileDeactivated = true;
        var title = new TextBlock { Text = $"GlassDock {info.Version} 업데이트", FontSize = 15, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) };
        _status = new TextBlock
        {
            Text = $"새 버전이 나왔어요. 지금은 {UpdateService.Current}이에요. 업데이트하면 도크가 잠깐 꺼졌다 다시 켜져요.",
            FontSize = 12,
            Foreground = ForegroundAlpha(F.MutedTextAlpha),
            TextWrapping = TextWrapping.Wrap,
        };
        _buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        _buttons.Children.Add(LinkButton("변경 내용", () => AppLauncher.Execute(info.PageUrl, null, null, null)));
        _buttons.Children.Add(LinkButton("나중에", SafeClose));
        var now = LinkButton("지금 업데이트", async () =>
        {
            _buttons.IsEnabled = false;
            var progress = new Progress<double>(p => _status.Text = $"받는 중… {(int)Math.Round(p * 100)}%");
            if (!await install(progress))
            {
                _buttons.IsEnabled = true;
                _status.Text = "업데이트하지 못했어요. 잠시 뒤 다시 해 보거나, 변경 내용 페이지에서 zip을 받아 주세요.";
            }
        });
        now.FontWeight = FontWeights.SemiBold;
        if (!UpdateService.CanInstall)
        {
            now.IsEnabled = false;
            now.ToolTip = "개발 빌드에서는 설치할 수 없어요";
        }
        _buttons.Children.Add(now);

        var root = new StackPanel { Margin = new Thickness(16, 14, 12, 12), Width = F.UpdateFlyoutWidth };
        root.Children.Add(title);
        root.Children.Add(_status);
        root.Children.Add(_buttons);
        Content = Tinted(root);
    }
}
