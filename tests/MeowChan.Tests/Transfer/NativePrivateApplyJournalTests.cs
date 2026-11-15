using System.Collections.Immutable;
using System.Text;
using System.Text.Json.Nodes;
using MeowChan.Testing;
using MeowChan.Persistence;
using MeowChan.Transfer;

namespace MeowChan.Tests.Transfer;

public sealed class NativePrivateApplyJournalTests
{
    private sealed class Workspace : IDisposable
    {
        internal string Root { get; } = TemporaryWorkspacePaths.CreateDirectory("meowchan-private-journal-");
        internal ProfileLease Lease { get; }
        internal NativePrivateApplyJournal Journal { get; }
        internal Workspace() { Lease = ProfileLease.Acquire(Root); Journal = NativePrivateApplyJournal.Open(Lease); }
        internal string Op(Guid id) => Path.Combine(Root, NativePrivateApplyJournal.DirectoryName, id.ToString("N"));
        public void Dispose() { Lease.Dispose(); if (Directory.Exists(Root)) Directory.Delete(Root, true); }
    }

    private static NativePrivateApplyRequest Request(Guid? operationId = null, bool notes = false, bool threads = true, string note = "selected incoming Ω\nexact")
    {
        var id = new ResourceId("synthetic", "b", "100");
        var selection = new NativePrivateMetadataSelection(threads, false, notes);
        var payload = new NativePrivateMetadataProjection([id], selection, [], [],
            notes ? [new PostBookmark(new(id, "101"), note)] : []);
        var rows = new NativePrivateMetadataComparisonResult(selection, payload, [], [],
            notes ? [new(new(id, "101"), note, NativePrivateMetadataDisposition.Conflict)] : []);
        var missing = new NativePrivateApplyStoreImage(false, 0, null, 0, 0, null);
        var fences = ImmutableArray.CreateBuilder<NativePrivateApplyStoreFence>();
        if (threads) fences.Add(new(NativePrivateApplyStoreKind.ThreadFavorites, missing, missing, missing));
        if (notes) fences.Add(new(NativePrivateApplyStoreKind.PostBookmarksAndNotes, missing, missing));
        return NativePrivateApplyRequest.Create(operationId ?? Guid.NewGuid(), rows,
            new(1024, new string('a', 64), new string('b', 64), 100, new string('c', 64),
                Guid.Parse("22222222-2222-2222-2222-222222222222"), new string('d', 64)), fences.ToImmutable());
    }

    private static NativePrivateApplyTerminalReceipt Receipt(NativePrivateApplyJournalInspection inspection,
        NativePrivateApplyOperationPhase phase, params NativePrivateApplyStepPhase[] phases)
        => new(inspection.OperationId, inspection.RequestSha256, inspection.Selection, phase,
            inspection.State.Steps.Select((step, index) => new NativePrivateApplyRecoveryStep(step.StoreKind, phases[index])).ToImmutableArray());

    private static NativePrivateApplyJournalInspection Applying(Workspace workspace, NativePrivateApplyRequest request)
    {
        var claim = workspace.Journal.Claim(request);
        var prepared = workspace.Journal.AppendPrepared(claim.Snapshot);
        return workspace.Journal.AppendApplying(prepared.Snapshot);
    }

    [Fact]
    public void ClaimIsExactIdempotentAndSelectedEmptyIsNotOff()
    {
        using var w = new Workspace(); var request = Request();
        var first = w.Journal.Claim(request); string claimPath = Path.Combine(w.Op(request.OperationId), "claim.json");
        byte[] before = File.ReadAllBytes(claimPath);
        var second = w.Journal.Claim(request);
        Assert.Equal(first.RequestSha256, second.RequestSha256); Assert.Equal(before, File.ReadAllBytes(claimPath));
        Assert.Single(second.State.Steps); Assert.Equal(NativePrivateApplyOperationPhase.Claimed, second.State.Phase);
        Assert.Throws<InvalidDataException>(() => w.Journal.Claim(Request(request.OperationId, threads: false)));
        Assert.Equal(before, File.ReadAllBytes(claimPath));
        var off = w.Journal.Claim(Request(threads: false)); Assert.Empty(off.State.Steps); Assert.False(off.Selection.ThreadFavorites);
    }

    [Fact]
    public void OrderedChainCompletesAndHistoricalReceiptDoesNotReadPayloadOrInventory()
    {
        using var w = new Workspace(); var request = Request(notes: true);
        var state = Applying(w, request);
        state = w.Journal.AppendObservation(state.Snapshot, NativePrivateApplyOperationObservation.None,
            [NativePrivateApplyStepObservation.NoChangeVerified, NativePrivateApplyStepObservation.NotObserved]);
        state = w.Journal.AppendObservation(state.Snapshot, NativePrivateApplyOperationObservation.None,
            [NativePrivateApplyStepObservation.NotObserved, NativePrivateApplyStepObservation.NoChangeVerified]);
        var final = Receipt(state, NativePrivateApplyOperationPhase.Completed,
            NativePrivateApplyStepPhase.NoChange, NativePrivateApplyStepPhase.NoChange);
        var completed = w.Journal.AppendReceipt(state.Snapshot, final);
        Assert.True(completed.HasTerminalReceipt);
        // Exact acknowledgement of the same final write may reuse its old snapshot.
        Assert.True(w.Journal.AppendReceipt(state.Snapshot, final).HasTerminalReceipt);
        File.Delete(Path.Combine(w.Op(request.OperationId), "request.json"));
        File.Delete(Path.Combine(w.Op(request.OperationId), "claim.json"));
        foreach (string file in Directory.EnumerateFiles(w.Op(request.OperationId), "event-*")) File.Delete(file);
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(w.Op(request.OperationId))!, "foreign"), "unknown sibling");
        Assert.Equal(NativePrivateApplyOperationPhase.Completed, w.Journal.Replay(request.OperationId).State.Phase);
        Assert.True(w.Journal.Claim(request).HasTerminalReceipt);
        Assert.Throws<InvalidDataException>(() => w.Journal.Inventory());
        Assert.Throws<InvalidDataException>(() => w.Journal.AppendPrepared(completed.Snapshot));
    }

    [Fact]
    public void SnapshotCannotBeStaleOrTransferredToAnotherJournalInstance()
    {
        using var w = new Workspace(); var claim = w.Journal.Claim(Request());
        var other = NativePrivateApplyJournal.Open(w.Lease);
        Assert.Throws<InvalidDataException>(() => other.AppendPrepared(claim.Snapshot));
        var prepared = w.Journal.AppendPrepared(claim.Snapshot);
        Assert.Throws<InvalidDataException>(() => w.Journal.AppendPrepared(claim.Snapshot));
        Assert.Equal(1, prepared.EventCount); Assert.Equal(1, w.Journal.Inspect(claim.OperationId).EventCount);
    }

    [Fact]
    public void NoProgressPollAndSkippedStepDoNotPersist()
    {
        using var w = new Workspace(); var claim = w.Journal.Claim(Request(notes: true));
        Assert.Throws<InvalidDataException>(() => w.Journal.AppendApplying(claim.Snapshot));
        var state = w.Journal.AppendApplying(w.Journal.AppendPrepared(claim.Snapshot).Snapshot);
        Assert.Throws<InvalidDataException>(() => w.Journal.AppendObservation(state.Snapshot,
            NativePrivateApplyOperationObservation.None, [NativePrivateApplyStepObservation.NotObserved, NativePrivateApplyStepObservation.NotObserved]));
        Assert.Throws<InvalidDataException>(() => w.Journal.AppendObservation(state.Snapshot,
            NativePrivateApplyOperationObservation.None, [NativePrivateApplyStepObservation.NotObserved, NativePrivateApplyStepObservation.IntentDurable]));
        Assert.Equal(2, w.Journal.Inspect(claim.OperationId).EventCount);
    }

    [Fact]
    public void RecoveryRequiresReceiptRecordsAndPreservesRecordedPrefix()
    {
        using var w = new Workspace(); var state = Applying(w, Request(notes: true));
        state = w.Journal.AppendObservation(state.Snapshot, NativePrivateApplyOperationObservation.None,
            [NativePrivateApplyStepObservation.IntentDurable, NativePrivateApplyStepObservation.NotObserved]);
        state = w.Journal.AppendObservation(state.Snapshot, NativePrivateApplyOperationObservation.None,
            [NativePrivateApplyStepObservation.ExactAfterWithMatchingAtomicMarker, NativePrivateApplyStepObservation.NotObserved]);
        state = w.Journal.AppendObservation(state.Snapshot, NativePrivateApplyOperationObservation.None,
            [NativePrivateApplyStepObservation.NotObserved, NativePrivateApplyStepObservation.Unknown]);
        Assert.Equal(NativePrivateApplyOperationPhase.RecoveryRequired, state.State.Phase);
        Assert.Throws<InvalidDataException>(() => w.Journal.AppendObservation(state.Snapshot,
            NativePrivateApplyOperationObservation.None, [NativePrivateApplyStepObservation.RecordedReceiptVerified, NativePrivateApplyStepObservation.NotObserved]));
        var refinement = Receipt(state, NativePrivateApplyOperationPhase.RecoveryRequired,
            NativePrivateApplyStepPhase.Recorded, NativePrivateApplyStepPhase.NotStartedProven);
        state = w.Journal.AppendReceiptRecord(state.Snapshot, refinement);
        Assert.Equal(6, state.EventCount);
        var partial = Receipt(state, NativePrivateApplyOperationPhase.Partial,
            NativePrivateApplyStepPhase.Recorded, NativePrivateApplyStepPhase.NotStartedProven);
        state = w.Journal.AppendReceipt(state.Snapshot, partial);
        Assert.Equal(NativePrivateApplyOperationPhase.Partial, state.State.Phase); Assert.True(state.HasTerminalReceipt);
        Assert.Equal(state.State.Phase, NativePrivateApplyJournal.Open(w.Lease).Replay(state.OperationId).State.Phase);
    }

    [Fact]
    public void StaleTargetCanBeRecordedAsNeedsReviewWithoutBecomingFailureBeforeApply()
    {
        using var w = new Workspace();
        var state = Applying(w, Request());
        state = w.Journal.AppendObservation(state.Snapshot,
            NativePrivateApplyOperationObservation.None,
            [NativePrivateApplyStepObservation.Drifted]);

        Assert.Equal(NativePrivateApplyOperationPhase.NeedsReview, state.State.Phase);
        var review = Receipt(state, NativePrivateApplyOperationPhase.NeedsReview,
            NativePrivateApplyStepPhase.Drifted);
        var finalized = w.Journal.AppendReceipt(state.Snapshot, review);

        Assert.Equal(NativePrivateApplyOperationPhase.NeedsReview, finalized.State.Phase);
        Assert.True(finalized.HasTerminalReceipt);
        Assert.Equal(NativePrivateApplyOperationPhase.NeedsReview,
            w.Journal.Replay(state.OperationId).State.Phase);
    }

    [Fact]
    public void PartialWithUnrecordedPublicationIsRejected()
    {
        using var w = new Workspace(); var state = Applying(w, Request(notes: true));
        state = w.Journal.AppendObservation(state.Snapshot, NativePrivateApplyOperationObservation.None,
            [NativePrivateApplyStepObservation.IntentDurable, NativePrivateApplyStepObservation.NotObserved]);
        state = w.Journal.AppendObservation(state.Snapshot, NativePrivateApplyOperationObservation.None,
            [NativePrivateApplyStepObservation.ExactAfterWithMatchingAtomicMarker, NativePrivateApplyStepObservation.NotObserved]);
        state = w.Journal.AppendObservation(state.Snapshot, NativePrivateApplyOperationObservation.None,
            [NativePrivateApplyStepObservation.NotObserved, NativePrivateApplyStepObservation.ExactBeforeWithContinuousFence]);
        var partial = Receipt(state, NativePrivateApplyOperationPhase.Partial,
            NativePrivateApplyStepPhase.PublishedVerified, NativePrivateApplyStepPhase.NotStartedProven);
        Assert.Throws<InvalidDataException>(() => w.Journal.AppendReceipt(state.Snapshot, partial));
        Assert.False(File.Exists(Path.Combine(w.Op(state.OperationId), "receipt.json")));
    }

    [Theory]
    [InlineData("missing-first")]
    [InlineData("wrong-chain")]
    [InlineData("payload")]
    [InlineData("unknown")]
    public void DamagedEvidenceFailsClosedAndInspectionDoesNotRepair(string damage)
    {
        using var w = new Workspace(); var state = Applying(w, Request(notes: true)); string dir = w.Op(state.OperationId);
        if (damage == "missing-first") File.Delete(Path.Combine(dir, "event-0001.json"));
        if (damage == "wrong-chain")
        {
            string path = Path.Combine(dir, "event-0002.json"); var json = JsonNode.Parse(File.ReadAllBytes(path))!;
            json["previousRecordSha256"] = new string('f', 64); File.WriteAllText(path, json.ToJsonString());
        }
        if (damage == "payload") File.AppendAllText(Path.Combine(dir, "request.json"), " ");
        if (damage == "unknown") File.WriteAllText(Path.Combine(dir, "unknown.pending"), "evidence");
        string[] names = Directory.GetFiles(dir).Order().ToArray();
        Assert.Throws<InvalidDataException>(() => w.Journal.Inspect(state.OperationId));
        Assert.Equal(names, Directory.GetFiles(dir).Order().ToArray());
    }

    [Fact]
    public void DurablePhaseMetadataContainsNoIncomingNoteOrProfilePath()
    {
        using var w = new Workspace(); const string note = "SYNTHETIC_SELECTED_NOTE_123";
        var request = Request(notes: true, note: note); var state = Applying(w, request);
        string dir = w.Op(state.OperationId);
        Assert.Contains(note, File.ReadAllText(Path.Combine(dir, "request.json")));
        foreach (string path in Directory.EnumerateFiles(dir).Where(path => Path.GetFileName(path) != "request.json"))
        {
            string text = File.ReadAllText(path); Assert.DoesNotContain(note, text); Assert.DoesNotContain(w.Root, text);
            Assert.DoesNotContain("targetNote", text); Assert.DoesNotContain("owner", text, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void SameRequestHashDifferentOperationIsNotAnIdentity()
    {
        using var w = new Workspace(); var a = Request(); var b = Request();
        Assert.Equal(a.RequestSha256, b.RequestSha256);
        w.Journal.Claim(a); w.Journal.Claim(b);
        File.Copy(Path.Combine(w.Op(a.OperationId), "claim.json"), Path.Combine(w.Op(b.OperationId), "claim.json"), true);
        Assert.Throws<InvalidDataException>(() => w.Journal.Inspect(b.OperationId));
    }
}
