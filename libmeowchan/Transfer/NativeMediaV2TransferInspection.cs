using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using MeowChan.Archive;
using MeowChan.Storage;

namespace MeowChan.Transfer;

// Bounded, read-only persistence observations for the native v2 coordinator. These
// records intentionally do not contain paths, catalog bytes, selections, leases,
// admission tokens, or commands. A matching receipt is historical consistency only;
// it is never an Apply/Resume grant.
internal enum NativeMediaV2PersistedState
{
    Preparing = 1,
    Prepared = 2,
    Committed = 3
}

internal enum NativeMediaV2ReceiptDisposition
{
    NotChecked = 0,
    Missing = 1,
    Matching = 2,
    Mismatch = 3
}

internal sealed record NativeMediaV2ImportOperationInspection(
    Guid OperationId,
    string RequestSha256,
    ResourceId Resource,
    NativeMediaV2PersistedState PersistedState,
    NativeMediaV2ReceiptDisposition ReceiptDisposition,
    NativeMediaImportCounts Counts);

internal sealed record NativeMediaV2MergeOperationInspection(
    Guid OperationId,
    string RequestSha256,
    ResourceId Resource,
    NativeMediaV2PersistedState PersistedState,
    NativeMediaV2ReceiptDisposition ReceiptDisposition,
    NativeMediaMergeCounts Counts);

internal sealed record NativeMediaV2ImportReceiptInspection(
    Guid OperationId,
    string RequestSha256,
    int PlanVersion,
    NativeMediaImportCounts Counts,
    bool IsOrphan);

internal sealed record NativeMediaV2MergeReceiptInspection(
    Guid OperationId,
    string RequestSha256,
    int PlanVersion,
    NativeMediaMergeCounts Counts,
    bool IsOrphan);

internal sealed record NativeMediaV2ImportOperationPage
{
    internal NativeMediaV2ImportOperationPage(ImmutableArray<NativeMediaV2ImportOperationInspection> items,
        Guid? nextOperationId)
    {
        NativeMediaTransferInspectionContract.ValidatePage(items, nextOperationId);
        if (items.Any(item => item is null))
            throw new ArgumentException("The v2 import inspection page contains a null item.", nameof(items));
        Items = items;
        NextOperationId = nextOperationId;
    }

    internal ImmutableArray<NativeMediaV2ImportOperationInspection> Items { get; }
    internal Guid? NextOperationId { get; }
}

internal sealed record NativeMediaV2MergeOperationPage
{
    internal NativeMediaV2MergeOperationPage(ImmutableArray<NativeMediaV2MergeOperationInspection> items,
        Guid? nextOperationId)
    {
        NativeMediaTransferInspectionContract.ValidatePage(items, nextOperationId);
        if (items.Any(item => item is null))
            throw new ArgumentException("The v2 merge inspection page contains a null item.", nameof(items));
        Items = items;
        NextOperationId = nextOperationId;
    }

    internal ImmutableArray<NativeMediaV2MergeOperationInspection> Items { get; }
    internal Guid? NextOperationId { get; }
}

internal static class NativeMediaV2TransferInspectionFactory
{
    internal static NativeMediaV2ImportOperationInspection ImportOperation(
        NativeMediaPersistedImportOperation operation, NativeMediaPersistedImportReceipt? receipt)
    {
        ArgumentNullException.ThrowIfNull(operation);
        RequireV2(operation.Identity);
        return new(operation.Identity.OperationId, operation.Identity.RequestSha256, operation.Identity.Resource,
            State(operation.State), ReceiptDisposition(operation, receipt), operation.Counts);
    }

    internal static NativeMediaV2MergeOperationInspection MergeOperation(
        NativeMediaPersistedMergeOperation operation, NativeMediaPersistedMergeReceipt? receipt)
    {
        ArgumentNullException.ThrowIfNull(operation);
        RequireV2(operation.Identity);
        return new(operation.Identity.OperationId, operation.Identity.RequestSha256, operation.Identity.Resource,
            State(operation.State), ReceiptDisposition(operation, receipt), operation.Counts);
    }

    internal static NativeMediaV2ImportReceiptInspection ImportReceipt(
        NativeMediaPersistedImportReceipt receipt, bool isOrphan)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        RequireV2(receipt.Identity.PlanVersion);
        return new(receipt.Identity.OperationId, receipt.Identity.RequestSha256, receipt.Identity.PlanVersion,
            receipt.Counts, isOrphan);
    }

    internal static NativeMediaV2MergeReceiptInspection MergeReceipt(
        NativeMediaPersistedMergeReceipt receipt, bool isOrphan)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        RequireV2(receipt.Identity.PlanVersion);
        return new(receipt.Identity.OperationId, receipt.Identity.RequestSha256, receipt.Identity.PlanVersion,
            receipt.Counts, isOrphan);
    }

    private static NativeMediaV2PersistedState State(NativeMediaImportOperationState state)
        => state switch
        {
            NativeMediaImportOperationState.Preparing => NativeMediaV2PersistedState.Preparing,
            NativeMediaImportOperationState.Prepared => NativeMediaV2PersistedState.Prepared,
            NativeMediaImportOperationState.Committed => NativeMediaV2PersistedState.Committed,
            _ => throw new InvalidDataException("The persisted v2 media operation has an unsupported state.")
        };

    private static NativeMediaV2ReceiptDisposition ReceiptDisposition(
        NativeMediaPersistedImportOperation operation, NativeMediaPersistedImportReceipt? receipt)
        => operation.State != NativeMediaImportOperationState.Committed
            ? NativeMediaV2ReceiptDisposition.NotChecked
            : receipt is null
                ? NativeMediaV2ReceiptDisposition.Missing
                : ArchiveReceiptMatches(operation, receipt)
                    ? NativeMediaV2ReceiptDisposition.Matching
                    : NativeMediaV2ReceiptDisposition.Mismatch;

    private static NativeMediaV2ReceiptDisposition ReceiptDisposition(
        NativeMediaPersistedMergeOperation operation, NativeMediaPersistedMergeReceipt? receipt)
        => operation.State != NativeMediaImportOperationState.Committed
            ? NativeMediaV2ReceiptDisposition.NotChecked
            : receipt is null
                ? NativeMediaV2ReceiptDisposition.Missing
                : ArchiveReceiptMatches(operation, receipt)
                    ? NativeMediaV2ReceiptDisposition.Matching
                    : NativeMediaV2ReceiptDisposition.Mismatch;

    private static bool ArchiveReceiptMatches(NativeMediaPersistedImportOperation operation,
        NativeMediaPersistedImportReceipt receipt)
        => ArchiveSql.PersistedMediaImportReceiptMatches(operation, receipt);

    private static bool ArchiveReceiptMatches(NativeMediaPersistedMergeOperation operation,
        NativeMediaPersistedMergeReceipt receipt)
        => ArchiveSql.PersistedMediaMergeReceiptMatches(operation, receipt);

    private static void RequireV2(NativeMediaPersistedIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        RequireV2(identity.PlanVersion);
    }

    private static void RequireV2(int planVersion)
    {
        if (planVersion != 2)
            throw new InvalidDataException("The native v2 inspection received a non-v2 persistence record.");
    }
}
