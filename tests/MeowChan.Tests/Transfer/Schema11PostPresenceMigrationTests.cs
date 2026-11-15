using System.Globalization;
using System.Text.Json;
using MeowChan.Archive;
using MeowChan.Interfaces;
using MeowChan.Persistence;
using Microsoft.Data.Sqlite;

namespace MeowChan.Tests.Transfer;

// Independent schema-only lane: real catalog creation, real DROP COLUMN back to schema10,
// and the actual migration helper. No NativeV2ImportFixture or forged version-only downgrade.
public sealed class Schema11PostPresenceMigrationTests
{
    private static readonly ResourceId Id = new("4chan", "b", "7001");
    private static readonly DateTimeOffset At = new(2026, 11, 8, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task GenuineTenReadIsPassiveAndNormalOpenPreservesAllLegacyRows()
    {
        using Fixture files = await Fixture.CreateTenAsync();
        string[] oldPosts;
        string[] otherTables;
        string[] otherObjects;
        using (SqliteConnection connection = files.Open())
        {
            ArchiveSql.ValidateSchema(connection);
            Assert.Equal(10, ArchiveSql.GetPragma(connection, "user_version"));
            Assert.False(HasPresence(connection));
            oldPosts = Rows(connection, "archive_posts");
            otherTables = DataSnapshot(connection, excludePosts: true);
            otherObjects = Objects(connection, excludePosts: true);
        }
        byte[] beforeRead = File.ReadAllBytes(files.Database);
        await AssertLegacyReadAsync(files.Profile);
        Assert.Equal(beforeRead, File.ReadAllBytes(files.Database));

        await files.OpenCatalogAsync();
        using (SqliteConnection connection = files.Open())
        {
            ArchiveSql.ValidateSchema(connection);
            Assert.Equal(13, ArchiveSql.GetPragma(connection, "user_version"));
            Assert.Equal(ArchiveSql.ApplicationId, ArchiveSql.GetPragma(connection, "application_id"));
            Assert.True(HasPresence(connection));
            Assert.Equal(oldPosts, Rows(connection, "archive_posts", excludePresence: true));
            Assert.Equal(otherTables, DataSnapshot(connection, excludePosts: true));
            Assert.Equal(otherObjects, Objects(connection, excludePosts: true));
            Assert.Equal(4L, Scalar(connection, "SELECT count(*) FROM archive_posts WHERE attachments_were_null=0"));
            Assert.Equal(1L, Scalar(connection, "SELECT count(*) FROM archive_attachments"));
        }
        // Schema10 had no null marker: a historical null cannot be guessed back into existence.
        await AssertLegacyReadAsync(files.Profile);
    }

    [Fact]
    public async Task HelperOwnsNeitherStampNorCommitAndCallerCanCommitThenReopenTwice()
    {
        using Fixture files = await Fixture.CreateTenAsync();
        string[] before;
        using (SqliteConnection connection = files.Open(SqliteOpenMode.ReadWrite))
        using (SqliteTransaction transaction = connection.BeginTransaction(deferred: false))
        {
            before = DataSnapshot(connection, transaction, excludePosts: true);
            ArchiveSql.MigratePostAttachmentPresenceSchema11(connection, transaction, CancellationToken.None);
            Assert.Equal(10, ArchiveSql.GetPragma(connection, "user_version", transaction));
            Assert.True(HasPresence(connection, transaction));
            ArchiveSql.ValidatePostAttachmentPresenceSchema11(connection, transaction);
            Assert.Equal(before, DataSnapshot(connection, transaction, excludePosts: true));
            Execute(connection, "PRAGMA user_version=11", transaction);
            ArchiveSql.ValidateSchema(connection, transaction);
            transaction.Commit();
        }
        string[] rows;
        string[] objects;
        using (SqliteConnection connection = files.Open())
        { rows = DataSnapshot(connection); objects = Objects(connection); }
        for (int reopen = 0; reopen < 2; reopen++)
        {
            await files.OpenCatalogAsync();
            using SqliteConnection connection = files.Open(SqliteOpenMode.ReadWrite);
            using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);
            ArchiveSql.MigratePostAttachmentPresenceSchema11(connection, transaction);
            Assert.Equal(13, ArchiveSql.GetPragma(connection, "user_version", transaction));
            Assert.Equal(rows, DataSnapshot(connection, transaction));
            Assert.Equal(objects, Objects(connection, transaction));
            transaction.Commit();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CallerFailureAfterDdlRollsBackColumnRowsAndOptionalVersionStamp(bool stamp)
    {
        using Fixture files = await Fixture.CreateTenAsync();
        string[] before;
        using (SqliteConnection read = files.Open()) before = CompleteSnapshot(read);
        using (SqliteConnection connection = files.Open(SqliteOpenMode.ReadWrite))
        {
            Assert.Throws<IOException>((Action)(() =>
            {
                using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);
                ArchiveSql.MigratePostAttachmentPresenceSchema11(connection, transaction);
                Assert.True(HasPresence(connection, transaction));
                if (stamp) Execute(connection, "PRAGMA user_version=11", transaction);
                throw new IOException("Synthetic caller failure after schema11 DDL.");
            }));
        }
        using (SqliteConnection read = files.Open())
        {
            Assert.False(HasPresence(read));
            Assert.Equal(before, CompleteSnapshot(read));
            ArchiveSql.ValidateSchema(read);
        }
        await files.OpenCatalogAsync(); // The rollback left a valid, still-upgradable schema10.
    }

    [Fact]
    public async Task PreCanceledHelperAndNormalOpenLeaveGenuineTenUnchanged()
    {
        using Fixture files = await Fixture.CreateTenAsync();
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        string[] before;
        using (SqliteConnection connection = files.Open(SqliteOpenMode.ReadWrite))
        {
            before = CompleteSnapshot(connection);
            using (SqliteTransaction transaction = connection.BeginTransaction(deferred: false))
            {
                Assert.ThrowsAny<OperationCanceledException>(() =>
                    ArchiveSql.MigratePostAttachmentPresenceSchema11(connection, transaction, cancellation.Token));
                Assert.False(HasPresence(connection, transaction));
                Assert.Equal(10, ArchiveSql.GetPragma(connection, "user_version", transaction));
                transaction.Rollback();
            }
        }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => files.OpenCatalogAsync(cancellation.Token));
        using SqliteConnection read = files.Open();
        Assert.Equal(before, CompleteSnapshot(read));
    }

    [Fact]
    public async Task CallerCancellationAfterMigrationBeforeCommitRollsBackDdlAndStamp()
    {
        using Fixture files = await Fixture.CreateTenAsync();
        string[] before;
        using (SqliteConnection connection = files.Open(SqliteOpenMode.ReadWrite))
        {
            before = CompleteSnapshot(connection);
            using CancellationTokenSource cancellation = new();
            // Deterministic outer-transaction cancellation, not a claimed mid-ALTER hook.
            Assert.ThrowsAny<OperationCanceledException>(() =>
            {
                using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);
                ArchiveSql.MigratePostAttachmentPresenceSchema11(connection, transaction, cancellation.Token);
                Execute(connection, "PRAGMA user_version=11", transaction);
                cancellation.Cancel();
                cancellation.Token.ThrowIfCancellationRequested();
                transaction.Commit();
            });
        }
        using SqliteConnection read = files.Open();
        Assert.False(HasPresence(read));
        Assert.Equal(before, CompleteSnapshot(read));
    }

    [Theory]
    [InlineData("INTEGER NOT NULL DEFAULT 0 CHECK(attachments_were_null IN (0,1))")]
    [InlineData("INTEGER NOT NULL DEFAULT 1 CHECK(attachments_were_null IN (0,1))")]
    [InlineData("TEXT NOT NULL DEFAULT '0'")]
    public async Task TenWithDuplicateColumnIsRejectedNotRepaired(string definition)
    {
        using Fixture files = await Fixture.CreateTenAsync();
        string[] before;
        using (SqliteConnection connection = files.Open(SqliteOpenMode.ReadWrite))
        {
            Execute(connection, "ALTER TABLE archive_posts ADD COLUMN attachments_were_null " + definition);
            before = CompleteSnapshot(connection);
            using (SqliteTransaction transaction = connection.BeginTransaction(deferred: false))
            {
                Assert.Throws<SqliteException>(() => ArchiveSql.MigratePostAttachmentPresenceSchema11(connection, transaction));
                transaction.Rollback();
            }
        }
        await Assert.ThrowsAsync<SqliteException>(() => files.OpenCatalogAsync());
        using SqliteConnection read = files.Open();
        Assert.Equal(before, CompleteSnapshot(read));
        Assert.Equal(10, ArchiveSql.GetPragma(read, "user_version"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("INTEGER NOT NULL DEFAULT 0")]
    [InlineData("INTEGER DEFAULT 0 CHECK(attachments_were_null IN (0,1))")]
    [InlineData("INTEGER NOT NULL DEFAULT 1 CHECK(attachments_were_null IN (0,1))")]
    [InlineData("TEXT NOT NULL DEFAULT '0' CHECK(attachments_were_null IN ('0','1'))")]
    public async Task ElevenWithMissingOrMalformedPresenceShapeFailsWithoutRepair(string? definition)
    {
        using Fixture files = await Fixture.CreateTenAsync();
        string[] before;
        using (SqliteConnection connection = files.Open(SqliteOpenMode.ReadWrite))
        {
            if (definition != null)
                Execute(connection, "ALTER TABLE archive_posts ADD COLUMN attachments_were_null " + definition);
            Execute(connection, "PRAGMA user_version=11");
            before = CompleteSnapshot(connection);
            using (SqliteTransaction transaction = connection.BeginTransaction(deferred: false))
            {
                Assert.Throws<InvalidDataException>(() => ArchiveSql.MigratePostAttachmentPresenceSchema11(connection, transaction));
                transaction.Rollback();
            }
        }
        await Assert.ThrowsAsync<InvalidDataException>(() => files.OpenCatalogAsync());
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
        { await using ArchiveReader reader = await ArchiveReader.OpenAsync(files.Profile); });
        using SqliteConnection read = files.Open();
        Assert.Equal(before, CompleteSnapshot(read));
    }

    [Fact]
    public async Task UnknownFutureVersionIsRejectedByHelperAndNormalOpenWithoutDowngrade()
    {
        using Fixture files = await Fixture.CreateTenAsync();
        await files.OpenCatalogAsync();
        string[] before;
        using (SqliteConnection connection = files.Open(SqliteOpenMode.ReadWrite))
        {
            Execute(connection, "PRAGMA user_version=13");
            before = CompleteSnapshot(connection);
            using (SqliteTransaction transaction = connection.BeginTransaction(deferred: false))
            {
                Assert.Throws<InvalidDataException>(() => ArchiveSql.MigratePostAttachmentPresenceSchema11(connection, transaction));
                transaction.Rollback();
            }
        }
        await Assert.ThrowsAsync<InvalidDataException>(() => files.OpenCatalogAsync());
        using SqliteConnection read = files.Open();
        Assert.Equal(before, CompleteSnapshot(read));
        Assert.Equal(13, ArchiveSql.GetPragma(read, "user_version"));
    }

    private static async Task AssertLegacyReadAsync(string profile)
    {
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(profile);
        ArchivedThreadView view = Assert.IsType<ArchivedThreadView>(await reader.ReadThreadAsync(Id));
        Assert.Equal(4, view.PostCount);
        CapturedPost missing = view.Posts.Single(p => p.PostId == "1");
        Assert.Equal(PostFieldPresence.Html, missing.Fields);
        Assert.Empty(Assert.IsAssignableFrom<IReadOnlyList<CapturedAttachment>>(missing.Attachments));
        foreach (string id in new[] { "2", "4" })
        {
            CapturedPost empty = view.Posts.Single(p => p.PostId == id);
            Assert.Equal(PostFieldPresence.Attachments, empty.Fields);
            Assert.Empty(Assert.IsAssignableFrom<IReadOnlyList<CapturedAttachment>>(empty.Attachments));
        }
        CapturedAttachment retained = Assert.Single(view.Posts.Single(p => p.PostId == "3").Attachments!);
        Assert.Null(retained.AttachmentRevisionKey); // Migration must not manufacture provenance.
        Assert.Equal("original.jpg", retained.OriginalFileName);
        Assert.Equal("https://i.4cdn.org/b/7001001.jpg", retained.Url);
    }

    private static void Execute(SqliteConnection c, string sql, SqliteTransaction? tx = null)
    { using SqliteCommand command = ArchiveSql.Command(c, tx, sql); command.ExecuteNonQuery(); }

    private static object? Scalar(SqliteConnection c, string sql, SqliteTransaction? tx = null)
    { using SqliteCommand command = ArchiveSql.Command(c, tx, sql); return command.ExecuteScalar(); }

    private static bool HasPresence(SqliteConnection c, SqliteTransaction? tx = null)
        => Columns(c, "archive_posts", tx).Contains("attachments_were_null", StringComparer.Ordinal);

    private static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"") + "\"";

    private static string[] Columns(SqliteConnection c, string table, SqliteTransaction? tx = null)
    {
        using SqliteCommand command = ArchiveSql.Command(c, tx, "PRAGMA table_info(" + Quote(table) + ")");
        using SqliteDataReader reader = command.ExecuteReader();
        List<string> values = [];
        while (reader.Read()) values.Add(reader.GetString(1));
        return values.ToArray();
    }

    private static string[] Rows(SqliteConnection c, string table, SqliteTransaction? tx = null, bool excludePresence = false)
    {
        string[] columns = Columns(c, table, tx).Where(n => !excludePresence || n != "attachments_were_null").ToArray();
        using SqliteCommand command = ArchiveSql.Command(c, tx, "SELECT " + string.Join(",", columns.Select(Quote)) + " FROM " + Quote(table));
        using SqliteDataReader reader = command.ExecuteReader();
        List<string> rows = [];
        while (reader.Read())
        {
            string[] values = new string[reader.FieldCount];
            for (int index = 0; index < values.Length; index++)
                values[index] = reader.GetValue(index) switch
                {
                    DBNull => "null",
                    byte[] bytes => "blob:" + Convert.ToHexString(bytes),
                    string text => "text:" + text,
                    long number => "integer:" + number.ToString(CultureInfo.InvariantCulture),
                    double number => "real:" + number.ToString("R", CultureInfo.InvariantCulture),
                    _ => throw new InvalidDataException("Unexpected SQLite storage type in schema fixture.")
                };
            rows.Add(JsonSerializer.Serialize(values));
        }
        return rows.Order(StringComparer.Ordinal).ToArray();
    }

    private static string[] DataSnapshot(SqliteConnection c, SqliteTransaction? tx = null, bool excludePosts = false)
    {
        List<string> tables = [];
        using (SqliteCommand command = ArchiveSql.Command(c, tx,
            "SELECT name FROM sqlite_master WHERE type='table' AND substr(name,1,7)<>'sqlite_' ORDER BY name"))
        using (SqliteDataReader reader = command.ExecuteReader())
            while (reader.Read()) tables.Add(reader.GetString(0));
        return tables.Where(t => !excludePosts || t != "archive_posts")
            .SelectMany(t => new[] { "table:" + t }.Concat(Rows(c, t, tx))).ToArray();
    }

    private static string[] Objects(SqliteConnection c, SqliteTransaction? tx = null, bool excludePosts = false)
    {
        using SqliteCommand command = ArchiveSql.Command(c, tx,
            "SELECT type,name,tbl_name,sql FROM sqlite_master WHERE substr(name,1,7)<>'sqlite_' ORDER BY type,name");
        using SqliteDataReader reader = command.ExecuteReader();
        List<string> objects = [];
        while (reader.Read())
        {
            if (excludePosts && reader.GetString(1) == "archive_posts") continue;
            objects.Add(JsonSerializer.Serialize(new[] { reader.GetString(0), reader.GetString(1),
                reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3) }));
        }
        return objects.ToArray();
    }

    private static string[] CompleteSnapshot(SqliteConnection c, SqliteTransaction? tx = null)
        => new[] { "version:" + ArchiveSql.GetPragma(c, "user_version", tx),
            "app:" + ArchiveSql.GetPragma(c, "application_id", tx) }
            .Concat(Objects(c, tx)).Concat(DataSnapshot(c, tx)).ToArray();

    private sealed class Fixture : IDisposable
    {
        internal string Profile { get; } = Directory.CreateTempSubdirectory("meowchan-schema11-presence-").FullName;
        internal string Database => Path.Combine(Profile, "archive.db");
        internal SqliteConnection Open(SqliteOpenMode mode = SqliteOpenMode.ReadOnly)
            => ArchiveSql.OpenConnection(Database, mode);

        internal static async Task<Fixture> CreateTenAsync()
        {
            Fixture files = new();
            try
            {
                using (ProfileLease lease = ProfileLease.Acquire(files.Profile))
                {
                    await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(files.Profile, lease);
                    await catalog.CapturePreservingRevisionsAsync(new(Id, "https://boards.4chan.org/b/thread/7001",
                        "schema11 synthetic legacy fixture", ThreadSourceState.NotFound, At,
                        Path.Combine(files.Profile, "media"),
                        [new("1", 1, PostFieldPresence.Html, Html: "<p>retained text</p>"),
                         new("2", 2, PostFieldPresence.Attachments, Attachments: []),
                         new("3", 3, PostFieldPresence.Attachments, Attachments:
                             [new(0, "7001001", Url: "https://i.4cdn.org/b/7001001.jpg", OriginalFileName: "original.jpg")]),
                         new("4", 4, PostFieldPresence.Attachments, Attachments: null)]));
                }
                using SqliteConnection connection = files.Open(SqliteOpenMode.ReadWrite);
                Assert.Equal(13, ArchiveSql.GetPragma(connection, "user_version"));
                string[] otherTables = DataSnapshot(connection, excludePosts: true);
                string[] otherObjects = Objects(connection, excludePosts: true);
                using (SqliteTransaction transaction = connection.BeginTransaction(deferred: false))
                {
                    Execute(connection, "ALTER TABLE archive_posts DROP COLUMN attachments_were_null", transaction);
                    Execute(connection, "PRAGMA user_version=10", transaction);
                    ArchiveSql.ValidateSchema(connection, transaction);
                    Assert.False(HasPresence(connection, transaction));
                    Assert.Equal(otherTables, DataSnapshot(connection, transaction, excludePosts: true));
                    Assert.Equal(otherObjects, Objects(connection, transaction, excludePosts: true));
                    transaction.Commit();
                }
                return files;
            }
            catch { files.Dispose(); throw; }
        }

        internal async Task OpenCatalogAsync(CancellationToken token = default)
        {
            using ProfileLease lease = ProfileLease.Acquire(Profile);
            await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(Profile, lease, token);
        }

        public void Dispose() { if (Directory.Exists(Profile)) Directory.Delete(Profile, true); }
    }
}
