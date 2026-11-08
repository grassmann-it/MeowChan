using MeowChan.Archive;
using MeowChan.Persistence;
using MeowChan.Storage;
using MeowChan.Transfer;
using Microsoft.Data.Sqlite;

namespace MeowChan.Tests.Transfer;

// Synthetic profiles and real validated stages only. Admission tests deliberately use the
// existing coordinator; a persisted DTO or a copied operation identity is not a capability.
public sealed class NativeV2ImportAdmissionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConstructedV2PlanCannotBorrowAnIssuedPlansIdentityOrReceipt(bool alreadyCommitted)
    {
        await using NativeV2ImportFixture f = await NativeV2ImportFixture.CreateAsync();
        using ProfileAdmissionToken admission = await f.ExclusiveAsync();
        NativeMediaImportPlan issued = f.Plan;
        NativeMediaImportV2State state = Assert.IsType<NativeMediaImportV2State>(issued.Representation);
        Assert.Same(state, f.Catalog.RequireIssuedNativeMediaImportV2Plan(admission, issued));
        if (alreadyCommitted)
        {
            var committed = await f.CommitAsync(admission);
            Assert.Equal(NativeMediaImportCommitStatus.Committed, committed.Result.Status);
            Assert.Equal(ProfileExclusiveDisposition.SafeToResume, committed.Disposition);
        }

        // The real v2 constructor copies the same materialization and operation identity.
        // It chooses its own timestamp; neither a timestamp nor matching DTO fields issues it.
        NativeMediaImportPlan forged = new(f.Catalog, state.Materialization, issued.Instance,
            issued.Generation, issued.OperationId, issued.RequestSha256, issued.OperationsParent);
        Assert.NotSame(issued, forged);
        Assert.Same(issued.Owner, forged.Owner);
        Assert.Same(state.Materialization, forged.Representation!.Materialization);
        Assert.Equal(2, forged.PlanVersion);
        Assert.Equal(issued.OperationId, forged.OperationId);
        Assert.Equal(issued.RequestSha256, forged.RequestSha256);
        Assert.Equal(issued.Resource, forged.Resource);
        Assert.Equal(issued.OperationsParent, forged.OperationsParent);
        Assert.Equal(state.Selection.CanonicalJson, forged.Representation.Selection.CanonicalJson);
        Assert.Throws<InvalidOperationException>(() => f.Catalog.RequireIssuedNativeMediaImportV2Plan(admission, forged));

        var filesBefore = NativeV2ImportFixture.PayloadFiles(f.Files.Profile);
        string[] entriesBefore = ProfileEntries(f);
        int callbacks = 0, dispatches = 0;
        f.Catalog.BeforeWriteDispatchForTests = _ => dispatches++;
        var rejected = await f.Catalog.CommitNativeMediaImportAdmittedAsync(admission, forged, f.Store,
            checkpoint: _ => callbacks++, beforeAdmission: () => callbacks++);
        Assert.Equal(ProfileExclusiveDisposition.SafeToResume, rejected.Disposition);
        Assert.Equal(new NativeMediaImportCommitResult(NativeMediaImportCommitStatus.StalePlan), rejected.Result);
        Assert.Equal(0, callbacks);
        Assert.Equal(0, dispatches);

        // Typed named arguments select the v2 overload. Owner/CWT rejection must precede
        // even null evidence validation, let alone filesystem I/O or a callback.
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Store.MaterializePassiveImportCandidateAdmittedAsync(
            admission, forged, preparing: null!, childDirectoryCreated: _ => callbacks++));
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Store.WithVerifiedPassiveImportCandidateAdmittedAsync(
            admission, forged, prepared: null!, callback: () => { callbacks++; return Task.FromResult(true); }));
        Assert.Equal(0, callbacks);
        Assert.Equal(filesBefore, NativeV2ImportFixture.PayloadFiles(f.Files.Profile));
        Assert.Equal(entriesBefore, ProfileEntries(f));
        Assert.Equal(alreadyCommitted ? 1 : 0, ReadOperations(f).Length);
        NativeMediaImportCommitResult? receipt = await ReceiptAsync(f, issued);
        if (alreadyCommitted)
        {
            Assert.NotNull(receipt);
            Assert.Equal(NativeMediaImportCommitStatus.Committed, receipt.Status);
            Assert.True(receipt.IsReplay);
            Assert.True(state.MatchesOperation(Assert.Single(ReadOperations(f)), NativeMediaImportOperationState.Committed));
        }
        else
        {
            Assert.Null(receipt);
            Assert.False(Directory.Exists(issued.OperationsParent));
        }
        Assert.Same(state, f.Catalog.RequireIssuedNativeMediaImportV2Plan(admission, issued));
    }

    [Fact]
    public async Task ForeignOwnerIssuedPlanCannotExecuteInThisCatalog()
    {
        await using NativeV2ImportFixture f = await NativeV2ImportFixture.CreateAsync();
        await using NativeV2ImportFixture foreign = await NativeV2ImportFixture.CreateAsync();
        using ProfileAdmissionToken admission = await f.ExclusiveAsync();
        using ProfileAdmissionToken foreignAdmission = await foreign.ExclusiveAsync();
        Assert.Same(foreign.Plan.Representation,
            foreign.Catalog.RequireIssuedNativeMediaImportV2Plan(foreignAdmission, foreign.Plan));
        Assert.Throws<InvalidOperationException>(() => f.Catalog.RequireIssuedNativeMediaImportV2Plan(admission, foreign.Plan));
        string[] localBefore = ProfileEntries(f), foreignBefore = ProfileEntries(foreign);
        int callbacks = 0, dispatches = 0;
        f.Catalog.BeforeWriteDispatchForTests = _ => dispatches++;
        foreign.Catalog.BeforeWriteDispatchForTests = _ => dispatches++;

        var rejected = await f.Catalog.CommitNativeMediaImportAdmittedAsync(admission, foreign.Plan, f.Store,
            checkpoint: _ => callbacks++, beforeAdmission: () => callbacks++);
        Assert.Equal(ProfileExclusiveDisposition.SafeToResume, rejected.Disposition);
        Assert.Equal(new NativeMediaImportCommitResult(NativeMediaImportCommitStatus.StalePlan), rejected.Result);
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Store.MaterializePassiveImportCandidateAdmittedAsync(
            admission, foreign.Plan, preparing: null!, childDirectoryCreated: _ => callbacks++));
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Store.WithVerifiedPassiveImportCandidateAdmittedAsync(
            admission, foreign.Plan, prepared: null!, callback: () => { callbacks++; return Task.FromResult(true); }));
        Assert.Equal(0, callbacks);
        Assert.Equal(0, dispatches);
        Assert.Empty(ReadOperations(f));
        Assert.Empty(ReadOperations(foreign));
        Assert.Null(await ReceiptAsync(f, f.Plan));
        Assert.Null(await ReceiptAsync(foreign, foreign.Plan));
        Assert.Equal(localBefore, ProfileEntries(f));
        Assert.Equal(foreignBefore, ProfileEntries(foreign));
        Assert.False(Directory.Exists(f.Plan.OperationsParent));
        Assert.False(Directory.Exists(foreign.Plan.OperationsParent));
    }

    [Fact]
    public async Task ForeignExclusiveAdmissionCannotBorrowTheLocalIssuedPlan()
    {
        await using NativeV2ImportFixture f = await NativeV2ImportFixture.CreateAsync();
        await using NativeV2ImportFixture foreign = await NativeV2ImportFixture.CreateAsync();
        using ProfileAdmissionToken foreignAdmission = await foreign.ExclusiveAsync();
        string[] before = ProfileEntries(f);
        int callbacks = 0, dispatches = 0;
        f.Catalog.BeforeWriteDispatchForTests = _ => dispatches++;
        Assert.Throws<InvalidOperationException>(() => f.Catalog.RequireIssuedNativeMediaImportV2Plan(foreignAdmission, f.Plan));
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Catalog.CommitNativeMediaImportAdmittedAsync(
            foreignAdmission, f.Plan, f.Store, checkpoint: _ => callbacks++, beforeAdmission: () => callbacks++));
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Store.MaterializePassiveImportCandidateAdmittedAsync(
            foreignAdmission, f.Plan, preparing: null!, childDirectoryCreated: _ => callbacks++));
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Store.WithVerifiedPassiveImportCandidateAdmittedAsync(
            foreignAdmission, f.Plan, prepared: null!, callback: () => { callbacks++; return Task.FromResult(true); }));
        Assert.Equal(0, callbacks);
        Assert.Equal(0, dispatches);
        Assert.Empty(ReadOperations(f));
        Assert.Null(await ReceiptAsync(f, f.Plan));
        Assert.Equal(before, ProfileEntries(f));
        Assert.False(Directory.Exists(f.Plan.OperationsParent));
    }

    [Fact]
    public async Task CanceledReplayUsesTheExactReceiptWithoutStageCandidateOrWriteDispatch()
    {
        await using NativeV2ImportFixture f = await NativeV2ImportFixture.CreateAsync();
        using ProfileAdmissionToken admission = await f.ExclusiveAsync();
        var first = await f.CommitAsync(admission);
        Assert.Equal(NativeMediaImportCommitStatus.Committed, first.Result.Status);
        Assert.Equal(ProfileExclusiveDisposition.SafeToResume, first.Disposition);
        Assert.False(first.Result.IsReplay);
        NativeMediaImportCommitResult receipt = Assert.IsType<NativeMediaImportCommitResult>(await ReceiptAsync(f, f.Plan));
        Assert.Equal(first.Result with { IsReplay = true }, receipt);
        NativeMediaPersistedImportOperation operation = Assert.Single(ReadOperations(f));
        Assert.Equal(NativeMediaImportOperationState.Committed, operation.State);
        Assert.NotNull(operation.Full.Directory);
        await f.Stage.DisposeAsync();
        // Losing synthetic candidate files cannot revoke historical SQL success. A replay
        // must not try to inspect, recreate or materialize that now-missing directory.
        Directory.Delete(operation.Full.Directory, recursive: true);
        string[] entriesBefore = ProfileEntries(f);
        var filesBefore = NativeV2ImportFixture.PayloadFiles(f.Files.Profile);
        int callbacks = 0, dispatches = 0;
        f.Catalog.BeforeWriteDispatchForTests = _ => dispatches++;
        var replay = await f.CommitAsync(admission, new CancellationToken(true),
            checkpoint: _ => callbacks++, beforeAdmission: () => callbacks++);
        Assert.Equal(ProfileExclusiveDisposition.SafeToResume, replay.Disposition);
        Assert.Equal(receipt, replay.Result); // Includes all four v2 counters and null diagnostic.
        Assert.True(replay.Result.IsReplay);
        Assert.Equal(0, callbacks);
        Assert.Equal(0, dispatches);
        Assert.Equal(receipt, await ReceiptAsync(f, f.Plan));
        AssertOperationUnchanged(operation, Assert.Single(ReadOperations(f)));
        Assert.Equal(filesBefore, NativeV2ImportFixture.PayloadFiles(f.Files.Profile));
        Assert.Equal(entriesBefore, ProfileEntries(f));
        Assert.False(Directory.Exists(operation.Full.Directory));
        f.Catalog.EnsureTransferAdmission(admission); // Borrowed owner is still the caller's.
    }

    [Theory]
    [InlineData((int)NativeMediaImportPhase.PreparingSaved)]
    [InlineData((int)NativeMediaImportPhase.PreparedSaved)]
    public async Task OwnCommittedReceiptCannotResumeOverAnotherUnfinishedV2Operation(int interruptedPhase)
    {
        await using NativeV2ImportFixture f = await NativeV2ImportFixture.CreateAsync();
        using ProfileAdmissionToken admission = await f.ExclusiveAsync();
        var committed = await f.CommitAsync(admission);
        Assert.Equal(NativeMediaImportCommitStatus.Committed, committed.Result.Status);
        Assert.Equal(ProfileExclusiveDisposition.SafeToResume, committed.Disposition);
        NativeMediaImportCommitResult receipt = Assert.IsType<NativeMediaImportCommitResult>(await ReceiptAsync(f, f.Plan));
        Assert.Equal(committed.Result with { IsReplay = true }, receipt);

        // A second genuine writer -> validator -> stage -> owner-issued plan. No raw SQL
        // inserts, repaired operation rows or malformed evidence are needed for the blocker.
        await using NativeTransferImportStage otherStage = await NativeV2ImportFixture.StageAsync(f.Files, OtherContent());
        NativeMediaImportPreparation prepared = await f.Catalog.PrepareNativeMediaImportAsync(otherStage);
        Assert.Equal(NativeMediaImportError.None, prepared.Error);
        NativeMediaImportPlan other = Assert.IsType<NativeMediaImportPlan>(prepared.Plan);
        Assert.Equal(2, other.PlanVersion);
        Assert.NotEqual(f.Plan.OperationId, other.OperationId);
        Assert.NotEqual(f.Plan.Resource, other.Resource);
        bool reached = false;
        var interrupted = await f.Catalog.CommitNativeMediaImportAdmittedAsync(admission, other, f.Store, checkpoint: phase =>
        {
            if ((int)phase != interruptedPhase) return;
            reached = true;
            throw new IOException("synthetic second-operation interruption");
        });
        Assert.True(reached);
        Assert.Equal(ProfileExclusiveDisposition.RecoveryRequired, interrupted.Disposition);
        Assert.Equal(NativeMediaImportCommitStatus.ReconciliationRequired, interrupted.Result.Status);
        Assert.Null(await ReceiptAsync(f, other));
        NativeMediaPersistedImportOperation[] operations = ReadOperations(f);
        Assert.Equal(2, operations.Length);
        NativeMediaPersistedImportOperation ownOperation = Assert.Single(operations, o => o.Identity.OperationId == f.Plan.OperationId);
        NativeMediaPersistedImportOperation otherOperation = Assert.Single(operations, o => o.Identity.OperationId == other.OperationId);
        Assert.Equal(NativeMediaImportOperationState.Committed, ownOperation.State);
        Assert.Equal(interruptedPhase == (int)NativeMediaImportPhase.PreparingSaved
            ? NativeMediaImportOperationState.Preparing : NativeMediaImportOperationState.Prepared, otherOperation.State);
        Assert.True(other.Representation!.MatchesOperation(otherOperation, otherOperation.State));
        await using (ArchiveReader reader = await ArchiveReader.OpenAsync(f.Files.Profile))
        {
            Assert.NotNull(await reader.ReadThreadAsync(f.Plan.Resource));
            Assert.Null(await reader.ReadThreadAsync(other.Resource));
        }
        await f.Stage.DisposeAsync();
        await otherStage.DisposeAsync();
        string[] entriesBefore = ProfileEntries(f);
        var filesBefore = NativeV2ImportFixture.PayloadFiles(f.Files.Profile);
        int callbacks = 0, dispatches = 0;
        f.Catalog.BeforeWriteDispatchForTests = _ => dispatches++;
        var replay = await f.CommitAsync(admission, new CancellationToken(true),
            checkpoint: _ => callbacks++, beforeAdmission: () => callbacks++);
        Assert.Equal(NativeMediaImportCommitStatus.Committed, replay.Result.Status);
        Assert.Equal(receipt, replay.Result);
        Assert.True(replay.Result.IsReplay);
        Assert.Equal(ProfileExclusiveDisposition.RecoveryRequired, replay.Disposition);
        Assert.Equal(0, callbacks);
        Assert.Equal(0, dispatches);
        Assert.Equal(receipt, await ReceiptAsync(f, f.Plan));
        Assert.Null(await ReceiptAsync(f, other));
        NativeMediaPersistedImportOperation[] after = ReadOperations(f);
        Assert.Equal(2, after.Length);
        AssertOperationUnchanged(ownOperation, Assert.Single(after, o => o.Identity.OperationId == f.Plan.OperationId));
        AssertOperationUnchanged(otherOperation, Assert.Single(after, o => o.Identity.OperationId == other.OperationId));
        Assert.Equal(filesBefore, NativeV2ImportFixture.PayloadFiles(f.Files.Profile));
        Assert.Equal(entriesBefore, ProfileEntries(f));
    }

    private static NativeTransferCatalog OtherContent()
    {
        NativeTransferCatalog template = NativeV2ImportFixture.Content(originalContained: false, derived: false, presence: false);
        TransferThread thread = template.Threads[0];
        ResourceId id = new(thread.Id.Source, thread.Id.Board, "7002");
        TransferAttachment attachment = thread.Posts[0].Attachments[0];
        CapturedAttachment metadata = attachment.Metadata;
        MediaProviderMetadata provider = new(metadata.MediaId!, metadata.Variant, metadata.Url!,
            metadata.OriginalFileName, metadata.ExpectedSize, metadata.ExpectedMd5, metadata.ExpectedSha256)
        { ExpectedSha1 = metadata.ExpectedSha1, ProviderSizeHint = metadata.ProviderSizeHint };
        attachment = attachment with { Metadata = metadata with
        { AttachmentRevisionKey = MediaRevisionKeys.CreateAttachmentKey(id, "15", metadata.Position, provider) } };
        return template with { Threads = [thread with
        {
            Id = id, PageUrl = "https://boards.4chan.org/b/thread/7002",
            Posts = [NativeV2ImportFixture.Post("15", attachment)]
        }] };
    }

    private static Task<NativeMediaImportCommitResult?> ReceiptAsync(NativeV2ImportFixture f, NativeMediaImportPlan plan)
        => f.Catalog.ReadNativeMediaImportReceiptAsync(plan.OperationId, plan.RequestSha256, planVersion: 2);

    private static NativeMediaPersistedImportOperation[] ReadOperations(NativeV2ImportFixture f)
    {
        using SqliteConnection connection = ArchiveSql.OpenConnection(f.Catalog.DatabasePath, SqliteOpenMode.ReadOnly);
        NativeMediaPersistedPage<NativeMediaPersistedImportOperation> page = ArchiveSql.ReadPersistedMediaImportOperationsPage(connection, null);
        Assert.Null(page.NextOperationId);
        return page.Items.ToArray();
    }

    private static string[] ProfileEntries(NativeV2ImportFixture f)
        => Directory.GetFileSystemEntries(f.Files.Profile, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(f.Files.Profile, path)).Order(StringComparer.Ordinal).ToArray();

    private static void AssertOperationUnchanged(NativeMediaPersistedImportOperation expected,
        NativeMediaPersistedImportOperation actual)
    {
        Assert.Equal(expected.Identity, actual.Identity);
        Assert.Equal(expected.State, actual.State);
        Assert.Equal(expected.Counts, actual.Counts);
        Assert.Equal(expected.CatalogBytes.ToArray(), actual.CatalogBytes.ToArray());
        Assert.Equal(expected.Full.LegacyKeysJson, actual.Full.LegacyKeysJson);
        Assert.Equal(expected.Full.LegacyBindingsSha256, actual.Full.LegacyBindingsSha256);
        Assert.Equal(expected.Full.Selection!.CanonicalJson, actual.Full.Selection!.CanonicalJson);
        Assert.Equal(expected.Full.SelectionSha256, actual.Full.SelectionSha256);
        Assert.Equal(expected.Full.MemberCount, actual.Full.MemberCount);
        Assert.Equal(expected.Full.InventorySha256, actual.Full.InventorySha256);
        Assert.Equal(expected.Full.Directory, actual.Full.Directory);
        Assert.Equal(expected.Full.ManifestGeneration, actual.Full.ManifestGeneration);
    }
}
