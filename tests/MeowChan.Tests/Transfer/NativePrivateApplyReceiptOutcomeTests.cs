using System.Collections.Immutable;
using MeowChan.Persistence;
using MeowChan.Transfer;

namespace MeowChan.Tests.Transfer;

// Physical journal coverage for content-free non-mutating category outcomes.
// ConflictOnly and MissingTarget are facts from the owner-bound planner; this
// test only verifies that the passive journal can preserve them without turning
// them into a write or silently collapsing them to NoChange.
public sealed class NativePrivateApplyReceiptOutcomeTests
{
    private static readonly Guid Operation = Guid.Parse("6c8fbc7e-4e93-4d06-bc20-9d0a01d9d6f8");
    private static readonly Guid Profile = Guid.Parse("b6967c5c-1a67-45f3-92ce-c4cfcfd8a01e");

    private static string Hash(char value) => new(value, 64);

    private static NativePrivateApplyRequest Request()
    {
        var selection = new NativePrivateMetadataSelection(true, true, true);
        var payload = new NativePrivateMetadataProjection(
            [new("synthetic", "b", "100")], selection, [], [], []);
        var rows = new NativePrivateMetadataComparisonResult(selection, payload, [], [], []);
        var missing = new NativePrivateApplyStoreImage(false, 0, null, 0, 0, null);
        var fences = Enum.GetValues<NativePrivateApplyStoreKind>()
            .Select(kind => new NativePrivateApplyStoreFence(
                kind, missing, missing,
                kind == NativePrivateApplyStoreKind.PostBookmarksAndNotes ? null : missing))
            .ToImmutableArray();
        return NativePrivateApplyRequest.Create(Operation, rows,
            new(1024, Hash('1'), Hash('2'), 256, Hash('3'), Profile, Hash('4')), fences);
    }

    [Fact]
    public void JournalRoundTripPreservesConflictAndMissingTargetAsCompletedFacts()
    {
        using TempDirectory temp = new();
        NativePrivateApplyRequest request = Request();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        NativePrivateApplyJournal journal = NativePrivateApplyJournal.Open(lease);

        NativePrivateApplyJournalInspection claimed = journal.Claim(request);
        NativePrivateApplyJournalInspection prepared = journal.AppendPrepared(claimed.Snapshot);
        NativePrivateApplyJournalInspection applying = journal.AppendApplying(prepared.Snapshot);
        NativePrivateApplyJournalInspection conflict = journal.AppendObservation(applying.Snapshot,
            NativePrivateApplyOperationObservation.None,
            [NativePrivateApplyStepObservation.ConflictOnlyVerified,
                NativePrivateApplyStepObservation.NotObserved,
                NativePrivateApplyStepObservation.NotObserved]);
        NativePrivateApplyJournalInspection missing = journal.AppendObservation(conflict.Snapshot,
            NativePrivateApplyOperationObservation.None,
            [NativePrivateApplyStepObservation.NotObserved,
                NativePrivateApplyStepObservation.MissingTargetVerified,
                NativePrivateApplyStepObservation.NotObserved]);
        NativePrivateApplyJournalInspection noChange = journal.AppendObservation(missing.Snapshot,
            NativePrivateApplyOperationObservation.None,
            [NativePrivateApplyStepObservation.NotObserved,
                NativePrivateApplyStepObservation.NotObserved,
                NativePrivateApplyStepObservation.NoChangeVerified]);

        var receipt = new NativePrivateApplyTerminalReceipt(Operation, request.RequestSha256,
            request.Selection, NativePrivateApplyOperationPhase.Completed,
            [new(NativePrivateApplyStoreKind.ThreadFavorites, NativePrivateApplyStepPhase.ConflictOnly),
                new(NativePrivateApplyStoreKind.MediaFavorites, NativePrivateApplyStepPhase.MissingTarget),
                new(NativePrivateApplyStoreKind.PostBookmarksAndNotes, NativePrivateApplyStepPhase.NoChange)]);
        NativePrivateApplyJournalInspection completed = journal.AppendReceipt(noChange.Snapshot, receipt);

        Assert.True(completed.HasTerminalReceipt);
        Assert.Equal(NativePrivateApplyOperationPhase.Completed, completed.State.Phase);
        Assert.Equal(
            new[]
            {
                NativePrivateApplyStepPhase.ConflictOnly,
                NativePrivateApplyStepPhase.MissingTarget,
                NativePrivateApplyStepPhase.NoChange
            },
            completed.State.Steps.Select(step => step.Phase));

        NativePrivateApplyJournalInspection replay = journal.Replay(Operation);
        Assert.True(replay.HasTerminalReceipt);
        Assert.Equal(completed.State.Steps.Select(step => step.Phase),
            replay.State.Steps.Select(step => step.Phase));
    }
}
