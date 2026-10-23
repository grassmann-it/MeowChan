using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using MeowChan.Desktop;
using Ursa.Controls;

namespace MeowChan.ImageViewerComparison;

internal interface IComparisonView
{
    Control Control { get; }
    IImage? Source { get; set; }
    double PhysicalZoom { get; }
    Vector Offset { get; }
    void Fit();
    void Actual();
    void ZoomBy(double factor);
    void PanBy(Vector delta);
}

// Deliberately the production Image + explicit dimensions + TranslateTransform
// approach. GalleryGeometry is linked from the actual decoder source, not copied.
internal sealed class ExistingView : Grid, IComparisonView
{
    private readonly Image _image = new() { Stretch = Stretch.Fill,
        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly TranslateTransform _translation = new();
    private Bitmap? _bitmap;
    private double _zoom = 1;
    private bool _automatic = true;
    private bool _actual;
    private Point? _drag;
    private double Scale => TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
    private double FitZoom => _bitmap == null ? 1 : GalleryGeometry.Fit(_bitmap.PixelSize.Width,
        _bitmap.PixelSize.Height, Bounds.Width, Bounds.Height, Scale);
    public ExistingView()
    {
        ClipToBounds = true; Background = Brushes.Transparent; Focusable = true;
        Name = "ExistingImageViewport";
        _image.RenderTransform = _translation;
        Children.Add(_image);
        SizeChanged += (_, _) => { if (_automatic) Fit(); else Apply(); };
        PointerPressed += (_, e) =>
        {
            if (_bitmap == null || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
            Focus(); _drag = e.GetPosition(this); e.Pointer.Capture(this); e.Handled = true;
        };
        PointerMoved += (_, e) =>
        {
            if (_drag is not { } old) return;
            Point point = e.GetPosition(this); PanBy(point - old); _drag = point;
        };
        PointerReleased += (_, e) => { _drag = null; e.Pointer.Capture(null); };
        PointerCaptureLost += (_, _) => _drag = null;
        PointerWheelChanged += (_, e) =>
        {
            if (_bitmap == null) return;
            Focus(); ZoomAt(GalleryGeometry.WheelFactor(e.Delta.Y), e.GetPosition(this)); e.Handled = true;
        };
        DoubleTapped += (_, e) => { Fit(); e.Handled = true; };
        KeyDown += (_, e) =>
        {
            Vector delta = e.Key switch { Key.Left => new(-10, 0), Key.Right => new(10, 0),
                Key.Up => new(0, -10), Key.Down => new(0, 10), _ => default };
            if (delta != default) { PanBy(delta); e.Handled = true; }
        };
    }
    public Control Control => this;
    public IImage? Source
    {
        get => _image.Source;
        set { _bitmap = (Bitmap?)value; _image.Source = value; Fit(); }
    }
    public double PhysicalZoom => _zoom;
    public Vector Offset => new(_translation.X, _translation.Y);
    public void Fit() { _automatic = true; _actual = false; _zoom = FitZoom; _translation.X = _translation.Y = 0; Apply(); }
    public void Actual() { _automatic = false; _actual = true; _zoom = 1; _translation.X = _translation.Y = 0; Apply(); }
    public void ZoomBy(double factor) => ZoomAt(factor, new Point(Bounds.Width / 2, Bounds.Height / 2));
    private void ZoomAt(double factor, Point point)
    {
        if (_bitmap == null) return;
        double old = _zoom;
        bool belowFit = _actual && FitZoom > 1 && old < FitZoom;
        _zoom = belowFit ? Math.Clamp(old * factor, 1, Math.Max(16, FitZoom))
            : GalleryGeometry.ClampZoom(old * factor, FitZoom);
        _actual = belowFit && _zoom < FitZoom;
        _automatic = !_actual && _zoom <= FitZoom;
        _translation.X = point.X - Bounds.Width / 2 - (point.X - Bounds.Width / 2 - _translation.X) * _zoom / old;
        _translation.Y = point.Y - Bounds.Height / 2 - (point.Y - Bounds.Height / 2 - _translation.Y) * _zoom / old;
        Apply();
    }
    public void PanBy(Vector delta) { _translation.X += delta.X; _translation.Y += delta.Y; Apply(); }
    private void Apply()
    {
        if (_bitmap == null) return;
        _image.Width = _bitmap.PixelSize.Width * _zoom / Scale;
        _image.Height = _bitmap.PixelSize.Height * _zoom / Scale;
        _translation.X = GalleryGeometry.Pan(_translation.X, _bitmap.PixelSize.Width, _zoom, Scale, Bounds.Width);
        _translation.Y = GalleryGeometry.Pan(_translation.Y, _bitmap.PixelSize.Height, _zoom, Scale, Bounds.Height);
    }
}

// Ursa owns its actual presenter and native wheel/drag/key gestures. This small
// adapter supplies physical 1:1, automatic refit and bounds-clamping, absent from
// Ursa's default semantics. No local replacement renderer or decoder is used.
internal sealed class UrsaView : ImageViewer, IComparisonView
{
    protected override Type StyleKeyOverride => typeof(ImageViewer);
    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);
        // Ursa Semi 2.2.0 binds Zoom/Offsets but omits MinZoom/MaxZoom.
        // The presenter's independent default minimum (0.1) otherwise silently
        // crops large images while ImageViewer.Zoom reports the correct Fit.
        if (e.NameScope.Find<ImageViewerPresenter>(PART_Image) is { } presenter)
        {
            presenter.MinZoom = MinZoom;
            presenter.MaxZoom = MaxZoom;
        }
    }
    private bool _automatic = true;
    private bool _setting;
    private double DisplayScale => TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
    public UrsaView()
    {
        Name = "UrsaImageViewport"; ClipToBounds = true; Focusable = true; Background = Brushes.Transparent;
        MinZoom = 0.000001; MaxZoom = 10000; Stretch = Stretch.Uniform;
        SizeChanged += (_, _) => { if (_automatic) Fit(); else ClampPan(); };
        PointerPressed += (_, _) => Focus();
        DoubleTapped += (_, _) => Fit();
        PropertyChanged += (_, e) =>
        {
            if (_setting) return;
            if (e.Property == ZoomProperty || e.Property == OffsetXProperty || e.Property == OffsetYProperty)
            { _automatic = false; ClampPan(); }
        };
    }
    public Control Control => this;
    public double PhysicalZoom => Zoom * DisplayScale;
    public Vector Offset => new(OffsetX, OffsetY);
    public void Fit()
    {
        _setting = true;
        try { FitToView(); _automatic = true; }
        finally { _setting = false; }
    }
    public void Actual()
    {
        _setting = true;
        try { Zoom = 1 / DisplayScale; OffsetX = OffsetY = 0; _automatic = false; }
        finally { _setting = false; }
        ClampPan();
    }
    public void ZoomBy(double factor)
    {
        if (Source == null) return;
        double old = Zoom;
        Zoom = Math.Clamp(old * factor, MinZoom, MaxZoom);
        OffsetX *= Zoom / old; OffsetY *= Zoom / old;
        _automatic = false; ClampPan();
    }
    public void PanBy(Vector delta) { OffsetX += delta.X; OffsetY += delta.Y; ClampPan(); }
    private void ClampPan()
    {
        if (Source is not Bitmap bitmap || _setting) return;
        _setting = true;
        try
        {
            OffsetX = GalleryGeometry.Pan(OffsetX, bitmap.PixelSize.Width, PhysicalZoom, DisplayScale, Bounds.Width);
            OffsetY = GalleryGeometry.Pan(OffsetY, bitmap.PixelSize.Height, PhysicalZoom, DisplayScale, Bounds.Height);
        }
        finally { _setting = false; }
    }
}
