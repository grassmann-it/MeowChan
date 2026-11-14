using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;

namespace MeowChan.Desktop;

/// <summary>
/// Where a preview is drawn. The visible frame of a preview, and everything that sits on it (play button, favourite star, badges,
/// hover and focus frame), follows the drawn image, not the box that was reserved before the image was known. Pure functions: no
/// control, no decode; the aspect comes from the bitmap's pixel size, which a loaded preview already has.
/// </summary>
internal static class MediaSlotGeometry
{
    /// <summary>What is reserved until a preview is loaded (the capture records no pixel size): wide, like most pictures.</summary>
    internal const double ReservedAspect = 16d / 9;
    /// <summary>The file line above a reader preview keeps this width at least (name and star), so a narrow picture does not squeeze it.</summary>
    internal const double MinimumBlockWidth = 140;
    /// <summary>The smallest side a frame takes: a one-pixel-wide strip stays clickable.</summary>
    internal const double MinimumExtent = 24;

    /// <summary>Width over height of a picture, or null when the size is not known or not usable.</summary>
    internal static double? AspectOf(int pixelWidth, int pixelHeight) =>
        pixelWidth > 0 && pixelHeight > 0 ? (double)pixelWidth / pixelHeight : null;

    internal static double? AspectOf(Bitmap? bitmap) => bitmap == null ? null : AspectOf(bitmap.PixelSize.Width, bitmap.PixelSize.Height);

    /// <summary>
    /// The frame of a reader preview. The picture fills the width it is given up to <paramref name="maxWidth"/>, as high as its aspect
    /// asks, at most <paramref name="maxHeight"/> (scaled down with the width below <paramref name="maxWidth"/>; then it gets narrower: a portrait picture is a narrow frame, not a wide box with the
    /// picture in a corner). Without an aspect the frame is <see cref="ReservedAspect"/> wide-screen at that width.
    /// </summary>
    internal static Size Fit(double? aspect, double availableWidth, double maxWidth, double maxHeight)
    {
        double width = double.IsFinite(availableWidth) ? Math.Clamp(availableWidth, 1, maxWidth) : maxWidth;
        if (aspect is not > 0 || !double.IsFinite(aspect.Value))
            return new Size(width, width / ReservedAspect);
        // A narrow owner (the thread list gives 76 dip) scales the height cap with its width: a tall picture stays a thumbnail there.
        maxHeight *= Math.Min(1, width / maxWidth);
        double height = width / aspect.Value;
        if (height > maxHeight) { height = maxHeight; width = height * aspect.Value; }
        // A very thin strip stays a usable frame; the picture is then centred in it.
        return new Size(Math.Max(Math.Min(MinimumExtent, availableWidth), width), Math.Max(MinimumExtent, height));
    }

    /// <summary>
    /// The picture inside a uniform grid cell: as large as the cell allows at its aspect, centred. Without an aspect it is the whole
    /// cell (placeholder text, "preview unavailable"). The tile's type badge, star and check mark sit on this rectangle.
    /// </summary>
    internal static Rect Letterbox(Size cell, double? aspect)
    {
        if (aspect is not > 0 || !double.IsFinite(aspect.Value) || cell.Width <= 0 || cell.Height <= 0 || !double.IsFinite(cell.Width) || !double.IsFinite(cell.Height))
            return new Rect(default, cell);
        double width = cell.Width, height = width / aspect.Value;
        if (height > cell.Height) { height = cell.Height; width = height * aspect.Value; }
        return new Rect((cell.Width - width) / 2, (cell.Height - height) / 2, width, height);
    }

    /// <summary>A picture lower or narrower than this keeps its marks apart: the band they are arranged on is at least this large.</summary>
    internal const double MinimumMarkExtent = 64;

    /// <summary>
    /// Where the corner marks of a tile (type badge, star, check mark) are arranged: the letterboxed picture, but at least
    /// <see cref="MinimumMarkExtent"/> in each direction, centred on it. A wide strip of 40 dip would otherwise stack badge and star.
    /// </summary>
    internal static Rect Marks(Size cell, double? aspect)
    {
        Rect picture = Letterbox(cell, aspect);
        double width = Math.Min(cell.Width, Math.Max(picture.Width, MinimumMarkExtent)), height = Math.Min(cell.Height, Math.Max(picture.Height, MinimumMarkExtent));
        return new Rect(picture.Center.X - width / 2, picture.Center.Y - height / 2, width, height);
    }

    /// <summary>
    /// The width of a reader attachment block (file line, frame, provenance): the frame, but at least the file line's minimum,
    /// and never more than what the owner gives.
    /// </summary>
    internal static double BlockWidth(double frameWidth, double availableWidth) =>
        Math.Max(frameWidth, Math.Min(MinimumBlockWidth, double.IsFinite(availableWidth) ? availableWidth : MinimumBlockWidth));
}

/// <summary>
/// A frame whose size is the drawn picture (<see cref="MediaSlotGeometry.Fit"/>): the picture, the click target and every overlay are
/// arranged on the same rectangle, so a play button is centred on the picture and a focus ring runs around it. The exceptions are the
/// <see cref="VideoPlayMarkView"/> and the shared badge view (<see cref="MeowChan.Desktop.Controls.MediaBadgeView"/>): they are sized from,
/// and placed on, the picture's letterboxed rectangle inside the frame, and the badge view is told that picture's size on every layout.
/// </summary>
internal sealed class MediaFramePanel : Panel
{
    internal bool Large { get; set; }
    /// <summary>The loaded picture's width over height; null while none is known (the reserved wide-screen box).</summary>
    internal double? Aspect { get; set; }
    /// <summary>Optional reader preference override; tiles keep the standard large/normal limits.</summary>
    internal double MaximumWidthOverride { get; set; } = double.NaN;
    /// <summary>Optional override of the height cap, for previews drawn wider than the reader default (textless or enlarged ones keep the cap's proportion).</summary>
    internal double MaximumHeightOverride { get; set; } = double.NaN;
    internal static double MaximumWidth(bool large) => large ? 360 : 240;
    internal static double MaximumHeight(bool large) => large ? 280 : 180;

    /// <summary>The shown picture's size inside the frame as it is arranged now (the badge view's picture); empty before the first layout.</summary>
    internal Size PictureSize => MediaSlotGeometry.Letterbox(Bounds.Size, Aspect).Size;

    protected override Size MeasureOverride(Size availableSize)
    {
        double maximumWidth = double.IsFinite(MaximumWidthOverride) && MaximumWidthOverride > 0
            ? MaximumWidthOverride
            : MaximumWidth(Large);
        double maximumHeight = double.IsFinite(MaximumHeightOverride) && MaximumHeightOverride > 0 ? MaximumHeightOverride : MaximumHeight(Large);
        Size frame = MediaSlotGeometry.Fit(Aspect, availableSize.Width, maximumWidth, maximumHeight);
        Size picture = MediaSlotGeometry.Letterbox(frame, Aspect).Size;
        foreach (Control child in Children)
        {
            // The play mark scales with the picture that is shown (not the frame, which a thin strip widens) and stays inside it.
            if (child is VideoPlayMarkView mark) { mark.Diameter = VideoPlayMark.Diameter(picture); mark.Measure(new Size(mark.Diameter, mark.Diameter)); }
            else if (child is MeowChan.Desktop.Controls.MediaBadgeView badges) { badges.Apply(badges.State, picture); badges.Measure(picture); }
            else child.Measure(frame);
        }
        return frame;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        Rect frame = new(default, DesiredSize.Width <= finalSize.Width + 0.01 ? DesiredSize : finalSize);
        Rect picture = MediaSlotGeometry.Letterbox(frame.Size, Aspect);
        foreach (Control child in Children)
        {
            if (child is MeowChan.Desktop.Controls.MediaBadgeView badges) { badges.Apply(badges.State, picture.Size); child.Arrange(picture); }
            else child.Arrange(child is VideoPlayMarkView ? VideoPlayMark.Place(picture) : frame);
        }
        return finalSize;
    }
}

/// <summary>
/// A uniform grid cell: its <see cref="Image"/> is arranged on the letterboxed picture inside it (<see cref="MediaSlotGeometry.Letterbox"/>),
/// every other child (corner marks, status text) on <see cref="MediaSlotGeometry.Marks"/>: the cell keeps the tile grid regular, the
/// marks sit on the picture's corners. The shared badge view (<see cref="MeowChan.Desktop.Controls.MediaBadgeView"/>) lies exactly on the
/// picture and is told its size on every layout.
/// </summary>
internal sealed class LetterboxPanel : Panel
{
    private double? _aspect;
    /// <summary>The loaded picture's width over height; null when there is none (the marks then use the whole cell).</summary>
    internal double? Aspect
    {
        get => _aspect;
        set { if (_aspect != value) { _aspect = value; InvalidateMeasure(); } }
    }

    /// <summary>The picture's rectangle inside this panel at its current size.</summary>
    internal Rect PictureBounds => MediaSlotGeometry.Letterbox(Bounds.Size, _aspect);

    /// <summary>The band the corner marks are arranged on at its current size (the picture, at least <see cref="MediaSlotGeometry.MinimumMarkExtent"/> each way).</summary>
    internal Rect MarksBounds => MediaSlotGeometry.Marks(Bounds.Size, _aspect);

    protected override Size MeasureOverride(Size availableSize)
    {
        if (double.IsInfinity(availableSize.Width) || double.IsInfinity(availableSize.Height))
        {
            double width = 0, height = 0;
            foreach (Control child in Children)
            {
                child.Measure(availableSize);
                width = Math.Max(width, child.DesiredSize.Width); height = Math.Max(height, child.DesiredSize.Height);
            }
            return new Size(double.IsInfinity(availableSize.Width) ? width : availableSize.Width,
                double.IsInfinity(availableSize.Height) ? height : availableSize.Height);
        }
        Rect picture = MediaSlotGeometry.Letterbox(availableSize, _aspect), marks = MediaSlotGeometry.Marks(availableSize, _aspect);
        foreach (Control child in Children)
        {
            if (child is MeowChan.Desktop.Controls.MediaBadgeView badges) badges.Apply(badges.State, picture.Size);
            child.Measure(child is Image or MeowChan.Desktop.Controls.MediaBadgeView ? picture.Size : marks.Size);
        }
        return availableSize;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        Rect picture = MediaSlotGeometry.Letterbox(finalSize, _aspect), marks = MediaSlotGeometry.Marks(finalSize, _aspect);
        foreach (Control child in Children)
        {
            if (child is MeowChan.Desktop.Controls.MediaBadgeView badges) badges.Apply(badges.State, picture.Size);
            child.Arrange(child is Image or MeowChan.Desktop.Controls.MediaBadgeView ? picture : marks);
        }
        return finalSize;
    }
}
