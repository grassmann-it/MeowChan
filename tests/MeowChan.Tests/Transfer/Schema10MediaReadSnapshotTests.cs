using MeowChan.Archive;
using MeowChan.Transfer;
using Microsoft.Data.Sqlite;

namespace MeowChan.Tests.Transfer;

// SQL reader snapshot/ownership tests only. The fixture contains the real four
// schema10 media tables, not a complete ArchiveCatalog or a valid v2 execution plan.
// These tests do NOT reproduce migration between PRAGMA and SELECT.
// In the supported DELETE journal mode a writer cannot commit while a read
// snapshot is held: stage on a separate connection, read the old snapshot, release
// it, then commit and read again. No WAL switch, scheduling hook or timing loop.
public sealed class Schema10MediaReadSnapshotTests
{
    private static readonly Guid LegacyOperation = Guid.Parse("10000000-0000-4000-8000-000000000001");
    private static readonly Guid FutureOperation = Guid.Parse("20000000-0000-4000-8000-000000000002");
    private static readonly string Request = new('a', 64);
    private static readonly string RevisionKey = "att-v2:" + new string('b', 64);
    private const string VersionGate = "This media transfer plan requires version-aware reconciliation.";

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void OperationAndReceiptReadersKeepCallerSnapshotUntilWriterCommits(bool merge, bool receipt)
    {
        using TempDirectory files = new();
        string path = CreateDatabase(files.Path);
        using SqliteConnection reader = ArchiveSql.OpenConnection(path, SqliteOpenMode.ReadOnly);
        using SqliteConnection writer = ArchiveSql.OpenConnection(path, SqliteOpenMode.ReadWrite);
        using SqliteTransaction snapshot = reader.BeginTransaction(deferred: true);
        PinEmptySnapshot(reader, snapshot, merge, receipt);
        Assert.Null(ReadOne(reader, snapshot, merge, receipt, FutureOperation));

        using SqliteTransaction pending = writer.BeginTransaction();
        Seed(writer, pending, merge, receipt, FutureOperation, version: 2);
        Assert.Equal(1L, CountRows(writer, pending, merge, receipt));
        AssertVersionGate(() => ReadOne(writer, pending, merge, receipt, FutureOperation));
        // Even the error path must not dispose a transaction supplied by its caller.
        Assert.Equal(1L, CountRows(writer, pending, merge, receipt));

        Assert.Null(ReadOne(reader, snapshot, merge, receipt, FutureOperation));
        Assert.Equal(10, ArchiveSql.GetPragma(reader, "user_version", snapshot));
        Assert.Equal(0L, CountRows(reader, snapshot, merge, receipt));
        snapshot.Commit();
        pending.Commit();

        AssertVersionGate(() => ReadOne(reader, null, merge, receipt, FutureOperation));
        AssertCanBeginReadTransaction(reader, merge, receipt, expectedRows: 1);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void UnsupportedScanKeepsCallerSnapshotAndSeesCommittedEvidenceOnNextRead(bool merge, bool receipt)
    {
        using TempDirectory files = new();
        string path = CreateDatabase(files.Path);
        using SqliteConnection reader = ArchiveSql.OpenConnection(path, SqliteOpenMode.ReadOnly);
        using SqliteConnection writer = ArchiveSql.OpenConnection(path, SqliteOpenMode.ReadWrite);
        using SqliteTransaction snapshot = reader.BeginTransaction(deferred: true);
        PinEmptySnapshot(reader, snapshot, merge, receipt);
        Assert.False(ArchiveSql.HasUnsupportedMediaTransferRecords(reader, snapshot));

        using SqliteTransaction pending = writer.BeginTransaction();
        Seed(writer, pending, merge, receipt, FutureOperation, version: 2);
        Assert.True(ArchiveSql.HasUnsupportedMediaTransferRecords(writer, pending));
        Assert.False(ArchiveSql.HasUnsupportedMediaTransferRecords(reader, snapshot));
        Assert.Equal(0L, CountRows(reader, snapshot, merge, receipt));
        snapshot.Commit();
        pending.Commit();

        // The receipt variants deliberately have no corresponding operation row.
        Assert.True(ArchiveSql.HasUnsupportedMediaTransferRecords(reader, null));
        AssertCanBeginReadTransaction(reader, merge, receipt, expectedRows: 1);
    }

    [Theory]
    [InlineData(false, "operation")]
    [InlineData(false, "receipt")]
    [InlineData(false, "page")]
    [InlineData(true, "operation")]
    [InlineData(true, "receipt")]
    [InlineData(true, "page")]
    public void OwnedReadTransactionsAreReleasedAfterSuccessAndVersionGate(bool merge, string kind)
    {
        using TempDirectory files = new();
        string path = CreateDatabase(files.Path);
        bool receipt = kind == "receipt";
        using SqliteConnection reader = ArchiveSql.OpenConnection(path, SqliteOpenMode.ReadOnly);
        using SqliteConnection writer = ArchiveSql.OpenConnection(path, SqliteOpenMode.ReadWrite);

        Assert.False(ArchiveSql.HasUnsupportedMediaTransferRecords(reader, null));
        AssertCanBeginReadTransaction(reader, merge, receipt, expectedRows: 0);
        if (kind != "page")
        {
            Assert.Null(ReadOne(reader, null, merge, receipt, LegacyOperation));
            AssertCanBeginReadTransaction(reader, merge, receipt, expectedRows: 0);
        }
        using (SqliteTransaction write = writer.BeginTransaction())
        {
            Seed(writer, write, merge, receipt, LegacyOperation, version: 1);
            write.Commit();
        }
        AssertLegacyRead(ReadWithoutTransaction(reader, merge, kind, LegacyOperation), merge, kind);
        AssertCanBeginReadTransaction(reader, merge, receipt, expectedRows: 1);

        using (SqliteTransaction write = writer.BeginTransaction())
        {
            Seed(writer, write, merge, receipt, FutureOperation, version: 2);
            write.Commit();
        }
        AssertVersionGate(() => ReadWithoutTransaction(reader, merge, kind, FutureOperation));
        AssertCanBeginReadTransaction(reader, merge, receipt, expectedRows: 2);
        // A second failure must neither inherit an old transaction nor leave one behind.
        AssertVersionGate(() => ReadWithoutTransaction(reader, merge, kind, FutureOperation));
        AssertCanBeginReadTransaction(reader, merge, receipt, expectedRows: 2);
        if (receipt)
        {
            // An own historical v1 receipt is still readable despite unrelated v2 evidence.
            AssertLegacyRead(ReadWithoutTransaction(reader, merge, kind, LegacyOperation), merge, kind);
            AssertCanBeginReadTransaction(reader, merge, receipt, expectedRows: 2);
        }
    }

    private static string CreateDatabase(string directory)
    {
        string path = Path.Combine(directory, "media-read-snapshots.db");
        using SqliteConnection connection = ArchiveSql.OpenConnection(path, SqliteOpenMode.ReadWriteCreate);
        using (SqliteCommand journal = ArchiveSql.Command(connection, null, "PRAGMA journal_mode=DELETE"))
            Assert.Equal("delete", Assert.IsType<string>(journal.ExecuteScalar()));
        using SqliteTransaction transaction = connection.BeginTransaction();
        ArchiveSql.CreateMediaTransferSchema10(connection, transaction);
        using (SqliteCommand stamp = ArchiveSql.Command(connection, transaction, "PRAGMA user_version=10"))
            stamp.ExecuteNonQuery();
        Assert.Equal(0, ArchiveSql.GetPragma(connection, "ignore_check_constraints", transaction));
        transaction.Commit();
        return path;
    }

    private static void PinEmptySnapshot(SqliteConnection connection, SqliteTransaction transaction, bool merge, bool receipt)
    {
        Assert.Equal(10, ArchiveSql.GetPragma(connection, "user_version", transaction));
        // A real table read pins the snapshot; do not rely on BEGIN DEFERRED alone.
        Assert.Equal(0L, CountRows(connection, transaction, merge, receipt));
    }

    private static void AssertCanBeginReadTransaction(SqliteConnection connection, bool merge, bool receipt, long expectedRows)
    {
        using SqliteTransaction transaction = connection.BeginTransaction(deferred: true);
        Assert.Equal(10, ArchiveSql.GetPragma(connection, "user_version", transaction));
        Assert.Equal(expectedRows, CountRows(connection, transaction, merge, receipt));
        transaction.Commit();
    }

    private static string Table(bool merge, bool receipt)
        => $"archive_media_{(merge ? "merge" : "import")}_{(receipt ? "receipts" : "operations")}";

    private static long CountRows(SqliteConnection connection, SqliteTransaction transaction, bool merge, bool receipt)
    {
        using SqliteCommand command = ArchiveSql.Command(connection, transaction, $"SELECT COUNT(*) FROM {Table(merge, receipt)}");
        return Assert.IsType<long>(command.ExecuteScalar());
    }

    private static object? ReadOne(SqliteConnection connection, SqliteTransaction? transaction, bool merge, bool receipt, Guid operation)
        => receipt
            ? merge ? ArchiveSql.ReadMediaMergeReceipt(connection, transaction, operation, Request)
                : ArchiveSql.ReadMediaImportReceipt(connection, transaction, operation, Request)
            : merge ? ArchiveSql.ReadMediaMergeOperation(connection, transaction, operation, Request)
                : ArchiveSql.ReadMediaImportOperation(connection, transaction, operation, Request);

    private static object? ReadWithoutTransaction(SqliteConnection connection, bool merge, string kind, Guid operation)
        => kind == "page"
            ? merge ? ArchiveSql.ReadMediaMergeOperationsPage(connection, null)
                : ArchiveSql.ReadMediaImportOperationsPage(connection, null)
            : ReadOne(connection, null, merge, kind == "receipt", operation);

    private static void AssertVersionGate(Func<object?> read)
    {
        InvalidDataException error = Assert.Throws<InvalidDataException>(() => { _ = read(); });
        Assert.Equal(VersionGate, error.Message);
        Assert.Null(error.InnerException);
    }

    private static void AssertLegacyRead(object? value, bool merge, string kind)
    {
        if (kind == "receipt")
        {
            if (merge)
                Assert.Equal(new NativeMediaMergeResult(NativeMediaImportCommitStatus.Committed, 2, 0, 1, 0, 0, IsReplay: true),
                    Assert.IsType<NativeMediaMergeResult>(value));
            else
                Assert.Equal(new NativeMediaImportCommitResult(NativeMediaImportCommitStatus.Committed, 1, 2, 1, IsReplay: true),
                    Assert.IsType<NativeMediaImportCommitResult>(value));
        }
        else if (merge)
        {
            NativeMediaMergeOperation operation = kind == "page"
                ? Assert.Single(Assert.IsType<NativeMediaMergeOperationPage>(value).Items)
                : Assert.IsType<NativeMediaMergeOperation>(value);
            Assert.Equal(LegacyOperation, operation.OperationId);
            Assert.Equal(RevisionKey, Assert.Single(operation.FullKeys));
        }
        else
        {
            NativeMediaImportOperation operation = kind == "page"
                ? Assert.Single(Assert.IsType<NativeMediaImportOperationPage>(value).Items)
                : Assert.IsType<NativeMediaImportOperation>(value);
            Assert.Equal(LegacyOperation, operation.OperationId);
            Assert.Equal(1, operation.AttachmentCount);
        }
    }

    private static void Seed(SqliteConnection connection, SqliteTransaction transaction, bool merge, bool receipt, Guid operation, int version)
    {
        // Only storage-valid Preparing rows and content-free receipts are needed.
        // The schema9 fixture uses empty v1 merge keys, unsuitable for a positive
        // merge-reader control; seed one decodable v1 key here instead.
        string columns = merge ? "new_posts,updated_posts,new_attachments,new_bindings,conflicts" : "post_count,attachment_count";
        string values = merge ? "2,0,1,0,0" : "2,1";
        if (!receipt)
        {
            columns += ",package_fingerprint,catalog_instance,catalog_generation,source,board,thread,operations_parent,imported_at,state,full_selection_json,full_selection_sha256,full_member_count,catalog_bytes";
            values += ",$hash,$instance,0,'fixture','b','snapshot-thread',$parent,'2026-11-08T00:00:00.0000000+00:00','Preparing',$selection,$selectionHash,$members,$catalog";
            columns += merge ? ",roots_json,full_keys_json,safe_keys_json" : ",revision_keys_json";
            values += merge ? ",'[]',$keys,'[]'" : ",$keys";
        }
        using SqliteCommand command = ArchiveSql.Command(connection, transaction, $"""
            INSERT INTO {Table(merge, receipt)}(operation_id,request_sha256,plan_version,{columns})
            VALUES($op,$request,$version,{values})
            """);
        command.Parameters.AddWithValue("$op", operation.ToString("D"));
        command.Parameters.AddWithValue("$request", Request);
        command.Parameters.AddWithValue("$version", version);
        if (!receipt)
        {
            command.Parameters.AddWithValue("$hash", Request);
            command.Parameters.AddWithValue("$instance", "30000000-0000-4000-8000-000000000003");
            command.Parameters.AddWithValue("$parent", Path.Combine(Path.GetDirectoryName(connection.DataSource)!, "never-created", operation.ToString("N")));
            command.Parameters.AddWithValue("$keys", version == 2 ? "[]" : "[\"" + RevisionKey + "\"]");
            command.Parameters.AddWithValue("$selection", version == 2 ? "{}" : DBNull.Value);
            command.Parameters.AddWithValue("$selectionHash", version == 2 ? Request : DBNull.Value);
            command.Parameters.AddWithValue("$members", version == 2 ? 1 : 0);
            command.Parameters.AddWithValue("$catalog", merge || version == 2 ? new byte[] { 0x7b, 0x7d } : DBNull.Value);
        }
        Assert.Equal(1, command.ExecuteNonQuery());
    }
}
