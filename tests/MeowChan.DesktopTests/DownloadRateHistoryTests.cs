using MeowChan.Desktop;

namespace MeowChan.DesktopTests;

public sealed class DownloadRateHistoryTests
{
    [Fact]
    public void RatesUseActualElapsedTimeAndSkipInvalidIntervalsWithoutLosingBytes()
    {
        DownloadRateHistory history = new(100);
        history.Sample(1124, TimeSpan.FromSeconds(2));
        Assert.Equal(512, history.BytesPerSecond);
        history.Sample(2148, TimeSpan.Zero);
        history.Sample(3172, TimeSpan.FromSeconds(4));
        Assert.Equal(new double[] { 512, 512 }, history.Samples);
    }

    [Fact]
    public void IdleAddsZerosAndHistoryRetainsOnlyLastSixtySamples()
    {
        DownloadRateHistory history = new(0);
        history.Sample(2048, TimeSpan.FromSeconds(1));
        for (int i = 0; i < 60; i++) history.Sample(2048, TimeSpan.FromSeconds(1));
        Assert.Equal(60, history.Samples.Count);
        Assert.All(history.Samples, value => Assert.Equal(0, value));
        Assert.Equal(0, history.BytesPerSecond);
    }

    [Fact]
    public void CounterResetCannotProduceNegativeThroughput()
    {
        DownloadRateHistory history = new(8192);
        history.Sample(0, TimeSpan.FromSeconds(1));
        Assert.Equal(0, history.BytesPerSecond);
        history.Sample(1024, TimeSpan.FromSeconds(1));
        Assert.Equal(1024, history.BytesPerSecond);
    }
}
