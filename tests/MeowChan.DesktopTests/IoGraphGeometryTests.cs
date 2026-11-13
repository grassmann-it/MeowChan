using System.Xml.Linq;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Media;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Stats;

namespace MeowChan.DesktopTests;

/// <summary>The shared scale and the sample positions of the network and disk graph, and the colour contract of its disk row.</summary>
public sealed class IoGraphGeometryTests
{
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void TheScaleNeverDropsBelowSixtyFourKibPerSecond()
    {
        Assert.Equal(65536, IoGraphGeometry.Peak([]));
        Assert.Equal(65536, IoGraphGeometry.Peak([100f, 2000f]));
        Assert.Equal(65536, IoGraphGeometry.Scale(0));
        Assert.Equal(65536, IoGraphGeometry.Scale(65536));
    }

    [Fact]
    public void TheScaleMovesInBinarySteps()
    {
        Assert.Equal(131072, IoGraphGeometry.Scale(65537));
        Assert.Equal(1_048_576, IoGraphGeometry.Scale(700_000));
        Assert.Equal(1_048_576, IoGraphGeometry.Scale(1_048_576));
        Assert.Equal(2_097_152, IoGraphGeometry.Scale(1_048_577));
        Assert.Equal(4_194_304, IoGraphGeometry.Peak([3_000_000f, 100f]));
    }

    [Fact]
    public void EveryRowHasItsOwnScaleSoALargeRowNeverFlattensASmallOne()
    {
        float[] network = [3_000_000f, 500_000f];
        float[] write = [40_000f, 90_000f];
        Assert.Equal(4_194_304, IoGraphGeometry.Peak(network));
        Assert.Equal(131072, IoGraphGeometry.Peak(write));
        // The 90 000 B/s of the write row reaches 69 percent of its own height, not the 2 percent a shared axis would give it.
        double y = IoGraphGeometry.Points([90_000f, 90_000f], 2, 100, 100, IoGraphGeometry.Peak(write), bottom: 100)[0].Y;
        Assert.Equal(100 - 90_000d / 131072 * 100, y, 6);
        Assert.True(y < 40);
    }

    [Fact]
    public void ThePeakIgnoresNonFiniteValues()
    {
        Assert.Equal(65536, IoGraphGeometry.Peak([float.NaN, float.PositiveInfinity, float.NegativeInfinity]));
        Assert.Equal(65536, IoGraphGeometry.Scale(double.NaN));
        Assert.Equal(65536, IoGraphGeometry.Scale(double.PositiveInfinity));
    }

    [Fact]
    public void ANewPeakIsFollowedAtOnceAndASinkingOneDecaysSlowlyInsteadOfJumping()
    {
        double tracked = IoGraphGeometry.Track(0, [10f, 8_000_000f, 10f]);
        Assert.Equal(8_000_000d, tracked);
        // The spike left the visible history: the remembered maximum sinks by the decay factor per sample, never in one jump.
        float[] quiet = [100f, 200f];
        double next = IoGraphGeometry.Track(tracked, quiet);
        Assert.Equal(8_000_000d * IoGraphGeometry.DefaultDecay, next, 3);
        Assert.Equal(IoGraphGeometry.Scale(tracked), IoGraphGeometry.Scale(next));          // one sample later the scale is unchanged
        int samples = 0;
        double previousScale = IoGraphGeometry.Scale(tracked);
        double scaleNow = previousScale;
        for (; samples < 2000 && scaleNow > IoGraphGeometry.DefaultMinimumPeak; samples++)
        {
            next = IoGraphGeometry.Track(next, quiet);
            double scale = IoGraphGeometry.Scale(next);
            Assert.True(scale == scaleNow || scale == scaleNow / 2, "the scale sinks one binary step at a time");
            scaleNow = scale;
        }
        Assert.True(samples > 100, "a spike keeps its scale for a while: " + samples);
        Assert.Equal(IoGraphGeometry.DefaultMinimumPeak, scaleNow);
    }

    [Fact]
    public void ARiseDuringTheDecayWinsOverTheRememberedMaximum()
    {
        double tracked = IoGraphGeometry.Track(1_000_000, [2_000_000f]);
        Assert.Equal(2_000_000d, tracked);
        Assert.Equal(0, IoGraphGeometry.Track(double.NaN, []));
        Assert.Equal(0, IoGraphGeometry.Track(-5, []));
    }

    [Fact]
    public void TheNewestSampleSitsAtTheRightEdgeAndAShortHistoryGrowsFromTheRight()
    {
        Point[] full = IoGraphGeometry.Points([1f, 2f, 3f], capacity: 3, width: 100, height: 10, peak: 3, left: 1, bottom: 10);
        Assert.Equal([1d, 51d, 101d], full.Select(point => point.X).ToArray());
        Point[] partial = IoGraphGeometry.Points([1f, 2f, 3f], capacity: 11, width: 100, height: 10, peak: 3, left: 1, bottom: 10);
        Assert.Equal(101, partial[^1].X, 6);
        Assert.Equal(81, partial[0].X, 6);
        Assert.Equal(0, partial[^1].Y, 6);       // the largest value reaches the top
        Assert.Equal(10 - 10d / 3, partial[0].Y, 6);
    }

    [Fact]
    public void AValueAboveThePeakIsClippedToTheTopAndNothingDrawsFromNothing()
    {
        Assert.Equal(0, IoGraphGeometry.Points([9f, 9f], 2, 10, 10, peak: 3, bottom: 10)[0].Y, 6);
        Assert.Empty(IoGraphGeometry.Points([], 5, 10, 10, 3));
        Assert.Empty(IoGraphGeometry.Points([1f], 5, 0, 10, 3));
    }

    [Fact]
    public void ALongHistoryIsThinnedToTheLargestValueOfEachGroupSoASpikeSurvives()
    {
        float[] values = new float[3600];
        values[1234] = 1000;
        Point[] points = IoGraphGeometry.Points(values, 3600, width: 96, height: 28, peak: 1000, bottom: 28);
        Assert.True(points.Length <= 2 * 96 + 1, "thinned to about two points per pixel");
        Assert.Contains(points, point => point.Y == 0);
        Assert.Equal(96, points[^1].X, 6);
    }

    [Theory]
    [InlineData("Dark")]
    [InlineData("Light")]
    public void EveryGraphStrokeIsOpaqueAndReachesThreeToOneAgainstEveryHeaderAndPageSurface(string theme)
    {
        XElement dictionary = Theme(theme);
        string[] strokes = ["Workspace.Accent", "Workspace.Stats.Disk", "Workspace.Stats.Read"];
        foreach (string key in strokes)
        {
            string stroke = Entry(dictionary, key).Value.Trim();
            Assert.Matches("^#[0-9A-Fa-f]{6}$", stroke);           // opaque: no alpha channel to lose against the surface
            foreach (string surface in new[] { "Workspace.Panel", "Workspace.Background", "Workspace.Card" })
                Assert.True(Contrast(Luminance(stroke), Luminance(Entry(dictionary, surface).Value.Trim())) >= 3.0, theme + " " + key + " on " + surface);
        }
        // Three different hues: the rows are also told apart by their separate rows and labels, but never share a colour.
        Assert.Equal(strokes.Length, strokes.Select(key => Entry(dictionary, key).Value.Trim().ToLowerInvariant()).Distinct().Count());
    }

    [Theory]
    [InlineData("Dark")]
    [InlineData("Light")]
    public void EveryGraphAreaIsOpaqueAndStaysUnderItsStrokeAndAboveTheSurface(string theme)
    {
        XElement dictionary = Theme(theme);
        foreach ((string stroke, string area) in new[] { ("Workspace.Accent", "Workspace.AccentSurface"), ("Workspace.Stats.Disk", "Workspace.Stats.DiskSurface"), ("Workspace.Stats.Read", "Workspace.Stats.ReadSurface") })
        {
            string fill = Entry(dictionary, area).Value.Trim();
            Assert.Matches("^#[0-9A-Fa-f]{6}$", fill);
            // The line stays readable on its own area, and the area is visibly not the surface it sits on.
            Assert.True(Contrast(Luminance(Entry(dictionary, stroke).Value.Trim()), Luminance(fill)) >= 3.0, theme + " " + stroke + " on " + area);
            foreach (string surface in new[] { "Workspace.Panel", "Workspace.Card" })
                Assert.True(Contrast(Luminance(fill), Luminance(Entry(dictionary, surface).Value.Trim())) >= 1.12, theme + " " + area + " against " + surface);
        }
    }

    private static XElement Theme(string theme) => XDocument.Load(Source("Desktop/WorkspaceStyles.axaml")).Root!.Descendants()
        .Single(element => element.Name.LocalName == "ResourceDictionary" && (string?)element.Attribute(X + "Key") == theme);

    private static XElement Entry(XElement scope, string key) => scope.Descendants().Single(element => (string?)element.Attribute(X + "Key") == key);

    private static double Luminance(string hex)
    {
        Color color = Color.Parse(hex);
        static double Channel(byte value) { double c = value / 255.0; return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4); }
        return 0.2126 * Channel(color.R) + 0.7152 * Channel(color.G) + 0.0722 * Channel(color.B);
    }

    private static double Contrast(double a, double b) => (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);

    private static string Source(string path, [CallerFilePath] string sourcePath = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourcePath)!, "../..", path));
}
