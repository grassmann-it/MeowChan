using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using MeowChan.Desktop.Stats;

namespace MeowChan.Desktop.Controls;

/// <summary>
/// The pure geometry of <see cref="IoGraph"/>: the scale of one row and the sample positions. Kept apart from drawing so the
/// scale rule (one stable maximum per row, never below the minimum) and the thinning of long histories can be tested.
/// </summary>
internal static class IoGraphGeometry
{
    /// <summary>The smallest top of a row's scale: 64 KiB/s, so a trickle does not fill the graph.</summary>
    internal const double DefaultMinimumPeak = 65536;

    /// <summary>
    /// How much of the remembered maximum survives one sample once the history no longer reaches it. 0.97 halves it in about
    /// 23 samples, so a spike that leaves the history lets the scale sink slowly instead of jumping.
    /// </summary>
    internal const double DefaultDecay = 0.97;

    /// <summary>The largest finite value of <paramref name="values"/>; zero for none.</summary>
    internal static double Largest(ReadOnlySpan<float> values)
    {
        double largest = 0;
        foreach (float value in values) if (float.IsFinite(value) && value > largest) largest = value;
        return largest;
    }

    /// <summary>
    /// The maximum a row remembers: the largest visible value, or the previous maximum decayed by one sample, whichever is
    /// larger. A new peak is followed at once; it sinks only after it left the visible history.
    /// </summary>
    internal static double Track(double previous, ReadOnlySpan<float> values, double decay = DefaultDecay)
    {
        double remembered = double.IsFinite(previous) && previous > 0 ? previous * Math.Clamp(decay, 0, 1) : 0;
        return Math.Max(Largest(values), remembered);
    }

    /// <summary>
    /// The top of a row's scale: the smallest step that holds <paramref name="tracked"/>, the steps being
    /// <paramref name="minimumPeak"/> times 1, 1.5, 2, 3, 4, 6, 8 ... (so 64, 96, 128, 192, 256, 384 KiB ... 1, 1.5, 2 MiB).
    /// The scale stays put while the values wander, never wastes more than a third of the height, and a drawn guide always
    /// sits on a round value.
    /// </summary>
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

    /// <summary>The scale of one row for a history seen once: <see cref="Scale"/> of its largest value.</summary>
    internal static double Peak(ReadOnlySpan<float> values, double minimumPeak = DefaultMinimumPeak) => Scale(Largest(values), minimumPeak);

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

/// <summary>The row of <see cref="IoSeries"/> an <see cref="IoGraph"/> draws.</summary>
public enum IoGraphSource
{
    /// <summary>Network download.</summary>
    NetDown,
    /// <summary>Bytes MeowChan writes to disk (what it archives).</summary>
    DiskWrite,
    /// <summary>Bytes MeowChan reads back from disk.</summary>
    DiskRead
}

/// <summary>
/// One row of the header and the statistics page: a filled area with a solid line over its own history, on its own scale.
/// There is no shared axis; each graph keeps a stable maximum (<see cref="IoGraphGeometry.Track"/>), and an idle row draws
/// a quiet baseline instead of nothing. It draws on <see cref="SetSeries"/> only, never on its own: no timer, no animation,
/// and the geometry is rebuilt only when the series or the size changed.
/// </summary>
public sealed class IoGraph : Control
{
    public static readonly StyledProperty<IBrush?> StrokeProperty = AvaloniaProperty.Register<IoGraph, IBrush?>(nameof(Stroke));
    public static readonly StyledProperty<IBrush?> FillProperty = AvaloniaProperty.Register<IoGraph, IBrush?>(nameof(Fill));
    public static readonly StyledProperty<IBrush?> GuideStrokeProperty = AvaloniaProperty.Register<IoGraph, IBrush?>(nameof(GuideStroke));
    public static readonly StyledProperty<IoGraphSource> SourceProperty = AvaloniaProperty.Register<IoGraph, IoGraphSource>(nameof(Source));
    public static readonly StyledProperty<double> LineThicknessProperty = AvaloniaProperty.Register<IoGraph, double>(nameof(LineThickness), 1.3);

    private float[] _values = [];
    private int _capacity = 1;
    private double _tracked;
    private bool _idle = true;
    private StreamGeometry? _area, _line;
    private Size _geometrySize;
    private bool _geometryDirty = true;

    static IoGraph() => AffectsRender<IoGraph>(StrokeProperty, FillProperty, GuideStrokeProperty, LineThicknessProperty, SourceProperty);

    /// <summary>The line and the baseline.</summary>
    public IBrush? Stroke { get => GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }
    /// <summary>The area under the line.</summary>
    public IBrush? Fill { get => GetValue(FillProperty); set => SetValue(FillProperty, value); }
    /// <summary>Optional horizontal guides at half and full scale (the statistics page); none when null.</summary>
    public IBrush? GuideStroke { get => GetValue(GuideStrokeProperty); set => SetValue(GuideStrokeProperty, value); }
    /// <summary>Which row of the series this graph draws.</summary>
    public IoGraphSource Source { get => GetValue(SourceProperty); set => SetValue(SourceProperty, value); }
    public double LineThickness { get => GetValue(LineThicknessProperty); set => SetValue(LineThicknessProperty, value); }

    /// <summary>The top of the scale used by the last drawing input, in bytes per second.</summary>
    internal double Peak { get; private set; } = IoGraphGeometry.DefaultMinimumPeak;
    internal int SampleCount => _values.Length;
    /// <summary>Whether the history holds nothing but zeros (the graph then draws only its baseline).</summary>
    internal bool IsIdle => _idle;

    internal void SetSeries(IoSeries series)
    {
        float[] values = Source switch { IoGraphSource.DiskWrite => series.DiskWrite, IoGraphSource.DiskRead => series.DiskRead, _ => series.NetDown };
        // A history that got shorter (a reset) starts the scale again instead of sinking from the old maximum.
        _tracked = IoGraphGeometry.Track(values.Length < _values.Length ? 0 : _tracked, values);
        _values = values;
        _capacity = Math.Max(series.Capacity, series.Count);
        Peak = IoGraphGeometry.Scale(_tracked);
        _idle = IoGraphGeometry.Largest(values) <= 0;
        _geometryDirty = true;
        InvalidateVisual();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SourceProperty) { _tracked = 0; _values = []; _idle = true; Peak = IoGraphGeometry.DefaultMinimumPeak; _geometryDirty = true; }
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (Bounds.Width <= 2 || Bounds.Height <= 2) return;
        double bottom = Bounds.Height - 1;
        double width = Bounds.Width - 2;
        double height = Bounds.Height - 3;
        if (GuideStroke is { } guide)
        {
            Pen guidePen = new(guide, 0.5);
            context.DrawLine(guidePen, new Point(1, bottom - 0.5 * height), new Point(width + 1, bottom - 0.5 * height));
            context.DrawLine(guidePen, new Point(1, bottom - height), new Point(width + 1, bottom - height));
        }
        // The baseline is always there; an idle row is only this thin, dimmed line, so "nothing happening" reads as calm and not broken.
        using (context.PushOpacity(_idle ? 0.55 : 0.35))
            context.DrawLine(new Pen(Stroke, 1), new Point(1, bottom), new Point(width + 1, bottom));
        if (_idle || _values.Length < 2) return;
        if (_geometryDirty || _geometrySize != Bounds.Size || _area == null || _line == null) BuildGeometry(width, height, bottom);
        context.DrawGeometry(Fill, null, _area!);
        context.DrawGeometry(null, new Pen(Stroke, LineThickness), _line!);
    }

    private void BuildGeometry(double width, double height, double bottom)
    {
        _geometryDirty = false;
        _geometrySize = Bounds.Size;
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
        StreamGeometry line = new();
        using (StreamGeometryContext path = line.Open())
        {
            path.BeginFigure(points[0], false);
            for (int i = 1; i < points.Length; i++) path.LineTo(points[i]);
            path.EndFigure(false);
        }
        (_area, _line) = (area, line);
    }
}
