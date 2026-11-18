using System.Text;
using MeowChan.Desktop.Stats;
using MeowChan.Persistence;

namespace MeowChan.DesktopTests;

public sealed class IoStatsStoreTests
{
    private static readonly DateOnly Today = new(2026, 11, 13);

    private static IoDays Days(params (string Day, string Key, long Net)[] entries)
    {
        IoDays days = new();
        foreach ((string day, string key, long net) in entries)
            days.Add(DateOnly.ParseExact(day, "yyyy-MM-dd"), key, new IoCounts(NetDown: net, NetUp: 0, DiskWritten: net + 1, DiskRead: net + 2, Requests: net + 3, Errors: net + 4), 64);
        return days;
    }

    private static string Valid(string day = "2026-11-13", string key = "4chan", long down = 7) =>
        "{\"version\":1,\"days\":[{\"day\":\"" + day + "\",\"sources\":{\"" + key + "\":{\"down\":" + down +
        ",\"diskWrite\":2,\"diskRead\":3,\"requests\":4,\"errors\":5}}}]}";

    [Fact]
    public void RoundTripKeepsEveryDaySourceAndCounter()
    {
        using TempDirectory dir = new();
        using ProfileLease lease = ProfileLease.Acquire(dir.Path);
        IoStatsStore store = new(dir.Path, lease);
        IoDays days = Days(("2026-11-12", "4chan", 100), ("2026-11-12", "danbooru", 50), ("2026-11-13", "4chan", 10));
        Assert.True(store.TrySave(IoStatsStore.Serialize(days, Today, 30), out string? error), error);

        IoStatsLoadResult loaded = store.Load(Today, 30, 64);
        Assert.Equal(IoStatsLoadStatus.Ok, loaded.Status);
        Assert.Null(loaded.Notice);
        Assert.Equal(2, loaded.Days.Count);
        Assert.True(loaded.Days.TryGet(new DateOnly(2026, 11, 12), out Dictionary<string, IoCounts>? first));
        Assert.Equal(new IoCounts(NetDown: 100, NetUp: 0, DiskWritten: 101, DiskRead: 102, Requests: 103, Errors: 104), first!["4chan"]);
        Assert.Equal(new IoCounts(NetDown: 50, NetUp: 0, DiskWritten: 51, DiskRead: 52, Requests: 53, Errors: 54), first["danbooru"]);
        Assert.True(loaded.Days.TryGet(Today, out Dictionary<string, IoCounts>? second));
        Assert.Equal(10, second!["4chan"].NetDown);
        Assert.Equal("""{"version":1,"days":[{"day":"2026-11-13","sources":{"4chan":{"down":10,"diskWrite":11,"diskRead":12,"requests":13,"errors":14}}}]}""",
            IoStatsStore.Serialize(Days(("2026-11-13", "4chan", 10)), Today, 30));
    }

    [Fact]
    public void AMissingFileIsAnEmptyStartWithoutANotice()
    {
        using TempDirectory dir = new();
        using ProfileLease lease = ProfileLease.Acquire(dir.Path);
        IoStatsLoadResult loaded = new IoStatsStore(dir.Path, lease).Load(Today, 30, 64);
        Assert.Equal(IoStatsLoadStatus.Ok, loaded.Status);
        Assert.Equal(0, loaded.Days.Count);
        Assert.Null(loaded.Notice);
    }

    [Fact]
    public void RetentionDropsOlderDaysWhenLoadingAndWhenSaving()
    {
        using TempDirectory dir = new();
        using ProfileLease lease = ProfileLease.Acquire(dir.Path);
        IoStatsStore store = new(dir.Path, lease);
        IoDays days = Days(("2026-10-01", "a", 1), ("2026-10-14", "a", 2), ("2026-10-15", "a", 3), ("2026-11-13", "a", 4));
        // Saving with a 30-day window (today and 29 days before: from 2026-10-15) already leaves the oldest two out.
        Assert.True(store.TrySave(IoStatsStore.Serialize(days, Today, 30), out _));
        Assert.Equal(2, store.Load(Today, 365, 64).Days.Count);
        Assert.Equal(1, store.Load(Today, 1, 64).Days.Count);
        // A shorter window later drops more at load time without touching the file.
        Assert.Equal(1, store.Load(Today, 29, 64).Days.Count);
        Assert.Equal(2, store.Load(Today, 30, 64).Days.Count);
    }

    [Fact]
    public void DaysFarInTheFutureAreIgnoredButNearOnesSurviveTimeZoneTravel()
    {
        using TempDirectory dir = new();
        using ProfileLease lease = ProfileLease.Acquire(dir.Path);
        IoStatsStore store = new(dir.Path, lease);
        File.WriteAllText(Path.Combine(dir.Path, IoStatsStore.FileName),
            """{"version":1,"days":[{"day":"2026-11-14","sources":{"a":{"down":1}}},{"day":"2027-06-01","sources":{"a":{"down":2}}}]}""");
        IoStatsLoadResult loaded = store.Load(Today, 30, 64);
        Assert.Equal(IoStatsLoadStatus.Ok, loaded.Status);
        Assert.Equal(1, loaded.Days.Count);
        Assert.True(loaded.Days.TryGet(new DateOnly(2026, 11, 14), out _));
    }

    [Fact]
    public void MissingCounterFieldsReadAsZeroAndTheSourceLimitFoldsOverflowIntoOther()
    {
        using TempDirectory dir = new();
        using ProfileLease lease = ProfileLease.Acquire(dir.Path);
        StringBuilder json = new("""{"version":1,"days":[{"day":"2026-11-13","sources":{""");
        for (int i = 0; i < 20; i++) json.Append(i == 0 ? "" : ",").Append($"\"s{i:00}\":{{\"down\":{100 + i}}}");
        json.Append("}}]}");
        File.WriteAllText(Path.Combine(dir.Path, IoStatsStore.FileName), json.ToString());
        IoStatsLoadResult loaded = new IoStatsStore(dir.Path, lease).Load(Today, 30, 8);
        Assert.True(loaded.Days.TryGet(Today, out Dictionary<string, IoCounts>? sources));
        Assert.Equal(8, sources!.Count);
        Assert.Equal(0, sources["s19"].Requests);
        Assert.Equal(119, sources["s19"].NetDown);
        Assert.Equal(20 * 100 + 190, sources.Values.Sum(c => c.NetDown));
        Assert.True(sources.ContainsKey(IoStatsKeys.Other));
        Assert.False(sources.ContainsKey("s00"));
    }

    public static TheoryData<string, string> DamagedDocuments => new()
    {
        { "not json at all", "garbage" },
        { "{", "truncated" },
        { """{"version":2,"days":[]}""", "other version" },
        { """{"days":[]}""", "no version" },
        { """{"version":1}""", "no days" },
        { """[1,2,3]""", "not an object" },
        { """{"version":1,"days":[{"day":"13.11.2026","sources":{}}]}""", "bad day format" },
        { """{"version":1,"days":[{"day":"2026-11-13","sources":{"a":{"down":-1}}}]}""", "negative counter" },
        { """{"version":1,"days":[{"day":"2026-11-13","sources":{"a":{"down":1.5}}}]}""", "fractional counter" },
        { """{"version":1,"days":[{"day":"2026-11-13","sources":{"a":{"down":"7"}}}]}""", "text counter" },
        { """{"version":1,"days":[{"day":"2026-11-13","sources":{"a":7}}]}""", "source is not an object" },
        { """{"version":1,"days":[{"day":"2026-11-13","sources":{"":{"down":1}}}]}""", "empty key" },
        { """{"version":1,"days":[{"day":"2026-11-13","sources":{}},{"day":"2026-11-13","sources":{}}]}""", "day twice" },
        { """{"version":1,"version":1,"days":[]}""", "duplicate top-level property" },
        { """{"version":1,"days":[{"day":"2026-11-13","day":"2026-11-12","sources":{}}]}""", "duplicate day property" },
        { """{"version":1,"days":[{"day":"2026-11-13","sources":{"a":{"down":1},"a":{"down":2}}}]}""", "duplicate source key" },
        { """{"version":1,"days":[{"day":"2026-11-13","sources":{"a":{"down":1,"down":2}}}]}""", "duplicate counter property" },
    };

    [Theory]
    [MemberData(nameof(DamagedDocuments))]
    public void ADamagedFileIsSetAsideAndTheStatisticsStartEmpty(string content, string reason)
    {
        using TempDirectory dir = new();
        using ProfileLease lease = ProfileLease.Acquire(dir.Path);
        string path = Path.Combine(dir.Path, IoStatsStore.FileName);
        File.WriteAllText(path, content);
        IoStatsLoadResult loaded = new IoStatsStore(dir.Path, lease).Load(Today, 30, 64);
        Assert.True(loaded.Status == IoStatsLoadStatus.Damaged, reason);
        Assert.Equal(0, loaded.Days.Count);
        Assert.NotNull(loaded.Notice);
        Assert.False(File.Exists(path), reason);
        Assert.Equal(content, File.ReadAllText(path + ".bad"));
        Assert.Equal(path + ".bad", loaded.SetAsidePath);
    }

    [Fact]
    public void AnOversizedFileIsRejectedBeforeItIsRead()
    {
        using TempDirectory dir = new();
        using ProfileLease lease = ProfileLease.Acquire(dir.Path);
        string path = Path.Combine(dir.Path, IoStatsStore.FileName);
        using (FileStream stream = new(path, FileMode.Create))
        {
            stream.Write(Encoding.UTF8.GetBytes(Valid()[..^2]));
            stream.SetLength(IoStatsStore.MaximumFileBytes + 1); // sparse: the size check must not need to parse it
        }
        IoStatsLoadResult loaded = new IoStatsStore(dir.Path, lease).Load(Today, 30, 64);
        Assert.Equal(IoStatsLoadStatus.Damaged, loaded.Status);
        Assert.Contains("MiB", loaded.Notice);
        Assert.Equal(0, loaded.Days.Count);
        Assert.True(File.Exists(path + ".bad"));
    }

    [Fact]
    public void AFileJustBelowTheCeilingIsStillRead()
    {
        using TempDirectory dir = new();
        using ProfileLease lease = ProfileLease.Acquire(dir.Path);
        string path = Path.Combine(dir.Path, IoStatsStore.FileName);
        string body = Valid();
        File.WriteAllText(path, body + new string(' ', (int)(IoStatsStore.MaximumFileBytes - body.Length)));
        Assert.Equal(IoStatsStore.MaximumFileBytes, new FileInfo(path).Length);
        Assert.Equal(IoStatsLoadStatus.Ok, new IoStatsStore(dir.Path, lease).Load(Today, 30, 64).Status);
    }

    [Fact]
    public void ADamagedFileFallsBackToTheBackupOfThePreviousSave()
    {
        using TempDirectory dir = new();
        using ProfileLease lease = ProfileLease.Acquire(dir.Path);
        IoStatsStore store = new(dir.Path, lease);
        Assert.True(store.TrySave(IoStatsStore.Serialize(Days(("2026-11-13", "old", 5)), Today, 30), out _));
        Assert.True(store.TrySave(IoStatsStore.Serialize(Days(("2026-11-13", "new", 9)), Today, 30), out _));
        Assert.True(File.Exists(store.FilePath + ".bak"));
        File.WriteAllText(store.FilePath, "{ broken");
        IoStatsLoadResult loaded = store.Load(Today, 30, 64);
        Assert.Equal(IoStatsLoadStatus.RestoredFromBackup, loaded.Status);
        Assert.True(loaded.Days.TryGet(Today, out Dictionary<string, IoCounts>? sources));
        Assert.Equal(5, sources!["old"].NetDown);
        Assert.Contains("previous copy", loaded.Notice);
        Assert.Equal("{ broken", File.ReadAllText(store.FilePath + ".bad"));
    }

    [Fact]
    public void ALinkInsteadOfAFileIsTreatedAsDamageAndNotFollowed()
    {
        if (OperatingSystem.IsWindows()) return; // creating links needs privileges there
        using TempDirectory dir = new();
        using ProfileLease lease = ProfileLease.Acquire(dir.Path);
        string target = Path.Combine(dir.Path, "elsewhere.json");
        File.WriteAllText(target, Valid());
        File.CreateSymbolicLink(Path.Combine(dir.Path, IoStatsStore.FileName), target);
        IoStatsLoadResult loaded = new IoStatsStore(dir.Path, lease).Load(Today, 30, 64);
        Assert.Equal(IoStatsLoadStatus.Damaged, loaded.Status);
        Assert.Equal(0, loaded.Days.Count);
        Assert.True(File.Exists(target));
    }

    [Fact]
    public void ALockedOrUnreadableFileLeavesTheFileAloneAndStartsEmpty()
    {
        if (OperatingSystem.IsWindows()) return;
        using TempDirectory dir = new();
        using ProfileLease lease = ProfileLease.Acquire(dir.Path);
        string path = Path.Combine(dir.Path, IoStatsStore.FileName);
        File.WriteAllText(path, Valid());
        File.SetUnixFileMode(path, UnixFileMode.None);
        try
        {
            if (Environment.UserName == "root") return; // root reads anything
            IoStatsLoadResult loaded = new IoStatsStore(dir.Path, lease).Load(Today, 30, 64);
            Assert.Equal(IoStatsLoadStatus.Unreadable, loaded.Status);
            Assert.Equal(0, loaded.Days.Count);
            Assert.True(File.Exists(path));
            Assert.False(File.Exists(path + ".bad"));
        }
        finally { File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
    }

    [Fact]
    public void AFailedWriteIsReportedAndNeverThrows()
    {
        using TempDirectory dir = new();
        using ProfileLease lease = ProfileLease.Acquire(dir.Path);
        IoStatsStore store = new(dir.Path, lease);
        Directory.CreateDirectory(store.FilePath); // a directory where the file belongs: replacing it must fail
        Assert.False(store.TrySave(IoStatsStore.Serialize(Days(("2026-11-13", "a", 1)), Today, 30), out string? error));
        Assert.False(string.IsNullOrEmpty(error));
    }

    [Fact]
    public void DeleteRemovesTheFileItsBackupAndTheSetAsideCopy()
    {
        using TempDirectory dir = new();
        using ProfileLease lease = ProfileLease.Acquire(dir.Path);
        IoStatsStore store = new(dir.Path, lease);
        Assert.True(store.TrySave(IoStatsStore.Serialize(Days(("2026-11-13", "a", 1)), Today, 30), out _));
        Assert.True(store.TrySave(IoStatsStore.Serialize(Days(("2026-11-13", "a", 2)), Today, 30), out _));
        File.WriteAllText(store.FilePath + ".bad", "x");
        Assert.True(store.Delete());
        Assert.Empty(Directory.GetFiles(dir.Path, "io-stats*"));
    }

    [Fact]
    public void ADocumentAtTheLargestSettingsStaysReadable()
    {
        using TempDirectory dir = new();
        using ProfileLease lease = ProfileLease.Acquire(dir.Path);
        IoStatsStore store = new(dir.Path, lease);
        IoDays days = new();
        for (int d = 0; d < 365; d++)
            for (int s = 0; s < 256; s++)
                days.Add(Today.AddDays(-d), "source-with-a-rather-long-name-" + s, new IoCounts(NetDown: long.MaxValue / 2, NetUp: 0, DiskWritten: 1_000_000_000_000, DiskRead: 1_000_000_000, Requests: 999_999, Errors: 12), 256);
        string json = IoStatsStore.Serialize(days, Today, 365);
        Assert.True(Encoding.UTF8.GetByteCount(json) <= IoStatsStore.MaximumFileBytes);
        Assert.True(store.TrySave(json, out _));
        IoStatsLoadResult loaded = store.Load(Today, 365, 256);
        Assert.Equal(IoStatsLoadStatus.Ok, loaded.Status);
        Assert.Equal(365, loaded.Days.Count);
        Assert.True(loaded.Days.TryGet(Today, out Dictionary<string, IoCounts>? newest));
        Assert.Equal(256, newest!.Count);
    }
}
