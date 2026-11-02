using System;
using System.Collections.Immutable;
using System.IO;

namespace MeowChan.Transfer;

internal enum NativePrivateApplyStoreKind { ThreadFavorites, MediaFavorites, PostBookmarksAndNotes }

internal enum NativePrivateApplyOperationPhase
{
    Claimed, Prepared, Applying, Completed, CancelledBeforeApply, Partial,
    FailedBeforeApply, RecoveryRequired,
}

internal enum NativePrivateApplyStepPhase
{
    Planned, IntentDurable, PublishedVerified, Recorded, NoChange,
    NotStartedProven, Drifted, Unreadable, Unknown,
}

// Observations are assertions supplied by a future bounded, identity-bound
// inspector. This pure contract does not obtain or authenticate those proofs.
// A matching atomic marker must eventually be verified through OperationGuid +
// ContentPlanSha256 and the request's expected-after chain, not a cyclic marker
// containing RequestSha256. Only the inspector can establish that relationship.
internal enum NativePrivateApplyOperationObservation
{
    None, Prepared, Applying, CancelledBeforeApply, FailedBeforeApply,
}

internal enum NativePrivateApplyStepObservation
{
    NotObserved, Unknown, ExactBeforeWithContinuousFence, ExactAfterWithoutMarker,
    ExactAfterWithMatchingAtomicMarker, NoChangeVerified, RecordedReceiptVerified,
    Drifted, Unreadable, IntentDurable,
}

internal sealed class NativePrivateApplyRecoveryStep
{
    internal NativePrivateApplyRecoveryStep(NativePrivateApplyStoreKind storeKind,
        NativePrivateApplyStepPhase phase)
    {
        if (!Enum.IsDefined(storeKind) || !Enum.IsDefined(phase))
            throw NativePrivateApplyRecovery.Invalid();
        StoreKind = storeKind;
        Phase = phase;
    }

    internal NativePrivateApplyStoreKind StoreKind { get; }
    internal NativePrivateApplyStepPhase Phase { get; }
}

/// <summary>
/// Passive journal snapshot, not consent, a validated disk journal or a commit
/// capability. Each selected category has exactly one step, even selected-empty.
/// No notes, target identities, paths, store images or mutation commands occur here.
/// </summary>
internal sealed class NativePrivateApplyRecoveryState
{
    internal NativePrivateApplyRecoveryState(Guid operationGuid, string requestSha256,
        NativePrivateMetadataSelection selection, NativePrivateApplyOperationPhase phase,
        ImmutableArray<NativePrivateApplyRecoveryStep> steps)
    {
        NativePrivateApplyRecovery.ValidateIdentity(operationGuid, requestSha256);
        if (!Enum.IsDefined(phase) || steps.IsDefault || steps.Length > 3)
            throw NativePrivateApplyRecovery.Invalid();

        int index = 0;
        for (int kind = 0; kind < 3; kind++)
        {
            NativePrivateApplyStoreKind storeKind = (NativePrivateApplyStoreKind)kind;
            if (!NativePrivateApplyRecovery.IsSelected(selection, storeKind)) continue;
            if (index >= steps.Length || steps[index] is null || steps[index].StoreKind != storeKind)
                throw NativePrivateApplyRecovery.Invalid();
            index++;
        }
        if (index != steps.Length) throw NativePrivateApplyRecovery.Invalid();
        ValidatePhases(phase, steps);
        OperationGuid = operationGuid;
        RequestSha256 = requestSha256;
        Selection = selection;
        Phase = phase;
        Steps = steps;
    }

    internal Guid OperationGuid { get; }
    internal string RequestSha256 { get; }
    internal NativePrivateMetadataSelection Selection { get; }
    internal NativePrivateApplyOperationPhase Phase { get; }
    internal ImmutableArray<NativePrivateApplyRecoveryStep> Steps { get; }

    internal static NativePrivateApplyRecoveryState CreateClaimed(Guid operationGuid,
        string requestSha256, NativePrivateMetadataSelection selection,
        ImmutableArray<NativePrivateApplyRecoveryStep> steps)
        => new(operationGuid, requestSha256, selection,
            NativePrivateApplyOperationPhase.Claimed, steps);

    internal static NativePrivateApplyRecoveryState FromValidated(Guid operationGuid,
        string requestSha256, NativePrivateMetadataSelection selection,
        NativePrivateApplyOperationPhase phase,
        ImmutableArray<NativePrivateApplyRecoveryStep> steps)
        => new(operationGuid, requestSha256, selection, phase, steps);

    private static void ValidatePhases(NativePrivateApplyOperationPhase phase,
        ImmutableArray<NativePrivateApplyRecoveryStep> steps)
    {
        bool unsettled = false;
        bool active = false;
        bool publication = false;
        bool incomplete = false;
        bool stoppedOrFailed = false;
        foreach (NativePrivateApplyRecoveryStep step in steps)
        {
            NativePrivateApplyStepPhase value = step.Phase;
            bool settled = value is NativePrivateApplyStepPhase.Recorded or NativePrivateApplyStepPhase.NoChange;
            bool publishing = value is NativePrivateApplyStepPhase.IntentDurable or NativePrivateApplyStepPhase.PublishedVerified;
            // A later store cannot have been published or acknowledged across an
            // unresolved predecessor. Inspection-only outcomes do not imply writes.
            if ((settled || publishing) && unsettled) throw NativePrivateApplyRecovery.Invalid();
            if (publishing)
            {
                if (active) throw NativePrivateApplyRecovery.Invalid();
                active = true;
            }
            if (!settled) unsettled = true;
            publication |= value is NativePrivateApplyStepPhase.Recorded or NativePrivateApplyStepPhase.PublishedVerified;
            incomplete |= !settled;
            stoppedOrFailed |= value is NativePrivateApplyStepPhase.NotStartedProven
                or NativePrivateApplyStepPhase.Drifted or NativePrivateApplyStepPhase.Unreadable;

            if (phase is NativePrivateApplyOperationPhase.Claimed or NativePrivateApplyOperationPhase.Prepared)
            {
                if (value != NativePrivateApplyStepPhase.Planned) throw NativePrivateApplyRecovery.Invalid();
            }

            if (phase == NativePrivateApplyOperationPhase.Completed && !settled)
                throw NativePrivateApplyRecovery.Invalid();
            if (phase is NativePrivateApplyOperationPhase.CancelledBeforeApply or NativePrivateApplyOperationPhase.FailedBeforeApply)
            {
                if (value is not (NativePrivateApplyStepPhase.Planned or NativePrivateApplyStepPhase.NotStartedProven
                    or NativePrivateApplyStepPhase.NoChange))
                    throw NativePrivateApplyRecovery.Invalid();
            }
            if (phase == NativePrivateApplyOperationPhase.Partial
                && value is not (NativePrivateApplyStepPhase.Recorded or NativePrivateApplyStepPhase.NoChange
                    or NativePrivateApplyStepPhase.PublishedVerified or NativePrivateApplyStepPhase.NotStartedProven
                    or NativePrivateApplyStepPhase.Drifted or NativePrivateApplyStepPhase.Unreadable
                    or NativePrivateApplyStepPhase.Planned))
                throw NativePrivateApplyRecovery.Invalid();
        }
        if (phase == NativePrivateApplyOperationPhase.Partial
            && (!publication || !incomplete || !stoppedOrFailed))
            throw NativePrivateApplyRecovery.Invalid();
    }
}

internal sealed class NativePrivateApplyStepEvidence
{
    internal NativePrivateApplyStepEvidence(NativePrivateApplyStoreKind storeKind,
        NativePrivateApplyStepObservation observation)
    {
        if (!Enum.IsDefined(storeKind) || !Enum.IsDefined(observation))
            throw NativePrivateApplyRecovery.Invalid();
        StoreKind = storeKind;
        Observation = observation;
    }

    internal NativePrivateApplyStoreKind StoreKind { get; }
    internal NativePrivateApplyStepObservation Observation { get; }
}

internal sealed class NativePrivateApplyTerminalReceipt
{
    internal NativePrivateApplyTerminalReceipt(Guid operationGuid, string requestSha256,
        NativePrivateMetadataSelection selection, NativePrivateApplyOperationPhase phase,
        ImmutableArray<NativePrivateApplyRecoveryStep> steps)
    {
        NativePrivateApplyRecovery.ValidateIdentity(operationGuid, requestSha256);
        if (!NativePrivateApplyRecovery.IsTerminal(phase)) throw NativePrivateApplyRecovery.Invalid();
        // Constructing a receipt is the only way this pure surface may represent
        // Completed/Partial/recovery outcomes; it still does not persist anything.
        _ = new NativePrivateApplyRecoveryState(operationGuid, requestSha256, selection, phase, steps);
        OperationGuid = operationGuid;
        RequestSha256 = requestSha256;
        Selection = selection;
        Phase = phase;
        Steps = steps;
    }

    internal Guid OperationGuid { get; }
    internal string RequestSha256 { get; }
    internal NativePrivateMetadataSelection Selection { get; }
    internal NativePrivateApplyOperationPhase Phase { get; }
    internal ImmutableArray<NativePrivateApplyRecoveryStep> Steps { get; }

    internal NativePrivateApplyRecoveryState ToState()
        => new(OperationGuid, RequestSha256, Selection, Phase, Steps);
}

internal sealed class NativePrivateApplyRecoveryEvidence
{
    internal NativePrivateApplyRecoveryEvidence(Guid operationGuid, string requestSha256,
        ImmutableArray<NativePrivateApplyStepEvidence> steps,
        NativePrivateApplyTerminalReceipt? terminalReceipt = null)
        : this(operationGuid, requestSha256, NativePrivateApplyOperationObservation.None,
            steps, terminalReceipt)
    {
    }

    internal NativePrivateApplyRecoveryEvidence(Guid operationGuid, string requestSha256,
        NativePrivateApplyOperationObservation operationObservation,
        ImmutableArray<NativePrivateApplyStepEvidence> steps,
        NativePrivateApplyTerminalReceipt? terminalReceipt = null)
    {
        NativePrivateApplyRecovery.ValidateIdentity(operationGuid, requestSha256);
        if (!Enum.IsDefined(operationObservation) || steps.IsDefault || steps.Length > 3)
            throw NativePrivateApplyRecovery.Invalid();
        for (int i = 0; i < steps.Length; i++)
        {
            if (steps[i] is null || i > 0 && steps[i - 1].StoreKind >= steps[i].StoreKind)
                throw NativePrivateApplyRecovery.Invalid();
        }
        OperationGuid = operationGuid;
        RequestSha256 = requestSha256;
        OperationObservation = operationObservation;
        Steps = steps;
        TerminalReceipt = terminalReceipt;
    }

    internal Guid OperationGuid { get; }
    internal string RequestSha256 { get; }
    internal NativePrivateApplyOperationObservation OperationObservation { get; }
    // Exact selected store order is required for incomplete-operation inspection.
    // Terminal receipt replay ignores current target observation coverage.
    internal ImmutableArray<NativePrivateApplyStepEvidence> Steps { get; }
    internal NativePrivateApplyTerminalReceipt? TerminalReceipt { get; }
}

/// <summary>
/// Pure recovery classification. No IO, clocks, callbacks, hashing, journal
/// publication, store mutation, automatic resume or apply instructions. Evidence
/// values are passive input, never an alternative to a future real inspector.
/// </summary>
internal static class NativePrivateApplyRecovery
{
    internal static NativePrivateApplyRecoveryState Reduce(NativePrivateApplyRecoveryState state,
        NativePrivateApplyRecoveryEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(evidence);
        if (state.OperationGuid != evidence.OperationGuid
            || !string.Equals(state.RequestSha256, evidence.RequestSha256, StringComparison.Ordinal))
            throw Invalid();

        // Historical replay precedes selection/freshness checks on today's
        // targets. In particular, a later unmark/delete cannot resurrect values.
        if (evidence.TerminalReceipt is { } receipt)
        {
            if (receipt.OperationGuid != state.OperationGuid
                || !string.Equals(receipt.RequestSha256, state.RequestSha256, StringComparison.Ordinal)
                || receipt.Selection != state.Selection) throw Invalid();
            // A later independent final receipt can resolve an uncertain status,
            // but cannot rewrite an already-final historical operation result.
            // This is only data consistency, never receipt provenance/authentication.
            if (IsTerminal(state.Phase) && state.Phase != NativePrivateApplyOperationPhase.RecoveryRequired
                && !SameTerminalState(state, receipt)) throw Invalid();
            for (int i = 0; i < state.Steps.Length; i++)
            {
                NativePrivateApplyStepPhase known = state.Steps[i].Phase;
                NativePrivateApplyStepPhase reported = receipt.Steps[i].Phase;
                // An uncertain later step does not invalidate already recorded
                // history. Receipt refinement must preserve the known prefix.
                if (known is NativePrivateApplyStepPhase.Recorded or NativePrivateApplyStepPhase.NoChange
                    && reported != known) throw Invalid();
                if (known == NativePrivateApplyStepPhase.PublishedVerified
                    && reported is not (NativePrivateApplyStepPhase.PublishedVerified
                        or NativePrivateApplyStepPhase.Recorded)) throw Invalid();
            }
            return receipt.ToState();
        }
        if (IsTerminal(state.Phase)) return state;

        NativePrivateApplyOperationPhase observedOperationPhase = ObserveOperation(
            state.Phase, evidence.OperationObservation);
        bool changed = observedOperationPhase != state.Phase;
        if (evidence.Steps.Length != state.Steps.Length) throw Invalid();
        for (int i = 0; i < state.Steps.Length; i++)
            if (evidence.Steps[i].StoreKind != state.Steps[i].StoreKind) throw Invalid();

        var next = ImmutableArray.CreateBuilder<NativePrivateApplyRecoveryStep>(state.Steps.Length);
        foreach (NativePrivateApplyRecoveryStep step in state.Steps)
        {
            NativePrivateApplyStepObservation observation = NativePrivateApplyStepObservation.NotObserved;
            foreach (NativePrivateApplyStepEvidence item in evidence.Steps)
                if (item.StoreKind == step.StoreKind) observation = item.Observation;
            if (Advances(observation) && !PreviousStepsSettled(state.Steps, step.StoreKind))
                throw Invalid();
            NativePrivateApplyStepPhase phase = Observe(observedOperationPhase, step.Phase, observation);
            changed |= phase != step.Phase;
            next.Add(phase == step.Phase ? step : new NativePrivateApplyRecoveryStep(step.StoreKind, phase));
        }
        if (!changed) return state;
        ImmutableArray<NativePrivateApplyRecoveryStep> steps = next.ToImmutable();
        NativePrivateApplyOperationPhase resultPhase = Classify(observedOperationPhase, steps);
        return new NativePrivateApplyRecoveryState(state.OperationGuid, state.RequestSha256,
            state.Selection, resultPhase, steps);
    }

    private static NativePrivateApplyOperationPhase ObserveOperation(
        NativePrivateApplyOperationPhase current,
        NativePrivateApplyOperationObservation observation)
    {
        return observation switch
        {
            NativePrivateApplyOperationObservation.None => current,
            NativePrivateApplyOperationObservation.Prepared
                when current is NativePrivateApplyOperationPhase.Claimed
                    or NativePrivateApplyOperationPhase.Prepared
                => NativePrivateApplyOperationPhase.Prepared,
            NativePrivateApplyOperationObservation.Applying
                when current is NativePrivateApplyOperationPhase.Prepared
                    or NativePrivateApplyOperationPhase.Applying
                => NativePrivateApplyOperationPhase.Applying,
            NativePrivateApplyOperationObservation.CancelledBeforeApply
                when current is NativePrivateApplyOperationPhase.Claimed
                    or NativePrivateApplyOperationPhase.Prepared
                => NativePrivateApplyOperationPhase.CancelledBeforeApply,
            NativePrivateApplyOperationObservation.FailedBeforeApply
                when current is NativePrivateApplyOperationPhase.Claimed
                    or NativePrivateApplyOperationPhase.Prepared
                => NativePrivateApplyOperationPhase.FailedBeforeApply,
            _ => throw Invalid(),
        };
    }

    private static NativePrivateApplyStepPhase Observe(NativePrivateApplyOperationPhase operationPhase,
        NativePrivateApplyStepPhase current, NativePrivateApplyStepObservation observation)
    {
        if (observation == NativePrivateApplyStepObservation.NotObserved) return current;
        return observation switch
        {
            NativePrivateApplyStepObservation.IntentDurable
                when operationPhase == NativePrivateApplyOperationPhase.Applying
                    && (current is NativePrivateApplyStepPhase.Planned or NativePrivateApplyStepPhase.IntentDurable)
                => NativePrivateApplyStepPhase.IntentDurable,
            NativePrivateApplyStepObservation.ExactBeforeWithContinuousFence
                when current is NativePrivateApplyStepPhase.Planned or NativePrivateApplyStepPhase.IntentDurable
                => NativePrivateApplyStepPhase.NotStartedProven,
            NativePrivateApplyStepObservation.ExactAfterWithoutMarker
                when current is NativePrivateApplyStepPhase.Planned or NativePrivateApplyStepPhase.IntentDurable
                => NativePrivateApplyStepPhase.Unknown,
            NativePrivateApplyStepObservation.ExactAfterWithMatchingAtomicMarker
                when operationPhase == NativePrivateApplyOperationPhase.Applying
                    && (current is NativePrivateApplyStepPhase.IntentDurable or NativePrivateApplyStepPhase.PublishedVerified)
                => NativePrivateApplyStepPhase.PublishedVerified,
            NativePrivateApplyStepObservation.RecordedReceiptVerified
                when current is NativePrivateApplyStepPhase.PublishedVerified or NativePrivateApplyStepPhase.Recorded
                => NativePrivateApplyStepPhase.Recorded,
            NativePrivateApplyStepObservation.NoChangeVerified
                when operationPhase == NativePrivateApplyOperationPhase.Applying
                    && (current is NativePrivateApplyStepPhase.Planned or NativePrivateApplyStepPhase.NoChange)
                => NativePrivateApplyStepPhase.NoChange,
            NativePrivateApplyStepObservation.Unknown
                when current is NativePrivateApplyStepPhase.Planned or NativePrivateApplyStepPhase.IntentDurable
                => NativePrivateApplyStepPhase.Unknown,
            NativePrivateApplyStepObservation.Drifted
                when current is NativePrivateApplyStepPhase.Planned or NativePrivateApplyStepPhase.IntentDurable
                => NativePrivateApplyStepPhase.Drifted,
            NativePrivateApplyStepObservation.Unreadable
                when current is NativePrivateApplyStepPhase.Planned or NativePrivateApplyStepPhase.IntentDurable
                => NativePrivateApplyStepPhase.Unreadable,
            // No phase may skip durable intent, publication verification or the
            // separate durable receipt. Incompatible observations are not repairs.
            _ => throw Invalid(),
        };
    }

    private static bool Advances(NativePrivateApplyStepObservation observation)
        => observation is NativePrivateApplyStepObservation.IntentDurable
            or NativePrivateApplyStepObservation.ExactAfterWithMatchingAtomicMarker
            or NativePrivateApplyStepObservation.RecordedReceiptVerified
            or NativePrivateApplyStepObservation.NoChangeVerified;

    private static bool PreviousStepsSettled(
        ImmutableArray<NativePrivateApplyRecoveryStep> steps,
        NativePrivateApplyStoreKind storeKind)
    {
        for (int i = 0; i < steps.Length; i++)
        {
            if (steps[i].StoreKind == storeKind) return true;
            if (steps[i].Phase is not NativePrivateApplyStepPhase.Recorded
                and not NativePrivateApplyStepPhase.NoChange)
                return false;
        }
        return false;
    }

    private static NativePrivateApplyOperationPhase Classify(NativePrivateApplyOperationPhase current,
        ImmutableArray<NativePrivateApplyRecoveryStep> steps)
    {
        bool publication = false;
        bool stopped = false;
        foreach (NativePrivateApplyRecoveryStep step in steps)
        {
            if (step.Phase == NativePrivateApplyStepPhase.Unknown)
                return NativePrivateApplyOperationPhase.RecoveryRequired;
            publication |= step.Phase is NativePrivateApplyStepPhase.Recorded or NativePrivateApplyStepPhase.PublishedVerified;
            stopped |= step.Phase == NativePrivateApplyStepPhase.NotStartedProven;
        }
        bool knownFailure = false;
        foreach (NativePrivateApplyRecoveryStep step in steps)
            knownFailure |= step.Phase is NativePrivateApplyStepPhase.Drifted
                or NativePrivateApplyStepPhase.Unreadable;
        if (knownFailure)
        {
            // Drift and unreadable evidence cannot prove non-publication after a
            // durable intent. Keep the operation inspectable; only an independent
            // terminal receipt may classify a Partial outcome.
            return NativePrivateApplyOperationPhase.RecoveryRequired;
        }
        if (stopped)
        {
            // Partial is claimed only if every unexecuted remainder is proven
            // unstarted. A missing observation is not such a proof.
            foreach (NativePrivateApplyRecoveryStep step in steps)
                if (step.Phase is NativePrivateApplyStepPhase.Planned or NativePrivateApplyStepPhase.IntentDurable)
                    return NativePrivateApplyOperationPhase.RecoveryRequired;
            // Publication facts are not a final operation receipt. In particular,
            // PublishedVerified still needs its step receipt. Keep the outcome
            // refinable rather than freezing an unacknowledged prefix as Partial.
            return publication ? NativePrivateApplyOperationPhase.RecoveryRequired : current;
        }
        // All Recorded still awaits an independently resolved final receipt.
        // This reducer neither fabricates Completed nor writes that receipt.
        return current;
    }

    private static bool SameTerminalState(NativePrivateApplyRecoveryState state,
        NativePrivateApplyTerminalReceipt receipt)
    {
        if (state.Phase != receipt.Phase || state.Steps.Length != receipt.Steps.Length) return false;
        for (int i = 0; i < state.Steps.Length; i++)
            if (state.Steps[i].StoreKind != receipt.Steps[i].StoreKind
                || state.Steps[i].Phase != receipt.Steps[i].Phase) return false;
        return true;
    }

    internal static bool IsTerminal(NativePrivateApplyOperationPhase phase)
        => phase is NativePrivateApplyOperationPhase.Completed or NativePrivateApplyOperationPhase.CancelledBeforeApply
            or NativePrivateApplyOperationPhase.Partial or NativePrivateApplyOperationPhase.FailedBeforeApply
            or NativePrivateApplyOperationPhase.RecoveryRequired;

    internal static bool IsSelected(NativePrivateMetadataSelection selection, NativePrivateApplyStoreKind kind)
        => kind switch
        {
            NativePrivateApplyStoreKind.ThreadFavorites => selection.ThreadFavorites,
            NativePrivateApplyStoreKind.MediaFavorites => selection.MediaFavorites,
            NativePrivateApplyStoreKind.PostBookmarksAndNotes => selection.PostBookmarksAndNotes,
            _ => false,
        };

    internal static void ValidateIdentity(Guid operationGuid, string? requestSha256)
    {
        if (operationGuid == Guid.Empty || requestSha256 is null || requestSha256.Length != 64) throw Invalid();
        foreach (char value in requestSha256)
            if (!(value >= '0' && value <= '9' || value >= 'a' && value <= 'f')) throw Invalid();
    }

    internal static InvalidDataException Invalid() => new("Invalid private apply recovery contract.");
}
