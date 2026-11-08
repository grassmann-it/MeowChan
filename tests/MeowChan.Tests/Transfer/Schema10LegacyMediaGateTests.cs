using MeowChan.Archive;
using MeowChan.Persistence;
using MeowChan.Storage;
using MeowChan.Transfer;
using Microsoft.Data.Sqlite;
using static MeowChan.Tests.Transfer.NativeMediaImportTests;

namespace MeowChan.Tests.Transfer;

// Storage10 shapes do not grant legacy v2 execution. All rows below are synthetic,
// inserted with CHECKs enabled into the real ArchiveCatalog-created schema. The v2
// operation selection/catalog documents are deliberately not semantically valid;
// their content-free orphan receipts can still be valid W32b5 history. No Ready files.
public sealed class Schema10LegacyMediaGateTests
{
    private static readonly Guid Operation = Guid.Parse("10000000-0000-4000-8000-000000000001");
    private static readonly Guid LaterOperation = Guid.Parse("20000000-0000-4000-8000-000000000002");
    private static readonly string Request = new('a', 64);
    private static readonly string Fingerprint = new('b', 64);
    private static readonly string Generation = new('c', 64);
    private static readonly string SelectionHash = new('d', 64);
    private static readonly string InventoryHash = new('e', 64);
    private static readonly string RevisionKey = "att-v2:" + new string('f', 64);
    private const string VersionGate = "This media transfer plan requires version-aware reconciliation.";

    [Theory]
    [InlineData(false, 0)]
    [InlineData(false, 1)]
    [InlineData(true, 0)]
    public async Task LegacyReceiptReadersRejectV2HistoricalRowsRatherThanProjectingCounts(bool merge, int attachments)
    {
        using TempDirectory files = new();
        using ProfileLease lease = ProfileLease.Acquire(files.Path);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(files.Path, lease);
        await Seed(catalog, merge, 2, state: null, receipt: true, attachments: attachments);
        byte[] before = File.ReadAllBytes(catalog.DatabasePath);

        using (SqliteConnection connection = OpenRead(catalog))
        {
            using SqliteTransaction transaction = connection.BeginTransaction(deferred: true);
            AssertVersionGate(Assert.Throws<InvalidDataException>(() => ReadReceipt(connection, transaction, merge)));
            transaction.Commit();
            AssertVersionGate(Assert.Throws<InvalidDataException>(() => ReadReceipt(connection, null, merge)));
            Assert.Null(ReadOperation(connection, merge, Operation)); // The receipt really is orphaned.
        }
        if (merge)
            AssertVersionGate(await Assert.ThrowsAsync<InvalidDataException>(
                () => catalog.ReadNativeMediaMergeReceiptAsync(Operation, Request)));
        else
            AssertVersionGate(await Assert.ThrowsAsync<InvalidDataException>(
                () => catalog.ReadNativeMediaImportReceiptAsync(Operation, Request)));
        Assert.Equal(before, File.ReadAllBytes(catalog.DatabasePath));
    }

    [Theory]
    [InlineData(false, "Preparing")]
    [InlineData(false, "Prepared")]
    [InlineData(false, "Committed")]
    [InlineData(true, "Preparing")]
    [InlineData(true, "Prepared")]
    [InlineData(true, "Committed")]
    public async Task LegacyOperationAndPageReadersRejectEveryV2State(bool merge, string state)
    {
        using TempDirectory files = new();
        using ProfileLease lease = ProfileLease.Acquire(files.Path);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(files.Path, lease);
        // Merge has a nonempty Safe selection with zero original bindings. Storage10
        // permits that shape, but the legacy reader must not interpret it as empty Safe.
        await Seed(catalog, merge, 2, state, receipt: false, attachments: merge ? 1 : 0, safeMembers: merge ? 1 : 0);
        byte[] before = File.ReadAllBytes(catalog.DatabasePath);
        using (SqliteConnection connection = OpenRead(catalog))
        {
            AssertVersionGate(Assert.Throws<InvalidDataException>(() => ReadOperation(connection, merge, Operation)));
            AssertVersionGate(Assert.Throws<InvalidDataException>(() => ReadPage(connection, merge)));
        }
        if (merge)
            AssertVersionGate(await Assert.ThrowsAsync<InvalidDataException>(
                () => catalog.ReadNativeMediaMergeOperationsPageAsync()));
        else
            AssertVersionGate(await Assert.ThrowsAsync<InvalidDataException>(
                () => catalog.ReadNativeMediaImportOperationsPageAsync()));
        Assert.Equal(before, File.ReadAllBytes(catalog.DatabasePath));
        Assert.False(Directory.Exists(Parent(files.Path, Operation)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LegacyPageCannotSilentlySkipV2BehindAValidV1Row(bool merge)
    {
        using TempDirectory files = new();
        using ProfileLease lease = ProfileLease.Acquire(files.Path);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(files.Path, lease);
        await Seed(catalog, merge, 1, "Preparing", receipt: false);
        await Seed(catalog, merge, 2, "Preparing", receipt: false, operation: LaterOperation);
        using SqliteConnection connection = OpenRead(catalog);
        Assert.NotNull(ReadOperation(connection, merge, Operation));
        AssertVersionGate(Assert.Throws<InvalidDataException>(() => ReadPage(connection, merge)));
        AssertVersionGate(Assert.Throws<InvalidDataException>(() => ReadPage(connection, merge, Operation)));
    }

    [Theory]
    [InlineData(false, "Preparing")]
    [InlineData(false, "Prepared")]
    [InlineData(false, "Committed")]
    [InlineData(true, "Preparing")]
    [InlineData(true, "Prepared")]
    [InlineData(true, "Committed")]
    public async Task LegacyV1OperationsAndReceiptsStillDecodeOnSchema10(bool merge, string state)
    {
        using TempDirectory files = new();
        using ProfileLease lease = ProfileLease.Acquire(files.Path);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(files.Path, lease);
        await Seed(catalog, merge, 1, state, receipt: true);
        using SqliteConnection connection = OpenRead(catalog);
        NativeMediaImportOperationState expectedState = Enum.Parse<NativeMediaImportOperationState>(state);
        string? expectedDirectory = state == "Preparing" ? null : Path.Combine(Parent(files.Path, Operation), "full");
        if (merge)
        {
            NativeMediaMergeOperation found = Assert.IsType<NativeMediaMergeOperation>(ReadOperation(connection, true, Operation));
            Assert.Equal(Operation, found.OperationId);
            Assert.Equal(Request, found.RequestSha256);
            Assert.Equal(expectedState, found.State);
            Assert.Equal(expectedDirectory, found.FullDirectory);
            Assert.Null(found.SafeDirectory);
            Assert.Equal(RevisionKey, Assert.Single(found.FullKeys));
            Assert.Empty(found.SafeKeys);
            Assert.Equal(new byte[] { 0x7b, 0x7d }, found.CatalogBytes.ToArray());
            NativeMediaMergeOperationPage page = ArchiveSql.ReadMediaMergeOperationsPage(connection, null);
            Assert.Equal(Operation, Assert.Single(page.Items).OperationId);
            Assert.Null(page.NextOperationId);
            Assert.Equal(Operation, Assert.Single((await catalog.ReadNativeMediaMergeOperationsPageAsync()).Items).OperationId);
            NativeMediaMergeResult expected = new(NativeMediaImportCommitStatus.Committed, 2, 3, 1, 0, 1, IsReplay: true);
            Assert.Equal(expected, Assert.IsType<NativeMediaMergeResult>(ReadReceipt(connection, null, true)));
            Assert.Equal(expected, await catalog.ReadNativeMediaMergeReceiptAsync(Operation, Request));
        }
        else
        {
            NativeMediaImportOperation found = Assert.IsType<NativeMediaImportOperation>(ReadOperation(connection, false, Operation));
            Assert.Equal(Operation, found.OperationId);
            Assert.Equal(Request, found.RequestSha256);
            Assert.Equal(Fingerprint, found.PackageFingerprint);
            Assert.Equal(expectedState, found.State);
            Assert.Equal(expectedDirectory, found.CandidateDirectory);
            Assert.Equal(2, found.PostCount);
            Assert.Equal(1, found.AttachmentCount);
            NativeMediaImportOperationPage page = ArchiveSql.ReadMediaImportOperationsPage(connection, null);
            Assert.Equal(found, Assert.Single(page.Items));
            Assert.Null(page.NextOperationId);
            Assert.Equal(found, Assert.Single((await catalog.ReadNativeMediaImportOperationsPageAsync()).Items));
            NativeMediaImportCommitResult expected = new(NativeMediaImportCommitStatus.Committed, 1, 2, 1, IsReplay: true);
            Assert.Equal(expected, Assert.IsType<NativeMediaImportCommitResult>(ReadReceipt(connection, null, false)));
            Assert.Equal(expected, await catalog.ReadNativeMediaImportReceiptAsync(Operation, Request));
        }
    }

    [Theory]
    [InlineData(false, null, 2)]
    [InlineData(true, null, 2)]
    [InlineData(false, "Preparing", 2)]
    [InlineData(true, "Preparing", 2)]
    [InlineData(false, "Prepared", 2)]
    [InlineData(true, "Prepared", 2)]
    [InlineData(false, "Committed", 2)]
    [InlineData(true, "Committed", 2)]
    [InlineData(false, "Committed", 1)]
    [InlineData(true, "Committed", 1)]
    public async Task StartupDispatchesV2EvidenceWithoutGrantingLegacyExecution(bool merge, string? state, int receiptVersion)
    {
        using TempDirectory files = new();
        using ProfileLease lease = ProfileLease.Acquire(files.Path);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(files.Path, lease);
        // A healthy opposite-family committed operation cannot conceal the v2 row.
        await Seed(catalog, !merge, 1, "Committed", receipt: true);
        await catalog.EnsureNativeMediaRecoveryClearAsync();
        if (state != null) await Seed(catalog, merge, 2, state, receipt: false);
        // A legacy receipt with identical old counters cannot bless a v2 operation.
        await Seed(catalog, merge, receiptVersion, state: null, receipt: true);
        byte[] before = File.ReadAllBytes(catalog.DatabasePath);
        string[] paths = Directory.GetFileSystemEntries(files.Path, "*", SearchOption.AllDirectories).Order().ToArray();
        bool compatibleOrphan = state is null && receiptVersion == 2;
        using (SqliteConnection connection = OpenRead(catalog))
        {
            Assert.True(ArchiveSql.HasUnsupportedMediaTransferRecords(connection, null));
            Assert.Equal(!compatibleOrphan, ArchiveSql.HasIncompatibleMediaTransferHistory(connection, null));
            if (merge && compatibleOrphan)
            {
                NativeMediaPersistedMergeReceipt receipt = Assert.IsType<NativeMediaPersistedMergeReceipt>(
                    ArchiveSql.ReadPersistedMediaMergeReceipt(connection, null, Operation, Request, 2));
                Assert.Equal(2, receipt.Identity.PlanVersion);
                Assert.Equal(new NativeMediaMergeCounts(2, 3, 1, 0, 1, 2, 3, 1), receipt.Counts);
                Assert.Null(ArchiveSql.ReadPersistedMediaMergeOperation(connection, null, Operation, Request));
                AssertVersionGate(Assert.Throws<InvalidDataException>(() => ReadReceipt(connection, null, merge)));
            }
            if (state == "Committed" && receiptVersion == 2)
            {
                // The '{}' operation is malformed in either family. Matching scalar
                // receipt counters must not turn it into valid committed history.
                if (merge) Assert.Throws<InvalidDataException>(() =>
                    ArchiveSql.ReadPersistedMediaMergeOperation(connection, null, Operation, Request));
                else Assert.Throws<InvalidDataException>(() =>
                    ArchiveSql.ReadPersistedMediaImportOperation(connection, null, Operation, Request));
            }
        }
        for (int attempt = 0; attempt < 2; attempt++)
        {
            if (compatibleOrphan)
            {
                // W32b5 supports valid orphan receipts from BOTH v2 families as history.
                // Strict legacy discovery remains true; no v1 execution or operation/
                // payload reconstruction is granted by this recovery-gate read.
                await catalog.EnsureNativeMediaRecoveryClearAsync();
            }
            else
            {
                InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(
                    () => catalog.EnsureNativeMediaRecoveryClearAsync());
                Assert.Equal("Native media transfer reconciliation is required.", error.Message);
                Assert.Null(error.InnerException);
            }
        }
        Assert.Equal(before, File.ReadAllBytes(catalog.DatabasePath));
        Assert.Equal(paths, Directory.GetFileSystemEntries(files.Path, "*", SearchOption.AllDirectories).Order().ToArray());
    }

    [Theory]
    [InlineData(false, null)]
    [InlineData(true, null)]
    [InlineData(false, "Committed")]
    [InlineData(true, "Committed")]
    public async Task V1HistoricalOrMatchedCommittedReceiptStillAllowsStartupAfterReopen(bool merge, string? state)
    {
        using TempDirectory files = new();
        using ProfileLease lease = ProfileLease.Acquire(files.Path);
        await using (ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(files.Path, lease))
        {
            await catalog.EnsureNativeMediaRecoveryClearAsync();
            await Seed(catalog, merge, 1, state, receipt: true);
            await catalog.EnsureNativeMediaRecoveryClearAsync();
        }
        await using ArchiveCatalog reopened = await ArchiveCatalog.OpenAsync(files.Path, lease);
        byte[] before = File.ReadAllBytes(reopened.DatabasePath);
        using (SqliteConnection connection = OpenRead(reopened))
        {
            Assert.False(ArchiveSql.HasUnsupportedMediaTransferRecords(connection, null));
            Assert.NotNull(ReadReceipt(connection, null, merge));
        }
        await reopened.EnsureNativeMediaRecoveryClearAsync();
        Assert.Equal(before, File.ReadAllBytes(reopened.DatabasePath));
    }

    [Theory]
    [InlineData(false, 1, null, 1)]
    [InlineData(true, 1, null, 1)]
    [InlineData(false, 2, null, 2)]
    [InlineData(true, 2, null, 2)]
    [InlineData(false, 1, "Committed", 1)]
    [InlineData(true, 1, "Committed", 1)]
    [InlineData(false, 2, "Committed", 2)]
    [InlineData(true, 2, "Committed", 2)]
    [InlineData(false, 2, "Committed", 1)]
    [InlineData(true, 2, "Committed", 1)]
    [InlineData(false, 2, "Preparing", 2)]
    [InlineData(true, 2, "Preparing", 2)]
    [InlineData(false, 2, "Prepared", 2)]
    [InlineData(true, 2, "Prepared", 2)]
    public async Task IndependentImportReconciliationUsesTheSameVersionAwareHistoryGateAsStartup(bool merge, int version, string? state, int receiptVersion)
    {
        // Reuse the existing real writer/stage/admission harness. A deliberate pre-claim
        // failure reaches independent reconciliation with no claim ambiguity, no reflection
        // and no startup-reader call. This is a storage-owner test, not Registry startup.
        using Workspace files = new();
        using ProfileLease lease = ProfileLease.Acquire(files.Profile);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(files.Profile, lease);
        await using NativeTransferImportStage stage = await Stage(files, Content());
        NativeMediaImportPreparation prepared = await catalog.PrepareNativeMediaImportAsync(stage);
        Assert.Equal(NativeMediaImportError.None, prepared.Error);
        NativeMediaImportPlan plan = Assert.IsType<NativeMediaImportPlan>(prepared.Plan);
        Assert.NotEqual(Operation, plan.OperationId);
        if (state != null) await Seed(catalog, merge, version, state, receipt: false);
        await Seed(catalog, merge, receiptVersion, state: null, receipt: true);
        lease.Admission.CompleteStartup();
        using ProfileAdmissionToken owner = await lease.Admission.EnterExclusiveAsync();
        Dictionary<string, string> before = Files(files.Profile);
        int beforeAdmission = 0, checkpoints = 0, dispatches = 0;
        catalog.BeforeWriteDispatchForTests = _ => dispatches++;
        ProfileExclusiveOutcome<NativeMediaImportCommitResult> outcome = await catalog.CommitNativeMediaImportAdmittedAsync(
            owner, plan, new MediaStore(), checkpoint: _ => checkpoints++, beforeAdmission: () =>
            {
                beforeAdmission++;
                throw new IOException("Synthetic pre-claim interruption.");
            });
        Assert.Equal(1, beforeAdmission);
        Assert.Equal(0, checkpoints);
        Assert.Equal(0, dispatches);
        // Both families' valid orphan v2 receipts are historical evidence. Every v2
        // operation seeded HERE is deliberately malformed ('{}'), or unfinished;
        // committed fixtures with a v1 receipt also remain version mismatches.
        bool blocked = version == 2 && state is not null;
        using (SqliteConnection connection = OpenRead(catalog))
            Assert.Equal(blocked, ArchiveSql.HasIncompatibleMediaTransferHistory(connection, null));
        Assert.Equal(blocked ? ProfileExclusiveDisposition.RecoveryRequired : ProfileExclusiveDisposition.SafeToResume,
            outcome.Disposition);
        Assert.Equal(blocked ? NativeMediaImportCommitStatus.ReconciliationRequired : NativeMediaImportCommitStatus.Inactive,
            outcome.Result.Status);
        Assert.False(outcome.Result.IsReplay);
        Assert.Null(await catalog.ReadNativeMediaImportReceiptAsync(plan.OperationId, plan.RequestSha256));
        Assert.False(Directory.Exists(plan.OperationsParent));
        Assert.Equal(before, Files(files.Profile));
        catalog.EnsureTransferAdmission(owner); // Borrowed authority remains owned by this test.
    }

    private static void AssertVersionGate(InvalidDataException error)
    {
        // Especially for merge, a generic key/catalog parse failure is not proof of
        // version dispatch: [] is mandatory v2 legacy-key storage and must not be decoded.
        Assert.Equal(VersionGate, error.Message);
        Assert.Null(error.InnerException);
    }

    private static SqliteConnection OpenRead(ArchiveCatalog catalog)
    {
        SqliteConnection connection = ArchiveSql.OpenConnection(catalog.DatabasePath, SqliteOpenMode.ReadOnly);
        try
        {
            Assert.Equal(11, ArchiveSql.GetPragma(connection, "user_version"));
            Assert.Equal(0, ArchiveSql.GetPragma(connection, "ignore_check_constraints"));
            ArchiveSql.ValidateSchema(connection);
            return connection;
        }
        catch { connection.Dispose(); throw; }
    }

    private static object? ReadReceipt(SqliteConnection connection, SqliteTransaction? transaction, bool merge)
        => merge ? ArchiveSql.ReadMediaMergeReceipt(connection, transaction, Operation, Request)
            : ArchiveSql.ReadMediaImportReceipt(connection, transaction, Operation, Request);

    private static object? ReadOperation(SqliteConnection connection, bool merge, Guid operation)
        => merge ? ArchiveSql.ReadMediaMergeOperation(connection, null, operation, Request)
            : ArchiveSql.ReadMediaImportOperation(connection, null, operation, Request);

    private static object ReadPage(SqliteConnection connection, bool merge, Guid? after = null)
        => merge ? ArchiveSql.ReadMediaMergeOperationsPage(connection, after)
            : ArchiveSql.ReadMediaImportOperationsPage(connection, after);

    private static string Parent(string profile, Guid operation) => Path.Combine(profile, "never-created-schema10", operation.ToString("N"));

    private static Task Seed(ArchiveCatalog catalog, bool merge, int version, string? state, bool receipt,
        int attachments = 1, int safeMembers = 0, Guid? operation = null)
        => catalog.WriteAsync((connection, transaction) =>
        {
            Assert.Equal(11, ArchiveSql.GetPragma(connection, "user_version", transaction));
            Assert.Equal(0, ArchiveSql.GetPragma(connection, "ignore_check_constraints", transaction));
            Guid id = operation ?? Operation;
            string table = merge ? "archive_media_merge" : "archive_media_import";
            bool v2 = version == 2;
            if (state != null)
            {
                bool ready = state != "Preparing";
                string parent = Parent(Path.GetDirectoryName(catalog.DatabasePath)!, id);
                string columns = merge
                    ? "roots_json,full_keys_json,safe_keys_json,catalog_bytes,full_directory,full_manifest_generation,full_bindings_sha256,new_posts,updated_posts,new_attachments,new_bindings,conflicts,safe_selection_json,safe_selection_sha256,safe_member_count,safe_directory,safe_manifest_generation,safe_inventory_sha256"
                    : "revision_keys_json,catalog_bytes,candidate_directory,manifest_generation,bindings_sha256,post_count,attachment_count,original_binding_count";
                string values = merge
                    ? "'[]',$keys,'[]',$catalog,$directory,$generation,$bindings,2,3,$attachments,0,1,$safeSelection,$safeHash,$safeMembers,$safeDirectory,$safeGeneration,$safeInventory"
                    : "$keys,$catalog,$directory,$generation,$bindings,2,$attachments,$originalBindings";
                using SqliteCommand command = ArchiveSql.Command(connection, transaction, $"""
                    INSERT INTO {table}_operations(operation_id,request_sha256,package_fingerprint,catalog_instance,
                        catalog_generation,source,board,thread,operations_parent,imported_at,state,plan_version,
                        full_selection_json,full_selection_sha256,full_inventory_sha256,full_member_count,
                        evidence_additions,derived_descriptor_additions,contained_derived_additions,{columns})
                    VALUES($op,$request,$fingerprint,$instance,0,'fixture','b','unrelated-resource',$parent,$at,$state,$version,
                        $selection,$selectionHash,$inventory,$members,$evidence,$derived,$contained,{values})
                    """);
                Add(command, "$op", id.ToString("D"));
                Add(command, "$request", Request);
                Add(command, "$fingerprint", Fingerprint);
                Add(command, "$instance", "30000000-0000-4000-8000-000000000003");
                Add(command, "$parent", parent);
                Add(command, "$at", "2026-11-08T00:00:00.0000000+00:00");
                Add(command, "$state", state);
                Add(command, "$version", version);
                Add(command, "$selection", v2 ? "{}" : null);
                Add(command, "$selectionHash", v2 ? SelectionHash : null);
                Add(command, "$inventory", v2 && ready ? InventoryHash : null);
                Add(command, "$members", v2 ? 1 : 0);
                Add(command, "$evidence", v2 ? 2 : 0);
                Add(command, "$derived", v2 ? 3 : 0);
                Add(command, "$contained", v2 ? 1 : 0);
                Add(command, "$keys", v2 ? "[]" : "[\"" + RevisionKey + "\"]");
                Add(command, "$catalog", merge || v2 ? new byte[] { 0x7b, 0x7d } : null);
                Add(command, "$directory", ready ? Path.Combine(parent, "full") : null);
                Add(command, "$generation", ready ? Generation : null);
                Add(command, "$bindings", !v2 && ready ? Generation : null);
                Add(command, "$attachments", attachments);
                if (merge)
                {
                    Add(command, "$safeSelection", v2 && safeMembers > 0 ? "{}" : null);
                    Add(command, "$safeHash", v2 && safeMembers > 0 ? SelectionHash : null);
                    Add(command, "$safeMembers", safeMembers);
                    Add(command, "$safeDirectory", ready && safeMembers > 0 ? Path.Combine(parent, "safe") : null);
                    Add(command, "$safeGeneration", ready && safeMembers > 0 ? Generation : null);
                    Add(command, "$safeInventory", ready && safeMembers > 0 ? InventoryHash : null);
                }
                else Add(command, "$originalBindings", v2 ? attachments : 0);
                Assert.Equal(1, command.ExecuteNonQuery());
            }
            if (receipt)
            {
                string columns = merge ? "new_posts,updated_posts,new_attachments,new_bindings,conflicts"
                    : "post_count,attachment_count,original_binding_count";
                string values = merge ? "2,3,$attachments,0,1" : "2,$attachments,$originalBindings";
                using SqliteCommand command = ArchiveSql.Command(connection, transaction, $"""
                    INSERT INTO {table}_receipts(operation_id,request_sha256,plan_version,
                        evidence_additions,derived_descriptor_additions,contained_derived_additions,{columns})
                    VALUES($op,$request,$version,$evidence,$derived,$contained,{values})
                    """);
                Add(command, "$op", id.ToString("D"));
                Add(command, "$request", Request);
                Add(command, "$version", version);
                Add(command, "$evidence", v2 ? 2 : 0);
                Add(command, "$derived", v2 ? 3 : 0);
                Add(command, "$contained", v2 ? 1 : 0);
                Add(command, "$attachments", attachments);
                if (!merge) Add(command, "$originalBindings", v2 ? attachments : 0);
                Assert.Equal(1, command.ExecuteNonQuery());
            }
            return true;
        });

    private static void Add(SqliteCommand command, string name, object? value)
        => command.Parameters.AddWithValue(name, value ?? DBNull.Value);
}
