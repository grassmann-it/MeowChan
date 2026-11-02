using System.Text.Json;
using System.Text.Json.Nodes;
using MeowChan.Persistence;
using Microsoft.Data.Sqlite;

namespace MeowChan.Tests;

// P08e-01 only: construction and passive reads. Runtime commit/producer fencing
// and Host Startup-token propagation are deliberately separate integration slices.
public sealed class WorkStateConstructionAdmissionTests
{
    private const string Url = "https://stub.test/g/thread/42";
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(15);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public async Task NewAndLegacyConstructionUsesLocalOrBorrowedStartupAndOrdinary(
        bool ready, bool borrowed, bool legacy)
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        if (legacy) SeedLegacy(temp.Path);
        if (ready) lease.Admission.CompleteStartup();
        using ProfileAdmissionToken? admission = borrowed ? lease.Admission.EnterStoreWriter() : null;
        if (admission is not null)
            Assert.Equal(ready ? ProfileAdmissionKind.Ordinary : ProfileAdmissionKind.Startup, admission.Kind);

        WorkStateStore store = borrowed
            ? new WorkStateStore(temp.Path, lease, admission!)
            : new WorkStateStore(temp.Path, lease);
        Assert.True(File.Exists(DatabasePath(temp.Path)));
        if (legacy)
        {
            Assert.Equal(Resource(temp.Path), Assert.Single(store.LoadResources()));
            Assert.Equal(Media(temp.Path), Assert.Single(store.LoadMedia(Url, 1)));
            Assert.Contains("variant", Columns(temp.Path));
            using SqliteConnection db = OpenFixture(temp.Path, SqliteOpenMode.ReadOnly);
            using SqliteCommand command = db.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE name='work_media_legacy'";
            Assert.Equal(0L, command.ExecuteScalar());
        }
        else
        {
            Assert.Empty(store.LoadResources());
            Assert.Empty(store.LoadMedia(Url, 1));
        }
        Assert.Equal(ready ? ProfileAdmissionPhase.Ready : ProfileAdmissionPhase.Starting, lease.Admission.Phase);
        if (admission is not null)
            Assert.True(lease.RunStoreWrite(admission, () => true)); // Constructor borrowed, never released it.
        if (!ready)
        {
            if (borrowed) Assert.Throws<InvalidOperationException>(lease.Admission.CompleteStartup);
            admission?.Dispose();
            lease.Admission.CompleteStartup(); // Local constructor must not leave a reservation behind.
        }
        else
        {
            using CancellationTokenSource cancel = new();
            Task<ProfileAdmissionToken> pending = lease.Admission.EnterExclusiveAsync(cancel.Token).AsTask();
            try
            {
                Assert.Equal(!borrowed, pending.IsCompletedSuccessfully);
                admission?.Dispose();
                using ProfileAdmissionToken exclusive = await pending.WaitAsync(Deadline);
                Assert.Equal(ProfileAdmissionKind.Exclusive, exclusive.Kind);
            }
            finally
            {
                admission?.Dispose();
                cancel.Cancel();
                try { (await pending.WaitAsync(Deadline)).Dispose(); }
                catch (OperationCanceledException) { }
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PendingAndActiveExclusiveRejectLocalConstructionWithoutCreatingOrMigrating(bool legacy)
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        if (legacy) SeedLegacy(temp.Path);
        lease.Admission.CompleteStartup();
        using ProfileAdmissionToken ordinary = lease.Admission.EnterOrdinaryWriter();
        using CancellationTokenSource cancel = new();
        Task<ProfileAdmissionToken> pending = lease.Admission.EnterExclusiveAsync(cancel.Token).AsTask();
        try
        {
            Assert.False(pending.IsCompleted);
            string[] before = Inventory(temp.Path);
            Assert.Throws<InvalidOperationException>(() => new WorkStateStore(temp.Path, lease));
            Assert.Equal(before, Inventory(temp.Path));
            ordinary.Dispose();
            using ProfileAdmissionToken exclusive = await pending.WaitAsync(Deadline);
            Assert.Throws<InvalidOperationException>(() => new WorkStateStore(temp.Path, lease));
            Assert.Throws<InvalidOperationException>(() => new WorkStateStore(temp.Path, lease, exclusive));
            Assert.Equal(before, Inventory(temp.Path));
            if (legacy) Assert.DoesNotContain("variant", Columns(temp.Path));
            else Assert.False(File.Exists(DatabasePath(temp.Path)));
        }
        finally
        {
            ordinary.Dispose();
            cancel.Cancel();
            try { (await pending.WaitAsync(Deadline)).Dispose(); }
            catch (OperationCanceledException) { }
        }
    }

    [Theory]
    [InlineData("failed", false)]
    [InlineData("failed", true)]
    [InlineData("stopping", false)]
    [InlineData("stopping", true)]
    [InlineData("stopped", false)]
    [InlineData("stopped", true)]
    public void RecoveryAndShutdownRejectLocalConstructionWithoutCreatingOrMigrating(string phase, bool legacy)
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        if (legacy) SeedLegacy(temp.Path);
        if (phase == "failed") lease.Admission.FailStartup();
        else
        {
            lease.Admission.BeginShutdown();
            if (phase == "stopped") lease.Admission.CompleteShutdown();
        }
        string[] before = Inventory(temp.Path);
        Assert.Throws<InvalidOperationException>(() => new WorkStateStore(temp.Path, lease));
        Assert.Equal(before, Inventory(temp.Path));
        if (legacy) Assert.DoesNotContain("variant", Columns(temp.Path));
        else Assert.False(File.Exists(DatabasePath(temp.Path)));
    }

    [Fact]
    public void NullForeignUnissuedAndDisposedBorrowedTokensFailBeforeCreatingDatabase()
    {
        using TempDirectory temp = new();
        using TempDirectory other = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        using ProfileLease foreignLease = ProfileLease.Acquire(other.Path);
        using ProfileAdmissionToken foreign = foreignLease.Admission.EnterStartupWriter();
        using ProfileAdmissionToken unissued = ProfileAdmissionToken.Create(lease.Admission, ProfileAdmissionKind.Startup);
        using ProfileAdmissionToken disposed = lease.Admission.EnterStartupWriter();
        disposed.Dispose();
        string[] before = Inventory(temp.Path);
        foreach (ProfileAdmissionToken invalid in new[] { null!, foreign, unissued, disposed })
        {
            Assert.Throws<InvalidOperationException>(() => new WorkStateStore(temp.Path, lease, invalid));
            Assert.Equal(before, Inventory(temp.Path));
            Assert.False(File.Exists(DatabasePath(temp.Path)));
        }
        Assert.True(foreignLease.RunStoreWrite(foreign, () => true));
        Assert.False(File.Exists(DatabasePath(other.Path)));
        lease.Admission.CompleteStartup();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InitializationFailureReleasesOnlyLocallyOwnedAdmission(bool borrowed)
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        // Force Open to fail without needing OS-specific filesystem permissions.
        Directory.CreateDirectory(DatabasePath(temp.Path));
        using ProfileAdmissionToken? admission = borrowed ? lease.Admission.EnterStartupWriter() : null;
        Assert.Throws<SqliteException>(() =>
        {
            if (borrowed) _ = new WorkStateStore(temp.Path, lease, admission!);
            else _ = new WorkStateStore(temp.Path, lease);
        });
        Assert.Empty(Inventory(temp.Path));
        Directory.Delete(DatabasePath(temp.Path));
        if (borrowed)
        {
            Assert.Throws<InvalidOperationException>(lease.Admission.CompleteStartup);
            _ = new WorkStateStore(temp.Path, lease, admission!); // Same reservation remains usable after failure.
            admission!.Dispose();
        }
        lease.Admission.CompleteStartup();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MismatchedPathIsRejectedBeforeOpeningEitherDatabase(bool borrowed)
    {
        using TempDirectory temp = new();
        using TempDirectory other = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        using ProfileAdmissionToken? admission = borrowed ? lease.Admission.EnterStartupWriter() : null;
        Assert.Throws<ArgumentException>(() =>
        {
            if (borrowed) _ = new WorkStateStore(other.Path, lease, admission!);
            else _ = new WorkStateStore(other.Path, lease);
        });
        Assert.Empty(Inventory(temp.Path));
        Assert.Empty(Inventory(other.Path));
        if (borrowed) Assert.True(lease.RunStoreWrite(admission!, () => true));
        admission?.Dispose();
        lease.Admission.CompleteStartup();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AcceptedStartupCanStillInitializeAfterRecoveryFailureOrShutdownSeal(bool shutdown)
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        using ProfileAdmissionToken startup = lease.Admission.EnterStartupWriter();
        if (shutdown) lease.Admission.BeginShutdown();
        else lease.Admission.FailStartup();
        Assert.Throws<InvalidOperationException>(() => new WorkStateStore(temp.Path, lease));
        WorkStateStore store = new(temp.Path, lease, startup);
        Assert.Empty(store.LoadResources());
        Assert.True(lease.RunStoreWrite(startup, () => true));
        lease.Admission.BeginShutdown();
        Task drain = lease.Admission.DrainAsync();
        Assert.False(drain.IsCompleted);
        startup.Dispose();
        Assert.True(drain.IsCompletedSuccessfully);
        lease.Admission.CompleteShutdown();
    }

    [Fact]
    public async Task AcceptedOrdinaryInitializesWhileExclusiveIsPendingWithoutNestedAdmission()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        SeedLegacy(temp.Path);
        lease.Admission.CompleteStartup();
        using ProfileAdmissionToken ordinary = lease.Admission.EnterOrdinaryWriter();
        using CancellationTokenSource cancel = new();
        Task<ProfileAdmissionToken> pending = lease.Admission.EnterExclusiveAsync(cancel.Token).AsTask();
        try
        {
            Assert.False(pending.IsCompleted);
            WorkStateStore store = new(temp.Path, lease, ordinary);
            Assert.Equal(Media(temp.Path), Assert.Single(store.LoadMedia(Url, 1)));
            Assert.Contains("variant", Columns(temp.Path));
            Assert.False(pending.IsCompleted); // Borrowed token still belongs to the caller.
            ordinary.Dispose();
            using ProfileAdmissionToken exclusive = await pending.WaitAsync(Deadline);
        }
        finally
        {
            ordinary.Dispose();
            cancel.Cancel();
            try { (await pending.WaitAsync(Deadline)).Dispose(); }
            catch (OperationCanceledException) { }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConstructorPinsBeforeLeaseWaitSoDisposeCannotCompleteExclusiveOrDrain(bool shutdown)
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        lease.Admission.CompleteStartup();
        using ProfileAdmissionToken ordinary = lease.Admission.EnterOrdinaryWriter();
        using CancellationTokenSource cancel = new();
        TaskCompletionSource<WorkStateStore> constructed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Thread worker = new(() =>
        {
            try { constructed.TrySetResult(new WorkStateStore(temp.Path, lease, ordinary)); }
            catch (Exception error) { constructed.TrySetException(error); }
        }) { IsBackground = true };
        Task<ProfileAdmissionToken>? exclusive = null;
        Task? drain = null;
        bool started = false;
        try
        {
            lease.RunExclusive(() =>
            {
                // The worker has no waits of its own. It must block at the lease
                // monitor, after the constructor's synchronous admission use-pin.
                worker.Start();
                started = true;
                Assert.True(SpinWait.SpinUntil(() =>
                    (worker.ThreadState & System.Threading.ThreadState.WaitSleepJoin) != 0
                    || constructed.Task.IsCompleted, Deadline), "Constructor did not reach the held lease monitor.");
                Assert.False(constructed.Task.IsCompleted);
                Assert.False(File.Exists(DatabasePath(temp.Path)));
                if (shutdown)
                {
                    lease.Admission.BeginShutdown();
                    drain = lease.Admission.DrainAsync();
                }
                else exclusive = lease.Admission.EnterExclusiveAsync(cancel.Token).AsTask();
                ordinary.Dispose();
                ordinary.Dispose();
                Assert.False((drain ?? exclusive!).IsCompleted);
                Assert.Throws<InvalidOperationException>(() => new WorkStateStore(temp.Path, lease, ordinary));
                if (shutdown) Assert.Throws<InvalidOperationException>(lease.Admission.CompleteShutdown);
                return true;
            });
            WorkStateStore store = await constructed.Task.WaitAsync(Deadline);
            Assert.True(File.Exists(DatabasePath(temp.Path)));
            Assert.Empty(store.LoadResources());
            if (shutdown)
            {
                await drain!.WaitAsync(Deadline);
                lease.Admission.CompleteShutdown();
            }
            else
            {
                using ProfileAdmissionToken granted = await exclusive!.WaitAsync(Deadline);
                Assert.Equal(ProfileAdmissionKind.Exclusive, granted.Kind);
            }
        }
        finally
        {
            ordinary.Dispose();
            cancel.Cancel();
            if (started)
            {
                Assert.True(worker.Join(Deadline), "Constructor worker did not leave the lease monitor.");
                await constructed.Task.WaitAsync(Deadline);
            }
            if (exclusive is not null)
            {
                try { (await exclusive.WaitAsync(Deadline)).Dispose(); }
                catch (OperationCanceledException) { }
            }
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task AllFourReadEntrypointsStayPassiveInExclusiveAndStoppedAndDoNotRecreateMissingDatabase(int entry)
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        WorkStateStore store = new(temp.Path, lease);
        WorkResource resource = store.CommitIntent(Resource(temp.Path));
        MediaWork media = Media(temp.Path);
        Assert.True(store.CommitMedia(media));
        lease.Admission.CompleteStartup();
        using (ProfileAdmissionToken exclusive = await lease.Admission.EnterExclusiveAsync())
        {
            string[] before = Inventory(temp.Path);
            AssertReadValue(store, entry, resource, media);
            Assert.Equal(before, Inventory(temp.Path));
        }
        lease.Admission.BeginShutdown();
        lease.Admission.CompleteShutdown();
        string[] stopped = Inventory(temp.Path);
        AssertReadValue(store, entry, resource, media);
        Assert.Equal(stopped, Inventory(temp.Path));
        File.Delete(DatabasePath(temp.Path)); // No pooled/retained connection may keep the file alive on Windows.
        Assert.Empty(Inventory(temp.Path));
        SqliteException error = Assert.Throws<SqliteException>(() => { ReadEntry(store, entry, media); });
        Assert.Equal(14, error.SqliteErrorCode); // SQLITE_CANTOPEN, not an empty successful result.
        Assert.False(File.Exists(DatabasePath(temp.Path)));
        Assert.Empty(Inventory(temp.Path)); // No WAL/SHM/journal/schema side effects either.
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void AllFourReadEntrypointsFailOnMissingSchemaWithoutInitializingIt(int entry)
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        WorkStateStore store = new(temp.Path, lease);
        File.Delete(DatabasePath(temp.Path));
        using (SqliteConnection db = OpenFixture(temp.Path))
        using (SqliteCommand command = db.CreateCommand())
        {
            command.CommandText = "CREATE TABLE synthetic_sentinel(value TEXT); INSERT INTO synthetic_sentinel VALUES('keep');";
            command.ExecuteNonQuery();
        }
        string[] before = Inventory(temp.Path);
        SqliteException error = Assert.Throws<SqliteException>(() => { ReadEntry(store, entry, Media(temp.Path)); });
        Assert.Equal(1, error.SqliteErrorCode); // SQLITE_ERROR: missing table, no constructor/migration fallback.
        Assert.Equal(before, Inventory(temp.Path));
        using SqliteConnection check = OpenFixture(temp.Path, SqliteOpenMode.ReadOnly);
        using SqliteCommand tables = check.CreateCommand();
        tables.CommandText = "SELECT name FROM sqlite_master WHERE type='table'";
        using SqliteDataReader reader = tables.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal("synthetic_sentinel", reader.GetString(0));
        Assert.False(reader.Read());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void ReadsNeverUpgradeLegacyVariantSchema(int entry)
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        WorkStateStore store = new(temp.Path, lease);
        File.Delete(DatabasePath(temp.Path));
        SeedLegacy(temp.Path);
        string[] before = Inventory(temp.Path);
        if (entry is 1 or 3)
            Assert.Throws<SqliteException>(() => { ReadEntry(store, entry, Media(temp.Path)); });
        else AssertReadValue(store, entry, Resource(temp.Path), Media(temp.Path));
        Assert.Equal(before, Inventory(temp.Path));
        Assert.DoesNotContain("variant", Columns(temp.Path));
    }

    private static void AssertReadValue(WorkStateStore store, int entry, WorkResource resource, MediaWork media)
    {
        object? value = ReadEntry(store, entry, media);
        switch (entry)
        {
            case 0: Assert.Equal(resource, Assert.Single(Assert.IsAssignableFrom<IReadOnlyList<WorkResource>>(value))); break;
            case 1: Assert.Equal(media, Assert.Single(Assert.IsAssignableFrom<IReadOnlyList<MediaWork>>(value))); break;
            case 2: Assert.Equal(resource, Assert.IsType<WorkResource>(value)); break;
            case 3: Assert.Equal(media, Assert.IsType<MediaWork>(value)); break;
        }
    }

    private static object? ReadEntry(WorkStateStore store, int entry, MediaWork media) => entry switch
    {
        0 => store.LoadResources(),
        1 => store.LoadMedia(Url, 1),
        2 => ((IConditionalWorkStateStore)store).FindResource(Url),
        3 => ((IConditionalWorkStateStore)store).FindMedia(MediaWorkKey.Of(media)),
        _ => throw new ArgumentOutOfRangeException(nameof(entry))
    };

    private static WorkResource Resource(string directory) => new()
    {
        Url = Url, OutputRoot = directory, Generation = 1, Intent = WorkIntent.Watching, IsThread = true
    };

    private static MediaWork Media(string directory) => new()
    {
        ResourceUrl = Url, Generation = 1, MediaIdentifier = "synthetic-media",
        DestinationPath = Path.Combine(directory, "synthetic-media.bin"), DownloadUrl = "https://stub.test/media.bin",
        Status = MediaWorkStatus.Waiting, Attempts = 2, ConsecutiveNotFound = 1, HttpStatus = 404,
        LastAttemptAt = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero),
        DueAt = new DateTimeOffset(2026, 1, 1, 12, 30, 0, TimeSpan.Zero)
    };

    private static void SeedLegacy(string directory)
    {
        MediaWork media = Media(directory);
        JsonObject payload = JsonSerializer.SerializeToNode(media, Json)!.AsObject();
        payload.Remove("variant");
        using SqliteConnection db = OpenFixture(directory);
        using SqliteCommand command = db.CreateCommand();
        command.CommandText = "CREATE TABLE work_resources(url TEXT PRIMARY KEY,payload TEXT NOT NULL); " +
            "CREATE TABLE work_media(resource_url TEXT NOT NULL,generation INTEGER NOT NULL,media_id TEXT NOT NULL," +
            "target TEXT NOT NULL,payload TEXT NOT NULL,PRIMARY KEY(resource_url,generation,media_id,target)); " +
            "INSERT INTO work_resources VALUES($url,$owner); INSERT INTO work_media VALUES($url,1,$id,$target,$payload);";
        command.Parameters.AddWithValue("$url", Url);
        command.Parameters.AddWithValue("$owner", JsonSerializer.Serialize(Resource(directory), Json));
        command.Parameters.AddWithValue("$id", media.MediaIdentifier);
        command.Parameters.AddWithValue("$target", media.DestinationPath);
        command.Parameters.AddWithValue("$payload", payload.ToJsonString());
        command.ExecuteNonQuery();
    }

    private static string[] Columns(string directory)
    {
        using SqliteConnection db = OpenFixture(directory, SqliteOpenMode.ReadOnly);
        using SqliteCommand command = db.CreateCommand();
        command.CommandText = "PRAGMA table_info(work_media)";
        using SqliteDataReader reader = command.ExecuteReader();
        List<string> result = new();
        while (reader.Read()) result.Add(reader.GetString(1));
        return result.ToArray();
    }

    private static SqliteConnection OpenFixture(string directory, SqliteOpenMode mode = SqliteOpenMode.ReadWriteCreate)
    {
        SqliteConnection db = new(new SqliteConnectionStringBuilder
        { DataSource = DatabasePath(directory), Mode = mode, Pooling = false }.ToString());
        try { db.Open(); return db; }
        catch { db.Dispose(); throw; }
    }

    private static string DatabasePath(string directory) => Path.Combine(directory, "work-state.sqlite");

    private static string[] Inventory(string directory) => Directory.EnumerateFiles(directory, "work-state.sqlite*")
        .OrderBy(path => path, StringComparer.Ordinal)
        .Select(path => Path.GetFileName(path) + ":" + Convert.ToBase64String(File.ReadAllBytes(path))).ToArray();
}
