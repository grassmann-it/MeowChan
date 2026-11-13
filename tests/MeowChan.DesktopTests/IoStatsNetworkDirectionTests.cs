using System.Text;
using System.Text.Json;
using MeowChan.Desktop.Stats;
using MeowChan.Persistence;

namespace MeowChan.DesktopTests;

/// <summary>Synthetic counters only: no transport requests or process-global meter mutations.</summary>
public sealed class IoStatsNetworkDirectionTests
{
    private static readonly DateTimeOffset Noon = new(2026, 11, 13, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 11, 13);
    private static IoStatsOptions Options => new() { Persist = false, SampleIntervalMs = 2000, HistorySamples = 30 };

    private static IoDays Parse(string json) => IoStatsStore.Parse(Encoding.UTF8.GetBytes(json), Today, 30, 64);

    private static string Document(string counters) =>
        "{\"version\":1,\"days\":[{\"day\":\"2026-11-13\",\"sources\":{\"a\":{" + counters + "}}}]}";

    [Fact]
    public void AsymmetricDirectionsReachEverySourceTotalRateAndRingWithoutMixingWithDisk()
    {
        IoManualClock clock = new(Noon);
        IoFakeSource source = new();
        using IoStatsHub hub = new(source, clock, Options);
        using IoStatsSubscription window = hub.Subscribe(IoStatsVisibility.Window);
        source.Add("a", net: 1200, netUp: 42, diskWritten: 80, diskRead: 14, requests: 6, errors: 1);
        source.Add("b", net: 100, netUp: 600, diskWritten: 8, diskRead: 4, requests: 2, errors: 2);
        clock.Advance(TimeSpan.FromSeconds(2));

        IoStatsSnapshot snapshot = hub.Snapshot();
        IoSourceStats a = Assert.Single(snapshot.Sources, row => row.Key == "a");
        IoSourceStats b = Assert.Single(snapshot.Sources, row => row.Key == "b");
        Assert.Equal(new IoCounts(1200, 42, 80, 14, 6, 1), a.Session);
        Assert.Equal(new IoCounts(100, 600, 8, 4, 2, 2), b.Session);
        Assert.Equal(a.Session, a.Today);
        Assert.Equal(a.Today, a.Retained);
        Assert.Equal(b.Session, b.Today);
        Assert.Equal(b.Today, b.Retained);
        Assert.Equal(new IoRates(600, 21, 40, 7, 3), a.Rate);
        Assert.Equal(new IoRates(50, 300, 4, 2, 1), b.Rate);
        IoCounts total = new(1300, 642, 88, 18, 8, 3);
        Assert.Equal(total, snapshot.Total.Session);
        Assert.Equal(total, snapshot.Total.Today);
        Assert.Equal(total, snapshot.Total.Retained);
        Assert.Equal(new IoRates(650, 321, 44, 9, 4), snapshot.Rate);
        Assert.Equal(snapshot.Rate, snapshot.Total.Rate);
        Assert.Equal([650f], snapshot.Series.NetDown);
        Assert.Equal([321f], snapshot.Series.NetUp);
        Assert.Equal([44f], snapshot.Series.DiskWrite);
        Assert.Equal([9f], snapshot.Series.DiskRead);
        Assert.Equal([4f], snapshot.Series.Requests);
        IoStatsPeek peek = hub.Peek();
        Assert.Equal(total, peek.Session);
        Assert.Equal(total, peek.Today);
        Assert.Equal(snapshot.Rate, peek.Rate);
    }

    [Fact]
    public void UploadOnlyCountsAsActivityButNeverAsDownloadOrDisk()
    {
        IoManualClock clock = new(Noon);
        IoFakeSource source = new();
        using IoStatsHub hub = new(source, clock, Options);
        using IoStatsSubscription window = hub.Subscribe(IoStatsVisibility.Window);
        int raised = 0;
        hub.Sampled += () => raised++;
        // Fill the ring with idle samples, then verify an upload breaks idle redraw suppression.
        clock.Advance(TimeSpan.FromSeconds(64));
        int before = raised;
        source.Add("upload", netUp: 38);
        clock.Advance(TimeSpan.FromSeconds(2));
        IoStatsSnapshot snapshot = hub.Snapshot();
        Assert.Equal(before + 1, raised);
        Assert.Equal(new IoCounts(0, 38, 0, 0, 0, 0), snapshot.Total.Session);
        Assert.Equal(new IoRates(0, 19, 0, 0, 0), snapshot.Rate);
        Assert.Equal(19f, snapshot.Series.NetUp[^1]);
        Assert.All(snapshot.Series.NetDown, v => Assert.Equal(0f, v));
        Assert.All(snapshot.Series.DiskWrite, v => Assert.Equal(0f, v));
        Assert.All(snapshot.Series.DiskRead, v => Assert.Equal(0f, v));
    }

    [Fact]
    public void ResetOfOnlyUploadClampsOnlyThatDeltaAndRebasesItsNextIncrement()
    {
        IoManualClock clock = new(Noon);
        IoFakeSource source = new();
        using IoStatsHub hub = new(source, clock, Options);
        using IoStatsSubscription window = hub.Subscribe(IoStatsVisibility.Window);
        source.Set("a", net: 1000, netUp: 300, diskWritten: 80, diskRead: 20, requests: 4, errors: 1);
        clock.Advance(TimeSpan.FromSeconds(2));
        source.Set("a", net: 1100, netUp: 2, diskWritten: 100, diskRead: 26, requests: 6, errors: 2);
        clock.Advance(TimeSpan.FromSeconds(2));
        IoStatsSnapshot reset = hub.Snapshot();
        Assert.Equal(new IoRates(50, 0, 10, 3, 1), reset.Rate);
        Assert.Equal(new IoCounts(1100, 300, 100, 26, 6, 2), reset.Total.Session);
        source.Add("a", net: 20, netUp: 8, diskWritten: 4, diskRead: 2, requests: 2);
        clock.Advance(TimeSpan.FromSeconds(2));
        IoStatsSnapshot next = hub.Snapshot();
        Assert.Equal(new IoCounts(1120, 308, 104, 28, 8, 2), next.Total.Session);
        Assert.Equal(next.Total.Session, next.Total.Today);
        Assert.Equal(next.Total.Today, next.Total.Retained);
        Assert.Equal(new IoRates(10, 4, 2, 1, 1), next.Rate);
        Assert.Equal([150f, 0f, 4f], next.Series.NetUp);
        Assert.Equal([500f, 50f, 10f], next.Series.NetDown);
    }

    [Fact]
    public void DuplicateSourceReadingsAggregateEachDirectionBeforeComputingDeltas()
    {
        IoManualClock clock = new(Noon);
        DuplicateSource source = new();
        using IoStatsHub hub = new(source, clock, Options);
        using IoStatsSubscription window = hub.Subscribe(IoStatsVisibility.Window);
        source.Readings.Add(new IoCounterReading("a", 30, 2, 4, 8, 1, 0));
        source.Readings.Add(new IoCounterReading("a", 10, 18, 6, 2, 3, 1));
        clock.Advance(TimeSpan.FromSeconds(2));
        IoStatsSnapshot snapshot = hub.Snapshot();
        Assert.Equal(new IoCounts(40, 20, 10, 10, 4, 1), Assert.Single(snapshot.Sources).Session);
        Assert.Equal(new IoRates(20, 10, 5, 5, 2), snapshot.Rate);
    }

    private sealed class DuplicateSource : IIoCounterSource
    {
        internal readonly List<IoCounterReading> Readings = [];
        public void Read(List<IoCounterReading> into) => into.AddRange(Readings);
    }

    [Fact]
    public void LiveOverflowAndSnapshotFoldingKeepDirectionsSeparate()
    {
        foreach (bool lowerLimitLater in new[] { false, true })
        {
            IoManualClock clock = new(Noon);
            IoFakeSource source = new();
            using IoStatsHub hub = new(source, clock, Options with { MaxSources = lowerLimitLater ? 64 : 8 });
            using IoStatsSubscription window = hub.Subscribe(IoStatsVisibility.Window);
            for (int i = 1; i <= 12; i++)
                source.Add("s" + i.ToString("00"), net: i * 10, netUp: 1000 - i, diskWritten: i, diskRead: i * 2);
            clock.Advance(TimeSpan.FromSeconds(2));
            if (lowerLimitLater) hub.Configure(Options with { MaxSources = 8 });
            IoStatsSnapshot snapshot = hub.Snapshot();
            Assert.Equal(8, snapshot.Sources.Count);
            IoSourceStats other = Assert.Single(snapshot.Sources, row => row.Key == IoStatsKeys.Other);
            IoCounts expected = new(780, 11922, 78, 156, 0, 0);
            Assert.Equal(expected, snapshot.Total.Session);
            Assert.Equal(expected, snapshot.Total.Today);
            Assert.Equal(expected, snapshot.Total.Retained);
            Assert.Equal(780, snapshot.Sources.Sum(row => row.Session.NetDown));
            Assert.Equal(11922, snapshot.Sources.Sum(row => row.Session.NetUp));
            Assert.Equal(5961d, snapshot.Sources.Sum(row => row.Rate.NetUp));
            Assert.Equal(new IoRates(390, 5961, 39, 78, 0), snapshot.Rate);
            Assert.Equal(snapshot.Sources.Where(row => row.Key != IoStatsKeys.Other).Sum(row => row.Session.NetUp) + other.Session.NetUp,
                snapshot.Total.Session.NetUp);
            Assert.True(other.Session.NetUp > 0);
        }
    }

    [Fact]
    public void RingWrapResizeClearAndCopiesPreserveBothDirectionsAndDiskRows()
    {
        IoRing ring = new(3);
        static IoSample Sample(int i) => new(i, 100 + i, 200 + i, 300 + i, 400 + i);
        for (int i = 1; i <= 5; i++) ring.Add(Sample(i));
        IoSeries copy = ring.ToSeries();
        Assert.Equal([3f, 4f, 5f], copy.NetDown);
        Assert.Equal([103f, 104f, 105f], copy.NetUp);
        Assert.Equal([203f, 204f, 205f], copy.DiskWrite);
        Assert.Equal([303f, 304f, 305f], copy.DiskRead);
        Assert.Equal([403f, 404f, 405f], copy.Requests);
        ring.Resize(5);
        ring.Add(Sample(6));
        ring.Resize(2);
        ring.Resize(2);
        IoSeries shrunk = ring.ToSeries();
        Assert.Equal([5f, 6f], shrunk.NetDown);
        Assert.Equal([105f, 106f], shrunk.NetUp);
        Assert.Equal([205f, 206f], shrunk.DiskWrite);
        Assert.Equal([305f, 306f], shrunk.DiskRead);
        Assert.Equal([405f, 406f], shrunk.Requests);
        ring.Add(Sample(7));
        Assert.Equal([106f, 107f], ring.ToSeries().NetUp);
        ring.Clear();
        Assert.Equal(0, ring.ToSeries().Count);
        Assert.Empty(ring.ToSeries().NetUp);
        ring.Add(new IoSample(0, 9, 0, 0, 0));
        Assert.Equal([9f], ring.ToSeries().NetUp);
        Assert.Equal([0f], ring.ToSeries().NetDown);
        Assert.Equal([0f], ring.ToSeries().DiskWrite);
        Assert.Equal([103f, 104f, 105f], copy.NetUp); // detached snapshots
        Assert.Equal([105f, 106f], shrunk.NetUp);
        Assert.Empty(IoSeries.Empty.NetUp);
    }

    [Fact]
    public void HubResizeAndResetKeepOrClearUploadAlongsideDownload()
    {
        IoManualClock clock = new(Noon);
        IoFakeSource source = new();
        using IoStatsHub hub = new(source, clock, Options with { HistorySamples = 60 });
        using IoStatsSubscription window = hub.Subscribe(IoStatsVisibility.Window);
        for (int i = 1; i <= 40; i++)
        {
            source.Add("a", net: 2 * i, netUp: 6 * i);
            clock.Advance(TimeSpan.FromSeconds(2));
        }
        hub.Configure(Options);
        IoStatsSnapshot shrunk = hub.Snapshot();
        Assert.Equal(Enumerable.Range(11, 30).Select(i => (float)i), shrunk.Series.NetDown);
        Assert.Equal(Enumerable.Range(11, 30).Select(i => 3f * i), shrunk.Series.NetUp);
        hub.Configure(Options with { HistorySamples = 60 });
        Assert.Equal(shrunk.Series.NetUp, hub.Snapshot().Series.NetUp);
        hub.Reset();
        IoStatsSnapshot empty = hub.Snapshot();
        Assert.Empty(empty.Sources);
        Assert.Empty(empty.Series.NetUp);
        Assert.Equal(IoCounts.Zero, empty.Total.Session);
        Assert.Equal(IoCounts.Zero, empty.Total.Today);
        Assert.Equal(IoCounts.Zero, empty.Total.Retained);
        Assert.Equal(IoRates.Zero, empty.Rate);
        source.Add("a", net: 4, netUp: 14);
        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(new IoCounts(4, 14, 0, 0, 0, 0), hub.Snapshot().Total.Session);
        Assert.Equal([7f], hub.Snapshot().Series.NetUp);
    }

    [Fact]
    public void HiddenCollectionAndDayRolloverKeepUploadTotalsWithoutInventingRates()
    {
        IoManualClock clock = new(Noon.AddHours(11).AddMinutes(59));
        IoFakeSource source = new();
        using IoStatsHub hub = new(source, clock, Options);
        source.Add("a", net: 10, netUp: 90);
        Assert.Equal(new IoCounts(10, 90, 0, 0, 0, 0), hub.Peek().Today);
        clock.Advance(TimeSpan.FromMinutes(2));
        source.Add("a", net: 3, netUp: 7, diskRead: 5);
        IoStatsSnapshot snapshot = hub.Snapshot();
        Assert.Equal(Today.AddDays(1), snapshot.Today);
        Assert.Equal(new IoCounts(13, 97, 0, 5, 0, 0), snapshot.Total.Session);
        Assert.Equal(new IoCounts(3, 7, 0, 5, 0, 0), snapshot.Total.Today);
        Assert.Equal(snapshot.Total.Session, snapshot.Total.Retained);
        Assert.Equal(IoRates.Zero, snapshot.Rate);
        Assert.Empty(snapshot.Series.NetUp);
        Assert.Equal(0, hub.ActiveTimerCount);
    }

    [Fact]
    public void UploadRateBecomesZeroWhenSamplingStopsButTotalsSurvive()
    {
        IoManualClock clock = new(Noon);
        IoFakeSource source = new();
        using IoStatsHub hub = new(source, clock, Options);
        IoStatsSubscription window = hub.Subscribe(IoStatsVisibility.Window);
        source.Add("a", netUp: 10);
        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(5d, hub.Peek().Rate.NetUp);
        window.Dispose();
        Assert.Equal(IoRates.Zero, hub.Peek().Rate);
        Assert.Equal(10, hub.Peek().Session.NetUp);
        Assert.Equal(IoRates.Zero, Assert.Single(hub.Snapshot().Sources).Rate);
    }

    [Fact]
    public void OptionalV1UploadKeepsOldDocumentsAndExactDownOnlySerialization()
    {
        string old = Document("\"down\":100,\"diskWrite\":11,\"diskRead\":12,\"requests\":13,\"errors\":14");
        IoDays days = Parse(old);
        Assert.True(days.TryGet(Today, out Dictionary<string, IoCounts> sources));
        Assert.Equal(new IoCounts(100, 0, 11, 12, 13, 14), sources["a"]);
        Assert.Equal(old, IoStatsStore.Serialize(days, Today, 30));
        Assert.True(Parse(Document("\"up\":0")).TryGet(Today, out sources));
        Assert.Equal(IoCounts.Zero, sources["a"]);
    }

    [Fact]
    public void V1RoundTripPreservesBothDirectionsOnEveryDayAndSource()
    {
        IoDays days = new();
        days.Add(Today.AddDays(-1), "a", new IoCounts(100, 7, 20, 30, 4, 1), 64);
        days.Add(Today, "a", new IoCounts(3, 500, 9, 11, 2, 0), 64);
        days.Add(Today, "b", new IoCounts(0, long.MaxValue, 0, 4, 0, 0), 64);
        string json = IoStatsStore.Serialize(days, Today, 30);
        using JsonDocument doc = JsonDocument.Parse(json);
        Assert.Equal(1, doc.RootElement.GetProperty("version").GetInt32());
        Assert.Equal(500, doc.RootElement.GetProperty("days")[1].GetProperty("sources").GetProperty("a").GetProperty("up").GetInt64());
        IoDays roundTrip = Parse(json);
        foreach ((DateOnly day, Dictionary<string, IoCounts> expected) in days.Days)
        {
            Assert.True(roundTrip.TryGet(day, out Dictionary<string, IoCounts> actual));
            Assert.Equal(expected.Count, actual.Count);
            foreach ((string key, IoCounts counts) in expected) Assert.Equal(counts, actual[key]);
        }
    }

    [Fact]
    public void LoadingAndRenormalizingSourceLimitsPreservesUploadInOtherAndDaySums()
    {
        IoDays days = new();
        for (int i = 1; i <= 12; i++)
        {
            days.Add(Today.AddDays(-1), "s" + i.ToString("00"), new IoCounts(i, 100 - i, 2 * i, 3 * i, 0, 0), 64);
            days.Add(Today, "s" + i.ToString("00"), new IoCounts(2 * i, 200 - i, 4 * i, 6 * i, 0, 0), 64);
        }
        string json = IoStatsStore.Serialize(days, Today, 30);
        IoDays loaded = IoStatsStore.Parse(Encoding.UTF8.GetBytes(json), Today, 30, 8);
        Assert.True(days.Renormalize(8));
        Dictionary<string, IoCounts> totals = loaded.Sum(Today.AddDays(-1), Today);
        Assert.Equal(8, totals.Count);
        Assert.Equal(234, totals.Values.Sum(c => c.NetDown));
        Assert.Equal(3444, totals.Values.Sum(c => c.NetUp));
        Assert.Equal(468, totals.Values.Sum(c => c.DiskWritten));
        Assert.Equal(702, totals.Values.Sum(c => c.DiskRead));
        Assert.True(totals[IoStatsKeys.Other].NetUp > 0);
        foreach ((DateOnly day, Dictionary<string, IoCounts> expected) in days.Days)
        {
            Assert.True(loaded.TryGet(day, out Dictionary<string, IoCounts> actual));
            Assert.Equal(expected.Count, actual.Count);
            foreach ((string key, IoCounts counts) in expected) Assert.Equal(counts, actual[key]);
        }
    }

    [Theory]
    [InlineData("\"up\":-1")]
    [InlineData("\"up\":1.5")]
    [InlineData("\"up\":9223372036854775808")]
    [InlineData("\"up\":\"12\"")]
    [InlineData("\"up\":null")]
    [InlineData("\"up\":true")]
    [InlineData("\"up\":[]")]
    [InlineData("\"up\":{}")]
    [InlineData("\"up\":1,\"up\":2")]
    public void InvalidOrDuplicateUploadFailsClosedLikeEveryOtherCounter(string counters)
    {
        Assert.Throws<InvalidDataException>(() => Parse(Document(counters)));
    }

    [Fact]
    public void DiskIsNotUsedToReconstructHistoricalUpload()
    {
        IoDays days = Parse(Document("\"diskWrite\":700,\"diskRead\":900"));
        Assert.True(days.TryGet(Today, out Dictionary<string, IoCounts> sources));
        Assert.Equal(new IoCounts(0, 0, 700, 900, 0, 0), sources["a"]);
    }

    [Fact]
    public void PersistedUploadSurvivesHubRestartAndInvalidPrimaryFallsBackWithoutLosingIt()
    {
        using TempDirectory dir = new();
        using ProfileLease lease = ProfileLease.Acquire(dir.Path);
        IoStatsStore store = new(dir.Path, lease);
        IoManualClock clock = new(Noon);
        IoFakeSource source = new();
        using (IoStatsHub hub = new(source, clock, Options with { Persist = true }))
        using (hub.Attach(store, () => default))
        {
            source.Add("a", net: 100, netUp: 31, diskWritten: 9, diskRead: 6);
            hub.Flush();
            source.Add("a", net: 20, netUp: 3);
            hub.Flush(); // backup contains the first flush, including upload
        }
        using (IoStatsHub restarted = new(new IoFakeSource(), new IoManualClock(Noon), Options with { Persist = true }))
        using (restarted.Attach(store, () => default))
        {
            IoStatsSnapshot snapshot = restarted.Snapshot();
            Assert.Equal(IoCounts.Zero, snapshot.Total.Session);
            Assert.Equal(new IoCounts(120, 34, 9, 6, 0, 0), snapshot.Total.Today);
            Assert.Equal(snapshot.Total.Today, snapshot.Total.Retained);
        }
        File.WriteAllText(store.FilePath, Document("\"up\":-1"));
        IoStatsLoadResult recovered = store.Load(Today, 30, 64);
        Assert.Equal(IoStatsLoadStatus.RestoredFromBackup, recovered.Status);
        Assert.True(recovered.Days.TryGet(Today, out Dictionary<string, IoCounts> sources));
        Assert.Equal(new IoCounts(100, 31, 9, 6, 0, 0), sources["a"]);
        Assert.NotNull(recovered.SetAsidePath);
        Assert.True(File.Exists(recovered.SetAsidePath));
    }

    [Fact]
    public void CountsSaturateDirectionsIndependentlyAndUploadIsNotZero()
    {
        IoCounts counts = new(long.MaxValue - 1, 3, 5, 7, 11, 13);
        Assert.Equal(new IoCounts(long.MaxValue, 20, 24, 30, 40, 44), counts.Plus(new IoCounts(2, 17, 19, 23, 29, 31)));
        Assert.Equal(new IoCounts(7, long.MaxValue, 0, 0, 0, 0),
            new IoCounts(3, long.MaxValue, 0, 0, 0, 0).Plus(new IoCounts(4, 1, 0, 0, 0, 0)));
        Assert.False(new IoCounts(0, 1, 0, 0, 0, 0).IsZero);
        Assert.False(new IoSample(0, 1, 0, 0, 0).IsZero);
        Assert.NotEqual(IoRates.Zero, new IoRates(0, 1, 0, 0, 0));
    }

    [Fact]
    public void DownOnlyConstructorAndFakeCallsKeepTheirOriginalPositionalMeaning()
    {
        Assert.Equal(new IoCounts(1, 0, 2, 3, 4, 5), new IoCounts(1, 2, 3, 4, 5));
        Assert.Equal(new IoRates(1, 0, 2, 3, 4), new IoRates(NetDown: 1, DiskWrite: 2, DiskRead: 3, Requests: 4));
        Assert.Equal(new IoSample(1, 0, 2, 3, 4), new IoSample(1, 2, 3, 4));
        IoSeries series = new(30, 2, [1, 2], [3, 4], [5, 6], [7, 8]);
        Assert.Equal([0f, 0f], series.NetUp);
        Assert.Equal([3f, 4f], series.DiskWrite);
        IoFakeSource source = new();
        source.Add("a", 1, 2, 3, 4, 5);
        source.Add("a", netUp: 9);
        List<IoCounterReading> readings = [];
        source.Read(readings);
        Assert.Equal(new IoCounterReading("a", 1, 9, 2, 3, 4, 5), Assert.Single(readings));
        source.Set("a", 17);
        readings.Clear();
        source.Read(readings);
        Assert.Equal(new IoCounterReading("a", 17, 0, 0, 0, 0, 0), Assert.Single(readings));
    }
}
