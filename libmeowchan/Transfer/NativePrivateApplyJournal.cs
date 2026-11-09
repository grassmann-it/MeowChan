using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using MeowChan.Persistence;

namespace MeowChan.Transfer;

/// <summary>
/// Selected-only create-only evidence, not store authority, consent or auto-resume.
/// Append observations remain supplied assertions. InspectStores independently
/// samples actual selected store bytes, without granting mutation authority. Hashes
/// detect mismatch, not hostile local tampering. Flush(true) is not directory fsync.
/// </summary>
internal sealed partial class NativePrivateApplyJournal
{
    internal const int MaximumEvents = 32;
    internal const int MaximumOperations = 512;
    internal const int InventoryPageSize = 32;
    internal const int MaximumRecordBytes = 4 * 1024;
    internal const string DirectoryName = ".meowchan-private-apply";
    private readonly ProfileLease _lease;
    private readonly string _root;
    private readonly object _snapshotOwner = new();

    private NativePrivateApplyJournal(ProfileLease lease)
    {
        _lease = lease;
        _root = Path.Combine(lease.ProfileDirectory, DirectoryName);
    }

    // Open neither enumerates operations nor touches private payloads. A missing
    // root is established only by Claim, so Inspect/Inventory stay read-only.
    internal static NativePrivateApplyJournal Open(ProfileLease lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        var journal = new NativePrivateApplyJournal(lease);
        return lease.RunExclusive(() =>
        {
            journal.CheckParents();
            return journal;
        });
    }

    internal NativePrivateApplyJournalInspection Claim(NativePrivateApplyRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return _lease.RunExclusive(() => ClaimCore(request));
    }

    internal NativePrivateApplyJournalInspection Inspect(Guid operationId)
        => _lease.RunExclusive(() => InspectCore(operationId));

    internal NativePrivateApplyJournalInspection Replay(Guid operationId) => Inspect(operationId);

    internal NativePrivateApplyJournalInspection AppendPrepared(NativePrivateApplyJournalSnapshot snapshot)
        => AppendObservation(snapshot, NativePrivateApplyOperationObservation.Prepared, []);

    internal NativePrivateApplyJournalInspection AppendApplying(NativePrivateApplyJournalSnapshot snapshot)
        => AppendObservation(snapshot, NativePrivateApplyOperationObservation.Applying, []);

    internal NativePrivateApplyJournalInspection AppendObservation(NativePrivateApplyJournalSnapshot snapshot,
        NativePrivateApplyOperationObservation operationObservation,
        ImmutableArray<NativePrivateApplyStepObservation> observations)
        => _lease.RunExclusive(() => AppendEventCore(snapshot, operationObservation, observations, null));

    // This is a chain event, not receipt.json. In particular RecoveryRequired can
    // be refined by a later compatible receipt record without freezing uncertainty.
    internal NativePrivateApplyJournalInspection AppendReceiptRecord(NativePrivateApplyJournalSnapshot snapshot,
        NativePrivateApplyTerminalReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        return _lease.RunExclusive(() => AppendEventCore(snapshot,
            NativePrivateApplyOperationObservation.None, [], receipt));
    }

    internal NativePrivateApplyJournalInspection AppendReceipt(NativePrivateApplyJournalSnapshot snapshot,
        NativePrivateApplyTerminalReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        return _lease.RunExclusive(() => AppendReceiptCore(snapshot, receipt));
    }

    /// <summary>Bounded scalar-only inventory; no selected payload is loaded.</summary>
    internal ImmutableArray<NativePrivateApplyJournalHeader> Inventory(int offset = 0)
        => _lease.RunExclusive(() =>
        {
            if (offset < 0 || offset > MaximumOperations) throw Invalid();
            CheckParents();
            if (!Exists(_root)) return ImmutableArray<NativePrivateApplyJournalHeader>.Empty;
            string[] directories = OperationDirectories();
            var headers = ImmutableArray.CreateBuilder<NativePrivateApplyJournalHeader>();
            // Validate all scalar headers before returning a page: a malformed
            // child elsewhere is not silently hidden by the pagination boundary.
            for (int i = 0; i < directories.Length; i++)
            {
                Guid id = ParseOperationName(Path.GetFileName(directories[i]));
                JournalReceipt? receipt = ReadHistoricalReceipt(id);
                NativePrivateApplyJournalHeader header;
                if (receipt is not null)
                    header = new(id, receipt.Value.RequestSha256, receipt.Value.Selection, receipt.Value.Phase, true);
                else
                {
                    ValidateOperationDirectory(directories[i]);
                    JournalClaim claim = ReadClaim(id);
                    header = new(id, claim.RequestSha256, claim.Selection, null, false);
                }
                if (i >= offset && i < offset + InventoryPageSize) headers.Add(header);
            }
            return headers.ToImmutable();
        });

    private NativePrivateApplyJournalInspection ClaimCore(NativePrivateApplyRequest request)
    {
        ValidateOperationId(request.OperationId);
        CheckParents();
        // Historical replay precedes capacity, sibling inventory and payload IO.
        JournalReceipt? historical = ReadHistoricalReceipt(request.OperationId);
        if (historical is not null)
        {
            if (historical.Value.RequestSha256 != request.RequestSha256
                || historical.Value.Selection != request.Selection) throw Invalid();
            return InspectReceipt(historical);
        }
        string directory = OperationDirectory(request.OperationId);
        if (Exists(directory))
        {
            NativePrivateApplyJournalInspection existing = InspectCore(request.OperationId);
            if (existing.RequestSha256 != request.RequestSha256 || existing.Selection != request.Selection) throw Invalid();
            // Full canonical bytes are also compared on an incomplete same-ID retry.
            byte[] encodedExisting = NativePrivateApplyRequestCodec.Serialize(request);
            JournalClaim prior = ReadClaim(request.OperationId);
            if (prior.RequestBytesSha256 != Hash(encodedExisting) || prior.RequestLength != encodedExisting.Length)
                throw Invalid();
            return existing;
        }

        // Validate/cap before publishing any operation evidence, not after mkdir.
        byte[] encoded = NativePrivateApplyRequestCodec.Serialize(request);
        _ = NativePrivateApplyRequestCodec.Deserialize(encoded);
        JournalClaim claim = JournalClaim.From(request, encoded);
        byte[] claimBytes = SerializeClaim(claim);
        CheckRecordSize(claimBytes);
        EnsureRoot();
        if (OperationDirectories().Length >= MaximumOperations) throw Invalid();
        _ = Inventory(); // scalar admission scan; malformed siblings never become empty slots
        // No random fallback ID. Under the shared profile lease, this name is
        // exclusive to this invocation; create-new files still reject drift.
        EnsureNewOperationDirectory(directory);
        CheckOperationDirectory(request.OperationId);
        PublishCreateOnly(Path.Combine(directory, "claim.json"), claimBytes, MaximumRecordBytes);
        CheckParents(); CheckOperationDirectory(request.OperationId);
        PublishCreateOnly(Path.Combine(directory, "request.json"), encoded, NativePrivateApplyRequestCodec.MaximumDocumentBytes);
        return InspectCore(request.OperationId);
    }

    private NativePrivateApplyJournalInspection InspectCore(Guid operationId)
    {
        ValidateOperationId(operationId);
        CheckParents();
        JournalReceipt? historical = ReadHistoricalReceipt(operationId);
        if (historical is not null) return InspectReceipt(historical);
        LoadedOperation loaded = LoadIncomplete(operationId);
        return Inspection(loaded.State, loaded.EventCount, loaded.LastHash, false,
            loaded.ClaimSha256, loaded.RequestBytesSha256, string.Empty);
    }

    private NativePrivateApplyJournalInspection InspectReceipt(JournalReceipt receipt)
        => Inspection(receipt.Value.ToState(), receipt.EventCount, receipt.LastHash, true,
            string.Empty, string.Empty, Hash(SerializeReceipt(receipt)));

    private NativePrivateApplyJournalInspection Inspection(NativePrivateApplyRecoveryState state,
        int count, string lastHash, bool final, string claimHash, string requestBytesHash, string receiptHash)
        => new(state, count, lastHash, final, new NativePrivateApplyJournalSnapshot(_snapshotOwner,
            state.OperationGuid, state.RequestSha256, count, lastHash, claimHash, requestBytesHash, receiptHash));

    private NativePrivateApplyJournalInspection AppendEventCore(NativePrivateApplyJournalSnapshot snapshot,
        NativePrivateApplyOperationObservation operationObservation,
        ImmutableArray<NativePrivateApplyStepObservation> observations, NativePrivateApplyTerminalReceipt? receipt)
    {
        LoadedOperation loaded = CheckSnapshot(snapshot);
        if (observations.IsDefault || !Enum.IsDefined(operationObservation)) throw Invalid();
        if (NativePrivateApplyRecovery.IsTerminal(loaded.State.Phase)
            && (receipt is null || loaded.State.Phase is not (NativePrivateApplyOperationPhase.RecoveryRequired
                or NativePrivateApplyOperationPhase.NeedsReview))) throw Invalid();
        ImmutableArray<NativePrivateApplyStepObservation> full = observations;
        if (receipt is null && operationObservation != NativePrivateApplyOperationObservation.None)
        {
            if (observations.Length != 0) throw Invalid();
            full = loaded.State.Steps.Select(_ => NativePrivateApplyStepObservation.NotObserved).ToImmutableArray();
        }
        if (receipt is null && full.Length != loaded.State.Steps.Length) throw Invalid();
        var item = new JournalEvent(snapshot.OperationId, snapshot.RequestSha256,
            loaded.EventCount + 1, loaded.LastHash, operationObservation, full, receipt);
        NativePrivateApplyRecoveryState next = ReduceEvent(loaded.State, item, loaded.IntentDurableSeen);
        if (SameState(next, loaded.State)) throw Invalid();
        // Slot 32 is reserved for a receipt refinement. receipt.json itself is a
        // separate bounded slot. Normal polling can never consume final capacity.
        if (loaded.EventCount >= MaximumEvents || receipt is null && loaded.EventCount >= MaximumEvents - 1)
            throw Invalid();
        byte[] bytes = SerializeEvent(item);
        PublishCreateOnly(Path.Combine(OperationDirectory(snapshot.OperationId), EventName(item.Sequence)), bytes, MaximumRecordBytes);
        return InspectCore(snapshot.OperationId);
    }

    private NativePrivateApplyJournalInspection AppendReceiptCore(NativePrivateApplyJournalSnapshot snapshot,
        NativePrivateApplyTerminalReceipt receipt)
    {
        ValidateSnapshotOwner(snapshot);
        if (receipt.OperationGuid != snapshot.OperationId || receipt.RequestSha256 != snapshot.RequestSha256) throw Invalid();
        CheckFinalReceipt(receipt);
        // Reconcile an uncertain acknowledgement at the same receipt name. The
        // prepublication snapshot can be reused for this one exact logical write.
        JournalReceipt? historical = ReadHistoricalReceipt(snapshot.OperationId);
        if (historical is not null)
        {
            if (historical.EventCount != snapshot.EventCount || historical.LastHash != snapshot.LastRecordSha256
                || !SameState(historical.Value.ToState(), receipt.ToState())) throw Invalid();
            CompareExisting(Path.Combine(OperationDirectory(snapshot.OperationId), "receipt.json"),
                SerializeReceipt(new JournalReceipt(receipt, snapshot.EventCount, snapshot.LastRecordSha256)), MaximumRecordBytes);
            return InspectReceipt(historical);
        }
        LoadedOperation loaded = CheckSnapshot(snapshot);
        if (loaded.State.Selection != receipt.Selection) throw Invalid();
        // Finalization does not invent or advance step evidence. Refinements
        // must first be preserved as explicit receipt records in the chain.
        if (!SameSteps(loaded.State.Steps, receipt.Steps)) throw Invalid();
        if (receipt.Phase == NativePrivateApplyOperationPhase.Completed
            && loaded.State.Phase is not (NativePrivateApplyOperationPhase.Applying
                or NativePrivateApplyOperationPhase.Completed or NativePrivateApplyOperationPhase.RecoveryRequired)) throw Invalid();
        if ((receipt.Phase is NativePrivateApplyOperationPhase.CancelledBeforeApply or NativePrivateApplyOperationPhase.FailedBeforeApply)
            && loaded.State.Phase != receipt.Phase) throw Invalid();
        NativePrivateApplyRecoveryState next = NativePrivateApplyRecovery.Reduce(loaded.State,
            new NativePrivateApplyRecoveryEvidence(receipt.OperationGuid, receipt.RequestSha256, [], receipt));
        if (!SameState(next, receipt.ToState())) throw Invalid();
        var final = new JournalReceipt(receipt, loaded.EventCount, loaded.LastHash);
        PublishCreateOnly(Path.Combine(OperationDirectory(snapshot.OperationId), "receipt.json"),
            SerializeReceipt(final), MaximumRecordBytes);
        return InspectCore(snapshot.OperationId);
    }

    private LoadedOperation CheckSnapshot(NativePrivateApplyJournalSnapshot snapshot)
    {
        ValidateSnapshotOwner(snapshot);
        CheckParents();
        if (ReadHistoricalReceipt(snapshot.OperationId) is not null) throw Invalid();
        LoadedOperation loaded = LoadIncomplete(snapshot.OperationId);
        if (snapshot.RequestSha256 != loaded.State.RequestSha256 || snapshot.EventCount != loaded.EventCount
            || snapshot.LastRecordSha256 != loaded.LastHash || snapshot.ClaimSha256 != loaded.ClaimSha256
            || snapshot.RequestBytesSha256 != loaded.RequestBytesSha256 || snapshot.ReceiptSha256.Length != 0) throw Invalid();
        return loaded;
    }

    private void ValidateSnapshotOwner(NativePrivateApplyJournalSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!snapshot.IsOwnedBy(_snapshotOwner)) throw Invalid();
        ValidateOperationId(snapshot.OperationId);
    }

    private LoadedOperation LoadIncomplete(Guid operationId)
    {
        string directory = OperationDirectory(operationId);
        CheckOperationDirectory(operationId);
        int count = ValidateOperationDirectory(directory);
        byte[] claimBytes = ReadBounded(Path.Combine(directory, "claim.json"), MaximumRecordBytes);
        JournalClaim claim = ParseClaim(claimBytes);
        if (claim.OperationId != operationId) throw Invalid();
        byte[] requestBytes = ReadBounded(Path.Combine(directory, "request.json"), NativePrivateApplyRequestCodec.MaximumDocumentBytes);
        if (requestBytes.Length != claim.RequestLength || Hash(requestBytes) != claim.RequestBytesSha256) throw Invalid();
        NativePrivateApplyRequest request = NativePrivateApplyRequestCodec.Deserialize(requestBytes);
        if (request.OperationId != operationId || request.RequestSha256 != claim.RequestSha256
            || request.ContentPlanSha256 != claim.ContentPlanSha256 || request.Selection != claim.Selection
            || request.SelectedPayloadSha256 != claim.SelectedPayloadSha256 || request.SelectedPayloadLength != claim.SelectedPayloadLength)
            throw Invalid();
        var steps = ImmutableArray.CreateBuilder<NativePrivateApplyRecoveryStep>();
        foreach (NativePrivateApplyStoreKind kind in Enum.GetValues<NativePrivateApplyStoreKind>())
            if (NativePrivateApplyRecovery.IsSelected(claim.Selection, kind)) steps.Add(new(kind, NativePrivateApplyStepPhase.Planned));
        NativePrivateApplyRecoveryState state = NativePrivateApplyRecoveryState.CreateClaimed(operationId,
            claim.RequestSha256, claim.Selection, steps.ToImmutable());
        var intentDurableSeen = new HashSet<NativePrivateApplyStoreKind>();
        string previous = string.Empty;
        for (int index = 1; index <= count; index++)
        {
            byte[] bytes = ReadBounded(Path.Combine(directory, EventName(index)), MaximumRecordBytes);
            JournalEvent item = ParseEvent(bytes);
            if (item.OperationId != operationId || item.RequestSha256 != claim.RequestSha256
                || item.Sequence != index || item.PreviousHash != previous) throw Invalid();
            if (index == MaximumEvents && item.Receipt is null) throw Invalid();
            NativePrivateApplyRecoveryState next = ReduceEvent(state, item, intentDurableSeen);
            if (SameState(next, state)) throw Invalid();
            RememberIntentDurable(state, item, intentDurableSeen);
            state = next; previous = Hash(bytes);
        }
        return new(state, count, previous, Hash(claimBytes), claim.RequestBytesSha256,
            intentDurableSeen.ToImmutableHashSet(), request);
    }

    private static NativePrivateApplyRecoveryState ReduceEvent(NativePrivateApplyRecoveryState state,
        JournalEvent item, IReadOnlySet<NativePrivateApplyStoreKind> intentDurableSeen)
    {
        if (item.Receipt is { } receipt)
        {
            if (item.OperationObservation != NativePrivateApplyOperationObservation.None || item.Steps.Length != 0
                || receipt.OperationGuid != item.OperationId || receipt.RequestSha256 != item.RequestSha256) throw Invalid();
            CheckRefinement(state, receipt, intentDurableSeen);
            return NativePrivateApplyRecovery.Reduce(state, new NativePrivateApplyRecoveryEvidence(
                item.OperationId, item.RequestSha256, [], receipt));
        }
        if (NativePrivateApplyRecovery.IsTerminal(state.Phase)) throw Invalid();
        if (item.Steps.Length != state.Steps.Length) throw Invalid();
        if (item.OperationObservation != NativePrivateApplyOperationObservation.None
            && item.Steps.Any(value => value != NativePrivateApplyStepObservation.NotObserved)) throw Invalid();
        ImmutableArray<NativePrivateApplyStepEvidence> steps = item.Steps.Select((value, index) =>
            new NativePrivateApplyStepEvidence(state.Steps[index].StoreKind, value)).ToImmutableArray();
        return NativePrivateApplyRecovery.Reduce(state, new NativePrivateApplyRecoveryEvidence(
            item.OperationId, item.RequestSha256, item.OperationObservation, steps));
    }

    private static void RememberIntentDurable(NativePrivateApplyRecoveryState state,
        JournalEvent item, ISet<NativePrivateApplyStoreKind> intentDurableSeen)
    {
        if (item.Receipt is not null) return;
        for (int i = 0; i < item.Steps.Length; i++)
            if (item.Steps[i] == NativePrivateApplyStepObservation.IntentDurable)
                intentDurableSeen.Add(state.Steps[i].StoreKind);
    }

    private static void CheckRefinement(NativePrivateApplyRecoveryState state,
        NativePrivateApplyTerminalReceipt receipt,
        IReadOnlySet<NativePrivateApplyStoreKind> intentDurableSeen)
    {
        if (state.Selection != receipt.Selection || state.Steps.Length != receipt.Steps.Length) throw Invalid();
        if (state.Phase is NativePrivateApplyOperationPhase.Claimed or NativePrivateApplyOperationPhase.Prepared)
            throw Invalid();
        if (receipt.Phase != NativePrivateApplyOperationPhase.RecoveryRequired) CheckFinalReceipt(receipt);
        for (int i = 0; i < state.Steps.Length; i++)
        {
            if (state.Steps[i].StoreKind != receipt.Steps[i].StoreKind) throw Invalid();
            NativePrivateApplyStepPhase before = state.Steps[i].Phase, after = receipt.Steps[i].Phase;
            // A receipt cannot execute an unstarted remainder or fabricate a
            // publication for a store that never acquired an intent. Uncertainty
            // must not launder the absence of an earlier durable intent: the
            // event-history provenance, not just the reducer's current phase,
            // controls publication/Recorded refinements. After an intent,
            // refinements remain supplied independent-inspector claims.
            if ((before is NativePrivateApplyStepPhase.Planned or NativePrivateApplyStepPhase.NotStartedProven)
                && (after is NativePrivateApplyStepPhase.Recorded or NativePrivateApplyStepPhase.NoChange
                    or NativePrivateApplyStepPhase.IntentDurable or NativePrivateApplyStepPhase.PublishedVerified)) throw Invalid();
            if (after is NativePrivateApplyStepPhase.Recorded or NativePrivateApplyStepPhase.PublishedVerified)
            {
                if (!intentDurableSeen.Contains(state.Steps[i].StoreKind)) throw Invalid();
            }
            // Proven unstarted is a stop, not an uncertainty state a subsequent
            // receipt may erase. NoChange is established by its own observation,
            // not fabricated by first weakening an unstarted Planned step.
            if (before == NativePrivateApplyStepPhase.NotStartedProven && before != after) throw Invalid();
            if (after == NativePrivateApplyStepPhase.NoChange && before != after) throw Invalid();
            if (after is NativePrivateApplyStepPhase.ConflictOnly or NativePrivateApplyStepPhase.MissingTarget)
            {
                if (before != after && before != NativePrivateApplyStepPhase.Planned) throw Invalid();
            }
            if (after == NativePrivateApplyStepPhase.IntentDurable && before != after) throw Invalid();
            if (after == NativePrivateApplyStepPhase.Planned && before != after) throw Invalid();
        }
    }

    private static void CheckFinalReceipt(NativePrivateApplyTerminalReceipt receipt)
    {
        if (!NativePrivateApplyRecovery.IsTerminal(receipt.Phase)
            || receipt.Phase == NativePrivateApplyOperationPhase.RecoveryRequired
            || receipt.Steps.Any(step => step.Phase is NativePrivateApplyStepPhase.PublishedVerified
                or NativePrivateApplyStepPhase.IntentDurable or NativePrivateApplyStepPhase.Unknown)) throw Invalid();
    }

    private static bool SameSteps(ImmutableArray<NativePrivateApplyRecoveryStep> a, ImmutableArray<NativePrivateApplyRecoveryStep> b)
        => a.Length == b.Length && a.Select((step, index) =>
            step.StoreKind == b[index].StoreKind && step.Phase == b[index].Phase).All(same => same);

    private static bool SameState(NativePrivateApplyRecoveryState a, NativePrivateApplyRecoveryState b)
        => a.OperationGuid == b.OperationGuid && a.RequestSha256 == b.RequestSha256
        && a.Selection == b.Selection && a.Phase == b.Phase && SameSteps(a.Steps, b.Steps);

    private string OperationDirectory(Guid id) => Path.Combine(_root, id.ToString("N"));
    private static void ValidateOperationId(Guid id) { if (id == Guid.Empty) throw Invalid(); }
    private static InvalidDataException Invalid() => new("The private apply journal is invalid or incomplete.");

    private sealed record LoadedOperation(NativePrivateApplyRecoveryState State, int EventCount,
        string LastHash, string ClaimSha256, string RequestBytesSha256,
        ImmutableHashSet<NativePrivateApplyStoreKind> IntentDurableSeen, NativePrivateApplyRequest Request);
}

// Only journal-issued snapshots may be used, only on their issuing instance.
// Scalar revision data is passive and cannot grant store mutation/consent.
internal sealed class NativePrivateApplyJournalSnapshot
{
    private readonly object _owner;
    internal NativePrivateApplyJournalSnapshot(object owner, Guid operationId, string requestSha256,
        int eventCount, string lastRecordSha256, string claimSha256, string requestBytesSha256, string receiptSha256)
    {
        _owner = owner; OperationId = operationId; RequestSha256 = requestSha256; EventCount = eventCount;
        LastRecordSha256 = lastRecordSha256; ClaimSha256 = claimSha256;
        RequestBytesSha256 = requestBytesSha256; ReceiptSha256 = receiptSha256;
    }
    internal bool IsOwnedBy(object owner) => ReferenceEquals(owner, _owner);
    internal Guid OperationId { get; }
    internal string RequestSha256 { get; }
    internal int EventCount { get; }
    internal string LastRecordSha256 { get; }
    internal string ClaimSha256 { get; }
    internal string RequestBytesSha256 { get; }
    internal string ReceiptSha256 { get; }
}

internal sealed class NativePrivateApplyJournalInspection
{
    internal NativePrivateApplyJournalInspection(NativePrivateApplyRecoveryState state,
        int eventCount, string lastRecordSha256, bool final, NativePrivateApplyJournalSnapshot snapshot)
    {
        State = state; EventCount = eventCount; LastRecordSha256 = lastRecordSha256;
        HasTerminalReceipt = final; Snapshot = snapshot;
    }
    internal Guid OperationId => State.OperationGuid;
    internal string RequestSha256 => State.RequestSha256;
    internal NativePrivateMetadataSelection Selection => State.Selection;
    internal NativePrivateApplyRecoveryState State { get; }
    internal int EventCount { get; }
    internal string LastRecordSha256 { get; }
    internal bool HasTerminalReceipt { get; }
    internal NativePrivateApplyJournalSnapshot Snapshot { get; }
}

internal sealed record NativePrivateApplyJournalHeader(Guid OperationId, string RequestSha256,
    NativePrivateMetadataSelection Selection, NativePrivateApplyOperationPhase? TerminalPhase, bool HasTerminalReceipt);
