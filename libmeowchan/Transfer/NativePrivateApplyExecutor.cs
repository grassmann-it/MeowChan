using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading;
using MeowChan.Favorites;
using MeowChan.Persistence;

namespace MeowChan.Transfer;

/// <summary>
/// The result of one owner-authorized private Apply attempt. The inspection is
/// the only result surface; it contains no selected private values or store
/// bytes. A replay may be a historical terminal receipt or an incomplete
/// operation that still needs the separate recovery workflow.
/// </summary>
internal sealed class NativePrivateApplyExecutionResult
{
    internal NativePrivateApplyExecutionResult(NativePrivateApplyJournalInspection inspection, bool isReplay)
    {
        Inspection = inspection ?? throw new ArgumentNullException(nameof(inspection));
        IsReplay = isReplay;
    }

    internal NativePrivateApplyJournalInspection Inspection { get; }
    // Alias kept for callers that treat the result as a journal outcome.
    internal NativePrivateApplyJournalInspection Journal => Inspection;
    internal bool IsReplay { get; }
}

/// <summary>
/// Executes the selected private metadata stores through the live, owner-issued
/// capability. Requests and journal files are passive evidence; this class is
/// intentionally the only bridge that can combine them with the exclusive
/// admission token and the three store-owned batch primitives.
///
/// Store order is fixed: thread favorites, media favorites, then bookmarks and
/// notes. A terminal receipt is written only after every changed store has been
/// read back, had its atomic marker retired, and has recorded its settled step.
/// </summary>
internal static class NativePrivateApplyExecutor
{
    private static readonly NativePrivateApplyStoreKind[] StoreOrder =
    [
        NativePrivateApplyStoreKind.ThreadFavorites,
        NativePrivateApplyStoreKind.MediaFavorites,
        NativePrivateApplyStoreKind.PostBookmarksAndNotes
    ];

    internal static NativePrivateApplyExecutionResult Execute(
        NativePrivateApplyCapability capability, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(capability);
        // The callback receives the exact live lease and exclusive token. No
        // overload accepts a decoded request plus a caller-supplied token.
        return capability.RunAdmitted((request, lease, admission) =>
            ExecuteAdmitted(request, lease, admission, cancellationToken));
    }

    private static NativePrivateApplyExecutionResult ExecuteAdmitted(
        NativePrivateApplyRequest request, ProfileLease lease, ProfileAdmissionToken admission,
        CancellationToken cancellationToken)
    {
        NativePrivateApplyJournal journal = NativePrivateApplyJournal.Open(lease);

        // Receipt-first is deliberate: a retry of a completed operation must not
        // read current stores, inspect payloads, or observe cancellation before
        // the immutable historical result is returned.
        NativePrivateApplyJournalInspection claimed = journal.Claim(request);
        if (claimed.HasTerminalReceipt)
            return new(claimed, isReplay: true);

        // A process which already advanced into Applying has no transient batch
        // capability anymore. Do not guess whether a marker was published; leave
        // it to the recovery inspector. Claimed/Prepared contain no store intent
        // and can safely continue under the still-live owner capability.
        if (claimed.State.Phase == NativePrivateApplyOperationPhase.Applying)
            return new(claimed, isReplay: true);
        if (NativePrivateApplyRecovery.IsTerminal(claimed.State.Phase))
            return new(claimed, isReplay: true);

        NativePrivateApplyJournalInspection prepared = claimed;
        if (claimed.State.Phase == NativePrivateApplyOperationPhase.Claimed)
        {
            cancellationToken.ThrowIfCancellationRequested();
            prepared = journal.AppendPrepared(claimed.Snapshot);
        }

        if (prepared.State.Phase != NativePrivateApplyOperationPhase.Prepared)
            return new(prepared, isReplay: true);

        // Cancellation before Applying has not established any store intent and
        // therefore gets the precise non-publication terminal phase.
        if (cancellationToken.IsCancellationRequested)
            return new(CompleteBeforeApply(journal, request, prepared,
                NativePrivateApplyOperationObservation.CancelledBeforeApply), isReplay: false);

        NativePrivateApplyJournalInspection applying = journal.AppendApplying(prepared.Snapshot);
        NativePrivateApplyJournalInspection cursor = applying;

        // A cancellation racing the Applying event is no longer a pre-apply
        // cancellation. The operation is intentionally made inspectable instead
        // of pretending that no publication could have happened.
        if (cancellationToken.IsCancellationRequested)
            return new(CompleteRecovery(journal, request, cursor, null), isReplay: false);

        foreach (NativePrivateApplyStoreKind kind in StoreOrder)
        {
            if (!NativePrivateApplyRecovery.IsSelected(request.Selection, kind))
                continue;

            try
            {
                cursor = ProcessStore(journal, request, lease, admission, cursor, kind,
                    cancellationToken);
                // Recovery can be terminal in the chain without receipt.json.
                // Stop before another store or a synthetic Completed receipt.
                if (cursor.HasTerminalReceipt || NativePrivateApplyRecovery.IsTerminal(cursor.State.Phase))
                    return new(cursor, isReplay: false);
            }
            catch (OperationCanceledException)
            {
                // Once Applying has been journaled, cancellation is an uncertain
                // outcome even when the store threw before its physical replace.
                return new(CompleteRecovery(journal, request, cursor, kind), isReplay: false);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException
                or InvalidDataException or InvalidOperationException or ArgumentException
                or OverflowException)
            {
                // Store APIs deliberately throw bounded, data-safe errors. The
                // journal remains the source of truth; never turn these into a
                // false FailedBeforeApply after durable intent.
                return new(CompleteRecovery(journal, request, cursor, kind), isReplay: false);
            }
        }

        NativePrivateApplyTerminalReceipt receipt = new(request.OperationId, request.RequestSha256,
            request.Selection, NativePrivateApplyOperationPhase.Completed, cursor.State.Steps);
        NativePrivateApplyJournalInspection completed = journal.AppendReceipt(cursor.Snapshot, receipt);
        return new(completed, isReplay: false);
    }

    private static NativePrivateApplyJournalInspection ProcessStore(
        NativePrivateApplyJournal journal, NativePrivateApplyRequest request,
        ProfileLease lease, ProfileAdmissionToken admission,
        NativePrivateApplyJournalInspection cursor, NativePrivateApplyStoreKind kind,
        CancellationToken cancellationToken)
    {
        NativePrivateApplyStoreFence fence = FenceFor(request, kind);
        NativePrivateApplyStoreInspection sample = journal.InspectStores(request.OperationId);
        NativePrivateApplyJournalInspection sampledJournal = sample.Journal;
        PrivateStoreBatchInspection observed = sample.Stores
            .Single(row => row.StoreKind == kind).Inspection;

        bool hasAdditions = HasAdditions(request, kind);
        if (observed == PrivateStoreBatchInspection.Drifted)
        {
            // Drift before a durable store intent is actionable review, not an
            // unknown write. Preserve the exact stale step and finalise it.
            NativePrivateApplyJournalInspection stale = ObserveStore(journal, sampledJournal, kind,
                NativePrivateApplyStepObservation.Drifted);
            NativePrivateApplyTerminalReceipt receipt = new(request.OperationId,
                request.RequestSha256, request.Selection, NativePrivateApplyOperationPhase.NeedsReview,
                stale.State.Steps);
            return journal.AppendReceipt(stale.Snapshot, receipt);
        }
        if (observed == PrivateStoreBatchInspection.Unreadable)
            return CompleteRecovery(journal, request, sampledJournal, kind,
                NativePrivateApplyStepPhase.Unreadable);

        if (!hasAdditions)
        {
            // The comparison rows retain the distinction between conflict and
            // missing target when there is no write. A mixed conflict/missing
            // store has no corresponding recovery phase, so conservatively keep
            // it as a no-change fact while the rows remain available to UI.
            NativePrivateApplyStepObservation outcome = NonMutatingOutcome(request, kind);
            if (observed != PrivateStoreBatchInspection.NoChange)
                return CompleteRecovery(journal, request, sampledJournal, kind);
            return ObserveStore(journal, sampledJournal, kind, outcome);
        }

        // A changed fence is required for an additive batch. NoChange here means
        // the target changed after comparison (for example another writer added
        // the value), not that the requested Add can be silently downgraded.
        if (observed != PrivateStoreBatchInspection.ExactBefore)
            return CompleteRecovery(journal, request, sampledJournal, kind);

        NativePrivateApplyJournalInspection intent = ObserveStore(journal, sampledJournal, kind,
            NativePrivateApplyStepObservation.IntentDurable);

        cancellationToken.ThrowIfCancellationRequested();
        PreparedStoreBatch prepared = PrepareBatch(request, lease, kind, cancellationToken);
        if (!prepared.Batch.HasChanges || !SameFence(prepared.Batch.Fence, fence))
            return CompleteRecovery(journal, request, intent, kind);

        PrivateStoreBatchInspection published = CommitBatch(prepared, admission, kind,
            cancellationToken);
        if (published != PrivateStoreBatchInspection.ExactAfterWithMarker)
            return CompleteRecovery(journal, request, intent, kind);

        NativePrivateApplyJournalInspection marked = ObserveStore(journal, intent, kind,
            NativePrivateApplyStepObservation.ExactAfterWithMatchingAtomicMarker);

        // Retirement is a store-owned exact-fenced operation. Its return value is
        // already an uncancelled readback proof; both Retired and AlreadyRetired
        // are successful idempotent outcomes.
        _ = RetireBatch(prepared, request, admission, kind, cancellationToken);
        return ObserveStore(journal, marked, kind,
            NativePrivateApplyStepObservation.RecordedReceiptVerified);
    }

    private sealed class PreparedStoreBatch
    {
        internal PreparedStoreBatch(object store, PrivateStoreBatch batch)
        {
            Store = store ?? throw new ArgumentNullException(nameof(store));
            Batch = batch ?? throw new ArgumentNullException(nameof(batch));
        }

        internal object Store { get; }
        internal PrivateStoreBatch Batch { get; }
    }

    private static PreparedStoreBatch PrepareBatch(NativePrivateApplyRequest request,
        ProfileLease lease, NativePrivateApplyStoreKind kind, CancellationToken token)
    {
        Guid operation = request.OperationId;
        string contentPlan = request.ContentPlanSha256;
        return kind switch
        {
            NativePrivateApplyStoreKind.ThreadFavorites => PrepareThreadFavorites(),
            NativePrivateApplyStoreKind.MediaFavorites => PrepareMediaFavorites(),
            NativePrivateApplyStoreKind.PostBookmarksAndNotes => PrepareBookmarks(),
            _ => throw new InvalidDataException("The private Apply store kind is invalid.")
        };

        PreparedStoreBatch PrepareThreadFavorites()
        {
            ThreadFavoriteStore store = new(lease.ProfileDirectory, lease);
            PrivateStoreBatch batch = store.PrepareAdditiveBatch(operation, contentPlan,
                request.ThreadFavorites.Where(row => row.Disposition == NativePrivateMetadataDisposition.Add)
                    .Select(row => row.Target).ToArray(), token);
            return new(store, batch);
        }

        PreparedStoreBatch PrepareMediaFavorites()
        {
            FavoriteStore store = new(lease.ProfileDirectory, lease);
            PrivateStoreBatch batch = store.PrepareAdditiveBatch(operation, contentPlan,
                request.MediaFavorites.Where(row => row.Disposition == NativePrivateMetadataDisposition.Add)
                    .Select(row => row.IncomingEntry ?? throw new InvalidDataException(
                        "A media favorite addition is missing its bound metadata."))
                    .ToArray(), token);
            return new(store, batch);
        }

        PreparedStoreBatch PrepareBookmarks()
        {
            PostBookmarkStore store = PostBookmarkStore.OpenWithoutCleanup(lease.ProfileDirectory, lease);
            PrivateStoreBatch batch = store.PrepareAdditiveBatch(operation, contentPlan,
                request.PostBookmarksAndNotes.Where(row => row.Disposition == NativePrivateMetadataDisposition.Add)
                    .Select(row => new PostBookmark(row.Target, row.IncomingNote)).ToArray(), token);
            return new(store, batch);
        }
    }

    private static PrivateStoreBatchInspection CommitBatch(PreparedStoreBatch prepared,
        ProfileAdmissionToken admission, NativePrivateApplyStoreKind kind, CancellationToken token)
    {
        return kind switch
        {
            NativePrivateApplyStoreKind.ThreadFavorites =>
                ((ThreadFavoriteStore)prepared.Store).CommitPreparedBatchAdmitted(
                    prepared.Batch, admission, token),
            NativePrivateApplyStoreKind.MediaFavorites =>
                ((FavoriteStore)prepared.Store).CommitPreparedBatchAdmitted(
                    prepared.Batch, admission, token),
            NativePrivateApplyStoreKind.PostBookmarksAndNotes =>
                ((PostBookmarkStore)prepared.Store).CommitPreparedBatchAdmitted(
                    prepared.Batch, admission, token),
            _ => throw new InvalidDataException("The private Apply store kind is invalid.")
        };
    }

    private static PrivateStoreMarkerRetirementResult RetireBatch(PreparedStoreBatch prepared,
        NativePrivateApplyRequest request, ProfileAdmissionToken admission,
        NativePrivateApplyStoreKind kind, CancellationToken token)
    {
        NativePrivateApplyStoreFence fence = FenceFor(request, kind);
        // Retirement validates the backup it is handed against the batch's own fence: a pre-existing
        // primary must be re-supplied, otherwise the fallback backup is expected. Passing the backup
        // unconditionally made a successful publication fail afterwards and left recovery work.
        NativePrivateApplyStoreImage? backup = fence.Before?.Exists == true ? fence.Before : fence.BeforeBackup;
        return kind switch
        {
            NativePrivateApplyStoreKind.ThreadFavorites =>
                ((ThreadFavoriteStore)prepared.Store).RetirePreparedBatchMarkerAdmitted(
                    prepared.Batch, request.OperationId, request.ContentPlanSha256, kind,
                    fence.ExpectedAfter, backup, admission, token),
            NativePrivateApplyStoreKind.MediaFavorites =>
                ((FavoriteStore)prepared.Store).RetirePreparedBatchMarkerAdmitted(
                    prepared.Batch, request.OperationId, request.ContentPlanSha256, kind,
                    fence.ExpectedAfter, backup, admission, token),
            NativePrivateApplyStoreKind.PostBookmarksAndNotes =>
                ((PostBookmarkStore)prepared.Store).RetirePreparedBatchMarkerAdmitted(
                    prepared.Batch, request.OperationId, request.ContentPlanSha256, kind,
                    fence.ExpectedAfter, null, admission, token),
            _ => throw new InvalidDataException("The private Apply store kind is invalid.")
        };
    }

    private static NativePrivateApplyJournalInspection CompleteBeforeApply(
        NativePrivateApplyJournal journal, NativePrivateApplyRequest request,
        NativePrivateApplyJournalInspection current,
        NativePrivateApplyOperationObservation operation)
    {
        NativePrivateApplyJournalInspection stopped = journal.AppendObservation(current.Snapshot,
            operation, []);
        NativePrivateApplyTerminalReceipt receipt = new(request.OperationId, request.RequestSha256,
            request.Selection, operation == NativePrivateApplyOperationObservation.CancelledBeforeApply
                ? NativePrivateApplyOperationPhase.CancelledBeforeApply
                : NativePrivateApplyOperationPhase.FailedBeforeApply,
            stopped.State.Steps);
        NativePrivateApplyJournalInspection completed = journal.AppendReceipt(stopped.Snapshot, receipt);
        return new NativePrivateApplyExecutionResult(completed, isReplay: false).Inspection;
    }

    private static NativePrivateApplyJournalInspection CompleteRecovery(
        NativePrivateApplyJournal journal, NativePrivateApplyRequest request,
        NativePrivateApplyJournalInspection current, NativePrivateApplyStoreKind? kind,
        NativePrivateApplyStepPhase? replacement = null)
    {
        // Reconcile the journal head before publishing a recovery receipt. A
        // store may have returned an error after the marker was actually written.
        NativePrivateApplyJournalInspection latest = journal.Inspect(request.OperationId);
        if (latest.HasTerminalReceipt)
            return latest;

        NativePrivateApplyRecoveryState state = latest.State;
        if (state.Phase == NativePrivateApplyOperationPhase.NeedsReview)
        {
            NativePrivateApplyTerminalReceipt review = new(request.OperationId,
                request.RequestSha256, request.Selection,
                NativePrivateApplyOperationPhase.NeedsReview, state.Steps);
            return journal.AppendReceipt(latest.Snapshot, review);
        }
        if (state.Phase == NativePrivateApplyOperationPhase.RecoveryRequired)
            return latest;

        ImmutableArray<NativePrivateApplyRecoveryStep> steps = state.Steps;
        if (kind is { } storeKind && replacement is { } phase)
        {
            var builder = ImmutableArray.CreateBuilder<NativePrivateApplyRecoveryStep>(steps.Length);
            foreach (NativePrivateApplyRecoveryStep step in steps)
                builder.Add(step.StoreKind == storeKind
                    ? new NativePrivateApplyRecoveryStep(step.StoreKind, phase) : step);
            steps = builder.ToImmutable();
        }

        NativePrivateApplyTerminalReceipt recovery = new(request.OperationId,
            request.RequestSha256, request.Selection,
            NativePrivateApplyOperationPhase.RecoveryRequired, steps);
        // A recovery receipt is an explicit refinement of the current applying
        // snapshot. Do not first append an observation that already turns the
        // state terminal: the journal correctly rejects a no-state-change second
        // receipt record in that case.
        return journal.AppendReceiptRecord(latest.Snapshot, recovery);
    }

    private static NativePrivateApplyJournalInspection ObserveStore(
        NativePrivateApplyJournal journal, NativePrivateApplyJournalInspection current,
        NativePrivateApplyStoreKind kind, NativePrivateApplyStepObservation observation)
    {
        ImmutableArray<NativePrivateApplyStepObservation> observations = current.State.Steps
            .Select(step => step.StoreKind == kind ? observation : NativePrivateApplyStepObservation.NotObserved)
            .ToImmutableArray();
        return journal.AppendObservation(current.Snapshot,
            NativePrivateApplyOperationObservation.None, observations);
    }

    private static NativePrivateApplyStepObservation NonMutatingOutcome(
        NativePrivateApplyRequest request, NativePrivateApplyStoreKind kind)
    {
        bool conflict = kind == NativePrivateApplyStoreKind.PostBookmarksAndNotes
            && request.PostBookmarksAndNotes.Any(row => row.Disposition == NativePrivateMetadataDisposition.Conflict);
        bool missing = kind switch
        {
            NativePrivateApplyStoreKind.ThreadFavorites => request.ThreadFavorites.Any(row => row.Disposition == NativePrivateMetadataDisposition.MissingTarget),
            NativePrivateApplyStoreKind.MediaFavorites => request.MediaFavorites.Any(row => row.Disposition == NativePrivateMetadataDisposition.MissingTarget),
            NativePrivateApplyStoreKind.PostBookmarksAndNotes => request.PostBookmarksAndNotes.Any(row => row.Disposition == NativePrivateMetadataDisposition.MissingTarget),
            _ => false
        };
        if (conflict && !missing) return NativePrivateApplyStepObservation.ConflictOnlyVerified;
        if (missing && !conflict) return NativePrivateApplyStepObservation.MissingTargetVerified;
        return NativePrivateApplyStepObservation.NoChangeVerified;
    }

    private static bool HasAdditions(NativePrivateApplyRequest request, NativePrivateApplyStoreKind kind)
        => kind switch
        {
            NativePrivateApplyStoreKind.ThreadFavorites => request.ThreadFavorites.Any(row => row.Disposition == NativePrivateMetadataDisposition.Add),
            NativePrivateApplyStoreKind.MediaFavorites => request.MediaFavorites.Any(row => row.Disposition == NativePrivateMetadataDisposition.Add),
            NativePrivateApplyStoreKind.PostBookmarksAndNotes => request.PostBookmarksAndNotes.Any(row => row.Disposition == NativePrivateMetadataDisposition.Add),
            _ => false
        };

    private static NativePrivateApplyStoreFence FenceFor(NativePrivateApplyRequest request,
        NativePrivateApplyStoreKind kind)
        => request.StoreFences.Single(fence => fence.Kind == kind);

    private static bool SameFence(NativePrivateApplyStoreFence actual,
        NativePrivateApplyStoreFence expected)
        => PrivateStoreBatchFiles.Same(actual.Before, expected.Before)
            && PrivateStoreBatchFiles.Same(actual.ExpectedAfter, expected.ExpectedAfter)
            && ((actual.BeforeBackup is null && expected.BeforeBackup is null)
                || actual.BeforeBackup is not null && expected.BeforeBackup is not null
                    && PrivateStoreBatchFiles.Same(actual.BeforeBackup, expected.BeforeBackup));
}
