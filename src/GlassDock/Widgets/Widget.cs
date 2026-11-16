using System.Windows;
using GlassDock.Apps;
using GlassDock.Glass;
using GlassDock.UI;

namespace GlassDock.Widgets;

/// <summary>What a widget gets to build its view (design §8.1).</summary>
/// <param name="Scale">Physical pixels per DIP on the dock's monitor, for sizing bitmaps.</param>
public sealed record WidgetContext(DockMetrics Metrics, GlassLook Look, RefreshScheduler Scheduler, IconProvider Icons, double Scale);

/// <summary>
/// A dock widget. One instance per tile; it is created on every dock rebuild and disposed with the
/// old tiles, so it must release its subscriptions in <see cref="IDisposable.Dispose"/>.
/// </summary>
public interface IWidget : IDisposable
{
    FrameworkElement CreateView(WidgetContext ctx);

    /// <summary>Hover label text, read when the pointer enters the tile.</summary>
    string Label { get; }

    void OnClick() { }

    /// <summary>Mouse wheel over the tile; <paramref name="delta"/> is in WHEEL_DELTA units (120 per notch).</summary>
    void OnWheel(int delta) { }

    /// <summary>Extra entries at the top of the widget's context menu.</summary>
    IEnumerable<(string Header, Action Action)> MenuItems => [];
}

public sealed record WidgetDescriptor(string Type, string DisplayName, string Description, Func<IWidget> Create)
{
    /// <summary>False when the hardware is missing (no battery), so the widget is not offered.</summary>
    public Func<bool> IsAvailable { get; init; } = () => true;
}

/// <summary>Built-in widgets, by a static table (no reflection scanning; plugins are deferred, design §8.1).</summary>
public static class WidgetRegistry
{
    public static IReadOnlyList<WidgetDescriptor> All { get; } =
    [
        new("media", "지금 재생 중", "재생 중인 음악·영상과 재생 컨트롤, 클릭하면 크게 보기와 앱 볼륨", () => new MediaWidget()),
        new("volume", "볼륨", "클릭하면 볼륨 창, 휠로 볼륨 조절", () => new VolumeWidget()),
        new("cpu", "CPU", "CPU 사용률", () => new CpuWidget()),
        new("memory", "메모리", "메모리 사용률", () => new MemoryWidget()),
        new("network", "네트워크", "다운로드·업로드 속도", () => new NetworkWidget()),
        new("ai", "AI 사용량", "Claude·Codex 사용 한도와 Claude Code 토큰", () => new AiUsageWidget()),
        new("battery", "배터리", "배터리 잔량과 충전 상태", () => new BatteryWidget()) { IsAvailable = BatteryWidget.HasBattery },
    ];

    public static WidgetDescriptor? Find(string type) => All.FirstOrDefault(d => d.Type == type);
}
