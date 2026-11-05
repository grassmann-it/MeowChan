using System;
using System.Collections.Immutable;
using System.IO;
using MeowChan.Archive;

namespace MeowChan.Storage;

/// <summary>Fixed v1 ceilings; these are logical budgets, not physical database/filesystem size guarantees.</summary>
internal static class LocalImportBounds
{
    internal const int Version = 1;
    internal const int MaximumItems = 1000;
    internal const int MaximumPageSize = 500;
    internal const int MaximumInventoryPageSize = 32;
    internal const int MaximumClaimsBytes = 1024 * 1024;
    internal const int MaximumCollectionClaimsBytes = 16 * 1024 * 1024;
    internal const int MaximumTitleBytes = 4096;
    internal const int MaximumDisplayNameBytes = 4096;
    internal const int MaximumInputKeyBytes = 128;
    internal const int MaximumProfileIdBytes = 128;
    internal const int MaximumClaimIdBytes = 1024;
    internal const int MaximumUrlBytes = 8192;
    internal const int MaximumTags = 4096;
    internal const int MaximumTagBytes = 1024;
    internal const int MaximumFieldCodeBytes = 64;
    internal const int MaximumSelectionBytes = 24 * 1024 * 1024;
    internal const int MaximumContentBytes = 1024 * 1024;
    internal const int MaximumJournalBytes = 32 * 1024 * 1024;
    internal const int MaximumJsonDepth = 16;
    internal const int MaximumReceiptBytes = 256 * 1024;
    internal const int MaximumOperationMetadataBytes = 32 * 1024 * 1024;
    internal const int MaximumOperations = 4096;
    internal const int MaximumUnresolvedOperations = 32;
    internal const long MaximumInventoryMetadataBytes = 256L * 1024 * 1024;
    internal const int MaximumRelativeSlotBytes = 256;
}

internal sealed record LocalImportLimits(long MaxSelectedPayloadBytes, long ReservedMetadataBytes);
internal sealed record LocalImportInput(string InputKey, string? DisplayName, LocalSourceClaims? Claims);
internal sealed record LocalImportSelection(string Title, ImmutableArray<LocalImportInput> Items);

// Assigned identities and persisted values only. No factories mint IDs, open sources,
// issue plans or turn these descriptors into owner/stage authority.
internal sealed record LocalSelectedItem(
    LocalMediaRevisionReference Reference, int Ordinal, string InputKey, string? DisplayName, LocalSourceClaims? Claims);
internal sealed record LocalImportSelectionDescriptor(
    int Version, string CatalogInstance, Guid OperationId, LocalCollectionId CollectionId,
    string Title, ImmutableArray<LocalSelectedItem> Items);
internal sealed record LocalImportContentItem(
    LocalMediaRevisionReference Reference, int Ordinal, LocalMediaRole Role, long Length,
    string Sha256, string PayloadRelativeSlot);
internal sealed record LocalImportContent(int Version, ImmutableArray<LocalImportContentItem> Items);
internal enum LocalImportBindingKind { Stage = 1, Candidate = 2 }

internal sealed record LocalImportMetadataBudget(
    long SelectionBytes, long MaximumContentBytes, long ReadyJournalBytes, long ReceiptBytes,
    long TemporaryBytes, long RequiredMetadataBytes, long RequiredFileBytes);

internal enum LocalImportOperationState { Capturing = 0, Frozen = 1, Prepared = 2, Committed = 3 }
internal sealed record LocalImportOperationSummary(
    Guid OperationId, LocalCollectionId CollectionId, string CatalogInstance, string? RequestSha256,
    LocalImportOperationState State, long Attempt, int ItemCount, long? TotalLength);
internal sealed record LocalImportOperationPage(ImmutableArray<LocalImportOperationSummary> Items, Guid? NextOperationId);
internal sealed record LocalFrozenItem(
    LocalMediaRevisionReference Reference, int Ordinal, string InputKey, string? DisplayName,
    LocalSourceClaims? Claims, long? Length, string? Sha256);
internal sealed record LocalImportOperationDetail(
    LocalImportOperationSummary Summary, LocalImportLimits Limits, long ExpectedCatalogGeneration,
    string Title, ImmutableArray<LocalFrozenItem> Items, string? StageBindingsSha256, string? CandidateBindingsSha256);

internal enum LocalImportError
{
    None, InvalidSelection, UnsupportedRole, BudgetExceeded, InventoryFull, TargetReplaced, WrongOwner,
    StalePlan, OperationConflict, ReconciliationRequired, StageIncomplete, CandidateIncomplete,
    InvalidPayloadBinding, PayloadMissing, IntegrityFailure, Busy, Cancelled, IoFailure, InvalidInventory, UnknownOutcome
}

internal sealed class LocalImportResult<T> where T : class
{
    private LocalImportResult(T? value, LocalImportError error) { Value = value; Error = error; }
    internal T? Value { get; }
    internal LocalImportError Error { get; }
    internal static LocalImportResult<T> Success(T value)
        => new(value ?? throw new ArgumentNullException(nameof(value)), LocalImportError.None);
    internal static LocalImportResult<T> Failure(LocalImportError error)
    {
        LocalImportErrors.RequireFailure(error);
        return new(null, error);
    }
}

internal enum LocalImportCommitStatus { Committed, NotCommitted, Unknown }
internal sealed class LocalImportCommitResult
{
    private LocalImportCommitResult(LocalImportCommitStatus status, LocalCollectionImportReceipt? receipt,
        bool isReplay, LocalImportError error)
    { Status = status; Receipt = receipt; IsReplay = isReplay; Error = error; }
    internal LocalImportCommitStatus Status { get; }
    internal LocalCollectionImportReceipt? Receipt { get; }
    internal bool IsReplay { get; }
    internal LocalImportError Error { get; }
    internal static LocalImportCommitResult Committed(LocalCollectionImportReceipt receipt, bool isReplay)
        => new(LocalImportCommitStatus.Committed, receipt ?? throw new ArgumentNullException(nameof(receipt)), isReplay, LocalImportError.None);
    // The caller must have proved safe absence in the SAME compatible v9 target.
    // No codec or legacy-null lookup is such a proof (core-v1 LC-02).
    internal static LocalImportCommitResult NotCommitted(LocalImportError error)
    {
        LocalImportErrors.RequireFailure(error);
        if (error is LocalImportError.UnknownOutcome or LocalImportError.TargetReplaced)
            throw new ArgumentException("An unresolved target is not a negative commit proof.", nameof(error));
        return new(LocalImportCommitStatus.NotCommitted, null, false, error);
    }
    internal static LocalImportCommitResult Unknown()
        => new(LocalImportCommitStatus.Unknown, null, false, LocalImportError.UnknownOutcome);
}

internal sealed class LocalImportReadException : IOException
{
    internal LocalImportReadException(LocalImportError error) : base("Local import read failed: " + error)
    { LocalImportErrors.RequireFailure(error); Error = error; }
    internal LocalImportError Error { get; }
}

/// <summary>Code-only diagnostics; never retains untrusted source strings or a decoder inner exception.</summary>
internal sealed class LocalImportCodecException : Exception
{
    internal LocalImportCodecException(LocalImportError error) : base("Local import codec rejected data: " + error)
    { LocalImportErrors.RequireFailure(error); Error = error; }
    internal LocalImportError Error { get; }
}

internal static class LocalImportErrors
{
    internal static void RequireFailure(LocalImportError error)
    {
        if (error == LocalImportError.None || !Enum.IsDefined(error))
            throw new ArgumentOutOfRangeException(nameof(error));
    }
}
