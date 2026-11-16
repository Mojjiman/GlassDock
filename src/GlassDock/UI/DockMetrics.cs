using System.Windows;
using GlassDock.Config;
using static GlassDock.Interop.Native;

namespace GlassDock.UI;

/// <summary>The screen edge the dock sits on. Left and right lay the dock out vertically.</summary>
public enum DockEdge { Bottom, Top, Left, Right }

/// <summary>All dock geometry in DIP, derived from settings in one place.</summary>
public sealed record DockMetrics(
    double IconSize,
    double TileSize,
    double ItemSpacing,
    double Padding,
    double EdgeGap,
    double ReserveGap,
    double CornerRadius,
    double PillHeight,
    double Headroom,
    DockEdge Edge,
    bool Bar)
{
    public static DockMetrics From(DockSettings s)
    {
        var d = s.Dock;
        var edge = ParseEdge(d.Edge);
        double icon = AppConstants.Dock.IconSizeFor(d.Size);
        double tile = icon + 12; // room for hover plate and the running indicator
        double pill = tile + 2 * d.PaddingDip;
        double radius = d.CornerRadiusDip ?? (d.Shape == "bar" ? 0 : pill / 2.4);
        // A vertical dock shows its hover labels beside the pill, which needs more room than one line above it.
        double label = edge is DockEdge.Left or DockEdge.Right ? AppConstants.Dock.SideLabelHeadroomDip : AppConstants.Dock.LabelHeadroomDip;
        // A magnified tile grows away from the edge by its own size times the extra scale.
        double headroom = label + (s.Appearance.Magnification.Enabled ? tile * (s.Appearance.Magnification.MaxScale - 1) : 0);
        return new DockMetrics(icon, tile, d.ItemSpacingDip, d.PaddingDip, d.Shape == "bar" ? 0 : d.EdgeGapDip,
            d.ReserveGapDip, radius, pill, headroom, edge, d.Shape == "bar");
    }

    public static DockEdge ParseEdge(string? edge) => edge switch
    {
        "top" => DockEdge.Top,
        "left" => DockEdge.Left,
        "right" => DockEdge.Right,
        _ => DockEdge.Bottom,
    };

    public static string EdgeName(DockEdge edge) => edge switch
    {
        DockEdge.Top => "top",
        DockEdge.Left => "left",
        DockEdge.Right => "right",
        _ => "bottom",
    };

    public bool Bottom => Edge == DockEdge.Bottom;

    /// <summary>Left and right docks stack their tiles top to bottom.</summary>
    public bool Vertical => Edge is DockEdge.Left or DockEdge.Right;

    /// <summary>+1 when "away from the screen edge" is the positive x/y direction (top, left), -1 otherwise.</summary>
    public double AwaySign => Edge is DockEdge.Top or DockEdge.Left ? 1 : -1;

    public int AppBarEdge => Edge switch { DockEdge.Top => ABE_TOP, DockEdge.Left => ABE_LEFT, DockEdge.Right => ABE_RIGHT, _ => ABE_BOTTOM };

    /// <summary>A margin of <paramref name="before"/> and <paramref name="after"/> along the dock's main axis.</summary>
    public Thickness Along(double before, double after) =>
        Vertical ? new Thickness(0, before, 0, after) : new Thickness(before, 0, after, 0);

    /// <summary>The coordinate of <paramref name="p"/> along the dock's main axis.</summary>
    public double Main(Point p) => Vertical ? p.Y : p.X;

    /// <summary>Thickness of the dock window across its edge (pill + gap to the edge + headroom for labels/magnification).</summary>
    public double WindowThickness => PillHeight + EdgeGap + Headroom;

    /// <summary>Scale of an item <paramref name="distance"/> DIP from the pointer along the dock (design §9.4).</summary>
    public static double MagnifyScale(double distance, double range, double maxScale)
    {
        double c = Math.Cos(Math.Clamp(distance / range, -1, 1) * Math.PI / 2);
        return 1 + (maxScale - 1) * Math.Max(0, c) * Math.Max(0, c);
    }

    /// <summary>Thickness of the work-area strip reserved for the dock.</summary>
    public double ReserveThickness => EdgeGap + PillHeight + (Bar ? 0 : ReserveGap);
}
