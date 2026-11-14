using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
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
        Assert.Equal(new IoCounts(NetDown: 1200, NetUp: 42, DiskWritten: 80, DiskRead: 14, Requests: 6, Errors: 1), a.Session);
        Assert.Equal(new IoCounts(NetDown: 100, NetUp: 600, DiskWritten: 8, DiskRead: 4, Requests: 2, Errors: 2), b.Session);
        Assert.Equal(a.Session, a.Today);
        Assert.Equal(a.Today, a.Retained);
        Assert.Equal(b.Session, b.Today);
        Assert.Equal(b.Today, b.Retained);
        Assert.Equal(new IoRates(NetDown: 600, NetUp: 21, DiskWrite: 40, DiskRead: 7, Requests: 3), a.Rate);
        Assert.Equal(new IoRates(NetDown: 50, NetUp: 300, DiskWrite: 4, DiskRead: 2, Requests: 1), b.Rate);
        IoCounts total = new IoCounts(NetDown: 1300, NetUp: 642, DiskWritten: 88, DiskRead: 18, Requests: 8, Errors: 3);
        Assert.Equal(total, snapshot.Total.Session);
        Assert.Equal(total, snapshot.Total.Today);
        Assert.Equal(total, snapshot.Total.Retained);
        Assert.Equal(new IoRates(NetDown: 650, NetUp: 321, DiskWrite: 44, DiskRead: 9, Requests: 4), snapshot.Rate);
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
        Assert.Equal(new IoCounts(NetDown: 0, NetUp: 38, DiskWritten: 0, DiskRead: 0, Requests: 0, Errors: 0), snapshot.Total.Session);
        Assert.Equal(new IoRates(NetDown: 0, NetUp: 19, DiskWrite: 0, DiskRead: 0, Requests: 0), snapshot.Rate);
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
        Assert.Equal(new IoRates(NetDown: 50, NetUp: 0, DiskWrite: 10, DiskRead: 3, Requests: 1), reset.Rate);
        Assert.Equal(new IoCounts(NetDown: 1100, NetUp: 300, DiskWritten: 100, DiskRead: 26, Requests: 6, Errors: 2), reset.Total.Session);
        source.Add("a", net: 20, netUp: 8, diskWritten: 4, diskRead: 2, requests: 2);
        clock.Advance(TimeSpan.FromSeconds(2));
        IoStatsSnapshot next = hub.Snapshot();
        Assert.Equal(new IoCounts(NetDown: 1120, NetUp: 308, DiskWritten: 104, DiskRead: 28, Requests: 8, Errors: 2), next.Total.Session);
        Assert.Equal(next.Total.Session, next.Total.Today);
        Assert.Equal(next.Total.Today, next.Total.Retained);
        Assert.Equal(new IoRates(NetDown: 10, NetUp: 4, DiskWrite: 2, DiskRead: 1, Requests: 1), next.Rate);
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
        source.Readings.Add(new IoCounterReading(Key: "a", NetDown: 30, NetUp: 2, DiskWritten: 4, DiskRead: 8, Requests: 1, Errors: 0));
        source.Readings.Add(new IoCounterReading(Key: "a", NetDown: 10, NetUp: 18, DiskWritten: 6, DiskRead: 2, Requests: 3, Errors: 1));
        clock.Advance(TimeSpan.FromSeconds(2));
        IoStatsSnapshot snapshot = hub.Snapshot();
        Assert.Equal(new IoCounts(NetDown: 40, NetUp: 20, DiskWritten: 10, DiskRead: 10, Requests: 4, Errors: 1), Assert.Single(snapshot.Sources).Session);
        Assert.Equal(new IoRates(NetDown: 20, NetUp: 10, DiskWrite: 5, DiskRead: 5, Requests: 2), snapshot.Rate);
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
            IoCounts expected = new IoCounts(NetDown: 780, NetUp: 11922, DiskWritten: 78, DiskRead: 156, Requests: 0, Errors: 0);
            Assert.Equal(expected, snapshot.Total.Session);
            Assert.Equal(expected, snapshot.Total.Today);
            Assert.Equal(expected, snapshot.Total.Retained);
            Assert.Equal(780, snapshot.Sources.Sum(row => row.Session.NetDown));
            Assert.Equal(11922, snapshot.Sources.Sum(row => row.Session.NetUp));
            Assert.Equal(5961d, snapshot.Sources.Sum(row => row.Rate.NetUp));
            Assert.Equal(new IoRates(NetDown: 390, NetUp: 5961, DiskWrite: 39, DiskRead: 78, Requests: 0), snapshot.Rate);
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
        ring.Add(new IoSample(NetDown: 0, NetUp: 9, DiskWrite: 0, DiskRead: 0, Requests: 0));
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
        Assert.Equal(new IoCounts(NetDown: 4, NetUp: 14, DiskWritten: 0, DiskRead: 0, Requests: 0, Errors: 0), hub.Snapshot().Total.Session);
        Assert.Equal([7f], hub.Snapshot().Series.NetUp);
    }

    [Fact]
    public void HiddenCollectionAndDayRolloverKeepUploadTotalsWithoutInventingRates()
    {
        IoManualClock clock = new(Noon.AddHours(11).AddMinutes(59));
        IoFakeSource source = new();
        using IoStatsHub hub = new(source, clock, Options);
        source.Add("a", net: 10, netUp: 90);
        Assert.Equal(new IoCounts(NetDown: 10, NetUp: 90, DiskWritten: 0, DiskRead: 0, Requests: 0, Errors: 0), hub.Peek().Today);
        clock.Advance(TimeSpan.FromMinutes(2));
        source.Add("a", net: 3, netUp: 7, diskRead: 5);
        IoStatsSnapshot snapshot = hub.Snapshot();
        Assert.Equal(Today.AddDays(1), snapshot.Today);
        Assert.Equal(new IoCounts(NetDown: 13, NetUp: 97, DiskWritten: 0, DiskRead: 5, Requests: 0, Errors: 0), snapshot.Total.Session);
        Assert.Equal(new IoCounts(NetDown: 3, NetUp: 7, DiskWritten: 0, DiskRead: 5, Requests: 0, Errors: 0), snapshot.Total.Today);
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
        Assert.Equal(new IoCounts(NetDown: 100, NetUp: 0, DiskWritten: 11, DiskRead: 12, Requests: 13, Errors: 14), sources["a"]);
        Assert.Equal(old, IoStatsStore.Serialize(days, Today, 30));
        Assert.True(Parse(Document("\"up\":0")).TryGet(Today, out sources));
        Assert.Equal(IoCounts.Zero, sources["a"]);
    }

    [Fact]
    public void V1RoundTripPreservesBothDirectionsOnEveryDayAndSource()
    {
        IoDays days = new();
        days.Add(Today.AddDays(-1), "a", new IoCounts(NetDown: 100, NetUp: 7, DiskWritten: 20, DiskRead: 30, Requests: 4, Errors: 1), 64);
        days.Add(Today, "a", new IoCounts(NetDown: 3, NetUp: 500, DiskWritten: 9, DiskRead: 11, Requests: 2, Errors: 0), 64);
        days.Add(Today, "b", new IoCounts(NetDown: 0, NetUp: long.MaxValue, DiskWritten: 0, DiskRead: 4, Requests: 0, Errors: 0), 64);
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
            days.Add(Today.AddDays(-1), "s" + i.ToString("00"), new IoCounts(NetDown: i, NetUp: 100 - i, DiskWritten: 2 * i, DiskRead: 3 * i, Requests: 0, Errors: 0), 64);
            days.Add(Today, "s" + i.ToString("00"), new IoCounts(NetDown: 2 * i, NetUp: 200 - i, DiskWritten: 4 * i, DiskRead: 6 * i, Requests: 0, Errors: 0), 64);
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
        Assert.Equal(new IoCounts(NetDown: 0, NetUp: 0, DiskWritten: 700, DiskRead: 900, Requests: 0, Errors: 0), sources["a"]);
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
            Assert.Equal(new IoCounts(NetDown: 120, NetUp: 34, DiskWritten: 9, DiskRead: 6, Requests: 0, Errors: 0), snapshot.Total.Today);
            Assert.Equal(snapshot.Total.Today, snapshot.Total.Retained);
        }
        File.WriteAllText(store.FilePath, Document("\"up\":-1"));
        IoStatsLoadResult recovered = store.Load(Today, 30, 64);
        Assert.Equal(IoStatsLoadStatus.RestoredFromBackup, recovered.Status);
        Assert.True(recovered.Days.TryGet(Today, out Dictionary<string, IoCounts> sources));
        Assert.Equal(new IoCounts(NetDown: 100, NetUp: 31, DiskWritten: 9, DiskRead: 6, Requests: 0, Errors: 0), sources["a"]);
        Assert.NotNull(recovered.SetAsidePath);
        Assert.True(File.Exists(recovered.SetAsidePath));
    }

    [Fact]
    public void CountsSaturateDirectionsIndependentlyAndUploadIsNotZero()
    {
        IoCounts counts = new IoCounts(NetDown: long.MaxValue - 1, NetUp: 3, DiskWritten: 5, DiskRead: 7, Requests: 11, Errors: 13);
        Assert.Equal(new IoCounts(NetDown: long.MaxValue, NetUp: 20, DiskWritten: 24, DiskRead: 30, Requests: 40, Errors: 44), counts.Plus(new IoCounts(NetDown: 2, NetUp: 17, DiskWritten: 19, DiskRead: 23, Requests: 29, Errors: 31)));
        Assert.Equal(new IoCounts(NetDown: 7, NetUp: long.MaxValue, DiskWritten: 0, DiskRead: 0, Requests: 0, Errors: 0),
            new IoCounts(NetDown: 3, NetUp: long.MaxValue, DiskWritten: 0, DiskRead: 0, Requests: 0, Errors: 0).Plus(new IoCounts(NetDown: 4, NetUp: 1, DiskWritten: 0, DiskRead: 0, Requests: 0, Errors: 0)));
        Assert.False(new IoCounts(NetDown: 0, NetUp: 1, DiskWritten: 0, DiskRead: 0, Requests: 0, Errors: 0).IsZero);
        Assert.False(new IoSample(NetDown: 0, NetUp: 1, DiskWrite: 0, DiskRead: 0, Requests: 0).IsZero);
        Assert.NotEqual(IoRates.Zero, new IoRates(NetDown: 0, NetUp: 1, DiskWrite: 0, DiskRead: 0, Requests: 0));
    }

    [Fact]
    public void DirectionalProductionConstructorCallsNameEveryField()
    {
        (string Type, string[] Fields)[] contracts =
        [
            ("IoCounts", ["NetDown", "NetUp", "DiskWritten", "DiskRead", "Requests", "Errors"]),
            ("IoRates", ["NetDown", "NetUp", "DiskWrite", "DiskRead", "Requests"]),
            ("IoSample", ["NetDown", "NetUp", "DiskWrite", "DiskRead", "Requests"]),
            ("IoSeries", ["capacity", "count", "netDown", "netUp", "diskWrite", "diskRead", "requests"]),
            ("IoCounterReading", ["Key", "NetDown", "NetUp", "DiskWritten", "DiskRead", "Requests", "Errors"])
        ];

        foreach (string path in new[]
        {
            "Desktop/Stats/IoStatsModel.cs", "Desktop/Stats/IoRing.cs", "Desktop/Stats/IoStatsHub.cs", "Desktop/Stats/IoStatsStore.cs"
        })
        {
            string source = File.ReadAllText(Source(path));
            foreach ((string type, string arguments) in ConstructorCalls(source))
            {
                string[] fields = Assert.Single(contracts, contract => contract.Type == type).Fields;
                foreach (string field in fields)
                    Assert.True(arguments.Contains(field + ":", StringComparison.Ordinal), $"{path}: {type} call must name {field}");
            }
        }
    }

    private static IEnumerable<(string Type, string Arguments)> ConstructorCalls(string source)
    {
        MatchCollection matches = Regex.Matches(source, @"\bnew\s+(IoCounts|IoRates|IoSample|IoSeries|IoCounterReading)\s*\(");
        foreach (Match match in matches)
        {
            int open = source.IndexOf('(', match.Index);
            int depth = 0;
            bool inString = false;
            bool escaped = false;
            for (int i = open; i < source.Length; i++)
            {
                char character = source[i];
                if (inString)
                {
                    if (escaped) escaped = false;
                    else if (character == '\\') escaped = true;
                    else if (character == '\"') inString = false;
                    continue;
                }
                if (character == '\"') { inString = true; continue; }
                if (character == '(') depth++;
                else if (character == ')' && --depth == 0)
                {
                    yield return (match.Groups[1].Value, source[(open + 1)..i]);
                    break;
                }
            }
        }
    }

    private static string Source(string path, [CallerFilePath] string sourcePath = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourcePath)!, "../..", path));

    [Fact]
    public void DownOnlyValuesRemainExplicitlyZeroForEveryDirectionalRecord()
    {
        IoCounts counts = new(NetDown: 1, NetUp: 0, DiskWritten: 2, DiskRead: 3, Requests: 4, Errors: 5);
        Assert.Equal((1L, 0L, 2L, 3L, 4L, 5L),
            (counts.NetDown, counts.NetUp, counts.DiskWritten, counts.DiskRead, counts.Requests, counts.Errors));
        IoRates rates = new(NetDown: 1, NetUp: 0, DiskWrite: 2, DiskRead: 3, Requests: 4);
        Assert.Equal((1d, 0d, 2d, 3d, 4d), (rates.NetDown, rates.NetUp, rates.DiskWrite, rates.DiskRead, rates.Requests));
        IoSample sample = new(NetDown: 1, NetUp: 0, DiskWrite: 2, DiskRead: 3, Requests: 4);
        Assert.Equal((1f, 0f, 2f, 3f, 4f), (sample.NetDown, sample.NetUp, sample.DiskWrite, sample.DiskRead, sample.Requests));
        IoSeries series = new IoSeries(capacity: 30, count: 2, netDown: [1, 2], netUp: [0, 0], diskWrite: [3, 4], diskRead: [5, 6], requests: [7, 8]);
        Assert.Equal([0f, 0f], series.NetUp);
        Assert.Equal([3f, 4f], series.DiskWrite);
        IoFakeSource source = new();
        source.Add("a", 1, 2, 3, 4, 5);
        source.Add("a", netUp: 9);
        List<IoCounterReading> readings = [];
        source.Read(readings);
        Assert.Equal(new IoCounterReading(Key: "a", NetDown: 1, NetUp: 9, DiskWritten: 2, DiskRead: 3, Requests: 4, Errors: 5), Assert.Single(readings));
        source.Set("a", 17);
        readings.Clear();
        source.Read(readings);
        Assert.Equal(new IoCounterReading(Key: "a", NetDown: 17, NetUp: 0, DiskWritten: 0, DiskRead: 0, Requests: 0, Errors: 0), Assert.Single(readings));
    }
}
