using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using MeowChan.Archive;
using MeowChan.Persistence;
using MeowChan.Testing;
using Microsoft.Data.Sqlite;

namespace MeowChan.Tests.Transfer;

/// <summary>
/// Contract tests for the caller-owned, media-only schema-9 to schema-10 migration.
/// The production API under test is intentionally not wrapped or emulated here.
/// </summary>
public sealed class Schema10MediaOperationsMigrationTests
{
    private const string Import = "archive_media_import_operations";
    private const string ImportReceipt = "archive_media_import_receipts";
    private const string Merge = "archive_media_merge_operations";
    private const string MergeReceipt = "archive_media_merge_receipts";
    private const string ImportId = "00000000-0000-4000-8000-000000000001";
    private const string MergeId = "10000000-0000-4000-8000-000000000001";
    private static readonly string[] MediaTables = [Import, ImportReceipt, Merge, MergeReceipt];
    private static readonly string[] Checkpoints = ["created", "copied", "swapped"];
    // SQL shape tests only: decoding/semantic selection validation belongs to the later v2 reader.
    private const string Selection = "{\"Members\":[{}]}";

    [Fact]
    public void Fresh10HasTheExactTransferShapeAndLeavesPragmasForTheCaller()
    {
        using SyntheticDatabase database = new(seedSchema9: false);
        using SqliteConnection connection = database.Open(SqliteOpenMode.ReadWrite);
        using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);

        ArchiveSql.CreateMediaTransferSchema10(connection, transaction);
        ArchiveSql.ValidateMediaTransferSchema10(connection, transaction);

        Assert.Equal(0, ArchiveSql.GetPragma(connection, "user_version", transaction));
        Assert.Equal(0, ArchiveSql.GetPragma(connection, "application_id", transaction));
        Assert.Equal(
            new[]
            {
                "archive_media_import_operations", "archive_media_import_receipts",
                "archive_media_merge_operations", "archive_media_merge_receipts"
            },
            TableNames(connection, transaction));

        AssertColumns(connection, transaction, "archive_media_import_operations",
            "plan_version", "full_selection_json", "full_selection_sha256", "full_inventory_sha256",
            "full_member_count", "catalog_bytes", "evidence_additions", "derived_descriptor_additions",
            "contained_derived_additions", "original_binding_count");
        AssertColumns(connection, transaction, "archive_media_import_receipts",
            "plan_version", "evidence_additions", "derived_descriptor_additions",
            "contained_derived_additions", "original_binding_count");
        AssertColumns(connection, transaction, "archive_media_merge_operations",
            "plan_version", "full_selection_json", "full_selection_sha256", "safe_selection_json",
            "safe_selection_sha256", "full_inventory_sha256", "safe_inventory_sha256", "full_member_count",
            "safe_member_count", "evidence_additions", "derived_descriptor_additions",
            "contained_derived_additions");
        AssertColumns(connection, transaction, "archive_media_merge_receipts",
            "plan_version", "evidence_additions", "derived_descriptor_additions",
            "contained_derived_additions");
    }

    [Fact]
    public void MigrationPreservesEveryV1StateAndOrphanReceiptExactly()
    {
        using SyntheticDatabase database = new();
        using SqliteConnection connection = database.Open(SqliteOpenMode.ReadWrite);
        using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);
        Schema9MediaOperationsFixture.TableSnapshot[] before =
            Schema9MediaOperationsFixture.ReadSnapshot(connection, transaction);
        string[] protectedObjects = ProtectedObjectSnapshot(connection, transaction);
        string[] identities = RowIdentitySnapshot(connection, transaction);

        ArchiveSql.MigrateMediaTransferSchema10(connection, transaction, CancellationToken.None);
        ArchiveSql.ValidateMediaTransferSchema10(connection, transaction);

        Assert.Equal(9, ArchiveSql.GetPragma(connection, "user_version", transaction));
        Assert.Equal(ArchiveSql.ApplicationId, ArchiveSql.GetPragma(connection, "application_id", transaction));
        AssertSnapshotsEqual(before, Schema9MediaOperationsFixture.ReadSnapshot(connection, transaction));
        Assert.Equal(protectedObjects, ProtectedObjectSnapshot(connection, transaction));
        Assert.Equal(identities, RowIdentitySnapshot(connection, transaction));
        Assert.Equal(new[] { "Preparing", "Prepared", "Committed" },
            Values(connection, transaction, "SELECT state FROM archive_media_import_operations ORDER BY operation_id"));
        Assert.Equal(new[] { "Preparing", "Prepared", "Committed" },
            Values(connection, transaction, "SELECT state FROM archive_media_merge_operations ORDER BY operation_id"));
        Assert.Equal(2L, Scalar(connection, transaction, "SELECT count(*) FROM archive_media_import_receipts"));
        Assert.Equal(2L, Scalar(connection, transaction, "SELECT count(*) FROM archive_media_merge_receipts"));
        Assert.Equal(1L, Scalar(connection, transaction,
            "SELECT count(*) FROM archive_media_import_receipts WHERE operation_id='20000000-0000-4000-8000-000000000001'"));
        Assert.Equal(1L, Scalar(connection, transaction,
            "SELECT count(*) FROM archive_media_merge_receipts WHERE operation_id='30000000-0000-4000-8000-000000000001'"));
        AssertV1Defaults(connection, transaction);

        // Version and application identity are deliberately not part of this API's commit.
        transaction.Commit();
        using SqliteConnection reopened = database.Open(SqliteOpenMode.ReadOnly);
        Assert.Equal(9, ArchiveSql.GetPragma(reopened, "user_version"));
        Assert.Equal(ArchiveSql.ApplicationId, ArchiveSql.GetPragma(reopened, "application_id"));
        using SqliteTransaction readTransaction = reopened.BeginTransaction(deferred: true);
        ArchiveSql.ValidateMediaTransferSchema10(reopened, readTransaction);
    }

    [Fact]
    public void CallerMayStampVersion10AfterTheMigrationAndReopenIsANoop()
    {
        using SyntheticDatabase database = new();
        MigrateAndStamp(database);
        byte[] beforeReopen = File.ReadAllBytes(database.Path);

        using (SqliteConnection connection = database.Open(SqliteOpenMode.ReadWrite))
        using (SqliteTransaction transaction = connection.BeginTransaction(deferred: false))
        {
            ArchiveSql.MigrateMediaTransferSchema10(connection, transaction, CancellationToken.None);
            ArchiveSql.ValidateMediaTransferSchema10(connection, transaction);
            Assert.Equal(10, ArchiveSql.GetPragma(connection, "user_version", transaction));
            transaction.Commit();
        }

        Assert.Equal(beforeReopen, File.ReadAllBytes(database.Path));
    }

    [Theory]
    [InlineData("created")]
    [InlineData("copied")]
    [InlineData("swapped")]
    public void CheckpointFaultRollsBackAllFourShadowTables(string failingCheckpoint)
    {
        using SyntheticDatabase database = new();
        byte[] beforeBytes = File.ReadAllBytes(database.Path);
        Schema9MediaOperationsFixture.TableSnapshot[] before = Snapshot(database);
        string[] beforeObjects = ProtectedObjectSnapshot(database);
        var checkpoints = new List<string>();

        using (SqliteConnection connection = database.Open(SqliteOpenMode.ReadWrite))
        using (SqliteTransaction transaction = connection.BeginTransaction(deferred: false))
        {
            IOException error = Assert.Throws<IOException>(() => ArchiveSql.MigrateMediaTransferSchema10(
                connection, transaction, CancellationToken.None,
                checkpoint: checkpoint =>
                {
                    checkpoints.Add(checkpoint);
                    AssertCheckpointShape(connection, transaction, checkpoint);
                    if (checkpoint == failingCheckpoint)
                        throw new IOException("synthetic schema10 checkpoint fault: " + checkpoint);
                }));
            Assert.Contains(failingCheckpoint, error.Message);
            Assert.Equal(Checkpoints.Take(Array.IndexOf(Checkpoints, failingCheckpoint) + 1), checkpoints);
            transaction.Rollback();
        }

        Assert.Equal(beforeBytes, File.ReadAllBytes(database.Path));
        AssertSnapshotsEqual(before, Snapshot(database));
        Assert.Equal(beforeObjects, ProtectedObjectSnapshot(database));
        Assert.Equal(0L, Scalar(database, "SELECT count(*) FROM sqlite_master WHERE name LIKE '%_schema10'"));
    }

    [Theory]
    [InlineData("created")]
    [InlineData("copied")]
    [InlineData("swapped")]
    public void CancellationRollsBackAfterTheCheckpointCallback(string cancelAt)
    {
        using SyntheticDatabase database = new();
        byte[] beforeBytes = File.ReadAllBytes(database.Path);
        Schema9MediaOperationsFixture.TableSnapshot[] before = Snapshot(database);
        using CancellationTokenSource cancellation = new();
        bool callbackReached = false;

        using (SqliteConnection connection = database.Open(SqliteOpenMode.ReadWrite))
        using (SqliteTransaction transaction = connection.BeginTransaction(deferred: false))
        {
            Assert.Throws<OperationCanceledException>(() => ArchiveSql.MigrateMediaTransferSchema10(
                connection, transaction, cancellation.Token,
                checkpoint: checkpoint =>
                {
                    if (checkpoint == cancelAt)
                    {
                        callbackReached = true;
                        cancellation.Cancel();
                    }
                }));
            Assert.True(callbackReached);
            transaction.Rollback();
        }

        Assert.Equal(beforeBytes, File.ReadAllBytes(database.Path));
        AssertSnapshotsEqual(before, Snapshot(database));
        Assert.Equal(9, Pragma(database, "user_version"));
        Assert.Equal(ArchiveSql.ApplicationId, Pragma(database, "application_id"));
        Assert.Equal(0L, Scalar(database, "SELECT count(*) FROM sqlite_master WHERE name LIKE '%_schema10'"));
    }

    [Theory]
    [InlineData(Import)]
    [InlineData(ImportReceipt)]
    [InlineData(Merge)]
    [InlineData(MergeReceipt)]
    public void ShadowNameCollisionRejectsWithoutTouchingTheLegacyDatabase(string table)
    {
        using SyntheticDatabase database = new();
        using (SqliteConnection connection = database.Open(SqliteOpenMode.ReadWrite))
        {
            Execute(connection, $"CREATE TABLE {table}_schema10 (sentinel TEXT NOT NULL)");
            Execute(connection, $"INSERT INTO {table}_schema10 VALUES ('keep-me')");
        }

        byte[] beforeBytes = File.ReadAllBytes(database.Path);
        Schema9MediaOperationsFixture.TableSnapshot[] before = Snapshot(database);
        string[] beforeObjects = ProtectedObjectSnapshot(database);
        string[] beforeShadowObjects = ShadowObjectSnapshot(database);

        using (SqliteConnection connection = database.Open(SqliteOpenMode.ReadWrite))
        using (SqliteTransaction transaction = connection.BeginTransaction(deferred: false))
        {
            Assert.Throws<InvalidDataException>(() => ArchiveSql.MigrateMediaTransferSchema10(
                connection, transaction, CancellationToken.None));
            transaction.Rollback();
        }

        Assert.Equal(beforeBytes, File.ReadAllBytes(database.Path));
        AssertSnapshotsEqual(before, Snapshot(database));
        Assert.Equal(beforeObjects, ProtectedObjectSnapshot(database));
        Assert.Equal(beforeShadowObjects, ShadowObjectSnapshot(database));
        Assert.Equal("keep-me", Text(database, $"SELECT sentinel FROM {table}_schema10"));
    }

    [Fact]
    public void InvalidLegacyRowIsRejectedAndRolledBackWithoutRepair()
    {
        using SyntheticDatabase database = new();
        using (SqliteConnection connection = database.Open(SqliteOpenMode.ReadWrite))
        {
            Execute(connection, "PRAGMA ignore_check_constraints=ON");
            Execute(connection, "UPDATE archive_media_import_operations SET state='Corrupt' WHERE operation_id='00000000-0000-4000-8000-000000000001'");
            Execute(connection, "PRAGMA ignore_check_constraints=OFF");
        }

        byte[] beforeBytes = File.ReadAllBytes(database.Path);
        Schema9MediaOperationsFixture.TableSnapshot[] before = Snapshot(database);
        using (SqliteConnection connection = database.Open(SqliteOpenMode.ReadWrite))
        using (SqliteTransaction transaction = connection.BeginTransaction(deferred: false))
        {
            Exception? error = Record.Exception(() => ArchiveSql.MigrateMediaTransferSchema10(
                connection, transaction, CancellationToken.None));
            Assert.NotNull(error);
            Assert.True(error is InvalidDataException or SqliteException { SqliteErrorCode: 19 },
                $"Expected invalid legacy data rejection, received {error.GetType().Name}.");
            transaction.Rollback();
        }

        Assert.Equal(beforeBytes, File.ReadAllBytes(database.Path));
        AssertSnapshotsEqual(before, Snapshot(database));
        Assert.Equal("Corrupt", Text(database,
            "SELECT state FROM archive_media_import_operations WHERE operation_id='00000000-0000-4000-8000-000000000001'"));
    }

    [Theory]
    [InlineData(Import)] [InlineData(ImportReceipt)]
    [InlineData(Merge)] [InlineData(MergeReceipt)]
    public void Schema10ValidationRejectsAWeakenedCheckWithoutRepair(string table)
    {
        using SyntheticDatabase database = new(seedSchema9: false);
        using (SqliteConnection connection = database.Open(SqliteOpenMode.ReadWrite))
        using (SqliteTransaction transaction = connection.BeginTransaction(deferred: false))
        {
            ArchiveSql.CreateMediaTransferSchema10(connection, transaction);
            transaction.Commit();
        }

        string weakened;
        using (SqliteConnection connection = database.Open(SqliteOpenMode.ReadWrite))
        {
            string original = Text(connection,
                $"SELECT sql FROM sqlite_master WHERE type='table' AND name='{table}'");
            weakened = RemoveFirstCheck(original);
            Assert.NotEqual(original, weakened);
            Execute(connection, $"DROP TABLE {table}");
            Execute(connection, weakened);
        }
        byte[] beforeBytes = File.ReadAllBytes(database.Path);
        using (SqliteConnection read = database.Open(SqliteOpenMode.ReadOnly))
        using (SqliteTransaction transaction = read.BeginTransaction(deferred: true))
        {
            Assert.Throws<InvalidDataException>(
                () => ArchiveSql.ValidateMediaTransferSchema10(read, transaction));
            Assert.Equal(weakened, Text(read,
                $"SELECT sql FROM sqlite_master WHERE type='table' AND name='{table}'", transaction));
        }
        Assert.Equal(beforeBytes, File.ReadAllBytes(database.Path));
    }

    [Fact]
    public void V9SimplyRestampedTo10IsNotAValidSchema10()
    {
        using SyntheticDatabase database = new();
        using (SqliteConnection connection = database.Open(SqliteOpenMode.ReadWrite))
            Execute(connection, "PRAGMA user_version=10");

        byte[] before = File.ReadAllBytes(database.Path);
        using (SqliteConnection read = database.Open(SqliteOpenMode.ReadOnly))
        using (SqliteTransaction transaction = read.BeginTransaction(deferred: true))
        {
            Assert.Throws<InvalidDataException>(() => ArchiveSql.ValidateMediaTransferSchema10(read, transaction));
            Assert.Throws<InvalidDataException>(() => ArchiveSql.ValidateSchema(read, transaction));
        }
        Assert.Equal(before, File.ReadAllBytes(database.Path));
    }

    [Fact]
    public void UnknownV12IsRejectedWithoutMutation()
    {
        using SyntheticDatabase database = new();
        using (SqliteConnection connection = database.Open(SqliteOpenMode.ReadWrite))
            Execute(connection, "PRAGMA user_version=12");
        byte[] beforeBytes = File.ReadAllBytes(database.Path);

        using (SqliteConnection connection = database.Open(SqliteOpenMode.ReadWrite))
        using (SqliteTransaction transaction = connection.BeginTransaction(deferred: false))
        {
            Assert.Throws<InvalidDataException>(() => ArchiveSql.ValidateSchema(connection, transaction));
            Assert.Throws<InvalidDataException>(() => ArchiveSql.ValidateTargetSchema(connection, transaction, 12));
            Assert.Throws<InvalidDataException>(() => ArchiveSql.MigrateMediaTransferSchema10(
                connection, transaction, CancellationToken.None));
            transaction.Rollback();
        }

        Assert.Equal(beforeBytes, File.ReadAllBytes(database.Path));
        Assert.Equal(12, Pragma(database, "user_version"));
    }

    [Fact]
    public void SuccessfulMigrationStillRollsBackWhenTheCallerDoesNotCommit()
    {
        using SyntheticDatabase database = new();
        byte[] before = File.ReadAllBytes(database.Path);
        using (SqliteConnection connection = database.Open(SqliteOpenMode.ReadWrite))
        using (SqliteTransaction transaction = connection.BeginTransaction(deferred: false))
        {
            var checkpoints = new List<string>();
            ArchiveSql.MigrateMediaTransferSchema10(connection, transaction, checkpoint: checkpoints.Add);
            Assert.Equal(Checkpoints, checkpoints);
            ArchiveSql.ValidateMediaTransferSchema10(connection, transaction);
            Assert.Equal(9, ArchiveSql.GetPragma(connection, "user_version", transaction));
            // Dispose, rather than a migration-owned commit, owns rollback here.
        }
        Assert.Equal(before, File.ReadAllBytes(database.Path));
    }

    [Fact]
    public void PreCanceledMigrationDoesNotCreateShadowObjectsOrInvokeCheckpoints()
    {
        using SyntheticDatabase database = new();
        byte[] before = File.ReadAllBytes(database.Path);
        using (SqliteConnection connection = database.Open(SqliteOpenMode.ReadWrite))
        using (SqliteTransaction transaction = connection.BeginTransaction(deferred: false))
        {
            var checkpoints = new List<string>();
            Assert.Throws<OperationCanceledException>(() => ArchiveSql.MigrateMediaTransferSchema10(
                connection, transaction, new CancellationToken(true), checkpoints.Add));
            Assert.Empty(checkpoints);
        }
        Assert.Equal(before, File.ReadAllBytes(database.Path));
    }

    [Theory]
    [InlineData(Import, false)] [InlineData(Import, true)]
    [InlineData(ImportReceipt, false)] [InlineData(ImportReceipt, true)]
    [InlineData(Merge, false)] [InlineData(Merge, true)]
    [InlineData(MergeReceipt, false)] [InlineData(MergeReceipt, true)]
    public void UserIndexesAndTriggersAreRejectedRatherThanSilentlyDropped(string table, bool trigger)
    {
        using SyntheticDatabase database = new();
        using (SqliteConnection connection = database.Open(SqliteOpenMode.ReadWrite))
            Execute(connection, trigger
                ? $"CREATE TRIGGER user_media_object AFTER INSERT ON {table} BEGIN SELECT 1; END"
                : $"CREATE INDEX user_media_object ON {table}(request_sha256)");
        byte[] before = File.ReadAllBytes(database.Path);
        using (SqliteConnection connection = database.Open(SqliteOpenMode.ReadWrite))
        using (SqliteTransaction transaction = connection.BeginTransaction(deferred: false))
            Assert.Throws<InvalidDataException>(() => ArchiveSql.MigrateMediaTransferSchema10(connection, transaction));
        Assert.Equal(before, File.ReadAllBytes(database.Path));
        Assert.Equal(1L, Scalar(database, "SELECT count(*) FROM sqlite_master WHERE name='user_media_object'"));
    }

    [Fact]
    public void LegacyStorageClassesAndNonCanonicalBytesAreNotNormalizedDuringCopy()
    {
        using SyntheticDatabase database = new();
        using SqliteConnection connection = database.Open(SqliteOpenMode.ReadWrite);
        using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);
        // These values are permitted by the frozen v9 DDL. A v1 migration must not
        // silently decode/reencode JSON, coerce REAL to INTEGER, or cast BLOB to TEXT.
        Execute(connection, "UPDATE archive_media_import_operations SET revision_keys_json=X'5B205D',post_count=1.25", transaction);
        Execute(connection, "UPDATE archive_media_merge_operations SET catalog_bytes=X'007B7DFF',full_keys_json=X'5B205D',updated_posts=0.5", transaction);
        Execute(connection, "UPDATE archive_media_import_receipts SET post_count=2.5", transaction);
        Execute(connection, "UPDATE archive_media_merge_receipts SET new_posts=3.5", transaction);
        var before = Schema9MediaOperationsFixture.ReadSnapshot(connection, transaction);
        ArchiveSql.MigrateMediaTransferSchema10(connection, transaction);
        ArchiveSql.ValidateMediaTransferSchema10(connection, transaction);
        AssertSnapshotsEqual(before, Schema9MediaOperationsFixture.ReadSnapshot(connection, transaction));
    }

    [Theory]
    [InlineData("Preparing", false)] [InlineData("Preparing", true)]
    [InlineData("Prepared", false)] [InlineData("Prepared", true)]
    [InlineData("Committed", false)] [InlineData("Committed", true)]
    public void V2AllowsEveryStateAndNonemptySafeWithoutOriginalBindings(string state, bool nonemptySafe)
    {
        using SyntheticDatabase database = new();
        MigrateAndStamp(database);
        using SqliteConnection connection = database.Open(SqliteOpenMode.ReadWrite);
        using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);
        PromoteToV2(connection, transaction, state, nonemptySafe);
        ArchiveSql.ValidateMediaTransferSchema10(connection, transaction);
        Assert.Equal(state, Text(connection, $"SELECT state FROM {Import} WHERE operation_id='{ImportId}'", transaction));
        Assert.Equal(state, Text(connection, $"SELECT state FROM {Merge} WHERE operation_id='{MergeId}'", transaction));
        Assert.Equal(0L, Scalar(connection, transaction, $"SELECT attachment_count FROM {Import} WHERE operation_id='{ImportId}'"));
        Assert.Equal(0L, Scalar(connection, transaction, $"SELECT new_bindings FROM {Merge} WHERE operation_id='{MergeId}'"));
        Assert.Equal(nonemptySafe ? 1L : 0L,
            Scalar(connection, transaction, $"SELECT safe_member_count FROM {Merge} WHERE operation_id='{MergeId}'"));
        if (!nonemptySafe)
            Assert.Equal(1L, Scalar(connection, transaction, $"""
                SELECT count(*) FROM {Merge} WHERE operation_id='{MergeId}'
                AND safe_selection_json IS NULL AND safe_selection_sha256 IS NULL
                AND safe_inventory_sha256 IS NULL AND safe_directory IS NULL AND safe_manifest_generation IS NULL
                """));
    }

    public static IEnumerable<object[]> InvalidV2Rows()
    {
        foreach (string table in MediaTables)
        {
            foreach (string value in new[] { "NULL", "0", "3", "1.5", "'unknown'" })
                yield return [table, "plan_version=" + value];
            foreach (string counter in table.StartsWith("archive_media_import_", StringComparison.Ordinal)
                ? new[] { "evidence_additions", "derived_descriptor_additions", "contained_derived_additions", "original_binding_count" }
                : new[] { "evidence_additions", "derived_descriptor_additions", "contained_derived_additions" })
                foreach (string value in new[] { "NULL", "-1", "2147483648", "0.5", "'bad'" })
                    yield return [table, counter + "=" + value];
            yield return [table, "contained_derived_additions=2,derived_descriptor_additions=1"];
        }
        foreach (string table in new[] { Import, Merge })
        {
            foreach (string field in new[] { "full_selection_json", "full_selection_sha256", "catalog_bytes" })
                yield return [table, field + "=NULL"];
            foreach (string value in new[] { "0", "-1", "100001", "0.5", "'bad'" })
                yield return [table, "full_member_count=" + value];
            foreach (string field in new[] { "full_selection_sha256", "full_inventory_sha256" })
                foreach (string value in new[] { "'short'", "'" + new string('A', 64) + "'", "'" + new string('g', 64) + "'", "X'00'" })
                    yield return [table, field + "=" + value];
            yield return [table, "full_selection_json=X'5B5D'"];
            yield return [table, "full_selection_json=''"];
            yield return [table, "catalog_bytes='{}'"];
        }
        foreach (string field in new[] { "candidate_directory", "manifest_generation", "full_inventory_sha256" })
            yield return [Import, field + "=NULL"];
        yield return [Import, "attachment_count=-1"];
        yield return [Import, "revision_keys_json='[1]'"];
        yield return [Import, "bindings_sha256='" + Schema9MediaOperationsFixture.Hash + "'"];
        yield return [ImportReceipt, "attachment_count=-1"];
        foreach (string field in new[] { "full_directory", "full_manifest_generation", "full_inventory_sha256",
            "safe_directory", "safe_manifest_generation", "safe_inventory_sha256", "safe_selection_json", "safe_selection_sha256" })
            yield return [Merge, field + "=NULL"];
        foreach (string value in new[] { "-1", "100001", "0.5", "'bad'" })
            yield return [Merge, "safe_member_count=" + value];
        yield return [Merge, "safe_member_count=0"]; // Existing Safe fields must disappear with empty Safe.
        yield return [Merge, "full_keys_json='[1]'"];
        yield return [Merge, "safe_keys_json=' [ ]'"]; // v2 uses exactly [], not normalized legacy keys.
        yield return [Merge, "full_bindings_sha256='" + Schema9MediaOperationsFixture.Hash + "'"];
        yield return [Merge, "safe_bindings_sha256='" + Schema9MediaOperationsFixture.Hash + "'"];
    }

    [Theory]
    [MemberData(nameof(InvalidV2Rows))]
    public void Schema10RejectsInvalidV2TypesBoundsAndRequiredReadyFields(string table, string assignment)
    {
        using SyntheticDatabase database = new();
        MigrateAndStamp(database);
        using SqliteConnection connection = database.Open(SqliteOpenMode.ReadWrite);
        using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);
        PromoteToV2(connection, transaction, "Prepared", nonemptySafe: true);
        // Receipt rows remain content-free and can have no operation, including v2 receipts.
        Execute(connection, $"UPDATE {ImportReceipt} SET plan_version=2,attachment_count=0", transaction);
        Execute(connection, $"UPDATE {MergeReceipt} SET plan_version=2", transaction);
        ArchiveSql.ValidateMediaTransferSchema10(connection, transaction);
        string[] before = ReadCells(connection, transaction, $"SELECT * FROM {table} ORDER BY operation_id");
        string id = table == Import ? ImportId : MergeId;
        string filter = table.EndsWith("_operations", StringComparison.Ordinal) ? $" WHERE operation_id='{id}'" : "";
        SqliteException error = Assert.Throws<SqliteException>(() => Execute(connection,
            $"UPDATE {table} SET {assignment}{filter}", transaction));
        Assert.Equal(19, error.SqliteErrorCode);
        Assert.Equal(before, ReadCells(connection, transaction, $"SELECT * FROM {table} ORDER BY operation_id"));
    }

    [Theory]
    [InlineData(Import)] [InlineData(ImportReceipt)]
    public void V1StillRequiresAnAttachmentWhileV2ReceiptsMayHaveZero(string table)
    {
        using SyntheticDatabase database = new();
        MigrateAndStamp(database);
        using SqliteConnection connection = database.Open(SqliteOpenMode.ReadWrite);
        using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);
        Assert.Equal(19, Assert.Throws<SqliteException>(() => Execute(connection,
            $"UPDATE {table} SET attachment_count=0", transaction)).SqliteErrorCode);
        if (table == ImportReceipt)
        {
            Execute(connection, $"UPDATE {table} SET plan_version=2,attachment_count=0", transaction);
            Assert.Equal(2L, Scalar(connection, transaction, $"SELECT count(*) FROM {table} WHERE attachment_count=0"));
            Assert.Equal(0L, Scalar(connection, transaction, $"SELECT count(*) FROM pragma_foreign_key_list('{table}')"));
        }
    }

    [Theory]
    [InlineData(Import, "full_selection_json", false)]
    [InlineData(Merge, "full_selection_json", false)]
    [InlineData(Merge, "safe_selection_json", false)]
    [InlineData(Import, "catalog_bytes", true)]
    [InlineData(Merge, "catalog_bytes", true)]
    public void ByteLimitsAcceptTheBoundaryAndRejectOneByteBeyondIt(string table, string column, bool blob)
    {
        using SyntheticDatabase database = new();
        MigrateAndStamp(database);
        using SqliteConnection connection = database.Open(SqliteOpenMode.ReadWrite);
        using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);
        PromoteToV2(connection, transaction, "Prepared", nonemptySafe: true);
        int limit = blob ? 16 * 1024 * 1024 : 8 * 1024 * 1024;
        // Multibyte text proves the limit is UTF-8 bytes, not SQLite character count.
        string document = "[\"" + new string('é', (limit - 4) / 2) + "\"]";
        Assert.Equal(limit, Encoding.UTF8.GetByteCount(document));
        object boundary = blob ? new byte[limit] : document;
        object excessive = blob ? new byte[limit + 1] : document + " ";
        using SqliteCommand command = ArchiveSql.Command(connection, transaction,
            $"UPDATE {table} SET {column}=$value WHERE operation_id='{(table == Import ? ImportId : MergeId)}'");
        command.Parameters.AddWithValue("$value", boundary);
        Assert.Equal(1, command.ExecuteNonQuery());
        ArchiveSql.ValidateMediaTransferSchema10(connection, transaction);
        command.Parameters["$value"].Value = excessive;
        Assert.Equal(19, Assert.Throws<SqliteException>(() => command.ExecuteNonQuery()).SqliteErrorCode);
        Assert.Equal((long)limit, Scalar(connection, transaction,
            $"SELECT length(CAST({column} AS BLOB)) FROM {table} WHERE operation_id='{(table == Import ? ImportId : MergeId)}'"));
    }

    [Theory]
    [InlineData("full_selection_json='{}'")]
    [InlineData("safe_selection_sha256='bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb'")]
    [InlineData("safe_member_count=2")]
    [InlineData("safe_inventory_sha256='bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb'")]
    [InlineData("safe_manifest_generation='bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb'")]
    [InlineData("conflicts=1")]
    public void AliasedV2RootsRequireExactFullSafeIdentityAndNoConflicts(string mismatch)
    {
        using SyntheticDatabase database = new();
        MigrateAndStamp(database);
        using SqliteConnection connection = database.Open(SqliteOpenMode.ReadWrite);
        using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);
        PromoteToV2(connection, transaction, "Prepared", nonemptySafe: true);
        Execute(connection, $"UPDATE {Merge} SET safe_directory=full_directory WHERE operation_id='{MergeId}'", transaction);
        ArchiveSql.ValidateMediaTransferSchema10(connection, transaction); // Positive identical alias control.
        Assert.Equal(19, Assert.Throws<SqliteException>(() => Execute(connection,
            $"UPDATE {Merge} SET {mismatch} WHERE operation_id='{MergeId}'", transaction)).SqliteErrorCode);
    }

    [Theory]
    [InlineData(7)] [InlineData(8)] [InlineData(9)]
    public async System.Threading.Tasks.Task CatalogUpgradesLegacy7Through9AndThenReopensWithoutChanges(int version)
    {
        using SyntheticDatabase database = new();
        var expected = Snapshot(database);
        using (SqliteConnection connection = database.Open(SqliteOpenMode.ReadWrite))
        using (SqliteTransaction transaction = connection.BeginTransaction(deferred: false))
        {
            if (version < 9)
                foreach (string table in new[] { "local_import_receipts", "local_import_operations", "local_media_revisions",
                    "local_items", "local_collections" })
                    Execute(connection, $"DROP TABLE {table}", transaction);
            if (version < 8)
            {
                Execute(connection, $"DROP TABLE {MergeReceipt}; DROP TABLE {Merge}", transaction);
                expected = expected.Take(2).ToArray();
            }
            Execute(connection, $"PRAGMA user_version={version}", transaction);
            // v7/v8 prevalidation must not demand schema10's columns (or v9 local tables).
            ArchiveSql.ValidateSchema(connection, transaction);
            transaction.Commit();
        }
        using ProfileLease lease = ProfileLease.Acquire(database.DirectoryPath);
        await using (ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(database.DirectoryPath, lease))
            Assert.Equal(database.Path, catalog.DatabasePath);
        using (SqliteConnection connection = database.Open(SqliteOpenMode.ReadOnly))
        using (SqliteTransaction transaction = connection.BeginTransaction(deferred: true))
        {
            Assert.Equal(12, ArchiveSql.GetPragma(connection, "user_version", transaction));
            Assert.Equal(ArchiveSql.ApplicationId, ArchiveSql.GetPragma(connection, "application_id", transaction));
            ArchiveSql.ValidateSchema(connection, transaction);
            var actual = Schema9MediaOperationsFixture.ReadSnapshot(connection, transaction);
            AssertSnapshotsEqual(expected, actual.Take(expected.Length).ToArray());
            if (version == 7)
                Assert.All(actual.Skip(2), snapshot => Assert.Empty(snapshot.Rows));
        }
        byte[] upgraded = File.ReadAllBytes(database.Path);
        await using (ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(database.DirectoryPath, lease)) { }
        Assert.Equal(upgraded, File.ReadAllBytes(database.Path));
    }

    [Fact]
    public void LegacyOperationReadersAndPagesRejectV2InsteadOfReportingV1Success()
    {
        using SyntheticDatabase database = new();
        MigrateAndStamp(database);
        using (SqliteConnection connection = database.Open(SqliteOpenMode.ReadWrite))
        using (SqliteTransaction transaction = connection.BeginTransaction(deferred: false))
        {
            // Positive v1 controls distinguish rejection by version from a broken lookup.
            Assert.NotNull(ArchiveSql.ReadMediaImportOperation(connection, transaction,
                Guid.Parse(ImportId), Schema9MediaOperationsFixture.Request));
            PromoteToV2(connection, transaction, "Preparing", nonemptySafe: true);
            Assert.Throws<InvalidDataException>(() => ArchiveSql.ReadMediaImportOperation(connection, transaction,
                Guid.Parse(ImportId), Schema9MediaOperationsFixture.Request));
            Assert.Throws<InvalidDataException>(() => ArchiveSql.ReadMediaMergeOperation(connection, transaction,
                Guid.Parse(MergeId), Schema9MediaOperationsFixture.Request));
            transaction.Commit();
        }
        using SqliteConnection read = database.Open(SqliteOpenMode.ReadOnly);
        Assert.Throws<InvalidDataException>(() => ArchiveSql.ReadMediaImportOperationsPage(read, null));
        Assert.Throws<InvalidDataException>(() => ArchiveSql.ReadMediaMergeOperationsPage(read, null));
    }

    [Fact]
    public void HistoricalV2ReceiptsWithoutOperationsAreNotDecodedAsV1()
    {
        using SyntheticDatabase database = new();
        MigrateAndStamp(database);
        using SqliteConnection connection = database.Open(SqliteOpenMode.ReadWrite);
        using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);
        Guid import = Guid.Parse("20000000-0000-4000-8000-000000000001");
        Guid merge = Guid.Parse("30000000-0000-4000-8000-000000000001");
        Assert.NotNull(ArchiveSql.ReadMediaImportReceipt(connection, transaction, import, Schema9MediaOperationsFixture.Request));
        Assert.NotNull(ArchiveSql.ReadMediaMergeReceipt(connection, transaction, merge, Schema9MediaOperationsFixture.Request));
        Execute(connection, $"UPDATE {ImportReceipt} SET plan_version=2,attachment_count=0 WHERE operation_id='{import:D}'", transaction);
        Execute(connection, $"UPDATE {MergeReceipt} SET plan_version=2 WHERE operation_id='{merge:D}'", transaction);
        ArchiveSql.ValidateMediaTransferSchema10(connection, transaction);
        Assert.Throws<InvalidDataException>(() => ArchiveSql.ReadMediaImportReceipt(connection, transaction,
            import, Schema9MediaOperationsFixture.Request));
        Assert.Throws<InvalidDataException>(() => ArchiveSql.ReadMediaMergeReceipt(connection, transaction,
            merge, Schema9MediaOperationsFixture.Request));
        Assert.Equal(0L, Scalar(connection, transaction, $"SELECT count(*) FROM {Import} WHERE operation_id='{import:D}'"));
        Assert.Equal(0L, Scalar(connection, transaction, $"SELECT count(*) FROM {Merge} WHERE operation_id='{merge:D}'"));
    }

    [Fact]
    public void CounterAndMemberUpperBoundsAreInclusive()
    {
        using SyntheticDatabase database = new();
        MigrateAndStamp(database);
        using SqliteConnection connection = database.Open(SqliteOpenMode.ReadWrite);
        using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);
        PromoteToV2(connection, transaction, "Prepared", nonemptySafe: true);
        Execute(connection, $"UPDATE {ImportReceipt} SET plan_version=2; UPDATE {MergeReceipt} SET plan_version=2", transaction);
        foreach (string table in MediaTables)
        {
            string additional = table.StartsWith("archive_media_import_", StringComparison.Ordinal)
                ? ",original_binding_count=2147483647" : "";
            string filter = table == Import ? $" WHERE operation_id='{ImportId}'"
                : table == Merge ? $" WHERE operation_id='{MergeId}'" : "";
            Execute(connection, $"UPDATE {table} SET evidence_additions=2147483647,derived_descriptor_additions=2147483647,contained_derived_additions=2147483647{additional}{filter}", transaction);
        }
        Execute(connection, $"UPDATE {Import} SET full_member_count=100000 WHERE operation_id='{ImportId}'", transaction);
        Execute(connection, $"UPDATE {Merge} SET full_member_count=100000,safe_member_count=100000 WHERE operation_id='{MergeId}'", transaction);
        ArchiveSql.ValidateMediaTransferSchema10(connection, transaction);
    }

    private static void PromoteToV2(SqliteConnection connection, SqliteTransaction transaction,
        string state, bool nonemptySafe)
    {
        string hash = "'" + Schema9MediaOperationsFixture.Hash + "'";
        bool ready = state != "Preparing";
        string generation = ready ? hash : "NULL";
        string fullDirectory = ready ? "'/synthetic/full'" : "NULL";
        string safeDirectory = ready && nonemptySafe ? "'/synthetic/safe'" : "NULL";
        string safeGeneration = ready && nonemptySafe ? hash : "NULL";
        Execute(connection, $"""
            UPDATE {Import} SET plan_version=2,state='{state}',revision_keys_json='[]',bindings_sha256=NULL,
                full_selection_json='{Selection}',full_selection_sha256={hash},full_member_count=1,
                full_inventory_sha256={generation},catalog_bytes=X'7B7D',original_binding_count=0,attachment_count=0,
                candidate_directory={fullDirectory},manifest_generation={generation}
            WHERE operation_id='{ImportId}';
            UPDATE {Merge} SET plan_version=2,state='{state}',full_keys_json='[]',safe_keys_json='[]',
                full_bindings_sha256=NULL,safe_bindings_sha256=NULL,
                full_selection_json='{Selection}',full_selection_sha256={hash},full_member_count=1,
                full_inventory_sha256={generation},full_directory={fullDirectory},full_manifest_generation={generation},
                safe_selection_json={(nonemptySafe ? "'" + Selection + "'" : "NULL")},
                safe_selection_sha256={(nonemptySafe ? hash : "NULL")},safe_member_count={(nonemptySafe ? 1 : 0)},
                safe_inventory_sha256={safeGeneration},safe_directory={safeDirectory},safe_manifest_generation={safeGeneration},
                new_bindings=0,conflicts=0
            WHERE operation_id='{MergeId}';
            """, transaction);
    }

    private static void AssertCheckpointShape(SqliteConnection connection, SqliteTransaction transaction, string checkpoint)
    {
        Assert.Contains(checkpoint, Checkpoints);
        Assert.Equal(9, ArchiveSql.GetPragma(connection, "user_version", transaction));
        Assert.Equal(ArchiveSql.ApplicationId, ArchiveSql.GetPragma(connection, "application_id", transaction));
        if (checkpoint == "swapped")
        {
            ArchiveSql.ValidateMediaTransferSchema10(connection, transaction);
            Assert.Equal(0L, Scalar(connection, transaction,
                "SELECT count(*) FROM sqlite_master WHERE name LIKE '%_schema10'"));
            return;
        }
        foreach (var table in Schema9MediaOperationsFixture.ReadSnapshot(connection, transaction))
        {
            string shadow = table.Table + "_schema10";
            Assert.Equal(1L, Scalar(connection, transaction,
                $"SELECT count(*) FROM sqlite_master WHERE type='table' AND name='{shadow}'"));
            Assert.Equal(checkpoint == "created" ? 0L : table.Rows.Length,
                Scalar(connection, transaction, $"SELECT count(*) FROM {shadow}"));
            if (checkpoint == "copied")
                Assert.Equal(ReadCells(connection, transaction, $"SELECT {string.Join(',', table.Columns)} FROM {table.Table} ORDER BY operation_id"),
                    ReadCells(connection, transaction, $"SELECT {string.Join(',', table.Columns)} FROM {shadow} ORDER BY operation_id"));
        }
    }

    private static string RemoveFirstCheck(string sql)
    {
        int start = sql.IndexOf("CHECK", StringComparison.OrdinalIgnoreCase);
        Assert.True(start >= 0);
        int opening = sql.IndexOf('(', start);
        int depth = 0;
        for (int i = opening; i < sql.Length; i++)
        {
            if (sql[i] == '(') depth++;
            if (sql[i] == ')' && --depth == 0) return sql.Remove(start, i + 1 - start);
        }
        throw new InvalidOperationException("Unbalanced synthetic CHECK.");
    }

    private static void SeedProtectedRows(SqliteConnection connection, SqliteTransaction transaction)
    {
        const string collection = "a1111111-1111-4111-8111-111111111111";
        const string item = "a2222222-2222-4222-8222-222222222222";
        const string revision = "a3333333-3333-4333-8333-333333333333";
        const string operation = "a4444444-4444-4444-8444-444444444444";
        const string at = "2026-11-08T00:00:00.0000000+00:00";
        string hash = Schema9MediaOperationsFixture.Hash;
        const string emptyJson = "{}";
        Execute(connection, $"""
            INSERT INTO archive_threads(source,board,thread,url,title,media_directory,source_state,capture_enabled,first_seen_at,revision)
                VALUES('synthetic','b','1','https://invalid.test/1','do not mutate','/synthetic/media',0,0,'{at}',42);
            INSERT INTO archive_posts(source,board,thread,post,ordinal,fields,html,is_deleted)
                VALUES('synthetic','b','1','2',0,0,'<p>unchanged</p>',0);
            INSERT INTO archive_attachments(source,board,thread,post,position,variant,is_deleted)
                VALUES('synthetic','b','1','2',0,'original',0);
            INSERT INTO archive_media_directories VALUES('synthetic','b','1','/synthetic/other');
            INSERT INTO archive_source_contracts VALUES('synthetic','b','1',1);
            INSERT INTO media_decisions(source_id,board_id,thread_id,media_id,variant,download_uri,destination_path,selection_json,decided_at_utc)
                VALUES('synthetic','b','1','m','original','https://invalid.test/m','/synthetic/m','{emptyJson}','{at}');
            INSERT INTO archive_import_receipts VALUES('{operation}','{hash}',1,2,3,0);
            INSERT INTO archive_import_candidates VALUES('synthetic','b','1','{hash}','{at}','{at}',0,X'007B7DFF');
            INSERT INTO local_collections VALUES('{collection}','local sentinel',1);
            INSERT INTO local_items VALUES('{collection}','{item}',0,NULL);
            INSERT INTO local_media_revisions VALUES('{collection}','{item}','{revision}',1,'{hash}',0,'sentinel.bin',NULL);
            INSERT INTO local_import_operations(operation_id,collection_id,catalog_instance,expected_generation,attempt,state,
                created_at,selection_json,item_count,payload_limit,metadata_reservation)
                SELECT '{operation}','{collection}',instance,0,1,'Capturing','{at}','{emptyJson}',1,1024,1 FROM archive_import_fence;
            INSERT INTO local_import_receipts(operation_id,request_sha256,catalog_instance,collection_id,receipt_json)
                SELECT '{operation}','{hash}',instance,'{collection}','{emptyJson}' FROM archive_import_fence;
            UPDATE archive_import_fence SET generation=12345;
            UPDATE archive_media_import_operations SET revision_keys_json=' [ ] ';
            UPDATE archive_media_merge_operations SET roots_json=' [ ] ',catalog_bytes=X'007B7DFF';
            """, transaction);
    }

    private static void MigrateAndStamp(SyntheticDatabase database)
    {
        using (SqliteConnection connection = database.Open(SqliteOpenMode.ReadWrite))
        using (SqliteTransaction transaction = connection.BeginTransaction(deferred: false))
        {
            ArchiveSql.MigrateMediaTransferSchema10(connection, transaction, CancellationToken.None);
            ArchiveSql.ValidateMediaTransferSchema10(connection, transaction);
            ArchiveSql.ValidateTargetSchema(connection, transaction, 10);
            Assert.Equal(9, ArchiveSql.GetPragma(connection, "user_version", transaction));
            Execute(connection, "PRAGMA user_version=10", transaction);
            ArchiveSql.ValidateSchema(connection, transaction);
            transaction.Commit();
        }
    }

    private static Schema9MediaOperationsFixture.TableSnapshot[] Snapshot(SyntheticDatabase database)
    {
        using SqliteConnection connection = database.Open(SqliteOpenMode.ReadOnly);
        using SqliteTransaction transaction = connection.BeginTransaction(deferred: true);
        return Schema9MediaOperationsFixture.ReadSnapshot(connection, transaction);
    }

    private static string[] ProtectedObjectSnapshot(SyntheticDatabase database)
    {
        using SqliteConnection connection = database.Open(SqliteOpenMode.ReadOnly);
        return ProtectedObjectSnapshot(connection, null);
    }

    private static string[] ProtectedObjectSnapshot(SqliteConnection connection, SqliteTransaction? transaction)
    {
        string predicate = "tbl_name NOT IN (" + string.Join(",", MediaTables.Select(t => "'" + t + "'")) + ")";
        var values = new List<string>(ReadCells(connection, transaction,
            "SELECT type,name,tbl_name,sql FROM sqlite_master WHERE " + predicate + " ORDER BY type,name"));
        string[] tables = Values(connection, transaction,
            "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' AND " + predicate + " ORDER BY name");
        foreach (string table in tables)
        {
            values.Add("TABLE:" + table);
            values.AddRange(ReadCells(connection, transaction, "SELECT * FROM " + Quote(table))
                .OrderBy(row => row, StringComparer.Ordinal));
        }
        return values.ToArray();
    }

    private static string[] ReadCells(SqliteConnection connection, SqliteTransaction? transaction, string sql)
    {
        using SqliteCommand command = ArchiveSql.Command(connection, transaction, sql);
        using SqliteDataReader reader = command.ExecuteReader();
        var rows = new List<string>();
        while (reader.Read())
        {
            var cells = new string[reader.FieldCount];
            for (int i = 0; i < cells.Length; i++)
                cells[i] = reader.GetValue(i) switch
                {
                    DBNull => "N",
                    byte[] blob => "B" + Convert.ToHexString(blob),
                    string text => "T" + text,
                    long integer => "I" + integer.ToString(CultureInfo.InvariantCulture),
                    double real => "R" + real.ToString("R", CultureInfo.InvariantCulture),
                    _ => throw new InvalidOperationException("Unexpected SQLite value.")
                };
            rows.Add(JsonSerializer.Serialize(cells));
        }
        return rows.ToArray();
    }

    private static string[] RowIdentitySnapshot(SqliteConnection connection, SqliteTransaction transaction)
        => MediaTables.SelectMany(table => new[] { table }.Concat(ReadCells(connection, transaction,
            $"SELECT rowid,operation_id FROM {table} ORDER BY operation_id"))).ToArray();

    private static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    private static string[] ShadowObjectSnapshot(SyntheticDatabase database)
    {
        using SqliteConnection connection = database.Open(SqliteOpenMode.ReadOnly);
        using SqliteCommand command = ArchiveSql.Command(connection, null,
            "SELECT type||'|'||name||'|'||COALESCE(sql,'') FROM sqlite_master WHERE name LIKE '%_schema10' ORDER BY type,name");
        using SqliteDataReader reader = command.ExecuteReader();
        var values = new List<string>();
        while (reader.Read()) values.Add(reader.GetString(0));
        return values.ToArray();
    }

    private static void AssertV1Defaults(SqliteConnection connection, SqliteTransaction transaction)
    {
        Assert.Equal(3L, Scalar(connection, transaction,
            "SELECT count(*) FROM archive_media_import_operations WHERE plan_version=1 AND full_selection_json IS NULL AND full_selection_sha256 IS NULL AND full_inventory_sha256 IS NULL AND full_member_count=0 AND catalog_bytes IS NULL AND evidence_additions=0 AND derived_descriptor_additions=0 AND contained_derived_additions=0 AND original_binding_count=0"));
        Assert.Equal(2L, Scalar(connection, transaction,
            "SELECT count(*) FROM archive_media_import_receipts WHERE plan_version=1 AND evidence_additions=0 AND derived_descriptor_additions=0 AND contained_derived_additions=0 AND original_binding_count=0"));
        Assert.Equal(3L, Scalar(connection, transaction,
            "SELECT count(*) FROM archive_media_merge_operations WHERE plan_version=1 AND full_selection_json IS NULL AND full_selection_sha256 IS NULL AND safe_selection_json IS NULL AND safe_selection_sha256 IS NULL AND full_inventory_sha256 IS NULL AND safe_inventory_sha256 IS NULL AND full_member_count=0 AND safe_member_count=0 AND evidence_additions=0 AND derived_descriptor_additions=0 AND contained_derived_additions=0"));
        Assert.Equal(2L, Scalar(connection, transaction,
            "SELECT count(*) FROM archive_media_merge_receipts WHERE plan_version=1 AND evidence_additions=0 AND derived_descriptor_additions=0 AND contained_derived_additions=0"));
    }

    private static void AssertColumns(SqliteConnection connection, SqliteTransaction transaction,
        string table, params string[] expected)
    {
        using SqliteCommand command = ArchiveSql.Command(connection, transaction, $"PRAGMA table_info({table})");
        using SqliteDataReader reader = command.ExecuteReader();
        var actual = new HashSet<string>(StringComparer.Ordinal);
        while (reader.Read()) actual.Add(reader.GetString(1));
        reader.Close();
        var legacy = Schema9MediaOperationsFixture.ReadSnapshot(connection, transaction)
            .Single(snapshot => snapshot.Table == table).Columns;
        Assert.Equal(legacy.Concat(expected).OrderBy(column => column, StringComparer.Ordinal),
            actual.OrderBy(column => column, StringComparer.Ordinal));
        Assert.Equal(1L, Scalar(connection, transaction,
            $"SELECT count(*) FROM pragma_table_info('{table}') WHERE name='plan_version' AND type='INTEGER' AND [notnull]=1 AND dflt_value='1'"));
        Assert.Equal(0L, Scalar(connection, transaction, $"SELECT count(*) FROM pragma_foreign_key_list('{table}')"));

    }

    private static void AssertSnapshotsEqual(Schema9MediaOperationsFixture.TableSnapshot[] expected,
        Schema9MediaOperationsFixture.TableSnapshot[] actual)
    {
        Assert.Equal(expected.Length, actual.Length);
        for (int table = 0; table < expected.Length; table++)
        {
            Assert.Equal(expected[table].Table, actual[table].Table);
            Assert.Equal(expected[table].Columns, actual[table].Columns);
            Assert.Equal(expected[table].Rows.Length, actual[table].Rows.Length);
            for (int row = 0; row < expected[table].Rows.Length; row++)
                Assert.Equal(expected[table].Rows[row], actual[table].Rows[row]);
        }
    }

    private static string[] TableNames(SqliteConnection connection, SqliteTransaction? transaction)
        => Values(connection, transaction,
            "SELECT name FROM sqlite_master WHERE type='table' AND name LIKE 'archive_media_%' ORDER BY name");

    private static string[] Values(SqliteConnection connection, SqliteTransaction? transaction, string sql)
    {
        using SqliteCommand command = ArchiveSql.Command(connection, transaction, sql);
        using SqliteDataReader reader = command.ExecuteReader();
        var values = new List<string>();
        while (reader.Read()) values.Add(reader.GetString(0));
        return values.ToArray();
    }

    private static long Scalar(SqliteConnection connection, SqliteTransaction? transaction, string sql)
    {
        using SqliteCommand command = ArchiveSql.Command(connection, transaction, sql);
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private static long Scalar(SyntheticDatabase database, string sql)
    {
        using SqliteConnection connection = database.Open(SqliteOpenMode.ReadOnly);
        return Scalar(connection, null, sql);
    }

    private static int Pragma(SyntheticDatabase database, string name)
    {
        using SqliteConnection connection = database.Open(SqliteOpenMode.ReadOnly);
        return ArchiveSql.GetPragma(connection, name);
    }

    private static string Text(SyntheticDatabase database, string sql)
    {
        using SqliteConnection connection = database.Open(SqliteOpenMode.ReadOnly);
        return Text(connection, sql);
    }

    private static string Text(SqliteConnection connection, string sql, SqliteTransaction? transaction = null)
    {
        using SqliteCommand command = ArchiveSql.Command(connection, transaction, sql);
        return (string)command.ExecuteScalar()!;
    }

    private static void Execute(SqliteConnection connection, string sql, SqliteTransaction? transaction = null)
    {
        using SqliteCommand command = ArchiveSql.Command(connection, transaction, sql);
        command.ExecuteNonQuery();
    }

    private sealed class SyntheticDatabase : IDisposable
    {
        private readonly string directory = TemporaryWorkspacePaths.CreateDirectory("meowchan-schema10-");
        internal string DirectoryPath => directory;
        internal string Path => System.IO.Path.Combine(directory, "archive.db");

        internal SyntheticDatabase(bool seedSchema9 = true)
        {
            using SqliteConnection connection = Open(SqliteOpenMode.ReadWriteCreate);
            using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);
            if (seedSchema9)
            {
                using Stream fixture = typeof(Schema10MediaOperationsMigrationTests).Assembly
                    .GetManifestResourceStream("MeowChan.Tests.Fixtures.archive-schema-1.sql")!;
                using StreamReader text = new(fixture);
                Execute(connection, text.ReadToEnd(), transaction);
                ArchiveSql.MigrateSearchSchema(connection, transaction, CancellationToken.None);
                ArchiveSql.CreateSourceContractSchema(connection, transaction);
                ArchiveSql.CreateIntegritySchema(connection, transaction);
                ArchiveSql.CreateNativeImportSchema(connection, transaction);
                // Only this frozen helper defines legacy media DDL; no production media creator.
                Schema9MediaOperationsFixture.CreateSchema9(connection, transaction);
                Schema9MediaOperationsFixture.SeedValidV1Rows(connection, transaction);
                ArchiveSql.CreateLocalCollectionSchema(connection, transaction);
                SeedProtectedRows(connection, transaction);
                foreach (string table in MediaTables)
                    Execute(connection, $"UPDATE {table} SET rowid=rowid*10+100", transaction);
                Execute(connection, $"PRAGMA application_id={ArchiveSql.ApplicationId}", transaction);
                Execute(connection, "PRAGMA user_version=9", transaction);
            }
            transaction.Commit();
        }

        internal SqliteConnection Open(SqliteOpenMode mode) => ArchiveSql.OpenConnection(Path, mode);

        public void Dispose() => Directory.Delete(directory, recursive: true);
    }
}
