using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeowChan.Archive;
using MeowChan.Persistence;
using MeowChan.Transfer;
using Microsoft.Data.Sqlite;

namespace MeowChan.Tests.Transfer;

// Read-only v2 persistence inspection boundary. These tests deliberately keep the
// inspection surface separate from the legacy readers and from Apply/Resume authority.
public sealed class NativeMediaV2InspectionTests
{
    [Fact]
    public void V2ReaderFiltersAtSqlBoundaryAndLegacyReaderStillSeesOnlyV1()
    {
        using PersistedMediaFixture fixture = new();
        Dictionary<string, object?> v2 = PersistedMediaFixture.Row(false, version: 2,
            state: "Prepared", operation: PersistedMediaFixture.OperationId);
        Guid v1Operation = Guid.Parse("cd220000-0000-4000-8000-000000000003");
        Dictionary<string, object?> v1 = PersistedMediaFixture.Row(false, version: 1,
            state: "Prepared", operation: v1Operation);
        fixture.Insert(false, v2);
        fixture.Insert(false, v1);

        NativeMediaPersistedPage<NativeMediaPersistedImportOperation> page =
            ArchiveSql.ReadPersistedMediaImportV2OperationsPage(fixture.Connection, null, null);

        NativeMediaPersistedImportOperation only = Assert.Single(page.Items);
        Assert.Equal(2, only.Identity.PlanVersion);
        Assert.Equal(PersistedMediaFixture.OperationId, only.Identity.OperationId);
        Assert.Null(page.NextOperationId);
        Assert.Null(ArchiveSql.ReadPersistedMediaImportV2Operation(fixture.Connection, null,
            v1Operation, PersistedMediaFixture.Request));
        Assert.Equal(v1Operation,
            ArchiveSql.ReadPersistedMediaImportOperation(fixture.Connection, null,
                v1Operation, PersistedMediaFixture.Request)!.Identity.OperationId);
    }

    [Fact]
    public void V2ReceiptDispositionDistinguishesMissingMatchingAndMismatch()
    {
        using PersistedMediaFixture fixture = new();
        Dictionary<string, object?> committed = PersistedMediaFixture.Row(false, version: 2,
            state: "Committed");
        fixture.Insert(false, committed);
        NativeMediaPersistedImportOperation operation =
            Assert.IsType<NativeMediaPersistedImportOperation>(ArchiveSql.ReadPersistedMediaImportV2Operation(
                fixture.Connection, null, PersistedMediaFixture.OperationId, PersistedMediaFixture.Request));

        Assert.Equal(NativeMediaV2ReceiptDisposition.Missing,
            NativeMediaV2TransferInspectionFactory.ImportOperation(operation, null).ReceiptDisposition);

        fixture.InsertReceipt(false, PersistedMediaFixture.Receipt(false, committed));
        NativeMediaPersistedImportReceipt matching =
            Assert.IsType<NativeMediaPersistedImportReceipt>(ArchiveSql.ReadPersistedMediaImportReceipt(
                fixture.Connection, null, PersistedMediaFixture.OperationId, PersistedMediaFixture.Request, 2));
        NativeMediaV2ImportOperationInspection inspected =
            NativeMediaV2TransferInspectionFactory.ImportOperation(operation, matching);
        Assert.Equal(NativeMediaV2ReceiptDisposition.Matching, inspected.ReceiptDisposition);

        fixture.Execute($"UPDATE archive_media_import_receipts SET post_count=post_count+1 WHERE operation_id='{PersistedMediaFixture.OperationId:D}'");
        NativeMediaPersistedImportReceipt mismatched =
            Assert.IsType<NativeMediaPersistedImportReceipt>(ArchiveSql.ReadPersistedMediaImportReceipt(
                fixture.Connection, null, PersistedMediaFixture.OperationId, PersistedMediaFixture.Request, 2));
        Assert.Equal(NativeMediaV2ReceiptDisposition.Mismatch,
            NativeMediaV2TransferInspectionFactory.ImportOperation(operation, mismatched).ReceiptDisposition);

        Dictionary<string, object?> preparing = PersistedMediaFixture.Row(false, version: 2,
            state: "Preparing", operation: Guid.Parse("cd220000-0000-4000-8000-000000000004"));
        fixture.Insert(false, preparing);
        NativeMediaPersistedImportOperation unfinished =
            Assert.IsType<NativeMediaPersistedImportOperation>(ArchiveSql.ReadPersistedMediaImportV2Operation(
                fixture.Connection, null, Guid.Parse("cd220000-0000-4000-8000-000000000004"), PersistedMediaFixture.Request));
        Assert.Equal(NativeMediaV2ReceiptDisposition.NotChecked,
            NativeMediaV2TransferInspectionFactory.ImportOperation(unfinished, null).ReceiptDisposition);
    }

    [Fact]
    public async Task CatalogFacadeReadsCommittedImportWithoutFilesystemInspection()
    {
        await using NativeV2ImportFixture fixture = await NativeV2ImportFixture.CreateAsync();
        using ProfileAdmissionToken admission = await fixture.ExclusiveAsync();
        ProfileExclusiveOutcome<NativeMediaImportCommitResult> outcome = await fixture.CommitAsync(admission);
        Assert.Equal(NativeMediaImportCommitStatus.Committed, outcome.Result.Status);

        NativeMediaV2ImportOperationPage page =
            await fixture.Catalog.ReadNativeMediaV2ImportOperationsPageAsync();
        NativeMediaV2ImportOperationInspection item = Assert.Single(page.Items);
        Assert.Equal(fixture.Plan.OperationId, item.OperationId);
        Assert.Equal(NativeMediaV2PersistedState.Committed, item.PersistedState);
        Assert.Equal(NativeMediaV2ReceiptDisposition.Matching, item.ReceiptDisposition);
        Assert.Equal(NativeV2ImportFixture.Id, item.Resource);

        NativeMediaV2ImportReceiptInspection receipt = Assert.IsType<NativeMediaV2ImportReceiptInspection>(
            await fixture.Catalog.ReadNativeMediaV2ImportReceiptAsync(
                fixture.Plan.OperationId, fixture.Plan.RequestSha256));
        Assert.False(receipt.IsOrphan);
        Assert.Equal(2, receipt.PlanVersion);
        Assert.Equal(item.Counts, receipt.Counts);

        NativeMediaV2ImportOperationInspection single = Assert.IsType<NativeMediaV2ImportOperationInspection>(
            await fixture.Catalog.ReadNativeMediaV2ImportOperationAsync(
                fixture.Plan.OperationId, fixture.Plan.RequestSha256));
        Assert.Equal(item, single);
    }

    [Fact]
    public async Task CatalogFacadeReadsCommittedMergeAndPreservesOrphanReceiptEvidence()
    {
        await using NativeV2ImportFixture fixture = await NativeV2ImportFixture.CreateAsync(
            NativeV2ImportFixture.Content(originalContained: false, derived: true, presence: false),
            [NativeV2ImportFixture.Preview]);
        using (ProfileAdmissionToken admission = await fixture.ExclusiveAsync())
        {
            ProfileExclusiveOutcome<NativeMediaImportCommitResult> seed = await fixture.CommitAsync(admission);
            Assert.Equal(NativeMediaImportCommitStatus.Committed, seed.Result.Status);
        }

        await using NativeTransferImportStage incoming = await NativeV2ImportFixture.StageAsync(
            fixture.Files, NativeV2ImportFixture.Content(), NativeV2ImportFixture.Original, NativeV2ImportFixture.Preview);
        NativeMediaMergePreparation prepared = await fixture.Catalog.PrepareNativeMediaMergeAsync(incoming);
        NativeMediaMergePlan plan = Assert.IsType<NativeMediaMergePlan>(prepared.Plan);
        Assert.Equal(NativeMediaMergeError.None, prepared.Error);
        using (ProfileAdmissionToken admission = await fixture.ExclusiveAsync())
        {
            ProfileExclusiveOutcome<NativeMediaMergeResult> outcome =
                await fixture.Catalog.CommitNativeMediaMergeAdmittedAsync(admission, plan, fixture.Store);
            Assert.Equal(NativeMediaImportCommitStatus.Committed, outcome.Result.Status);
        }

        NativeMediaV2MergeOperationPage page =
            await fixture.Catalog.ReadNativeMediaV2MergeOperationsPageAsync();
        NativeMediaV2MergeOperationInspection item = Assert.Single(page.Items);
        Assert.Equal(plan.OperationId, item.OperationId);
        Assert.Equal(NativeMediaV2PersistedState.Committed, item.PersistedState);
        Assert.Equal(NativeMediaV2ReceiptDisposition.Matching, item.ReceiptDisposition);

        Assert.Single(await fixture.Catalog.DeleteThreadsAsync([NativeV2ImportFixture.Id]));
        Assert.Empty((await fixture.Catalog.ReadNativeMediaV2MergeOperationsPageAsync()).Items);
        NativeMediaV2MergeReceiptInspection orphan = Assert.IsType<NativeMediaV2MergeReceiptInspection>(
            await fixture.Catalog.ReadNativeMediaV2MergeReceiptAsync(plan.OperationId, plan.RequestSha256));
        Assert.True(orphan.IsOrphan);
        Assert.Equal(2, orphan.PlanVersion);
    }

    [Fact]
    public async Task V2InspectionHonorsCancellationBeforeOpeningRead()
    {
        await using NativeV2ImportFixture fixture = await NativeV2ImportFixture.CreateAsync();
        using CancellationTokenSource cancelled = new();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            fixture.Catalog.ReadNativeMediaV2ImportOperationsPageAsync(cancellationToken: cancelled.Token));
    }

    [Fact]
    public void V2PagingReturnsAtMost32ItemsAndCursor()
    {
        using PersistedMediaFixture fixture = new();
        for (int index = 1; index <= 33; index++)
        {
            Guid operation = Guid.Parse($"cd220000-0000-4000-8000-{index:x12}");
            fixture.Insert(false, PersistedMediaFixture.Row(false, version: 2,
                state: "Preparing", operation: operation));
        }

        NativeMediaPersistedPage<NativeMediaPersistedImportOperation> page =
            ArchiveSql.ReadPersistedMediaImportV2OperationsPage(fixture.Connection, null, null);
        Assert.Equal(32, page.Items.Length);
        Assert.NotNull(page.NextOperationId);
        Assert.Equal(page.Items[^1].Identity.OperationId, page.NextOperationId);
        Assert.All(page.Items, item => Assert.Equal(2, item.Identity.PlanVersion));
    }

    [Fact]
    public void InspectionDtosDoNotExposePathsPayloadsOrAuthorizationMembers()
    {
        Type[] types =
        [
            typeof(NativeMediaV2ImportOperationInspection),
            typeof(NativeMediaV2MergeOperationInspection),
            typeof(NativeMediaV2ImportReceiptInspection),
            typeof(NativeMediaV2MergeReceiptInspection),
            typeof(NativeMediaV2ImportOperationPage),
            typeof(NativeMediaV2MergeOperationPage)
        ];
        string[] forbidden = ["Path", "Directory", "CatalogBytes", "Selection", "Token", "Lease", "Command", "Apply", "Resume"];
        foreach (Type type in types)
        foreach (System.Reflection.MemberInfo member in type.GetMembers())
            Assert.DoesNotContain(forbidden, fragment => member.Name.Contains(fragment, StringComparison.OrdinalIgnoreCase));
    }
}
