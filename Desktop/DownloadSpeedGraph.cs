using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace MeowChan.Desktop;

public sealed class DownloadSpeedGraph : Control
{
    public static readonly StyledProperty<IBrush?> StrokeProperty =
        AvaloniaProperty.Register<DownloadSpeedGraph, IBrush?>(nameof(Stroke));
    public static readonly StyledProperty<IBrush?> FillProperty =
        AvaloniaProperty.Register<DownloadSpeedGraph, IBrush?>(nameof(Fill));
    private double[] _samples = Array.Empty<double>();

    static DownloadSpeedGraph() => AffectsRender<DownloadSpeedGraph>(StrokeProperty, FillProperty);
    public IBrush? Stroke { get => GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }
    public IBrush? Fill { get => GetValue(FillProperty); set => SetValue(FillProperty, value); }

    internal void SetSamples(IReadOnlyList<double> samples)
    {
        _samples = samples.ToArray();
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (Bounds.Width <= 2 || Bounds.Height <= 2) return;
        double bottom = Bounds.Height - 1;
        double width = Bounds.Width - 2;
        double peak = Math.Max(65536, _samples.Length == 0 ? 0 : _samples.Max());
        Point At(int index) => new(1 + (60 - _samples.Length + index) * width / 59,
            bottom - _samples[index] / peak * (Bounds.Height - 3));
        context.DrawLine(new Pen(Stroke, 0.4), new Point(1, bottom), new Point(width + 1, bottom));
        if (_samples.Length < 2) return;
        StreamGeometry line = new();
        using (StreamGeometryContext path = line.Open())
        {
            path.BeginFigure(At(0), false);
            for (int i = 1; i < _samples.Length; i++) path.LineTo(At(i));
            path.EndFigure(false);
        }
        StreamGeometry area = new();
        using (StreamGeometryContext path = area.Open())
        {
            path.BeginFigure(new Point(At(0).X, bottom), true);
            for (int i = 0; i < _samples.Length; i++) path.LineTo(At(i));
            path.LineTo(new Point(At(_samples.Length - 1).X, bottom));
            path.EndFigure(true);
        }
        context.DrawGeometry(Fill, null, area);
        context.DrawGeometry(null, new Pen(Stroke, 1.3), line);
    }
}
