using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Media;

namespace MeowChan.Desktop.Controls;

/// <summary>
/// A compact, passive marker for one directional I/O series. The marker deliberately carries no text or action: its owner
/// supplies the complete localized tooltip and remains the only action control.
/// </summary>
public enum HeaderIoDirection
{
    NetworkDown,
    NetworkUp,
    DiskRead,
    DiskWrite
}

/// <summary>
/// A small vector marker which combines a medium shape with its direction. It is intended for the header's 32 by 14 DIP slot.
/// Network directions use a connected-node shape and a vertical arrow; disk directions use a cylinder and a horizontal arrow.
/// </summary>
public sealed class HeaderIoMarker : Control
{
    public const double DefaultWidth = 32;
    public const double DefaultHeight = 14;

    public static readonly StyledProperty<HeaderIoDirection> DirectionProperty =
        AvaloniaProperty.Register<HeaderIoMarker, HeaderIoDirection>(nameof(Direction), HeaderIoDirection.NetworkDown);

    public static readonly StyledProperty<IBrush?> StrokeProperty =
        AvaloniaProperty.Register<HeaderIoMarker, IBrush?>(nameof(Stroke));

    private const double VectorWidth = DefaultWidth;
    private const double VectorHeight = DefaultHeight;
    private const double StrokeThickness = 1.5;

    private static readonly HeaderIoMarkerPath[] NetworkBody =
    [
        new([new Point(1, 2), new Point(6, 2), new Point(6, 6), new Point(1, 6)], true),
        new([new Point(1, 8), new Point(6, 8), new Point(6, 12), new Point(1, 12)], true),
        new([new Point(10, 5), new Point(15, 5), new Point(15, 9), new Point(10, 9)], true),
        new([new Point(6, 4), new Point(10, 6)], false),
        new([new Point(6, 10), new Point(10, 8)], false)
    ];

    private static readonly HeaderIoMarkerPath[] DiskBody =
    [
        new([
            new Point(1.5, 4), new Point(2.7, 2.4), new Point(5.8, 1.4), new Point(10.2, 1.4), new Point(13.3, 2.4),
            new Point(14.5, 4), new Point(13.3, 5.6), new Point(10.2, 6.6), new Point(5.8, 6.6), new Point(2.7, 5.6)
        ], true),
        new([new Point(1.5, 4), new Point(1.5, 10)], false),
        new([new Point(14.5, 4), new Point(14.5, 10)], false),
        new([
            new Point(1.5, 10), new Point(2.7, 11.6), new Point(5.8, 12.6), new Point(10.2, 12.6), new Point(13.3, 11.6),
            new Point(14.5, 10)
        ], true)
    ];

    private static readonly HeaderIoMarkerPath[] NetworkDownArrow =
    [
        new([new Point(22, 2), new Point(22, 10.5)], false),
        new([new Point(19, 7.5), new Point(22, 11.5), new Point(25, 7.5)], false)
    ];

    private static readonly HeaderIoMarkerPath[] NetworkUpArrow =
    [
        new([new Point(22, 12), new Point(22, 3.5)], false),
        new([new Point(19, 6.5), new Point(22, 2.5), new Point(25, 6.5)], false)
    ];

    private static readonly HeaderIoMarkerPath[] DiskReadArrow =
    [
        new([new Point(18, 7), new Point(28.5, 7)], false),
        new([new Point(25, 4), new Point(29, 7), new Point(25, 10)], false)
    ];

    private static readonly HeaderIoMarkerPath[] DiskWriteArrow =
    [
        new([new Point(29, 7), new Point(18.5, 7)], false),
        new([new Point(22, 4), new Point(18, 7), new Point(22, 10)], false)
    ];

    private static readonly HeaderIoMarkerVector NetworkDownVector = new(NetworkBody, NetworkDownArrow);
    private static readonly HeaderIoMarkerVector NetworkUpVector = new(NetworkBody, NetworkUpArrow);
    private static readonly HeaderIoMarkerVector DiskReadVector = new(DiskBody, DiskReadArrow);
    private static readonly HeaderIoMarkerVector DiskWriteVector = new(DiskBody, DiskWriteArrow);

    // Geometry is created only from Render. Keeping the vector descriptions above as points means unit tests can inspect the
    // actual shapes without touching a render-platform-dependent geometry factory.
    private static readonly Dictionary<HeaderIoDirection, StreamGeometry> GeometryCache = new();

    static HeaderIoMarker() => AffectsRender<HeaderIoMarker>(DirectionProperty, StrokeProperty);

    public HeaderIoMarker()
    {
        Width = DefaultWidth;
        Height = DefaultHeight;
        Focusable = false;
        IsTabStop = false;
        // Keep hit testing enabled: the parent may attach a hover tooltip to this visual without giving it an action of its own.
        AutomationProperties.SetAccessibilityView(this, AccessibilityView.Raw);
    }

    public HeaderIoDirection Direction
    {
        get => GetValue(DirectionProperty);
        set => SetValue(DirectionProperty, value);
    }

    public IBrush? Stroke
    {
        get => GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    /// <summary>The immutable point description used by the renderer and by platform-free shape tests.</summary>
    internal static HeaderIoMarkerVector VectorFor(HeaderIoDirection direction) => direction switch
    {
        HeaderIoDirection.NetworkDown => NetworkDownVector,
        HeaderIoDirection.NetworkUp => NetworkUpVector,
        HeaderIoDirection.DiskRead => DiskReadVector,
        HeaderIoDirection.DiskWrite => DiskWriteVector,
        _ => throw new ArgumentOutOfRangeException(nameof(direction), direction, "Unknown header I/O direction.")
    };

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (Stroke is not { } stroke || Bounds.Width <= 0 || Bounds.Height <= 0) return;

        double scale = Math.Min(Bounds.Width / VectorWidth, Bounds.Height / VectorHeight);
        if (scale <= 0) return;

        double offsetX = (Bounds.Width - VectorWidth * scale) / 2;
        double offsetY = (Bounds.Height - VectorHeight * scale) / 2;
        using (context.PushTransform(Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(offsetX, offsetY)))
            context.DrawGeometry(null, new Pen(stroke, StrokeThickness), GeometryFor(Direction));
    }

    private static StreamGeometry GeometryFor(HeaderIoDirection direction)
    {
        lock (GeometryCache)
        {
            if (GeometryCache.TryGetValue(direction, out StreamGeometry? geometry)) return geometry;

            HeaderIoMarkerVector vector = VectorFor(direction);
            geometry = new StreamGeometry();
            using (StreamGeometryContext path = geometry.Open())
            {
                AddPaths(path, vector.Body);
                AddPaths(path, vector.Arrow);
            }

            GeometryCache.Add(direction, geometry);
            return geometry;
        }
    }

    private static void AddPaths(StreamGeometryContext target, IReadOnlyList<HeaderIoMarkerPath> paths)
    {
        foreach (HeaderIoMarkerPath path in paths)
        {
            target.BeginFigure(path.Points[0], false);
            for (int i = 1; i < path.Points.Count; i++) target.LineTo(path.Points[i]);
            target.EndFigure(path.Closed);
        }
    }
}

/// <summary>A platform-free open or closed polyline used to describe a <see cref="HeaderIoMarker"/>.</summary>
internal sealed class HeaderIoMarkerPath
{
    internal HeaderIoMarkerPath(IReadOnlyList<Point> points, bool closed)
    {
        Points = points;
        Closed = closed;
    }

    internal IReadOnlyList<Point> Points { get; }
    internal bool Closed { get; }
}

/// <summary>The two shape parts of a header marker, kept separate so shape and direction remain testable without a renderer.</summary>
internal sealed class HeaderIoMarkerVector
{
    internal HeaderIoMarkerVector(IReadOnlyList<HeaderIoMarkerPath> body, IReadOnlyList<HeaderIoMarkerPath> arrow)
    {
        Body = body;
        Arrow = arrow;
    }

    internal IReadOnlyList<HeaderIoMarkerPath> Body { get; }
    internal IReadOnlyList<HeaderIoMarkerPath> Arrow { get; }
}
