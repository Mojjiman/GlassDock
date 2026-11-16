using System.Windows;
using System.Windows.Media;
using GlassDock.Apps;
using GlassDock.Config;

namespace GlassDock.Stacks;

/// <summary>The dock icon of a pinned folder or app group: its first items as a 2×2 grid on a glass square.</summary>
public static class StackIcon
{
    // Drawn in a 100×100 unit square; DrawingImage scales it to the icon size without blurring.
    private const double Unit = 100, Pad = 6, Gap = 4, Radius = 24;

    /// <returns>Null when the folder is empty or unreadable; the caller then shows the folder's own icon.</returns>
    public static async Task<ImageSource?> BuildAsync(IconProvider icons, string folder, string? sort, int iconPx)
    {
        var (items, _) = await Task.Run(() => FolderContents.Read(folder, sort, AppConstants.Stacks.CollageItems));
        if (items.Count == 0) return null;
        int px = IconProvider.SnapSize(iconPx / 2.0);
        return Compose(await Task.WhenAll(items.Select(i => icons.GetAsync(i.Path, px, thumbnail: true))));
    }

    /// <summary>Draws up to four images as a 2×2 grid on the glass plate (stacks and app groups).</summary>
    public static ImageSource Compose(IReadOnlyList<ImageSource?> images)
    {
        var g = new DrawingGroup();
        var plate = new RectangleGeometry(new Rect(0, 0, Unit, Unit), Radius, Radius);
        g.Children.Add(new GeometryDrawing(
            new SolidColorBrush(Color.FromArgb(AppConstants.Stacks.CollageFillAlpha, 0xFF, 0xFF, 0xFF)),
            new Pen(new SolidColorBrush(Color.FromArgb(AppConstants.Stacks.CollageEdgeAlpha, 0xFF, 0xFF, 0xFF)), 1.5),
            plate));
        double slot = (Unit - 2 * Pad - Gap) / 2;
        int count = Math.Min(images.Count, AppConstants.Stacks.CollageItems);
        for (int i = 0; i < count; i++)
        {
            if (images[i] is not { } img) continue;
            g.Children.Add(new ImageDrawing(img, Fit(img, Slot(i, count, slot))));
        }
        // Keep the full square as bounds even if a corner slot is empty.
        g.ClipGeometry = plate;
        var result = new DrawingImage(g);
        result.Freeze();
        return result;
    }

    /// <summary>
    /// Where item <paramref name="i"/> of <paramref name="count"/> goes: a 2×2 grid for four, two side by
    /// side in the middle for two, two over one for three, one large in the middle for one, so a small
    /// group does not leave half the plate empty.
    /// </summary>
    private static Rect Slot(int i, int count, double slot)
    {
        double centre = (Unit - slot) / 2;
        double col = Pad + i % 2 * (slot + Gap), row = Pad + i / 2 * (slot + Gap);
        return count switch
        {
            1 => new Rect(Pad, Pad, Unit - 2 * Pad, Unit - 2 * Pad),
            2 => new Rect(col, centre, slot, slot),
            3 when i == 2 => new Rect(centre, row, slot, slot),
            _ => new Rect(col, row, slot, slot),
        };
    }

    /// <summary>Thumbnails are not square; keep their aspect inside the slot.</summary>
    private static Rect Fit(ImageSource img, Rect slot)
    {
        double w = img.Width, h = img.Height;
        if (w <= 0 || h <= 0) return slot;
        double s = Math.Min(slot.Width / w, slot.Height / h);
        double fw = w * s, fh = h * s;
        return new Rect(slot.X + (slot.Width - fw) / 2, slot.Y + (slot.Height - fh) / 2, fw, fh);
    }
}
