using System.Globalization;
using System.Text.Json;
using MeowChan.Archive;
using MeowChan.Transfer;
using Microsoft.Data.Sqlite;

namespace MeowChan.Tests.Transfer;

// Read-only persistence contracts, not public success, Apply or recovery tests.
// WAL coverage pins a caller snapshot before a second connection commits; it does
// not claim to inject a race between private statements in an owned reader.
public sealed class PersistedMediaReadLifecycleTests
{
    private static readonly Guid MissingOperation = Guid.Parse("cd220000-0000-4000-8000-000000000099");
    private static readonly string OtherRequest = new('9', 64);
    private const string LegacyGate = "This media transfer plan requires version-aware reconciliation.";

    [Theory]
    [InlineData(false, "operation")]
    [InlineData(false, "receipt")]
    [InlineData(false, "page")]
    [InlineData(true, "operation")]
    [InlineData(true, "receipt")]
    [InlineData(true, "page")]
    public void OwnedTransactionIsReleasedOnEmptySuccessSchemaAndDecodeFailures(bool merge, string kind)
    {
        using PersistedMediaFixture f = new();
        string empty = DatabaseEvidence(f.Connection);
        AssertReadCount(Read(f.Connection, null, merge, kind), merge, kind, 0);
        AssertFreshTransaction(f.Connection);
        Assert.Equal(empty, DatabaseEvidence(f.Connection));

        var row = PersistedMediaFixture.Row(merge);
        if (kind == "receipt") f.InsertReceipt(merge, PersistedMediaFixture.Receipt(merge, row));
        else f.Insert(merge, row);
        string before = DatabaseEvidence(f.Connection);
        long changes = Changes(f.Connection);
        AssertReadCount(Read(f.Connection, null, merge, kind), merge, kind, 1);
        AssertFreshTransaction(f.Connection);
        Assert.Equal(before, DatabaseEvidence(f.Connection));
        Assert.Equal(changes, Changes(f.Connection));

        // Deliberate stored corruption, not an accepted writer/producer path.
        f.Execute("PRAGMA ignore_check_constraints=ON");
        try { f.Execute($"UPDATE {Table(merge, kind == "receipt")} SET plan_version=3"); }
        finally { f.Execute("PRAGMA ignore_check_constraints=OFF"); }
        Assert.Equal(0L, Scalar(f.Connection, null, "PRAGMA ignore_check_constraints"));
        string corrupt = DatabaseEvidence(f.Connection);
        changes = Changes(f.Connection);
        for (int attempt = 0; attempt < 2; attempt++)
        {
            Assert.Throws<InvalidDataException>(() => Read(f.Connection, null, merge, kind));
            AssertFreshTransaction(f.Connection);
            Assert.Equal(corrupt, DatabaseEvidence(f.Connection));
            Assert.Equal(changes, Changes(f.Connection));
        }

        f.Execute($"UPDATE {Table(merge, kind == "receipt")} SET plan_version=2");
        f.Execute("PRAGMA user_version=9");
        string wrongHeader = DatabaseEvidence(f.Connection);
        changes = Changes(f.Connection);
        Assert.Throws<InvalidDataException>(() => Read(f.Connection, null, merge, kind));
        AssertFreshTransaction(f.Connection);
        Assert.Equal(wrongHeader, DatabaseEvidence(f.Connection));
        Assert.Equal(changes, Changes(f.Connection));
        f.Execute("PRAGMA user_version=10");
        AssertReadCount(Read(f.Connection, null, merge, kind), merge, kind, 1);
        AssertFreshTransaction(f.Connection);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void CallerTransactionRetainsUncommittedDataAndOwnershipOnEveryExit(bool merge, bool receipt)
    {
        using PersistedMediaFixture f = new();
        string kind = receipt ? "receipt" : "operation";
        string empty = DatabaseEvidence(f.Connection);
        using (SqliteTransaction transaction = f.Connection.BeginTransaction())
        {
            var row = PersistedMediaFixture.Row(merge);
            Insert(f.Connection, transaction, merge, receipt,
                receipt ? PersistedMediaFixture.Receipt(merge, row) : row);
            string pending = DatabaseEvidence(f.Connection, transaction);
            long changes = Changes(f.Connection, transaction);
            AssertReadCount(Read(f.Connection, transaction, merge, kind), merge, kind, 1);
            Assert.Null(Read(f.Connection, transaction, merge, kind, operation: MissingOperation));
            Assert.Throws<InvalidDataException>(() => Read(f.Connection, transaction, merge, kind, request: OtherRequest));
            Assert.Same(f.Connection, transaction.Connection);
            Assert.Equal(pending, DatabaseEvidence(f.Connection, transaction));
            Assert.Equal(changes, Changes(f.Connection, transaction));

            using CancellationTokenSource canceled = new();
            canceled.Cancel();
            OperationCanceledException failure = Assert.Throws<OperationCanceledException>(
                () => Read(f.Connection, transaction, merge, kind, ct: canceled.Token));
            Assert.Equal(canceled.Token, failure.CancellationToken);
            Assert.Same(f.Connection, transaction.Connection);
            Assert.Equal(pending, DatabaseEvidence(f.Connection, transaction));
            Assert.Equal(changes, Changes(f.Connection, transaction));
            AssertReadCount(Read(f.Connection, transaction, merge, kind), merge, kind, 1);
            transaction.Rollback(); // Proves the reader neither committed nor disposed our write.
        }
        Assert.Equal(empty, DatabaseEvidence(f.Connection));
        Assert.Null(Read(f.Connection, null, merge, kind));
        AssertFreshTransaction(f.Connection);

        // A different caller may still commit after a successful read.
        using (SqliteTransaction transaction = f.Connection.BeginTransaction())
        {
            var row = PersistedMediaFixture.Row(merge);
            Insert(f.Connection, transaction, merge, receipt,
                receipt ? PersistedMediaFixture.Receipt(merge, row) : row);
            AssertReadCount(Read(f.Connection, transaction, merge, kind), merge, kind, 1);
            transaction.Commit();
        }
        AssertReadCount(Read(f.Connection, null, merge, kind), merge, kind, 1);
        AssertFreshTransaction(f.Connection);
    }

    [Theory]
    [InlineData(false, "operation")]
    [InlineData(false, "receipt")]
    [InlineData(false, "page")]
    [InlineData(true, "operation")]
    [InlineData(true, "receipt")]
    [InlineData(true, "page")]
    public void EntryCancellationDoesNotOpenOrLeaveATransactionOrChangeEvidence(bool merge, string kind)
    {
        using PersistedMediaFixture f = new();
        var row = PersistedMediaFixture.Row(merge);
        if (kind == "receipt") f.InsertReceipt(merge, PersistedMediaFixture.Receipt(merge, row));
        else f.Insert(merge, row);
        string before = DatabaseEvidence(f.Connection);
        long changes = Changes(f.Connection);
        using CancellationTokenSource canceled = new();
        canceled.Cancel();
        for (int attempt = 0; attempt < 2; attempt++)
        {
            OperationCanceledException failure = Assert.Throws<OperationCanceledException>(
                () => Read(f.Connection, null, merge, kind, ct: canceled.Token));
            Assert.Equal(canceled.Token, failure.CancellationToken);
            AssertFreshTransaction(f.Connection);
            Assert.Equal(before, DatabaseEvidence(f.Connection));
            Assert.Equal(changes, Changes(f.Connection));
        }
        AssertReadCount(Read(f.Connection, null, merge, kind), merge, kind, 1);
        AssertFreshTransaction(f.Connection);
    }

    [Theory]
    [InlineData(false, "operation")]
    [InlineData(false, "receipt")]
    [InlineData(false, "page")]
    [InlineData(true, "operation")]
    [InlineData(true, "receipt")]
    [InlineData(true, "page")]
    public void VersionAwareEvidenceReadDoesNotRelaxLegacyGatesOrMutateDatabase(bool merge, string kind)
    {
        using PersistedMediaFixture f = new();
        var row = PersistedMediaFixture.Row(merge, state: "Committed");
        // Receipt cases intentionally remain orphaned historical evidence.
        if (kind == "receipt") f.InsertReceipt(merge, PersistedMediaFixture.Receipt(merge, row));
        else f.Insert(merge, row);
        string before = DatabaseEvidence(f.Connection);
        long changes = Changes(f.Connection);
        AssertReadCount(Read(f.Connection, null, merge, kind), merge, kind, 1);
        for (int attempt = 0; attempt < 2; attempt++)
        {
            Assert.Equal(LegacyGate, Assert.Throws<InvalidDataException>(() => ReadLegacy(f.Connection, merge, kind)).Message);
            Assert.True(ArchiveSql.HasUnsupportedMediaTransferRecords(f.Connection, null));
            AssertFreshTransaction(f.Connection);
        }
        AssertReadCount(Read(f.Connection, null, merge, kind), merge, kind, 1);
        Assert.Equal(before, DatabaseEvidence(f.Connection));
        Assert.Equal(changes, Changes(f.Connection));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void CallerWalSnapshotBindsHeaderAndRowDespiteCommittedWriterChange(bool merge, bool receipt)
    {
        using TempDirectory files = new();
        string path = Path.Combine(files.Path, "persisted-lifecycle.db");
        string connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private, Pooling = false
        }.ToString();
        using SqliteConnection reader = new(connectionString);
        reader.Open();
        Assert.Equal("wal", Scalar(reader, null, "PRAGMA journal_mode=WAL"));
        using (SqliteTransaction setup = reader.BeginTransaction())
        {
            ArchiveSql.CreateMediaTransferSchema10(reader, setup);
            Execute(reader, setup, "PRAGMA user_version=10");
            var row = PersistedMediaFixture.Row(merge);
            Insert(reader, setup, merge, receipt, receipt ? PersistedMediaFixture.Receipt(merge, row) : row);
            setup.Commit();
        }
        using SqliteConnection writer = new(connectionString);
        writer.Open();
        Assert.Equal("wal", Scalar(writer, null, "PRAGMA journal_mode"));
        string kind = receipt ? "receipt" : "operation";
        using (SqliteTransaction snapshot = reader.BeginTransaction(deferred: true))
        {
            Assert.Equal(10L, Scalar(reader, snapshot, "PRAGMA user_version"));
            // Explicit table read establishes the old snapshot, without sleeps/tasks/hooks.
            Assert.Equal(1L, Scalar(reader, snapshot, $"SELECT count(*) FROM {Table(merge, receipt)}"));
            string oldEvidence = DatabaseEvidence(reader, snapshot);
            using (SqliteTransaction write = writer.BeginTransaction())
            {
                Execute(writer, write, $"DELETE FROM {Table(merge, receipt)}");
                // Synthetic header transition, NOT a claim about a supported downgrade.
                Execute(writer, write, "PRAGMA user_version=9");
                write.Commit();
            }
            Assert.Equal(9L, Scalar(writer, null, "PRAGMA user_version"));
            Assert.Equal(0L, Scalar(writer, null, $"SELECT count(*) FROM {Table(merge, receipt)}"));
            string committedEvidence = DatabaseEvidence(writer);
            long changes = Changes(reader, snapshot);
            AssertReadCount(Read(reader, snapshot, merge, kind), merge, kind, 1);
            Assert.Equal(10L, Scalar(reader, snapshot, "PRAGMA user_version"));
            Assert.Equal(oldEvidence, DatabaseEvidence(reader, snapshot));
            Assert.Equal(committedEvidence, DatabaseEvidence(writer));
            Assert.Equal(changes, Changes(reader, snapshot));
            snapshot.Commit();
        }
        string after = DatabaseEvidence(reader);
        Assert.Throws<InvalidDataException>(() => Read(reader, null, merge, kind));
        AssertFreshTransaction(reader);
        Assert.Equal(after, DatabaseEvidence(reader));
        Execute(writer, null, "PRAGMA user_version=10");
        Assert.Null(Read(reader, null, merge, kind));
        AssertFreshTransaction(reader);
    }

    private static object? Read(SqliteConnection connection, SqliteTransaction? transaction, bool merge, string kind,
        Guid? operation = null, string? request = null, CancellationToken ct = default)
    {
        Guid op = operation ?? PersistedMediaFixture.OperationId;
        string hash = request ?? PersistedMediaFixture.Request;
        return (merge, kind) switch
        {
            (false, "operation") => ArchiveSql.ReadPersistedMediaImportOperation(connection, transaction, op, hash, ct),
            (true, "operation") => ArchiveSql.ReadPersistedMediaMergeOperation(connection, transaction, op, hash, ct),
            (false, "receipt") => ArchiveSql.ReadPersistedMediaImportReceipt(connection, transaction, op, hash, 2, ct),
            (true, "receipt") => ArchiveSql.ReadPersistedMediaMergeReceipt(connection, transaction, op, hash, 2, ct),
            (false, "page") when transaction is null => ArchiveSql.ReadPersistedMediaImportOperationsPage(connection, null, ct),
            (true, "page") when transaction is null => ArchiveSql.ReadPersistedMediaMergeOperationsPage(connection, null, ct),
            _ => throw new ArgumentException("Unexpected lifecycle reader variant.")
        };
    }

    private static object? ReadLegacy(SqliteConnection connection, bool merge, string kind)
        => (merge, kind) switch
        {
            (false, "operation") => ArchiveSql.ReadMediaImportOperation(connection, null, PersistedMediaFixture.OperationId, PersistedMediaFixture.Request),
            (true, "operation") => ArchiveSql.ReadMediaMergeOperation(connection, null, PersistedMediaFixture.OperationId, PersistedMediaFixture.Request),
            (false, "receipt") => ArchiveSql.ReadMediaImportReceipt(connection, null, PersistedMediaFixture.OperationId, PersistedMediaFixture.Request),
            (true, "receipt") => ArchiveSql.ReadMediaMergeReceipt(connection, null, PersistedMediaFixture.OperationId, PersistedMediaFixture.Request),
            (false, "page") => ArchiveSql.ReadMediaImportOperationsPage(connection, null),
            (true, "page") => ArchiveSql.ReadMediaMergeOperationsPage(connection, null),
            _ => throw new ArgumentException("Unexpected legacy reader variant.")
        };

    private static void AssertReadCount(object? result, bool merge, string kind, int expected)
    {
        if (kind == "page")
        {
            if (merge)
            {
                var page = Assert.IsType<NativeMediaPersistedPage<NativeMediaPersistedMergeOperation>>(result);
                Assert.Equal(expected, page.Items.Length);
                Assert.Null(page.NextOperationId);
            }
            else
            {
                var page = Assert.IsType<NativeMediaPersistedPage<NativeMediaPersistedImportOperation>>(result);
                Assert.Equal(expected, page.Items.Length);
                Assert.Null(page.NextOperationId);
            }
        }
        else if (expected == 0) Assert.Null(result);
        else Assert.IsType((merge, kind) switch
        {
            (false, "operation") => typeof(NativeMediaPersistedImportOperation),
            (true, "operation") => typeof(NativeMediaPersistedMergeOperation),
            (false, "receipt") => typeof(NativeMediaPersistedImportReceipt),
            (true, "receipt") => typeof(NativeMediaPersistedMergeReceipt),
            _ => throw new ArgumentException("Unexpected lifecycle result variant.")
        }, result);
    }

    private static string Table(bool merge, bool receipt)
        => $"archive_media_{(merge ? "merge" : "import")}_{(receipt ? "receipts" : "operations")}";

    private static void Insert(SqliteConnection connection, SqliteTransaction transaction, bool merge, bool receipt,
        Dictionary<string, object?> row)
    {
        // Names originate only in the shared fixture, values are always parameterized.
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        var columns = row.ToArray();
        command.CommandText = $"INSERT INTO {Table(merge, receipt)} ({string.Join(",", columns.Select(c => c.Key))}) VALUES ("
            + string.Join(",", columns.Select((_, i) => "$p" + i.ToString(CultureInfo.InvariantCulture))) + ")";
        for (int i = 0; i < columns.Length; i++)
            command.Parameters.AddWithValue("$p" + i.ToString(CultureInfo.InvariantCulture), columns[i].Value ?? DBNull.Value);
        Assert.Equal(1, command.ExecuteNonQuery());
    }

    private static void AssertFreshTransaction(SqliteConnection connection)
    {
        using SqliteTransaction probe = connection.BeginTransaction(deferred: true);
        Assert.Equal(1L, Scalar(connection, probe, "SELECT 1"));
        probe.Rollback();
    }

    private static long Changes(SqliteConnection connection, SqliteTransaction? transaction = null)
        => Assert.IsType<long>(Scalar(connection, transaction, "SELECT total_changes()"));

    private static object? Scalar(SqliteConnection connection, SqliteTransaction? transaction, string sql)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    private static void Execute(SqliteConnection connection, SqliteTransaction? transaction, string sql)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static string DatabaseEvidence(SqliteConnection connection, SqliteTransaction? transaction = null)
    {
        var values = new List<string?> { Convert.ToString(Scalar(connection, transaction, "PRAGMA user_version"), CultureInfo.InvariantCulture) };
        foreach (bool merge in new[] { false, true })
        foreach (bool receipt in new[] { false, true })
        {
            values.Add(Table(merge, receipt));
            using SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"SELECT rowid,* FROM {Table(merge, receipt)} ORDER BY operation_id";
            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                values.Add("row");
                for (int i = 0; i < reader.FieldCount; i++)
                {
                    object value = reader.GetValue(i);
                    values.Add(reader.GetName(i));
                    values.Add(value.GetType().FullName);
                    values.Add(value is DBNull ? null : value is byte[] blob ? Convert.ToHexString(blob)
                        : Convert.ToString(value, CultureInfo.InvariantCulture));
                }
            }
        }
        return JsonSerializer.Serialize(values);
    }
}
