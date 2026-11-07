using System.Collections.Immutable;
using MeowChan.Archive;
using MeowChan.Persistence;
using MeowChan.Storage;
using MeowChan.Transfer;
using Microsoft.Data.Sqlite;
using static MeowChan.Tests.Transfer.NativeMediaImportTests;

namespace MeowChan.Tests.Transfer;

// Storage-owner acceptance only: real synthetic profile/stage/SQL and genuine core-IVT
// Exclusive tokens. No reflection, fabricated tokens, public startup or host-resume claim.
public sealed class NativeMediaMergeAdmissionTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(15);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvalidAuthorityCannotReplayOrReachCallbacksSqlOrPaths(bool committed)
    {
        await using Fixture f = await Fixture.OpenAsync();
        if (committed)
        {
            using ProfileAdmissionToken owner = await f.Lease.Admission.EnterExclusiveAsync();
            Assert.Equal(NativeMediaImportCommitStatus.Committed,
                (await f.Commit(owner)).Result.Status);
        }
        using Workspace otherFiles = new();
        using ProfileLease foreignLease = ProfileLease.Acquire(otherFiles.Profile);
        foreignLease.Admission.CompleteStartup();
        using ProfileAdmissionToken foreign = await foreignLease.Admission.EnterExclusiveAsync();
        using ProfileAdmissionToken released = await f.Lease.Admission.EnterExclusiveAsync();
        released.Dispose();
        using ProfileAdmissionToken ordinary = f.Lease.Admission.EnterOrdinaryWriter();
        using ProfileLease startingLease = ProfileLease.Acquire(otherFiles.StageRoot);
        using ProfileAdmissionToken startup = startingLease.Admission.EnterStartupWriter();
        Dictionary<string, string> before = Files(f.Files.Profile);
        int callbacks = 0, dispatches = 0;
        f.Catalog.BeforeWriteDispatchForTests = _ => dispatches++;
        foreach (ProfileAdmissionToken invalid in new[] { foreign, released, ordinary, startup })
            await Assert.ThrowsAsync<InvalidOperationException>(() => f.Catalog.CommitNativeMediaMergeAdmittedAsync(
                invalid, f.Plan, new MediaStore(), beforeAdmission: () => callbacks++, checkpoint: _ => callbacks++));
        await Assert.ThrowsAsync<ArgumentNullException>(() => f.Catalog.CommitNativeMediaMergeAdmittedAsync(
            null!, f.Plan, new MediaStore(), beforeAdmission: () => callbacks++));
        Assert.Equal(0, callbacks);
        Assert.Equal(0, dispatches);
        Assert.Equal(before, Files(f.Files.Profile));
        Assert.Equal(committed, await f.Catalog.ReadNativeMediaMergeReceiptAsync(f.Plan.OperationId, f.Plan.RequestSha256) != null);
    }

    [Fact]
    public async Task StartingOwnerCannotApplyEvenWithAnIssuedPlan()
    {
        await using Fixture f = await Fixture.OpenAsync(ready: false);
        using ProfileAdmissionToken startup = f.Lease.Admission.EnterStartupWriter();
        Dictionary<string, string> before = Files(f.Files.Profile);
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Commit(startup));
        Assert.Equal(before, Files(f.Files.Profile));
        Assert.False(Directory.Exists(f.Plan.OperationsParent));
    }

    [Fact]
    public async Task AnotherCatalogsPlanIsRejectedWithoutTrustingItsPaths()
    {
        await using Fixture f = await Fixture.OpenAsync();
        await using ArchiveCatalog other = await ArchiveCatalog.OpenAsync(f.Files.Profile, f.Lease);
        using ProfileAdmissionToken owner = await f.Lease.Admission.EnterExclusiveAsync();
        Dictionary<string, string> before = Files(f.Files.Profile);
        var outcome = await other.CommitNativeMediaMergeAdmittedAsync(owner, f.Plan, new MediaStore(),
            beforeAdmission: () => throw new InvalidOperationException("must not enter"));
        Assert.Equal(NativeMediaImportCommitStatus.StalePlan, outcome.Result.Status);
        Assert.Equal(ProfileExclusiveDisposition.SafeToResume, outcome.Disposition);
        Assert.Equal(before, Files(f.Files.Profile));
        other.EnsureTransferAdmission(owner);
    }

    [Fact]
    public async Task SameBorrowedOwnerPinsAllFourWritesAndReplayNeedsNeitherStageNorRoot()
    {
        await using Fixture f = await Fixture.OpenAsync();
        using ProfileAdmissionToken owner = await f.Lease.Admission.EnterExclusiveAsync();
        int dispatches = 0, commits = 0;
        f.Catalog.BeforeWriteDispatchForTests = token => { Assert.Same(owner, token); dispatches++; };
        f.Catalog.AfterWriteCommitForTests = token => { Assert.Same(owner, token); commits++; };
        var outcome = await f.Commit(owner);
        Assert.Equal(ProfileExclusiveDisposition.SafeToResume, outcome.Disposition);
        Assert.Equal(NativeMediaImportCommitStatus.Committed, outcome.Result.Status);
        Assert.Equal(1, outcome.Result.NewPosts);
        Assert.False(outcome.Result.IsReplay);
        Assert.Equal(4, dispatches);
        Assert.Equal(4, commits);
        f.Catalog.EnsureTransferAdmission(owner); // Consumer never disposes borrowed authority.
        await f.Stage.DisposeAsync();
        foreach (string root in f.Plan.Roots.Select(r => r.Directory))
            foreach (string file in Directory.GetFiles(root, "*.original.bin")) File.Delete(file);
        Dictionary<string, string> before = Files(f.Files.Profile);
        var replay = await f.Catalog.CommitNativeMediaMergeAdmittedAsync(owner, f.Plan, new MediaStore(),
            new CancellationToken(true), beforeAdmission: () => throw new IOException("no replay callback"));
        Assert.Equal(ProfileExclusiveDisposition.SafeToResume, replay.Disposition);
        Assert.Equal(outcome.Result with { IsReplay = true }, replay.Result);
        Assert.Equal(4, dispatches);
        Assert.Equal(before, Files(f.Files.Profile));
        await f.Catalog.DisposeAsync();
        Assert.Equal(replay.Result, await f.Catalog.ReadNativeMediaMergeReceiptAsync(f.Plan.OperationId, f.Plan.RequestSha256));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => f.Commit(owner));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreclaimCancellationAndExplicitStaleAreIndependentlySafe(bool stale)
    {
        await using Fixture f = await Fixture.OpenAsync();
        if (stale) await f.Catalog.SetCaptureEnabledAsync(Id, true); // Ordinary mutation BEFORE Exclusive.
        using ProfileAdmissionToken owner = await f.Lease.Admission.EnterExclusiveAsync();
        var outcome = await f.Catalog.CommitNativeMediaMergeAdmittedAsync(owner, f.Plan, new MediaStore(),
            stale ? default : new CancellationToken(true));
        Assert.Equal(stale ? NativeMediaImportCommitStatus.StalePlan : NativeMediaImportCommitStatus.Inactive, outcome.Result.Status);
        Assert.Equal(ProfileExclusiveDisposition.SafeToResume, outcome.Disposition);
        Assert.Empty((await f.Catalog.ReadNativeMediaMergeOperationsPageAsync()).Items);
        Assert.False(Directory.Exists(f.Plan.OperationsParent));
    }

    [Fact]
    public async Task PreclaimReplacedInstancePreservesStaleAttemptButNeverAttestsSafeResume()
    {
        await using Fixture f = await Fixture.OpenAsync();
        using ProfileAdmissionToken owner = await f.Lease.Admission.EnterExclusiveAsync();
        Dictionary<string, string>? afterReplacement = null;
        int phases = 0;
        var outcome = await f.Catalog.CommitNativeMediaMergeAdmittedAsync(owner, f.Plan, new MediaStore(),
            beforeAdmission: () =>
            {
                // Preserve generation: the identity check alone must reject before claim.
                Execute(f, "UPDATE archive_import_fence SET instance='90505f59-c8f8-45b2-8a58-e48d4746b578' WHERE singleton=1");
                afterReplacement = Files(f.Files.Profile);
            }, checkpoint: _ => phases++);
        Assert.Equal(NativeMediaImportCommitStatus.StalePlan, outcome.Result.Status);
        Assert.Null(outcome.Result.Diagnostic);
        Assert.Equal(ProfileExclusiveDisposition.RecoveryRequired, outcome.Disposition);
        Assert.Equal(0, phases);
        Assert.False(Directory.Exists(f.Plan.OperationsParent));
        Assert.Empty((await f.Catalog.ReadNativeMediaMergeOperationsPageAsync()).Items);
        Assert.Null(await f.Catalog.ReadNativeMediaMergeReceiptAsync(f.Plan.OperationId, f.Plan.RequestSha256));
        Assert.NotNull(afterReplacement);
        Assert.Equal(afterReplacement, Files(f.Files.Profile));
        using SqliteConnection connection = ArchiveSql.OpenConnection(f.Catalog.DatabasePath, SqliteOpenMode.ReadOnly);
        var fence = ArchiveSql.ReadImportFence(connection, null);
        Assert.NotEqual(f.Plan.Instance, fence.Instance);
        Assert.Equal(f.Plan.Generation, fence.Generation);
        f.Catalog.EnsureTransferAdmission(owner);
    }

    [Theory]
    [InlineData("PreparingSaved", false)]
    [InlineData("OperationsParentCreated", false)]
    [InlineData("CandidateDirectoryCreated", false)]
    [InlineData("CandidateReady", false)]
    [InlineData("PreparedSaved", false)]
    [InlineData("CandidateVerified", false)]
    [InlineData("BeforeSqlCommit", false)]
    [InlineData("PreparingSaved", true)]
    [InlineData("CandidateReady", true)]
    [InlineData("PreparedSaved", true)]
    [InlineData("BeforeSqlCommit", true)]
    public async Task PhaseFaultOrCancellationRetainsClaimAndNeverAttestsRollback(string phaseName, bool cancel)
    {
        await using Fixture f = await Fixture.OpenAsync();
        using ProfileAdmissionToken owner = await f.Lease.Admission.EnterExclusiveAsync();
        using CancellationTokenSource cancellation = new();
        NativeMediaImportPhase phase = Enum.Parse<NativeMediaImportPhase>(phaseName);
        var outcome = await f.Catalog.CommitNativeMediaMergeAdmittedAsync(owner, f.Plan, new MediaStore(),
            cancellation.Token, checkpoint: reached =>
            {
                if (reached != phase) return;
                if (cancel) cancellation.Cancel();
                else throw new IOException("synthetic phase loss");
            });
        Assert.Equal(ProfileExclusiveDisposition.RecoveryRequired, outcome.Disposition);
        Assert.Equal(NativeMediaImportCommitStatus.ReconciliationRequired, outcome.Result.Status);
        Assert.Null(await f.Catalog.ReadNativeMediaMergeReceiptAsync(f.Plan.OperationId, f.Plan.RequestSha256));
        NativeMediaMergeOperation operation = Assert.Single((await f.Catalog.ReadNativeMediaMergeOperationsPageAsync()).Items);
        bool prepared = phaseName is "PreparedSaved" or "CandidateVerified" or "BeforeSqlCommit";
        Assert.Equal(prepared ? NativeMediaImportOperationState.Prepared : NativeMediaImportOperationState.Preparing, operation.State);
        Assert.Equal(phaseName != "PreparingSaved", Directory.Exists(f.Plan.OperationsParent));
        await AssertTargetUnchanged(f);
        AssertReleasedRoots(f, operation);
        Dictionary<string, string> before = Files(f.Files.Profile);
        var retry = await f.Commit(owner);
        Assert.Equal(ProfileExclusiveDisposition.RecoveryRequired, retry.Disposition);
        Assert.Equal(before, Files(f.Files.Profile)); // Never retries materialization.
        f.Catalog.EnsureTransferAdmission(owner);
    }

    [Theory]
    [InlineData(2)] // Preparing actually committed, its return lost.
    [InlineData(3)] // Prepared actually committed, its return lost.
    [InlineData(4)] // Content + receipt actually committed, its return lost.
    public async Task LostActualCommitReturnUsesDurableEvidence(int lostCommit)
    {
        await using Fixture f = await Fixture.OpenAsync();
        using ProfileAdmissionToken owner = await f.Lease.Admission.EnterExclusiveAsync();
        int commits = 0;
        f.Catalog.AfterWriteCommitForTests = token =>
        {
            Assert.Same(owner, token);
            if (++commits == lostCommit) throw new IOException("synthetic commit return loss");
        };
        var outcome = await f.Commit(owner);
        Assert.Equal(lostCommit, commits);
        Assert.Equal(lostCommit == 4 ? ProfileExclusiveDisposition.SafeToResume : ProfileExclusiveDisposition.RecoveryRequired, outcome.Disposition);
        Assert.Equal(lostCommit == 4 ? NativeMediaImportCommitStatus.Committed : NativeMediaImportCommitStatus.ReconciliationRequired, outcome.Result.Status);
        NativeMediaMergeOperation operation = Assert.Single((await f.Catalog.ReadNativeMediaMergeOperationsPageAsync()).Items);
        Assert.Equal(lostCommit == 2 ? NativeMediaImportOperationState.Preparing
            : lostCommit == 3 ? NativeMediaImportOperationState.Prepared : NativeMediaImportOperationState.Committed, operation.State);
        Assert.Equal(lostCommit == 4, await f.Catalog.ReadNativeMediaMergeReceiptAsync(f.Plan.OperationId, f.Plan.RequestSha256) != null);
        AssertReleasedRoots(f, operation);
        f.Catalog.EnsureTransferAdmission(owner);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NormalFinalStaleRetainsPreparedAndRequiresRecovery(bool replaceInstance)
    {
        await using Fixture f = await Fixture.OpenAsync();
        using ProfileAdmissionToken owner = await f.Lease.Admission.EnterExclusiveAsync();
        var outcome = await f.Catalog.CommitNativeMediaMergeAdmittedAsync(owner, f.Plan, new MediaStore(), checkpoint: phase =>
        {
            if (phase == NativeMediaImportPhase.CandidateVerified)
                Execute(f, replaceInstance
                    ? "UPDATE archive_import_fence SET instance='90505f59-c8f8-45b2-8a58-e48d4746b578' WHERE singleton=1"
                    : "UPDATE archive_import_fence SET generation=generation+1 WHERE singleton=1");
        });
        Assert.Equal(NativeMediaImportCommitStatus.StalePlan, outcome.Result.Status);
        Assert.Null(outcome.Result.Diagnostic); // Completed rejection, not a failed/unknown attempt.
        Assert.Equal(ProfileExclusiveDisposition.RecoveryRequired, outcome.Disposition);
        NativeMediaMergeOperation operation = Assert.Single((await f.Catalog.ReadNativeMediaMergeOperationsPageAsync()).Items);
        Assert.Equal(NativeMediaImportOperationState.Prepared, operation.State);
        Assert.True(Directory.Exists(operation.FullDirectory));
        Assert.Null(await f.Catalog.ReadNativeMediaMergeReceiptAsync(f.Plan.OperationId, f.Plan.RequestSha256));
        await AssertTargetUnchanged(f);
        AssertReleasedRoots(f, operation);
    }

    [Theory]
    [InlineData("missing-claim")]
    [InlineData("replaced-instance")]
    [InlineData("unreadable-receipt")]
    [InlineData("committed-without-receipt")]
    public async Task NegativeProofNeverUsesBareAbsenceOrAChangedDatabase(string mode)
    {
        await using Fixture f = await Fixture.OpenAsync();
        using ProfileAdmissionToken owner = await f.Lease.Admission.EnterExclusiveAsync();
        var outcome = await f.Catalog.CommitNativeMediaMergeAdmittedAsync(owner, f.Plan, new MediaStore(),
            beforeAdmission: () =>
            {
                if (mode != "replaced-instance") return;
                Execute(f, "UPDATE archive_import_fence SET instance='90505f59-c8f8-45b2-8a58-e48d4746b578' WHERE singleton=1");
                throw new IOException("synthetic identity replacement");
            }, checkpoint: phase =>
            {
                if (mode == "missing-claim" && phase == NativeMediaImportPhase.OperationsParentCreated)
                {
                    Execute(f, "DELETE FROM archive_media_merge_operations");
                    throw new IOException("synthetic disappeared claim");
                }
                if (mode == "unreadable-receipt" && phase == NativeMediaImportPhase.CandidateReady)
                {
                    Execute(f, "ALTER TABLE archive_media_merge_receipts RENAME TO synthetic_hidden_receipts");
                    throw new IOException("synthetic unreadable evidence");
                }
                if (mode == "committed-without-receipt" && phase == NativeMediaImportPhase.SqlCommitted)
                {
                    Execute(f, "DELETE FROM archive_media_merge_receipts");
                    throw new IOException("synthetic missing receipt");
                }
            });
        Assert.Equal(NativeMediaImportCommitStatus.Unknown, outcome.Result.Status);
        Assert.Equal(ProfileExclusiveDisposition.RecoveryRequired, outcome.Disposition);
        f.Catalog.EnsureTransferAdmission(owner);
    }

    [Fact]
    public async Task HistoricalReceiptWinsButSeparateUnresolvedMergeStillBlocksResume()
    {
        await using Fixture f = await Fixture.OpenAsync();
        using ProfileAdmissionToken owner = await f.Lease.Admission.EnterExclusiveAsync();
        var committed = await f.Commit(owner);
        NativeMediaMergePlan next = Assert.IsType<NativeMediaMergePlan>((await f.Catalog.PrepareNativeMediaMergeAsync(f.Stage)).Plan);
        var unresolved = await f.Catalog.CommitNativeMediaMergeAdmittedAsync(owner, next, new MediaStore(), checkpoint: phase =>
        {
            if (phase == NativeMediaImportPhase.PreparingSaved) throw new IOException("synthetic unfinished next operation");
        });
        Assert.Equal(ProfileExclusiveDisposition.RecoveryRequired, unresolved.Disposition);
        await f.Stage.DisposeAsync();
        Dictionary<string, string> before = Files(f.Files.Profile);
        var replay = await f.Commit(owner);
        Assert.Equal(committed.Result with { IsReplay = true }, replay.Result);
        Assert.Equal(ProfileExclusiveDisposition.RecoveryRequired, replay.Disposition);
        Assert.Equal(before, Files(f.Files.Profile));
    }

    [Fact]
    public async Task OtherFamilyUnresolvedImportBlocksHistoricalMergeReceipt()
    {
        await using Fixture f = await Fixture.OpenAsync();
        using ProfileAdmissionToken owner = await f.Lease.Admission.EnterExclusiveAsync();
        var committed = await f.Commit(owner);
        NativeTransferCatalog other = Content(legacy: true);
        other = other with { Threads = [other.Threads[0] with { Id = new(Id.Source, Id.Board, "7002"), PageUrl = "https://boards.4chan.org/b/thread/7002" }] };
        await using NativeTransferImportStage stage = await Stage(f.Files, other);
        NativeMediaImportPlan plan = Assert.IsType<NativeMediaImportPlan>((await f.Catalog.PrepareNativeMediaImportAsync(stage)).Plan);
        var unfinished = await f.Catalog.CommitNativeMediaImportAdmittedAsync(owner, plan, new MediaStore(), checkpoint: phase =>
        {
            if (phase == NativeMediaImportPhase.PreparingSaved) throw new IOException("synthetic other-family claim");
        });
        Assert.Equal(ProfileExclusiveDisposition.RecoveryRequired, unfinished.Disposition);
        Assert.Equal(NativeMediaImportOperationState.Preparing,
            (await f.Catalog.ReadNativeMediaImportOperationsPageAsync()).Items.Single(o => o.OperationId == plan.OperationId).State);
        var replay = await f.Commit(owner);
        Assert.Equal(committed.Result with { IsReplay = true }, replay.Result);
        Assert.Equal(ProfileExclusiveDisposition.RecoveryRequired, replay.Disposition);
    }

    [Fact]
    public async Task ValidReceiptInReplacedInstanceRemainsHistoryButCannotAuthorizeResume()
    {
        await using Fixture f = await Fixture.OpenAsync();
        using ProfileAdmissionToken owner = await f.Lease.Admission.EnterExclusiveAsync();
        var committed = await f.Commit(owner);
        Execute(f, "UPDATE archive_import_fence SET instance='90505f59-c8f8-45b2-8a58-e48d4746b578' WHERE singleton=1");
        var replay = await f.Commit(owner);
        Assert.Equal(committed.Result with { IsReplay = true }, replay.Result);
        Assert.Equal(ProfileExclusiveDisposition.RecoveryRequired, replay.Disposition);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PostCommitExceptionReconcilesEvenAfterCatalogDisposal(bool disposeCatalog)
    {
        await using Fixture f = await Fixture.OpenAsync();
        using ProfileAdmissionToken owner = await f.Lease.Admission.EnterExclusiveAsync();
        var outcome = await f.Catalog.CommitNativeMediaMergeAdmittedAsync(owner, f.Plan, new MediaStore(), checkpoint: phase =>
        {
            if (phase != NativeMediaImportPhase.SqlCommitted) return;
            if (disposeCatalog) f.Catalog.DisposeAsync().GetAwaiter().GetResult();
            throw new IOException("synthetic lost success");
        });
        Assert.Equal(NativeMediaImportCommitStatus.Committed, outcome.Result.Status);
        Assert.Equal(ProfileExclusiveDisposition.SafeToResume, outcome.Disposition);
        Assert.Equal(1, outcome.Result.NewPosts);
        Assert.Equal(nameof(IOException), outcome.Result.Diagnostic);
        Assert.True(f.Lease.RunPrivateStoreCommit(owner, () => true));
        AssertReleasedRoots(f, Assert.Single((await f.Catalog.ReadNativeMediaMergeOperationsPageAsync()).Items));
    }

    [Fact]
    public async Task CompleteConsumerKeepsBorrowedExclusiveAndAllRootLeasesAcrossPreparedGate()
    {
        await using Fixture f = await Fixture.OpenAsync();
        using ProfileAdmissionToken owner = await f.Lease.Admission.EnterExclusiveAsync();
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<ProfileExclusiveOutcome<NativeMediaMergeResult>> work = Task.Run(() => f.Catalog.CommitNativeMediaMergeAdmittedAsync(
            owner, f.Plan, new MediaStore(), checkpoint: phase =>
            {
                if (phase != NativeMediaImportPhase.PreparedSaved) return;
                entered.SetResult();
                release.Task.GetAwaiter().GetResult(); // Test-only synchronous seam, outside SQL/lease monitors.
            }));
        try
        {
            await entered.Task.WaitAsync(Deadline);
            Assert.False(work.IsCompleted);
            Assert.Throws<InvalidOperationException>(() => { _ = f.Catalog.SetCaptureEnabledAsync(Id, true); });
            foreach (var root in f.Plan.Roots)
                Assert.Throws<IOException>(() =>
                {
                    using FileStream held = new(Path.Combine(root.Directory, ".meowchan-store.lock"),
                        FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                });
            f.Lease.Admission.BeginShutdown();
            Task drain = f.Lease.Admission.DrainAsync();
            Assert.False(drain.IsCompleted);
            release.SetResult();
            var outcome = await work.WaitAsync(Deadline);
            Assert.Equal(NativeMediaImportCommitStatus.Committed, outcome.Result.Status);
            Assert.Equal(ProfileExclusiveDisposition.SafeToResume, outcome.Disposition);
            Assert.False(drain.IsCompleted); // Operation completion does not release its caller's owner.
            AssertReleasedRoots(f, Assert.Single((await f.Catalog.ReadNativeMediaMergeOperationsPageAsync()).Items));
            owner.Dispose();
            await drain.WaitAsync(Deadline);
        }
        finally { release.TrySetResult(); await work.WaitAsync(Deadline); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EmptySafeAndFullSafeAliasKeepTheirOriginalCandidateProtocol(bool alias)
    {
        await using Fixture f = await Fixture.OpenAsync();
        NativeTransferCatalog content = Content();
        TransferAttachment attachment = Attachment(postId: "17");
        TransferPost added = new(new("17", 2, PostFieldPresence.Html | PostFieldPresence.Attachments,
            Html: "new-media", Attachments: [attachment.Metadata]), [attachment]);
        await using NativeTransferImportStage stage = await Stage(f.Files,
            content with { Threads = [content.Threads[0] with { Posts = [added] }] });
        NativeMediaMergePlan plan = alias
            ? Assert.IsType<NativeMediaMergePlan>((await f.Catalog.PrepareNativeMediaMergeAsync(stage)).Plan) : f.Plan;
        Assert.Equal(alias, plan.ReuseFullAsSafe);
        Assert.Equal(!alias, plan.SafeKeys.IsEmpty);
        using ProfileAdmissionToken owner = await f.Lease.Admission.EnterExclusiveAsync();
        var outcome = await f.Catalog.CommitNativeMediaMergeAdmittedAsync(owner, plan, new MediaStore());
        Assert.Equal(NativeMediaImportCommitStatus.Committed, outcome.Result.Status);
        Assert.Equal(ProfileExclusiveDisposition.SafeToResume, outcome.Disposition);
        Assert.Equal(alias ? 1 : 0, outcome.Result.NewBindings);
        NativeMediaMergeOperation operation = Assert.Single((await f.Catalog.ReadNativeMediaMergeOperationsPageAsync()).Items);
        Assert.Single(Directory.GetDirectories(plan.OperationsParent));
        if (alias) Assert.Equal(operation.FullDirectory, operation.SafeDirectory);
        else Assert.Null(operation.SafeDirectory);
        AssertReleasedRoots(f, operation);
    }

    [Fact]
    public async Task ChangedCandidateBytesCannotPublishAndLeavePreparedEvidence()
    {
        await using Fixture f = await Fixture.OpenAsync();
        using ProfileAdmissionToken owner = await f.Lease.Admission.EnterExclusiveAsync();
        var outcome = await f.Catalog.CommitNativeMediaMergeAdmittedAsync(owner, f.Plan, new MediaStore(), checkpoint: phase =>
        {
            if (phase == NativeMediaImportPhase.CandidateReady)
                File.WriteAllBytes(Assert.Single(Directory.GetFiles(f.Plan.OperationsParent, "*.original.bin",
                    SearchOption.AllDirectories)), [1, 2, 3]);
        });
        Assert.Equal(NativeMediaImportCommitStatus.ReconciliationRequired, outcome.Result.Status);
        Assert.Equal(ProfileExclusiveDisposition.RecoveryRequired, outcome.Disposition);
        NativeMediaMergeOperation operation = Assert.Single((await f.Catalog.ReadNativeMediaMergeOperationsPageAsync()).Items);
        Assert.Equal(NativeMediaImportOperationState.Prepared, operation.State);
        Assert.Null(await f.Catalog.ReadNativeMediaMergeReceiptAsync(f.Plan.OperationId, f.Plan.RequestSha256));
        await AssertTargetUnchanged(f);
        AssertReleasedRoots(f, operation);
    }

    [Fact]
    public async Task PassiveAdmittedBoundariesRejectBeforeTouchingPathsOrCallback()
    {
        await using Fixture f = await Fixture.OpenAsync();
        using ProfileAdmissionToken ordinary = f.Lease.Admission.EnterOrdinaryWriter();
        MediaStore store = new();
        Dictionary<string, string> before = Files(f.Files.Profile);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.MaterializePassiveMergeCandidateAdmittedAsync(ordinary, f.Plan, false));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.WithPassiveMergeTargetRootsAdmittedAsync<bool>(ordinary, f.Plan,
            _ => throw new IOException("must not enter")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.WithVerifiedPassiveMergeCandidatesAdmittedAsync(ordinary,
            f.Plan, null!, null, () => Task.FromResult(true)));
        Assert.Equal(before, Files(f.Files.Profile));
        // Preparation's raw read-only helper remains usable without Exclusive authority.
        int roots = await store.WithPassiveMergeTargetRootsAsync(f.Plan.Roots.Select(r => r.Directory).ToArray(),
            snapshots => Task.FromResult(snapshots.Length));
        Assert.Equal(f.Plan.Roots.Length, roots);
    }

    [Fact]
    public async Task AwaitedRootCallbackRetainsLocksAndOwnerUntilItsRealCompletion()
    {
        await using Fixture f = await Fixture.OpenAsync();
        using ProfileAdmissionToken owner = await f.Lease.Admission.EnterExclusiveAsync();
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<int> work = new MediaStore().WithPassiveMergeTargetRootsAdmittedAsync(owner, f.Plan, async snapshots =>
        {
            entered.SetResult();
            await release.Task;
            f.Catalog.EnsureTransferAdmission(owner);
            return snapshots.Length;
        });
        try
        {
            await entered.Task.WaitAsync(Deadline);
            Assert.False(work.IsCompleted);
            foreach (var root in f.Plan.Roots)
                Assert.Throws<IOException>(() =>
                {
                    using FileStream held = new(Path.Combine(root.Directory, ".meowchan-store.lock"),
                        FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                });
            Assert.Throws<InvalidOperationException>(() => { _ = f.Catalog.SetCaptureEnabledAsync(Id, true); });
            f.Lease.Admission.BeginShutdown();
            Task drain = f.Lease.Admission.DrainAsync();
            Assert.False(drain.IsCompleted);
            release.SetResult();
            Assert.Equal(f.Plan.Roots.Length, await work.WaitAsync(Deadline));
            AssertReleasedRoots(f, null);
            Assert.False(drain.IsCompleted); // Borrowed token still belongs to this owner.
            owner.Dispose();
            await drain.WaitAsync(Deadline);
        }
        finally { release.TrySetResult(); await work.WaitAsync(Deadline); }
    }

    private static async Task AssertTargetUnchanged(Fixture f)
    {
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(f.Files.Profile);
        var view = Assert.IsType<ArchivedThreadView>(await reader.ReadThreadAsync(Id));
        Assert.Equal(2, view.PostCount);
        Assert.Single(view.MediaDirectories);
    }

    private static void AssertReleasedRoots(Fixture f, NativeMediaMergeOperation? operation)
    {
        foreach (string root in f.Plan.Roots.Select(r => r.Directory)
            .Concat(new[] { operation?.FullDirectory, operation?.SafeDirectory }.OfType<string>()).Distinct())
        {
            using FileStream released = new(Path.Combine(root, ".meowchan-store.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }
    }

    private static void Execute(Fixture f, string sql)
    {
        // Deliberate corruption seam, not a product writer bypass or schedule model.
        using SqliteConnection connection = ArchiveSql.OpenConnection(f.Catalog.DatabasePath, SqliteOpenMode.ReadWrite);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private sealed class Fixture : IAsyncDisposable
    {
        internal Workspace Files { get; } = new();
        internal ProfileLease Lease { get; private set; } = null!;
        internal ArchiveCatalog Catalog { get; private set; } = null!;
        internal NativeTransferImportStage Stage { get; private set; } = null!;
        internal NativeMediaMergePlan Plan { get; private set; } = null!;

        internal static async Task<Fixture> OpenAsync(bool ready = true)
        {
            Fixture f = new();
            try
            {
                // Seed via the real admitted W27 import, then reopen to exercise either
                // legitimate Starting or Ready storage-owner admission independently.
                using (ProfileLease seedLease = ProfileLease.Acquire(f.Files.Profile))
                {
                    await using ArchiveCatalog seed = await ArchiveCatalog.OpenAsync(f.Files.Profile, seedLease);
                    await using NativeTransferImportStage stage = await NativeMediaImportTests.Stage(f.Files, Content());
                    NativeMediaImportPlan plan = (await seed.PrepareNativeMediaImportAsync(stage)).Plan!;
                    seedLease.Admission.CompleteStartup();
                    using ProfileAdmissionToken owner = await seedLease.Admission.EnterExclusiveAsync();
                    var imported = await seed.CommitNativeMediaImportAdmittedAsync(owner, plan, new MediaStore());
                    Assert.Equal(ProfileExclusiveDisposition.SafeToResume, imported.Disposition);
                    Assert.Equal(NativeMediaImportCommitStatus.Committed, imported.Result.Status);
                }
                f.Lease = ProfileLease.Acquire(f.Files.Profile);
                f.Catalog = await ArchiveCatalog.OpenAsync(f.Files.Profile, f.Lease);
                NativeTransferCatalog content = Content();
                TransferThread thread = content.Threads[0];
                TransferPost added = new(new("17", 2, PostFieldPresence.Html, Html: "merge-admission-new"),
                    ImmutableArray<TransferAttachment>.Empty);
                f.Stage = await NativeMediaImportTests.Stage(f.Files, content with { Threads = [thread with { Posts = thread.Posts.Add(added) }] });
                f.Plan = Assert.IsType<NativeMediaMergePlan>((await f.Catalog.PrepareNativeMediaMergeAsync(f.Stage)).Plan);
                if (ready) f.Lease.Admission.CompleteStartup();
                return f;
            }
            catch { await f.DisposeAsync(); throw; }
        }

        internal Task<ProfileExclusiveOutcome<NativeMediaMergeResult>> Commit(ProfileAdmissionToken owner)
            => Catalog.CommitNativeMediaMergeAdmittedAsync(owner, Plan, new MediaStore());

        public async ValueTask DisposeAsync()
        {
            if (Stage != null) await Stage.DisposeAsync();
            if (Catalog != null) await Catalog.DisposeAsync();
            Lease?.Dispose();
            Files.Dispose();
        }
    }
}
