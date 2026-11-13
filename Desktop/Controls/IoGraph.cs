using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using MeowChan.Desktop.Stats;

namespace MeowChan.Desktop.Controls;

/// <summary>
/// Pure graph geometry: a stable stepped scale with a slowly decaying remembered maximum, and thinned sample positions.
/// Every graph has its own scale; the two-direction disk graph shares one scale between its write and read series, never
/// with the network. Values are never summed across series.
/// </summary>
internal static class IoGraphGeometry
{
    internal const double DefaultMinimumPeak = 65536;
    internal const double DefaultDecay = 0.97;

    internal static double Largest(ReadOnlySpan<float> values)
    {
        double largest = 0;
        foreach (float value in values) if (float.IsFinite(value) && value > largest) largest = value;
        return largest;
    }

    internal static double Track(double previous, ReadOnlySpan<float> values, double decay = DefaultDecay)
    {
        double remembered = double.IsFinite(previous) && previous > 0 ? previous * Math.Clamp(decay, 0, 1) : 0;
        return Math.Max(Largest(values), remembered);
    }

    /// <summary>One remembered maximum for two series drawn under one scale: the larger of both, never their sum.</summary>
    internal static double Track(double previous, ReadOnlySpan<float> first, ReadOnlySpan<float> second, double decay = DefaultDecay) =>
        Math.Max(Track(previous, first, decay), Largest(second));

    internal static double Scale(double tracked, double minimumPeak = DefaultMinimumPeak)
    {
        double step = Math.Max(minimumPeak, 1);
        if (!double.IsFinite(tracked)) return step;
        while (step < 1e15)
        {
            if (tracked <= step) return step;
            if (tracked <= step * 1.5) return step * 1.5;
            step *= 2;
        }
        return step;
    }

    internal static double Peak(ReadOnlySpan<float> values, double minimumPeak = DefaultMinimumPeak) =>
        Scale(Largest(values), minimumPeak);

    internal static Point[] Points(ReadOnlySpan<float> values, int capacity, double width, double height, double peak, double left = 0, double bottom = 0)
    {
        int count = values.Length;
        if (count == 0 || width <= 0 || height <= 0 || capacity < 1) return [];
        int limit = Math.Max(2, (int)Math.Ceiling(width * 2));
        int groups = Math.Min(count, limit);
        Point[] points = new Point[groups];
        double span = Math.Max(capacity - 1, 1);
        for (int g = 0; g < groups; g++)
        {
            int start = (int)((long)g * count / groups), end = Math.Max((int)((long)(g + 1) * count / groups), start + 1);
            float value = 0;
            for (int i = start; i < end; i++) if (float.IsFinite(values[i]) && values[i] > value) value = values[i];
            int newest = end - 1;
            double x = left + (capacity - count + newest) * width / span;
            double y = bottom - Math.Min(value / peak, 1.0) * height;
            points[g] = new Point(x, y);
        }
        return points;
    }
}

public enum IoGraphSource
{
    /// <summary>Network download only. Upload is not measured, so no graph claims to show it.</summary>
    NetDown,
    DiskWrite,
    DiskRead,
    /// <summary>
    /// Two directions of the disk in one graph: writing as the primary series (solid line over its filled area) and reading as
    /// the secondary series (dashed line, no area), both under one shared disk scale that never includes the network.
    /// </summary>
    DiskIo
}

/// <summary>
/// One source per graph, chosen by <see cref="Source"/>. <see cref="IoGraphSource.DiskIo"/> is the only two-series mode; it
/// draws disk write and disk read as two separate curves and never adds them up. The old combined network-plus-disk graph is
/// gone: <see cref="ShowDisk"/> and <see cref="DiskStroke"/> stay only so existing markup still compiles and have no mode of
/// their own.
/// </summary>
public sealed class IoGraph : Control
{
    /// <summary>The dash pattern of the secondary series, in multiples of the line thickness.</summary>
    internal static readonly IReadOnlyList<double> SecondaryDashes = [3, 2];

    public static readonly StyledProperty<IBrush?> StrokeProperty = AvaloniaProperty.Register<IoGraph, IBrush?>(nameof(Stroke));
    public static readonly StyledProperty<IBrush?> FillProperty = AvaloniaProperty.Register<IoGraph, IBrush?>(nameof(Fill));
    public static readonly StyledProperty<IBrush?> SecondaryStrokeProperty = AvaloniaProperty.Register<IoGraph, IBrush?>(nameof(SecondaryStroke));
    public static readonly StyledProperty<IBrush?> DiskStrokeProperty = AvaloniaProperty.Register<IoGraph, IBrush?>(nameof(DiskStroke));
    public static readonly StyledProperty<IBrush?> GuideStrokeProperty = AvaloniaProperty.Register<IoGraph, IBrush?>(nameof(GuideStroke));
    public static readonly StyledProperty<bool> ShowDiskProperty = AvaloniaProperty.Register<IoGraph, bool>(nameof(ShowDisk));
    public static readonly StyledProperty<IoGraphSource> SourceProperty = AvaloniaProperty.Register<IoGraph, IoGraphSource>(nameof(Source));
    public static readonly StyledProperty<double> LineThicknessProperty = AvaloniaProperty.Register<IoGraph, double>(nameof(LineThickness), 1.3);

    /// <summary>Zero keeps the page history; a positive value limits this graph to recent seconds.</summary>
    public int HistoryWindowSeconds { get; set; }
    internal Func<int>? SampleIntervalMs { get; set; }

    private float[] _values = [];
    private float[] _secondary = [];
    private int _capacity = 1;
    private double _tracked;
    private bool _idle = true;
    private IoGraphSource _lastSource;
    private StreamGeometry? _area, _line, _secondaryLine;
    private Size _geometrySize;
    private bool _geometryDirty = true;

    static IoGraph() => AffectsRender<IoGraph>(StrokeProperty, FillProperty, SecondaryStrokeProperty, DiskStrokeProperty, GuideStrokeProperty,
        SourceProperty, LineThicknessProperty);

    public IBrush? Stroke { get => GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }
    public IBrush? Fill { get => GetValue(FillProperty); set => SetValue(FillProperty, value); }
    /// <summary>The stroke of the dashed secondary series (disk read in <see cref="IoGraphSource.DiskIo"/>).</summary>
    public IBrush? SecondaryStroke { get => GetValue(SecondaryStrokeProperty); set => SetValue(SecondaryStrokeProperty, value); }
    /// <summary>Markup compatibility: used for the secondary series only when <see cref="SecondaryStroke"/> is not set.</summary>
    public IBrush? DiskStroke { get => GetValue(DiskStrokeProperty); set => SetValue(DiskStrokeProperty, value); }
    public IBrush? GuideStroke { get => GetValue(GuideStrokeProperty); set => SetValue(GuideStrokeProperty, value); }
    /// <summary>Markup compatibility only. Has no effect: there is no combined network-plus-disk graph any more.</summary>
    public bool ShowDisk { get => GetValue(ShowDiskProperty); set => SetValue(ShowDiskProperty, value); }
    public IoGraphSource Source { get => GetValue(SourceProperty); set => SetValue(SourceProperty, value); }
    public double LineThickness { get => GetValue(LineThicknessProperty); set => SetValue(LineThicknessProperty, value); }

    /// <summary>The scale of the graph; in <see cref="IoGraphSource.DiskIo"/> one scale for write and read together.</summary>
    internal double Peak { get; private set; } = IoGraphGeometry.DefaultMinimumPeak;
    internal int SampleCount => _values.Length;
    internal bool HasTraffic => !_idle;
    internal bool IsIdle => _idle;
    /// <summary>The largest visible value of any drawn series (never a sum of two series).</summary>
    internal double TrafficPeak { get; private set; }
    /// <summary>The largest visible value of the primary series (disk write in <see cref="IoGraphSource.DiskIo"/>).</summary>
    internal double PrimaryTrafficPeak { get; private set; }
    /// <summary>The largest visible value of the secondary series; zero outside <see cref="IoGraphSource.DiskIo"/>.</summary>
    internal double SecondaryTrafficPeak { get; private set; }
    internal bool HasSecondarySeries => Source == IoGraphSource.DiskIo;
    internal int SecondarySampleCount => _secondary.Length;
    internal int WindowIntervals => Math.Max(0, _capacity - 1);
    internal IBrush? EffectiveSecondaryStroke => SecondaryStroke ?? DiskStroke ?? Stroke;

    internal void SetSeries(IoSeries series)
    {
        int previousCount = _values.Length;
        IoGraphSource source = Source;
        float[] primary = source switch
        {
            IoGraphSource.DiskWrite or IoGraphSource.DiskIo => series.DiskWrite,
            IoGraphSource.DiskRead => series.DiskRead,
            _ => series.NetDown
        };
        float[]? secondary = source == IoGraphSource.DiskIo ? series.DiskRead : null;
        (int skip, int capacity) = IoStatsDisplay.GraphWindow(series, SampleIntervalMs?.Invoke() ?? 1000, HistoryWindowSeconds);
        // The graph only reads the snapshot; it never writes into the arrays it shares with the page history.
        _values = Window(primary, skip);
        _secondary = secondary is null ? [] : Window(secondary, skip);
        bool reset = _values.Length < previousCount || source != _lastSource;
        _tracked = IoGraphGeometry.Track(reset ? 0 : _tracked, _values, _secondary);
        _lastSource = source;
        _capacity = capacity;
        Peak = IoGraphGeometry.Scale(_tracked);
        PrimaryTrafficPeak = IoGraphGeometry.Largest(_values);
        SecondaryTrafficPeak = IoGraphGeometry.Largest(_secondary);
        TrafficPeak = Math.Max(PrimaryTrafficPeak, SecondaryTrafficPeak);
        _idle = TrafficPeak <= 0;
        _geometryDirty = true;
        InvalidateVisual();
    }

    private static float[] Window(float[] values, int skip) => skip <= 0 ? values : values[Math.Min(skip, values.Length)..];

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SourceProperty)
        {
            _tracked = 0; _values = []; _secondary = []; _idle = true;
            Peak = IoGraphGeometry.DefaultMinimumPeak; TrafficPeak = PrimaryTrafficPeak = SecondaryTrafficPeak = 0;
            _geometryDirty = true;
        }
    }

    /// <summary>
    /// The drawn positions of both series for a control size, exactly as <see cref="Render"/> uses them. A series without any
    /// positive visible value yields no points, so a silent direction never covers the baseline or the other curve.
    /// </summary>
    internal (Point[] Primary, Point[] Secondary) Layout(Size size)
    {
        if (size.Width <= 2 || size.Height <= 2 || _idle) return ([], []);
        double bottom = size.Height - 1, width = size.Width - 2, height = size.Height - 3;
        Point[] primary = PrimaryTrafficPeak > 0 ? IoGraphGeometry.Points(_values, _capacity, width, height, Peak, 1, bottom) : [];
        Point[] secondary = SecondaryTrafficPeak > 0 ? IoGraphGeometry.Points(_secondary, _capacity, width, height, Peak, 1, bottom) : [];
        return (primary.Length >= 2 ? primary : [], secondary.Length >= 2 ? secondary : []);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (Bounds.Width <= 2 || Bounds.Height <= 2) return;
        double bottom = Bounds.Height - 1, width = Bounds.Width - 2, height = Bounds.Height - 3;

        if (GuideStroke is { } guide)
        {
            Pen guidePen = new(guide, 0.5);
            context.DrawLine(guidePen, new Point(1, bottom - 0.5 * height), new Point(width + 1, bottom - 0.5 * height));
            context.DrawLine(guidePen, new Point(1, bottom - height), new Point(width + 1, bottom - height));
        }
        using (context.PushOpacity(_idle ? 0.55 : 0.35))
            context.DrawLine(new Pen(Stroke, 1), new Point(1, bottom), new Point(width + 1, bottom));
        if (_idle || _values.Length < 2) return;
        if (_geometryDirty || _geometrySize != Bounds.Size) BuildGeometry(bottom); // cached until new samples, a new source or a new size
        if (_area is { } area) context.DrawGeometry(Fill, null, area);
        if (_line is { } line) context.DrawGeometry(null, new Pen(Stroke, LineThickness), line);
        // Read lies over write: where both curves meet, the gaps of the dashes keep the solid write line visible.
        if (_secondaryLine is { } secondaryLine)
            context.DrawGeometry(null, new Pen(EffectiveSecondaryStroke, LineThickness, new DashStyle(SecondaryDashes, 0)), secondaryLine);
    }

    private void BuildGeometry(double bottom)
    {
        _geometryDirty = false; _geometrySize = Bounds.Size;
        (Point[] primary, Point[] secondary) = Layout(Bounds.Size);
        _area = primary.Length >= 2 ? Area(primary, bottom) : null;
        _line = primary.Length >= 2 ? Line(primary) : null;
        _secondaryLine = secondary.Length >= 2 ? Line(secondary) : null;
    }

    private static StreamGeometry Area(Point[] points, double bottom)
    {
        StreamGeometry area = new();
        using (StreamGeometryContext path = area.Open())
        {
            path.BeginFigure(new Point(points[0].X, bottom), true);
            foreach (Point point in points) path.LineTo(point);
            path.LineTo(new Point(points[^1].X, bottom));
            path.EndFigure(true);
        }
        return area;
    }

    private static StreamGeometry Line(Point[] points)
    {
        StreamGeometry line = new();
        using (StreamGeometryContext path = line.Open())
        {
            path.BeginFigure(points[0], false);
            for (int i = 1; i < points.Length; i++) path.LineTo(points[i]);
            path.EndFigure(false);
        }
        return line;
    }
}
