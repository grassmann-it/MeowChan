using System.Collections.Immutable;
using MeowChan.Storage;
using MeowChan.Transfer;
using static MeowChan.Tests.Transfer.NativeV2ImportFixture;

namespace MeowChan.Tests.Transfer;

// Value semantics only. Direct DTO/plan construction below is deliberately not owner issuance.
public sealed class NativeV2MergeStateTests
{
    [Fact]
    public async Task VersionedPlanFreezesSemanticSelectionsAndRejectsLegacyProjections()
    {
        using Workspace files = new();
        await using NativeTransferImportStage stage = await StageAsync(files, Content(), Original, Preview);
        NativeMediaMaterializationPlan materialization = NativeMediaMaterializationPlan.Create(stage, Id);
        NativeMediaCandidateSelection safe = materialization.Select(materialization.FullSelection.Members
            .Where(m => m.Id.Role == NativeMediaMemberRole.Preview).Select(m => m.Id));
        NativeMediaMergeCounts counts = Counts(materialization, safe);
        NativeMediaMergePlan plan = new(null!, materialization, "synthetic-instance", 7, null!, [], [], [], [],
            safe, counts, Guid.NewGuid(), new string('a', 64), files.StageRoot,
            new(true, 0, 0, 0, 0, 0));
        NativeMediaMergeV2State state = Assert.IsType<NativeMediaMergeV2State>(plan.Representation);
        Assert.Equal(2, plan.PlanVersion);
        Assert.True(plan.HasSafeCandidate);
        Assert.Equal(0, plan.NewBindingCount);
        Assert.Same(materialization.FullSelection, state.FullSelection);
        Assert.Same(safe, state.SafeSelection);
        Assert.Equal(stage.Package.Envelope.CatalogBytes, plan.CatalogBytes);
        Assert.Equal(plan.OperationId, state.Identity.OperationId);
        Assert.Equal(plan.ImportedAtUtc, state.Identity.ImportedAtUtc);
        Assert.Equal(plan.OperationsParent, state.Identity.OperationsParent);
        Assert.Throws<InvalidOperationException>(() => plan.Inputs);
        Assert.Throws<InvalidOperationException>(() => plan.FullKeys);
        Assert.Throws<InvalidOperationException>(() => plan.SafeKeys);
        Assert.Throws<InvalidOperationException>(() => plan.FullKeysJson);
        Assert.Throws<InvalidOperationException>(() => plan.SafeKeysJson);
        var copied = safe.Members.ToArray();
        copied[0] = copied[0] with { ContextOnly = false };
        Assert.True(Assert.Single(state.SafeSelection.Members,
            m => m.Id.Role == NativeMediaMemberRole.OriginalEvidence).ContextOnly);
        Assert.Empty(state.SafeInventory!.Bindings);
        Assert.Single(state.SafeInventory.DerivedRepresentations);
        Assert.False(plan.ReuseFullAsSafe);
        Assert.Equal(counts.EvidenceAdditions, state.CommittedResult().EvidenceAdditions);
        Assert.Equal(counts.DerivedDescriptorAdditions, state.CommittedResult().DerivedDescriptorAdditions);
        Assert.Equal(counts.ContainedDerivedAdditions, state.CommittedResult().ContainedDerivedAdditions);
        Assert.Equal(state.CommittedResult() with { IsReplay = true }, state.CommittedResult(true));
    }

    [Fact]
    public async Task AliasRequiresCanonicalFullEqualityAndNoConflictsAndEmptySafeHasNoInventory()
    {
        using Workspace files = new();
        await using NativeTransferImportStage stage = await StageAsync(files, Content(), Original, Preview);
        var materialization = NativeMediaMaterializationPlan.Create(stage, Id);
        var fullCopy = NativeMediaCandidateSelection.Parse(materialization.FullSelection.CanonicalJson);
        Assert.NotSame(materialization.FullSelection, fullCopy);
        Assert.True(State(materialization, fullCopy).ReuseFullAsSafe);
        Assert.False(State(materialization, fullCopy, conflicts: 1).ReuseFullAsSafe);
        var empty = materialization.Select([]);
        var state = State(materialization, empty);
        Assert.Null(state.SafeInventory);
        Assert.False(state.ReuseFullAsSafe);
        var operation = Operation(state, NativeMediaImportOperationState.Preparing);
        Assert.True(state.MatchesOperation(operation, operation.State));
        Assert.False(state.MatchesOperation(operation with
        { Safe = operation.Safe with { Directory = "unexpected" } }, operation.State));
    }

    [Theory]
    [InlineData((int)NativeMediaImportOperationState.Preparing)]
    [InlineData((int)NativeMediaImportOperationState.Prepared)]
    [InlineData((int)NativeMediaImportOperationState.Committed)]
    public async Task OperationMatchingBindsEveryFrozenValue(int phaseValue)
    {
        var phase = (NativeMediaImportOperationState)phaseValue;
        using Workspace files = new();
        await using NativeTransferImportStage stage = await StageAsync(files, Content(), Original, Preview);
        var materialization = NativeMediaMaterializationPlan.Create(stage, Id);
        var state = State(materialization, materialization.FullSelection);
        var operation = Operation(state, phase);
        Assert.True(state.MatchesOperation(operation, phase));
        NativeMediaPersistedMergeOperation[] changes =
        [
            operation with { Identity = operation.Identity with { OperationId = Guid.NewGuid() } },
            operation with { Identity = operation.Identity with { OperationsParent = "other-parent" } },
            operation with { Identity = operation.Identity with { CatalogGeneration = 8 } },
            operation with { CatalogBytes = operation.CatalogBytes.SetItem(0, (byte)(operation.CatalogBytes[0] ^ 1)) },
            operation with { RootsJson = "[]" },
            operation with { Roots = [] },
            operation with { Counts = operation.Counts with { Conflicts = 1 } },
            operation with { Full = operation.Full with { MemberCount = 0 } },
            operation with { Safe = operation.Safe with { SelectionSha256 = new string('f', 64) } },
            operation with { Full = operation.Full with { LegacyBindingsSha256 = new string('f', 64) } },
            operation with { Full = operation.Full with { LegacyKeysJson = "[ ]" } },
            operation with { Safe = operation.Safe with { Selection = materialization.Select([]) } }
        ];
        Assert.All(changes, changed => Assert.False(state.MatchesOperation(changed, phase)));
        Assert.False(state.MatchesOperation(operation, phase == NativeMediaImportOperationState.Preparing
            ? NativeMediaImportOperationState.Prepared : NativeMediaImportOperationState.Preparing));
        Assert.False(state.MatchesOperation(operation with { Full = operation.Full with
        { InventorySha256 = new string('f', 64) } }, phase));
    }

    [Fact]
    public async Task ExactNullableSourceVersionCannotBeFilledFromMeasuredBytesAndContextNeedsChild()
    {
        using Workspace files = new();
        await using NativeTransferImportStage stage = await StageAsync(files, Content(), Original, Preview);
        var materialization = NativeMediaMaterializationPlan.Create(stage, Id);
        var preview = Assert.Single(materialization.FullSelection.Members, m => m.Id.Role == NativeMediaMemberRole.Preview);
        Assert.Null(preview.Id.SourceVersion!.Sha1);
        Assert.NotNull(preview.Descriptor.MeasuredPayload!.Sha1);
        var changedId = preview.Id with { SourceVersion = preview.Id.SourceVersion with
        { Sha1 = preview.Descriptor.MeasuredPayload.Sha1 } };
        Assert.Throws<InvalidDataException>(() => materialization.Select([changedId]));
        Assert.Throws<InvalidDataException>(() => materialization.Select([preview.Id with { SourceVersion = null }]));
        var safe = materialization.Select([preview.Id]);
        var parent = Assert.Single(safe.Members, m => m.ContextOnly);
        Assert.Throws<InvalidDataException>(() => NativeMediaCandidateSelection.Create([parent]));
        Assert.Throws<InvalidDataException>(() => safe.Select([parent.Id]));
        materialization.ValidateSelection(safe);
        Assert.All(materialization.FullSelection.Members, member => Assert.False(member.ContextOnly));
        Assert.False(State(materialization, safe).ReuseFullAsSafe);
    }

    private static NativeMediaMergeCounts Counts(NativeMediaMaterializationPlan materialization,
        NativeMediaCandidateSelection safe, int conflicts = 0)
    {
        var inventory = safe.CreateInventory(Guid.NewGuid(), materialization.Package.Envelope.Fingerprint, At);
        return new(0, 0, 0, inventory.Bindings.Length, conflicts,
            safe.Members.Count(m => m.Id.Role == NativeMediaMemberRole.OriginalEvidence && !m.ContextOnly),
            inventory.DerivedRepresentations.Length,
            inventory.DerivedRepresentations.Count(d => d.IncomingStatus == MediaDerivedStatus.Contained));
    }

    private static NativeMediaMergeV2State State(NativeMediaMaterializationPlan materialization,
        NativeMediaCandidateSelection safe, int conflicts = 0)
        => new(materialization, safe, new(2, Guid.NewGuid(), new string('a', 64),
            materialization.Package.Envelope.Fingerprint, "synthetic-instance", 7, Id, "synthetic-parent", At),
            [new("synthetic-target-root", new string('b', 64))], Counts(materialization, safe, conflicts));

    private static NativeMediaPersistedMergeOperation Operation(NativeMediaMergeV2State state,
        NativeMediaImportOperationState phase)
        => new(state.Identity, phase, state.RootsJson, state.Roots,
            Root(state.FullSelection, state.FullInventory, phase),
            Root(state.SafeSelection, state.SafeInventory, phase), state.CatalogBytes, state.Counts);

    private static NativeMediaPersistedRoot Root(NativeMediaCandidateSelection selection,
        NativeMediaCandidateInventory? inventory, NativeMediaImportOperationState phase)
    {
        if (selection.IsEmpty) return new("[]", [], null, null, null, 0, null, null, null);
        bool ready = phase != NativeMediaImportOperationState.Preparing;
        return new("[]", [], null, selection, selection.Sha256, selection.Members.Length,
            ready ? inventory!.Sha256 : null, ready ? "synthetic-candidate" : null,
            ready ? new string('c', 64) : null);
    }
}
