using MeowChan.Archive;
using MeowChan.Persistence;
using MeowChan.Tests.Transfer;
using Microsoft.Data.Sqlite;

namespace MeowChan.Tests;

// Synthetic SQLite metadata only. No local import producer, media store, host or network.
public sealed class LocalCollectionMigrationTests
{
    private const string Collection = "11111111-1111-1111-1111-111111111111";
    private const string Item = "22222222-2222-2222-2222-222222222222";
    private const string Revision = "33333333-3333-3333-3333-333333333333";
    private const string Operation = "44444444-4444-4444-4444-444444444444";
    private static readonly string Hash = new('a', 64);
    private const string EmptyJson = "{}";

    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    [InlineData(5)] [InlineData(6)] [InlineData(7)] [InlineData(8)] [InlineData(9)]
    public async Task EveryLegacyVersionIsPassiveThenMigratesCompletely(int version)
    {
        using Workspace files = new();
        SeedLegacy(files.Database, version);
        byte[] before = File.ReadAllBytes(files.Database);
        await using (ArchiveReader reader = await ArchiveReader.OpenAsync(files.Directory))
            Assert.Single((await reader.ListThreadsAsync()).Items);
        Assert.Equal(before, File.ReadAllBytes(files.Database));
        (string Instance, long Generation)? fence = null;
        string[] oldTriggers;
        using (var connection = ArchiveSql.OpenConnection(files.Database, SqliteOpenMode.ReadOnly))
        {
            Assert.Equal(version, ArchiveSql.GetPragma(connection, "user_version"));
            if (version >= 6) fence = ArchiveSql.ReadImportFence(connection, null);
            oldTriggers = TriggerDefinitions(connection);
            if (version < 9)
                Assert.Equal(0L, Scalar(connection, "SELECT count(*) FROM sqlite_master WHERE name LIKE 'local_%'"));
            else
                Assert.Equal(5L, Scalar(connection, "SELECT count(*) FROM sqlite_master WHERE type='table' AND name LIKE 'local_%'"));
        }
        using ProfileLease lease = ProfileLease.Acquire(files.Directory);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(files.Directory, lease);
        using var migrated = ArchiveSql.OpenConnection(files.Database, SqliteOpenMode.ReadOnly);
        using var tx = migrated.BeginTransaction(deferred: true);
        ArchiveSql.ValidateSchema(migrated, tx);
        Assert.Equal(10, ArchiveSql.GetPragma(migrated, "user_version", tx));
        Assert.Equal(5L, Scalar(migrated, "SELECT count(*) FROM sqlite_master WHERE type='table' AND name LIKE 'local_%'", tx));
        AssertFenceNames(migrated, tx);
        if (fence.HasValue) Assert.Equal(fence.Value, ArchiveSql.ReadImportFence(migrated, tx));
        if (version >= 6) Assert.Equal(oldTriggers, TriggerDefinitions(migrated, tx));
        Assert.Equal("sentinel", Text(migrated, "SELECT title FROM archive_threads", tx));
    }

    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    [InlineData(5)] [InlineData(6)] [InlineData(7)] [InlineData(8)] [InlineData(9)]
    public void LegacyReadsUseTheExactCallerTransaction(int version)
    {
        using Workspace files = new();
        SeedLegacy(files.Database, version);
        using var connection = ArchiveSql.OpenConnection(files.Database, SqliteOpenMode.ReadOnly);
        using var tx = connection.BeginTransaction(deferred: true);
        ArchiveSql.ValidateSchema(connection, tx);
        var id = new ResourceId("synthetic", "b", "1");
        Assert.Equal("sentinel", ArchiveSql.ReadThread(connection, tx, id)!.Summary.Title);
        Assert.Empty(ArchiveCatalog.ReadDecisions(connection, tx, id));
        Assert.Single(ArchiveSql.SearchPage(connection, tx, ArchiveSearchRequest.Create(new("sentinel")),
            0, 10, CancellationToken.None).Items);
        Assert.Equal(version, ArchiveSql.GetPragma(connection, "user_version", tx));
    }

    [Fact]
    public async Task NewDatabaseAndCurrentSchemaReopenWithoutReseeding()
    {
        using Workspace files = new();
        using ProfileLease lease = ProfileLease.Acquire(files.Directory);
        await using (var catalog = await ArchiveCatalog.OpenAsync(files.Directory, lease)) { }
        (string Instance, long Generation) fence;
        using (var connection = ArchiveSql.OpenConnection(files.Database, SqliteOpenMode.ReadWrite))
        {
            Execute(connection, "UPDATE archive_import_fence SET generation=123");
            fence = ArchiveSql.ReadImportFence(connection, null);
        }
        byte[] before = File.ReadAllBytes(files.Database);
        await using (var catalog = await ArchiveCatalog.OpenAsync(files.Directory, lease)) { }
        Assert.Equal(before, File.ReadAllBytes(files.Database));
        using var read = ArchiveSql.OpenConnection(files.Database, SqliteOpenMode.ReadOnly);
        Assert.Equal(fence, ArchiveSql.ReadImportFence(read, null));
        Assert.Equal(10, ArchiveSql.GetPragma(read, "user_version"));
    }

    [Theory]
    [InlineData("DROP TRIGGER import_fence_local_items_INSERT")]
    [InlineData("DROP TRIGGER import_fence_local_items_INSERT; CREATE TRIGGER import_fence_local_items_INSERT AFTER INSERT ON local_items BEGIN SELECT 1; END")]
    [InlineData("DROP TABLE local_import_receipts; CREATE TABLE local_import_receipts(operation_id TEXT,request_sha256 TEXT,catalog_instance TEXT,collection_id TEXT,receipt_json TEXT)")]
    [InlineData("DELETE FROM archive_import_fence")]
    [InlineData("CREATE UNIQUE INDEX forbidden_hash_merge ON local_media_revisions(sha256)")]
    [InlineData("CREATE TRIGGER surprise AFTER INSERT ON local_import_operations BEGIN UPDATE archive_import_fence SET generation=generation+1; END")]
    public async Task DamagedNineIsRejectedWithoutRepair(string corruption)
    {
        using Workspace files = new(); SeedLegacy(files.Database, 9);
        using ProfileLease lease = ProfileLease.Acquire(files.Directory);
        using (var connection = ArchiveSql.OpenConnection(files.Database, SqliteOpenMode.ReadWrite)) Execute(connection, corruption);
        byte[] before = File.ReadAllBytes(files.Database);
        await Assert.ThrowsAsync<InvalidDataException>(() => ArchiveCatalog.OpenAsync(files.Directory, lease));
        await Assert.ThrowsAsync<InvalidDataException>(() => ArchiveReader.OpenAsync(files.Directory));
        Assert.Equal(before, File.ReadAllBytes(files.Database));
    }

    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    [InlineData(5)] [InlineData(6)] [InlineData(7)] [InlineData(8)]
    public async Task CollidingLegacyTableRollsBackActualMigration(int version)
    {
        using Workspace files = new(); SeedLegacy(files.Database, version);
        using (var connection = ArchiveSql.OpenConnection(files.Database, SqliteOpenMode.ReadWrite))
            Execute(connection, "CREATE TABLE local_items(wrong TEXT)");
        byte[] before = File.ReadAllBytes(files.Database);
        using ProfileLease lease = ProfileLease.Acquire(files.Directory);
        await Assert.ThrowsAsync<InvalidDataException>(() => ArchiveCatalog.OpenAsync(files.Directory, lease));
        Assert.Equal(before, File.ReadAllBytes(files.Database));
        await using var reader = await ArchiveReader.OpenAsync(files.Directory);
        Assert.Single((await reader.ListThreadsAsync()).Items);
    }

    [Theory]
    [InlineData(0, false)] [InlineData(1, false)] [InlineData(2, false)]
    [InlineData(0, true)] [InlineData(1, true)] [InlineData(2, true)]
    public void DdlValidationAndStampAreRollbackable(int checkpoint, bool cancel)
    {
        using Workspace files = new(); SeedLegacy(files.Database, 8);
        byte[] before = File.ReadAllBytes(files.Database);
        using (var connection = ArchiveSql.OpenConnection(files.Database, SqliteOpenMode.ReadWrite))
        {
            // Injection harness exercises the same SQL helpers/transaction, without a product fault hook.
            Action inject = () =>
            {
                using var tx = connection.BeginTransaction(deferred: false);
                ArchiveSql.CreateLocalCollectionSchema(connection, tx);
                if (checkpoint >= 1) ArchiveSql.ValidateTargetSchema(connection, tx, 9);
                if (checkpoint >= 2) Execute(connection, "PRAGMA user_version=9", tx);
                if (cancel) new CancellationToken(true).ThrowIfCancellationRequested();
                throw new IOException("synthetic migration fault");
            };
            if (cancel) Assert.Throws<OperationCanceledException>(inject);
            else Assert.Throws<IOException>(inject);
        }
        Assert.Equal(before, File.ReadAllBytes(files.Database));
        using var read = ArchiveSql.OpenConnection(files.Database, SqliteOpenMode.ReadOnly);
        Assert.Equal(8, ArchiveSql.GetPragma(read, "user_version"));
        ArchiveSql.ValidateSchema(read);
        Assert.Equal(0L, Scalar(read, "SELECT count(*) FROM sqlite_master WHERE name LIKE 'local_%'"));
    }

    [Fact]
    public void TargetNineMustBeCheckedBeforeOldStampIsAdvanced()
    {
        using Workspace files = new(); SeedLegacy(files.Database, 8);
        using var connection = ArchiveSql.OpenConnection(files.Database, SqliteOpenMode.ReadWrite);
        using var tx = connection.BeginTransaction();
        ArchiveSql.CreateLocalCollectionSchema(connection, tx);
        Assert.Equal(8, ArchiveSql.GetPragma(connection, "user_version", tx));
        ArchiveSql.ValidateTargetSchema(connection, tx, 9);
        Execute(connection, "DROP TRIGGER import_fence_local_collections_DELETE", tx);
        ArchiveSql.ValidateSchema(connection, tx); // v8 deliberately does not inspect local schema.
        Assert.Throws<InvalidDataException>(() => ArchiveSql.ValidateTargetSchema(connection, tx, 9));
    }

    [Fact]
    public async Task CurrentContentTriggersAndInventoryIsolationAndOverflow()
    {
        using Workspace files = new(); using ProfileLease lease = ProfileLease.Acquire(files.Directory);
        await using var catalog = await ArchiveCatalog.OpenAsync(files.Directory, lease);
        using var connection = ArchiveSql.OpenConnection(files.Database, SqliteOpenMode.ReadWrite);
        long generation = ArchiveSql.ReadImportFence(connection, null).Generation;
        Execute(connection, $"""
            INSERT INTO local_import_operations(operation_id,collection_id,catalog_instance,expected_generation,attempt,state,created_at,selection_json,item_count,payload_limit,metadata_reservation)
            VALUES('{Operation}','{Collection}','{Collection}',0,1,'Capturing','synthetic','{EmptyJson}',1,100,100);
            UPDATE local_import_operations SET attempt=2;
            INSERT INTO local_import_receipts VALUES('{Operation}','{Hash}','{Collection}','{Collection}','{EmptyJson}');
            UPDATE local_import_receipts SET receipt_json='{EmptyJson}';
            DELETE FROM local_import_receipts; DELETE FROM local_import_operations;
            """);
        Assert.Equal(generation, ArchiveSql.ReadImportFence(connection, null).Generation);
        string[] mutations =
        [
            $"INSERT INTO local_collections VALUES('{Collection}','synthetic',1)",
            $"INSERT INTO local_items VALUES('{Collection}','{Item}',0,NULL)",
            $"INSERT INTO local_media_revisions VALUES('{Collection}','{Item}','{Revision}',1,'{Hash}',0,'synthetic.original.bin',NULL)",
            "UPDATE local_collections SET title='updated'", "UPDATE local_items SET ordinal=1",
            "UPDATE local_media_revisions SET display_name='updated'",
            "DELETE FROM local_media_revisions", "DELETE FROM local_items", "DELETE FROM local_collections"
        ];
        foreach (string sql in mutations)
        {
            Execute(connection, sql);
            Assert.Equal(++generation, ArchiveSql.ReadImportFence(connection, null).Generation);
        }
        Execute(connection, "UPDATE archive_import_fence SET generation=9223372036854775807");
        Assert.Throws<SqliteException>(() => Execute(connection, mutations[0]));
        Assert.Equal(0L, Scalar(connection, "SELECT count(*) FROM local_collections"));
        Assert.Equal(long.MaxValue, ArchiveSql.ReadImportFence(connection, null).Generation);
    }

    [Fact]
    public async Task LocalConstraintsRejectMalformedRowsAndAllowDuplicateHashes()
    {
        using Workspace files = new(); using ProfileLease lease = ProfileLease.Acquire(files.Directory);
        await using var catalog = await ArchiveCatalog.OpenAsync(files.Directory, lease);
        using var connection = ArchiveSql.OpenConnection(files.Database, SqliteOpenMode.ReadWrite);
        Assert.Throws<SqliteException>(() => Execute(connection, $"INSERT INTO local_collections VALUES('{Guid.Empty:D}','bad',1)"));
        Assert.Throws<SqliteException>(() => Execute(connection, $"INSERT INTO local_collections VALUES('{Collection}','bad',1.5)"));
        Execute(connection, $"INSERT INTO local_collections VALUES('{Collection}','ok',1)");
        Assert.Throws<SqliteException>(() => Execute(connection, $"INSERT INTO local_items VALUES('{Item}','{Item}',0,NULL)"));
        Execute(connection, $"INSERT INTO local_items VALUES('{Collection}','{Item}',0,NULL)");
        Assert.Throws<SqliteException>(() => Execute(connection, $"INSERT INTO local_items VALUES('{Collection}','{Revision}',0,NULL)"));
        Execute(connection, $"INSERT INTO local_items VALUES('{Collection}','{Revision}',1,NULL)");
        Execute(connection, $"INSERT INTO local_media_revisions VALUES('{Collection}','{Item}','{Revision}',1,'{Hash}',0,'one',NULL)");
        Execute(connection, $"INSERT INTO local_media_revisions VALUES('{Collection}','{Revision}','{Item}',1,'{Hash}',0,'two',NULL)");
        Assert.Equal(2L, Scalar(connection, "SELECT count(*) FROM local_media_revisions"));
        Assert.Throws<SqliteException>(() => Execute(connection, "UPDATE local_media_revisions SET role=2"));
        Assert.Throws<SqliteException>(() => Execute(connection, "UPDATE local_media_revisions SET length=1.5"));
        Assert.Throws<SqliteException>(() => Execute(connection, "UPDATE local_media_revisions SET sha256=upper(sha256)"));
        Assert.Throws<SqliteException>(() => Execute(connection, $"""
            INSERT INTO local_import_operations(operation_id,collection_id,catalog_instance,expected_generation,attempt,state,created_at,selection_json,item_count,payload_limit,metadata_reservation)
            VALUES('{Operation}','{Collection}','{Collection}',0,1,'Frozen','synthetic','{EmptyJson}',1,100,100)
            """));
    }

    [Theory]
    [InlineData("PRAGMA user_version=11")]
    [InlineData("PRAGMA user_version=0")]
    [InlineData("PRAGMA application_id=123")]
    public async Task UnknownAndForeignHeadersFailClosed(string mutation)
    {
        using Workspace files = new(); SeedLegacy(files.Database, 8);
        using (var connection = ArchiveSql.OpenConnection(files.Database, SqliteOpenMode.ReadWrite)) Execute(connection, mutation);
        byte[] before = File.ReadAllBytes(files.Database);
        using ProfileLease lease = ProfileLease.Acquire(files.Directory);
        await Assert.ThrowsAsync<InvalidDataException>(() => ArchiveCatalog.OpenAsync(files.Directory, lease));
        await Assert.ThrowsAsync<InvalidDataException>(() => ArchiveReader.OpenAsync(files.Directory));
        Assert.Equal(before, File.ReadAllBytes(files.Database));
    }

    [Fact]
    public async Task ZeroZeroWithForeignViewIsNotAnEmptyDatabase()
    {
        using Workspace files = new();
        using (var connection = ArchiveSql.OpenConnection(files.Database, SqliteOpenMode.ReadWriteCreate))
            Execute(connection, "CREATE VIEW unrelated AS SELECT 1");
        byte[] before = File.ReadAllBytes(files.Database);
        using ProfileLease lease = ProfileLease.Acquire(files.Directory);
        await Assert.ThrowsAsync<InvalidDataException>(() => ArchiveCatalog.OpenAsync(files.Directory, lease));
        Assert.Equal(before, File.ReadAllBytes(files.Database));
    }

    private static void SeedLegacy(string path, int version)
    {
        using var connection = ArchiveSql.OpenConnection(path, SqliteOpenMode.ReadWriteCreate);
        using var fixture = typeof(LocalCollectionMigrationTests).Assembly.GetManifestResourceStream("MeowChan.Tests.Fixtures.archive-schema-1.sql")!;
        using StreamReader text = new(fixture);
        Execute(connection, text.ReadToEnd());
        using var tx = connection.BeginTransaction();
        Execute(connection, "INSERT INTO archive_threads VALUES('synthetic','b','1','https://example.invalid/1','sentinel','inert',0,0,'2026-11-05T00:00:00.0000000+00:00',NULL,0)", tx);
        if (version >= 2) ArchiveSql.MigrateSearchSchema(connection, tx, CancellationToken.None);
        if (version >= 3) ArchiveSql.CreateSourceContractSchema(connection, tx);
        // The existing integrity helper adds both v4 and v5 nullable columns. Version 4 is
        // a compatible synthetic superset, not a released-client provenance assertion.
        if (version >= 4) ArchiveSql.CreateIntegritySchema(connection, tx);
        if (version >= 6) ArchiveSql.CreateNativeImportSchema(connection, tx);
        if (version == 9)
            Schema9MediaOperationsFixture.CreateSchema9(connection, tx);
        else
        {
            if (version >= 7) ArchiveSql.CreateMediaImportSchema(connection, tx);
            if (version >= 8) ArchiveSql.CreateMediaMergeSchema(connection, tx);
        }
        if (version >= 9) ArchiveSql.CreateLocalCollectionSchema(connection, tx);
        Execute(connection, $"PRAGMA user_version={version}", tx);
        tx.Commit();
    }

    private static void AssertFenceNames(SqliteConnection connection, SqliteTransaction tx)
    {
        string[] oldTables = ["archive_threads", "archive_posts", "archive_attachments",
            "archive_media_directories", "archive_source_contracts", "media_decisions", "archive_import_candidates"];
        string[] localTables = ["local_collections", "local_items", "local_media_revisions"];
        string[] operations = ["INSERT", "UPDATE", "DELETE"];
        using var command = ArchiveSql.Command(connection, tx,
            "SELECT name FROM sqlite_master WHERE type='trigger' AND name GLOB 'import_fence_*' ORDER BY name");
        using var reader = command.ExecuteReader();
        List<string> names = [];
        while (reader.Read()) names.Add(reader.GetString(0));
        Assert.Equal(oldTables.SelectMany(t => operations.Select(op => $"import_fence_{t}_{op}"))
            .OrderBy(n => n, StringComparer.Ordinal),
            names.Where(n => !n.StartsWith("import_fence_local_", StringComparison.Ordinal)));
        Assert.Equal(localTables.SelectMany(t => operations.Select(op => $"import_fence_{t}_{op}"))
            .OrderBy(n => n, StringComparer.Ordinal),
            names.Where(n => n.StartsWith("import_fence_local_", StringComparison.Ordinal)));
    }

    private static string[] TriggerDefinitions(SqliteConnection connection, SqliteTransaction? tx = null)
    {
        using var command = ArchiveSql.Command(connection, tx,
            "SELECT sql FROM sqlite_master WHERE type='trigger' AND name LIKE 'import_fence_archive_%' OR type='trigger' AND name LIKE 'import_fence_media_decisions_%' ORDER BY name");
        using var reader = command.ExecuteReader();
        List<string> result = [];
        while (reader.Read()) result.Add(reader.GetString(0));
        return result.ToArray();
    }
    private static void Execute(SqliteConnection connection, string sql, SqliteTransaction? tx = null)
    { using var command = ArchiveSql.Command(connection, tx, sql); command.ExecuteNonQuery(); }
    private static long Scalar(SqliteConnection connection, string sql, SqliteTransaction? tx = null)
    { using var command = ArchiveSql.Command(connection, tx, sql); return Convert.ToInt64(command.ExecuteScalar()); }
    private static string Text(SqliteConnection connection, string sql, SqliteTransaction? tx = null)
    { using var command = ArchiveSql.Command(connection, tx, sql); return (string)command.ExecuteScalar()!; }

    private sealed class Workspace : IDisposable
    {
        internal string Directory { get; } = Path.Combine(Path.GetTempPath(), "ochan-local-migration-" + Guid.NewGuid().ToString("N"));
        internal string Database => Path.Combine(Directory, "archive.db");
        internal Workspace() => System.IO.Directory.CreateDirectory(Directory);
        public void Dispose() => System.IO.Directory.Delete(Directory, true);
    }
}
