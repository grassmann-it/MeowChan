using MeowChan.Desktop.Stats;
using MeowChan.Persistence;

namespace MeowChan.DesktopTests;

public sealed class IoStatsHubPersistenceTests
{
    private const long MiB = 1024 * 1024;
    private static readonly DateTimeOffset Noon = new(2026, 11, 13, 12, 0, 0, TimeSpan.Zero);

    private static IoStatsOptions Options(int persistMinutes = 5, bool persist = true) =>
        new() { Persist = persist, PersistIntervalMinutes = persistMinutes };

    [Fact]
    public void TotalsSurviveARestartAndSessionStartsAtZero()
    {
        using TempDirectory dir = new();
        using (ProfileLease lease = ProfileLease.Acquire(dir.Path))
        {
            IoManualClock clock = new(Noon);
            IoFakeSource source = new();
            using IoStatsHub hub = new(source, clock, Options());
            using (hub.Attach(new IoStatsStore(dir.Path, lease), () => default))
            {
                source.Add("4chan", net: 3 * MiB, diskWritten: 2 * MiB, requests: 9, errors: 1);
                hub.Peek(); // folds the counts in; nothing is written yet
                Assert.False(File.Exists(Path.Combine(dir.Path, IoStatsStore.FileName)));
            } // detaching writes
            Assert.True(File.Exists(Path.Combine(dir.Path, IoStatsStore.FileName)));
        }
        using (ProfileLease lease = ProfileLease.Acquire(dir.Path))
        {
            IoManualClock clock = new(Noon.AddHours(1));
            IoFakeSource source = new();
            using IoStatsHub hub = new(source, clock, Options());
            using (hub.Attach(new IoStatsStore(dir.Path, lease), () => default))
            {
                IoStatsSnapshot s = hub.Snapshot();
                IoSourceStats row = Assert.Single(s.Sources);
                Assert.Equal("4chan", row.Key);
                Assert.Equal(0, row.Session.NetDown);
                Assert.Equal(3 * MiB, row.Today.NetDown);
                Assert.Equal(2 * MiB, row.Retained.DiskWritten);
                Assert.Equal(9, row.Retained.Requests);
                Assert.Equal(1, row.Retained.Errors);
                Assert.Null(hub.LoadNotice);
            }
        }
    }

    [Fact]
    public void ChangedTotalsAreWrittenAtTheConfiguredIntervalAndOnlyWhenChanged()
    {
        using TempDirectory dir = new();
        using ProfileLease lease = ProfileLease.Acquire(dir.Path);
        string path = Path.Combine(dir.Path, IoStatsStore.FileName);
        IoManualClock clock = new(Noon);
        IoFakeSource source = new();
        int active = 1;
        using IoStatsHub hub = new(source, clock, Options(persistMinutes: 2));
        using IDisposable attachment = hub.Attach(new IoStatsStore(dir.Path, lease), () => new IoActivity(active, 0, 0));
        // Downloads are active, so a writing timer exists even though nothing is visible and no sampling timer runs.
        Assert.Equal(1, hub.ActiveTimerCount);
        Assert.Equal(IoStatsVisibility.None, hub.SamplingMode);

        clock.Advance(TimeSpan.FromMinutes(2));
        Assert.False(File.Exists(path)); // nothing changed yet: nothing written
        source.Add("a", net: MiB);
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.False(File.Exists(path)); // the interval has not elapsed
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.True(File.Exists(path));
        DateTime written = File.GetLastWriteTimeUtc(path);
        Assert.Contains("\"down\":" + MiB, File.ReadAllText(path));

        // Idle again and clean: the writing timer ends at its next look.
        active = 0;
        hub.NotifyActivityChanged();
        clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(0, hub.ActiveTimerCount);
        Assert.Equal(0, clock.ActiveTimers);
        Assert.Equal(written, File.GetLastWriteTimeUtc(path));
    }

    [Fact]
    public void TheWritingTimerNeverRunsHiddenAndIdleAndEndsRightAfterTheLastWrite()
    {
        using TempDirectory dir = new();
        using ProfileLease lease = ProfileLease.Acquire(dir.Path);
        IoManualClock clock = new(Noon);
        IoFakeSource source = new();
        using IoStatsHub hub = new(source, clock, Options(persistMinutes: 1));
        using IDisposable attachment = hub.Attach(new IoStatsStore(dir.Path, lease), () => default);
        using IoStatsSubscription tray = hub.Subscribe(IoStatsVisibility.Tray);
        Assert.Equal(0, hub.ActiveTimerCount);
        clock.Advance(TimeSpan.FromHours(3));
        Assert.Equal(0, hub.ActiveTimerCount);
        Assert.Equal(0, clock.ActiveTimers);

        // A change that shows up while idle (an import) arms one write, then everything is quiet again.
        source.Add("local", diskWritten: MiB);
        hub.Peek();
        Assert.Equal(1, hub.ActiveTimerCount);
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(0, hub.ActiveTimerCount);
        Assert.Equal(0, clock.ActiveTimers);
        Assert.Contains("\"diskWrite\":" + MiB, File.ReadAllText(Path.Combine(dir.Path, IoStatsStore.FileName)));
    }

    [Fact]
    public void PersistOffKeepsEverythingInMemoryAndWritesNothing()
    {
        using TempDirectory dir = new();
        using ProfileLease lease = ProfileLease.Acquire(dir.Path);
        IoManualClock clock = new(Noon);
        IoFakeSource source = new();
        using IoStatsHub hub = new(source, clock, Options(persist: false));
        using (hub.Attach(new IoStatsStore(dir.Path, lease), () => new IoActivity(2, 0, 0)))
        {
            Assert.Equal(0, hub.ActiveTimerCount);
            source.Add("a", net: MiB);
            Assert.Equal(MiB, hub.Peek().Today.NetDown);
            clock.Advance(TimeSpan.FromHours(1));
        }
        Assert.Empty(Directory.GetFiles(dir.Path, "io-stats*"));
    }

    [Fact]
    public void SwitchingPersistOnAtRuntimeLoadsTheSavedDaysAndAddsTheCountsOfThisSession()
    {
        using TempDirectory dir = new();
        using ProfileLease lease = ProfileLease.Acquire(dir.Path);
        IoStatsStore store = new(dir.Path, lease);
        IoDays saved = new();
        saved.Add(new DateOnly(2026, 11, 12), "a", new IoCounts(1000, 0, 0, 0, 0), 64);
        saved.Add(new DateOnly(2026, 11, 13), "a", new IoCounts(500, 0, 0, 0, 0), 64);
        Assert.True(store.TrySave(IoStatsStore.Serialize(saved, new DateOnly(2026, 11, 13), 30), out _));

        IoManualClock clock = new(Noon);
        IoFakeSource source = new();
        using IoStatsHub hub = new(source, clock, Options(persist: false));
        using IDisposable attachment = hub.Attach(store, () => default);
        source.Add("a", net: 70);
        Assert.Equal(70, hub.Snapshot().Total.Today.NetDown);
        hub.Configure(Options(persist: true));
        IoStatsSnapshot s = hub.Snapshot();
        Assert.Equal(570, s.Total.Today.NetDown);
        Assert.Equal(1570, s.Total.Retained.NetDown);
        Assert.Equal(70, s.Total.Session.NetDown);
        hub.Flush();
        Assert.Equal(570, store.Load(new DateOnly(2026, 11, 13), 30, 64).Days.Days.Last().Value["a"].NetDown);
    }

    [Fact]
    public void ADamagedFileNeverStopsTheHubAndLeavesANoticeAndASetAsideCopy()
    {
        using TempDirectory dir = new();
        using ProfileLease lease = ProfileLease.Acquire(dir.Path);
        string path = Path.Combine(dir.Path, IoStatsStore.FileName);
        File.WriteAllText(path, "{\"version\":1,\"days\":[{\"day\":\"2026-11-13\",\"day\":\"x\"}]}");
        IoManualClock clock = new(Noon);
        IoFakeSource source = new();
        using IoStatsHub hub = new(source, clock, Options());
        using IDisposable attachment = hub.Attach(new IoStatsStore(dir.Path, lease), () => default);
        Assert.NotNull(hub.LoadNotice);
        Assert.True(File.Exists(path + ".bad"));
        Assert.Empty(hub.Snapshot().Sources);
        source.Add("a", net: 5);
        hub.Flush();
        Assert.Contains("\"down\":5", File.ReadAllText(path));
    }

    [Fact]
    public void AFailingWriteIsKeptForTheNextTryAndReportedNotThrown()
    {
        using TempDirectory dir = new();
        using ProfileLease lease = ProfileLease.Acquire(dir.Path);
        IoStatsStore store = new(dir.Path, lease);
        Directory.CreateDirectory(store.FilePath); // the file cannot be created
        IoManualClock clock = new(Noon);
        IoFakeSource source = new();
        using IoStatsHub hub = new(source, clock, Options());
        using IDisposable attachment = hub.Attach(store, () => default);
        source.Add("a", net: 5);
        hub.Flush();
        Assert.NotNull(hub.LastPersistError);
        Directory.Delete(store.FilePath);
        hub.Flush();
        Assert.Null(hub.LastPersistError);
        Assert.Contains("\"down\":5", File.ReadAllText(store.FilePath));
    }

    [Fact]
    public void ResetDeletesTheFileAndTheBackup()
    {
        using TempDirectory dir = new();
        using ProfileLease lease = ProfileLease.Acquire(dir.Path);
        IoManualClock clock = new(Noon);
        IoFakeSource source = new();
        using IoStatsHub hub = new(source, clock, Options());
        using IDisposable attachment = hub.Attach(new IoStatsStore(dir.Path, lease), () => default);
        source.Add("a", net: 5);
        hub.Flush();
        source.Add("a", net: 5);
        hub.Flush();
        Assert.Equal(2, Directory.GetFiles(dir.Path, "io-stats*").Length);
        hub.Reset();
        Assert.Empty(Directory.GetFiles(dir.Path, "io-stats*"));
        Assert.Equal(0, hub.Snapshot().Total.Retained.NetDown);
        Assert.Equal(0, hub.ActiveTimerCount);
    }

    [Fact]
    public void ALowerRetentionAtRuntimeDropsDaysAndASmallerSourceLimitFoldsThem()
    {
        using TempDirectory dir = new();
        using ProfileLease lease = ProfileLease.Acquire(dir.Path);
        IoStatsStore store = new(dir.Path, lease);
        IoDays saved = new();
        Dictionary<string, IoCounts> many = new();
        for (int i = 0; i < 20; i++) many["s" + i.ToString("00")] = new IoCounts(100 + i, 0, 0, 0, 0);
        saved.Merge(new DateOnly(2026, 11, 13), many, 64);
        saved.Add(new DateOnly(2026, 11, 1), "old", new IoCounts(9, 0, 0, 0, 0), 64);
        Assert.True(store.TrySave(IoStatsStore.Serialize(saved, new DateOnly(2026, 11, 13), 30), out _));

        IoManualClock clock = new(Noon);
        using IoStatsHub hub = new(new IoFakeSource(), clock, Options());
        using IDisposable attachment = hub.Attach(store, () => default);
        Assert.Equal(21, hub.Snapshot().Sources.Count);
        hub.Configure(Options() with { RetentionDays = 5, MaxSources = 8 });
        IoStatsSnapshot s = hub.Snapshot();
        Assert.Equal(8, s.Sources.Count);
        Assert.DoesNotContain(s.Sources, row => row.Key == "old");
        Assert.Equal(20 * 100 + 190, s.Total.Retained.NetDown);
        hub.Flush();
        Assert.DoesNotContain("old", File.ReadAllText(store.FilePath));
    }

    [Fact]
    public void AnUnreadableFileIsNeverOverwrittenByThisSessionAlone()
    {
        if (OperatingSystem.IsWindows() || Environment.UserName == "root") return;
        using TempDirectory dir = new();
        using ProfileLease lease = ProfileLease.Acquire(dir.Path);
        string path = Path.Combine(dir.Path, IoStatsStore.FileName);
        File.WriteAllText(path, """{"version":1,"days":[{"day":"2026-11-13","sources":{"a":{"down":1000}}}]}""");
        IoManualClock clock = new(Noon);
        IoFakeSource source = new();
        using IoStatsHub hub = new(source, clock, Options());
        File.SetUnixFileMode(path, UnixFileMode.None);
        try
        {
            using IDisposable attachment = hub.Attach(new IoStatsStore(dir.Path, lease), () => default);
            Assert.NotNull(hub.LoadNotice);
            source.Add("a", net: 70);
            hub.Flush();
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            Assert.Contains("\"down\":1000", File.ReadAllText(path)); // untouched
            hub.Flush();
            Assert.Contains("\"down\":1070", File.ReadAllText(path));
        }
        finally { if (File.Exists(path)) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
    }
}
