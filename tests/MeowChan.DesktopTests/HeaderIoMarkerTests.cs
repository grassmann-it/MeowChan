using Avalonia;
using Avalonia.Automation;
using Avalonia.Media;
using MeowChan.Desktop.Controls;

namespace MeowChan.DesktopTests;

/// <summary>Platform-free contract tests for the optional passive header I/O marker.</summary>
public sealed class HeaderIoMarkerTests
{
    [Fact]
    public void PublicControlHasTheCompactPassiveDefaults()
    {
        HeaderIoMarker marker = new();

        Assert.Equal(32, marker.Width);
        Assert.Equal(14, marker.Height);
        Assert.Equal(HeaderIoDirection.NetworkDown, marker.Direction);
        Assert.Null(marker.Stroke);
        Assert.False(marker.Focusable);
        Assert.False(marker.IsTabStop);
        Assert.True(marker.IsHitTestVisible); // a parent can keep a hover tooltip on the marker
        Assert.Equal(AccessibilityView.Raw, AutomationProperties.GetAccessibilityView(marker));
    }

    [Fact]
    public void DirectionAndStrokeAreRealStyledProperties()
    {
        HeaderIoMarker marker = new();
        SolidColorBrush stroke = new();

        marker.Direction = HeaderIoDirection.DiskRead;
        marker.Stroke = stroke;

        Assert.Equal(HeaderIoDirection.DiskRead, marker.GetValue(HeaderIoMarker.DirectionProperty));
        Assert.Same(stroke, marker.Stroke);
        Assert.Same(stroke, marker.GetValue(HeaderIoMarker.StrokeProperty));
        Assert.Equal(HeaderIoDirection.NetworkDown, HeaderIoMarker.DirectionProperty.GetMetadata(typeof(HeaderIoMarker)).DefaultValue);
    }

    [Fact]
    public void EveryDirectionHasAConcreteBoundedBodyAndArrowVector()
    {
        foreach (HeaderIoDirection direction in Enum.GetValues<HeaderIoDirection>())
        {
            HeaderIoMarkerVector vector = HeaderIoMarker.VectorFor(direction);
            Assert.NotEmpty(vector.Body);
            Assert.NotEmpty(vector.Arrow);
            Assert.All(vector.Body.Concat(vector.Arrow), path =>
            {
                Assert.NotEmpty(path.Points);
                Assert.All(path.Points, point =>
                {
                    Assert.InRange(point.X, 0, 32);
                    Assert.InRange(point.Y, 0, 14);
                });
            });
        }
    }

    [Fact]
    public void NetworkDirectionsUseTheSameNodeShapeButOppositeVerticalArrows()
    {
        HeaderIoMarkerVector down = HeaderIoMarker.VectorFor(HeaderIoDirection.NetworkDown);
        HeaderIoMarkerVector up = HeaderIoMarker.VectorFor(HeaderIoDirection.NetworkUp);

        Assert.Same(down.Body, up.Body);
        Assert.NotEqual(Describe(down.Arrow), Describe(up.Arrow));
        Assert.Equal(22, down.Arrow[0].Points[0].X);
        Assert.Equal(22, up.Arrow[0].Points[0].X);
        Assert.True(down.Arrow.SelectMany(path => path.Points).Max(point => point.Y) > up.Arrow.SelectMany(path => path.Points).Max(point => point.Y));
        Assert.True(up.Arrow.SelectMany(path => path.Points).Min(point => point.Y) < down.Arrow.SelectMany(path => path.Points).Min(point => point.Y));

        // The network body has three bounded nodes and two links, not a disk outline.
        Assert.Equal(3, down.Body.Count(path => path.Closed));
        Assert.Equal(2, down.Body.Count(path => !path.Closed));
    }

    [Fact]
    public void DiskDirectionsUseTheSameCylinderButOppositeHorizontalArrows()
    {
        HeaderIoMarkerVector read = HeaderIoMarker.VectorFor(HeaderIoDirection.DiskRead);
        HeaderIoMarkerVector write = HeaderIoMarker.VectorFor(HeaderIoDirection.DiskWrite);

        Assert.Same(read.Body, write.Body);
        Assert.NotEqual(Describe(read.Arrow), Describe(write.Arrow));
        Assert.Equal(4, read.Body.Count);
        Assert.Equal(2, read.Body.Count(path => path.Closed));
        Assert.True(read.Arrow.SelectMany(path => path.Points).Max(point => point.X) > write.Arrow.SelectMany(path => path.Points).Max(point => point.X));
        Assert.True(write.Arrow.SelectMany(path => path.Points).Min(point => point.X) < read.Arrow.SelectMany(path => path.Points).Min(point => point.X));

        // The cylinder and the node graph remain visibly different even when both use the same Stroke brush.
        Assert.NotEqual(Describe(read.Body), Describe(HeaderIoMarker.VectorFor(HeaderIoDirection.NetworkDown).Body));
    }

    [Fact]
    public void MediumShapesAndDirectionArrowsRemainDistinctWithoutTextOrColours()
    {
        string source = File.ReadAllText(SourcePath());
        Assert.DoesNotContain("Geometry.Parse", source);
        Assert.DoesNotContain("Brushes.", source);
        Assert.DoesNotContain("Click", source);
        Assert.DoesNotContain("Pointer", source);
        Assert.DoesNotContain("Emoji", source);
        Assert.DoesNotContain("IsHitTestVisible = false", source);
        Assert.Equal(4, Enum.GetValues<HeaderIoDirection>().Length);

        HeaderIoMarkerVector[] vectors = Enum.GetValues<HeaderIoDirection>().Select(HeaderIoMarker.VectorFor).ToArray();
        Assert.Equal(vectors[0].Body, vectors[1].Body);
        Assert.Equal(vectors[2].Body, vectors[3].Body);
        Assert.NotEqual(Describe(vectors[0].Body), Describe(vectors[2].Body));
        Assert.NotEqual(Describe(vectors[0].Arrow), Describe(vectors[1].Arrow));
        Assert.NotEqual(Describe(vectors[2].Arrow), Describe(vectors[3].Arrow));
    }

    private static string Describe(IEnumerable<HeaderIoMarkerPath> paths) => string.Join("|", paths.Select(path =>
        (path.Closed ? "C:" : "O:") + string.Join(";", path.Points.Select(point => $"{point.X:0.###},{point.Y:0.###}"))));

    private static string SourcePath() => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../Desktop/Controls/HeaderIoMarker.cs"));
}
