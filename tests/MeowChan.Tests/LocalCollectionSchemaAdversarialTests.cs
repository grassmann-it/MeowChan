using MeowChan.Archive;
using Microsoft.Data.Sqlite;

namespace MeowChan.Tests;

// Only synthetic temporary SQLite files. No ProfileLease, catalog, media or host.
public sealed class LocalCollectionSchemaAdversarialTests
{
    private const string Collection = "10203040-5060-7080-90a0-b0c0d0e0f001";
    private const string Item = "20304050-6070-8090-a0b0-c0d0e0f00102";
    private const string Revision = "30405060-7080-90a0-b0c0-d0e0f0010203";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void IntactDdlDoesNotHideExistingOrphanRows(bool orphanRevision)
    {
        using var database = new SyntheticDatabase();
        string[] definitions;
        using (var write = database.Open(SqliteOpenMode.ReadWrite))
        {
            definitions = LocalDefinitions(write);
            // Simulate a foreign writer that disabled FK enforcement, not malformed DDL.
            Execute(write, "PRAGMA foreign_keys=OFF");
            Assert.Equal(0, ArchiveSql.GetPragma(write, "foreign_keys"));
            if (orphanRevision)
            {
                Execute(write, $"INSERT INTO local_collections VALUES('{Collection}','synthetic',1)");
                Execute(write, $"""
                    INSERT INTO local_media_revisions VALUES(
                        '{Collection}','{Item}','{Revision}',1,'{new string('a', 64)}',0,'synthetic.original.bin',NULL)
                    """);
            }
            else
                Execute(write, $"INSERT INTO local_items VALUES('{Collection}','{Item}',0,NULL)");
            Assert.Equal(definitions, LocalDefinitions(write));
            using var check = write.CreateCommand();
            check.CommandText = orphanRevision
                ? "PRAGMA foreign_key_check(local_media_revisions)"
                : "PRAGMA foreign_key_check(local_items)";
            using var rows = check.ExecuteReader();
            Assert.True(rows.Read()); // Prove the intended corrupt row was actually stored.
        }
        AssertRejectedWithoutRepair(database, orphanRevision
            ? "SELECT count(*) FROM local_media_revisions" : "SELECT count(*) FROM local_items",
            "Invalid local collection foreign key.");
    }

    [Theory]
    [InlineData("total_length<=payload_limit", "total_length>=0")]
    [InlineData("'Capturing'", "'Capt uring'")]
    public void SameColumnsAndNamesCannotHideWeakenedCheckOrChangedQuotedLiteral(string original, string replacement)
    {
        using var database = new SyntheticDatabase();
        using (var write = database.Open(SqliteOpenMode.ReadWrite))
        {
            string ddl = Text(write, "SELECT sql FROM sqlite_master WHERE type='table' AND name='local_import_operations'");
            string columns = ColumnSignature(write);
            string changed = ddl.Replace(original, replacement, StringComparison.Ordinal);
            Assert.NotEqual(ddl, changed); // Fail loudly if the actual manifest changes.
            using var tx = write.BeginTransaction();
            Execute(write, "DROP TABLE local_import_operations", tx);
            Execute(write, changed, tx);
            tx.Commit();
            Assert.Equal(columns, ColumnSignature(write));
            Assert.Equal(5L, Scalar(write, "SELECT count(*) FROM sqlite_master WHERE type='table' AND name LIKE 'local_%'"));
            Assert.Equal(9L, Scalar(write, "SELECT count(*) FROM sqlite_master WHERE type='trigger' AND name LIKE 'import_fence_local_%'"));
        }
        // First mutation removes the budget comparison while preserving every column.
        // Second differs ONLY inside SQL string literals: a whitespace-stripping validator
        // must not erase the semantic difference between Capturing and Capt uring.
        AssertRejectedWithoutRepair(database, "SELECT count(*) FROM local_import_operations",
            "Invalid local collection schema definition.", expectedRows: 0);
    }

    private static void AssertRejectedWithoutRepair(SyntheticDatabase database, string countSql,
        string expectedMessage, long expectedRows = 1)
    {
        byte[] before = File.ReadAllBytes(database.Path);
        using (var read = database.Open(SqliteOpenMode.ReadOnly))
        {
            Assert.Equal(1, ArchiveSql.GetPragma(read, "foreign_keys"));
            Assert.Equal(9, ArchiveSql.GetPragma(read, "user_version"));
            Assert.Equal(ArchiveSql.ApplicationId, ArchiveSql.GetPragma(read, "application_id"));
            // Actual production entry point: implicit snapshot as well as caller-owned snapshot.
            var implicitError = Assert.Throws<InvalidDataException>(() => ArchiveSql.ValidateSchema(read));
            Assert.Equal(expectedMessage, implicitError.Message);
            using var tx = read.BeginTransaction(deferred: true);
            var explicitError = Assert.Throws<InvalidDataException>(() => ArchiveSql.ValidateSchema(read, tx));
            Assert.Equal(expectedMessage, explicitError.Message);
            Assert.Equal(expectedRows, Scalar(read, countSql, tx));
        }
        Assert.Equal(before, File.ReadAllBytes(database.Path));
    }

    private static string[] LocalDefinitions(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT sql FROM sqlite_master WHERE name LIKE 'local_%' OR name LIKE 'import_fence_local_%' ORDER BY type,name";
        using var rows = command.ExecuteReader();
        List<string> definitions = [];
        while (rows.Read()) definitions.Add(rows.GetString(0));
        return definitions.ToArray();
    }

    private static string ColumnSignature(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA table_info(local_import_operations)";
        using var rows = command.ExecuteReader();
        List<string> columns = [];
        while (rows.Read())
            columns.Add($"{rows.GetInt64(0)}|{rows.GetString(1)}|{rows.GetString(2)}|{rows.GetInt64(3)}|{rows.GetInt64(5)}");
        return string.Join("\n", columns);
    }

    private static void Execute(SqliteConnection connection, string sql, SqliteTransaction? tx = null)
    { using var command = ArchiveSql.Command(connection, tx, sql); command.ExecuteNonQuery(); }
    private static long Scalar(SqliteConnection connection, string sql, SqliteTransaction? tx = null)
    { using var command = ArchiveSql.Command(connection, tx, sql); return Convert.ToInt64(command.ExecuteScalar()); }
    private static string Text(SqliteConnection connection, string sql)
    { using var command = ArchiveSql.Command(connection, null, sql); return (string)command.ExecuteScalar()!; }

    private sealed class SyntheticDatabase : IDisposable
    {
        private readonly string _directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "ochan-local-schema-adversarial-" + Guid.NewGuid().ToString("N"));
        internal string Path => System.IO.Path.Combine(_directory, "synthetic.db");
        internal SqliteConnection Open(SqliteOpenMode mode) => ArchiveSql.OpenConnection(Path, mode);

        internal SyntheticDatabase()
        {
            Directory.CreateDirectory(_directory);
            try
            {
                using var connection = Open(SqliteOpenMode.ReadWriteCreate);
                using var fixture = typeof(LocalCollectionSchemaAdversarialTests).Assembly
                    .GetManifestResourceStream("MeowChan.Tests.Fixtures.archive-schema-1.sql")!;
                using var text = new StreamReader(fixture);
                Execute(connection, text.ReadToEnd());
                using var tx = connection.BeginTransaction();
                ArchiveSql.MigrateSearchSchema(connection, tx, CancellationToken.None);
                ArchiveSql.CreateSourceContractSchema(connection, tx);
                ArchiveSql.CreateIntegritySchema(connection, tx);
                ArchiveSql.CreateNativeImportSchema(connection, tx);
                ArchiveSql.CreateMediaImportSchema(connection, tx);
                ArchiveSql.CreateMediaMergeSchema(connection, tx);
                ArchiveSql.CreateLocalCollectionSchema(connection, tx);
                Execute(connection, "PRAGMA user_version=9", tx);
                ArchiveSql.ValidateSchema(connection, tx); // A valid control before every corruption.
                tx.Commit();
            }
            catch
            {
                Directory.Delete(_directory, recursive: true);
                throw;
            }
        }
        public void Dispose() => Directory.Delete(_directory, recursive: true);
    }
}
