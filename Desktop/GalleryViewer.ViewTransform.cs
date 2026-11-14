using System;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Threading;
using MeowChan.Desktop.Localization;
using MeowChan.Gallery;
using MeowChan.Archive;

namespace MeowChan.Desktop;

// Zoom, pan, Fit, 1:1 and rotation of the shown medium (plan V4). Images and videos share one set of rules
// (GalleryViewMath): the same keys, steps, wheel factor, Fit and 1:1 logic, the same configurable maximum. Only the surface
// differs. An image is a bitmap sized to the wanted zoom; a video on Linux and macOS is a render transform of its software
// surface (the decoded frames are never re-decoded); on Windows the player is cropped (zoom, pan) or restarted with LibVLC's
// transform filter (rotation). Rotation is a view transform, never a file change, and starts at 0° with every new medium.
internal sealed partial class GalleryViewer
{
    /// <summary>Wheel and key steps bundle into one spoken message after this long (documented fixed delay).</summary>
    internal const int ViewAnnouncementDelayMs = 400;

    private int _rotation;
    private int _maxZoomPercent = GalleryZoomLimits.MaxPercentDefault;
    private bool _announceZoom, _announceRotation;
    private readonly DispatcherTimer _announceTimer = new() { Interval = TimeSpan.FromMilliseconds(ViewAnnouncementDelayMs) };
    // A polite live region (screen readers): one short line, invisible, never in the way of the pointer.
    private readonly TextBlock _viewAnnouncement = new()
    {
        Name = "GalleryViewAnnouncement", Width = 1, Height = 1, Opacity = 0, IsHitTestVisible = false,
        HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top
    };

    /// <summary>The shown medium's rotation in degrees clockwise (0, 90, 180, 270).</summary>
    internal int Rotation => _rotation;
    internal bool IsViewManuallyZoomed => _manualZoom;
    internal double ViewZoom => _zoom;
    internal string ViewAnnouncementText => _viewAnnouncement.Text ?? string.Empty;
    internal Button RotateButton => _rotate;

    /// <summary>Sets the largest zoom (percent of the source size); the default keeps the image viewer's long-standing 1600 %.</summary>
    internal void ConfigureZoomLimits(int maxPercent)
    {
        maxPercent = GalleryZoomLimits.ClampPercent(maxPercent);
        if (_maxZoomPercent == maxPercent) return;
        _maxZoomPercent = maxPercent;
        if (CurrentGeometry != null) FitIfAutomatic();
    }

    private void InitializeViewTransform()
    {
        AutomationProperties.SetLiveSetting(_viewAnnouncement, AutomationLiveSetting.Polite);
        AutomationProperties.SetAccessibilityView(_viewAnnouncement, AccessibilityView.Content);
        _viewport.Children.Add(_viewAnnouncement);
        _announceTimer.Tick += (_, _) => { _announceTimer.Stop(); SpeakView(); };
    }

    private bool IsVideoView => _entry?.Kind == SavedMediaKind.Video && _video is { IsVisible: true };

    /// <summary>The shown medium's source size: the decoded image, or the video once its first frame (or size) is known.</summary>
    private (int Width, int Height)? MediaSize => IsVideoView
        ? _video!.SourceSize
        : _info is { Width: > 0, Height: > 0 } info ? (info.Width, info.Height) : null;

    private Size ViewportSize => IsVideoView ? _video!.SurfaceSize : _viewport.Bounds.Size;

    /// <summary>The scene of the shown medium, or null while there is nothing to place (no decode yet, no video frame yet).</summary>
    private GalleryViewGeometry? CurrentGeometry => MediaSize is { } size
        ? new GalleryViewGeometry(size.Width, size.Height, ViewportSize.Width, ViewportSize.Height, DisplayScale,
            // Fit enlargement is limited for images only; a video has always filled its area.
            IsVideoView ? null : FitUpscaleCap, _maxZoomPercent, _rotation)
        : null;

    private GalleryViewPlacement Placement => new(_zoom, _panX, _panY, _manualZoom, _actualZoom);

    private void Adopt(GalleryViewPlacement placement)
    {
        _zoom = placement.Zoom; _panX = placement.PanX; _panY = placement.PanY;
        _manualZoom = placement.Manual; _actualZoom = placement.Actual;
    }

    private double CurrentFit => CurrentGeometry?.Fit ?? 1;

    /// <summary>
    /// One zoom step. <paramref name="centerOffset"/> is the pointer's offset from the centre of the picture area (keys zoom
    /// about the centre).
    /// </summary>
    private void Zoom(double factor, Point? centerOffset = null)
    {
        if (CurrentGeometry is not { } geometry) return;
        GalleryViewPlacement before = Placement;
        GalleryViewPlacement next = GalleryViewMath.ZoomBy(before, geometry, factor, centerOffset is { } offset ? (offset.X, offset.Y) : null);
        if (next == before) return;
        Adopt(next);
        if (!next.Manual) { Fit(retry: false); AnnounceView(zoom: true); return; }
        ApplyTransform();
        if (!IsVideoView) _ = EnsureDetailAsync();
        AnnounceView(zoom: true);
    }

    private void ActualPixels()
    {
        if (CurrentGeometry is not { } geometry) return;
        Adopt(GalleryViewMath.ActualPixels(geometry));
        ApplyTransform();
        if (!IsVideoView) _ = EnsureDetailAsync();
        AnnounceView(zoom: true);
    }

    private void FitIfAutomatic()
    {
        if (CurrentGeometry is not { } geometry) return;
        GalleryViewPlacement next = GalleryViewMath.IfAutomatic(Placement, geometry);
        if (!next.Manual) { Fit(retry: false); return; }
        Adopt(next);
        ApplyTransform();
    }

    private void Fit() { Fit(retry: true); AnnounceView(zoom: true); }

    private void Fit(bool retry)
    {
        if (CurrentGeometry is not { } geometry) return;
        Adopt(GalleryViewMath.Fitted(geometry));
        ApplyTransform();
        if (!IsVideoView) _ = RequestDecodeAsync(CurrentFitRequest(), debounce: !retry, retry: retry);
    }

    private void ApplyTransform()
    {
        if (CurrentGeometry is not { } geometry) return;
        if (!IsVideoView)
        {
            double scale = DisplayScale;
            _image.Width = geometry.SourceWidth * _zoom / scale; _image.Height = geometry.SourceHeight * _zoom / scale;
        }
        ApplyPan();
        UpdateQuality();
    }

    // The pan is kept inside the medium's edges and pushed to the surface (the image's transform, the video's matrix or crop).
    private void ApplyPan()
    {
        if (CurrentGeometry is not { } geometry) return;
        Adopt(GalleryViewMath.Clamped(Placement, geometry));
        if (IsVideoView) _video!.ApplyViewTransform(geometry, Placement, DisplayScale);
        else
        {
            _imageRotation.Angle = _rotation;
            _imageTranslation.X = _panX; _imageTranslation.Y = _panY;
        }
    }

    private void UpdateQuality()
    {
        if (MediaSize is not { } size) return;
        string zoom = _manualZoom ? (_zoom * 100).ToString("0", Texts.Culture) + "%" : Texts.T("gallery.fit");
        string rotation = _rotation == 0 ? string.Empty : " · " + Texts.T("viewer.view.rotationState", _rotation.ToString(Texts.Culture));
        if (IsVideoView) { _quality.Text = size.Width + " × " + size.Height + " · " + zoom + rotation; return; }
        if (_info == null) return;
        _quality.Text = _info.Width + " × " + _info.Height + " · " + _info.Format + " · " + zoom + rotation +
            (_manualZoom && !HasOriginal ? Texts.T("gallery.viewer11") : string.Empty) +
            (_info.StillFrame ? Texts.T("gallery.viewer12") : string.Empty);
    }

    // ---- rotation ---------------------------------------------------------------------------------------------------

    /// <summary>Turns the shown medium by <paramref name="steps"/> quarter turns clockwise (a negative count turns it back).</summary>
    internal void RotateView(int steps = 1)
    {
        if (steps == 0 || CurrentGeometry is not { } before) return;
        // Windows: a playing video that cannot reopen at its position cannot turn, so the view must not claim a turn either.
        if (IsVideoView && _video is { TurnsNow: false }) return;
        int next = GalleryRotation.Normalize(_rotation + steps * GalleryRotation.Step);
        GalleryViewGeometry after = before with { Rotation = next };
        GalleryViewPlacement placement = GalleryViewMath.Rotated(Placement, after, steps);
        _rotation = next;
        Adopt(placement);
        if (!IsVideoView && !_manualZoom) Fit(retry: false); // The decode target follows the new orientation.
        else
        {
            ApplyTransform();
            if (!IsVideoView) _ = EnsureDetailAsync();
        }
        AnnounceView(zoom: true, rotation: true);
    }

    // A new medium starts upright; the same medium shown again (a video back from the reader) keeps its rotation.
    private void ResetViewForMedium(GalleryEntry entry)
    {
        bool same = _entry != null && _entry.Key == entry.Key && _entry.Post?.PostId == entry.Post?.PostId
            && _entry.Attachment?.Position == entry.Attachment?.Position;
        _rotation = GalleryViewMath.RotationForMedium(same, _rotation);
        _imageRotation.Angle = _rotation;
        _announceZoom = _announceRotation = false;
        _announceTimer.Stop();
    }

    // ---- pointer ----------------------------------------------------------------------------------------------------

    private bool IsOverVideoSurface(object? source, Control root) => ReferenceEquals(root, _nativeVideoOverlay)
        ? ReferenceEquals(source, root)
        : ReferenceEquals(source, _viewport) || _video?.OwnsSurface(source) == true;

    private void OnViewWheel(object? sender, PointerWheelEventArgs e)
    {
        if (sender is not Control root) return;
        if (IsVideoView)
        {
            // Over the transport (docked, not overlaid) the wheel belongs to its sliders, not to the picture.
            if (!IsOverVideoSurface(e.Source, root) || CurrentGeometry == null) return;
        }
        else if (ReferenceEquals(root, _nativeVideoOverlay)) return;
        Focus();
        Point point = e.GetPosition(root);
        Zoom(GalleryGeometry.WheelFactor(e.Delta.Y), new Point(point.X - root.Bounds.Width / 2, point.Y - root.Bounds.Height / 2));
        e.Handled = true;
    }

    private void OnViewPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        if (sender is Control root && ReferenceEquals(e.Pointer.Captured, root)) e.Pointer.Capture(null);
    }

    private void OnViewPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_dragging || !_manualZoom || sender is not Control root) return;
        Point point = e.GetPosition(root);
        _panX += point.X - _dragPoint.X; _panY += point.Y - _dragPoint.Y; _dragPoint = point; ApplyPan();
    }

    private void OnViewDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is not Control root) return;
        if (IsVideoView)
        {
            if (!IsOverVideoSurface(e.Source, root) || CurrentGeometry == null) return;
        }
        else if (ReferenceEquals(root, _nativeVideoOverlay)) return;
        if (_manualZoom) Fit(); else ActualPixels();
        e.Handled = true;
    }

    /// <summary>The video's size or area changed (first frame, resolution change, new layout): Fit and the transform follow.</summary>
    private void OnVideoViewSurfaceChanged()
    {
        if (_terminalRetirement || !IsVideoView) return;
        bool known = MediaSize != null;
        SetImageCommands(known);
        if (!known) { _quality.Text = string.Empty; return; }
        FitIfAutomatic();
        UpdateQuality();
    }

    // ---- spoken state -----------------------------------------------------------------------------------------------

    private void AnnounceView(bool zoom = false, bool rotation = false)
    {
        _announceZoom |= zoom; _announceRotation |= rotation;
        _announceTimer.Stop(); _announceTimer.Start();
    }

    private void SpeakView()
    {
        string text = GalleryViewAnnouncement.Compose(_announceZoom, _announceRotation, _manualZoom, _zoom, _rotation);
        _announceZoom = _announceRotation = false;
        if (text.Length == 0) return;
        // An unchanged sentence is announced again only when the text actually changes: clear first.
        _viewAnnouncement.Text = string.Empty;
        _viewAnnouncement.Text = text;
    }
}

/// <summary>The one polite sentence that says where the picture is after zoom keys, the wheel, Fit, 1:1 or a rotation.</summary>
internal static class GalleryViewAnnouncement
{
    internal static string Compose(bool zoom, bool rotation, bool manualZoom, double physicalZoom, int degrees)
    {
        var parts = new System.Collections.Generic.List<string>(2);
        degrees = GalleryRotation.Normalize(degrees);
        if (zoom)
            parts.Add(manualZoom
                ? Texts.T("viewer.view.zoomAnnounce", Math.Round(physicalZoom * 100).ToString("0", Texts.Culture))
                : Texts.T("viewer.view.fitAnnounce"));
        if (rotation)
            parts.Add(degrees == 0 ? Texts.T("viewer.view.rotationOff")
                : Texts.T("viewer.view.rotationAnnounce", degrees.ToString(Texts.Culture)));
        return string.Join(", ", parts);
    }
}
