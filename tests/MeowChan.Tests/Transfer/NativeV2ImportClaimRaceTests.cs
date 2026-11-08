using System.Collections.Concurrent;
using MeowChan.Archive;
using MeowChan.Persistence;
using MeowChan.Storage;
using MeowChan.Transfer;
using Microsoft.Data.Sqlite;

namespace MeowChan.Tests.Transfer;

// Real owner-issued v2 plans and SQL claims. All scheduling gates are outside SQL;
// neither the shared fixture nor production admission/claim behavior is replaced.
public sealed class NativeV2ImportClaimRaceTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(20);

    [Fact]
    public async Task ConcurrentPlansForSameResourceOnlyActualInsertWinnerMaterializes()
    {
        await using NativeV2ImportFixture f = await NativeV2ImportFixture.CreateAsync(
            NativeV2ImportFixture.Content(presence: false));
        NativeMediaImportPreparation secondPreparation = await f.Catalog.PrepareNativeMediaImportAsync(f.Stage);
        Assert.Equal(NativeMediaImportError.None, secondPreparation.Error);
        NativeMediaImportPlan second = Assert.IsType<NativeMediaImportPlan>(secondPreparation.Plan);
        NativeMediaImportPlan[] plans = [f.Plan, second];
        Assert.NotSame(plans[0], plans[1]);
        Assert.NotEqual(plans[0].OperationId, plans[1].OperationId);
        Assert.NotEqual(plans[0].RequestSha256, plans[1].RequestSha256);
        Assert.NotEqual(plans[0].OperationsParent, plans[1].OperationsParent);
        Assert.Equal(plans[0].Resource, plans[1].Resource);
        Assert.Equal(plans[0].Instance, plans[1].Instance);
        Assert.Equal(plans[0].Generation, plans[1].Generation);
        Assert.All(plans, p => Assert.Equal(2, p.PlanVersion));
        using ProfileAdmissionToken admission = await f.ExclusiveAsync();
        var dispatchOwners = new ConcurrentQueue<ProfileAdmissionToken>();
        var commitOwners = new ConcurrentQueue<ProfileAdmissionToken>();
        f.Catalog.BeforeWriteDispatchForTests = dispatchOwners.Enqueue;
        f.Catalog.AfterWriteCommitForTests = commitOwners.Enqueue;
        ConcurrentQueue<NativeMediaImportPhase>[] phases = [new(), new()];
        TaskCompletionSource bothNegativeReads = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<int> actualClaimWinner = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using ManualResetEventSlim releaseClaims = new(false);
        using ManualResetEventSlim releaseWinner = new(false);
        int observers = 0;
        Task<ProfileExclusiveOutcome<NativeMediaImportCommitResult>>[] pending = plans.Select((plan, index) =>
            Task.Run(() => f.Catalog.CommitNativeMediaImportAdmittedAsync(admission, plan, f.Store,
                checkpoint: phase =>
                {
                    phases[index].Enqueue(phase);
                    if (phase != NativeMediaImportPhase.PreparingSaved) return;
                    actualClaimWinner.TrySetResult(index);
                    if (!releaseWinner.Wait(Deadline)) throw new TimeoutException("claim winner watchdog");
                },
                beforeAdmission: () =>
                {
                    // Both independent receipt reads must be negative before either SQL claim
                    // may run. This is concurrency, not a second import after the first finishes.
                    if (Interlocked.Increment(ref observers) == 2) bothNegativeReads.TrySetResult();
                    if (!releaseClaims.Wait(Deadline)) throw new TimeoutException("claim race watchdog");
                }))).ToArray();
        int winner = -1;
        try
        {
            await bothNegativeReads.Task.WaitAsync(Deadline);
            Assert.All(pending, task => Assert.False(task.IsCompleted));
            Assert.Empty(Operations(f));
            Assert.All(plans, plan => Assert.False(Directory.Exists(plan.OperationsParent)));
            releaseClaims.Set();
            winner = await actualClaimWinner.Task.WaitAsync(Deadline);
            int loser = 1 - winner;
            ProfileExclusiveOutcome<NativeMediaImportCommitResult> rejected = await pending[loser].WaitAsync(Deadline);
            Assert.False(pending[winner].IsCompleted);
            Assert.Equal(ProfileExclusiveDisposition.RecoveryRequired, rejected.Disposition);
            Assert.Equal(NativeMediaImportCommitStatus.ReconciliationRequired, rejected.Result.Status);
            Assert.Null(rejected.Result.Diagnostic);
            Assert.Empty(phases[loser]);
            Assert.Equal(new[] { NativeMediaImportPhase.PreparingSaved }, phases[winner].ToArray());
            Assert.All(plans, plan => Assert.False(Directory.Exists(plan.OperationsParent)));
            NativeMediaPersistedImportOperation claim = Assert.Single(Operations(f));
            Assert.Equal(plans[winner].Representation!.Identity, claim.Identity);
            Assert.Equal(NativeMediaImportOperationState.Preparing, claim.State);
            Assert.Null(claim.Full.Directory);
            Assert.True(plans[winner].Representation!.MatchesOperation(claim, NativeMediaImportOperationState.Preparing));
            Assert.Null(await f.Catalog.ReadNativeMediaImportReceiptAsync(plans[loser].OperationId,
                plans[loser].RequestSha256, 2));
            Assert.All(dispatchOwners, token => Assert.Same(admission, token));
            Assert.All(commitOwners, token => Assert.Same(admission, token));
        }
        finally
        {
            // Always release and join both real operations before fixture/admission disposal.
            releaseClaims.Set();
            releaseWinner.Set();
            await Task.WhenAll(pending).WaitAsync(Deadline);
        }

        Assert.InRange(winner, 0, 1);
        int losingIndex = 1 - winner;
        ProfileExclusiveOutcome<NativeMediaImportCommitResult> committed = await pending[winner];
        Assert.Equal(ProfileExclusiveDisposition.SafeToResume, committed.Disposition);
        AssertCommittedCounts(committed.Result, replay: false);
        Assert.Equal(4, dispatchOwners.Count); // two claims, winner Prepared, winner final content
        Assert.Equal(4, commitOwners.Count); // includes the loser's no-op claim transaction
        Assert.All(dispatchOwners.Concat(commitOwners), token => Assert.Same(admission, token));
        Assert.Empty(phases[losingIndex]);
        Assert.False(Directory.Exists(plans[losingIndex].OperationsParent));
        foreach (NativeMediaImportPhase phase in Enum.GetValues<NativeMediaImportPhase>())
            Assert.Single(phases[winner], observed => observed == phase);
        NativeMediaPersistedImportOperation stored = Assert.Single(Operations(f));
        Assert.Equal(plans[winner].Representation!.Identity, stored.Identity);
        Assert.Equal(NativeMediaImportOperationState.Committed, stored.State);
        string candidate = Assert.Single(Directory.GetDirectories(plans[winner].OperationsParent));
        Assert.Equal(candidate, stored.Full.Directory);
        Assert.Equal(plans[winner].Representation!.Inventory.Sha256, stored.Full.InventorySha256);
        Assert.Equal(committed.Result with { IsReplay = true },
            await f.Catalog.ReadNativeMediaImportReceiptAsync(plans[winner].OperationId, plans[winner].RequestSha256, 2));
        Assert.Null(await f.Catalog.ReadNativeMediaImportReceiptAsync(plans[losingIndex].OperationId,
            plans[losingIndex].RequestSha256, 2));
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(f.Files.Profile);
        ArchivedThreadView thread = Assert.IsType<ArchivedThreadView>(await reader.ReadThreadAsync(NativeV2ImportFixture.Id));
        Assert.Equal(candidate, Assert.Single(thread.MediaDirectories));
        Assert.Equal(1, thread.PostCount);
        MediaAssetSnapshot media = await MediaAssetReader.OpenAsync(candidate);
        Assert.Equal(MediaManifestStatus.Available, media.Status);
        Assert.Equal(6, media.ManifestVersion);
        Assert.Single(media.AttachmentBindings);
        Assert.Single(media.OriginalEvidence);
        Assert.Single(media.DerivedRepresentations);
        f.Catalog.EnsureTransferAdmission(admission); // borrowed live owner was not consumed
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NegativeReceiptObserverFaultBeforeClaimHasNoDurableOrFilesystemEffects(bool cancel)
    {
        await using NativeV2ImportFixture f = await NativeV2ImportFixture.CreateAsync(
            NativeV2ImportFixture.Content(presence: false));
        using ProfileAdmissionToken admission = await f.ExclusiveAsync();
        using CancellationTokenSource cancellation = new();
        int observers = 0, dispatches = 0, phases = 0;
        f.Catalog.BeforeWriteDispatchForTests = _ => Interlocked.Increment(ref dispatches);
        ProfileExclusiveOutcome<NativeMediaImportCommitResult> outcome = await f.CommitAsync(admission,
            cancellation.Token, _ => Interlocked.Increment(ref phases), beforeAdmission: () =>
            {
                observers++;
                if (cancel)
                {
                    cancellation.Cancel();
                    cancellation.Token.ThrowIfCancellationRequested();
                }
                throw new IOException("negative receipt observer failed before claim");
            });
        Assert.Equal(1, observers);
        Assert.Equal(0, dispatches);
        Assert.Equal(0, phases);
        Assert.Equal(ProfileExclusiveDisposition.SafeToResume, outcome.Disposition);
        Assert.Equal(NativeMediaImportCommitStatus.Inactive, outcome.Result.Status);
        Assert.Equal(cancel ? nameof(OperationCanceledException) : nameof(IOException), outcome.Result.Diagnostic);
        Assert.Empty(Operations(f));
        Assert.False(Directory.Exists(f.Plan.OperationsParent));
        Assert.Null(await f.Catalog.ReadNativeMediaImportReceiptAsync(f.Plan.OperationId, f.Plan.RequestSha256, 2));
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(f.Files.Profile);
        Assert.Null(await reader.ReadThreadAsync(NativeV2ImportFixture.Id));
        f.Catalog.EnsureTransferAdmission(admission);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task LostResponseAfterRealSqlCommitReconcilesPersistedV2Evidence(int lostCommit)
    {
        await using NativeV2ImportFixture f = await NativeV2ImportFixture.CreateAsync(
            NativeV2ImportFixture.Content(presence: false));
        using ProfileAdmissionToken admission = await f.ExclusiveAsync();
        var owners = new ConcurrentQueue<ProfileAdmissionToken>();
        var phases = new ConcurrentQueue<NativeMediaImportPhase>();
        int commits = 0;
        f.Catalog.AfterWriteCommitForTests = token =>
        {
            owners.Enqueue(token);
            if (Interlocked.Increment(ref commits) == lostCommit)
                throw new IOException("lost return after real v2 SQL commit");
        };
        ProfileExclusiveOutcome<NativeMediaImportCommitResult> outcome = await f.CommitAsync(admission,
            checkpoint: phases.Enqueue);
        f.Catalog.AfterWriteCommitForTests = null;
        Assert.Equal(lostCommit, commits);
        Assert.All(owners, token => Assert.Same(admission, token));
        Assert.Equal(nameof(IOException), outcome.Result.Diagnostic);
        NativeMediaPersistedImportOperation stored = Assert.Single(Operations(f));
        Assert.Equal(f.Plan.Representation!.Identity, stored.Identity);
        NativeMediaImportOperationState expectedState = lostCommit switch
        {
            1 => NativeMediaImportOperationState.Preparing,
            2 => NativeMediaImportOperationState.Prepared,
            _ => NativeMediaImportOperationState.Committed
        };
        Assert.Equal(expectedState, stored.State);
        Assert.True(f.Plan.Representation!.MatchesOperation(stored, expectedState));
        NativeMediaImportCommitResult? receipt = await f.Catalog.ReadNativeMediaImportReceiptAsync(
            f.Plan.OperationId, f.Plan.RequestSha256, 2);
        Assert.DoesNotContain(NativeMediaImportPhase.SqlCommitted, phases); // commit observer failed before return
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(f.Files.Profile);
        ArchivedThreadView? thread = await reader.ReadThreadAsync(NativeV2ImportFixture.Id);
        if (lostCommit == 3)
        {
            Assert.Equal(ProfileExclusiveDisposition.SafeToResume, outcome.Disposition);
            AssertCommittedCounts(outcome.Result, replay: true);
            Assert.NotNull(thread);
            Assert.Equal(outcome.Result with { Diagnostic = null }, receipt);
            Assert.Equal(stored.Full.Directory, Assert.Single(thread!.MediaDirectories));
        }
        else
        {
            Assert.Equal(ProfileExclusiveDisposition.RecoveryRequired, outcome.Disposition);
            Assert.Equal(NativeMediaImportCommitStatus.ReconciliationRequired, outcome.Result.Status);
            Assert.False(outcome.Result.IsReplay);
            Assert.Null(receipt);
            Assert.Null(thread);
        }
        if (lostCommit == 1)
        {
            Assert.Empty(phases);
            Assert.False(Directory.Exists(f.Plan.OperationsParent));
            Assert.Null(stored.Full.Directory);
        }
        else
        {
            Assert.Single(phases, phase => phase == NativeMediaImportPhase.CandidateDirectoryCreated);
            Assert.Single(phases, phase => phase == NativeMediaImportPhase.CandidateReady);
            string candidate = Assert.Single(Directory.GetDirectories(f.Plan.OperationsParent));
            Assert.Equal(candidate, stored.Full.Directory);
            // All candidate handles were unwound before independent outcome reconciliation.
            using FileStream unlocked = new(Path.Combine(candidate, ".meowchan-store.lock"),
                FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }

        string[] beforeRetry = CandidateTree(f.Plan);
        int retriedPhases = 0, retriedObservers = 0;
        ProfileExclusiveOutcome<NativeMediaImportCommitResult> retry = await f.CommitAsync(admission,
            checkpoint: _ => retriedPhases++, beforeAdmission: () => retriedObservers++);
        Assert.Equal(0, retriedPhases);
        Assert.Equal(beforeRetry, CandidateTree(f.Plan));
        Assert.Equal(expectedState, Assert.Single(Operations(f)).State);
        Assert.Equal(lostCommit == 3 ? 0 : 1, retriedObservers);
        Assert.Equal(outcome.Disposition, retry.Disposition);
        Assert.Equal(outcome.Result with { Diagnostic = null }, retry.Result);
        f.Catalog.EnsureTransferAdmission(admission);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SqlCommittedObserverFaultOrCancellationCannotEraseHistoricalSuccess(bool cancel)
    {
        await using NativeV2ImportFixture f = await NativeV2ImportFixture.CreateAsync(
            NativeV2ImportFixture.Content(presence: false));
        using ProfileAdmissionToken admission = await f.ExclusiveAsync();
        using CancellationTokenSource cancellation = new();
        int observers = 0;
        ProfileExclusiveOutcome<NativeMediaImportCommitResult> outcome = await f.CommitAsync(admission,
            cancellation.Token, phase =>
            {
                if (phase != NativeMediaImportPhase.SqlCommitted) return;
                observers++;
                if (cancel)
                {
                    cancellation.Cancel();
                    cancellation.Token.ThrowIfCancellationRequested();
                }
                throw new IOException("committed observer response lost");
            });
        Assert.Equal(1, observers);
        Assert.Equal(ProfileExclusiveDisposition.SafeToResume, outcome.Disposition);
        AssertCommittedCounts(outcome.Result, replay: true);
        Assert.Equal(cancel ? nameof(OperationCanceledException) : nameof(IOException), outcome.Result.Diagnostic);
        Assert.Equal(NativeMediaImportOperationState.Committed, Assert.Single(Operations(f)).State);
        Assert.Equal(outcome.Result with { Diagnostic = null },
            await f.Catalog.ReadNativeMediaImportReceiptAsync(f.Plan.OperationId, f.Plan.RequestSha256, 2));
        await f.Stage.DisposeAsync();
        string[] before = CandidateTree(f.Plan);
        ProfileExclusiveOutcome<NativeMediaImportCommitResult> replay = await f.CommitAsync(admission,
            new CancellationToken(true), _ => throw new IOException("replay touched phases"),
            () => throw new IOException("replay touched claim observer"));
        Assert.Equal(ProfileExclusiveDisposition.SafeToResume, replay.Disposition);
        Assert.Equal(outcome.Result with { Diagnostic = null }, replay.Result);
        Assert.Equal(before, CandidateTree(f.Plan));
        f.Catalog.EnsureTransferAdmission(admission);
    }

    private static NativeMediaPersistedImportOperation[] Operations(NativeV2ImportFixture f)
    {
        using SqliteConnection connection = ArchiveSql.OpenConnection(f.Catalog.DatabasePath, SqliteOpenMode.ReadOnly);
        NativeMediaPersistedPage<NativeMediaPersistedImportOperation> page =
            ArchiveSql.ReadPersistedMediaImportOperationsPage(connection, null);
        Assert.Null(page.NextOperationId);
        return page.Items.ToArray();
    }

    private static string[] CandidateTree(NativeMediaImportPlan plan)
        => !Directory.Exists(plan.OperationsParent) ? []
            : Directory.GetFileSystemEntries(plan.OperationsParent, "*", SearchOption.AllDirectories)
                .Select(path => Path.GetRelativePath(plan.OperationsParent, path) + ":"
                    + (Directory.Exists(path) ? "directory" : NativeV2ImportFixture.Hash(File.ReadAllBytes(path))))
                .Order(StringComparer.Ordinal).ToArray();

    private static void AssertCommittedCounts(NativeMediaImportCommitResult result, bool replay)
    {
        Assert.Equal(NativeMediaImportCommitStatus.Committed, result.Status);
        Assert.Equal(replay, result.IsReplay);
        Assert.Equal(1, result.NewThreads);
        Assert.Equal(1, result.NewPosts);
        Assert.Equal(1, result.NewAttachments);
        Assert.Equal(1, result.EvidenceAdditions);
        Assert.Equal(1, result.DerivedDescriptorAdditions);
        Assert.Equal(1, result.ContainedDerivedAdditions);
        Assert.Equal(1, result.OriginalBindingCount);
    }
}
