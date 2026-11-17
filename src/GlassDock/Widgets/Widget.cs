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
        new("media", "지금 재생 중", "재생 중인 음악·영상을 보여 주고 바로 넘길 수 있어요. 누르면 크게 보이고 앱별 소리도 맞춰요.", () => new MediaWidget()),
        new("volume", "볼륨", "소리 크기를 보여 줘요. 누르면 조절 창이 열리고, 휠을 굴려도 바뀌어요.", () => new VolumeWidget()),
        new("cpu", "CPU", "컴퓨터가 얼마나 바쁜지 %로 보여 줘요.", () => new CpuWidget()),
        new("memory", "메모리", "메모리(RAM)를 얼마나 쓰고 있는지 보여 줘요.", () => new MemoryWidget()),
        new("network", "인터넷 속도", "지금 내려받고 올리는 속도를 보여 줘요.", () => new NetworkWidget()),
        new("ai", "AI 사용량", "Claude·ChatGPT(Codex)의 5시간·주간 사용 한도를 보여 줘요. 하나만 쓰면 그것만 보여요.", () => new AiUsageWidget()),
        new("battery", "배터리", "남은 배터리와 충전 중인지 보여 줘요.", () => new BatteryWidget()) { IsAvailable = BatteryWidget.HasBattery },
    ];

    public static WidgetDescriptor? Find(string type) => All.FirstOrDefault(d => d.Type == type);
}
