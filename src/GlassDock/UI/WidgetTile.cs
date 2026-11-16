using System.Windows.Input;
using GlassDock.Glass;
using GlassDock.Widgets;

namespace GlassDock.UI;

/// <summary>A dock tile hosting one widget; owns the widget and disposes it with the tile.</summary>
public sealed class WidgetTile : TileButton, IDisposable
{
    public WidgetDescriptor Descriptor { get; }
    public IWidget Widget { get; }
    /// <summary>Id of the <c>TileSpec</c> this tile was built from, for move/remove.</summary>
    public string SpecId { get; }

    public WidgetTile(DockMetrics m, GlassLook look, WidgetDescriptor descriptor, IWidget widget, WidgetContext ctx, string specId) : base(m, look)
    {
        Descriptor = descriptor;
        Widget = widget;
        SpecId = specId;
        Label = descriptor.DisplayName;
        var view = widget.CreateView(ctx);
        // A vertical dock is one tile wide; a wide value (network speed) shrinks rather than widening the dock.
        Children.Add(m.Vertical
            ? new System.Windows.Controls.Viewbox { Child = view, StretchDirection = System.Windows.Controls.StretchDirection.DownOnly, MaxWidth = CellSize }
            : view);
        Clicked += _ => Widget.OnClick();
    }

    protected override void OnMouseEnter(MouseEventArgs e)
    {
        // The label shows live values, so read it when the pointer arrives.
        Label = Widget.Label;
        base.OnMouseEnter(e);
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        Widget.OnWheel(e.Delta);
        e.Handled = true;
    }

    public void Dispose() => Widget.Dispose();
}
