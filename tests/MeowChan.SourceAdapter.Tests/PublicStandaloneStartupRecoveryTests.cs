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
    public async Task MissingOrViewBackedReceiptSchemaIsNotAnEmptyJournal(bool runtime, bool merge, bool readError)
    {
        using ProfileDirectory profile = new();
        await SeedProfileAsync(profile.Path, watch: true);
        SeedJournal(profile.Path, merge, "Committed", "exact");
        string receipt = Family(merge) + "_receipts";
        using (SqliteConnection connection = Open(DatabasePath(profile.Path), SqliteOpenMode.ReadWrite))
        {
            // Keep all receipt rows as evidence. The missing-table variant tests schema
            // failure. The view passes the former LIMIT 0 shape validation and its
            // SELECT still fails deterministically. Schema10 rejects this invalid
            // schema earlier; this is no longer an actual receipt-evaluation oracle.
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
        // Schema10 validates the complete receipt table before recovery. A missing
        // table and the deliberately view-backed read-error fixture are therefore
        // rejected as structural corruption, never treated as an empty journal.
        await AssertTwoBlockedStartsAsync(profile.Path, runtime, sqliteError: null,
            expectedMessage: "Invalid media transfer schema definition.", retainedReceipt: receipt);
    }

    private static async Task AssertTwoBlockedStartsAsync(string profile, bool runtime, int? sqliteError,
        string? expectedMessage = null, bool initializeRuntime = false, byte[]? corrupt = null,
        string? retainedReceipt = null)
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
                    await AssertStartupFailureAsync(start, sqliteError, expectedMessage);
                    Assert.Same(start, registry.StartAsync(lease));
                    await AssertStartupFailureAsync(registry.StartAsync(lease), sqliteError, expectedMessage);
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

    private static async Task AssertStartupFailureAsync(Task start, int? sqliteError, string? expectedMessage)
    {
        if (sqliteError.HasValue)
        {
            SqliteException error = await Assert.ThrowsAsync<SqliteException>(() => start.WaitAsync(Deadline));
            Assert.Equal(sqliteError.Value, error.SqliteErrorCode);
        }
        else
        {
            InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(() => start.WaitAsync(Deadline));
            Assert.Equal(expectedMessage ?? "Native media transfer reconciliation is required.", error.Message);
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
            string receiptColumns = LegacyJournalColumns(table + "_receipts");
            string receiptValues = merge ? "$op,$hash,1,0,1,0,0" : "$op,$hash,1,1";
            add.CommandText = $"INSERT INTO {table}_receipts({receiptColumns}) VALUES({receiptValues})";
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
            // Compare every legacy value across the additive migration, while also
            // asserting every new column is exactly its v1 default. SELECT * is retained:
            // a new/unknown column or a changed extension value cannot disappear here.
            int legacyCount = table == "archive_import_fence" ? reader.FieldCount
                : LegacyJournalColumns(table).Split(',').Length;
            (string Name, object Value)[] extensions = table == "archive_import_fence" ? []
                : LegacyExtensionDefaults(table);
            bool schema10 = reader.FieldCount != legacyCount;
            if (schema10)
            {
                Assert.Equal(legacyCount + extensions.Length, reader.FieldCount);
                for (int index = 0; index < extensions.Length; index++)
                    Assert.Equal(extensions[index].Name, reader.GetName(legacyCount + index));
            }
            List<string?[]> rows = [];
            while (reader.Read())
            {
                if (schema10)
                    for (int index = 0; index < extensions.Length; index++)
                        Assert.Equal(extensions[index].Value, reader.GetValue(legacyCount + index));
                string?[] row = new string?[legacyCount];
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
        // Remove the additive v9 family and v11 post-presence column, and rebuild
        // the four media journals with frozen v8/v9 DDL. Restamping current tables
        // without removing later schema additions is intentionally unsupported.
        using SqliteConnection connection = Open(DatabasePath(profile), SqliteOpenMode.ReadWrite);
        using SqliteTransaction transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        foreach (string table in new[] { "local_import_receipts", "local_import_operations", "local_media_revisions", "local_items", "local_collections" })
        {
            command.CommandText = $"DROP TABLE {table}";
            command.ExecuteNonQuery();
        }
        foreach (string table in JournalTables)
        {
            command.CommandText = $"ALTER TABLE {table} RENAME TO {table}_current_fixture";
            command.ExecuteNonQuery();
        }
        // Independent fixture schema: no reflection or internal production creator.
        command.CommandText = """
            CREATE TABLE archive_media_import_operations (
                operation_id TEXT NOT NULL PRIMARY KEY CHECK(length(operation_id)=36),
                request_sha256 TEXT NOT NULL CHECK(length(request_sha256)=64),
                package_fingerprint TEXT NOT NULL CHECK(length(package_fingerprint)=64),
                catalog_instance TEXT NOT NULL, catalog_generation INTEGER NOT NULL CHECK(catalog_generation>=0),
                source TEXT NOT NULL, board TEXT NOT NULL, thread TEXT NOT NULL,
                operations_parent TEXT NOT NULL UNIQUE, imported_at TEXT NOT NULL,
                state TEXT NOT NULL CHECK(state IN ('Preparing','Prepared','Committed')),
                revision_keys_json TEXT NOT NULL CHECK(length(CAST(revision_keys_json AS BLOB))<=8388608),
                candidate_directory TEXT, manifest_generation TEXT, bindings_sha256 TEXT,
                post_count INTEGER NOT NULL CHECK(post_count>=0), attachment_count INTEGER NOT NULL CHECK(attachment_count>0),
                CHECK((state='Preparing' AND candidate_directory IS NULL AND manifest_generation IS NULL AND bindings_sha256 IS NULL)
                    OR (state IN ('Prepared','Committed') AND candidate_directory IS NOT NULL
                        AND length(manifest_generation)=64 AND length(bindings_sha256)=64)));
            CREATE TABLE archive_media_import_receipts (
                operation_id TEXT NOT NULL PRIMARY KEY CHECK(length(operation_id)=36),
                request_sha256 TEXT NOT NULL CHECK(length(request_sha256)=64),
                post_count INTEGER NOT NULL CHECK(post_count>=0),
                attachment_count INTEGER NOT NULL CHECK(attachment_count>0));
            CREATE TABLE archive_media_merge_operations (
                operation_id TEXT NOT NULL PRIMARY KEY CHECK(length(operation_id)=36),
                request_sha256 TEXT NOT NULL CHECK(length(request_sha256)=64),
                package_fingerprint TEXT NOT NULL CHECK(length(package_fingerprint)=64),
                catalog_instance TEXT NOT NULL CHECK(length(catalog_instance)=36),
                catalog_generation INTEGER NOT NULL CHECK(catalog_generation>=0),
                source TEXT NOT NULL, board TEXT NOT NULL, thread TEXT NOT NULL,
                operations_parent TEXT NOT NULL UNIQUE, imported_at TEXT NOT NULL,
                state TEXT NOT NULL CHECK(state IN ('Preparing','Prepared','Committed')),
                roots_json TEXT NOT NULL CHECK(length(CAST(roots_json AS BLOB))<=8388608),
                full_keys_json TEXT NOT NULL CHECK(length(CAST(full_keys_json AS BLOB))<=8388608),
                safe_keys_json TEXT NOT NULL CHECK(length(CAST(safe_keys_json AS BLOB))<=8388608),
                catalog_bytes BLOB NOT NULL CHECK(length(catalog_bytes)<=16777216),
                full_directory TEXT, safe_directory TEXT,
                full_manifest_generation TEXT, safe_manifest_generation TEXT,
                full_bindings_sha256 TEXT, safe_bindings_sha256 TEXT,
                new_posts INTEGER NOT NULL CHECK(new_posts>=0),
                updated_posts INTEGER NOT NULL CHECK(updated_posts>=0),
                new_attachments INTEGER NOT NULL CHECK(new_attachments>=0),
                new_bindings INTEGER NOT NULL CHECK(new_bindings>=0),
                conflicts INTEGER NOT NULL CHECK(conflicts>=0),
                CHECK((state='Preparing' AND full_directory IS NULL AND safe_directory IS NULL
                    AND full_manifest_generation IS NULL AND safe_manifest_generation IS NULL
                    AND full_bindings_sha256 IS NULL AND safe_bindings_sha256 IS NULL)
                    OR (state IN ('Prepared','Committed') AND full_directory IS NOT NULL
                    AND full_manifest_generation IS NOT NULL AND length(full_manifest_generation)=64
                    AND full_bindings_sha256 IS NOT NULL AND length(full_bindings_sha256)=64
                    AND ((new_bindings=0 AND safe_directory IS NULL AND safe_manifest_generation IS NULL
                        AND safe_bindings_sha256 IS NULL)
                        OR (new_bindings>0 AND safe_directory IS NOT NULL
                        AND safe_manifest_generation IS NOT NULL AND length(safe_manifest_generation)=64
                        AND safe_bindings_sha256 IS NOT NULL AND length(safe_bindings_sha256)=64))))
            );
            CREATE TABLE archive_media_merge_receipts (
                operation_id TEXT NOT NULL PRIMARY KEY CHECK(length(operation_id)=36),
                request_sha256 TEXT NOT NULL CHECK(length(request_sha256)=64),
                new_posts INTEGER NOT NULL CHECK(new_posts>=0),
                updated_posts INTEGER NOT NULL CHECK(updated_posts>=0),
                new_attachments INTEGER NOT NULL CHECK(new_attachments>=0),
                new_bindings INTEGER NOT NULL CHECK(new_bindings>=0),
                conflicts INTEGER NOT NULL CHECK(conflicts>=0));
            """;
        command.ExecuteNonQuery();
        foreach (string table in JournalTables)
        {
            string columns = LegacyJournalColumns(table);
            command.CommandText = $"INSERT INTO {table}(rowid,{columns}) SELECT rowid,{columns} FROM {table}_current_fixture; DROP TABLE {table}_current_fixture";
            command.ExecuteNonQuery();
        }
        command.CommandText = "ALTER TABLE archive_posts DROP COLUMN attachments_were_null";
        command.ExecuteNonQuery();
        command.CommandText = "PRAGMA user_version=8";
        command.ExecuteNonQuery();
        command.CommandText = "SELECT count(*) FROM sqlite_master WHERE name LIKE 'local_%'";
        Assert.Equal(0L, (long)command.ExecuteScalar()!);
        transaction.Commit();
    }

    private static void AssertCurrentSchema(string profile)
    {
        using SqliteConnection connection = Open(DatabasePath(profile), SqliteOpenMode.ReadOnly);
        using var version = connection.CreateCommand();
        version.CommandText = "PRAGMA user_version";
        // Schema 13 added captured image dimensions; these fixtures start at 8 and migrate forward.
        Assert.Equal(13L, (long)version.ExecuteScalar()!);
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

    private static string LegacyJournalColumns(string table) => table switch
    {
        "archive_media_import_operations" => "operation_id,request_sha256,package_fingerprint,catalog_instance,catalog_generation,source,board,thread,operations_parent,imported_at,state,revision_keys_json,candidate_directory,manifest_generation,bindings_sha256,post_count,attachment_count",
        "archive_media_import_receipts" => "operation_id,request_sha256,post_count,attachment_count",
        "archive_media_merge_operations" => "operation_id,request_sha256,package_fingerprint,catalog_instance,catalog_generation,source,board,thread,operations_parent,imported_at,state,roots_json,full_keys_json,safe_keys_json,catalog_bytes,full_directory,safe_directory,full_manifest_generation,safe_manifest_generation,full_bindings_sha256,safe_bindings_sha256,new_posts,updated_posts,new_attachments,new_bindings,conflicts",
        "archive_media_merge_receipts" => "operation_id,request_sha256,new_posts,updated_posts,new_attachments,new_bindings,conflicts",
        _ => throw new InvalidOperationException("Not a fixture journal table.")
    };

    private static (string Name, object Value)[] LegacyExtensionDefaults(string table)
    {
        bool import = table.StartsWith("archive_media_import_", StringComparison.Ordinal);
        List<(string Name, object Value)> columns =
        [
            ("plan_version", 1L), ("evidence_additions", 0L),
            ("derived_descriptor_additions", 0L), ("contained_derived_additions", 0L)
        ];
        if (import) columns.Add(("original_binding_count", 0L));
        if (table.EndsWith("_operations", StringComparison.Ordinal))
        {
            columns.AddRange(new (string, object)[]
            {
                ("full_selection_json", DBNull.Value), ("full_selection_sha256", DBNull.Value),
                ("full_inventory_sha256", DBNull.Value), ("full_member_count", 0L)
            });
            if (import) columns.Add(("catalog_bytes", DBNull.Value));
            else columns.AddRange(new (string, object)[]
            {
                ("safe_selection_json", DBNull.Value), ("safe_selection_sha256", DBNull.Value),
                ("safe_inventory_sha256", DBNull.Value), ("safe_member_count", 0L)
            });
        }
        return columns.ToArray();
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
