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
    [InlineData("committed-merge")]
    [InlineData("orphan-merge-receipt")]
    [InlineData("unknown-import-receipt-version")]
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
        f.Catalog.EnsureTransferAdmission(admission);
    }

    [Fact]
    public async Task DirectClaimSeesUncommittedForeignHistoryInTheCallerTransaction()
    {
        await using NativeV2ImportFixture f = await NativeV2ImportFixture.CreateAsync(
            NativeV2ImportFixture.Content(presence: false));
        using ProfileAdmissionToken admission = await f.ExclusiveAsync();
        await f.Catalog.WriteTransferAdmittedAsync(admission, (connection, transaction) =>
        {
            SeedHistory(connection, transaction, "preparing-import");
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
    [InlineData(false)]
    [InlineData(true)]
    public async Task ValidCommittedOrOrphanImportHistoryAllowsFreshImportAndOwnReceiptRemainsFirst(bool orphan)
    {
        await using NativeV2ImportFixture f = await NativeV2ImportFixture.CreateAsync(
            NativeV2ImportFixture.Content(presence: false));
        using ProfileAdmissionToken admission = await f.ExclusiveAsync();
        await f.Catalog.WriteTransferAdmittedAsync(admission, (connection, transaction) =>
        {
            Dictionary<string, object?> row = PersistedMediaFixture.Row(false, state: "Committed");
            if (!orphan) Insert(connection, transaction, "archive_media_import_operations", row);
            Insert(connection, transaction, "archive_media_import_receipts", PersistedMediaFixture.Receipt(false, row));
            Assert.False(ArchiveSql.HasIncompatibleMediaTransferHistory(connection, transaction));
            return true;
        });

        var committed = await f.CommitAsync(admission);
        Assert.Equal(ProfileExclusiveDisposition.SafeToResume, committed.Disposition);
        Assert.Equal(NativeMediaImportCommitStatus.Committed, committed.Result.Status);
        Assert.False(committed.Result.IsReplay);
        await f.Catalog.WriteTransferAdmittedAsync(admission, (connection, transaction) =>
        {
            // Own committed receipt prevents reuse even when a new foreign blocker exists.
            SeedHistory(connection, transaction, "orphan-merge-receipt");
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
    }

    private static void SeedHistory(SqliteConnection connection, SqliteTransaction transaction, string history)
    {
        bool merge = history is "committed-merge" or "orphan-merge-receipt";
        string state = history switch
        {
            "preparing-import" => "Preparing",
            "prepared-import" => "Prepared",
            _ => "Committed"
        };
        Dictionary<string, object?> row = PersistedMediaFixture.Row(merge, state: state);
        Dictionary<string, object?> receipt = PersistedMediaFixture.Receipt(merge, row);
        if (history == "malformed-import-selection") row["full_selection_json"] = "{}";
        if (history == "mismatched-import-receipt") receipt["post_count"] = (long)row["post_count"]! + 1;
        if (history is not ("orphan-merge-receipt" or "unknown-import-receipt-version"))
            Insert(connection, transaction, merge ? "archive_media_merge_operations" : "archive_media_import_operations", row);
        if (history is "committed-import-without-receipt" or "preparing-import" or "prepared-import") return;
        if (history == "unknown-import-receipt-version")
        {
            // Corrupt future-version evidence only in this synthetic profile. Restore CHECKs
            // before returning; production still validates/enforces its normal schema.
            using SqliteCommand checks = ArchiveSql.Command(connection, transaction, "PRAGMA ignore_check_constraints=ON");
            checks.ExecuteNonQuery();
            try
            {
                receipt["plan_version"] = 3L;
                Insert(connection, transaction, "archive_media_import_receipts", receipt);
            }
            finally
            {
                checks.CommandText = "PRAGMA ignore_check_constraints=OFF";
                checks.ExecuteNonQuery();
            }
            return;
        }
        Insert(connection, transaction, merge ? "archive_media_merge_receipts" : "archive_media_import_receipts", receipt);
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
