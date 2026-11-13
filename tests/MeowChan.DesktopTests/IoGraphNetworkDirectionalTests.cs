using Avalonia;
using Avalonia.Media;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Stats;

namespace MeowChan.DesktopTests;

/// <summary>
/// The two-direction network graph: download is the filled/solid primary series and application request payload upload is
/// the dashed secondary series. Both directions use one network scale and are never added together or mixed with disk I/O.
/// </summary>
public sealed class IoGraphNetworkDirectionalTests
{
    // 130 x 17: drawn width 128, height 14, baseline at y = 16.
    private static readonly Size Bounds = new(130, 17);
    private const double Bottom = 16, Height = 14;

    private static IoSeries Series(float[] down, float[] up, float[]? write = null, float[]? read = null) =>
        new(down.Length, down.Length, down, up, write ?? new float[down.Length], read ?? new float[down.Length], new float[down.Length]);

    private static float[] Last(int count, float value) { float[] values = new float[count]; values[^1] = value; return values; }

    private static IoGraph NetworkIo() => new() { Source = IoGraphSource.NetworkIo };

    [Fact]
    public void AddingNetworkIoKeepsExistingSourceValuesStable()
    {
        Assert.Equal(0, (int)IoGraphSource.NetDown);
        Assert.Equal(1, (int)IoGraphSource.DiskWrite);
        Assert.Equal(2, (int)IoGraphSource.DiskRead);
        Assert.Equal(3, (int)IoGraphSource.DiskIo);
        Assert.Equal(4, (int)IoGraphSource.NetworkIo);
    }

    [Fact]
    public void DownloadAndUploadAreSeparateCurvesUnderOneNetworkScaleNotTheirSum()
    {
        float[] down = new float[10], up = new float[10];
        down[^1] = 1_000_000; up[^1] = 250_000;
        down[4] = 100_000; up[6] = 600_000;
        IoGraph graph = NetworkIo();
        graph.SetSeries(Series(down, up, Last(10, 50_000_000), Last(10, 40_000_000)));

        Assert.True(graph.HasTraffic);
        Assert.True(graph.HasSecondarySeries);
        Assert.Equal(1_000_000, graph.PrimaryTrafficPeak);
        Assert.Equal(600_000, graph.SecondaryTrafficPeak);
        Assert.Equal(1_000_000, graph.TrafficPeak); // the larger direction, never down + up or network + disk
        Assert.Equal(1_048_576, graph.Peak);
        Assert.Equal(10, graph.SampleCount);
        Assert.Equal(10, graph.SecondarySampleCount);

        (Point[] primary, Point[] secondary) = graph.Layout(Bounds);
        Assert.Equal(Bottom - 1_000_000d / 1_048_576 * Height, primary[^1].Y, 6);
        Assert.Equal(Bottom - 250_000d / 1_048_576 * Height, secondary[^1].Y, 6);
        Assert.Equal(Bottom - 100_000d / 1_048_576 * Height, primary[4].Y, 6);
        Assert.Equal(Bottom, secondary[4].Y, 6);
        Assert.Equal(Bottom - 600_000d / 1_048_576 * Height, secondary[6].Y, 6);
        Assert.Equal(Bottom, primary[6].Y, 6);
        Assert.Equal(primary.Select(point => point.X), secondary.Select(point => point.X));
    }

    [Fact]
    public void UploadWithoutDownloadIsTrafficAndDrawsOnlyTheSecondaryCurve()
    {
        IoGraph graph = NetworkIo();
        graph.SetSeries(Series(new float[10], Last(10, 300_000)));

        Assert.True(graph.HasTraffic);
        Assert.False(graph.IsIdle);
        Assert.Equal(0, graph.PrimaryTrafficPeak);
        Assert.Equal(300_000, graph.SecondaryTrafficPeak);
        Assert.Equal(300_000, graph.TrafficPeak);
        Assert.Equal(393_216, graph.Peak);
        (Point[] primary, Point[] secondary) = graph.Layout(Bounds);
        Assert.Empty(primary); // a silent download does not cover the baseline
        Assert.Equal(10, secondary.Length);
        Assert.Equal(Bottom - 300_000d / 393_216 * Height, secondary[^1].Y, 6);
    }

    [Fact]
    public void DownloadWithoutUploadDrawsOnlyThePrimaryCurve()
    {
        IoGraph graph = NetworkIo();
        graph.SetSeries(Series(Last(10, 90_000), new float[10]));

        Assert.True(graph.HasTraffic);
        Assert.Equal(90_000, graph.PrimaryTrafficPeak);
        Assert.Equal(0, graph.SecondaryTrafficPeak);
        Assert.Equal(98_304, graph.Peak);
        (Point[] primary, Point[] secondary) = graph.Layout(Bounds);
        Assert.Equal(10, primary.Length);
        Assert.Empty(secondary);
        Assert.Equal(Bottom - 90_000d / 98_304 * Height, primary[^1].Y, 6);
    }

    [Fact]
    public void NetworkGraphIgnoresDiskAndSingleSourceModesStaySingleSeries()
    {
        IoSeries series = Series(Last(10, 1_000), Last(10, 2_000), Last(10, 50_000_000), Last(10, 40_000_000));
        IoGraph graph = NetworkIo();
        graph.SetSeries(series);
        Assert.Equal(2_000, graph.TrafficPeak);
        Assert.Equal(IoGraphGeometry.DefaultMinimumPeak, graph.Peak);

        graph.Source = IoGraphSource.NetDown;
        graph.SetSeries(series);
        Assert.False(graph.HasSecondarySeries);
        Assert.Equal(1_000, graph.TrafficPeak);
        Assert.Equal(0, graph.SecondarySampleCount);
        Assert.Empty(graph.Layout(Bounds).Secondary);

        graph.Source = IoGraphSource.DiskRead;
        graph.SetSeries(series);
        Assert.False(graph.HasSecondarySeries);
        Assert.Equal(40_000_000, graph.TrafficPeak);
        Assert.Equal(0, graph.SecondaryTrafficPeak);
        Assert.Equal(40_000_000, graph.PrimaryTrafficPeak);
    }

    [Fact]
    public void NonFiniteDirectionsAreNotTrafficAndDoNotSetTheScale()
    {
        IoGraph graph = NetworkIo();
        graph.SetSeries(Series(
            [float.NaN, float.PositiveInfinity, float.NegativeInfinity],
            [float.NaN, float.PositiveInfinity, float.NegativeInfinity]));

        Assert.True(graph.IsIdle);
        Assert.False(graph.HasTraffic);
        Assert.Equal(0, graph.TrafficPeak);
        Assert.Equal(IoGraphGeometry.DefaultMinimumPeak, graph.Peak);
        Assert.Empty(graph.Layout(Bounds).Primary);
        Assert.Empty(graph.Layout(Bounds).Secondary);
    }

    [Fact]
    public void AFiniteUploadStillDrawsWhenDownloadSamplesAreNonFinite()
    {
        IoGraph graph = NetworkIo();
        graph.SetSeries(Series(
            [float.NaN, float.PositiveInfinity, float.NegativeInfinity],
            [0, 0, 300_000]));

        Assert.True(graph.HasTraffic);
        Assert.Equal(0, graph.PrimaryTrafficPeak);
        Assert.Equal(300_000, graph.SecondaryTrafficPeak);
        Assert.Equal(393_216, graph.Peak);
        (Point[] primary, Point[] secondary) = graph.Layout(Bounds);
        Assert.Empty(primary);
        Assert.Equal(3, secondary.Length);
    }

    [Fact]
    public void NetworkUploadUsesTheDashedSecondaryStroke()
    {
        IBrush primary = Brushes.Red, legacy = Brushes.Green, secondary = Brushes.Blue;
        IoGraph graph = new() { Source = IoGraphSource.NetworkIo, Stroke = primary };

        Assert.True(IoGraph.SecondaryDashes.Count >= 2 && IoGraph.SecondaryDashes.All(dash => dash > 0));
        Assert.Same(primary, graph.EffectiveSecondaryStroke);
        graph.DiskStroke = legacy; // compatibility fallback also applies to the network secondary series
        Assert.Same(legacy, graph.EffectiveSecondaryStroke);
        graph.SecondaryStroke = secondary;
        Assert.Same(secondary, graph.EffectiveSecondaryStroke);
    }
}
