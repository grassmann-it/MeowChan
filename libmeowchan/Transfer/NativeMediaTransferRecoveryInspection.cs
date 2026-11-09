using System;
using System.Collections.Immutable;
using System.Linq;

namespace MeowChan.Transfer;

// These enums are stable read-only vocabulary. They are observations, not commands or
// readiness grants. In particular, RecoveryRequired does not imply that retry/resume is legal.
internal enum NativeMediaTransferInspectionPhase
{
    Unknown = 0,
    Preparing = 1,
    Prepared = 2,
    Applying = 3,
    Committed = 4,
    Cancelled = 5,
    Failed = 6,
    RecoveryRequired = 7
}

internal enum NativeMediaTransferRecoveryDisposition
{
    Clear = 0,
    Required = 1,
    Resolved = 2
}

internal enum NativeMediaTransferRecoveryReason
{
    None = 0,
    MissingOperation = 1,
    MissingReceipt = 2,
    ReceiptMismatch = 3,
    NonTerminalOperation = 4,
    IncompleteFilesystem = 5,
    StaleTarget = 6,
    MalformedEvidence = 7,
    BoundsExceeded = 8,
    ReadFailure = 9
}

internal sealed record NativeMediaTransferPhaseIdentityV1
{
    internal NativeMediaTransferPhaseIdentityV1(Guid operationId, NativeMediaTransferInspectionPhase phase, int sequence)
    {
        Validate(operationId, phase, sequence);
        OperationId = operationId;
        Phase = phase;
        Sequence = sequence;
        StableKey = NativeMediaTransferInspectionContract.PhaseKey(1, operationId, phase, sequence);
    }

    internal Guid OperationId { get; }
    internal NativeMediaTransferInspectionPhase Phase { get; }
    internal int Sequence { get; }
    internal string StableKey { get; }

    private static void Validate(Guid operationId, NativeMediaTransferInspectionPhase phase, int sequence)
    {
        if (operationId == Guid.Empty) throw new ArgumentException("The phase operation identity is empty.", nameof(operationId));
        if (!Enum.IsDefined(phase)) throw new ArgumentOutOfRangeException(nameof(phase));
        if (sequence is < 0 or > NativeMediaTransferInspectionContract.MaximumPhaseSequence)
            throw new ArgumentOutOfRangeException(nameof(sequence));
    }
}

internal sealed record NativeMediaTransferRecoveryIdentityV1
{
    internal NativeMediaTransferRecoveryIdentityV1(Guid operationId, NativeMediaTransferRecoveryDisposition disposition,
        NativeMediaTransferRecoveryReason reason)
    {
        Validate(operationId, disposition, reason);
        OperationId = operationId;
        Disposition = disposition;
        Reason = reason;
        StableKey = NativeMediaTransferInspectionContract.RecoveryKey(1, operationId, disposition, reason);
    }

    internal Guid OperationId { get; }
    internal NativeMediaTransferRecoveryDisposition Disposition { get; }
    internal NativeMediaTransferRecoveryReason Reason { get; }
    internal string StableKey { get; }

    private static void Validate(Guid operationId, NativeMediaTransferRecoveryDisposition disposition,
        NativeMediaTransferRecoveryReason reason)
    {
        if (operationId == Guid.Empty) throw new ArgumentException("The recovery operation identity is empty.", nameof(operationId));
        if (!Enum.IsDefined(disposition)) throw new ArgumentOutOfRangeException(nameof(disposition));
        if (!Enum.IsDefined(reason)) throw new ArgumentOutOfRangeException(nameof(reason));
        if ((disposition == NativeMediaTransferRecoveryDisposition.Clear) != (reason == NativeMediaTransferRecoveryReason.None))
            throw new ArgumentException("Recovery disposition and reason must describe the same bounded observation.");
    }
}

internal sealed record NativeMediaTransferReceiptIdentityV1
{
    internal NativeMediaTransferReceiptIdentityV1(NativeMediaTransferOperationIdentityV1 operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        Operation = operation;
        StableKey = NativeMediaTransferInspectionContract.ReceiptKey(1, operation.StableKey);
    }

    internal NativeMediaTransferOperationIdentityV1 Operation { get; }
    internal string StableKey { get; }
}

internal sealed record NativeMediaTransferRecoveryInspectionV1
{
    internal NativeMediaTransferRecoveryInspectionV1(NativeMediaTransferOperationIdentityV1 operation,
        NativeMediaTransferPhaseIdentityV1 phase, NativeMediaTransferRecoveryIdentityV1 recovery,
        NativeMediaTransferReceiptIdentityV1? receipt, int observedEntryCount)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(phase);
        ArgumentNullException.ThrowIfNull(recovery);
        if (phase.OperationId != operation.OperationId || recovery.OperationId != operation.OperationId
            || receipt is not null && (receipt.Operation.OperationId != operation.OperationId
                || receipt.Operation.StableKey != operation.StableKey))
            throw new ArgumentException("Inspection identities must refer to one operation.");
        if (observedEntryCount is < 0 or > NativeMediaTransferInspectionContract.MaximumInspectionEntries)
            throw new ArgumentOutOfRangeException(nameof(observedEntryCount));
        Operation = operation;
        Phase = phase;
        Recovery = recovery;
        Receipt = receipt;
        ObservedEntryCount = observedEntryCount;
    }

    internal NativeMediaTransferOperationIdentityV1 Operation { get; }
    internal NativeMediaTransferPhaseIdentityV1 Phase { get; }
    internal NativeMediaTransferRecoveryIdentityV1 Recovery { get; }
    internal NativeMediaTransferReceiptIdentityV1? Receipt { get; }
    internal int ObservedResourceCount => NativeMediaTransferInspectionContract.MaximumResourceCountV1;
    internal int ObservedEntryCount { get; }
    internal bool HasHistoricalReceipt => Receipt is not null;
}

internal sealed record NativeMediaTransferInspectionPageV1
{
    internal NativeMediaTransferInspectionPageV1(ImmutableArray<NativeMediaTransferRecoveryInspectionV1> items,
        Guid? nextOperationId)
    {
        NativeMediaTransferInspectionContract.ValidatePage(items, nextOperationId);
        if (items.Any(item => item is null)) throw new ArgumentException("The inspection page contains a null item.", nameof(items));
        Items = items;
        NextOperationId = nextOperationId;
    }

    internal ImmutableArray<NativeMediaTransferRecoveryInspectionV1> Items { get; }
    internal Guid? NextOperationId { get; }
}

internal sealed record NativeMediaTransferPhaseIdentityV2
{
    internal NativeMediaTransferPhaseIdentityV2(Guid operationId, NativeMediaTransferInspectionPhase phase, int sequence)
    {
        if (operationId == Guid.Empty) throw new ArgumentException("The phase operation identity is empty.", nameof(operationId));
        if (!Enum.IsDefined(phase)) throw new ArgumentOutOfRangeException(nameof(phase));
        if (sequence is < 0 or > NativeMediaTransferInspectionContract.MaximumPhaseSequence)
            throw new ArgumentOutOfRangeException(nameof(sequence));
        OperationId = operationId;
        Phase = phase;
        Sequence = sequence;
        StableKey = NativeMediaTransferInspectionContract.PhaseKey(2, operationId, phase, sequence);
    }

    internal Guid OperationId { get; }
    internal NativeMediaTransferInspectionPhase Phase { get; }
    internal int Sequence { get; }
    internal string StableKey { get; }
}

internal sealed record NativeMediaTransferRecoveryIdentityV2
{
    internal NativeMediaTransferRecoveryIdentityV2(Guid operationId, NativeMediaTransferRecoveryDisposition disposition,
        NativeMediaTransferRecoveryReason reason)
    {
        if (operationId == Guid.Empty) throw new ArgumentException("The recovery operation identity is empty.", nameof(operationId));
        if (!Enum.IsDefined(disposition)) throw new ArgumentOutOfRangeException(nameof(disposition));
        if (!Enum.IsDefined(reason)) throw new ArgumentOutOfRangeException(nameof(reason));
        if ((disposition == NativeMediaTransferRecoveryDisposition.Clear) != (reason == NativeMediaTransferRecoveryReason.None))
            throw new ArgumentException("Recovery disposition and reason must describe the same bounded observation.");
        OperationId = operationId;
        Disposition = disposition;
        Reason = reason;
        StableKey = NativeMediaTransferInspectionContract.RecoveryKey(2, operationId, disposition, reason);
    }

    internal Guid OperationId { get; }
    internal NativeMediaTransferRecoveryDisposition Disposition { get; }
    internal NativeMediaTransferRecoveryReason Reason { get; }
    internal string StableKey { get; }
}

internal sealed record NativeMediaTransferReceiptIdentityV2
{
    internal NativeMediaTransferReceiptIdentityV2(NativeMediaTransferOperationIdentityV2 operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        Operation = operation;
        StableKey = NativeMediaTransferInspectionContract.ReceiptKey(2, operation.StableKey);
    }

    internal NativeMediaTransferOperationIdentityV2 Operation { get; }
    internal string StableKey { get; }
}

internal sealed record NativeMediaTransferRecoveryInspectionV2
{
    internal NativeMediaTransferRecoveryInspectionV2(NativeMediaTransferOperationIdentityV2 operation,
        NativeMediaTransferPhaseIdentityV2 phase, NativeMediaTransferRecoveryIdentityV2 recovery,
        NativeMediaTransferReceiptIdentityV2? receipt, int observedEntryCount)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(phase);
        ArgumentNullException.ThrowIfNull(recovery);
        if (phase.OperationId != operation.OperationId || recovery.OperationId != operation.OperationId
            || receipt is not null && (receipt.Operation.OperationId != operation.OperationId
                || receipt.Operation.StableKey != operation.StableKey))
            throw new ArgumentException("Inspection identities must refer to one operation.");
        if (observedEntryCount is < 0 or > NativeMediaTransferInspectionContract.MaximumInspectionEntries)
            throw new ArgumentOutOfRangeException(nameof(observedEntryCount));
        Operation = operation;
        Phase = phase;
        Recovery = recovery;
        Receipt = receipt;
        ObservedEntryCount = observedEntryCount;
    }

    internal NativeMediaTransferOperationIdentityV2 Operation { get; }
    internal NativeMediaTransferPhaseIdentityV2 Phase { get; }
    internal NativeMediaTransferRecoveryIdentityV2 Recovery { get; }
    internal NativeMediaTransferReceiptIdentityV2? Receipt { get; }
    internal int ObservedResourceCount => Operation.ResourceSet.Count;
    internal int ObservedEntryCount { get; }
    internal bool HasHistoricalReceipt => Receipt is not null;
}

internal sealed record NativeMediaTransferInspectionPageV2
{
    internal NativeMediaTransferInspectionPageV2(ImmutableArray<NativeMediaTransferRecoveryInspectionV2> items,
        Guid? nextOperationId)
    {
        NativeMediaTransferInspectionContract.ValidatePage(items, nextOperationId);
        if (items.Any(item => item is null)) throw new ArgumentException("The inspection page contains a null item.", nameof(items));
        Items = items;
        NextOperationId = nextOperationId;
    }

    internal ImmutableArray<NativeMediaTransferRecoveryInspectionV2> Items { get; }
    internal Guid? NextOperationId { get; }
}
