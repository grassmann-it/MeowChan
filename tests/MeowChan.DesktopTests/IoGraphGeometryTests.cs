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
        Assert.Equal(65536, IoGraphGeometry.Peak([], []));
        Assert.Equal(65536, IoGraphGeometry.Peak([100f, 2000f], [5f]));
    }

    [Fact]
    public void BothRowsShareOnePeakSoADiskRowAboveTheNetworkRowRaisesTheScaleForBoth()
    {
        float[] network = [1_000_000f, 500_000f];
        float[] disk = [3_000_000f, 100f];
        double peak = IoGraphGeometry.Peak(network, disk);
        Assert.Equal(3_000_000, peak);
        // The same peak places both rows: a value equal to the peak is at the top in either row, half of it halfway.
        Assert.Equal(0, IoGraphGeometry.Points([3_000_000f, 3_000_000f], 2, 100, 50, peak, bottom: 50)[0].Y, 6);
        Assert.Equal(25, IoGraphGeometry.Points([1_500_000f, 1_500_000f], 2, 100, 50, peak, bottom: 50)[1].Y, 6);
        Assert.Equal(25, IoGraphGeometry.Points([1_500_000f, 1_500_000f], 2, 100, 50, peak, bottom: 50)[0].Y, 6);
    }

    [Fact]
    public void ThePeakIgnoresNonFiniteValues()
    {
        Assert.Equal(65536, IoGraphGeometry.Peak([float.NaN, float.PositiveInfinity], [float.NegativeInfinity]));
    }

    [Fact]
    public void TheDiskRowIsWritePlusReadOfEverySample()
    {
        IoSeries series = new(capacity: 4, count: 3, netDown: [1, 2, 3], diskWrite: [10, 20, 30], diskRead: [1, 0, 5], requests: [0, 0, 0]);
        Assert.Equal([11f, 20f, 35f], IoGraphGeometry.DiskTotals(series));
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
    public void TheDiskRowIsOpaqueAndReachesThreeToOneAgainstEveryHeaderAndPageSurface(string theme)
    {
        XElement dictionary = Theme(theme);
        string disk = Entry(dictionary, "Workspace.Stats.Disk").Value.Trim();
        Assert.Matches("^#[0-9A-Fa-f]{6}$", disk);           // opaque: no alpha channel to lose against the surface
        double diskLuminance = Luminance(disk);
        foreach (string surface in new[] { "Workspace.Panel", "Workspace.Background", "Workspace.Card" })
            Assert.True(Contrast(diskLuminance, Luminance(Entry(dictionary, surface).Value.Trim())) >= 3.0, theme + " on " + surface);
        // The network row's own colour must clear it too; form (dashed against filled) tells the rows apart, not colour alone.
        Assert.NotEqual(disk.ToLowerInvariant(), Entry(dictionary, "Workspace.Accent").Value.Trim().ToLowerInvariant());
        Assert.True(Contrast(Luminance(Entry(dictionary, "Workspace.Accent").Value.Trim()), Luminance(Entry(dictionary, "Workspace.Panel").Value.Trim())) >= 3.0);
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
