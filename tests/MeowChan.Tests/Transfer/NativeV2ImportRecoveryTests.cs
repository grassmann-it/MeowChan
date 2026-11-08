using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using MeowChan.Archive;
using MeowChan.Persistence;
using MeowChan.Transfer;
using Microsoft.Data.Sqlite;

namespace MeowChan.Tests.Transfer;

// Recovery evidence only: synthetic rows below are NOT an Apply or materialization
// proof. The positive restart case uses the real owner-issued coordinator instead.
public sealed class NativeV2ImportRecoveryTests
{
    private static readonly string ManifestGeneration = new('c', 64);

    [Fact]
    public async Task CommittedV2ImportPassesBothRecoveryGatesAcrossTwoOfflineRestarts()
    {
        await using NativeV2ImportFixture f = await NativeV2ImportFixture.CreateAsync();
        using (ProfileAdmissionToken admission = await f.ExclusiveAsync())
        {
            ProfileExclusiveOutcome<NativeMediaImportCommitResult> outcome = await f.CommitAsync(admission);
            Assert.Equal(NativeMediaImportCommitStatus.Committed, outcome.Result.Status);
            Assert.Equal(ProfileExclusiveDisposition.SafeToResume, outcome.Disposition);
        }
        await AssertImportEvidenceAsync(f, "Committed", receipt: true);
        await f.Stage.DisposeAsync();
        await AssertRecoveryAsync(f.Catalog, blocked: false);
        await f.CloseOwnerAsync();

        for (int restart = 0; restart < 2; restart++)
        {
            using ProfileLease lease = ProfileLease.Acquire(f.Files.Profile);
            await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(f.Files.Profile, lease);
            // OpenAsync alone is NOT a recovery/startup-success assertion.
            await AssertRecoveryAsync(catalog, blocked: false);
            NativeMediaImportCommitResult receipt = Assert.IsType<NativeMediaImportCommitResult>(
                await catalog.ReadNativeMediaImportReceiptAsync(f.Plan.OperationId, f.Plan.RequestSha256, 2));
            Assert.Equal(NativeMediaImportCommitStatus.Committed, receipt.Status);
            Assert.True(receipt.IsReplay);
            Assert.Equal(f.Plan.Representation!.Counts.EvidenceAdditions, receipt.EvidenceAdditions);
            Assert.Equal(f.Plan.Representation.Counts.OriginalBindingCount, receipt.OriginalBindingCount);
        }
    }

    [Theory]
    [InlineData("Preparing", false)]
    [InlineData("Preparing", true)]
    [InlineData("Prepared", false)]
    [InlineData("Prepared", true)]
    public async Task UnfinishedV2ImportBlocksEvenWithAnOtherwiseMatchingReceipt(string state, bool receipt)
    {
        await using NativeV2ImportFixture f = await NativeV2ImportFixture.CreateAsync();
        await SeedImportAsync(f, state, receipt);
        await AssertImportEvidenceAsync(f, state, receipt);
        await AssertRecoveryAsync(f.Catalog, blocked: true);
        // Neither reader upgrades, deletes or resumes the unfinished operation.
        await AssertImportEvidenceAsync(f, state, receipt);
    }

    [Fact]
    public async Task CommittedV2ImportWithoutReceiptBlocks()
    {
        await using NativeV2ImportFixture f = await NativeV2ImportFixture.CreateAsync();
        await SeedImportAsync(f, "Committed", receipt: false);
        await AssertImportEvidenceAsync(f, "Committed", receipt: false);
        await AssertRecoveryAsync(f.Catalog, blocked: true);
        await AssertImportEvidenceAsync(f, "Committed", receipt: false);
    }

    [Theory]
    [InlineData("operation_id")]
    [InlineData("request_sha256")]
    [InlineData("plan_version")]
    [InlineData("post_count")]
    [InlineData("attachment_count")]
    [InlineData("evidence_additions")]
    [InlineData("derived_descriptor_additions")]
    [InlineData("contained_derived_additions")]
    [InlineData("original_binding_count")]
    public async Task IndividuallyValidButMismatchedReceiptBlocksEveryIdentityAndCounter(string field)
    {
        await using NativeV2ImportFixture f = await NativeV2ImportFixture.CreateAsync();
        await SeedImportAsync(f, "Committed", receipt: true);
        await AssertImportEvidenceAsync(f, "Committed", receipt: true);
        await AssertRecoveryAsync(f.Catalog, blocked: false);

        Guid receiptId = field == "operation_id" ? Guid.NewGuid() : f.Plan.OperationId;
        string request = field == "request_sha256" ? new string('9', 64) : f.Plan.RequestSha256;
        int version = field == "plan_version" ? 1 : 2;
        if (field == "operation_id") Assert.NotEqual(f.Plan.OperationId, receiptId);
        if (field == "request_sha256") Assert.NotEqual(f.Plan.RequestSha256, request);
        // Only fixed theory cases select SQL identifiers. Keep CHECK constraints ON;
        // even the version mismatch is a valid v1 receipt with all v2 counters zero.
        string change = field switch
        {
            "operation_id" => "operation_id=$receiptId",
            "request_sha256" => "request_sha256=$request",
            "plan_version" => "plan_version=1,evidence_additions=0,derived_descriptor_additions=0,contained_derived_additions=0,original_binding_count=0",
            "post_count" => "post_count=post_count+1",
            "attachment_count" => "attachment_count=attachment_count+1",
            "evidence_additions" => "evidence_additions=evidence_additions+1",
            "derived_descriptor_additions" => "derived_descriptor_additions=derived_descriptor_additions+1",
            "contained_derived_additions" => "contained_derived_additions=contained_derived_additions-1",
            "original_binding_count" => "original_binding_count=original_binding_count+1",
            _ => throw new ArgumentOutOfRangeException(nameof(field))
        };
        Assert.True(f.Plan.Representation!.Counts.ContainedDerivedAdditions > 0);
        Assert.True(f.Plan.Representation.Counts.AttachmentCount > 0);
        Assert.Equal(1, await f.Catalog.WriteAsync((c, tx) =>
        {
            using SqliteCommand command = ArchiveSql.Command(c, tx,
                $"UPDATE archive_media_import_receipts SET {change} WHERE operation_id=$op");
            command.Parameters.AddWithValue("$op", f.Plan.OperationId.ToString("D"));
            command.Parameters.AddWithValue("$receiptId", receiptId.ToString("D"));
            command.Parameters.AddWithValue("$request", request);
            return command.ExecuteNonQuery();
        }));
        await f.Catalog.ReadAsync(c =>
        {
            using SqliteTransaction tx = c.BeginTransaction(deferred: true);
            NativeMediaPersistedImportOperation operation = Assert.IsType<NativeMediaPersistedImportOperation>(
                ArchiveSql.ReadPersistedMediaImportOperation(c, tx, f.Plan.OperationId, f.Plan.RequestSha256));
            NativeMediaPersistedImportReceipt receipt = Assert.IsType<NativeMediaPersistedImportReceipt>(
                ArchiveSql.ReadPersistedMediaImportReceipt(c, tx, receiptId, request, version));
            Assert.False(ArchiveSql.PersistedMediaImportReceiptMatches(operation, receipt));
            return true;
        });
        await AssertRecoveryAsync(f.Catalog, blocked: true);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(33)]
    [InlineData(65)]
    public async Task ValidOrphanV2ImportReceiptsAreHistoryWithoutOperationsOrCandidateFiles(int count)
    {
        await using NativeV2ImportFixture f = await NativeV2ImportFixture.CreateAsync();
        await SeedImportAsync(f, "Committed", receipt: true);
        await f.Catalog.WriteAsync((c, tx) =>
        {
            for (int index = 1; index < count; index++)
            {
                using SqliteCommand copy = ArchiveSql.Command(c, tx, """
                    INSERT INTO archive_media_import_receipts
                        (operation_id,request_sha256,plan_version,post_count,attachment_count,
                         evidence_additions,derived_descriptor_additions,contained_derived_additions,original_binding_count)
                    SELECT $id,request_sha256,plan_version,post_count,attachment_count,
                        evidence_additions,derived_descriptor_additions,contained_derived_additions,original_binding_count
                    FROM archive_media_import_receipts WHERE operation_id=$op
                    """);
                copy.Parameters.AddWithValue("$id", $"cd330000-0000-4000-8000-{index:x12}");
                copy.Parameters.AddWithValue("$op", f.Plan.OperationId.ToString("D"));
                Assert.Equal(1, copy.ExecuteNonQuery());
            }
            using SqliteCommand remove = ArchiveSql.Command(c, tx, "DELETE FROM archive_media_import_operations");
            Assert.Equal(1, remove.ExecuteNonQuery());
            return true;
        });
        await f.Stage.DisposeAsync();
        Assert.False(Directory.Exists(f.Plan.OperationsParent));
        await AssertRecoveryAsync(f.Catalog, blocked: false);
        await f.Catalog.ReadAsync(c =>
        {
            using SqliteCommand command = ArchiveSql.Command(c, null, """
                SELECT (SELECT count(*) FROM archive_media_import_operations),
                       (SELECT count(*) FROM archive_media_import_receipts)
                """);
            using SqliteDataReader reader = command.ExecuteReader();
            Assert.True(reader.Read());
            Assert.Equal(0L, reader.GetInt64(0));
            Assert.Equal((long)count, reader.GetInt64(1));
            return true;
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task V2MergeCommittedOrOrphanHistoryPassesBothRecoveryGatesWithoutCandidateFiles(bool orphan)
    {
        await using NativeV2ImportFixture f = await NativeV2ImportFixture.CreateAsync();
        await SeedImportAsync(f, "Committed", receipt: true);
        await AssertRecoveryAsync(f.Catalog, blocked: false);
        // Copy authentic identity/Full evidence into synthetic historical v2 merge
        // rows with empty Safe and zero deltas. This is not a merge Apply proof.
        await f.Catalog.WriteAsync((c, tx) =>
        {
            using (SqliteCommand operation = ArchiveSql.Command(c, tx, """
                INSERT INTO archive_media_merge_operations
                    (operation_id,request_sha256,package_fingerprint,catalog_instance,catalog_generation,
                     source,board,thread,operations_parent,imported_at,state,roots_json,full_keys_json,safe_keys_json,
                     catalog_bytes,full_directory,full_manifest_generation,full_bindings_sha256,new_posts,updated_posts,
                     new_attachments,new_bindings,conflicts,plan_version,full_selection_json,full_selection_sha256,
                     full_inventory_sha256,full_member_count)
                SELECT operation_id,request_sha256,package_fingerprint,catalog_instance,catalog_generation,
                    source,board,thread,operations_parent,imported_at,'Committed','[]','[]','[]',
                    catalog_bytes,candidate_directory,manifest_generation,NULL,0,0,0,0,0,2,
                    full_selection_json,full_selection_sha256,full_inventory_sha256,full_member_count
                FROM archive_media_import_operations WHERE operation_id=$op
                """))
            {
                operation.Parameters.AddWithValue("$op", f.Plan.OperationId.ToString("D"));
                Assert.Equal(1, operation.ExecuteNonQuery());
            }
            using (SqliteCommand receipt = ArchiveSql.Command(c, tx, """
                INSERT INTO archive_media_merge_receipts
                    (operation_id,request_sha256,plan_version,new_posts,updated_posts,new_attachments,new_bindings,conflicts)
                SELECT operation_id,request_sha256,2,0,0,0,0,0 FROM archive_media_merge_operations
                """)) Assert.Equal(1, receipt.ExecuteNonQuery());
            // W32b5 admits this exact typed operation/receipt pair as history, not
            // every storage-valid v2 shape. Legacy readers remain gated separately.
            NativeMediaPersistedMergeOperation stored = Assert.IsType<NativeMediaPersistedMergeOperation>(
                ArchiveSql.ReadPersistedMediaMergeOperation(c, tx, f.Plan.OperationId, f.Plan.RequestSha256));
            NativeMediaPersistedMergeReceipt matched = Assert.IsType<NativeMediaPersistedMergeReceipt>(
                ArchiveSql.ReadPersistedMediaMergeReceipt(c, tx, f.Plan.OperationId, f.Plan.RequestSha256, 2));
            Assert.True(ArchiveSql.PersistedMediaMergeReceiptMatches(stored, matched));
            if (orphan)
            {
                using SqliteCommand remove = ArchiveSql.Command(c, tx, "DELETE FROM archive_media_merge_operations");
                Assert.Equal(1, remove.ExecuteNonQuery());
                Assert.Null(ArchiveSql.ReadPersistedMediaMergeOperation(c, tx, f.Plan.OperationId, f.Plan.RequestSha256));
            }
            return true;
        });
        await f.Stage.DisposeAsync();
        Assert.False(Directory.Exists(f.Plan.OperationsParent));
        Assert.Empty(NativeV2ImportFixture.PayloadFiles(f.Files.Profile));
        for (int attempt = 0; attempt < 2; attempt++)
            await AssertRecoveryAsync(f.Catalog, blocked: false);
        await f.Catalog.ReadAsync(c =>
        {
            using SqliteTransaction tx = c.BeginTransaction(deferred: true);
            NativeMediaPersistedMergeOperation? operation = ArchiveSql.ReadPersistedMediaMergeOperation(
                c, tx, f.Plan.OperationId, f.Plan.RequestSha256);
            NativeMediaPersistedMergeReceipt receipt = Assert.IsType<NativeMediaPersistedMergeReceipt>(
                ArchiveSql.ReadPersistedMediaMergeReceipt(c, tx, f.Plan.OperationId, f.Plan.RequestSha256, 2));
            if (orphan) Assert.Null(operation);
            else Assert.True(ArchiveSql.PersistedMediaMergeReceiptMatches(
                Assert.IsType<NativeMediaPersistedMergeOperation>(operation), receipt));
            Assert.Equal(new NativeMediaMergeResult(NativeMediaImportCommitStatus.Committed, IsReplay: true),
                ArchiveSql.ReadMediaMergeReceiptV2(c, tx, f.Plan.OperationId, f.Plan.RequestSha256));
            Assert.False(ArchiveSql.HasIncompatibleMediaTransferHistory(c, tx));
            Assert.True(ArchiveSql.MediaImportResourceAbsent(c, tx, f.Plan.Resource));
            return true;
        });
    }

    [Theory]
    [InlineData("Preparing", false)]
    [InlineData("Preparing", true)]
    [InlineData("Prepared", false)]
    [InlineData("Prepared", true)]
    [InlineData("Committed", false)]
    public async Task NonterminalOrUnreceiptedV2MergeStillBlocksBothRecoveryGates(string state, bool receipt)
    {
        await using NativeV2ImportFixture f = await NativeV2ImportFixture.CreateAsync();
        // A healthy import receipt must not hide a separate unfinished/unmatched merge.
        await SeedImportAsync(f, "Committed", receipt: true);
        await AssertRecoveryAsync(f.Catalog, blocked: false);
        Assert.NotEqual(f.Plan.OperationId, PersistedMediaFixture.OperationId);
        await f.Catalog.WriteAsync((c, tx) =>
        {
            Dictionary<string, object?> row = PersistedMediaFixture.Row(merge: true, state: state);
            InsertMergeHistory(c, tx, "archive_media_merge_operations", row);
            if (receipt) InsertMergeHistory(c, tx, "archive_media_merge_receipts", PersistedMediaFixture.Receipt(true, row));
            AssertUnreconciledMergeEvidence(c, tx, state, receipt);
            return true;
        });
        await AssertRecoveryAsync(f.Catalog, blocked: true);
        // Both recovery entrypoints must leave the exact state/receipt evidence intact.
        await f.Catalog.ReadAsync(c =>
        {
            using SqliteTransaction tx = c.BeginTransaction(deferred: true);
            AssertUnreconciledMergeEvidence(c, tx, state, receipt);
            return true;
        });
        Assert.False(Directory.Exists(f.Plan.OperationsParent));
        Assert.Empty(NativeV2ImportFixture.PayloadFiles(f.Files.Profile));
    }

    private static void AssertUnreconciledMergeEvidence(SqliteConnection connection, SqliteTransaction transaction,
        string state, bool receipt)
    {
        NativeMediaPersistedMergeOperation operation = Assert.IsType<NativeMediaPersistedMergeOperation>(
            ArchiveSql.ReadPersistedMediaMergeOperation(connection, transaction,
                PersistedMediaFixture.OperationId, PersistedMediaFixture.Request));
        Assert.Equal(state, operation.State.ToString());
        NativeMediaPersistedMergeReceipt? found = ArchiveSql.ReadPersistedMediaMergeReceipt(connection, transaction,
            PersistedMediaFixture.OperationId, PersistedMediaFixture.Request, 2);
        if (receipt)
        {
            Assert.NotNull(found);
            Assert.Equal(operation.Counts, found.Counts);
            Assert.False(ArchiveSql.PersistedMediaMergeReceiptMatches(operation, found));
        }
        else Assert.Null(found);
        Assert.True(ArchiveSql.HasIncompatibleMediaTransferHistory(connection, transaction));
    }

    private static void InsertMergeHistory(SqliteConnection connection, SqliteTransaction transaction, string table,
        Dictionary<string, object?> row)
    {
        // Fixed synthetic table/column names; no package-controlled SQL identifiers.
        using SqliteCommand command = ArchiveSql.Command(connection, transaction,
            $"INSERT INTO {table} ({string.Join(",", row.Keys)}) VALUES ({string.Join(",", row.Keys.Select(key => "$" + key))})");
        foreach (var field in row) command.Parameters.AddWithValue("$" + field.Key, field.Value ?? DBNull.Value);
        Assert.Equal(1, command.ExecuteNonQuery());
    }

    [Fact]
    public async Task CancellationPropagatesWithoutConsumingTheCallerTransactionOrChangingHistory()
    {
        await using NativeV2ImportFixture f = await NativeV2ImportFixture.CreateAsync();
        await SeedImportAsync(f, "Committed", receipt: true);
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        await f.Catalog.ReadAsync(c =>
        {
            using SqliteTransaction tx = c.BeginTransaction(deferred: true);
            Assert.ThrowsAny<OperationCanceledException>(() =>
                ArchiveSql.HasNativeMediaTransferRecoveryBlocker(c, tx, cancellation.Token));
            using SqliteCommand probe = ArchiveSql.Command(c, tx, "SELECT 1");
            Assert.Equal(1L, probe.ExecuteScalar());
            tx.Rollback();
            Assert.ThrowsAny<OperationCanceledException>(() =>
                ArchiveSql.HasNativeMediaTransferRecoveryBlocker(c, null, cancellation.Token));
            return true;
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            f.Catalog.EnsureNativeMediaRecoveryClearAsync(cancellation.Token));
        await AssertRecoveryAsync(f.Catalog, blocked: false);
    }

    private static async Task AssertRecoveryAsync(ArchiveCatalog catalog, bool blocked)
    {
        byte[] before = SHA256.HashData(File.ReadAllBytes(catalog.DatabasePath));
        await catalog.ReadAsync(c =>
        {
            using (SqliteTransaction tx = c.BeginTransaction(deferred: true))
            {
                Assert.Equal(blocked, ArchiveSql.HasNativeMediaTransferRecoveryBlocker(c, tx));
                using SqliteCommand probe = ArchiveSql.Command(c, tx, "SELECT 1");
                Assert.Equal(1L, probe.ExecuteScalar()); // Caller still owns the transaction.
                tx.Rollback();
            }
            Assert.Equal(blocked, ArchiveSql.HasNativeMediaTransferRecoveryBlocker(c, null));
            return true;
        });
        if (blocked)
        {
            InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(
                () => catalog.EnsureNativeMediaRecoveryClearAsync());
            Assert.Equal("Native media transfer reconciliation is required.", error.Message);
            Assert.Null(error.InnerException);
        }
        else await catalog.EnsureNativeMediaRecoveryClearAsync();
        Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(catalog.DatabasePath)));
    }

    private static Task<bool> AssertImportEvidenceAsync(NativeV2ImportFixture f, string state, bool receipt) =>
        f.Catalog.ReadAsync(c =>
        {
            using SqliteTransaction tx = c.BeginTransaction(deferred: true);
            NativeMediaPersistedImportOperation operation = Assert.IsType<NativeMediaPersistedImportOperation>(
                ArchiveSql.ReadPersistedMediaImportOperation(c, tx, f.Plan.OperationId, f.Plan.RequestSha256));
            Assert.Equal(state, operation.State.ToString());
            Assert.True(f.Plan.Representation!.MatchesOperation(operation, operation.State));
            NativeMediaPersistedImportReceipt? found = ArchiveSql.ReadPersistedMediaImportReceipt(
                c, tx, f.Plan.OperationId, f.Plan.RequestSha256, 2);
            if (receipt)
            {
                Assert.NotNull(found);
                Assert.Equal(operation.Counts, found.Counts);
                Assert.Equal(state == "Committed", ArchiveSql.PersistedMediaImportReceiptMatches(operation, found));
            }
            else Assert.Null(found);
            return true;
        });

    private static Task<bool> SeedImportAsync(NativeV2ImportFixture f, string state, bool receipt) =>
        f.Catalog.WriteAsync((c, tx) =>
        {
            Assert.Contains(state, new[] { "Preparing", "Prepared", "Committed" });
            NativeMediaImportV2State v2 = f.Plan.Representation!;
            bool ready = state != "Preparing";
            using (SqliteCommand operation = ArchiveSql.Command(c, tx, """
                INSERT INTO archive_media_import_operations
                    (operation_id,request_sha256,package_fingerprint,catalog_instance,catalog_generation,
                     source,board,thread,operations_parent,imported_at,state,revision_keys_json,plan_version,
                     full_selection_json,full_selection_sha256,full_member_count,catalog_bytes,
                     candidate_directory,manifest_generation,full_inventory_sha256,post_count,attachment_count,
                     evidence_additions,derived_descriptor_additions,contained_derived_additions,original_binding_count)
                VALUES ($op,$request,$fingerprint,$instance,$generation,$source,$board,$thread,$parent,$at,$state,'[]',2,
                    $selection,$selectionHash,$members,$catalog,$directory,$manifest,$inventory,$posts,$attachments,
                    $evidence,$derived,$contained,$bindings)
                """))
            {
                operation.Parameters.AddWithValue("$op", f.Plan.OperationId.ToString("D"));
                operation.Parameters.AddWithValue("$request", f.Plan.RequestSha256);
                operation.Parameters.AddWithValue("$fingerprint", v2.Identity.PackageFingerprint);
                operation.Parameters.AddWithValue("$instance", f.Plan.Instance);
                operation.Parameters.AddWithValue("$generation", f.Plan.Generation);
                ArchiveSql.AddId(operation, f.Plan.Resource);
                operation.Parameters.AddWithValue("$parent", f.Plan.OperationsParent);
                operation.Parameters.AddWithValue("$at", ArchiveSql.Date(f.Plan.ImportedAtUtc));
                operation.Parameters.AddWithValue("$state", state);
                operation.Parameters.AddWithValue("$selection", v2.Selection.CanonicalJson);
                operation.Parameters.AddWithValue("$selectionHash", v2.Selection.Sha256);
                operation.Parameters.AddWithValue("$members", v2.Selection.Members.Length);
                operation.Parameters.AddWithValue("$catalog", v2.CatalogBytes.ToArray());
                operation.Parameters.AddWithValue("$directory", ready ? Path.Combine(f.Plan.OperationsParent, "synthetic-unmaterialized") : DBNull.Value);
                operation.Parameters.AddWithValue("$manifest", ready ? ManifestGeneration : DBNull.Value);
                operation.Parameters.AddWithValue("$inventory", ready ? v2.Inventory.Sha256 : DBNull.Value);
                operation.Parameters.AddWithValue("$posts", v2.Counts.PostCount);
                operation.Parameters.AddWithValue("$attachments", v2.Counts.AttachmentCount);
                operation.Parameters.AddWithValue("$evidence", v2.Counts.EvidenceAdditions);
                operation.Parameters.AddWithValue("$derived", v2.Counts.DerivedDescriptorAdditions);
                operation.Parameters.AddWithValue("$contained", v2.Counts.ContainedDerivedAdditions);
                operation.Parameters.AddWithValue("$bindings", v2.Counts.OriginalBindingCount);
                Assert.Equal(1, operation.ExecuteNonQuery());
            }
            if (receipt)
            {
                using SqliteCommand add = ArchiveSql.Command(c, tx, """
                    INSERT INTO archive_media_import_receipts
                        (operation_id,request_sha256,plan_version,post_count,attachment_count,
                         evidence_additions,derived_descriptor_additions,contained_derived_additions,original_binding_count)
                    SELECT operation_id,request_sha256,plan_version,post_count,attachment_count,
                        evidence_additions,derived_descriptor_additions,contained_derived_additions,original_binding_count
                    FROM archive_media_import_operations WHERE operation_id=$op
                    """);
                add.Parameters.AddWithValue("$op", f.Plan.OperationId.ToString("D"));
                Assert.Equal(1, add.ExecuteNonQuery());
            }
            return true;
        });
}
