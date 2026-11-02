using System.Collections.Immutable;
using System.Text;
using System.Text.Json.Nodes;
using MeowChan.Persistence;
using MeowChan.Transfer;

namespace MeowChan.Tests.Transfer;

// Physical corruption/restart tests over owned synthetic profiles only. The
// journal is passive: no test uses it to obtain consent or mutate a user store.
public sealed class NativePrivateApplyJournalBoundaryTests
{
    private static readonly Guid Operation = Guid.Parse("108cd03c-1768-459b-81c8-20cde26506ea");
    private static readonly Guid Profile = Guid.Parse("6dd0560d-808c-49dd-befa-18d336257b04");
    private static string Hash(char value) => new(value, 64);
    private static string Root(string profile) => Path.Combine(profile, ".meowchan-private-apply");
    private static string DirectoryFor(string profile, Guid? operation = null)
        => Path.Combine(Root(profile), (operation ?? Operation).ToString("N"));
    private static NativePrivateApplyRequest Request(int bits = 7, Guid? operation = null)
    {
        var selection = new NativePrivateMetadataSelection((bits & 1) != 0, (bits & 2) != 0, (bits & 4) != 0);
        var payload = new NativePrivateMetadataProjection([new("synthetic", "b", "100")], selection, [], [], []);
        var rows = new NativePrivateMetadataComparisonResult(selection, payload, [], [], []);
        var missing = new NativePrivateApplyStoreImage(false, 0, null, 0, 0, null);
        var fences = ImmutableArray.CreateBuilder<NativePrivateApplyStoreFence>();
        foreach (var kind in Enum.GetValues<NativePrivateApplyStoreKind>())
            if (NativePrivateApplyRecovery.IsSelected(selection, kind))
                fences.Add(new(kind, missing, missing,
                    kind == NativePrivateApplyStoreKind.PostBookmarksAndNotes ? null : missing));
        return NativePrivateApplyRequest.Create(operation ?? Operation, rows,
            new(1024, Hash('1'), Hash('2'), 256, Hash('3'), Profile, Hash('4')), fences.ToImmutable());
    }
    private static NativePrivateApplyTerminalReceipt Completed(NativePrivateApplyRequest request)
        => new(request.OperationId, request.RequestSha256, request.Selection, NativePrivateApplyOperationPhase.Completed,
            request.StoreFences.Select(x => new NativePrivateApplyRecoveryStep(x.Kind, NativePrivateApplyStepPhase.NoChange)).ToImmutableArray());
    private static void Complete(NativePrivateApplyJournal journal, NativePrivateApplyRequest request)
    {
        journal.Claim(request);
        journal.AppendPrepared(journal.Inspect(request.OperationId).Snapshot);
        journal.AppendApplying(journal.Inspect(request.OperationId).Snapshot);
        // Store-order policy requires separate transitions; never settle a later
        // store in the same observation that settles its predecessor.
        for (int index = 0; index < request.StoreFences.Length; index++)
            journal.AppendObservation(journal.Inspect(request.OperationId).Snapshot, NativePrivateApplyOperationObservation.None,
                request.StoreFences.Select((_, i) => i == index ? NativePrivateApplyStepObservation.NoChangeVerified
                    : NativePrivateApplyStepObservation.NotObserved).ToImmutableArray());
        journal.AppendReceipt(journal.Inspect(request.OperationId).Snapshot, Completed(request));
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    [InlineData(4)] [InlineData(5)] [InlineData(6)] [InlineData(7)]
    public void EverySelectionHasOrderedReplayAndHistoricalReceipt(int bits)
    {
        using TempDirectory temp = new();
        var request = Request(bits);
        using (ProfileLease lease = ProfileLease.Acquire(temp.Path))
        {
            var journal = NativePrivateApplyJournal.Open(lease);
            Complete(journal, request);
            var state = journal.Replay(Operation);
            Assert.True(state.HasTerminalReceipt);
            Assert.Equal(NativePrivateApplyOperationPhase.Completed, state.State.Phase);
            Assert.Equal(request.StoreFences.Length, state.State.Steps.Length);
        }
        using ProfileLease reopened = ProfileLease.Acquire(temp.Path);
        var restored = NativePrivateApplyJournal.Open(reopened).Replay(Operation);
        Assert.True(restored.HasTerminalReceipt);
        Assert.Equal(request.RequestSha256, restored.RequestSha256);
        Assert.Equal(request.Selection, restored.Selection);
    }

    [Theory]
    [InlineData("truncated")] [InlineData("appended")] [InlineData("same-length")] [InlineData("missing")]
    public void DamagedSelectedRequestNeverBecomesAnEmptySuccessfulOperation(string mutation)
    {
        using TempDirectory temp = new(); using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        var journal = NativePrivateApplyJournal.Open(lease);
        journal.Claim(Request());
        string path = Path.Combine(DirectoryFor(temp.Path), "request.json");
        byte[] original = File.ReadAllBytes(path);
        if (mutation == "truncated") File.WriteAllBytes(path, original[..^1]);
        if (mutation == "appended") File.WriteAllBytes(path, original.Concat(new byte[] { 32 }).ToArray());
        if (mutation == "same-length") { original[0] ^= 1; File.WriteAllBytes(path, original); }
        if (mutation == "missing") File.Delete(path);
        Exception? error = Record.Exception(() => journal.Inspect(Operation));
        Assert.True(error is InvalidDataException or IOException, error?.ToString() ?? "Corrupt request accepted");
        Assert.False(File.Exists(Path.Combine(DirectoryFor(temp.Path), "receipt.json")));
    }

    [Theory]
    [InlineData(1)] [InlineData(2)]
    public void EventGapIsNotAShorterValidPrefix(int deletedEvent)
    {
        using TempDirectory temp = new(); using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        var journal = NativePrivateApplyJournal.Open(lease);
        journal.Claim(Request()); journal.AppendPrepared(journal.Inspect(Operation).Snapshot); journal.AppendApplying(journal.Inspect(Operation).Snapshot);
        journal.AppendObservation(journal.Inspect(Operation).Snapshot, NativePrivateApplyOperationObservation.None,
            [NativePrivateApplyStepObservation.NoChangeVerified, NativePrivateApplyStepObservation.NotObserved,
                NativePrivateApplyStepObservation.NotObserved]);
        File.Delete(Path.Combine(DirectoryFor(temp.Path), $"event-{deletedEvent:0000}.json"));
        Assert.Throws<InvalidDataException>(() => journal.Inspect(Operation));
    }

    [Fact]
    public void OperationDirectoryCannotBorrowAnotherOperationsClaim()
    {
        using TempDirectory temp = new(); using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        var journal = NativePrivateApplyJournal.Open(lease); journal.Claim(Request());
        Guid other = Guid.Parse("02955e13-81ba-4519-a265-8a445876e63d");
        Directory.Move(DirectoryFor(temp.Path), DirectoryFor(temp.Path, other));
        Assert.Throws<InvalidDataException>(() => journal.Inspect(other));
    }

    [Fact]
    public void FinalReceiptIsReadWithoutTheSelectedPayloadAfterRestart()
    {
        using TempDirectory temp = new();
        using (ProfileLease lease = ProfileLease.Acquire(temp.Path))
            Complete(NativePrivateApplyJournal.Open(lease), Request());
        File.Delete(Path.Combine(DirectoryFor(temp.Path), "request.json"));
        using ProfileLease reopened = ProfileLease.Acquire(temp.Path);
        var state = NativePrivateApplyJournal.Open(reopened).Replay(Operation);
        Assert.True(state.HasTerminalReceipt);
        Assert.Equal(NativePrivateApplyOperationPhase.Completed, state.State.Phase);
        Assert.False(File.Exists(Path.Combine(DirectoryFor(temp.Path), "request.json")));
    }

    [Fact]
    public void HistoricalReceiptReplayIsNotBlockedByUnknownSiblingInventory()
    {
        using TempDirectory temp = new();
        using (ProfileLease lease = ProfileLease.Acquire(temp.Path))
            Complete(NativePrivateApplyJournal.Open(lease), Request());
        File.WriteAllText(Path.Combine(Root(temp.Path), "unknown-child"), "synthetic unknown evidence");
        using ProfileLease reopened = ProfileLease.Acquire(temp.Path);
        var journal = NativePrivateApplyJournal.Open(reopened);
        Assert.True(journal.Replay(Operation).HasTerminalReceipt);
        Assert.Throws<InvalidDataException>(() => journal.Claim(Request(operation: Guid.NewGuid())));
        Assert.Equal("synthetic unknown evidence", File.ReadAllText(Path.Combine(Root(temp.Path), "unknown-child")));
    }

    [Fact]
    public void LateObservationCannotInvalidateAnAlreadyPublishedTerminalReceipt()
    {
        using TempDirectory temp = new(); using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        var journal = NativePrivateApplyJournal.Open(lease); var request = Request(); Complete(journal, request);
        int count = Directory.GetFiles(DirectoryFor(temp.Path), "event-*.json").Length;
        byte[] receipt = File.ReadAllBytes(Path.Combine(DirectoryFor(temp.Path), "receipt.json"));
        Assert.Throws<InvalidDataException>(() => journal.AppendObservation(journal.Inspect(Operation).Snapshot,
            NativePrivateApplyOperationObservation.None, [NativePrivateApplyStepObservation.Unknown,
                NativePrivateApplyStepObservation.NotObserved, NativePrivateApplyStepObservation.NotObserved]));
        Assert.Equal(count, Directory.GetFiles(DirectoryFor(temp.Path), "event-*.json").Length);
        Assert.Equal(receipt, File.ReadAllBytes(Path.Combine(DirectoryFor(temp.Path), "receipt.json")));
        Assert.True(journal.Replay(Operation).HasTerminalReceipt);
    }

    [Fact]
    public void UnknownPendingEvidenceIsPreservedAndNeverCleanedUpByInspect()
    {
        using TempDirectory temp = new(); using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        var journal = NativePrivateApplyJournal.Open(lease); journal.Claim(Request());
        string path = Path.Combine(DirectoryFor(temp.Path), "event-0001.json.pending-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(path, "synthetic pending evidence");
        Assert.Throws<InvalidDataException>(() => journal.Inspect(Operation));
        Assert.Equal("synthetic pending evidence", File.ReadAllText(path));
    }

    [Fact]
    public void UnreadableAndMalformedClaimErrorsAreOpaque()
    {
        using TempDirectory temp = new(); using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        var journal = NativePrivateApplyJournal.Open(lease); journal.Claim(Request());
        string path = Path.Combine(DirectoryFor(temp.Path), "claim.json");
        var root = JsonNode.Parse(File.ReadAllBytes(path))!;
        root["requestLength"] = "not-a-number";
        File.WriteAllText(path, root.ToJsonString());
        Exception error = Assert.Throws<InvalidDataException>(() => journal.Inspect(Operation));
        Assert.DoesNotContain(temp.Path, error.ToString());
        Assert.Null(error.InnerException);
    }
    [Fact]
    public void StaleAndOtherJournalSnapshotsCannotAdvanceCurrentHistory()
    {
        using TempDirectory temp = new(); using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        var journal = NativePrivateApplyJournal.Open(lease);
        var claimed = journal.Claim(Request());
        var prepared = journal.AppendPrepared(claimed.Snapshot);
        Assert.Throws<InvalidDataException>(() => journal.AppendApplying(claimed.Snapshot));
        var other = NativePrivateApplyJournal.Open(lease);
        Assert.Throws<InvalidDataException>(() => other.AppendApplying(prepared.Snapshot));
        Assert.Equal(prepared.LastRecordSha256, journal.Inspect(Operation).LastRecordSha256);
        Assert.Single(Directory.GetFiles(DirectoryFor(temp.Path), "event-*.json"));
    }

    [Fact]
    public void ClaimFormattingDriftInvalidatesAnEarlierSnapshotEvenWithTheSameRequest()
    {
        using TempDirectory temp = new(); using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        var journal = NativePrivateApplyJournal.Open(lease); var state = journal.Claim(Request());
        string path = Path.Combine(DirectoryFor(temp.Path), "claim.json");
        File.AppendAllText(path, " ");
        Assert.Throws<InvalidDataException>(() => journal.AppendPrepared(state.Snapshot));
        Assert.Empty(Directory.GetFiles(DirectoryFor(temp.Path), "event-*.json"));
    }

    [Fact]
    public void ProfileRenameAndReplacementCannotRedirectAnExistingLease()
    {
        if (OperatingSystem.IsWindows()) return; // Windows lock/rename proof is a separate native gate.
        using TempDirectory temp = new();
        string profile = Path.Combine(temp.Path, "profile"), moved = Path.Combine(temp.Path, "moved");
        using ProfileLease lease = ProfileLease.Acquire(profile);
        var journal = NativePrivateApplyJournal.Open(lease); journal.Claim(Request());
        Directory.Move(profile, moved); Directory.CreateDirectory(profile);
        Assert.Throws<InvalidDataException>(() => journal.Claim(Request(operation: Guid.NewGuid())));
        Assert.Throws<InvalidDataException>(() => journal.Inspect(Operation));
        Assert.Empty(Directory.GetFileSystemEntries(profile));
        Assert.True(File.Exists(Path.Combine(DirectoryFor(moved), "claim.json")));
    }

    [Fact]
    public void DirectoryIdentityIsStableForContentChangesButNotReplacement()
    {
        using TempDirectory temp = new();
        string directory = Path.Combine(temp.Path, "identity"), moved = Path.Combine(temp.Path, "old");
        Directory.CreateDirectory(directory);
        var identity = ProfileDirectoryIdentity.Capture(directory);
        File.WriteAllText(Path.Combine(directory, "synthetic"), "test");
        identity.EnsureMatches(directory);
        Directory.Move(directory, moved); Directory.CreateDirectory(directory);
        Assert.Throws<InvalidDataException>(() => identity.EnsureMatches(directory));
        Assert.Null(ProfileDirectoryIdentity.TryCapture(Path.Combine(moved, "synthetic")));
    }

    [Fact]
    public void OpenAndMissingInspectionNeverCreateAJournalOrRepairAClaim()
    {
        using TempDirectory temp = new(); using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        string[] before = Directory.GetFileSystemEntries(temp.Path).Order().ToArray();
        var journal = NativePrivateApplyJournal.Open(lease);
        Assert.Empty(journal.Inventory());
        Assert.NotNull(Record.Exception(() => journal.Inspect(Operation)));
        Assert.False(Directory.Exists(Root(temp.Path)));
        Assert.Equal(before, Directory.GetFileSystemEntries(temp.Path).Order().ToArray());
    }

    [Fact]
    public void DamagedHistoricalReceiptDoesNotFallBackToFreshClaim()
    {
        using TempDirectory temp = new(); using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        var journal = NativePrivateApplyJournal.Open(lease); var request = Request(); Complete(journal, request);
        string path = Path.Combine(DirectoryFor(temp.Path), "receipt.json");
        byte[] damaged = Encoding.UTF8.GetBytes("{\"schema\":false}");
        File.WriteAllBytes(path, damaged);
        Assert.Throws<InvalidDataException>(() => journal.Claim(request));
        Assert.Throws<InvalidDataException>(() => journal.Replay(Operation));
        Assert.Equal(damaged, File.ReadAllBytes(path));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void RecoveryHasABoundedRefinementSlotAndFinalReceiptDoesNotNeedAnotherEvent(bool finish)
    {
        using TempDirectory temp = new(); using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        var journal = NativePrivateApplyJournal.Open(lease); var request = Request(1);
        var state = journal.AppendApplying(journal.AppendPrepared(journal.Claim(request).Snapshot).Snapshot);
        state = journal.AppendObservation(state.Snapshot, NativePrivateApplyOperationObservation.None,
            [NativePrivateApplyStepObservation.IntentDurable]);
        state = journal.AppendObservation(state.Snapshot, NativePrivateApplyOperationObservation.None,
            [NativePrivateApplyStepObservation.Unknown]);
        NativePrivateApplyTerminalReceipt Evidence(NativePrivateApplyOperationPhase phase, NativePrivateApplyStepPhase step)
            => new(Operation, request.RequestSha256, request.Selection, phase,
                [new(NativePrivateApplyStoreKind.ThreadFavorites, step)]);
        while (state.EventCount < NativePrivateApplyJournal.MaximumEvents - 1)
        {
            var phase = state.State.Steps[0].Phase == NativePrivateApplyStepPhase.Unknown
                ? NativePrivateApplyStepPhase.Drifted : NativePrivateApplyStepPhase.Unknown;
            state = journal.AppendReceiptRecord(state.Snapshot, Evidence(NativePrivateApplyOperationPhase.RecoveryRequired, phase));
        }
        // No-progress polls never consume another durable record.
        var unchanged = Evidence(NativePrivateApplyOperationPhase.RecoveryRequired, state.State.Steps[0].Phase);
        Assert.Throws<InvalidDataException>(() => journal.AppendReceiptRecord(state.Snapshot, unchanged));
        NativePrivateApplyTerminalReceipt last = finish
            ? Evidence(NativePrivateApplyOperationPhase.FailedBeforeApply, NativePrivateApplyStepPhase.NotStartedProven)
            : Evidence(NativePrivateApplyOperationPhase.RecoveryRequired,
                state.State.Steps[0].Phase == NativePrivateApplyStepPhase.Unknown
                    ? NativePrivateApplyStepPhase.Drifted : NativePrivateApplyStepPhase.Unknown);
        state = journal.AppendReceiptRecord(state.Snapshot, last);
        Assert.Equal(NativePrivateApplyJournal.MaximumEvents, state.EventCount);
        var beyond = Evidence(NativePrivateApplyOperationPhase.RecoveryRequired, NativePrivateApplyStepPhase.Unreadable);
        Assert.Throws<InvalidDataException>(() => journal.AppendReceiptRecord(state.Snapshot, beyond));
        Assert.Equal(NativePrivateApplyJournal.MaximumEvents, Directory.GetFiles(DirectoryFor(temp.Path), "event-*.json").Length);
        if (finish)
        {
            var completed = journal.AppendReceipt(state.Snapshot, last);
            Assert.True(completed.HasTerminalReceipt);
            Assert.Equal(NativePrivateApplyOperationPhase.FailedBeforeApply, completed.State.Phase);
        }
        else Assert.False(File.Exists(Path.Combine(DirectoryFor(temp.Path), "receipt.json")));
    }

    [Fact]
    public void AdmissionCapacityCannotBlockAnExistingHistoricalReceipt()
    {
        using TempDirectory temp = new(); using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        var journal = NativePrivateApplyJournal.Open(lease); var request = Request(); Complete(journal, request);
        for (int i = 1; i < NativePrivateApplyJournal.MaximumOperations; i++)
            Directory.CreateDirectory(DirectoryFor(temp.Path, Guid.NewGuid()));
        Assert.True(journal.Claim(request).HasTerminalReceipt);
        Guid unadmitted = Guid.NewGuid();
        Assert.Throws<InvalidDataException>(() => journal.Claim(Request(operation: unadmitted)));
        Assert.False(Directory.Exists(DirectoryFor(temp.Path, unadmitted)));
        Assert.Equal(NativePrivateApplyJournal.MaximumOperations, Directory.GetDirectories(Root(temp.Path)).Length);
    }

    private static NativePrivateApplyTerminalReceipt StepReceipt(NativePrivateApplyJournalInspection state,
        NativePrivateApplyOperationPhase phase, params NativePrivateApplyStepPhase[] steps)
        => new(state.OperationId, state.RequestSha256, state.Selection, phase,
            state.State.Steps.Select((step, i) => new NativePrivateApplyRecoveryStep(step.StoreKind, steps[i])).ToImmutableArray());

    [Theory]
    [InlineData(0, false)] [InlineData(1, false)] [InlineData(2, false)]
    [InlineData(0, true)] [InlineData(1, true)] [InlineData(2, true)]
    public void UnstartedUncertaintyCannotInventPublication(int uncertainty, bool receiptRecord)
    {
        using TempDirectory temp = new(); using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        var journal = NativePrivateApplyJournal.Open(lease);
        var state = journal.AppendApplying(journal.AppendPrepared(journal.Claim(Request(1)).Snapshot).Snapshot);
        NativePrivateApplyStepPhase phase = UncertaintyPhase(uncertainty);
        state = receiptRecord
            ? journal.AppendReceiptRecord(state.Snapshot, StepReceipt(state, NativePrivateApplyOperationPhase.RecoveryRequired, phase))
            : journal.AppendObservation(state.Snapshot, NativePrivateApplyOperationObservation.None, [UncertaintyObservation(uncertainty)]);
        int count = state.EventCount;
        Assert.Throws<InvalidDataException>(() => journal.AppendReceiptRecord(state.Snapshot,
            StepReceipt(state, NativePrivateApplyOperationPhase.Completed, NativePrivateApplyStepPhase.Recorded)));
        Assert.Throws<InvalidDataException>(() => journal.AppendReceiptRecord(state.Snapshot,
            StepReceipt(state, NativePrivateApplyOperationPhase.RecoveryRequired, NativePrivateApplyStepPhase.PublishedVerified)));
        Assert.Throws<InvalidDataException>(() => journal.AppendReceiptRecord(state.Snapshot,
            StepReceipt(state, NativePrivateApplyOperationPhase.Completed, NativePrivateApplyStepPhase.NoChange)));
        Assert.Equal(count, Directory.GetFiles(DirectoryFor(temp.Path), "event-*.json").Length);
        Assert.False(File.Exists(Path.Combine(DirectoryFor(temp.Path), "receipt.json")));
    }

    [Theory]
    [InlineData(1, 0)] [InlineData(1, 1)] [InlineData(1, 2)]
    [InlineData(2, 0)] [InlineData(2, 1)] [InlineData(2, 2)]
    [InlineData(4, 0)] [InlineData(4, 1)] [InlineData(4, 2)]
    public void ActualStoreSpecificIntentSurvivesUncertaintyAndRestart(int bits, int uncertainty)
    {
        using TempDirectory temp = new();
        using (ProfileLease lease = ProfileLease.Acquire(temp.Path))
        {
            var journal = NativePrivateApplyJournal.Open(lease);
            var state = journal.AppendApplying(journal.AppendPrepared(journal.Claim(Request(bits)).Snapshot).Snapshot);
            state = journal.AppendObservation(state.Snapshot, NativePrivateApplyOperationObservation.None,
                [NativePrivateApplyStepObservation.IntentDurable]);
            journal.AppendObservation(state.Snapshot, NativePrivateApplyOperationObservation.None, [UncertaintyObservation(uncertainty)]);
        }
        using ProfileLease reopened = ProfileLease.Acquire(temp.Path);
        var restored = NativePrivateApplyJournal.Open(reopened);
        var recovery = restored.Replay(Operation);
        Assert.Equal(UncertaintyPhase(uncertainty), recovery.State.Steps[0].Phase);
        var receipt = StepReceipt(recovery, NativePrivateApplyOperationPhase.Completed, NativePrivateApplyStepPhase.Recorded);
        recovery = restored.AppendReceiptRecord(recovery.Snapshot, receipt);
        Assert.True(restored.AppendReceipt(recovery.Snapshot, receipt).HasTerminalReceipt);
    }

    [Fact]
    public void APreviousStoresIntentDoesNotAuthorizeAnUnstartedSelectedStore()
    {
        using TempDirectory temp = new(); using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        var journal = NativePrivateApplyJournal.Open(lease);
        var state = journal.AppendApplying(journal.AppendPrepared(journal.Claim(Request(3)).Snapshot).Snapshot);
        foreach (var observation in new[] { NativePrivateApplyStepObservation.IntentDurable,
            NativePrivateApplyStepObservation.ExactAfterWithMatchingAtomicMarker, NativePrivateApplyStepObservation.RecordedReceiptVerified })
            state = journal.AppendObservation(state.Snapshot, NativePrivateApplyOperationObservation.None,
                [observation, NativePrivateApplyStepObservation.NotObserved]);
        state = journal.AppendObservation(state.Snapshot, NativePrivateApplyOperationObservation.None,
            [NativePrivateApplyStepObservation.NotObserved, NativePrivateApplyStepObservation.Unknown]);
        Assert.Throws<InvalidDataException>(() => journal.AppendReceiptRecord(state.Snapshot,
            StepReceipt(state, NativePrivateApplyOperationPhase.Completed,
                NativePrivateApplyStepPhase.Recorded, NativePrivateApplyStepPhase.Recorded)));
        Assert.Equal(NativePrivateApplyStepPhase.Recorded, journal.Replay(Operation).State.Steps[0].Phase);
        Assert.False(File.Exists(Path.Combine(DirectoryFor(temp.Path), "receipt.json")));
    }

    [Fact]
    public void ProvenUnstartedCannotBeWeakenedToLaunderAnOldIntent()
    {
        using TempDirectory temp = new(); using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        var journal = NativePrivateApplyJournal.Open(lease);
        var state = journal.AppendApplying(journal.AppendPrepared(journal.Claim(Request(1)).Snapshot).Snapshot);
        state = journal.AppendObservation(state.Snapshot, NativePrivateApplyOperationObservation.None,
            [NativePrivateApplyStepObservation.IntentDurable]);
        state = journal.AppendObservation(state.Snapshot, NativePrivateApplyOperationObservation.None,
            [NativePrivateApplyStepObservation.ExactBeforeWithContinuousFence]);
        Assert.Throws<InvalidDataException>(() => journal.AppendReceiptRecord(state.Snapshot,
            StepReceipt(state, NativePrivateApplyOperationPhase.RecoveryRequired, NativePrivateApplyStepPhase.Unknown)));
        Assert.Equal(NativePrivateApplyStepPhase.NotStartedProven, journal.Replay(Operation).State.Steps[0].Phase);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public void ReopeningRejectsAHashLinkedRefinementThatNeverHadIntent(int uncertainty)
    {
        using TempDirectory temp = new();
        using (ProfileLease lease = ProfileLease.Acquire(temp.Path))
        {
            var journal = NativePrivateApplyJournal.Open(lease);
            var state = journal.AppendApplying(journal.AppendPrepared(journal.Claim(Request(1)).Snapshot).Snapshot);
            state = journal.AppendObservation(state.Snapshot, NativePrivateApplyOperationObservation.None,
                [UncertaintyObservation(uncertainty)]);
            // Deliberately bypass the API to emulate a formerly accepted invalid
            // history. All names/sequence/hash links are otherwise correct.
            var invalid = new JsonObject
            {
                ["schema"] = 1, ["operationId"] = Operation.ToString("N"), ["requestSha256"] = state.RequestSha256,
                ["sequence"] = state.EventCount + 1, ["previousRecordSha256"] = state.LastRecordSha256,
                ["operationObservation"] = (int)NativePrivateApplyOperationObservation.None,
                ["steps"] = new JsonArray(),
                ["receipt"] = new JsonObject
                {
                    ["phase"] = (int)NativePrivateApplyOperationPhase.Completed,
                    ["selection"] = new JsonObject { ["threadFavorites"] = true, ["mediaFavorites"] = false, ["postBookmarksAndNotes"] = false },
                    ["steps"] = new JsonArray(new JsonObject { ["storeKind"] = (int)NativePrivateApplyStoreKind.ThreadFavorites,
                        ["phase"] = (int)NativePrivateApplyStepPhase.Recorded })
                }
            };
            File.WriteAllText(Path.Combine(DirectoryFor(temp.Path), $"event-{state.EventCount + 1:0000}.json"), invalid.ToJsonString());
        }
        using ProfileLease reopened = ProfileLease.Acquire(temp.Path);
        Assert.Throws<InvalidDataException>(() => NativePrivateApplyJournal.Open(reopened).Replay(Operation));
        Assert.Equal(4, Directory.GetFiles(DirectoryFor(temp.Path), "event-*.json").Length);
        Assert.False(File.Exists(Path.Combine(DirectoryFor(temp.Path), "receipt.json")));
    }

    private static NativePrivateApplyStepPhase UncertaintyPhase(int value) => value switch
    {
        0 => NativePrivateApplyStepPhase.Unknown, 1 => NativePrivateApplyStepPhase.Drifted,
        _ => NativePrivateApplyStepPhase.Unreadable
    };
    private static NativePrivateApplyStepObservation UncertaintyObservation(int value) => value switch
    {
        0 => NativePrivateApplyStepObservation.Unknown, 1 => NativePrivateApplyStepObservation.Drifted,
        _ => NativePrivateApplyStepObservation.Unreadable
    };

}
