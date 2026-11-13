using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using MeowChan.Desktop.Stats;

namespace MeowChan.Desktop.Controls;

/// <summary>
/// Pure graph geometry. The source-specific graph uses a stable stepped scale; the legacy combined header/page seam uses
/// the two-series overload. Keeping both contracts lets the existing combined statistics smoke remain additive while the
/// B13 header and page each draw a correctly labelled source.
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

    /// <summary>The combined legacy graph's shared raw scale (network and disk write+read).</summary>
    internal static double Peak(ReadOnlySpan<float> network, ReadOnlySpan<float> disk, double minimumPeak = DefaultMinimumPeak)
    {
        double peak = Math.Max(minimumPeak, 1);
        foreach (float value in network) if (float.IsFinite(value) && value > peak) peak = value;
        foreach (float value in disk) if (float.IsFinite(value) && value > peak) peak = value;
        return peak;
    }

    internal static float[] DiskTotals(IoSeries series)
    {
        float[] total = new float[series.Count];
        for (int i = 0; i < total.Length; i++) total[i] = series.DiskWrite[i] + series.DiskRead[i];
        return total;
    }

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
    NetDown,
    DiskWrite,
    DiskRead
}

/// <summary>
/// A single source graph for the B13 header/statistics layout. When ShowDisk remains true (the old programmatic contract),
/// the control keeps its combined network/disk rendering for the existing readability seam; all new XAML graphs set it false.
/// </summary>
public sealed class IoGraph : Control
{
    public static readonly StyledProperty<IBrush?> StrokeProperty = AvaloniaProperty.Register<IoGraph, IBrush?>(nameof(Stroke));
    public static readonly StyledProperty<IBrush?> FillProperty = AvaloniaProperty.Register<IoGraph, IBrush?>(nameof(Fill));
    public static readonly StyledProperty<IBrush?> DiskStrokeProperty = AvaloniaProperty.Register<IoGraph, IBrush?>(nameof(DiskStroke));
    public static readonly StyledProperty<IBrush?> GuideStrokeProperty = AvaloniaProperty.Register<IoGraph, IBrush?>(nameof(GuideStroke));
    public static readonly StyledProperty<bool> ShowDiskProperty = AvaloniaProperty.Register<IoGraph, bool>(nameof(ShowDisk), true);
    public static readonly StyledProperty<IoGraphSource> SourceProperty = AvaloniaProperty.Register<IoGraph, IoGraphSource>(nameof(Source));
    public static readonly StyledProperty<double> LineThicknessProperty = AvaloniaProperty.Register<IoGraph, double>(nameof(LineThickness), 1.3);

    /// <summary>Zero keeps the page history; a positive value limits this graph to recent seconds.</summary>
    public int HistoryWindowSeconds { get; set; }
    internal Func<int>? SampleIntervalMs { get; set; }

    private float[] _network = [];
    private float[] _disk = [];
    private float[] _values = [];
    private int _capacity = 1;
    private double _tracked;
    private bool _idle = true;
    private StreamGeometry? _area, _line;
    private Size _geometrySize;
    private bool _geometryDirty = true;

    static IoGraph() => AffectsRender<IoGraph>(StrokeProperty, FillProperty, DiskStrokeProperty, GuideStrokeProperty,
        ShowDiskProperty, SourceProperty, LineThicknessProperty);

    public IBrush? Stroke { get => GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }
    public IBrush? Fill { get => GetValue(FillProperty); set => SetValue(FillProperty, value); }
    public IBrush? DiskStroke { get => GetValue(DiskStrokeProperty); set => SetValue(DiskStrokeProperty, value); }
    public IBrush? GuideStroke { get => GetValue(GuideStrokeProperty); set => SetValue(GuideStrokeProperty, value); }
    public bool ShowDisk { get => GetValue(ShowDiskProperty); set => SetValue(ShowDiskProperty, value); }
    public IoGraphSource Source { get => GetValue(SourceProperty); set => SetValue(SourceProperty, value); }
    public double LineThickness { get => GetValue(LineThicknessProperty); set => SetValue(LineThicknessProperty, value); }

    internal double Peak { get; private set; } = IoGraphGeometry.DefaultMinimumPeak;
    internal int SampleCount => ShowDisk ? _network.Length : _values.Length;
    internal bool HasTraffic => ShowDisk ? TrafficPeak > 0 : !_idle;
    internal bool IsIdle => !HasTraffic;
    internal double TrafficPeak { get; private set; }
    internal int WindowIntervals => Math.Max(0, _capacity - 1);

    internal void SetSeries(IoSeries series)
    {
        if (ShowDisk)
        {
            (int skip, int capacity) = IoStatsDisplay.GraphWindow(series, SampleIntervalMs?.Invoke() ?? 1000, HistoryWindowSeconds);
            _network = skip == 0 ? series.NetDown : series.NetDown[skip..];
            float[] disk = IoGraphGeometry.DiskTotals(series);
            _disk = skip == 0 ? disk : disk[skip..];
            _capacity = capacity;
            UpdateCombinedPeak();
            InvalidateVisual();
            return;
        }

        int previousCount = _values.Length;
        IoGraphSource previousSource = _lastSource;
        float[] source = Source switch
        {
            IoGraphSource.DiskWrite => series.DiskWrite,
            IoGraphSource.DiskRead => series.DiskRead,
            _ => series.NetDown
        };
        (int skipSource, int sourceCapacity) = IoStatsDisplay.GraphWindow(series, SampleIntervalMs?.Invoke() ?? 1000, HistoryWindowSeconds);
        _values = skipSource == 0 ? source : source[skipSource..];
        bool reset = _values.Length < previousCount || Source != previousSource;
        _tracked = IoGraphGeometry.Track(reset ? 0 : _tracked, _values);
        _lastSource = Source;
        _capacity = sourceCapacity;
        Peak = IoGraphGeometry.Scale(_tracked);
        TrafficPeak = IoGraphGeometry.Largest(_values);
        _idle = TrafficPeak <= 0;
        _geometryDirty = true;
        InvalidateVisual();
    }

    private IoGraphSource _lastSource;

    private void UpdateCombinedPeak()
    {
        Peak = IoGraphGeometry.Peak(_network, ShowDisk ? _disk : []);
        TrafficPeak = Math.Max(IoGraphGeometry.Largest(_network), ShowDisk ? IoGraphGeometry.Largest(_disk) : 0);
        _idle = TrafficPeak <= 0;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ShowDiskProperty)
        {
            if (ShowDisk) UpdateCombinedPeak();
            else { _tracked = 0; _values = []; _idle = true; Peak = IoGraphGeometry.DefaultMinimumPeak; TrafficPeak = 0; }
            _geometryDirty = true;
            InvalidateVisual();
        }
        else if (change.Property == SourceProperty)
        {
            _tracked = 0; _values = []; _idle = true; Peak = IoGraphGeometry.DefaultMinimumPeak; TrafficPeak = 0;
            _geometryDirty = true;
        }
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (Bounds.Width <= 2 || Bounds.Height <= 2) return;
        double bottom = Bounds.Height - 1, width = Bounds.Width - 2, height = Bounds.Height - 3;
        if (ShowDisk) { RenderCombined(context, width, height, bottom); return; }

        if (GuideStroke is { } guide)
        {
            Pen guidePen = new(guide, 0.5);
            context.DrawLine(guidePen, new Point(1, bottom - 0.5 * height), new Point(width + 1, bottom - 0.5 * height));
            context.DrawLine(guidePen, new Point(1, bottom - height), new Point(width + 1, bottom - height));
        }
        using (context.PushOpacity(_idle ? 0.55 : 0.35))
            context.DrawLine(new Pen(Stroke, 1), new Point(1, bottom), new Point(width + 1, bottom));
        if (_idle || _values.Length < 2) return;
        if (_geometryDirty || _geometrySize != Bounds.Size || _area == null || _line == null) BuildGeometry(width, height, bottom);
        context.DrawGeometry(Fill, null, _area!);
        context.DrawGeometry(null, new Pen(Stroke, LineThickness), _line!);
    }

    private void RenderCombined(DrawingContext context, double width, double height, double bottom)
    {
        if (TrafficPeak > 0 && GuideStroke is { } guide)
        {
            Pen guidePen = new(guide, 0.5);
            foreach (double fraction in new[] { 0.5, 1.0 })
                context.DrawLine(guidePen, new Point(1, bottom - fraction * height), new Point(width + 1, bottom - fraction * height));
        }
        using (context.PushOpacity(TrafficPeak > 0 ? 1 : 0.55))
            context.DrawLine(new Pen(Stroke, 0.4), new Point(1, bottom), new Point(width + 1, bottom));
        if (_network.Length < 2 || TrafficPeak <= 0) return;
        Point[] network = IoGraphGeometry.Points(_network, _capacity, width, height, Peak, 1, bottom);
        Point[] disk = ShowDisk ? IoGraphGeometry.Points(_disk, _capacity, width, height, Peak, 1, bottom) : [];
        if (network.Length < 2) return;
        StreamGeometry area = new();
        using (StreamGeometryContext path = area.Open())
        {
            path.BeginFigure(new Point(network[0].X, bottom), true);
            foreach (Point point in network) path.LineTo(point);
            path.LineTo(new Point(network[^1].X, bottom));
            path.EndFigure(true);
        }
        context.DrawGeometry(Fill, null, area);
        context.DrawGeometry(null, new Pen(Stroke, LineThickness), Line(network));
        if (disk.Length >= 2) context.DrawGeometry(null, new Pen(DiskStroke, LineThickness, new DashStyle([3, 2], 0)), Line(disk));
    }

    private void BuildGeometry(double width, double height, double bottom)
    {
        _geometryDirty = false; _geometrySize = Bounds.Size;
        Point[] points = IoGraphGeometry.Points(_values, _capacity, width, height, Peak, 1, bottom);
        if (points.Length < 2) { _area = new StreamGeometry(); _line = new StreamGeometry(); return; }
        StreamGeometry area = new();
        using (StreamGeometryContext path = area.Open())
        {
            path.BeginFigure(new Point(points[0].X, bottom), true);
            foreach (Point point in points) path.LineTo(point);
            path.LineTo(new Point(points[^1].X, bottom));
            path.EndFigure(true);
        }
        _area = area; _line = Line(points);
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
