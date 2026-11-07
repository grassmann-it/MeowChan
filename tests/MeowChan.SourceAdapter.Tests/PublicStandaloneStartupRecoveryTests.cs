using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using MeowChan.Archive;
using MeowChan.Downloader;
using MeowChan.Interfaces;
using MeowChan.Persistence;
using Microsoft.Data.Sqlite;

namespace MeowChan.SourceAdapter.Tests;

// External-consumer acceptance: only public lifecycle/store APIs. SQL below is
// synthetic on-disk fixture data, never admission state or a recovery implementation.
public sealed class PublicStandaloneStartupRecoveryTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(45);
    private const string Operation = "01234567-89ab-cdef-0123-456789abcdef";
    private const string WatchUrl = "https://recovery.invalid/b/thread/1";
    private static readonly string Hash = new('a', 64);
    private static readonly string[] JournalTables =
    [
        "archive_media_import_operations", "archive_media_import_receipts",
        "archive_media_merge_operations", "archive_media_merge_receipts"
    ];

    public static IEnumerable<object[]> BlockedJournals()
    {
        foreach (bool runtime in new[] { false, true })
        foreach (bool merge in new[] { false, true })
        {
            foreach (string state in new[] { "Preparing", "Prepared" })
            foreach (string receipt in new[] { "none", "exact" })
                yield return [runtime, merge, state, receipt];
            foreach (string receipt in new[] { "none", "request_sha256", "operation_id" })
                yield return [runtime, merge, "Committed", receipt];
            foreach (string count in merge
                ? new[] { "new_posts", "updated_posts", "new_attachments", "new_bindings", "conflicts" }
                : new[] { "post_count", "attachment_count" })
                yield return [runtime, merge, "Committed", count];
        }
    }

    [Theory]
    [MemberData(nameof(BlockedJournals))]
    public async Task UnresolvedJournalBlocksBeforeRestoreAndSurvivesLeaseReacquisition(
        bool runtime, bool merge, string state, string receipt)
    {
        using ProfileDirectory profile = new();
        await SeedProfileAsync(profile.Path, watch: true);
        SeedJournal(profile.Path, merge, state, receipt);
        // Same operation identity, healthy opposite family: neither its receipt nor
        // its committed state can mask the unresolved family.
        SeedJournal(profile.Path, !merge, "Committed", "exact");
        await AssertTwoBlockedStartsAsync(profile.Path, runtime, sqliteError: null);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AlreadyInitializedRuntimeStillChecksRecoveryBeforeRestore(bool merge)
    {
        using ProfileDirectory profile = new();
        await SeedProfileAsync(profile.Path, watch: true);
        SeedJournal(profile.Path, merge, "Prepared", "exact");
        await AssertTwoBlockedStartsAsync(profile.Path, runtime: true, sqliteError: null, initializeRuntime: true);
    }

    [Theory]
    [InlineData(false, "empty")]
    [InlineData(true, "empty")]
    [InlineData(false, "import")]
    [InlineData(true, "import")]
    [InlineData(false, "merge")]
    [InlineData(true, "merge")]
    public async Task EmptyOrExactlyReceiptedJournalAllowsPublicStartupWithoutReconciliation(bool runtime, string journal)
    {
        using ProfileDirectory profile = new();
        await SeedProfileAsync(profile.Path, watch: false);
        if (journal != "empty") SeedJournal(profile.Path, journal == "merge", "Committed", "exact");
        string before = ReadArchiveEvidence(profile.Path);
        string[] candidates = CandidateInventory(profile.Path);
        byte[] session = File.ReadAllBytes(SessionPath(profile.Path));
        await AssertClearStartAsync(profile.Path, runtime);
        Assert.Equal(before, ReadArchiveEvidence(profile.Path));
        Assert.Equal(candidates, CandidateInventory(profile.Path));
        Assert.Equal(session, File.ReadAllBytes(SessionPath(profile.Path)));
    }

    [Fact]
    public async Task WorkStoreOnlyCreatesRecoveryArchiveWithoutAttachingContent()
    {
        using ProfileDirectory profile = new();
        await SeedProfileAsync(profile.Path, watch: false, createArchive: false);
        Assert.False(File.Exists(DatabasePath(profile.Path)));
        await AssertClearStartAsync(profile.Path, runtime: false);
        Assert.True(File.Exists(DatabasePath(profile.Path)));
        AssertCurrentSchema(profile.Path);
        ReadArchiveEvidence(profile.Path); // All journals/content remain empty.
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WorkStoreOnlyMigratesBeforeCheckingBothTransferFamilies(bool blockedMerge)
    {
        using ProfileDirectory profile = new();
        await SeedProfileAsync(profile.Path, watch: true);
        SeedJournal(profile.Path, blockedMerge, "Prepared", "none");
        MakeSyntheticVersionEight(profile.Path);
        await AssertTwoBlockedStartsAsync(profile.Path, runtime: false, sqliteError: null);
        AssertCurrentSchema(profile.Path); // Migration is allowed; journal/candidate cleanup is not.
    }

    [Fact]
    public async Task WorkStoreOnlyMigratesEmptyVersionEightAndStarts()
    {
        using ProfileDirectory profile = new();
        await SeedProfileAsync(profile.Path, watch: false);
        MakeSyntheticVersionEight(profile.Path);
        string before = ReadArchiveEvidence(profile.Path);
        await AssertClearStartAsync(profile.Path, runtime: false);
        AssertCurrentSchema(profile.Path);
        Assert.Equal(before, ReadArchiveEvidence(profile.Path));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CorruptArchiveIsNotAnAbsentArchiveAndIsRetained(bool runtime)
    {
        using ProfileDirectory profile = new();
        await SeedProfileAsync(profile.Path, watch: true, createArchive: false);
        WriteCandidate(profile.Path, merge: false);
        byte[] corrupt = System.Text.Encoding.UTF8.GetBytes("Synthetic corrupt archive; do not replace with an empty database.");
        File.WriteAllBytes(DatabasePath(profile.Path), corrupt);
        await AssertTwoBlockedStartsAsync(profile.Path, runtime, sqliteError: 26, corrupt: corrupt);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    public async Task MissingSchemaOrReceiptReadErrorIsNotAnEmptyJournal(bool runtime, bool merge, bool readError)
    {
        using ProfileDirectory profile = new();
        await SeedProfileAsync(profile.Path, watch: true);
        SeedJournal(profile.Path, merge, "Committed", "exact");
        string receipt = Family(merge) + "_receipts";
        using (SqliteConnection connection = Open(DatabasePath(profile.Path), SqliteOpenMode.ReadWrite))
        {
            // Keep all receipt rows as evidence. The missing-table variant tests schema
            // failure. The view variant passes LIMIT 0 shape validation but fails on
            // actual receipt evaluation, deterministically and without permissions tricks.
            Execute(connection, $"ALTER TABLE {receipt} RENAME TO retained_receipts");
            if (readError)
            {
                Execute(connection, $"CREATE VIEW {receipt} AS SELECT * FROM retained_receipts WHERE abs(-9223372036854775808) > 0");
                using var shape = connection.CreateCommand();
                shape.CommandText = $"SELECT * FROM {receipt} LIMIT 0";
                using (var rows = shape.ExecuteReader()) Assert.False(rows.Read());
                Assert.Throws<SqliteException>(() => Execute(connection, $"SELECT * FROM {receipt}"));
            }
        }
        await AssertTwoBlockedStartsAsync(profile.Path, runtime, sqliteError: 1, retainedReceipt: receipt);
    }

    private static async Task AssertTwoBlockedStartsAsync(string profile, bool runtime, int? sqliteError,
        bool initializeRuntime = false, byte[]? corrupt = null, string? retainedReceipt = null)
    {
        string? archive = corrupt == null ? ReadArchiveEvidence(profile, retainedReceipt) : null;
        string[] candidates = CandidateInventory(profile);
        byte[] session = File.ReadAllBytes(SessionPath(profile));
        for (int restart = 0; restart < 2; restart++)
        {
            using (ProfileLease lease = ProfileLease.Acquire(profile))
            {
                await using ThreadContentRuntime? content = runtime ? new(profile, lease) : null;
                if (initializeRuntime) await content!.InitializeAsync().WaitAsync(Deadline);
                ObservedStore store = new(new SessionStore(profile, lease));
                EffectSentinel provider = new();
                DownloadQueue queue = new(1, 1048576); // Unpaused: recovery, not user pause, is the barrier.
                WorkStateStore work = new(profile, lease);
                WorkResource[] workBefore = work.LoadResources().ToArray();
                await using (Registry registry = new(new[] { provider }, queue, Path.Combine(profile, "media"),
                    TimeSpan.FromHours(1), 3600, sessionStore: store, contentRuntime: content, workStore: work))
                {
                    Assert.Equal(0, store.Loads);
                    Task start = registry.StartAsync(lease);
                    await AssertStartupFailureAsync(start, sqliteError);
                    Assert.Same(start, registry.StartAsync(lease));
                    await AssertStartupFailureAsync(registry.StartAsync(lease), sqliteError);
                    // Public negative oracles, not private phase observations. A failed
                    // coordinator must not authorize either explicit restore or new work.
                    await Assert.ThrowsAnyAsync<InvalidOperationException>(() => registry.AddAsync(WatchUrl).WaitAsync(Deadline));
                    await Assert.ThrowsAnyAsync<InvalidOperationException>(() => registry.RestoreAsync().WaitAsync(Deadline));
                    AssertNoRestoreOrProviderEffects(registry, store, provider, queue);
                    AssertRetained();
                    await registry.StopAsync().WaitAsync(Deadline);
                    AssertNoRestoreOrProviderEffects(registry, store, provider, queue);
                }
                Assert.Equal(workBefore, work.LoadResources().ToArray());
                Assert.Equal(0, store.Loads);
                Assert.Equal(0, store.Saves);
                Assert.Equal(0, provider.Effects);
            }
            // Include Registry/runtime/lease disposal in the no-repair oracle, then
            // acquire a genuinely new lease and coordinator over the unchanged evidence.
            AssertRetained();
        }

        void AssertRetained()
        {
            if (corrupt != null) Assert.Equal(corrupt, File.ReadAllBytes(DatabasePath(profile)));
            else Assert.Equal(archive, ReadArchiveEvidence(profile, retainedReceipt));
            Assert.Equal(candidates, CandidateInventory(profile));
            Assert.Equal(session, File.ReadAllBytes(SessionPath(profile)));
            Assert.False(Directory.Exists(Path.Combine(profile, "media")));
        }
    }

    private static async Task AssertStartupFailureAsync(Task start, int? sqliteError)
    {
        if (sqliteError.HasValue)
        {
            SqliteException error = await Assert.ThrowsAsync<SqliteException>(() => start.WaitAsync(Deadline));
            Assert.Equal(sqliteError.Value, error.SqliteErrorCode);
        }
        else
        {
            InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(() => start.WaitAsync(Deadline));
            Assert.Equal("Native media transfer reconciliation is required.", error.Message);
        }
    }

    private static void AssertNoRestoreOrProviderEffects(Registry registry, ObservedStore store,
        EffectSentinel provider, DownloadQueue queue)
    {
        Assert.Equal(0, store.Loads);
        Assert.Equal(0, store.Saves);
        Assert.Equal(0, provider.Effects);
        Assert.Empty(registry.Threads);
        Assert.Empty(registry.Boards);
        Assert.Equal(0L, registry.ReceivedMediaBytes);
        Assert.Equal((0, 0), queue.GetMetadataCapacitySnapshot());
    }

    private static async Task AssertClearStartAsync(string profile, bool runtime)
    {
        using ProfileLease lease = ProfileLease.Acquire(profile);
        await using ThreadContentRuntime? content = runtime ? new(profile, lease) : null;
        ObservedStore store = new(new SessionStore(profile, lease));
        EffectSentinel provider = new();
        DownloadQueue queue = new(1, 1048576);
        await using Registry registry = new(new[] { provider }, queue, Path.Combine(profile, "media"),
            TimeSpan.FromHours(1), 3600, sessionStore: store, contentRuntime: content,
            workStore: new WorkStateStore(profile, lease));
        await registry.StartAsync(lease).WaitAsync(Deadline);
        Assert.Equal(1, store.Loads); // Not merely a completed no-op startup task.
        Assert.Equal(0, store.Saves);
        Assert.Empty(registry.Threads);
        Assert.Empty(registry.Boards);
        Assert.Equal(0, provider.Effects);
        if (content != null) Assert.True(content.IsInitialized);
        else Assert.All(registry.GetProviderDescriptors(), descriptor => Assert.False(descriptor.CaptureAvailable));
        await registry.StopAsync().WaitAsync(Deadline);
    }

    private static async Task SeedProfileAsync(string profile, bool watch, bool createArchive = true)
    {
        using ProfileLease lease = ProfileLease.Acquire(profile);
        await new SessionStore(profile, lease).SaveAsync(new SessionSnapshot
        { ThreadUrls = watch ? [WatchUrl] : [] }).WaitAsync(Deadline);
        WorkStateStore work = new(profile, lease);
        if (watch) work.CommitIntent(new WorkResource
        {
            Url = WatchUrl, OutputRoot = Path.Combine(profile, "media"), Generation = 1,
            IsThread = true, Intent = WorkIntent.Watching
        });
        if (createArchive)
        {
            await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(profile, lease).WaitAsync(Deadline);
        }
        // All public setup and disposal finishes before the raw fixture transaction.
    }

    private static void SeedJournal(string profile, bool merge, string state, string receipt)
    {
        string child = WriteCandidate(profile, merge);
        using SqliteConnection connection = Open(DatabasePath(profile), SqliteOpenMode.ReadWrite);
        using SqliteTransaction transaction = connection.BeginTransaction();
        string table = Family(merge);
        string columns = merge
            ? "roots_json,full_keys_json,safe_keys_json,catalog_bytes,full_directory,full_manifest_generation,full_bindings_sha256,new_posts,updated_posts,new_attachments,new_bindings,conflicts"
            : "revision_keys_json,candidate_directory,manifest_generation,bindings_sha256,post_count,attachment_count";
        string values = merge ? "'[]','[]','[]',X'7B7D',$dir,$generation,$generation,1,0,1,0,0"
            : "'[]',$dir,$generation,$generation,1,1";
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            INSERT INTO {table}_operations(operation_id,request_sha256,package_fingerprint,catalog_instance,
                catalog_generation,source,board,thread,operations_parent,imported_at,state,{columns})
            VALUES($op,$hash,$hash,$op,0,'fixture','b','1',$parent,'2026-11-06',$state,{values});
            """;
        command.Parameters.AddWithValue("$op", Operation);
        command.Parameters.AddWithValue("$hash", Hash);
        command.Parameters.AddWithValue("$parent", Path.GetDirectoryName(child)!);
        command.Parameters.AddWithValue("$state", state);
        command.Parameters.AddWithValue("$dir", state == "Preparing" ? DBNull.Value : child);
        command.Parameters.AddWithValue("$generation", state == "Preparing" ? DBNull.Value : Hash);
        Assert.Equal(1, command.ExecuteNonQuery());
        if (receipt != "none")
        {
            using var add = connection.CreateCommand();
            add.Transaction = transaction;
            add.CommandText = $"INSERT INTO {table}_receipts VALUES($op,$hash," + (merge ? "1,0,1,0,0)" : "1,1)");
            add.Parameters.AddWithValue("$op", Operation);
            add.Parameters.AddWithValue("$hash", Hash);
            Assert.Equal(1, add.ExecuteNonQuery());
            if (receipt != "exact")
            {
                // Identifiers are drawn exclusively from this fixed fixture whitelist.
                Assert.Contains(receipt, merge
                    ? new[] { "operation_id", "request_sha256", "new_posts", "updated_posts", "new_attachments", "new_bindings", "conflicts" }
                    : new[] { "operation_id", "request_sha256", "post_count", "attachment_count" });
                using var change = connection.CreateCommand();
                change.Transaction = transaction;
                change.CommandText = $"UPDATE {table}_receipts SET {receipt}=$value";
                change.Parameters.AddWithValue("$value", receipt switch
                {
                    "operation_id" => "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee",
                    "request_sha256" => new string('b', 64),
                    _ => (object)99
                });
                Assert.Equal(1, change.ExecuteNonQuery());
            }
        }
        transaction.Commit();
    }

    private static string WriteCandidate(string profile, bool merge)
    {
        string child = Path.Combine(profile, "import-media-operations", merge ? "merge" : "import", "synthetic-incomplete-child");
        Directory.CreateDirectory(child);
        File.WriteAllText(Path.Combine(child, ".meowchan-import-candidate.json"), "synthetic invalid journal: never repair");
        File.WriteAllBytes(Path.Combine(child, "retained-original.bin"), [0, 1, 2, 255]);
        return child;
    }

    private static string ReadArchiveEvidence(string profile, string? retainedReceipt = null)
    {
        using SqliteConnection connection = Open(DatabasePath(profile), SqliteOpenMode.ReadOnly);
        using var transaction = connection.BeginTransaction(deferred: true);
        List<string> evidence = [];
        foreach (string table in JournalTables.Append("archive_import_fence"))
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"SELECT * FROM {(table == retainedReceipt ? "retained_receipts" : table)} ORDER BY 1";
            using var reader = command.ExecuteReader();
            List<string?[]> rows = [];
            while (reader.Read())
            {
                string?[] row = new string?[reader.FieldCount];
                for (int index = 0; index < row.Length; index++)
                    row[index] = reader.IsDBNull(index) ? null : reader.GetValue(index) is byte[] bytes
                        ? Convert.ToHexString(bytes) : Convert.ToString(reader.GetValue(index), CultureInfo.InvariantCulture);
                rows.Add(row);
            }
            evidence.Add(table + ":" + JsonSerializer.Serialize(rows));
        }
        if (retainedReceipt != null)
        {
            using var schema = connection.CreateCommand();
            schema.Transaction = transaction;
            schema.CommandText = "SELECT type,name,sql FROM sqlite_master WHERE name=$receipt OR name='retained_receipts' ORDER BY name";
            schema.Parameters.AddWithValue("$receipt", retainedReceipt);
            using var rows = schema.ExecuteReader();
            while (rows.Read()) evidence.Add($"{rows.GetString(0)}:{rows.GetString(1)}:{rows.GetString(2)}");
        }
        foreach (string table in new[] { "archive_threads", "archive_posts", "archive_attachments", "archive_media_directories" })
        {
            using var count = connection.CreateCommand();
            count.Transaction = transaction;
            count.CommandText = $"SELECT count(*) FROM {table}";
            Assert.Equal(0L, (long)count.ExecuteScalar()!);
        }
        transaction.Commit();
        return string.Join("\n", evidence);
    }

    private static void MakeSyntheticVersionEight(string profile)
    {
        // Synthetic supported schema shape, NOT an authentic historical release profile.
        // Remove the complete additive v9 family (including its table-owned triggers).
        using SqliteConnection connection = Open(DatabasePath(profile), SqliteOpenMode.ReadWrite);
        foreach (string table in new[] { "local_import_receipts", "local_import_operations", "local_media_revisions", "local_items", "local_collections" })
            Execute(connection, $"DROP TABLE {table}");
        Execute(connection, "PRAGMA user_version=8");
        using var check = connection.CreateCommand();
        check.CommandText = "SELECT count(*) FROM sqlite_master WHERE name LIKE 'local_%'";
        Assert.Equal(0L, (long)check.ExecuteScalar()!);
    }

    private static void AssertCurrentSchema(string profile)
    {
        using SqliteConnection connection = Open(DatabasePath(profile), SqliteOpenMode.ReadOnly);
        using var version = connection.CreateCommand();
        version.CommandText = "PRAGMA user_version";
        Assert.Equal(9L, (long)version.ExecuteScalar()!);
        version.CommandText = "SELECT count(*) FROM sqlite_master WHERE type='table' AND name LIKE 'local_%'";
        Assert.Equal(5L, (long)version.ExecuteScalar()!);
    }

    private static SqliteConnection Open(string database, SqliteOpenMode mode)
    {
        SqliteConnection connection = new(new SqliteConnectionStringBuilder
        { DataSource = database, Mode = mode, Pooling = false }.ToString());
        try { connection.Open(); return connection; }
        catch { connection.Dispose(); throw; }
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var rows = command.ExecuteReader();
        do { while (rows.Read()) { } } while (rows.NextResult());
    }

    private static string Family(bool merge) => merge ? "archive_media_merge" : "archive_media_import";
    private static string DatabasePath(string profile) => Path.Combine(profile, "archive.db");
    private static string SessionPath(string profile) => Path.Combine(profile, "saved_urls.json");
    private static string[] CandidateInventory(string profile)
    {
        string root = Path.Combine(profile, "import-media-operations");
        return !Directory.Exists(root) ? [] : Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal).Select(path => Path.GetRelativePath(root, path) +
                (Directory.Exists(path) ? "/" : ":" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))))).ToArray();
    }

    private sealed class ObservedStore(SessionStore inner) : ISessionStore
    {
        internal int Loads, Saves;
        public Task<SessionLoadResult> LoadAsync(CancellationToken cancellationToken = default)
        { Interlocked.Increment(ref Loads); return inner.LoadAsync(cancellationToken); }
        // Like the Desktop recovery fixture, count shutdown projections without
        // overwriting the input session. Loads still delegate to the real public store.
        public Task SaveAsync(SessionSnapshot snapshot, CancellationToken cancellationToken = default)
        { Interlocked.Increment(ref Saves); return Task.CompletedTask; }
    }

    private sealed class EffectSentinel : IImageBoard
    {
        internal int Effects;
        public string Name => "recovery-fixture";
        public string NiceName => "Offline recovery fixture";
        public Uri BaseUri => new("https://recovery.invalid/");
        public bool CanHandle(Uri uri) { Interlocked.Increment(ref Effects); return true; }
        public bool IsThreadUri(Uri uri) { Interlocked.Increment(ref Effects); return true; }
        public bool IsBoardUri(Uri uri) { Interlocked.Increment(ref Effects); return false; }
        public IBoard GetBoard(Uri uri) => throw UnexpectedEffect();
        public IThread GetThread(Uri uri) => throw UnexpectedEffect();
        public HttpClient GetHttpClient() => throw UnexpectedEffect();
        private InvalidOperationException UnexpectedEffect()
        {
            Interlocked.Increment(ref Effects);
            return new InvalidOperationException("Recovery must finish before provider construction, monitors or physical dispatch.");
        }
    }

    private sealed class ProfileDirectory : IDisposable
    {
        internal string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "meowchan-public-recovery-" + Guid.NewGuid().ToString("N"));
        internal ProfileDirectory() => Directory.CreateDirectory(Path);
        // Test-only teardown, after all retention/reacquisition assertions and owner disposal.
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
