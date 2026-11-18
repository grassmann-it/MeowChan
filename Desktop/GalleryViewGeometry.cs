using System;

namespace MeowChan.Desktop;

/// <summary>
/// The limits of the gallery's zoom: how far a medium may be enlarged (physical pixels per source pixel, as a percentage).
/// The default keeps the long-standing 16× (1600 %) of the image viewer; videos use the same limit, so both behave alike.
/// </summary>
internal static class GalleryZoomLimits
{
    internal const int MaxPercentDefault = 1600;
    internal const int MaxPercentMinimum = 200;
    internal const int MaxPercentMaximum = 6400;

    internal static int ClampPercent(int percent) => Math.Clamp(percent, MaxPercentMinimum, MaxPercentMaximum);
}

/// <summary>Rotation of the shown medium in 90° steps (clockwise). A view transform only: the file is never touched.</summary>
internal static class GalleryRotation
{
    internal const int Step = 90;

    /// <summary>The nearest rotation of 0, 90, 180 or 270 degrees for any integer (negative and large values wrap).</summary>
    internal static int Normalize(int degrees)
    {
        int wrapped = ((degrees % 360) + 360) % 360;
        return wrapped / Step * Step;
    }

    /// <summary>The next clockwise step (270 → 0).</summary>
    internal static int Next(int degrees) => Normalize(Normalize(degrees) + Step);

    /// <summary>The next counter-clockwise step (0 → 270).</summary>
    internal static int Previous(int degrees) => Normalize(Normalize(degrees) - Step);

    /// <summary>Whether width and height trade places on screen (90° and 270°).</summary>
    internal static bool IsQuarterTurn(int degrees) => Normalize(degrees) is 90 or 270;

    /// <summary>A screen-space vector (y down) turned by the same quarter steps, so the spot under the viewport centre stays.</summary>
    internal static (double X, double Y) Turn(double x, double y, int steps)
    {
        int turns = ((steps % 4) + 4) % 4;
        return turns switch { 1 => (-y, x), 2 => (-x, -y), 3 => (y, -x), _ => (x, y) };
    }
}

/// <summary>
/// The scene of one shown medium: its source size, the viewport, the display scale and the limits. Source size and rotation
/// give the orientation on screen; every method answers in physical pixels per source pixel (zoom) or dip (pan).
/// </summary>
internal readonly record struct GalleryViewGeometry(int SourceWidth, int SourceHeight, double ViewportWidth, double ViewportHeight,
    double DisplayScale, double? FitCap = null, int MaxZoomPercent = GalleryZoomLimits.MaxPercentDefault, int Rotation = 0)
{
    internal bool IsValid => SourceWidth > 0 && SourceHeight > 0 && double.IsFinite(DisplayScale) && DisplayScale > 0;

    /// <summary>The source size as it appears on screen (width and height swapped at 90° and 270°).</summary>
    internal (int Width, int Height) Oriented => GalleryRotation.IsQuarterTurn(Rotation)
        ? (SourceHeight, SourceWidth) : (SourceWidth, SourceHeight);

    /// <summary>The zoom that fits the rotated medium into the viewport (the image viewer's Fit, with its upscale cap).</summary>
    internal double Fit => IsValid
        ? GalleryGeometry.Fit(Oriented.Width, Oriented.Height, ViewportWidth, ViewportHeight, DisplayScale, FitCap) : 1;

    /// <summary>The largest zoom: the setting, but never below Fit (a huge medium that only fits above it stays usable).</summary>
    internal double MaxZoom => Math.Max(Fit, GalleryZoomLimits.ClampPercent(MaxZoomPercent) / 100.0);

    internal double ClampZoom(double requested) =>
        Math.Clamp(double.IsFinite(requested) ? requested : Fit, Fit, MaxZoom);

    internal double PanX(double pan, double zoom) => GalleryGeometry.Pan(pan, Oriented.Width, zoom, DisplayScale, ViewportWidth);
    internal double PanY(double pan, double zoom) => GalleryGeometry.Pan(pan, Oriented.Height, zoom, DisplayScale, ViewportHeight);
}

/// <summary>Where the medium is: the zoom (physical pixels per source pixel), the pan in dip, and how it got there.</summary>
internal readonly record struct GalleryViewPlacement(double Zoom, double PanX, double PanY, bool Manual, bool Actual);

/// <summary>
/// The zoom, pan, Fit, 1:1 and rotation rules shared by images and videos, as pure functions of a placement and a geometry.
/// The behaviour of the image viewer is kept exactly (explicit 1:1 may sit below Fit, wheel zoom anchors at the pointer);
/// only the rotation and the configurable maximum are new.
/// </summary>
internal static class GalleryViewMath
{
    internal static GalleryViewPlacement Fitted(GalleryViewGeometry geometry) => new(geometry.Fit, 0, 0, false, false);

    internal static GalleryViewPlacement ActualPixels(GalleryViewGeometry geometry) => new(1, 0, 0, true, true);

    /// <summary>
    /// The rotation a medium starts with: upright for a new medium (a view transform is never remembered per file), the same
    /// rotation when the very same medium is shown again (a video back from the reader).
    /// </summary>
    internal static int RotationForMedium(bool sameMedium, int rotation) => sameMedium ? GalleryRotation.Normalize(rotation) : 0;

    /// <summary>
    /// One zoom step. <paramref name="anchor"/> is the pointer's offset from the viewport centre (the centre itself for keys).
    /// Returns the same placement when nothing can change.
    /// </summary>
    internal static GalleryViewPlacement ZoomBy(GalleryViewPlacement view, GalleryViewGeometry geometry, double factor, (double X, double Y)? anchor = null)
    {
        if (!geometry.IsValid || !double.IsFinite(factor) || factor <= 0) return view;
        double fit = geometry.Fit;
        // The explicit 1:1 path can remain below Fit; move gently from 1:1 toward Fit.
        bool belowFitActual = view.Actual && fit > 1 && view.Zoom < fit;
        if (belowFitActual && view.Zoom == 1 && factor < 1) return view;
        double old = view.Zoom;
        double zoom = belowFitActual
            ? Math.Clamp(old * factor, 1, geometry.MaxZoom)
            : geometry.ClampZoom(old * factor);
        bool actual = belowFitActual && zoom < fit;
        bool manual = actual || zoom > fit * (1 + 1e-10);
        if (!manual) return Fitted(geometry);
        (double offsetX, double offsetY) = anchor ?? (0, 0);
        double panX = offsetX - (offsetX - view.PanX) * zoom / old;
        double panY = offsetY - (offsetY - view.PanY) * zoom / old;
        return Clamped(new GalleryViewPlacement(zoom, panX, panY, manual, actual), geometry);
    }

    /// <summary>The placement after a viewport or limit change: Fit follows the viewport, a manual zoom is re-clamped.</summary>
    internal static GalleryViewPlacement IfAutomatic(GalleryViewPlacement view, GalleryViewGeometry geometry)
    {
        if (!geometry.IsValid) return view;
        double fit = geometry.Fit;
        if (view.Actual && view.Zoom < fit) return Clamped(view, geometry);
        if (!view.Manual || view.Zoom <= fit) return Fitted(geometry);
        return Clamped(view with { Zoom = geometry.ClampZoom(view.Zoom) }, geometry);
    }

    /// <summary>The pan kept inside the medium's edges (no pan while the medium is smaller than the viewport).</summary>
    internal static GalleryViewPlacement Clamped(GalleryViewPlacement view, GalleryViewGeometry geometry) => geometry.IsValid
        ? view with { PanX = geometry.PanX(view.PanX, view.Zoom), PanY = geometry.PanY(view.PanY, view.Zoom) }
        : view;

    /// <summary>
    /// Turns the medium by <paramref name="steps"/> quarter turns clockwise; <paramref name="after"/> is the scene with the new
    /// rotation. Fit stays Fit; a manual view keeps the spot under the centre, with the zoom re-clamped to the new Fit and pan limits.
    /// </summary>
    internal static GalleryViewPlacement Rotated(GalleryViewPlacement view, GalleryViewGeometry after, int steps)
    {
        if (!after.IsValid) return view;
        if (!view.Manual) return Fitted(after);
        (double x, double y) = GalleryRotation.Turn(view.PanX, view.PanY, steps);
        double zoom = view.Actual && view.Zoom < after.Fit ? view.Zoom : after.ClampZoom(view.Zoom);
        GalleryViewPlacement next = new(zoom, x, y, view.Manual, view.Actual && zoom < after.Fit);
        if (!next.Actual && zoom <= after.Fit * (1 + 1e-10)) return Fitted(after);
        return Clamped(next, after);
    }
}

/// <summary>
/// How a placed medium is drawn onto a render surface that shows it uniformly fitted into its cell (the software video image):
/// a uniform scale about the cell centre, the rotation and the pan. The decoded frame is never touched; only this matrix changes.
/// </summary>
internal readonly record struct VideoSurfaceTransform(double Scale, int Rotation, double TranslateX, double TranslateY)
{
    internal static readonly VideoSurfaceTransform Identity = new(1, 0, 0, 0);

    /// <summary>
    /// <paramref name="cellWidth"/>/<paramref name="cellHeight"/> is the surface's cell in dip; the surface draws the source
    /// Uniform in it. The scale relates the wanted size (zoom / display scale) to that fitted size.
    /// </summary>
    internal static VideoSurfaceTransform For(int sourceWidth, int sourceHeight, double cellWidth, double cellHeight,
        double displayScale, GalleryViewPlacement view, int rotation)
    {
        if (sourceWidth <= 0 || sourceHeight <= 0 || cellWidth <= 0 || cellHeight <= 0
            || !double.IsFinite(displayScale) || displayScale <= 0 || !double.IsFinite(view.Zoom) || view.Zoom <= 0) return Identity;
        double uniform = Math.Min(cellWidth / sourceWidth, cellHeight / sourceHeight);
        double scale = view.Zoom / displayScale / uniform;
        return new(scale, GalleryRotation.Normalize(rotation), view.PanX, view.PanY);
    }

    /// <summary>The transform as one matrix (scale, then rotation, then translation; row-vector convention of Avalonia).</summary>
    internal Avalonia.Matrix ToMatrix() =>
        Avalonia.Matrix.CreateScale(Scale, Scale)
        * Avalonia.Matrix.CreateRotation(Rotation * Math.PI / 180)
        * Avalonia.Matrix.CreateTranslation(TranslateX, TranslateY);
}

/// <summary>
/// The crop a native (Windows) video output shows for a placement: the player cannot be transformed, so zoom and pan become a
/// crop rectangle of the decoded video that the output scales to the window. The crop keeps the video's aspect ratio.
/// </summary>
internal readonly record struct VideoNativeCrop(int Left, int Top, int Width, int Height, bool IsFull)
{
    /// <summary>The "WxH+X+Y" form LibVLC's crop geometry takes, or the empty string for the whole picture.</summary>
    internal string Geometry => IsFull ? string.Empty : Width + "x" + Height + "+" + Left + "+" + Top;

    internal static VideoNativeCrop Full(int width, int height) => new(0, 0, Math.Max(0, width), Math.Max(0, height), true);

    /// <summary>
    /// <paramref name="fit"/> is the zoom that fits the (unrotated) video into the window; the view's zoom relative to it says how
    /// much of the picture stays visible. The pan (dip, positive moves the picture right/down) moves the crop the other way.
    /// </summary>
    internal static VideoNativeCrop For(int sourceWidth, int sourceHeight, double fit, double displayScale, GalleryViewPlacement view)
    {
        if (sourceWidth <= 0 || sourceHeight <= 0 || !double.IsFinite(fit) || fit <= 0 || !double.IsFinite(displayScale) || displayScale <= 0
            || !view.Manual || !double.IsFinite(view.Zoom) || view.Zoom <= fit * (1 + 1e-10)) return Full(sourceWidth, sourceHeight);
        double relative = view.Zoom / fit;
        int width = Math.Clamp((int)Math.Round(sourceWidth / relative), 2, sourceWidth);
        int height = Math.Clamp((int)Math.Round(sourceHeight / relative), 2, sourceHeight);
        // dip → source pixels at this zoom (physical pixels per source pixel).
        double panX = double.IsFinite(view.PanX) ? view.PanX : 0, panY = double.IsFinite(view.PanY) ? view.PanY : 0;
        double centreX = sourceWidth / 2.0 - panX * displayScale / view.Zoom;
        double centreY = sourceHeight / 2.0 - panY * displayScale / view.Zoom;
        int left = Math.Clamp((int)Math.Round(centreX - width / 2.0), 0, sourceWidth - width);
        int top = Math.Clamp((int)Math.Round(centreY - height / 2.0), 0, sourceHeight - height);
        return new(left, top, width, height, false);
    }
}
