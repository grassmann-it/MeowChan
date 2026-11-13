using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using MeowChan.Desktop.Stats;

namespace MeowChan.Desktop.Controls;

/// <summary>
/// The pure geometry of <see cref="IoGraph"/>: the shared scale and the sample positions. Kept apart from drawing so the
/// scale rule (one peak for both rows, never below the minimum) and the thinning of long histories can be tested.
/// </summary>
internal static class IoGraphGeometry
{
    /// <summary>The smallest top of the scale: 64 KiB/s, so a trickle does not fill the graph.</summary>
    internal const double DefaultMinimumPeak = 65536;

    /// <summary>The largest of both rows and <paramref name="minimumPeak"/>. Both rows share this one scale.</summary>
    internal static double Peak(ReadOnlySpan<float> network, ReadOnlySpan<float> disk, double minimumPeak = DefaultMinimumPeak)
    {
        double peak = Math.Max(minimumPeak, 1);
        foreach (float value in network) if (float.IsFinite(value) && value > peak) peak = value;
        foreach (float value in disk) if (float.IsFinite(value) && value > peak) peak = value;
        return peak;
    }

    /// <summary>The disk row: write plus read of every sample, in the same unit as the network row.</summary>
    internal static float[] DiskTotals(IoSeries series)
    {
        float[] total = new float[series.Count];
        for (int i = 0; i < total.Length; i++) total[i] = series.DiskWrite[i] + series.DiskRead[i];
        return total;
    }

    /// <summary>
    /// Positions of one row inside <paramref name="width"/> by <paramref name="height"/>. The newest sample sits at the right
    /// edge and the row grows leftwards until <paramref name="capacity"/> samples exist. A history longer than the width
    /// can show is thinned to the largest value of each group, so a short spike never disappears.
    /// </summary>
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
            // Each group covers [start, end) of the samples; its x is the newest sample's, its value the largest.
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

/// <summary>
/// The network and disk graph of the header and the statistics page: both rows on one shared scale. The network row is a
/// filled area with a solid line, the disk row (write plus read) a dashed line, so the rows differ by form and not only by
/// colour. It draws on <see cref="SetSeries"/> only, never on its own: no timer, no animation.
/// </summary>
public sealed class IoGraph : Control
{
    public static readonly StyledProperty<IBrush?> StrokeProperty = AvaloniaProperty.Register<IoGraph, IBrush?>(nameof(Stroke));
    public static readonly StyledProperty<IBrush?> FillProperty = AvaloniaProperty.Register<IoGraph, IBrush?>(nameof(Fill));
    public static readonly StyledProperty<IBrush?> DiskStrokeProperty = AvaloniaProperty.Register<IoGraph, IBrush?>(nameof(DiskStroke));
    public static readonly StyledProperty<IBrush?> GuideStrokeProperty = AvaloniaProperty.Register<IoGraph, IBrush?>(nameof(GuideStroke));
    public static readonly StyledProperty<bool> ShowDiskProperty = AvaloniaProperty.Register<IoGraph, bool>(nameof(ShowDisk), true);
    public static readonly StyledProperty<double> LineThicknessProperty = AvaloniaProperty.Register<IoGraph, double>(nameof(LineThickness), 1.3);

    /// <summary>Zero keeps the page history; a positive value limits the header to recent seconds.</summary>
    public int HistoryWindowSeconds { get; set; }

    // Supplied by the shell, not a timer or a second hub subscription. Read on every snapshot so settings apply at once.
    internal Func<int>? SampleIntervalMs { get; set; }

    private float[] _network = [];
    private float[] _disk = [];
    private int _capacity = 1;

    static IoGraph() => AffectsRender<IoGraph>(StrokeProperty, FillProperty, DiskStrokeProperty, GuideStrokeProperty, LineThicknessProperty, ShowDiskProperty);

    /// <summary>Network line and baseline.</summary>
    public IBrush? Stroke { get => GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }
    /// <summary>Network area.</summary>
    public IBrush? Fill { get => GetValue(FillProperty); set => SetValue(FillProperty, value); }
    /// <summary>Disk line; opaque, dashed.</summary>
    public IBrush? DiskStroke { get => GetValue(DiskStrokeProperty); set => SetValue(DiskStrokeProperty, value); }
    /// <summary>Optional horizontal guides at half and full scale (the statistics page); none when null.</summary>
    public IBrush? GuideStroke { get => GetValue(GuideStrokeProperty); set => SetValue(GuideStrokeProperty, value); }
    /// <summary>Whether the dashed disk row is drawn. When it is not, the scale follows the network row alone.</summary>
    public bool ShowDisk { get => GetValue(ShowDiskProperty); set => SetValue(ShowDiskProperty, value); }
    public double LineThickness { get => GetValue(LineThicknessProperty); set => SetValue(LineThicknessProperty, value); }

    /// <summary>The top of the scale used by the last drawing input, in bytes per second.</summary>
    internal double Peak { get; private set; } = IoGraphGeometry.DefaultMinimumPeak;
    internal int SampleCount => _network.Length;
    internal bool HasTraffic => TrafficPeak > 0;
    internal double TrafficPeak { get; private set; }
    internal int WindowIntervals => Math.Max(0, _capacity - 1);

    internal void SetSeries(IoSeries series)
    {
        (int skip, int capacity) = IoStatsDisplay.GraphWindow(series, SampleIntervalMs?.Invoke() ?? 1000, HistoryWindowSeconds);
        _network = skip == 0 ? series.NetDown : series.NetDown[skip..];
        float[] disk = IoGraphGeometry.DiskTotals(series);
        _disk = skip == 0 ? disk : disk[skip..];
        _capacity = capacity;
        UpdatePeak();
        InvalidateVisual();
    }

    private void UpdatePeak()
    {
        Peak = IoGraphGeometry.Peak(_network, ShowDisk ? _disk : []);
        double actual = 0;
        foreach (float value in _network) if (float.IsFinite(value) && value > actual) actual = value;
        if (ShowDisk) foreach (float value in _disk) if (float.IsFinite(value) && value > actual) actual = value;
        TrafficPeak = actual;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ShowDiskProperty) UpdatePeak();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (Bounds.Width <= 2 || Bounds.Height <= 2) return;
        double thickness = LineThickness;
        double bottom = Bounds.Height - 1;
        double width = Bounds.Width - 2;
        double height = Bounds.Height - 3;
        if (HasTraffic && GuideStroke is { } guide)
        {
            Pen guidePen = new(guide, 0.5);
            foreach (double fraction in new[] { 0.5, 1.0 })
                context.DrawLine(guidePen, new Point(1, bottom - fraction * height), new Point(width + 1, bottom - fraction * height));
        }
        context.DrawLine(new Pen(Stroke, 0.4), new Point(1, bottom), new Point(width + 1, bottom));
        if (_network.Length < 2) return;
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
        context.DrawGeometry(null, new Pen(Stroke, thickness), Line(network));
        // The dashed row goes last so it stays readable over the area.
        if (disk.Length >= 2) context.DrawGeometry(null, new Pen(DiskStroke, thickness, new DashStyle([3, 2], 0)), Line(disk));
    }

    private static StreamGeometry Line(Point[] points)
    {
        StreamGeometry line = new();
        using StreamGeometryContext path = line.Open();
        path.BeginFigure(points[0], false);
        for (int i = 1; i < points.Length; i++) path.LineTo(points[i]);
        path.EndFigure(false);
        return line;
    }
}
