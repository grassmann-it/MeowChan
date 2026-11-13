using Avalonia;
using Avalonia.Media;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Stats;

namespace MeowChan.DesktopTests;

/// <summary>
/// The two-direction disk graph: write and read are two separate curves under one disk scale, never a sum and never tied to
/// the network. Every check goes through <see cref="IoGraph.SetSeries"/> and the layout <see cref="IoGraph.Render"/> draws.
/// </summary>
public sealed class IoGraphDirectionalTests
{
    // 130 x 17: drawn width 128, height 14, baseline at y = 16.
    private static readonly Size Bounds = new(130, 17);
    private const double Bottom = 16, Height = 14;

    private static IoSeries Series(float[] write, float[] read, float[]? net = null) =>
        new(write.Length, write.Length, net ?? new float[write.Length], write, read, new float[write.Length]);

    private static float[] Last(int count, float value) { float[] values = new float[count]; values[^1] = value; return values; }

    private static IoGraph DiskIo() => new() { Source = IoGraphSource.DiskIo };

    [Fact]
    public void ReadingWithoutWritingIsTrafficWithItsOwnScaleAndADrawnCurve()
    {
        IoGraph graph = DiskIo();
        graph.SetSeries(Series(new float[10], Last(10, 300_000)));
        Assert.True(graph.HasTraffic);
        Assert.False(graph.IsIdle);
        Assert.Equal(0, graph.PrimaryTrafficPeak);
        Assert.Equal(300_000, graph.SecondaryTrafficPeak);
        Assert.Equal(300_000, graph.TrafficPeak);
        Assert.Equal(393_216, graph.Peak); // 384 KiB, the step above the read value, not the 64 KiB idle scale
        (Point[] write, Point[] read) = graph.Layout(Bounds);
        Assert.Empty(write); // a silent write series draws nothing over the baseline
        Assert.Equal(10, read.Length);
        Assert.Equal(129, read[^1].X, 6);
        Assert.Equal(Bottom - 300_000d / 393_216 * Height, read[^1].Y, 6);
        Assert.Equal(Bottom, read[0].Y, 6);
    }

    [Fact]
    public void WritingWithoutReadingDrawsOnlyTheWriteCurve()
    {
        IoGraph graph = DiskIo();
        graph.SetSeries(Series(Last(10, 90_000), new float[10]));
        Assert.True(graph.HasTraffic);
        Assert.Equal(90_000, graph.PrimaryTrafficPeak);
        Assert.Equal(0, graph.SecondaryTrafficPeak);
        Assert.Equal(98_304, graph.Peak);
        (Point[] write, Point[] read) = graph.Layout(Bounds);
        Assert.Equal(10, write.Length);
        Assert.Empty(read);
        Assert.Equal(Bottom - 90_000d / 98_304 * Height, write[^1].Y, 6);
    }

    [Fact]
    public void BothDirectionsAreSeparateCurvesUnderOneScaleThatIsNotTheirSum()
    {
        float[] write = new float[10], read = new float[10];
        write[^1] = 1_000_000; read[^1] = 250_000;
        write[4] = 100_000; read[6] = 600_000;
        IoGraph graph = DiskIo();
        graph.SetSeries(Series(write, read));
        Assert.Equal(1_000_000, graph.TrafficPeak);          // the larger direction, not 1 250 000
        Assert.Equal(1_048_576, graph.Peak);                 // 1 MiB; the sum would have needed 1.5 MiB
        Assert.Equal(10, graph.SampleCount);
        Assert.Equal(10, graph.SecondarySampleCount);
        (Point[] w, Point[] r) = graph.Layout(Bounds);
        Assert.Equal(Bottom - 1_000_000d / 1_048_576 * Height, w[^1].Y, 6);
        Assert.Equal(Bottom - 250_000d / 1_048_576 * Height, r[^1].Y, 6);
        Assert.Equal(Bottom - 100_000d / 1_048_576 * Height, w[4].Y, 6);
        Assert.Equal(Bottom, r[4].Y, 6);                     // read keeps its own value where only write was busy
        Assert.Equal(Bottom - 600_000d / 1_048_576 * Height, r[6].Y, 6);
        Assert.Equal(Bottom, w[6].Y, 6);
        Assert.Equal(w.Select(p => p.X), r.Select(p => p.X)); // the same instants, one time axis
    }

    [Fact]
    public void TheDiskGraphNeitherSeesNorDependsOnTheNetwork()
    {
        float[] write = Last(10, 200_000), read = Last(10, 50_000);
        IoGraph quiet = DiskIo(), busy = DiskIo();
        quiet.SetSeries(Series(write, read, new float[10]));
        busy.SetSeries(Series(write, read, Enumerable.Repeat(50_000_000f, 10).ToArray()));
        Assert.Equal(quiet.Peak, busy.Peak);
        Assert.Equal(quiet.TrafficPeak, busy.TrafficPeak);
        Assert.Equal(quiet.Layout(Bounds), busy.Layout(Bounds), new LayoutComparer());

        IoGraph networkOnly = DiskIo();
        networkOnly.SetSeries(Series(new float[10], new float[10], Enumerable.Repeat(50_000_000f, 10).ToArray()));
        Assert.True(networkOnly.IsIdle);
        Assert.Equal(IoGraphGeometry.DefaultMinimumPeak, networkOnly.Peak);
        (Point[] silentWrite, Point[] silentRead) = networkOnly.Layout(Bounds);
        Assert.Empty(silentWrite);
        Assert.Empty(silentRead);
    }

    [Fact]
    public void ASpikeInEitherDirectionDecaysSlowlyForBoth()
    {
        IoGraph graph = DiskIo();
        graph.SetSeries(Series(Last(10, 4_000_000), new float[10]));
        Assert.Equal(4_194_304, graph.Peak);
        // The write spike scrolled out; only a small read remains. The shared scale sinks by the decay, it does not jump.
        graph.SetSeries(Series(new float[10], Last(10, 10_000)));
        Assert.Equal(IoGraphGeometry.Scale(4_000_000 * IoGraphGeometry.DefaultDecay), graph.Peak);
        Assert.Equal(4_194_304, graph.Peak);
        Assert.True(graph.HasTraffic);
        Assert.Equal(10_000, graph.TrafficPeak);
    }

    [Fact]
    public void ResetClearsBothDirectionsAndForgetsTheRememberedScale()
    {
        IoGraph graph = DiskIo();
        graph.SetSeries(Series(Last(10, 4_000_000), Last(10, 3_000_000)));
        graph.SetSeries(IoSeries.Empty);
        Assert.True(graph.IsIdle);
        Assert.Equal(0, graph.SampleCount);
        Assert.Equal(0, graph.SecondarySampleCount);
        Assert.Equal(0, graph.TrafficPeak);
        Assert.Equal(0, graph.SecondaryTrafficPeak);
        Assert.Equal(0, graph.WindowIntervals);
        Assert.Equal(IoGraphGeometry.DefaultMinimumPeak, graph.Peak);
        // The first sample after a reset is scaled on its own, not on the remembered 4 MB/s.
        graph.SetSeries(Series([0f, 0f], [0f, 100_000f]));
        Assert.Equal(131_072, graph.Peak);
    }

    [Fact]
    public void ChangingTheSourceStartsFreshAndDropsTheSecondSeries()
    {
        float[] write = Last(10, 80_000), read = Last(10, 6_000_000);
        IoSeries series = Series(write, read, Last(10, 1_000));
        IoGraph graph = DiskIo();
        graph.SetSeries(series);
        Assert.Equal(6_291_456, graph.Peak);
        Assert.True(graph.HasSecondarySeries);

        graph.Source = IoGraphSource.DiskWrite;
        Assert.True(graph.IsIdle);                           // nothing of the old source survives the switch
        Assert.Equal(IoGraphGeometry.DefaultMinimumPeak, graph.Peak);
        graph.SetSeries(series);
        Assert.False(graph.HasSecondarySeries);
        Assert.Equal(0, graph.SecondarySampleCount);
        Assert.Equal(0, graph.SecondaryTrafficPeak);
        Assert.Equal(98_304, graph.Peak);                    // write only, not the remembered 6 MB/s read
        Assert.Empty(graph.Layout(Bounds).Secondary);

        graph.Source = IoGraphSource.NetDown;
        graph.SetSeries(series);
        Assert.Equal(1_000, graph.TrafficPeak);
        Assert.Equal(IoGraphGeometry.DefaultMinimumPeak, graph.Peak);

        graph.Source = IoGraphSource.DiskIo;
        graph.SetSeries(series);
        Assert.Equal(6_000_000, graph.TrafficPeak);
        Assert.Equal(10, graph.SecondarySampleCount);
    }

    [Fact]
    public void TheHeaderWindowKeepsSixtySecondsOfBothDirections()
    {
        float[] write = Enumerable.Repeat(1_000f, 300).ToArray(), read = Enumerable.Repeat(2_000f, 300).ToArray();
        read[0] = 50_000_000; // 750 s ago at 2.5 s per sample
        IoGraph graph = new() { Source = IoGraphSource.DiskIo, HistoryWindowSeconds = 60, SampleIntervalMs = () => 2500 };
        graph.SetSeries(Series(write, read));
        Assert.Equal(25, graph.SampleCount);
        Assert.Equal(25, graph.SecondarySampleCount);
        Assert.Equal(24, graph.WindowIntervals);
        Assert.Equal(2_000, graph.TrafficPeak);
        Assert.Equal(IoGraphGeometry.DefaultMinimumPeak, graph.Peak);
        Assert.Equal(50_000_000, read[0]); // the snapshot is never changed
    }

    [Fact]
    public void NonFiniteReadValuesAreNeitherTrafficNorScale()
    {
        IoGraph graph = DiskIo();
        graph.SetSeries(Series(new float[3], [float.NaN, float.PositiveInfinity, float.NegativeInfinity]));
        Assert.True(graph.IsIdle);
        Assert.Equal(IoGraphGeometry.DefaultMinimumPeak, graph.Peak);
    }

    [Fact]
    public void ReadIsDashedSoItIsToldApartFromWriteWithoutColour()
    {
        // Write is a solid line over its filled area, read a dashed line without an area: different in shape, not only hue.
        Assert.True(IoGraph.SecondaryDashes.Count >= 2 && IoGraph.SecondaryDashes.All(d => d > 0));
        IBrush primary = Brushes.Red, legacy = Brushes.Green, secondary = Brushes.Blue;
        IoGraph graph = new() { Source = IoGraphSource.DiskIo, Stroke = primary };
        Assert.Same(primary, graph.EffectiveSecondaryStroke);  // never invisible, even without a second brush
        graph.DiskStroke = legacy;
        Assert.Same(legacy, graph.EffectiveSecondaryStroke);
        graph.SecondaryStroke = secondary;
        Assert.Same(secondary, graph.EffectiveSecondaryStroke);
    }

    private sealed class LayoutComparer : IEqualityComparer<(Point[] Primary, Point[] Secondary)>
    {
        public bool Equals((Point[] Primary, Point[] Secondary) a, (Point[] Primary, Point[] Secondary) b) =>
            a.Primary.SequenceEqual(b.Primary) && a.Secondary.SequenceEqual(b.Secondary);
        public int GetHashCode((Point[] Primary, Point[] Secondary) value) => value.Primary.Length ^ value.Secondary.Length << 8;
    }
}
