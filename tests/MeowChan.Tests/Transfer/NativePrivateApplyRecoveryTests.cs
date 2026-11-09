using System.Collections.Immutable;
using System.IO;
using System.Reflection;
using MeowChan.Transfer;
using Operation = MeowChan.Transfer.NativePrivateApplyOperationPhase;
using OperationObservation = MeowChan.Transfer.NativePrivateApplyOperationObservation;
using Phase = MeowChan.Transfer.NativePrivateApplyStepPhase;
using Observation = MeowChan.Transfer.NativePrivateApplyStepObservation;
using Store = MeowChan.Transfer.NativePrivateApplyStoreKind;
using State = MeowChan.Transfer.NativePrivateApplyRecoveryState;
using Step = MeowChan.Transfer.NativePrivateApplyRecoveryStep;
using Evidence = MeowChan.Transfer.NativePrivateApplyRecoveryEvidence;
using StepEvidence = MeowChan.Transfer.NativePrivateApplyStepEvidence;
using Receipt = MeowChan.Transfer.NativePrivateApplyTerminalReceipt;

namespace MeowChan.Tests.Transfer;

// Synthetic, in-memory contract tests only. No stores, files, profiles, commands or
// executable plans are constructed. Negative protocol tests deliberately must not
// be weakened to accept an implementation that skips intent or ordered admission.
// The bounded synchronous Reduce API has no caller cancellation token: inspection
// evidence and historical receipts must not become a fictional rollback on cancel.
public sealed class NativePrivateApplyRecoveryTests
{
    private static readonly Guid OperationId = new("11111111-2222-4333-8444-555555555555");
    private static readonly Guid OtherOperationId = new("aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee");
    private static readonly string RequestHash = new('a', 64);
    private static readonly string OtherHash = new('b', 64);
    private static readonly NativePrivateMetadataSelection All = new(true, true, true);
    private static readonly NativePrivateMetadataSelection ThreadsOnly = new(true, false, false);

    private static Step S(Store kind, Phase phase = Phase.Planned) => new(kind, phase);
    private static StepEvidence E(Store kind, Observation observation = Observation.NotObserved) => new(kind, observation);
    private static ImmutableArray<Step> Single(Phase phase = Phase.Planned) => [S(Store.ThreadFavorites, phase)];
    private static ImmutableArray<Step> Ordered(params Phase[] phases)
        => phases.Select((phase, index) => S((Store)index, phase)).ToImmutableArray();
    private static State Claimed() => State.CreateClaimed(OperationId, RequestHash, ThreadsOnly, Single());
    private static State Applying(Phase phase = Phase.Planned)
        => State.FromValidated(OperationId, RequestHash, ThreadsOnly, Operation.Applying, Single(phase));
    private static Evidence Observe(OperationObservation operation, params StepEvidence[] steps)
        => new(OperationId, RequestHash, operation, steps.ToImmutableArray());
    private static State Move(State state, OperationObservation operation = OperationObservation.None,
        Observation step = Observation.NotObserved)
        => NativePrivateApplyRecovery.Reduce(state, Observe(operation, E(Store.ThreadFavorites, step)));
    private static Receipt CompletedReceipt()
        => new(OperationId, RequestHash, ThreadsOnly, Operation.Completed, Single(Phase.Recorded));

    [Fact]
    public void ValidProtocolClassifiesIntentPublicationAndRecordedEvidenceWithoutInventingCompletion()
    {
        State claimed = Claimed();
        State prepared = Move(claimed, OperationObservation.Prepared);
        State applying = Move(prepared, OperationObservation.Applying);
        State intent = Move(applying, step: Observation.IntentDurable);
        State published = Move(intent, step: Observation.ExactAfterWithMatchingAtomicMarker);
        State recorded = Move(published, step: Observation.RecordedReceiptVerified);

        Assert.Equal(Operation.Claimed, claimed.Phase);
        Assert.Equal(Operation.Prepared, prepared.Phase);
        Assert.Equal(Operation.Applying, applying.Phase);
        Assert.Equal(Phase.Planned, claimed.Steps[0].Phase);
        Assert.Equal(Phase.Planned, prepared.Steps[0].Phase);
        Assert.Equal(Phase.IntentDurable, intent.Steps[0].Phase);
        Assert.Equal(Phase.PublishedVerified, published.Steps[0].Phase);
        Assert.Equal(Phase.Recorded, recorded.Steps[0].Phase);
        // A recorded step is not a persisted final operation receipt.
        Assert.Equal(Operation.Applying, recorded.Phase);
        State completed = NativePrivateApplyRecovery.Reduce(recorded,
            new Evidence(OperationId, RequestHash, OperationObservation.None, [], CompletedReceipt()));
        Assert.Equal(Operation.Completed, completed.Phase);
        Assert.Equal(Phase.Recorded, completed.Steps[0].Phase);
    }

    [Theory]
    [InlineData("Claimed", "Applying")]
    [InlineData("Applying", "Prepared")]
    [InlineData("Applying", "CancelledBeforeApply")]
    [InlineData("Applying", "FailedBeforeApply")]
    public void OperationProtocolRejectsSkippedOrBackwardTransitions(string from, string observation)
    {
        State state = State.FromValidated(OperationId, RequestHash, ThreadsOnly, Enum.Parse<Operation>(from), Single());
        Assert.Throws<InvalidDataException>(() => Move(state, Enum.Parse<OperationObservation>(observation)));
        Assert.Equal(Enum.Parse<Operation>(from), state.Phase);
        Assert.Equal(Phase.Planned, state.Steps[0].Phase);
    }

    [Theory]
    [InlineData("Claimed", "Prepared")]
    [InlineData("Prepared", "Prepared")]
    [InlineData("Prepared", "Applying")]
    [InlineData("Applying", "Applying")]
    public void LegalOperationTransitionsPreserveIdentitySelectionAndUnobservedSteps(string from, string observation)
    {
        State state = State.FromValidated(OperationId, RequestHash, ThreadsOnly, Enum.Parse<Operation>(from), Single());
        State next = Move(state, Enum.Parse<OperationObservation>(observation));
        Assert.Equal(Enum.Parse<Operation>(observation), next.Phase);
        Assert.Equal(OperationId, next.OperationGuid);
        Assert.Equal(RequestHash, next.RequestSha256);
        Assert.Equal(ThreadsOnly, next.Selection);
        Assert.Same(state.Steps[0], next.Steps[0]);
    }

    [Theory]
    [InlineData("Claimed", "CancelledBeforeApply")]
    [InlineData("Claimed", "FailedBeforeApply")]
    [InlineData("Prepared", "CancelledBeforeApply")]
    [InlineData("Prepared", "FailedBeforeApply")]
    public void BeforeApplyCancellationAndFailureAreExplicitNoPublicationObservations(string from, string observation)
    {
        State state = State.FromValidated(OperationId, RequestHash, ThreadsOnly, Enum.Parse<Operation>(from), Single());
        State next = Move(state, Enum.Parse<OperationObservation>(observation));
        Assert.Equal(Enum.Parse<Operation>(observation), next.Phase);
        Assert.Equal(Phase.Planned, next.Steps[0].Phase);
        Assert.Equal(Enum.Parse<Operation>(from), state.Phase);
    }

    [Theory]
    [InlineData("Claimed")]
    [InlineData("Prepared")]
    public void IntentCannotBeAdmittedBeforeApplyingPhase(string operation)
    {
        State state = State.FromValidated(OperationId, RequestHash, ThreadsOnly, Enum.Parse<Operation>(operation), Single());
        Assert.Throws<InvalidDataException>(() => Move(state, step: Observation.IntentDurable));
        Assert.Equal(Phase.Planned, state.Steps[0].Phase);
    }

    public static IEnumerable<object[]> StepTransitions()
    {
        // Explicit expected edges, not reflection over the implementation switch.
        var accepted = new Dictionary<(Phase, Observation), Phase>
        {
            [(Phase.Planned, Observation.IntentDurable)] = Phase.IntentDurable,
            [(Phase.IntentDurable, Observation.IntentDurable)] = Phase.IntentDurable,
            [(Phase.Planned, Observation.ExactBeforeWithContinuousFence)] = Phase.NotStartedProven,
            [(Phase.IntentDurable, Observation.ExactBeforeWithContinuousFence)] = Phase.NotStartedProven,
            [(Phase.Planned, Observation.ExactAfterWithoutMarker)] = Phase.Unknown,
            [(Phase.IntentDurable, Observation.ExactAfterWithoutMarker)] = Phase.Unknown,
            [(Phase.IntentDurable, Observation.ExactAfterWithMatchingAtomicMarker)] = Phase.PublishedVerified,
            [(Phase.PublishedVerified, Observation.ExactAfterWithMatchingAtomicMarker)] = Phase.PublishedVerified,
            [(Phase.PublishedVerified, Observation.RecordedReceiptVerified)] = Phase.Recorded,
            [(Phase.Recorded, Observation.RecordedReceiptVerified)] = Phase.Recorded,
            [(Phase.Planned, Observation.NoChangeVerified)] = Phase.NoChange,
            [(Phase.NoChange, Observation.NoChangeVerified)] = Phase.NoChange,
            [(Phase.Planned, Observation.ConflictOnlyVerified)] = Phase.ConflictOnly,
            [(Phase.ConflictOnly, Observation.ConflictOnlyVerified)] = Phase.ConflictOnly,
            [(Phase.Planned, Observation.MissingTargetVerified)] = Phase.MissingTarget,
            [(Phase.MissingTarget, Observation.MissingTargetVerified)] = Phase.MissingTarget,
        };
        Phase[] starts = [Phase.Planned, Phase.IntentDurable, Phase.PublishedVerified, Phase.Recorded,
            Phase.NoChange, Phase.ConflictOnly, Phase.MissingTarget, Phase.NotStartedProven];
        foreach (Phase phase in starts)
        {
            foreach (Observation observation in Enum.GetValues<Observation>())
            {
                Phase? expected = observation == Observation.NotObserved ? phase : null;
                if (accepted.TryGetValue((phase, observation), out Phase edge)) expected = edge;
                if (phase is Phase.Planned or Phase.IntentDurable)
                {
                    if (observation == Observation.Drifted) expected = Phase.Drifted;
                    if (observation == Observation.Unreadable) expected = Phase.Unreadable;
                    if (observation == Observation.Unknown) expected = Phase.Unknown;
                }
                yield return [phase.ToString(), observation.ToString(), expected?.ToString()!];
            }
        }
    }

    [Theory]
    [MemberData(nameof(StepTransitions))]
    public void StepTransitionMatrixRejectsProtocolSkipsAndPreservesInput(string from, string observation, string? expected)
    {
        State state = Applying(Enum.Parse<Phase>(from));
        Observation input = Enum.Parse<Observation>(observation);
        if (expected is null)
            Assert.Throws<InvalidDataException>(() => Move(state, step: input));
        else
        {
            State next = Move(state, step: input);
            Phase phase = Enum.Parse<Phase>(expected);
            Assert.Equal(phase, next.Steps[0].Phase);
            Operation expectedOperation = phase == Phase.Drifted && from == Phase.Planned.ToString()
                ? Operation.NeedsReview
                : phase is Phase.Unknown or Phase.Drifted or Phase.Unreadable
                    ? Operation.RecoveryRequired : Operation.Applying;
            Assert.Equal(expectedOperation, next.Phase);
        }
        Assert.Equal(Enum.Parse<Phase>(from), state.Steps[0].Phase);
        Assert.Equal(Operation.Applying, state.Phase);
    }

    [Theory]
    [InlineData("Unknown")]
    [InlineData("ExactAfterWithoutMarker")]
    public void MatchingAfterBytesOrUnknownNeverBecomeCommitted(string observation)
    {
        State state = Applying(Phase.IntentDurable);
        State next = Move(state, step: Enum.Parse<Observation>(observation));
        Assert.Equal(Operation.RecoveryRequired, next.Phase);
        Assert.Equal(Phase.Unknown, next.Steps[0].Phase);
        Assert.NotEqual(Operation.Completed, next.Phase);
        Assert.NotEqual(Phase.PublishedVerified, next.Steps[0].Phase);
        // A second observation is inspection only, not an automatic retry.
        Assert.Same(next, Move(next, step: Observation.ExactAfterWithMatchingAtomicMarker));
    }

    [Fact]
    public void StaleTargetBeforeIntentIsAnExplicitNeedsReviewOutcome()
    {
        State state = Move(Applying(), step: Observation.Drifted);

        Assert.Equal(Operation.NeedsReview, state.Phase);
        Assert.Equal(Phase.Drifted, state.Steps[0].Phase);

        Receipt receipt = new(OperationId, RequestHash, ThreadsOnly,
            Operation.NeedsReview, Single(Phase.Drifted));
        State finalized = NativePrivateApplyRecovery.Reduce(state,
            new Evidence(OperationId, RequestHash, OperationObservation.None, [], receipt));

        Assert.Equal(Operation.NeedsReview, finalized.Phase);
        Assert.Equal(Phase.Drifted, finalized.Steps[0].Phase);
        Assert.Same(finalized, NativePrivateApplyRecovery.Reduce(finalized,
            Observe(OperationObservation.Applying, E(Store.ThreadFavorites, Observation.IntentDurable))));
    }

    [Fact]
    public void StaleTargetAfterIntentRemainsRecoveryRequired()
    {
        State intent = Move(Applying(), step: Observation.IntentDurable);
        State stale = Move(intent, step: Observation.Drifted);

        Assert.Equal(Operation.RecoveryRequired, stale.Phase);
        Assert.Equal(Phase.Drifted, stale.Steps[0].Phase);
    }

    [Fact]
    public void ExactBeforeRequiresContinuousFenceAndDoesNotEmitAResume()
    {
        State state = Applying(Phase.IntentDurable);
        State next = Move(state, step: Observation.ExactBeforeWithContinuousFence);
        Assert.Equal(Phase.NotStartedProven, next.Steps[0].Phase);
        Assert.Equal(Operation.Applying, next.Phase);
        Assert.Throws<InvalidDataException>(() => Move(next, step: Observation.IntentDurable));
        Assert.Equal(Phase.IntentDurable, state.Steps[0].Phase);
    }

    [Fact]
    public void ConflictOnlyAndMissingTargetAreSettledNoWriteOutcomesAndPreserveStoreOrder()
    {
        State state = State.FromValidated(OperationId, RequestHash, All, Operation.Applying,
            Ordered(Phase.Planned, Phase.Planned, Phase.Planned));
        State conflict = NativePrivateApplyRecovery.Reduce(state,
            Observe(OperationObservation.None,
                E(Store.ThreadFavorites, Observation.ConflictOnlyVerified),
                E(Store.MediaFavorites), E(Store.PostBookmarksAndNotes)));
        Assert.Equal(Operation.Applying, conflict.Phase);
        Assert.Equal(Phase.ConflictOnly, conflict.Steps[0].Phase);

        State missing = NativePrivateApplyRecovery.Reduce(conflict,
            Observe(OperationObservation.None,
                E(Store.ThreadFavorites),
                E(Store.MediaFavorites, Observation.MissingTargetVerified),
                E(Store.PostBookmarksAndNotes)));
        Assert.Equal(Operation.Applying, missing.Phase);
        Assert.Equal(Phase.ConflictOnly, missing.Steps[0].Phase);
        Assert.Equal(Phase.MissingTarget, missing.Steps[1].Phase);

        State noChange = NativePrivateApplyRecovery.Reduce(missing,
            Observe(OperationObservation.None,
                E(Store.ThreadFavorites), E(Store.MediaFavorites),
                E(Store.PostBookmarksAndNotes, Observation.NoChangeVerified)));
        Receipt receipt = new(OperationId, RequestHash, All, Operation.Completed,
            Ordered(Phase.ConflictOnly, Phase.MissingTarget, Phase.NoChange));
        State completed = NativePrivateApplyRecovery.Reduce(noChange,
            new Evidence(OperationId, RequestHash, OperationObservation.None, [], receipt));

        Assert.Equal(Operation.Completed, completed.Phase);
        Assert.Equal(new[] { Phase.ConflictOnly, Phase.MissingTarget, Phase.NoChange },
            completed.Steps.Select(step => step.Phase));
    }

    [Theory]
    [InlineData("ConflictOnly")]
    [InlineData("MissingTarget")]
    public void CompletedReceiptAcceptsSettledNonMutatingStepOutcomes(string phase)
    {
        Receipt receipt = new(OperationId, RequestHash, ThreadsOnly, Operation.Completed,
            Single(Enum.Parse<Phase>(phase)));
        Assert.Equal(Operation.Completed, receipt.ToState().Phase);
        Assert.Equal(Enum.Parse<Phase>(phase), receipt.Steps[0].Phase);
    }

    [Theory]
    [InlineData("Drifted")]
    [InlineData("Unreadable")]
    public void KnownFailureAfterRecordedStoreRemainsRecoveryRequiredUntilReceipt(string failure)
    {
        State state = State.FromValidated(OperationId, RequestHash, All, Operation.Applying,
            Ordered(Phase.Recorded, Phase.IntentDurable, Phase.Planned));
        State partial = NativePrivateApplyRecovery.Reduce(state,
            Observe(OperationObservation.None, E(Store.ThreadFavorites), E(Store.MediaFavorites, Enum.Parse<Observation>(failure)), E(Store.PostBookmarksAndNotes)));
        Assert.Equal(Operation.RecoveryRequired, partial.Phase);
        Assert.Equal(Phase.Recorded, partial.Steps[0].Phase);
        Assert.Equal(Enum.Parse<Phase>(failure), partial.Steps[1].Phase);
        Assert.Equal(Phase.Planned, partial.Steps[2].Phase);
        State unknown = NativePrivateApplyRecovery.Reduce(state,
            Observe(OperationObservation.None, E(Store.ThreadFavorites), E(Store.MediaFavorites, Observation.Unknown), E(Store.PostBookmarksAndNotes)));
        Assert.Equal(Operation.RecoveryRequired, unknown.Phase);
        Assert.Equal(Phase.Recorded, unknown.Steps[0].Phase);
    }

    [Theory]
    [InlineData("Drifted")]
    [InlineData("Unreadable")]
    public void PossiblePublicationFailureDoesNotBecomeFinalPartial(string failure)
    {
        State state = State.FromValidated(OperationId, RequestHash, All, Operation.Applying,
            Ordered(Phase.Recorded, Phase.IntentDurable, Phase.Planned));
        State result = NativePrivateApplyRecovery.Reduce(state,
            Observe(OperationObservation.None, E(Store.ThreadFavorites),
                E(Store.MediaFavorites, Enum.Parse<Observation>(failure)),
                E(Store.PostBookmarksAndNotes)));
        Assert.Equal(Operation.RecoveryRequired, result.Phase);
        Assert.Equal(Enum.Parse<Phase>(failure), result.Steps[1].Phase);
        State completed = NativePrivateApplyRecovery.Reduce(result,
            new Evidence(OperationId, RequestHash, OperationObservation.None, [],
                new Receipt(OperationId, RequestHash, All, Operation.Completed,
                    Ordered(Phase.Recorded, Phase.Recorded, Phase.NoChange))));
        Assert.Equal(Operation.Completed, completed.Phase);
        Assert.Equal(Phase.Recorded, completed.Steps[0].Phase);
        Assert.Equal(Phase.Recorded, completed.Steps[1].Phase);
        Assert.Equal(Phase.NoChange, completed.Steps[2].Phase);
        Assert.Equal(Operation.RecoveryRequired, result.Phase);
    }

    [Theory]
    [InlineData("PublishedVerified")]
    [InlineData("Recorded")]
    public void StoppedRemainderNeedsFinalReceiptEvenWithKnownPublication(string prefix)
    {
        NativePrivateMetadataSelection selection = new(true, true);
        State state = State.FromValidated(OperationId, RequestHash, selection, Operation.Applying,
            [new(Store.ThreadFavorites, Enum.Parse<Phase>(prefix)), new(Store.MediaFavorites, Phase.Planned)]);
        State result = NativePrivateApplyRecovery.Reduce(state,
            Observe(OperationObservation.None, E(Store.ThreadFavorites),
                E(Store.MediaFavorites, Observation.ExactBeforeWithContinuousFence)));
        Assert.Equal(Operation.RecoveryRequired, result.Phase);
        Assert.Equal(Enum.Parse<Phase>(prefix), result.Steps[0].Phase);
        Assert.Equal(Phase.NotStartedProven, result.Steps[1].Phase);
        Receipt receipt = new(OperationId, RequestHash, selection, Operation.Partial,
            [new(Store.ThreadFavorites, Phase.Recorded), new(Store.MediaFavorites, Phase.NotStartedProven)]);
        State final = NativePrivateApplyRecovery.Reduce(result,
            new Evidence(OperationId, RequestHash, [], receipt));
        Assert.Equal(Operation.Partial, final.Phase);
        Assert.Equal(Phase.Recorded, final.Steps[0].Phase);
        Assert.Equal(Phase.NotStartedProven, final.Steps[1].Phase);
        Assert.Same(final, NativePrivateApplyRecovery.Reduce(final,
            Observe(OperationObservation.None, E(Store.ThreadFavorites), E(Store.MediaFavorites))));
    }

    public static IEnumerable<object[]> TerminalStates()
    {
        yield return ["Completed", new[] { "Recorded", "NoChange", "Recorded" }];
        yield return ["CancelledBeforeApply", new[] { "Planned", "Planned", "Planned" }];
        yield return ["FailedBeforeApply", new[] { "NotStartedProven", "Planned", "Planned" }];
        yield return ["Partial", new[] { "Recorded", "Drifted", "Planned" }];
        yield return ["RecoveryRequired", new[] { "Unknown", "Planned", "Planned" }];
    }

    [Theory]
    [MemberData(nameof(TerminalStates))]
    public void TerminalStateCannotBeRewrittenOrResumedByLaterTargetObservations(string phase, string[] stepPhases)
    {
        Receipt receipt = new(OperationId, RequestHash, All, Enum.Parse<Operation>(phase),
            Ordered(stepPhases.Select(Enum.Parse<Phase>).ToArray()));
        State state = receipt.ToState();
        foreach (OperationObservation observation in Enum.GetValues<OperationObservation>())
        {
            // Freshness / target inspection is irrelevant after the terminal result.
            State next = NativePrivateApplyRecovery.Reduce(state,
                Observe(observation, E(Store.ThreadFavorites, Observation.IntentDurable),
                    E(Store.MediaFavorites, Observation.Unknown), E(Store.PostBookmarksAndNotes, Observation.Unreadable)));
            Assert.Same(state, next);
        }
        Assert.Equal(Enum.Parse<Operation>(phase), state.Phase);
    }

    [Fact]
    public void HistoricalReceiptPrecedesMissingOrIncompatibleCurrentTargetEvidence()
    {
        State state = Claimed();
        Receipt receipt = CompletedReceipt();
        Evidence evidence = new(OperationId, RequestHash, OperationObservation.Applying, [], receipt);
        State next = NativePrivateApplyRecovery.Reduce(state, evidence);
        Assert.Equal(Operation.Completed, next.Phase);
        Assert.Equal(Phase.Recorded, next.Steps[0].Phase);
        Assert.Equal(Operation.Claimed, state.Phase);
        Assert.Same(receipt, evidence.TerminalReceipt);
        // Deliberately different currently inspected store; must never be re-applied.
        Evidence unavailable = new(OperationId, RequestHash, OperationObservation.Prepared,
            [E(Store.PostBookmarksAndNotes, Observation.Unreadable)], receipt);
        Assert.Equal(Operation.Completed, NativePrivateApplyRecovery.Reduce(state, unavailable).Phase);
    }

    [Fact]
    public void ExactReceiptCanResolvePreviouslyUnknownObservationWithoutReplayingMutation()
    {
        State unknown = Move(Applying(Phase.IntentDurable), step: Observation.Unknown);
        State resolved = NativePrivateApplyRecovery.Reduce(unknown,
            new Evidence(OperationId, RequestHash, OperationObservation.None, [], CompletedReceipt()));
        Assert.Equal(Operation.Completed, resolved.Phase);
        Assert.Equal(Phase.Recorded, resolved.Steps[0].Phase);
        Assert.Equal(Operation.RecoveryRequired, unknown.Phase);
        Assert.Equal(Phase.Unknown, unknown.Steps[0].Phase);
    }

    [Theory]
    [InlineData("operation")]
    [InlineData("hash")]
    [InlineData("selection")]
    public void ReceiptCannotAuthorizeReplayForDifferentIdentityOrReceivingSelection(string mismatch)
    {
        Receipt receipt = new(mismatch == "operation" ? OtherOperationId : OperationId,
            mismatch == "hash" ? OtherHash : RequestHash,
            mismatch == "selection" ? new NativePrivateMetadataSelection(false, true, false) : ThreadsOnly,
            Operation.Completed,
            [S(mismatch == "selection" ? Store.MediaFavorites : Store.ThreadFavorites, Phase.Recorded)]);
        Assert.Throws<InvalidDataException>(() => NativePrivateApplyRecovery.Reduce(Claimed(),
            new Evidence(OperationId, RequestHash, OperationObservation.None, [], receipt)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EvidenceIdentityMismatchIsRejectedEvenWithAnOtherwiseMatchingReceipt(bool wrongOperation)
    {
        Evidence evidence = new(wrongOperation ? OtherOperationId : OperationId,
            wrongOperation ? RequestHash : OtherHash, OperationObservation.None, [], CompletedReceipt());
        Assert.Throws<InvalidDataException>(() => NativePrivateApplyRecovery.Reduce(Claimed(), evidence));
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    [InlineData(4)] [InlineData(5)] [InlineData(6)] [InlineData(7)]
    public void EveryReceivingSubsetHasExactlyItsSelectedStoresInFixedOrder(int mask)
    {
        NativePrivateMetadataSelection selection = new((mask & 1) != 0, (mask & 2) != 0, (mask & 4) != 0);
        ImmutableArray<Step> steps = Enumerable.Range(0, 3).Where(i => (mask & (1 << i)) != 0)
            .Select(i => S((Store)i)).ToImmutableArray();
        State state = State.CreateClaimed(OperationId, RequestHash, selection, steps);
        Assert.InRange(state.Steps.Length, 0, 3);
        Assert.Equal(steps.Select(s => s.StoreKind), state.Steps.Select(s => s.StoreKind));
        Assert.All(state.Steps, s => Assert.Equal(Phase.Planned, s.Phase));
    }

    public static IEnumerable<object[]> InvalidStoreShapes()
    {
        yield return ["duplicate", new[] { 0, 0, 2 }];
        yield return ["reverse", new[] { 2, 1, 0 }];
        yield return ["swap", new[] { 0, 2, 1 }];
        yield return ["missing", new[] { 0, 2 }];
        yield return ["four", new[] { 0, 1, 2, 2 }];
    }

    [Theory]
    [MemberData(nameof(InvalidStoreShapes))]
    public void ClaimedStateRejectsDuplicateOutOfOrderMissingAndMoreThanThreeStores(string _, int[] kinds)
    {
        ImmutableArray<Step> steps = kinds.Select(i => S((Store)i)).ToImmutableArray();
        Assert.Throws<InvalidDataException>(() => State.CreateClaimed(OperationId, RequestHash, All, steps));
    }

    [Theory]
    [MemberData(nameof(InvalidStoreShapes))]
    public void InspectionRejectsDuplicateOutOfOrderMissingAndMoreThanThreeStores(string _, int[] kinds)
    {
        State state = State.CreateClaimed(OperationId, RequestHash, All, Ordered(Phase.Planned, Phase.Planned, Phase.Planned));
        Assert.Throws<InvalidDataException>(() => NativePrivateApplyRecovery.Reduce(state,
            Observe(OperationObservation.None, kinds.Select(i => E((Store)i)).ToArray())));
    }

    [Theory]
    [InlineData("IntentDurable")]
    [InlineData("PublishedVerified")]
    [InlineData("NoChange")]
    [InlineData("ConflictOnly")]
    [InlineData("MissingTarget")]
    public void NextStoreCannotStartBeforePreviousStoreResultWasRecorded(string earlierPhase)
    {
        State state = State.FromValidated(OperationId, RequestHash, All, Operation.Applying,
            Ordered(Enum.Parse<Phase>(earlierPhase), Phase.Planned, Phase.Planned));
        // Each settled no-write result is itself a durable explicit step result and may advance.
        if (earlierPhase is "NoChange" or "ConflictOnly" or "MissingTarget")
        {
            State next = NativePrivateApplyRecovery.Reduce(state,
                Observe(OperationObservation.None, E(Store.ThreadFavorites), E(Store.MediaFavorites, Observation.IntentDurable), E(Store.PostBookmarksAndNotes)));
            Assert.Equal(Phase.IntentDurable, next.Steps[1].Phase);
        }
        else
            Assert.Throws<InvalidDataException>(() => NativePrivateApplyRecovery.Reduce(state,
                Observe(OperationObservation.None, E(Store.ThreadFavorites), E(Store.MediaFavorites, Observation.IntentDurable), E(Store.PostBookmarksAndNotes))));
        Assert.Equal(Phase.Planned, state.Steps[1].Phase);
    }

    [Fact]
    public void RecordedEarlierStoreAllowsExactlyTheNextStoreToAdvance()
    {
        State state = State.FromValidated(OperationId, RequestHash, All, Operation.Applying,
            Ordered(Phase.Recorded, Phase.Planned, Phase.Planned));
        State next = NativePrivateApplyRecovery.Reduce(state,
            Observe(OperationObservation.None, E(Store.ThreadFavorites), E(Store.MediaFavorites, Observation.IntentDurable), E(Store.PostBookmarksAndNotes)));
        Assert.Equal(new[] { Phase.Recorded, Phase.IntentDurable, Phase.Planned }, next.Steps.Select(s => s.Phase));
        Assert.Throws<InvalidDataException>(() => NativePrivateApplyRecovery.Reduce(state,
            Observe(OperationObservation.None, E(Store.ThreadFavorites), E(Store.MediaFavorites), E(Store.PostBookmarksAndNotes, Observation.IntentDurable))));
    }

    [Theory]
    [InlineData("Planned")]
    [InlineData("IntentDurable")]
    [InlineData("PublishedVerified")]
    [InlineData("Unknown")]
    [InlineData("Drifted")]
    [InlineData("Unreadable")]
    public void CompletedReceiptRejectsUnrecordedOrUncertainSteps(string phase)
        => Assert.Throws<InvalidDataException>(() => new Receipt(OperationId, RequestHash, ThreadsOnly,
            Operation.Completed, Single(Enum.Parse<Phase>(phase))));

    [Theory]
    [InlineData("Claimed")]
    [InlineData("Prepared")]
    [InlineData("Applying")]
    public void ReceiptMustBeTerminal(string phase)
        => Assert.Throws<InvalidDataException>(() => new Receipt(OperationId, RequestHash, ThreadsOnly,
            Enum.Parse<Operation>(phase), Single()));

    [Theory]
    [InlineData("CancelledBeforeApply", "IntentDurable")]
    [InlineData("CancelledBeforeApply", "PublishedVerified")]
    [InlineData("CancelledBeforeApply", "Recorded")]
    [InlineData("CancelledBeforeApply", "Unknown")]
    [InlineData("FailedBeforeApply", "IntentDurable")]
    [InlineData("FailedBeforeApply", "PublishedVerified")]
    [InlineData("FailedBeforeApply", "Recorded")]
    [InlineData("FailedBeforeApply", "Unknown")]
    public void BeforeApplyReceiptMustNeverConcealPossibleOrKnownPublication(string operation, string phase)
        => Assert.Throws<InvalidDataException>(() => new Receipt(OperationId, RequestHash, ThreadsOnly,
            Enum.Parse<Operation>(operation), Single(Enum.Parse<Phase>(phase))));

    [Fact]
    public void SelectedEmptyCanCompleteOnlyWithExplicitTerminalReceiptAndNeverInventsStoreWork()
    {
        State state = State.CreateClaimed(OperationId, RequestHash, default, []);
        State prepared = NativePrivateApplyRecovery.Reduce(state, Observe(OperationObservation.Prepared));
        Assert.Empty(prepared.Steps);
        Assert.Equal(Operation.Prepared, prepared.Phase);
        Receipt receipt = new(OperationId, RequestHash, default, Operation.Completed, []);
        State completed = NativePrivateApplyRecovery.Reduce(prepared,
            new Evidence(OperationId, RequestHash, OperationObservation.None, [], receipt));
        Assert.Empty(completed.Steps);
        Assert.Equal(Operation.Completed, completed.Phase);
    }

    [Theory]
    [InlineData(-1)] [InlineData(999)]
    public void UnknownEnumValuesAreRejectedAtContractBoundaries(int invalid)
    {
        Assert.Throws<InvalidDataException>(() => S((Store)invalid));
        Assert.Throws<InvalidDataException>(() => E((Store)invalid));
        Assert.Throws<InvalidDataException>(() => State.CreateClaimed(OperationId, RequestHash, ThreadsOnly, Single((Phase)invalid)));
        Assert.Throws<InvalidDataException>(() => Move(Applying(), (OperationObservation)invalid));
        Assert.Throws<InvalidDataException>(() => Move(Applying(), step: (Observation)invalid));
        Assert.Throws<InvalidDataException>(() => new Receipt(OperationId, RequestHash, ThreadsOnly, (Operation)invalid, Single()));
    }

    [Theory]
    [InlineData("")]
    [InlineData("a")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("gggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggg")]
    public void InvalidRequestIdentityNeverBecomesAClaimOrReceipt(string hash)
    {
        Assert.Throws<InvalidDataException>(() => State.CreateClaimed(OperationId, hash, ThreadsOnly, Single()));
        Assert.Throws<InvalidDataException>(() => new Evidence(OperationId, hash, OperationObservation.None, []));
        Assert.Throws<InvalidDataException>(() => new Receipt(OperationId, hash, ThreadsOnly, Operation.Completed, Single(Phase.Recorded)));
    }

    [Fact]
    public void NullDefaultAndEmptyIdentityInputsFailClosed()
    {
        Assert.Throws<ArgumentNullException>(() => NativePrivateApplyRecovery.Reduce(null!, Observe(OperationObservation.None)));
        Assert.Throws<ArgumentNullException>(() => NativePrivateApplyRecovery.Reduce(Claimed(), null!));
        Assert.Throws<InvalidDataException>(() => State.CreateClaimed(Guid.Empty, RequestHash, ThreadsOnly, Single()));
        Assert.Throws<InvalidDataException>(() => State.CreateClaimed(OperationId, RequestHash, ThreadsOnly, default));
        Assert.Throws<InvalidDataException>(() => State.CreateClaimed(OperationId, RequestHash, ThreadsOnly, [null!]));
        Assert.Throws<InvalidDataException>(() => new Evidence(OperationId, RequestHash, OperationObservation.None, default));
        Assert.Throws<InvalidDataException>(() => new Evidence(OperationId, RequestHash, OperationObservation.None, [null!]));
    }

    [Fact]
    public void ReductionIsRepeatableAndDoesNotMutateAnySuppliedStateOrEvidence()
    {
        State state = Applying();
        Evidence evidence = Observe(OperationObservation.None, E(Store.ThreadFavorites, Observation.IntentDurable));
        Step originalStep = state.Steps[0];
        StepEvidence originalObservation = evidence.Steps[0];
        State first = NativePrivateApplyRecovery.Reduce(state, evidence);
        State second = NativePrivateApplyRecovery.Reduce(state, evidence);
        Assert.NotSame(state, first);
        Assert.Equal(first.Phase, second.Phase);
        Assert.Equal(first.Steps.Select(s => (s.StoreKind, s.Phase)), second.Steps.Select(s => (s.StoreKind, s.Phase)));
        Assert.Same(originalStep, state.Steps[0]);
        Assert.Same(originalObservation, evidence.Steps[0]);
        Assert.Equal(Phase.Planned, originalStep.Phase);
        Assert.Equal(Observation.IntentDurable, originalObservation.Observation);
        Assert.Null(evidence.TerminalReceipt);
    }


    [Theory]
    [InlineData("Completed", "CancelledBeforeApply")]
    [InlineData("Completed", "FailedBeforeApply")]
    [InlineData("CancelledBeforeApply", "Completed")]
    [InlineData("FailedBeforeApply", "Completed")]
    [InlineData("Partial", "Completed")]
    public void FinalHistoricalResultCannotBeRewrittenByIncompatibleTerminalReceipt(string prior, string incoming)
    {
        static Receipt ReceiptFor(string name)
        {
            Operation phase = Enum.Parse<Operation>(name);
            return new(OperationId, RequestHash, All, phase, phase switch
            {
                Operation.Completed => Ordered(Phase.Recorded, Phase.NoChange, Phase.Recorded),
                Operation.Partial => Ordered(Phase.Recorded, Phase.Drifted, Phase.Planned),
                _ => Ordered(Phase.Planned, Phase.Planned, Phase.Planned)
            });
        }
        State final = ReceiptFor(prior).ToState();
        Assert.Throws<InvalidDataException>(() => NativePrivateApplyRecovery.Reduce(final,
            new Evidence(OperationId, RequestHash, OperationObservation.None, [], ReceiptFor(incoming))));
        Assert.Equal(Enum.Parse<Operation>(prior), final.Phase);
    }

    [Fact]
    public void SameFinalPhaseWithDifferentStoreHistoryIsAlsoRejected()
    {
        Receipt old = new(OperationId, RequestHash, ThreadsOnly, Operation.Completed, Single(Phase.Recorded));
        Receipt different = new(OperationId, RequestHash, ThreadsOnly, Operation.Completed, Single(Phase.NoChange));
        Assert.Throws<InvalidDataException>(() => NativePrivateApplyRecovery.Reduce(old.ToState(),
            new Evidence(OperationId, RequestHash, OperationObservation.None, [], different)));
        Assert.Equal(Operation.Completed, NativePrivateApplyRecovery.Reduce(old.ToState(),
            new Evidence(OperationId, RequestHash, OperationObservation.None, [], old)).Phase);
    }

    [Theory]
    [InlineData("Recorded", "NoChange")]
    [InlineData("NoChange", "Recorded")]
    [InlineData("PublishedVerified", "NoChange")]
    public void RefiningUnknownRemainderCannotRewriteAlreadyKnownPrefix(string prior, string reported)
    {
        State uncertain = State.FromValidated(OperationId, RequestHash, All, Operation.RecoveryRequired,
            Ordered(Enum.Parse<Phase>(prior), Phase.Unknown, Phase.Planned));
        Receipt incompatible = new(OperationId, RequestHash, All, Operation.Completed,
            Ordered(Enum.Parse<Phase>(reported), Phase.Recorded, Phase.Recorded));
        Assert.Throws<InvalidDataException>(() => NativePrivateApplyRecovery.Reduce(uncertain,
            new Evidence(OperationId, RequestHash, OperationObservation.None, [], incompatible)));
    }

    [Fact]
    public void RefiningUnknownCanAcknowledgePreviouslyPublishedPrefixWithoutReapplying()
    {
        State uncertain = State.FromValidated(OperationId, RequestHash, All, Operation.RecoveryRequired,
            Ordered(Phase.PublishedVerified, Phase.Unknown, Phase.Planned));
        Receipt completed = new(OperationId, RequestHash, All, Operation.Completed,
            Ordered(Phase.Recorded, Phase.Recorded, Phase.Recorded));
        State result = NativePrivateApplyRecovery.Reduce(uncertain,
            new Evidence(OperationId, RequestHash, OperationObservation.None, [], completed));
        Assert.Equal(Operation.Completed, result.Phase);
        Assert.Equal(Phase.PublishedVerified, uncertain.Steps[0].Phase);
        Assert.Equal(Phase.Unknown, uncertain.Steps[1].Phase);
    }

    [Fact]
    public void PureRecoverySurfaceAcceptsOnlyDataAndReturnsNoIoOrMutationCommands()
    {
        const BindingFlags members = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        MethodInfo reduce = Assert.Single(typeof(NativePrivateApplyRecovery)
            .GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic),
            m => m.Name == "Reduce");
        Assert.Equal(typeof(State), reduce.ReturnType);
        Assert.Equal(new[] { typeof(State), typeof(Evidence) }, reduce.GetParameters().Select(p => p.ParameterType));
        // No Stream/path/provider callback/store/owner/Task/token is accepted. This
        // checks the passive boundary, not a claim of OS-level IO sandboxing.
        Type[] dataTypes = [typeof(State), typeof(Step), typeof(Evidence), typeof(StepEvidence), typeof(Receipt)];
        HashSet<Type> allowed = [typeof(Guid), typeof(string), typeof(NativePrivateMetadataSelection),
            typeof(Operation), typeof(Phase), typeof(Store), typeof(OperationObservation), typeof(Observation),
            typeof(ImmutableArray<Step>), typeof(ImmutableArray<StepEvidence>), typeof(Receipt)];
        foreach (Type type in dataTypes)
        {
            Assert.True(type.IsSealed);
            Assert.All(type.GetProperties(members), property =>
            {
                Assert.Null(property.GetSetMethod(true));
                Assert.Contains(property.PropertyType, allowed);
            });
            Assert.All(type.GetFields(members), field => Assert.True(field.IsInitOnly));
        }
        Assert.DoesNotContain(typeof(State).GetProperties(members), p =>
            p.Name.Contains("Command", StringComparison.OrdinalIgnoreCase)
            || p.Name.Contains("Mutation", StringComparison.OrdinalIgnoreCase)
            || p.Name.Contains("Resume", StringComparison.OrdinalIgnoreCase));
    }
}
