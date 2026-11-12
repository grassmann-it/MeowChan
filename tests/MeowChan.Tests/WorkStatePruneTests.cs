using System.Text.Json;
using MeowChan.Downloader;
using MeowChan.Persistence;
using Microsoft.Data.Sqlite;

namespace MeowChan.Tests;

// Retention: only work_media rows of superseded generations are removed, after a verified backup.
public sealed class WorkStatePruneTests
{
    private const string Watching = "https://stub.test/g/thread/201";
    private const string Completed = "https://stub.test/g/thread/202";
    private const string Suppressed = "https://stub.test/g/thread/203";
    private const string Unreadable = "https://stub.test/g/thread/204";
    private const string Orphan = "https://stub.test/g/thread/205";
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task StartupRemovesOnlySupersededMediaAndKeepsABackup()
    {
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        WorkStateStore work = new(directory.Path, lease);
        Seed(work, directory.Path);
        string[] before = MediaKeys(Journal(directory.Path));
        Assert.Equal(12, before.Length);
        string[] resources = ResourceRows(Journal(directory.Path));

        await using (Registry registry = await StartAsync(directory.Path, lease, work)) { }

        Assert.Equal(new[]
        {
            Watching + "|3|a", Watching + "|3|b", Completed + "|2|a", Suppressed + "|4|a",
            Unreadable + "|1|a", Unreadable + "|2|a", Orphan + "|1|a", Orphan + "|2|a"
        }.Order(StringComparer.Ordinal), MediaKeys(Journal(directory.Path)));
        Assert.Equal(resources, ResourceRows(Journal(directory.Path)));
        // The backup is a complete pre-deletion database that opens as a work journal.
        string backup = Path.Combine(directory.Path, WorkStateStore.PruneBackupFileName);
        Assert.Equal(before, MediaKeys(backup));
        Assert.Equal(resources, ResourceRows(backup));
        Assert.Equal("ok", QuickCheck(backup));
        Assert.False(File.Exists(backup + ".tmp"));
        Assert.Equal(["a"], OpenBackupAsJournal(backup, store => store.LoadMedia(Watching, 1))
            .Select(media => media.MediaIdentifier).ToArray());
        // Current-generation readers are unaffected.
        Assert.Equal(["a", "b"], work.LoadMedia(Watching, 3).Select(media => media.MediaIdentifier).ToArray());
        Assert.Empty(work.LoadMedia(Watching, 2));
    }

    [Fact]
    public async Task RestoreReadsTheSameWorkBeforeAndAfterPruning()
    {
        using TempDirectory directory = new();
        string backup = Path.Combine(directory.Path, WorkStateStore.PruneBackupFileName);
        using (ProfileLease seed = ProfileLease.Acquire(directory.Path)) Seed(new WorkStateStore(directory.Path, seed), directory.Path);

        // First session without pruning: a directory at the backup name makes the backup fail.
        Directory.CreateDirectory(backup);
        string unpruned, unprunedRegistry;
        using (ProfileLease lease = ProfileLease.Acquire(directory.Path))
        {
            WorkStateStore work = new(directory.Path, lease);
            await using (Registry registry = await StartAsync(directory.Path, lease, work))
                unprunedRegistry = RegistryView(registry);
            unpruned = StoreView(work);
        }
        Assert.Equal(12, MediaKeys(Journal(directory.Path)).Length);
        Directory.Delete(backup);

        string pruned, prunedRegistry;
        using (ProfileLease lease = ProfileLease.Acquire(directory.Path))
        {
            WorkStateStore work = new(directory.Path, lease);
            await using (Registry registry = await StartAsync(directory.Path, lease, work))
                prunedRegistry = RegistryView(registry);
            pruned = StoreView(work);
        }

        Assert.Equal(8, MediaKeys(Journal(directory.Path)).Length);
        Assert.Contains(Watching, unprunedRegistry);
        Assert.Equal(unprunedRegistry, prunedRegistry);
        Assert.Equal(unpruned, pruned);
    }

    [Fact]
    public void SecondRunRemovesNothingAndKeepsTheSingleBackup()
    {
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        WorkStateStore work = new(directory.Path, lease);
        Seed(work, directory.Path);

        Assert.Equal(new WorkJournalPrune(4, null), work.PruneSupersededMedia(null));
        string backup = Path.Combine(directory.Path, WorkStateStore.PruneBackupFileName);
        byte[] first = File.ReadAllBytes(backup);
        DateTime written = File.GetLastWriteTimeUtc(backup);
        string[] after = MediaKeys(Journal(directory.Path));

        Assert.Equal(new WorkJournalPrune(0, null), work.PruneSupersededMedia(null));
        Assert.Equal(after, MediaKeys(Journal(directory.Path)));
        Assert.Equal(first, File.ReadAllBytes(backup));
        Assert.Equal(written, File.GetLastWriteTimeUtc(backup));
        Assert.Equal([WorkStateStore.PruneBackupFileName], Directory.EnumerateFiles(directory.Path, "work-state.before-prune*")
            .Select(path => Path.GetFileName(path)!).ToArray());
    }

    [Fact]
    public void LaterSupersessionReplacesTheBackupInsteadOfAddingOne()
    {
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        WorkStateStore work = new(directory.Path, lease);
        Seed(work, directory.Path);
        Assert.Equal(4, work.PruneSupersededMedia(null).Removed);
        Commit(work, directory.Path, Watching, 4, WorkIntent.Watching, "c");
        string[] before = MediaKeys(Journal(directory.Path));

        Assert.Equal(new WorkJournalPrune(2, null), work.PruneSupersededMedia(null));

        Assert.Equal(before, MediaKeys(Path.Combine(directory.Path, WorkStateStore.PruneBackupFileName)));
        Assert.Single(Directory.EnumerateFiles(directory.Path, "work-state.before-prune*"));
    }

    [Theory]
    [InlineData("")]
    [InlineData(".tmp")]
    public async Task FailedBackupKeepsEveryRowAndWarns(string blocked)
    {
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        WorkStateStore work = new(directory.Path, lease);
        Seed(work, directory.Path);
        string[] before = AllRows(Journal(directory.Path));
        // A directory at the backup (or its partial copy's) name makes the copy fail on every platform.
        Directory.CreateDirectory(Path.Combine(directory.Path, WorkStateStore.PruneBackupFileName + blocked));

        await using (Registry registry = await StartAsync(directory.Path, lease, work))
        {
            Assert.Contains(registry.GetSnapshot().Warnings, warning =>
                warning.Contains(WorkStateStore.PruneBackupFileName, StringComparison.Ordinal) && warning.Contains("4 superseded", StringComparison.Ordinal));
            Assert.Contains(Watching, registry.Threads.Select(thread => thread.ThreadUri.AbsoluteUri));
        }

        Assert.Equal(before, AllRows(Journal(directory.Path)));
        Assert.False(File.Exists(Path.Combine(directory.Path, WorkStateStore.PruneBackupFileName)));
        Assert.False(File.Exists(Path.Combine(directory.Path, WorkStateStore.PruneBackupFileName + ".tmp")));
    }

    [Fact]
    public void InterruptedDeletionLeavesTheJournalUnchangedAndConsistent()
    {
        using TempDirectory directory = new();
        using TempDirectory crash = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        WorkStateStore work = new(directory.Path, lease);
        Seed(work, directory.Path);
        string[] before = AllRows(Journal(directory.Path));
        string[] copied = [];
        work.PruneDeleteObserver = processed =>
        {
            if (processed != 2) return;
            // The on-disk image at this point is what a process killed mid-transaction leaves behind.
            copied = Directory.EnumerateFiles(directory.Path, "work-state.sqlite*").Select(path =>
            {
                string target = Path.Combine(crash.Path, Path.GetFileName(path));
                File.Copy(path, target);
                return Path.GetFileName(path);
            }).ToArray();
            throw new SimulatedInterruption();
        };

        Assert.Throws<SimulatedInterruption>(() => work.PruneSupersededMedia(null));

        Assert.Contains("work-state.sqlite", copied);
        Assert.Equal(before, AllRows(Journal(directory.Path)));
        Assert.Equal("ok", QuickCheck(Journal(directory.Path)));
        // The backup precedes the first DELETE and is complete.
        Assert.Equal(before, AllRows(Path.Combine(directory.Path, WorkStateStore.PruneBackupFileName)));
        // Reopening the interrupted image (hot journal included, if any) yields the untouched journal.
        using (ProfileLease recovered = ProfileLease.Acquire(crash.Path))
        {
            WorkStateStore reopened = new(crash.Path, recovered);
            Assert.Equal(["a", "b"], reopened.LoadMedia(Watching, 3).Select(media => media.MediaIdentifier).ToArray());
        }
        Assert.Equal(before, AllRows(Journal(crash.Path)));
        Assert.Equal("ok", QuickCheck(Journal(crash.Path)));
        // The next run completes normally.
        work.PruneDeleteObserver = null;
        Assert.Equal(new WorkJournalPrune(4, null), work.PruneSupersededMedia(null));
    }

    [Fact]
    public async Task CorruptRowsNeitherBlockPruningNorCostValidRows()
    {
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        WorkStateStore work = new(directory.Path, lease);
        Seed(work, directory.Path);
        WorkResource completed = work.LoadReadableResources().Resources.Single(resource => resource.Url == Completed);
        // A key-mismatched owner is unreadable for restore: its generation is unknown and all its media stay.
        Execute(directory.Path, "UPDATE work_resources SET payload=$payload WHERE url=$url",
            ("$url", Completed), ("$payload", JsonSerializer.Serialize(completed with { Url = "https://stub.test/g/thread/999" }, Web)));
        // A corrupt superseded media row goes with its generation; a corrupt current one stays untouched.
        Execute(directory.Path, "UPDATE work_media SET payload='{\"status\":' WHERE resource_url=$url AND generation=1", ("$url", Watching));
        Execute(directory.Path, "UPDATE work_media SET payload='{\"status\":' WHERE resource_url=$url AND generation=4", ("$url", Suppressed));
        string[] keptRows = AllRows(Journal(directory.Path)).Where(row =>
            !row.StartsWith("m|" + Watching + "|1|", StringComparison.Ordinal) && !row.StartsWith("m|" + Watching + "|2|", StringComparison.Ordinal)
            && !row.StartsWith("m|" + Suppressed + "|3|", StringComparison.Ordinal)).ToArray();

        await using (Registry registry = await StartAsync(directory.Path, lease, work))
            Assert.Contains(Watching, registry.Threads.Select(thread => thread.ThreadUri.AbsoluteUri));

        Assert.Equal(keptRows, AllRows(Journal(directory.Path)));
        Assert.Contains(Completed + "|1|a", MediaKeys(Journal(directory.Path)));
        Assert.Contains("m|" + Suppressed + "|4|a|original|", string.Join("\n", keptRows));
        Assert.Equal(["a", "b"], work.LoadMedia(Watching, 3).Select(media => media.MediaIdentifier).ToArray());
    }

    [Fact]
    public async Task ReAddAfterPruningStartsTheNextGenerationAndRestoresIt()
    {
        using TempDirectory directory = new();
        using (ProfileLease lease = ProfileLease.Acquire(directory.Path))
        {
            WorkStateStore work = new(directory.Path, lease);
            Seed(work, directory.Path);
            await using Registry registry = await StartAsync(directory.Path, lease, work);
            Assert.Contains(Watching, registry.Threads.Select(thread => thread.ThreadUri.AbsoluteUri));
            // A completed thread was never admitted; its explicit re-add continues from generation 2.
            await registry.AddAsync(Completed);
            Assert.Equal(3, WorkStateAccess.For(work).FindResource(Completed)!.Generation);
        }
        // The re-add superseded generation 2; it stays until the next session starts.
        Assert.Contains(Completed + "|2|a", MediaKeys(Journal(directory.Path)));

        using (ProfileLease lease = ProfileLease.Acquire(directory.Path))
        {
            WorkStateStore work = new(directory.Path, lease);
            await using Registry registry = await StartAsync(directory.Path, lease, work);
            Assert.Contains(Completed, registry.Threads.Select(thread => thread.ThreadUri.AbsoluteUri));
            Assert.Equal(WorkIntent.Watching, WorkStateAccess.For(work).FindResource(Completed)!.Intent);
            Assert.Contains(Watching, registry.Threads.Select(thread => thread.ThreadUri.AbsoluteUri));
            Assert.Equal(["a", "b"], work.LoadMedia(Watching, 3).Select(media => media.MediaIdentifier).ToArray());
        }
        Assert.DoesNotContain(MediaKeys(Journal(directory.Path)), key => key.StartsWith(Completed, StringComparison.Ordinal));
    }

    [Fact]
    public void JournalWithoutSupersededRowsIsNotCopied()
    {
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        WorkStateStore work = new(directory.Path, lease);
        Commit(work, directory.Path, Watching, 1, WorkIntent.Watching, "a");

        Assert.Equal(new WorkJournalPrune(0, null), work.PruneSupersededMedia(null));
        Assert.Empty(Directory.EnumerateFiles(directory.Path, "work-state.before-prune*"));
    }

    // Journals written by earlier builds: version 1 (current layout) and version 0 (before the variant column).
    [Theory]
    [InlineData(1)]
    [InlineData(0)]
    public void EarlierJournalFileOpensPrunesAndKeepsSchemaVersionOne(int version)
    {
        using TempDirectory directory = new();
        string media = version == 0
            ? "CREATE TABLE work_media(resource_url TEXT NOT NULL,generation INTEGER NOT NULL,media_id TEXT NOT NULL,target TEXT NOT NULL,payload TEXT NOT NULL,PRIMARY KEY(resource_url,generation,media_id,target));"
            : "CREATE TABLE work_media(resource_url TEXT NOT NULL,generation INTEGER NOT NULL,media_id TEXT NOT NULL,variant TEXT NOT NULL DEFAULT 'original',target TEXT NOT NULL,payload TEXT NOT NULL,PRIMARY KEY(resource_url,generation,media_id,variant,target));";
        Execute(directory.Path, "CREATE TABLE work_resources(url TEXT PRIMARY KEY,payload TEXT NOT NULL); " + media + " PRAGMA user_version=" + version);
        WorkResource owner = new() { Url = Watching, OutputRoot = directory.Path, Generation = 2, Intent = WorkIntent.Watching, IsThread = true };
        Execute(directory.Path, "INSERT INTO work_resources VALUES($url,$payload)", ("$url", Watching), ("$payload", JsonSerializer.Serialize(owner, Web)));
        foreach (long generation in new long[] { 1, 2 })
        {
            MediaWork row = new() { ResourceUrl = Watching, Generation = generation, MediaIdentifier = "a",
                DestinationPath = Path.Combine(directory.Path, "a.bin"), DownloadUrl = "https://stub.test/a.bin", Status = MediaWorkStatus.Succeeded };
            Execute(directory.Path, version == 0
                    ? "INSERT INTO work_media(resource_url,generation,media_id,target,payload) VALUES($url,$generation,'a',$target,$payload)"
                    : "INSERT INTO work_media(resource_url,generation,media_id,variant,target,payload) VALUES($url,$generation,'a','original',$target,$payload)",
                ("$url", Watching), ("$generation", generation), ("$target", row.DestinationPath), ("$payload", JsonSerializer.Serialize(row, Web)));
        }

        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        WorkStateStore work = new(directory.Path, lease);
        Assert.Equal(new WorkJournalPrune(1, null), work.PruneSupersededMedia(null));

        Assert.Equal([Watching + "|2|a"], MediaKeys(Journal(directory.Path)));
        Assert.Equal(1L, Scalar(Journal(directory.Path), "PRAGMA user_version"));
        string backup = Path.Combine(directory.Path, WorkStateStore.PruneBackupFileName);
        Assert.Equal(1L, Scalar(backup, "PRAGMA user_version"));
        Assert.Equal([Watching + "|1|a", Watching + "|2|a"], MediaKeys(backup));
        Assert.Equal(["a"], work.LoadMedia(Watching, 2).Select(item => item.MediaIdentifier).ToArray());
        Assert.Equal(Watching, Assert.Single(work.LoadResources()).Url);
    }

    private sealed class SimulatedInterruption : Exception;

    private static async Task<Registry> StartAsync(string directory, ProfileLease lease, WorkStateStore work)
    {
        using StubImageBoard provider = new();
        DownloadQueue queue = new(1, 1L << 30);
        queue.SetPaused(true);
        Registry registry = new([provider], queue, directory, TimeSpan.FromMinutes(1), 60,
            new SessionStore(directory, lease), workStore: work);
        await registry.StartAsync(lease);
        return registry;
    }

    // What restore reads from the journal: every readable owner, the unreadable keys and each
    // readable owner's current-generation media.
    private static string StoreView(WorkStateStore work)
    {
        WorkResourceLoad load = work.LoadReadableResources();
        return JsonSerializer.Serialize(new
        {
            Resources = load.Resources,
            Unreadable = load.Unreadable.Select(row => row.Url),
            Media = load.Resources.Select(resource => work.LoadMedia(resource.Url, resource.Generation))
        }, Web);
    }

    private static string RegistryView(Registry registry) => JsonSerializer.Serialize(new
    {
        Threads = registry.Threads.Select(thread => thread.ThreadUri.AbsoluteUri).Order(StringComparer.Ordinal),
        Work = registry.GetSnapshot().Threads.Where(thread => thread.Work != null).OrderBy(thread => thread.Url, StringComparer.Ordinal)
            .Select(thread => new { thread.Url, thread.DownloadDirectory, thread.Work!.Intent, thread.Work.Source,
                thread.Work.EndObservedAt, thread.Work.CapturePending, thread.Work.Media })
    }, Web);

    private static T OpenBackupAsJournal<T>(string backup, Func<WorkStateStore, T> read)
    {
        using TempDirectory restored = new();
        File.Copy(backup, Journal(restored.Path));
        using ProfileLease lease = ProfileLease.Acquire(restored.Path);
        return read(new WorkStateStore(restored.Path, lease));
    }

    // Watching gen 3 (superseded 1, 2), completed gen 2 (superseded 1), suppressed gen 4 (superseded 3),
    // an unreadable resource row with two generations and media without any resource row.
    private static void Seed(WorkStateStore work, string root)
    {
        Commit(work, root, Watching, 1, WorkIntent.Watching, "a");
        Commit(work, root, Watching, 2, WorkIntent.Watching, "a");
        Commit(work, root, Watching, 3, WorkIntent.Watching, "a", "b");
        Commit(work, root, Completed, 1, WorkIntent.Watching, "a");
        Commit(work, root, Completed, 2, WorkIntent.Completed, "a");
        Commit(work, root, Suppressed, 3, WorkIntent.Watching, "a");
        Commit(work, root, Suppressed, 4, WorkIntent.Suppressed, "a");
        Commit(work, root, Unreadable, 1, WorkIntent.Watching, "a");
        Commit(work, root, Unreadable, 2, WorkIntent.Watching, "a");
        Commit(work, root, Orphan, 1, WorkIntent.Watching, "a");
        Commit(work, root, Orphan, 2, WorkIntent.Watching, "a");
        Execute(root, "UPDATE work_resources SET payload='{\"url\":' WHERE url=$unreadable; DELETE FROM work_resources WHERE url=$orphan",
            ("$unreadable", Unreadable), ("$orphan", Orphan));
    }

    private static void Commit(WorkStateStore work, string root, string url, long generation, WorkIntent intent, params string[] media)
    {
        WorkResource resource = new() { Url = url, OutputRoot = root, Generation = generation,
            Intent = WorkIntent.Watching, IsThread = true, Source = ThreadSourceStateValue.Active };
        work.CommitIntent(resource);
        foreach (string id in media)
            Assert.True(work.CommitMedia(new MediaWork { ResourceUrl = url, Generation = generation, MediaIdentifier = id,
                DestinationPath = Path.Combine(root, url[^3..], id + ".bin"), DownloadUrl = "https://stub.test/" + id + ".bin",
                Status = MediaWorkStatus.Succeeded }));
        if (intent != WorkIntent.Watching)
            Assert.NotNull(work.CommitState(resource with { Intent = intent,
                Source = intent == WorkIntent.Completed ? ThreadSourceStateValue.NotFound : ThreadSourceStateValue.Active,
                EndObservedAt = intent == WorkIntent.Completed ? DateTimeOffset.UtcNow.AddDays(-1) : null }));
    }

    private static string Journal(string directory) => Path.Combine(directory, "work-state.sqlite");

    private static SqliteConnection Connect(string path)
    {
        SqliteConnection db = new(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        db.Open();
        return db;
    }

    private static void Execute(string directory, string sql, params (string Name, object Value)[] parameters)
    {
        using SqliteConnection db = Connect(Journal(directory));
        using SqliteCommand command = db.CreateCommand();
        command.CommandText = sql;
        foreach ((string name, object value) in parameters) command.Parameters.AddWithValue(name, value);
        command.ExecuteNonQuery();
    }

    private static long Scalar(string path, string sql)
    {
        using SqliteConnection db = Connect(path);
        using SqliteCommand command = db.CreateCommand();
        command.CommandText = sql;
        return (long)command.ExecuteScalar()!;
    }

    private static string QuickCheck(string path)
    {
        using SqliteConnection db = Connect(path);
        using SqliteCommand command = db.CreateCommand();
        command.CommandText = "PRAGMA quick_check";
        return (string)command.ExecuteScalar()!;
    }

    private static string[] Query(string path, string sql)
    {
        using SqliteConnection db = Connect(path);
        using SqliteCommand command = db.CreateCommand();
        command.CommandText = sql;
        using SqliteDataReader reader = command.ExecuteReader();
        List<string> rows = new();
        while (reader.Read()) rows.Add(reader.GetString(0));
        return rows.Order(StringComparer.Ordinal).ToArray();
    }

    private static string[] MediaKeys(string path) => Query(path, "SELECT resource_url||'|'||generation||'|'||media_id FROM work_media");
    private static string[] ResourceRows(string path) => Query(path, "SELECT url||'|'||payload FROM work_resources");
    private static string[] AllRows(string path) => Query(path,
        "SELECT 'r|'||url||'|'||payload FROM work_resources UNION ALL SELECT 'm|'||resource_url||'|'||generation||'|'||media_id||'|'||variant||'|'||target||'|'||payload FROM work_media");
}
