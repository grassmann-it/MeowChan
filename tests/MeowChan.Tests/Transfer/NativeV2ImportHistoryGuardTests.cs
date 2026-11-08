using MeowChan.Archive;
using MeowChan.Persistence;
using MeowChan.Transfer;
using Microsoft.Data.Sqlite;

namespace MeowChan.Tests.Transfer;

// Claim-boundary regressions only. Synthetic foreign history is bounded persistence
// evidence, not an executable package; the incoming plan uses the real validated stage.
public sealed class NativeV2ImportHistoryGuardTests
{
    [Theory]
    [InlineData("preparing-import")]
    [InlineData("prepared-import")]
    [InlineData("committed-import-without-receipt")]
    [InlineData("mismatched-import-receipt")]
    [InlineData("malformed-import-selection")]
    [InlineData("preparing-merge")]
    [InlineData("prepared-merge")]
    [InlineData("committed-merge-without-receipt")]
    [InlineData("mismatched-merge-receipt")]
    [InlineData("malformed-merge-selection")]
    [InlineData("unknown-import-receipt-version")]
    [InlineData("unknown-merge-receipt-version")]
    public async Task ForeignBlockerAppearingAfterNegativeReceiptReadPreventsAllFreshEffects(string history)
    {
        await using NativeV2ImportFixture f = await NativeV2ImportFixture.CreateAsync(
            NativeV2ImportFixture.Content(presence: false));
        using ProfileAdmissionToken admission = await f.ExclusiveAsync();
        Assert.NotEqual(f.Plan.Resource, PersistedMediaFixture.Resource);
        int observations = 0, phases = 0;
        var outcome = await f.CommitAsync(admission, checkpoint: _ => phases++, beforeAdmission: () =>
        {
            observations++;
            // Insert AFTER the coordinator's negative independent receipt lookup. These
            // journal writes do not advance the content fence and must be seen at Claim.
            using SqliteConnection connection = ArchiveSql.OpenConnection(f.Catalog.DatabasePath, SqliteOpenMode.ReadWrite);
            using SqliteTransaction transaction = connection.BeginTransaction();
            SeedHistory(connection, transaction, history);
            Assert.True(ArchiveSql.HasIncompatibleMediaTransferHistory(connection, transaction));
            transaction.Commit();
        });

        Assert.Equal(1, observations);
        Assert.Equal(0, phases); // no PreparingSaved, mkdir, materialization or content callback
        Assert.Equal(ProfileExclusiveDisposition.RecoveryRequired, outcome.Disposition);
        Assert.Equal(NativeMediaImportCommitStatus.ReconciliationRequired, outcome.Result.Status);
        Assert.False(outcome.Result.IsReplay);
        Assert.Null(outcome.Result.Diagnostic);
        Assert.False(Directory.Exists(f.Plan.OperationsParent));
        Assert.Empty(NativeV2ImportFixture.PayloadFiles(f.Files.Profile));
        Assert.Null(await f.Catalog.ReadNativeMediaImportReceiptAsync(f.Plan.OperationId, f.Plan.RequestSha256, 2));
        await f.Catalog.ReadAsync(connection =>
        {
            using SqliteTransaction transaction = connection.BeginTransaction(deferred: true);
            Assert.Null(ArchiveSql.ReadPersistedMediaImportOperation(connection, transaction,
                f.Plan.OperationId, f.Plan.RequestSha256));
            Assert.True(ArchiveSql.MediaImportResourceAbsent(connection, transaction, f.Plan.Resource));
            Assert.True(ArchiveSql.HasIncompatibleMediaTransferHistory(connection, transaction));
            Assert.Equal((f.Plan.Instance, f.Plan.Generation), ArchiveSql.ReadImportFence(connection, transaction));
            return true;
        });
        InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(
            () => f.Catalog.EnsureNativeMediaRecoveryClearAsync());
        Assert.Equal("Native media transfer reconciliation is required.", error.Message);
        Assert.Null(error.InnerException);
        f.Catalog.EnsureTransferAdmission(admission);
    }

    [Theory]
    [InlineData("preparing-import")]
    [InlineData("prepared-import")]
    [InlineData("preparing-merge")]
    [InlineData("prepared-merge")]
    public async Task DirectClaimSeesUncommittedForeignHistoryInTheCallerTransaction(string history)
    {
        await using NativeV2ImportFixture f = await NativeV2ImportFixture.CreateAsync(
            NativeV2ImportFixture.Content(presence: false));
        using ProfileAdmissionToken admission = await f.ExclusiveAsync();
        await f.Catalog.WriteTransferAdmittedAsync(admission, (connection, transaction) =>
        {
            SeedHistory(connection, transaction, history);
            Assert.True(ArchiveSql.HasIncompatibleMediaTransferHistory(connection, transaction));
            Assert.False(ArchiveSql.ClaimMediaImportV2(connection, transaction, f.Plan));
            Assert.Null(ArchiveSql.ReadPersistedMediaImportOperation(connection, transaction,
                f.Plan.OperationId, f.Plan.RequestSha256));
            using SqliteCommand probe = ArchiveSql.Command(connection, transaction, "SELECT 1");
            Assert.Equal(1L, probe.ExecuteScalar()); // guard did not consume the caller's tx
            return true;
        });
        Assert.False(Directory.Exists(f.Plan.OperationsParent));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ValidCommittedOrOrphanHistoryAllowsFreshImportAndOwnReceiptRemainsFirst(bool merge, bool orphan)
    {
        await using NativeV2ImportFixture f = await NativeV2ImportFixture.CreateAsync(
            NativeV2ImportFixture.Content(presence: false));
        using ProfileAdmissionToken admission = await f.ExclusiveAsync();
        await f.Catalog.WriteTransferAdmittedAsync(admission, (connection, transaction) =>
        {
            Dictionary<string, object?> row = PersistedMediaFixture.Row(merge, state: "Committed");
            string table = merge ? "archive_media_merge" : "archive_media_import";
            if (!orphan) Insert(connection, transaction, table + "_operations", row);
            Insert(connection, transaction, table + "_receipts", PersistedMediaFixture.Receipt(merge, row));
            AssertValidHistory(connection, transaction, merge, orphan);
            Assert.False(ArchiveSql.HasIncompatibleMediaTransferHistory(connection, transaction));
            return true;
        });

        var committed = await f.CommitAsync(admission);
        Assert.Equal(ProfileExclusiveDisposition.SafeToResume, committed.Disposition);
        Assert.Equal(NativeMediaImportCommitStatus.Committed, committed.Result.Status);
        Assert.False(committed.Result.IsReplay);
        Guid unfinished = Guid.Parse("cd220000-0000-4000-8000-000000000099");
        Assert.NotEqual(f.Plan.OperationId, unfinished);
        Assert.NotEqual(PersistedMediaFixture.OperationId, unfinished);
        await f.Catalog.WriteTransferAdmittedAsync(admission, (connection, transaction) =>
        {
            // A valid orphan merge receipt is allowed history in W32b5. Use an actual
            // separate unfinished operation: our receipt wins replay, never resume.
            SeedHistory(connection, transaction, "preparing-merge", unfinished);
            Assert.True(ArchiveSql.HasIncompatibleMediaTransferHistory(connection, transaction));
            Assert.False(ArchiveSql.ClaimMediaImportV2(connection, transaction, f.Plan));
            return true;
        });
        await f.Stage.DisposeAsync();
        var filesBefore = NativeV2ImportFixture.PayloadFiles(f.Files.Profile);
        var replay = await f.CommitAsync(admission, new CancellationToken(true),
            checkpoint: _ => throw new InvalidOperationException("replay entered a write phase"),
            beforeAdmission: () => throw new InvalidOperationException("replay attempted a claim"));
        Assert.Equal(ProfileExclusiveDisposition.RecoveryRequired, replay.Disposition);
        Assert.Equal(committed.Result with { IsReplay = true }, replay.Result);
        Assert.Equal(filesBefore, NativeV2ImportFixture.PayloadFiles(f.Files.Profile));
        await f.Catalog.ReadAsync(connection =>
        {
            using SqliteTransaction transaction = connection.BeginTransaction(deferred: true);
            NativeMediaPersistedMergeOperation pending = Assert.IsType<NativeMediaPersistedMergeOperation>(
                ArchiveSql.ReadPersistedMediaMergeOperation(connection, transaction, unfinished, PersistedMediaFixture.Request));
            Assert.Equal(NativeMediaImportOperationState.Preparing, pending.State);
            Assert.Null(ArchiveSql.ReadPersistedMediaMergeReceipt(connection, transaction,
                unfinished, PersistedMediaFixture.Request, 2));
            Assert.True(ArchiveSql.HasIncompatibleMediaTransferHistory(connection, transaction));
            AssertValidHistory(connection, transaction, merge, orphan);
            return true;
        });
    }

    [Theory]
    [InlineData("committed-merge")]
    [InlineData("orphan-merge-receipt")]
    public async Task ValidMergeHistoryAppearingAfterNegativeReceiptReadAllowsFreshEffects(string history)
    {
        await using NativeV2ImportFixture f = await NativeV2ImportFixture.CreateAsync(
            NativeV2ImportFixture.Content(presence: false));
        using ProfileAdmissionToken admission = await f.ExclusiveAsync();
        Assert.NotEqual(f.Plan.Resource, PersistedMediaFixture.Resource);
        bool orphan = history == "orphan-merge-receipt";
        int observations = 0;
        List<NativeMediaImportPhase> phases = new();
        var outcome = await f.CommitAsync(admission, checkpoint: phase => phases.Add(phase), beforeAdmission: () =>
        {
            observations++;
            using SqliteConnection connection = ArchiveSql.OpenConnection(f.Catalog.DatabasePath, SqliteOpenMode.ReadWrite);
            using SqliteTransaction transaction = connection.BeginTransaction();
            SeedHistory(connection, transaction, history);
            AssertValidHistory(connection, transaction, merge: true, orphan: orphan);
            Assert.False(ArchiveSql.HasIncompatibleMediaTransferHistory(connection, transaction));
            Assert.Equal((f.Plan.Instance, f.Plan.Generation), ArchiveSql.ReadImportFence(connection, transaction));
            transaction.Commit();
        });

        Assert.Equal(1, observations);
        Assert.Contains(NativeMediaImportPhase.PreparingSaved, phases);
        Assert.Contains(NativeMediaImportPhase.SqlCommitted, phases);
        Assert.Equal(ProfileExclusiveDisposition.SafeToResume, outcome.Disposition);
        Assert.Equal(NativeMediaImportCommitStatus.Committed, outcome.Result.Status);
        Assert.False(outcome.Result.IsReplay);
        Assert.Null(outcome.Result.Diagnostic);
        Assert.True(Directory.Exists(f.Plan.OperationsParent));
        Assert.NotEmpty(NativeV2ImportFixture.PayloadFiles(f.Files.Profile));
        Assert.Equal(outcome.Result with { IsReplay = true },
            await f.Catalog.ReadNativeMediaImportReceiptAsync(f.Plan.OperationId, f.Plan.RequestSha256, 2));
        await f.Catalog.ReadAsync(connection =>
        {
            using SqliteTransaction transaction = connection.BeginTransaction(deferred: true);
            NativeMediaPersistedImportOperation operation = Assert.IsType<NativeMediaPersistedImportOperation>(
                ArchiveSql.ReadPersistedMediaImportOperation(connection, transaction, f.Plan.OperationId, f.Plan.RequestSha256));
            Assert.True(f.Plan.Representation!.MatchesOperation(operation, NativeMediaImportOperationState.Committed));
            Assert.False(ArchiveSql.MediaImportResourceAbsent(connection, transaction, f.Plan.Resource));
            Assert.False(ArchiveSql.HasIncompatibleMediaTransferHistory(connection, transaction));
            AssertValidHistory(connection, transaction, merge: true, orphan: orphan);
            return true;
        });
        f.Catalog.EnsureTransferAdmission(admission);
    }

    private static void AssertValidHistory(SqliteConnection connection, SqliteTransaction transaction, bool merge, bool orphan)
    {
        if (merge)
        {
            NativeMediaPersistedMergeOperation? operation = ArchiveSql.ReadPersistedMediaMergeOperation(
                connection, transaction, PersistedMediaFixture.OperationId, PersistedMediaFixture.Request);
            NativeMediaPersistedMergeReceipt receipt = Assert.IsType<NativeMediaPersistedMergeReceipt>(
                ArchiveSql.ReadPersistedMediaMergeReceipt(connection, transaction,
                    PersistedMediaFixture.OperationId, PersistedMediaFixture.Request, 2));
            if (orphan) Assert.Null(operation);
            else Assert.True(ArchiveSql.PersistedMediaMergeReceiptMatches(
                Assert.IsType<NativeMediaPersistedMergeOperation>(operation), receipt));
        }
        else
        {
            NativeMediaPersistedImportOperation? operation = ArchiveSql.ReadPersistedMediaImportOperation(
                connection, transaction, PersistedMediaFixture.OperationId, PersistedMediaFixture.Request);
            NativeMediaPersistedImportReceipt receipt = Assert.IsType<NativeMediaPersistedImportReceipt>(
                ArchiveSql.ReadPersistedMediaImportReceipt(connection, transaction,
                    PersistedMediaFixture.OperationId, PersistedMediaFixture.Request, 2));
            if (orphan) Assert.Null(operation);
            else Assert.True(ArchiveSql.PersistedMediaImportReceiptMatches(
                Assert.IsType<NativeMediaPersistedImportOperation>(operation), receipt));
        }
    }

    private static void SeedHistory(SqliteConnection connection, SqliteTransaction transaction, string history,
        Guid? operation = null)
    {
        bool merge = history is "preparing-merge" or "prepared-merge" or "committed-merge"
            or "committed-merge-without-receipt" or "mismatched-merge-receipt" or "malformed-merge-selection"
            or "orphan-merge-receipt" or "unknown-merge-receipt-version";
        string state = history switch
        {
            "preparing-import" or "preparing-merge" => "Preparing",
            "prepared-import" or "prepared-merge" => "Prepared",
            _ => "Committed"
        };
        Dictionary<string, object?> row = PersistedMediaFixture.Row(merge, state: state, operation: operation);
        Dictionary<string, object?> receipt = PersistedMediaFixture.Receipt(merge, row);
        if (history is "malformed-import-selection" or "malformed-merge-selection") row["full_selection_json"] = "{}";
        if (history is "mismatched-import-receipt" or "mismatched-merge-receipt")
        {
            string counter = merge ? "new_posts" : "post_count";
            receipt[counter] = (long)row[counter]! + 1;
        }
        if (history is not ("orphan-merge-receipt" or "unknown-import-receipt-version" or "unknown-merge-receipt-version"))
            Insert(connection, transaction, merge ? "archive_media_merge_operations" : "archive_media_import_operations", row);
        if (state != "Committed" || history is "committed-import-without-receipt" or "committed-merge-without-receipt") return;
        if (history is "unknown-import-receipt-version" or "unknown-merge-receipt-version")
        {
            // Corrupt future-version evidence only in this synthetic profile. Restore CHECKs
            // before returning; production still validates/enforces its normal schema.
            using SqliteCommand checks = ArchiveSql.Command(connection, transaction, "PRAGMA ignore_check_constraints=ON");
            checks.ExecuteNonQuery();
            try
            {
                receipt["plan_version"] = 3L;
                Insert(connection, transaction, merge ? "archive_media_merge_receipts" : "archive_media_import_receipts", receipt);
            }
            finally
            {
                checks.CommandText = "PRAGMA ignore_check_constraints=OFF";
                checks.ExecuteNonQuery();
            }
            Assert.Equal(0, ArchiveSql.GetPragma(connection, "ignore_check_constraints", transaction));
            return;
        }
        Insert(connection, transaction, merge ? "archive_media_merge_receipts" : "archive_media_import_receipts", receipt);
        if (history == "mismatched-merge-receipt")
        {
            NativeMediaPersistedMergeOperation stored = Assert.IsType<NativeMediaPersistedMergeOperation>(
                ArchiveSql.ReadPersistedMediaMergeOperation(connection, transaction,
                    operation ?? PersistedMediaFixture.OperationId, PersistedMediaFixture.Request));
            NativeMediaPersistedMergeReceipt mismatched = Assert.IsType<NativeMediaPersistedMergeReceipt>(
                ArchiveSql.ReadPersistedMediaMergeReceipt(connection, transaction,
                    operation ?? PersistedMediaFixture.OperationId, PersistedMediaFixture.Request, 2));
            Assert.False(ArchiveSql.PersistedMediaMergeReceiptMatches(stored, mismatched));
        }
        if (history == "malformed-merge-selection")
            Assert.Throws<InvalidDataException>(() => ArchiveSql.ReadPersistedMediaMergeOperation(connection, transaction,
                operation ?? PersistedMediaFixture.OperationId, PersistedMediaFixture.Request));
    }

    private static void Insert(SqliteConnection connection, SqliteTransaction transaction, string table,
        Dictionary<string, object?> row)
    {
        // Table/columns come only from fixed fixture factories above, never package input.
        using SqliteCommand command = ArchiveSql.Command(connection, transaction,
            $"INSERT INTO {table} ({string.Join(",", row.Keys)}) VALUES ({string.Join(",", row.Keys.Select(key => "$" + key))})");
        foreach (var field in row) command.Parameters.AddWithValue("$" + field.Key, field.Value ?? DBNull.Value);
        Assert.Equal(1, command.ExecuteNonQuery());
    }
}
