using MeowChan.Archive;
using MeowChan.Persistence;
using Microsoft.Data.Sqlite;

namespace MeowChan.Tests;

// Reconstructed documented pre-v5 schema shape, NOT a frozen/released historical v4 database.
// Unlike LocalCollectionMigrationTests' v4 superset, this fixture never invokes the current
// CreateIntegritySchema: it adds only the five v4 columns, and asserts all v5 columns absent.
public sealed class LocalCollectionEarliestV4MigrationTests
{
    private static readonly ResourceId Id = new("synthetic", "v4", "1");
    private static readonly string Sha256 = new('a', 64);

    [Fact]
    public async Task PreV5ShapeIsPassiveThenActuallyMigratesToElevenWithSentinelAndFence()
    {
        using Workspace files = new();
        SeedPreV5Shape(files);
        using (var connection = ArchiveSql.OpenConnection(files.Database, SqliteOpenMode.ReadOnly))
            AssertPreV5Shape(connection);
        byte[] before = File.ReadAllBytes(files.Database);
        await AssertPassiveSentinel(files);
        Assert.Equal(before, File.ReadAllBytes(files.Database));
        Assert.False(File.Exists(files.Database + "-journal"));
        Assert.Empty(Directory.GetDirectories(files.Profile));

        using ProfileLease lease = ProfileLease.Acquire(files.Profile);
        await using (var catalog = await ArchiveCatalog.OpenAsync(files.Profile, lease)) { }
        (string Instance, long Generation) fence;
        using (var connection = ArchiveSql.OpenConnection(files.Database, SqliteOpenMode.ReadOnly))
        using (var tx = connection.BeginTransaction(deferred: true))
        {
            Assert.Equal(12, ArchiveSql.GetPragma(connection, "user_version", tx));
            ArchiveSql.ValidateSchema(connection, tx);
            Assert.Equal(new[] { "local_collections", "local_import_operations", "local_import_receipts",
                    "local_items", "local_media_revisions" },
                Strings(connection, tx, "SELECT name FROM sqlite_master WHERE type='table' AND name GLOB 'local_*' ORDER BY name"));
            AssertFenceManifest(connection, tx);
            Assert.Equal(1L, Scalar(connection, tx, "SELECT count(*) FROM archive_import_fence"));
            fence = ArchiveSql.ReadImportFence(connection, tx);
            Assert.NotEqual(Guid.Empty, Guid.ParseExact(fence.Instance, "D"));
            Assert.Equal(0L, fence.Generation); // v4 had no fence; pure schema work is not content mutation.
            Assert.Contains("expected_sha1", Columns(connection, tx, "archive_attachments"));
            Assert.Contains("provider_size_hint_json", Columns(connection, tx, "media_decisions"));
            tx.Commit();
        }
        await AssertPassiveSentinel(files);
        // New fence is durable, not reseeded by a second writeropen.
        await using (var reopened = await ArchiveCatalog.OpenAsync(files.Profile, lease)) { }
        using var read = ArchiveSql.OpenConnection(files.Database, SqliteOpenMode.ReadOnly);
        Assert.Equal(fence, ArchiveSql.ReadImportFence(read, null));
        Assert.Empty(Directory.GetDirectories(files.Profile));
    }

    [Fact]
    public async Task LateLocalSchemaConflictRollsBackV5ColumnsFenceAndLocalDdlToPreV5Four()
    {
        using Workspace files = new();
        SeedPreV5Shape(files);
        using (var connection = ArchiveSql.OpenConnection(files.Database, SqliteOpenMode.ReadWrite))
            Execute(connection, null, "CREATE TABLE local_items(occupied TEXT); INSERT INTO local_items VALUES('keep')");
        byte[] before = File.ReadAllBytes(files.Database);
        using ProfileLease lease = ProfileLease.Acquire(files.Profile);
        // Actual public writeropen performs v5/v6/v7/v8 work before local target validation fails.
        await Assert.ThrowsAsync<InvalidDataException>(() => ArchiveCatalog.OpenAsync(files.Profile, lease));
        Assert.Equal(before, File.ReadAllBytes(files.Database));
        using (var read = ArchiveSql.OpenConnection(files.Database, SqliteOpenMode.ReadOnly))
        {
            AssertPreV5Shape(read);
            Assert.Equal(new[] { "local_items" }, Strings(read, null,
                "SELECT name FROM sqlite_master WHERE type='table' AND name GLOB 'local_*' ORDER BY name"));
            Assert.Equal(new[] { "keep" }, Strings(read, null, "SELECT occupied FROM local_items"));
        }
        await AssertPassiveSentinel(files);
        Assert.Equal(before, File.ReadAllBytes(files.Database));
        Assert.False(File.Exists(files.Database + "-journal"));
    }

    private static async Task AssertPassiveSentinel(Workspace files)
    {
        await using var reader = await ArchiveReader.OpenAsync(files.Profile);
        var view = Assert.IsType<ArchivedThreadView>(await reader.ReadThreadAsync(Id));
        Assert.Equal("pre-v5 sentinel", view.Summary.Title);
        Assert.Equal(7L, view.Summary.Revision);
        Assert.False(view.Summary.CaptureEnabled);
        Assert.True(view.DecimalPostOrder == true);
        var post = Assert.Single(view.GetPosts());
        Assert.Equal("2", post.PostId);
        var attachment = Assert.Single(post.Attachments!);
        Assert.Equal(3L, attachment.ExpectedSize);
        Assert.Equal(Sha256, attachment.ExpectedSha256);
        Assert.Null(attachment.ExpectedSha1);
        Assert.Null(attachment.ProviderSizeHint);
        Assert.Null(attachment.AttachmentRevisionKey); // No invented legacy binding.
    }

    private static void SeedPreV5Shape(Workspace files)
    {
        using var connection = ArchiveSql.OpenConnection(files.Database, SqliteOpenMode.ReadWriteCreate);
        using var fixture = typeof(LocalCollectionEarliestV4MigrationTests).Assembly.GetManifestResourceStream(
            "MeowChan.Tests.Fixtures.archive-schema-1.sql")!;
        using StreamReader text = new(fixture);
        Execute(connection, null, text.ReadToEnd());
        using var tx = connection.BeginTransaction(deferred: false);
        // Existing v2/v3 DDL is reused; never construct v9 and downgrade its stamp.
        ArchiveSql.MigrateSearchSchema(connection, tx, CancellationToken.None);
        ArchiveSql.CreateSourceContractSchema(connection, tx);
        Execute(connection, tx, """
            ALTER TABLE archive_attachments ADD COLUMN expected_sha256 TEXT;
            ALTER TABLE archive_attachments ADD COLUMN attachment_revision_key TEXT;
            ALTER TABLE media_decisions ADD COLUMN expected_sha256 TEXT;
            ALTER TABLE media_decisions ADD COLUMN acquisition_revision_key TEXT;
            ALTER TABLE media_decisions ADD COLUMN attachment_references_json TEXT;
            PRAGMA user_version=4;
            INSERT INTO archive_threads(source,board,thread,url,title,media_directory,source_state,capture_enabled,first_seen_at,revision)
                VALUES('synthetic','v4','1','https://example.invalid/1','pre-v5 sentinel','inert',0,0,'2026-11-05T00:00:00.0000000+00:00',7);
            INSERT INTO archive_posts(source,board,thread,post,ordinal,fields,is_deleted)
                VALUES('synthetic','v4','1','2',0,32,0);
            INSERT INTO archive_source_contracts VALUES('synthetic','v4','1',1);
            """);
        using var attachment = ArchiveSql.Command(connection, tx, """
            INSERT INTO archive_attachments(source,board,thread,post,position,media_id,variant,url,expected_size,is_deleted,expected_sha256)
                VALUES('synthetic','v4','1','2',0,'media','original','https://example.invalid/media',3,0,$sha)
            """);
        attachment.Parameters.AddWithValue("$sha", Sha256);
        attachment.ExecuteNonQuery();
        tx.Commit();
    }

    private static void AssertPreV5Shape(SqliteConnection connection)
    {
        using var tx = connection.BeginTransaction(deferred: true);
        Assert.Equal(4, ArchiveSql.GetPragma(connection, "user_version", tx));
        ArchiveSql.ValidateSchema(connection, tx);
        Assert.Equal(new[] { "acquisition_revision_key", "attachment_references_json", "board_id", "decided_at_utc",
                "destination_path", "download_uri", "expected_md5", "expected_sha256", "expected_size", "extension",
                "legacy_path", "media_id", "original_filename", "selection_json", "source_id", "thread_id", "variant" },
            Columns(connection, tx, "media_decisions"));
        Assert.Equal(new[] { "attachment_revision_key", "board", "expected_md5", "expected_sha256", "expected_size",
                "is_deleted", "media_id", "original_name", "planned_path", "position", "post", "source", "thread", "url", "variant" },
            Columns(connection, tx, "archive_attachments"));
        Assert.Equal(0L, Scalar(connection, tx,
            "SELECT count(*) FROM sqlite_master WHERE name GLOB 'archive_import_*' OR name GLOB 'archive_media_import_*' OR name GLOB 'archive_media_merge_*' OR name GLOB 'import_fence_*'"));
        tx.Commit();
    }

    private static void AssertFenceManifest(SqliteConnection connection, SqliteTransaction tx)
    {
        string[] oldTables = ["archive_threads", "archive_posts", "archive_attachments", "archive_media_directories",
            "archive_source_contracts", "media_decisions", "archive_import_candidates"];
        string[] localTables = ["local_collections", "local_items", "local_media_revisions"];
        string[] operations = ["INSERT", "UPDATE", "DELETE"];
        string[] actual = Strings(connection, tx,
            "SELECT name FROM sqlite_master WHERE type='trigger' AND name GLOB 'import_fence_*' ORDER BY name");
        Assert.Equal(oldTables.SelectMany(t => operations.Select(op => $"import_fence_{t}_{op}")).OrderBy(n => n, StringComparer.Ordinal),
            actual.Where(n => !n.StartsWith("import_fence_local_", StringComparison.Ordinal)));
        Assert.Equal(localTables.SelectMany(t => operations.Select(op => $"import_fence_{t}_{op}")).OrderBy(n => n, StringComparer.Ordinal),
            actual.Where(n => n.StartsWith("import_fence_local_", StringComparison.Ordinal)));
    }

    private static string[] Columns(SqliteConnection connection, SqliteTransaction tx, string table)
        => Strings(connection, tx, $"SELECT name FROM pragma_table_info('{table}') ORDER BY name");
    private static string[] Strings(SqliteConnection connection, SqliteTransaction? tx, string sql)
    {
        using var command = ArchiveSql.Command(connection, tx, sql);
        using var reader = command.ExecuteReader();
        List<string> values = [];
        while (reader.Read()) values.Add(reader.GetString(0));
        return values.ToArray();
    }
    private static long Scalar(SqliteConnection connection, SqliteTransaction tx, string sql)
    { using var command = ArchiveSql.Command(connection, tx, sql); return Convert.ToInt64(command.ExecuteScalar()); }
    private static void Execute(SqliteConnection connection, SqliteTransaction? tx, string sql)
    { using var command = ArchiveSql.Command(connection, tx, sql); command.ExecuteNonQuery(); }
    private sealed class Workspace : IDisposable
    {
        internal string Profile { get; } = Path.Combine(Path.GetTempPath(), "ochan-earliest-v4-" + Guid.NewGuid().ToString("N"));
        internal string Database => Path.Combine(Profile, "archive.db");
        internal Workspace() => Directory.CreateDirectory(Profile);
        public void Dispose() => Directory.Delete(Profile, true);
    }
}
