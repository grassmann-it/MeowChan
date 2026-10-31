using Avalonia;
using Avalonia.Media;
using MeowChan.Desktop;

namespace MeowChan.DesktopTests;

public sealed class NativeVideoOverlayClipTests
{
    [Fact]
    public void IdenticalNativeRectanglesDoNotRepublishClipChanges()
    {
        NativeVideoOverlayRoot host = new();
        int changes = 0;
        host.PropertyChanged += (_, e) => { if (e.Property == Avalonia.Visual.ClipProperty) changes++; };
        RectangleGeometry first = new(new Rect(0, 0, 640, 480));
        host.Clip = first;
        for (int i = 0; i < 200; i++) host.Clip = new RectangleGeometry(first.Rect);
        Assert.Same(first, host.Clip);
        Assert.Equal(1, changes);
        host.Clip = new RectangleGeometry(new Rect(4, 8, 320, 240));
        Assert.Equal(new Rect(4, 8, 320, 240), Assert.IsType<RectangleGeometry>(host.Clip).Rect);
        Assert.Equal(2, changes);
        host.Clip = null;
        Assert.Null(host.Clip);
        Assert.Equal(3, changes);
    }

    [Fact]
    public void ClearingClipDoesNotResurrectAPreviousRectangleInstance()
    {
        NativeVideoOverlayRoot host = new();
        RectangleGeometry first = new(new Rect(0, 0, 100, 80));
        host.Clip = first;
        host.ClearValue(Avalonia.Visual.ClipProperty);
        Assert.Null(host.Clip);
        RectangleGeometry replacement = new(first.Rect);
        host.Clip = replacement;
        Assert.Same(replacement, host.Clip);
    }

    [Fact]
    public void DifferentRadiiTransformedAndOtherGeometriesAreNotDiscarded()
    {
        NativeVideoOverlayRoot host = new();
        Rect rect = new(0, 0, 100, 80);
        host.Clip = new RectangleGeometry(rect);
        RectangleGeometry rounded = new(rect) { RadiusX = 4, RadiusY = 8 };
        host.Clip = rounded;
        Assert.Same(rounded, host.Clip);
        RectangleGeometry transformed = new(rect) { Transform = new TranslateTransform(2, 3) };
        host.Clip = transformed;
        Assert.Same(transformed, host.Clip);
        EllipseGeometry ellipse = new(rect);
        host.Clip = ellipse;
        Assert.Same(ellipse, host.Clip);
        RectangleGeometry afterOther = new(rect);
        host.Clip = afterOther;
        Assert.Same(afterOther, host.Clip);
    }
}
