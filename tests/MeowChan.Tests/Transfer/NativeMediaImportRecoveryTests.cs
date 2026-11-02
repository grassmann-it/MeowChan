using MeowChan.Archive;
using MeowChan.Persistence;
using MeowChan.Storage;
using MeowChan.Transfer;
using Microsoft.Data.Sqlite;
using static MeowChan.Tests.Transfer.NativeMediaImportTests;

namespace MeowChan.Tests.Transfer;

// Coordinator-only failures. Every operation starts with the real package writer and owned
// stage; SQL is read for evidence, never seeded. Generic candidate-file and happy-path
// reader/resolver/provenance tests live elsewhere. No network or personal profile is used.
public sealed class NativeMediaImportRecoveryTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(15);

    [Fact]
    public async Task PreparingIsDurableBeforeFirstOperationsMkdirAndDiscoverableAfterRestart()
    {
        using Workspace files = new();
        Guid operation; string request, parent;
        using (ProfileLease lease = ProfileLease.Acquire(files.Profile))
        {
            await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(files.Profile, lease);
            await using NativeTransferImportStage stage = await Stage(files, Content());
            NativeMediaImportPlan plan = await Plan(catalog, stage);
            operation = plan.OperationId; request = plan.RequestSha256; parent = plan.OperationsParent;
            string? durableState = null; bool parentAlreadyExists = true; int calls = 0;
            var result = await catalog.CommitNativeMediaImportAsync(plan, new MediaStore(), checkpoint: phase =>
            {
                if (phase != NativeMediaImportPhase.PreparingSaved) return;
                calls++;
                // Independent connection, while the first commit is paused BEFORE mkdir.
                durableState = OperationState(catalog.DatabasePath, operation);
                parentAlreadyExists = Directory.Exists(parent);
                throw new IOException("synthetic loss immediately after durable Preparing");
            });
            Assert.Equal(1, calls);
            Assert.Equal("Preparing", durableState);
            Assert.False(parentAlreadyExists);
            Assert.False(Directory.Exists(parent));
            Assert.Equal(NativeMediaImportCommitStatus.Inactive, result.Status);
            Assert.Equal(nameof(IOException), result.Diagnostic);
            await AssertNoPublication(catalog);
        }
        Assert.Empty(Directory.GetFiles(files.StageRoot));
        using ProfileLease restartedLease = ProfileLease.Acquire(files.Profile);
        await using ArchiveCatalog restarted = await ArchiveCatalog.OpenAsync(files.Profile, restartedLease);
        NativeMediaImportOperation found = await OnlyOperation(restarted, operation, request, NativeMediaImportOperationState.Preparing);
        Assert.Equal(parent, found.OperationsParent);
        Assert.Null(found.CandidateDirectory);
        var inspection = await restarted.InspectNativeMediaImportOperationAsync(operation, request, new MediaStore());
        Assert.Equal(NativeMediaImportFilesystemStatus.MissingParent, inspection.FilesystemStatus);
        Assert.Empty(inspection.Children);
        Assert.Null(inspection.Receipt);
        Assert.Null(await restarted.ReadNativeMediaImportReceiptAsync(operation, request));
        Assert.False(Directory.Exists(parent)); // Discovery neither repairs nor restarts materialization.
        await AssertNoPublication(restarted);
    }

    [Fact]
    public async Task CancellationAfterParentMkdirLeavesDiscoverableEmptyParentWithoutReplacementAttempt()
    {
        using Workspace files = new();
        Guid operation; string request, parent;
        using (ProfileLease lease = ProfileLease.Acquire(files.Profile))
        {
            await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(files.Profile, lease);
            await using NativeTransferImportStage stage = await Stage(files, Content());
            NativeMediaImportPlan plan = await Plan(catalog, stage);
            operation = plan.OperationId; request = plan.RequestSha256; parent = plan.OperationsParent;
            using CancellationTokenSource cancellation = new();
            List<NativeMediaImportPhase> seen = [];
            var result = await catalog.CommitNativeMediaImportAsync(plan, new MediaStore(), cancellation.Token, phase =>
            {
                seen.Add(phase);
                if (phase == NativeMediaImportPhase.OperationsParentCreated) cancellation.Cancel();
            });
            Assert.Equal(new[] { NativeMediaImportPhase.PreparingSaved, NativeMediaImportPhase.OperationsParentCreated }, seen);
            Assert.Equal(NativeMediaImportCommitStatus.Inactive, result.Status);
            Assert.True(Directory.Exists(parent));
            Assert.Empty(Directory.EnumerateFileSystemEntries(parent));
            await AssertRefusesRetry(catalog, plan);
            await AssertNoPublication(catalog);
        }
        using ProfileLease restartedLease = ProfileLease.Acquire(files.Profile);
        await using ArchiveCatalog restarted = await ArchiveCatalog.OpenAsync(files.Profile, restartedLease);
        await OnlyOperation(restarted, operation, request, NativeMediaImportOperationState.Preparing);
        var inspection = await restarted.InspectNativeMediaImportOperationAsync(operation, request, new MediaStore());
        Assert.Equal(NativeMediaImportFilesystemStatus.EmptyParent, inspection.FilesystemStatus);
        Assert.Empty(inspection.Children);
        Assert.Null(inspection.Receipt);
        Assert.Empty(Directory.EnumerateFileSystemEntries(parent));
        await AssertNoPublication(restarted);
    }

    [Fact]
    public async Task CrashAfterRealCandidateMkdirBeforeLeaseOrJournalIsDiscoverableWithoutRepairAfterRestart()
    {
        using Workspace files = new();
        Guid operation; string request, parent, child; Tree retained;
        using (ProfileLease lease = ProfileLease.Acquire(files.Profile))
        {
            await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(files.Profile, lease);
            await using NativeTransferImportStage stage = await Stage(files, Content());
            NativeMediaImportPlan plan = await Plan(catalog, stage);
            operation = plan.OperationId; request = plan.RequestSha256; parent = plan.OperationsParent;
            List<NativeMediaImportPhase> seen = [];
            string? observedChild = null, durableState = null;
            bool emptyAtCheckpoint = false;
            var result = await catalog.CommitNativeMediaImportAsync(plan, new MediaStore(), checkpoint: phase =>
            {
                seen.Add(phase);
                if (phase != NativeMediaImportPhase.CandidateDirectoryCreated) return;
                // The producer itself created this child. Do not manufacture the crash residue
                // by making directories or deleting a journal/lock after a later transition.
                observedChild = Candidate(parent);
                emptyAtCheckpoint = !Directory.EnumerateFileSystemEntries(observedChild).Any();
                durableState = OperationState(catalog.DatabasePath, operation);
                throw new IOException("synthetic crash after real child mkdir, before lease or journal");
            });
            Assert.Equal(new[] { NativeMediaImportPhase.PreparingSaved, NativeMediaImportPhase.OperationsParentCreated,
                NativeMediaImportPhase.CandidateDirectoryCreated }, seen);
            Assert.Equal("Preparing", durableState);
            Assert.True(emptyAtCheckpoint);
            Assert.NotNull(observedChild);
            child = observedChild;
            Assert.Equal(child, Candidate(parent));
            Assert.Equal(NativeMediaImportCommitStatus.Inactive, result.Status);
            Assert.Equal(nameof(IOException), result.Diagnostic);
            Assert.Empty(Directory.EnumerateFileSystemEntries(child)); // No lease, journal or payload.
            var journal = await OnlyOperation(catalog, operation, request, NativeMediaImportOperationState.Preparing);
            Assert.Equal(parent, journal.OperationsParent);
            Assert.Null(journal.CandidateDirectory); Assert.Null(journal.ManifestGeneration);
            Assert.Null(await catalog.ReadNativeMediaImportReceiptAsync(operation, request));
            retained = Tree.Capture(parent);
            await AssertNoPublication(catalog);
        }
        Assert.Empty(Directory.GetFiles(files.StageRoot));
        using ProfileLease restartedLease = ProfileLease.Acquire(files.Profile);
        await using ArchiveCatalog restarted = await ArchiveCatalog.OpenAsync(files.Profile, restartedLease);
        // Rediscover identity from durable SQL, with neither the original stage nor issued plan.
        var discovered = Assert.Single((await restarted.ReadNativeMediaImportOperationsPageAsync()).Items);
        Assert.Equal(operation, discovered.OperationId); Assert.Equal(request, discovered.RequestSha256);
        Assert.Equal(parent, discovered.OperationsParent);
        for (int observation = 0; observation < 2; observation++)
        {
            var inspection = await restarted.InspectNativeMediaImportOperationAsync(
                discovered.OperationId, discovered.RequestSha256, new MediaStore());
            Assert.NotNull(inspection.Operation);
            Assert.Equal(NativeMediaImportOperationState.Preparing, inspection.Operation.State);
            Assert.Equal(NativeMediaImportFilesystemStatus.Inspected, inspection.FilesystemStatus);
            var residue = Assert.Single(inspection.Children);
            Assert.Equal(child, residue.Path);
            Assert.True(residue.IsEmpty); Assert.True(residue.JournalMissing);
            Assert.Equal(PassiveMediaCandidateStatus.Invalid, residue.Status);
            Assert.False(residue.MatchesOperation); // Associated residue is not verified Ready authority.
            Assert.Null(inspection.Receipt);
            Assert.Null(await restarted.ReadNativeMediaImportReceiptAsync(operation, request));
            retained.AssertUnchanged(parent); // No lock/journal repair, cleanup or replacement child.
        }
        await OnlyOperation(restarted, operation, request, NativeMediaImportOperationState.Preparing);
        Assert.Equal(child, Candidate(parent));
        Assert.Single(Directory.GetDirectories(Path.GetDirectoryName(parent)!));
        await AssertNoPublication(restarted);
    }

    [Fact]
    public async Task RealPayloadRenameFailureRetainsIncompleteChildAcrossRestartWithoutActivatingIt()
    {
        using Workspace files = new();
        Guid operation; string request, parent; Tree retained;
        using (ProfileLease lease = ProfileLease.Acquire(files.Profile))
        {
            await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(files.Profile, lease);
            await using NativeTransferImportStage stage = await Stage(files, Content());
            NativeMediaImportPlan plan = await Plan(catalog, stage);
            operation = plan.OperationId; request = plan.RequestSha256; parent = plan.OperationsParent;
            int failedRenames = 0;
            MediaStore failingStore = new((source, destination, overwrite) =>
            {
                File.Move(source, destination, overwrite);
                if (destination.EndsWith(".original.bin", StringComparison.Ordinal))
                {
                    failedRenames++;
                    throw new IOException("synthetic disk failure after actual payload rename");
                }
            });
            var result = await catalog.CommitNativeMediaImportAsync(plan, failingStore);
            Assert.Equal(1, failedRenames);
            Assert.Equal(NativeMediaImportCommitStatus.Inactive, result.Status);
            string child = Candidate(parent);
            Assert.Equal(Bytes, File.ReadAllBytes(Assert.Single(Directory.GetFiles(child, "*.original.bin"))));
            retained = Tree.Capture(parent);
            var inspection = await catalog.InspectNativeMediaImportOperationAsync(operation, request, new MediaStore());
            Assert.Equal(PassiveMediaCandidateStatus.Incomplete, Assert.Single(inspection.Children).Status);
            await AssertRefusesRetry(catalog, plan);
            retained.AssertUnchanged(parent);
            await AssertNoPublication(catalog);
        }
        using ProfileLease restartedLease = ProfileLease.Acquire(files.Profile);
        await using ArchiveCatalog restarted = await ArchiveCatalog.OpenAsync(files.Profile, restartedLease);
        await OnlyOperation(restarted, operation, request, NativeMediaImportOperationState.Preparing);
        var recovered = await restarted.InspectNativeMediaImportOperationAsync(operation, request, new MediaStore());
        Assert.Equal(NativeMediaImportFilesystemStatus.Inspected, recovered.FilesystemStatus);
        var incomplete = Assert.Single(recovered.Children);
        Assert.Equal(PassiveMediaCandidateStatus.Incomplete, incomplete.Status);
        Assert.False(incomplete.MatchesOperation); // Incomplete is NOT a verified Ready DTO.
        Assert.Null(recovered.Receipt);
        retained.AssertUnchanged(parent);
        await AssertNoPublication(restarted);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailureOrCancellationAtCandidateReadyLeavesReadyBytesButOnlyPreparingSql(bool cancel)
    {
        using Workspace files = new();
        Guid operation; string request, parent; Tree retained;
        using (ProfileLease lease = ProfileLease.Acquire(files.Profile))
        {
            await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(files.Profile, lease);
            await using NativeTransferImportStage stage = await Stage(files, Content());
            NativeMediaImportPlan plan = await Plan(catalog, stage);
            operation = plan.OperationId; request = plan.RequestSha256; parent = plan.OperationsParent;
            using CancellationTokenSource cancellation = new();
            List<NativeMediaImportPhase> seen = [];
            var result = await catalog.CommitNativeMediaImportAsync(plan, new MediaStore(), cancellation.Token, phase =>
            {
                seen.Add(phase);
                if (phase != NativeMediaImportPhase.CandidateReady) return;
                if (cancel) cancellation.Cancel();
                else throw new IOException("synthetic loss after Ready, before Prepared SQL");
            });
            Assert.Equal(NativeMediaImportPhase.CandidateReady, seen.Last());
            Assert.Equal(NativeMediaImportCommitStatus.Inactive, result.Status);
            Assert.NotNull(result.Diagnostic);
            NativeMediaImportOperation journal = await OnlyOperation(catalog, operation, request, NativeMediaImportOperationState.Preparing);
            Assert.Null(journal.CandidateDirectory); Assert.Null(journal.ManifestGeneration);
            retained = Tree.Capture(parent);
            await AssertRefusesRetry(catalog, plan);
            retained.AssertUnchanged(parent);
            await AssertNoPublication(catalog);
        }
        using ProfileLease restartedLease = ProfileLease.Acquire(files.Profile);
        await using ArchiveCatalog restarted = await ArchiveCatalog.OpenAsync(files.Profile, restartedLease);
        await OnlyOperation(restarted, operation, request, NativeMediaImportOperationState.Preparing);
        var inspection = await restarted.InspectNativeMediaImportOperationAsync(operation, request, new MediaStore());
        var child = Assert.Single(inspection.Children);
        Assert.Equal(Candidate(parent), child.Path);
        Assert.Equal(PassiveMediaCandidateStatus.Ready, child.Status);
        Assert.True(child.MatchesOperation); // Discovery binds even a not-yet-SQL-bound Ready child.
        Assert.Null(inspection.Receipt);
        Assert.Null(await restarted.ReadNativeMediaImportReceiptAsync(operation, request));
        retained.AssertUnchanged(parent);
        await AssertNoPublication(restarted);
    }

    [Fact]
    public async Task CancellationAfterPreparedSqlDoesNotTurnPreparedIntoPublicationOrResumeAfterRestart()
    {
        using Workspace files = new();
        Guid operation; string request, parent; Tree retained;
        using (ProfileLease lease = ProfileLease.Acquire(files.Profile))
        {
            await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(files.Profile, lease);
            await using NativeTransferImportStage stage = await Stage(files, Content());
            NativeMediaImportPlan plan = await Plan(catalog, stage);
            operation = plan.OperationId; request = plan.RequestSha256; parent = plan.OperationsParent;
            using CancellationTokenSource cancellation = new();
            List<NativeMediaImportPhase> seen = [];
            var result = await catalog.CommitNativeMediaImportAsync(plan, new MediaStore(), cancellation.Token, phase =>
            {
                seen.Add(phase);
                if (phase == NativeMediaImportPhase.PreparedSaved) cancellation.Cancel();
            });
            Assert.Equal(NativeMediaImportPhase.PreparedSaved, seen.Last());
            Assert.Equal(NativeMediaImportCommitStatus.Inactive, result.Status);
            var journal = await OnlyOperation(catalog, operation, request, NativeMediaImportOperationState.Prepared);
            Assert.Equal(Candidate(parent), journal.CandidateDirectory);
            Assert.NotNull(journal.ManifestGeneration);
            retained = Tree.Capture(parent);
            await AssertRefusesRetry(catalog, plan);
            retained.AssertUnchanged(parent);
            await AssertNoPublication(catalog);
        }
        using ProfileLease restartedLease = ProfileLease.Acquire(files.Profile);
        await using ArchiveCatalog restarted = await ArchiveCatalog.OpenAsync(files.Profile, restartedLease);
        await OnlyOperation(restarted, operation, request, NativeMediaImportOperationState.Prepared);
        var inspection = await restarted.InspectNativeMediaImportOperationAsync(operation, request, new MediaStore());
        Assert.True(Assert.Single(inspection.Children).MatchesOperation);
        Assert.Null(inspection.Receipt);
        retained.AssertUnchanged(parent);
        await AssertNoPublication(restarted);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailureOrCancellationInsideFinalTransactionRollsBackAllContentRootReceiptAndFence(bool cancel)
    {
        using Workspace files = new(); using ProfileLease lease = ProfileLease.Acquire(files.Profile);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(files.Profile, lease);
        await using NativeTransferImportStage stage = await Stage(files, Content());
        NativeMediaImportPlan plan = await Plan(catalog, stage);
        using CancellationTokenSource cancellation = new();
        List<NativeMediaImportPhase> seen = [];
        Tree? ready = null;
        var result = await catalog.CommitNativeMediaImportAsync(plan, new MediaStore(), cancellation.Token, phase =>
        {
            seen.Add(phase);
            if (phase == NativeMediaImportPhase.PreparedSaved) ready = Tree.Capture(plan.OperationsParent);
            // This checkpoint is INSIDE WriteAsync, after insert-only content + FinishMediaImport.
            // Returning with a cancelled token must roll those writes back as well as throwing.
            if (phase != NativeMediaImportPhase.BeforeSqlCommit) return;
            if (cancel) cancellation.Cancel();
            else throw new IOException("synthetic final SQL transaction failure");
        });
        Assert.Equal(NativeMediaImportPhase.BeforeSqlCommit, seen.Last());
        Assert.Equal(NativeMediaImportCommitStatus.Inactive, result.Status);
        Assert.NotNull(result.Diagnostic);
        await OnlyOperation(catalog, plan.OperationId, plan.RequestSha256, NativeMediaImportOperationState.Prepared);
        Assert.Null(await catalog.ReadNativeMediaImportReceiptAsync(plan.OperationId, plan.RequestSha256));
        await AssertNoPublication(catalog); // Includes quotes, search indexes, roots and attachments.
        NativeMediaImportPlan nextPreview = await Plan(catalog, stage);
        Assert.Equal(plan.Instance, nextPreview.Instance);
        Assert.Equal(plan.Generation, nextPreview.Generation); // Content triggers rolled back too.
        Assert.NotNull(ready); ready.AssertUnchanged(plan.OperationsParent);
        await AssertRefusesRetry(catalog, plan);
        ready.AssertUnchanged(plan.OperationsParent);
    }

    [Fact]
    public async Task ResponseLossAndCancellationAfterSqlCommitReportHistoricalSuccessWithoutAnotherAttempt()
    {
        using Workspace files = new(); using ProfileLease lease = ProfileLease.Acquire(files.Profile);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(files.Profile, lease);
        await using NativeTransferImportStage stage = await Stage(files, Content());
        NativeMediaImportPlan plan = await Plan(catalog, stage);
        using CancellationTokenSource cancellation = new();
        bool committedCheckpoint = false;
        var result = await catalog.CommitNativeMediaImportAsync(plan, new MediaStore(), cancellation.Token, phase =>
        {
            if (phase != NativeMediaImportPhase.SqlCommitted) return;
            committedCheckpoint = true;
            cancellation.Cancel();
            throw new IOException("synthetic lost response after real SQL commit");
        });
        Assert.True(committedCheckpoint);
        Assert.Equal(NativeMediaImportCommitStatus.Committed, result.Status);
        Assert.True(result.IsReplay); Assert.Equal(nameof(IOException), result.Diagnostic);
        Assert.Equal(1, result.NewThreads); Assert.Equal(2, result.NewPosts); Assert.Equal(1, result.NewAttachments);
        var journal = await OnlyOperation(catalog, plan.OperationId, plan.RequestSha256, NativeMediaImportOperationState.Committed);
        Tree before = Tree.Capture(plan.OperationsParent);
        long generation = (await ReadFence(catalog)).Generation;
        int replayCheckpoints = 0;
        var replay = await catalog.CommitNativeMediaImportAsync(plan, new MediaStore(), cancellation.Token, _ => replayCheckpoints++);
        Assert.Equal(NativeMediaImportCommitStatus.Committed, replay.Status);
        Assert.True(replay.IsReplay); Assert.Equal(0, replayCheckpoints);
        Assert.Equal(generation, (await ReadFence(catalog)).Generation);
        before.AssertUnchanged(plan.OperationsParent);
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(files.Profile);
        var view = Assert.IsType<ArchivedThreadView>(await reader.ReadThreadAsync(Id));
        Assert.Equal(journal.CandidateDirectory, Assert.Single(view.MediaDirectories));
        Assert.Equal(2, view.Posts.Count);
    }

    [Fact]
    public async Task UnreadableReceiptAfterRealCommitIsUnknownUntilIndependentLookupRecoversHistoricalSuccess()
    {
        using Workspace files = new();
        Guid operation; string request, parent; Tree retained;
        using (ProfileLease lease = ProfileLease.Acquire(files.Profile))
        {
            await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(files.Profile, lease);
            await using NativeTransferImportStage stage = await Stage(files, Content());
            NativeMediaImportPlan plan = await Plan(catalog, stage);
            operation = plan.OperationId; request = plan.RequestSha256; parent = plan.OperationsParent;
            string parked = catalog.DatabasePath + ".synthetic-unavailable";
            bool unavailable = false;
            try
            {
                var result = await catalog.CommitNativeMediaImportAsync(plan, new MediaStore(), checkpoint: phase =>
                {
                    if (phase != NativeMediaImportPhase.SqlCommitted) return;
                    // WriteAsync's real connection is closed here (rollback-journal SQLite).
                    // Preserve the committed database, but make its known path unopenable.
                    File.Move(catalog.DatabasePath, parked);
                    unavailable = true;
                    Directory.CreateDirectory(catalog.DatabasePath);
                    throw new IOException("synthetic response loss plus unavailable receipt storage");
                });
                Assert.True(unavailable);
                Assert.Equal(NativeMediaImportCommitStatus.Unknown, result.Status);
                Assert.Contains("/receipt:", result.Diagnostic!);
                Assert.Equal(0, result.NewThreads); Assert.False(result.IsReplay);
                await Assert.ThrowsAsync<SqliteException>(() => catalog.ReadNativeMediaImportReceiptAsync(operation, request));
                await Assert.ThrowsAsync<SqliteException>(() => catalog.ReadNativeMediaImportOperationsPageAsync());
            }
            finally
            {
                if (unavailable)
                {
                    if (Directory.Exists(catalog.DatabasePath)) Directory.Delete(catalog.DatabasePath);
                    File.Move(parked, catalog.DatabasePath);
                }
            }
            retained = Tree.Capture(parent);
            var receipt = await catalog.ReadNativeMediaImportReceiptAsync(operation, request);
            Assert.NotNull(receipt);
            Assert.Equal(NativeMediaImportCommitStatus.Committed, receipt.Status); Assert.True(receipt.IsReplay);
            int callbacks = 0;
            var replay = await catalog.CommitNativeMediaImportAsync(plan, new MediaStore(), checkpoint: _ => callbacks++);
            Assert.Equal(NativeMediaImportCommitStatus.Committed, replay.Status);
            Assert.True(replay.IsReplay); Assert.Equal(0, callbacks);
            retained.AssertUnchanged(parent);
        }
        using ProfileLease restartedLease = ProfileLease.Acquire(files.Profile);
        await using ArchiveCatalog restarted = await ArchiveCatalog.OpenAsync(files.Profile, restartedLease);
        await OnlyOperation(restarted, operation, request, NativeMediaImportOperationState.Committed);
        var inspection = await restarted.InspectNativeMediaImportOperationAsync(operation, request, new MediaStore());
        Assert.NotNull(inspection.Receipt);
        Assert.Equal(NativeMediaImportCommitStatus.Committed, inspection.Receipt.Status);
        Assert.True(Assert.Single(inspection.Children).MatchesOperation);
        retained.AssertUnchanged(parent);
    }

    [Fact]
    public async Task ParallelSamePlanClaimAdmitsOnlyOneMaterializerAndOneCandidateRoot()
    {
        using Workspace files = new(); using ProfileLease lease = ProfileLease.Acquire(files.Profile);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(files.Profile, lease);
        await using NativeTransferImportStage stage = await Stage(files, Content());
        NativeMediaImportPlan plan = await Plan(catalog, stage);
        TaskCompletionSource claimed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using ManualResetEventSlim release = new(false);
        int preparations = 0, ready = 0, loserCallbacks = 0;
        Task<NativeMediaImportCommitResult> first = Task.Run(() => catalog.CommitNativeMediaImportAsync(plan, new MediaStore(), checkpoint: phase =>
        {
            if (phase == NativeMediaImportPhase.CandidateReady) Interlocked.Increment(ref ready);
            if (phase != NativeMediaImportPhase.PreparingSaved) return;
            Interlocked.Increment(ref preparations);
            claimed.TrySetResult();
            if (!release.Wait(Deadline)) throw new TimeoutException("test did not release real Preparing checkpoint");
        }));
        NativeMediaImportCommitResult? second = null;
        try
        {
            await claimed.Task.WaitAsync(Deadline);
            second = await catalog.CommitNativeMediaImportAsync(plan, new MediaStore(), checkpoint: _ => loserCallbacks++).WaitAsync(Deadline);
            Assert.Equal(NativeMediaImportCommitStatus.ReconciliationRequired, second.Status);
            Assert.False(Directory.Exists(plan.OperationsParent)); // Claim winner has not run mkdir yet.
            await OnlyOperation(catalog, plan.OperationId, plan.RequestSha256, NativeMediaImportOperationState.Preparing);
        }
        finally { release.Set(); await first.WaitAsync(Deadline); }
        var winner = await first;
        Assert.NotNull(second);
        Assert.Equal(NativeMediaImportCommitStatus.Committed, winner.Status);
        Assert.False(winner.IsReplay); Assert.Null(winner.Diagnostic);
        Assert.Equal(1, preparations); Assert.Equal(1, ready); Assert.Equal(0, loserCallbacks);
        var operation = await OnlyOperation(catalog, plan.OperationId, plan.RequestSha256, NativeMediaImportOperationState.Committed);
        Assert.Equal(operation.CandidateDirectory, Candidate(plan.OperationsParent));
        Assert.Single(Directory.GetDirectories(Path.GetDirectoryName(plan.OperationsParent)!));
        var replay = await catalog.CommitNativeMediaImportAsync(plan, new MediaStore());
        Assert.True(replay.IsReplay);
        Assert.Equal(NativeMediaImportCommitStatus.Committed, replay.Status);
        Assert.Equal(operation.CandidateDirectory, Candidate(plan.OperationsParent));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CandidatePayloadOrManifestChangedAtPreparedHandoffCannotReachFinalSql(bool manifest)
    {
        using Workspace files = new(); using ProfileLease lease = ProfileLease.Acquire(files.Profile);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(files.Profile, lease);
        await using NativeTransferImportStage stage = await Stage(files, Content());
        NativeMediaImportPlan plan = await Plan(catalog, stage);
        List<NativeMediaImportPhase> seen = [];
        Tree? changed = null;
        var result = await catalog.CommitNativeMediaImportAsync(plan, new MediaStore(), checkpoint: phase =>
        {
            seen.Add(phase);
            if (phase != NativeMediaImportPhase.PreparedSaved) return;
            string child = Candidate(plan.OperationsParent);
            if (manifest)
            {
                // Still valid JSON and identical semantic bindings, but NOT the frozen generation.
                File.AppendAllText(Path.Combine(child, ".meowchan-manifest.json"), "\n ");
            }
            else
            {
                string payload = Assert.Single(Directory.GetFiles(child, "*.original.bin"));
                byte[] bytes = File.ReadAllBytes(payload); bytes[0] ^= 0x55;
                File.WriteAllBytes(payload, bytes); // Same length, wrong actual digest.
            }
            changed = Tree.Capture(plan.OperationsParent);
        });
        Assert.Equal(NativeMediaImportPhase.PreparedSaved, seen.Last());
        Assert.Equal(NativeMediaImportCommitStatus.Inactive, result.Status);
        Assert.NotNull(result.Diagnostic); Assert.NotNull(changed);
        await OnlyOperation(catalog, plan.OperationId, plan.RequestSha256, NativeMediaImportOperationState.Prepared);
        var inspection = await catalog.InspectNativeMediaImportOperationAsync(plan.OperationId, plan.RequestSha256, new MediaStore());
        Assert.Equal(PassiveMediaCandidateStatus.Invalid, Assert.Single(inspection.Children).Status);
        Assert.False(Assert.Single(inspection.Children).MatchesOperation);
        Assert.Null(inspection.Receipt);
        await AssertRefusesRetry(catalog, plan);
        changed.AssertUnchanged(plan.OperationsParent); // Neither re-copy nor manifest repair.
        await AssertNoPublication(catalog);
    }

    [Fact]
    public async Task CandidateLeaseTakenAtPreparedHandoffIsBusyNotAuthorityToPublishOrRepair()
    {
        using Workspace files = new(); using ProfileLease lease = ProfileLease.Acquire(files.Profile);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(files.Profile, lease);
        await using NativeTransferImportStage stage = await Stage(files, Content());
        NativeMediaImportPlan plan = await Plan(catalog, stage);
        MediaStore shortWaitStore = new(File.Move, directoryLeaseTimeout: TimeSpan.FromMilliseconds(50));
        FileStream? externalLease = null;
        Tree? ready = null;
        List<NativeMediaImportPhase> seen = [];
        try
        {
            var result = await catalog.CommitNativeMediaImportAsync(plan, shortWaitStore, checkpoint: phase =>
            {
                seen.Add(phase);
                if (phase != NativeMediaImportPhase.PreparedSaved) return;
                ready = Tree.Capture(plan.OperationsParent);
                externalLease = new FileStream(Path.Combine(Candidate(plan.OperationsParent), ".meowchan-store.lock"),
                    FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            });
            Assert.NotNull(externalLease);
            Assert.Equal(NativeMediaImportPhase.PreparedSaved, seen.Last());
            Assert.Equal(NativeMediaImportCommitStatus.Inactive, result.Status);
            Assert.NotNull(result.Diagnostic);
            var busy = await catalog.InspectNativeMediaImportOperationAsync(plan.OperationId, plan.RequestSha256, shortWaitStore);
            Assert.Equal(PassiveMediaCandidateStatus.Busy, Assert.Single(busy.Children).Status);
            Assert.False(Assert.Single(busy.Children).MatchesOperation);
            Assert.Null(busy.Receipt);
        }
        finally { externalLease?.Dispose(); }
        Assert.NotNull(ready);
        await OnlyOperation(catalog, plan.OperationId, plan.RequestSha256, NativeMediaImportOperationState.Prepared);
        var released = await catalog.InspectNativeMediaImportOperationAsync(plan.OperationId, plan.RequestSha256, shortWaitStore);
        Assert.Equal(PassiveMediaCandidateStatus.Ready, Assert.Single(released.Children).Status);
        Assert.True(Assert.Single(released.Children).MatchesOperation);
        await AssertRefusesRetry(catalog, plan); // Lease release does not grant automatic old-fence resume.
        ready.AssertUnchanged(plan.OperationsParent);
        await AssertNoPublication(catalog);
    }

    private static async Task<NativeMediaImportPlan> Plan(ArchiveCatalog catalog, NativeTransferImportStage stage)
    {
        var prepared = await catalog.PrepareNativeMediaImportAsync(stage);
        Assert.Equal(NativeMediaImportError.None, prepared.Error);
        return Assert.IsType<NativeMediaImportPlan>(prepared.Plan);
    }

    private static async Task<NativeMediaImportOperation> OnlyOperation(ArchiveCatalog catalog, Guid operation,
        string request, NativeMediaImportOperationState state)
    {
        var page = await catalog.ReadNativeMediaImportOperationsPageAsync();
        Assert.Null(page.NextOperationId);
        var found = Assert.Single(page.Items);
        Assert.Equal(operation, found.OperationId); Assert.Equal(request, found.RequestSha256);
        Assert.Equal(Id, found.Resource); Assert.Equal(state, found.State);
        Assert.Equal(2, found.PostCount); Assert.Equal(1, found.AttachmentCount);
        return found;
    }

    private static async Task AssertRefusesRetry(ArchiveCatalog catalog, NativeMediaImportPlan plan)
    {
        int checkpoints = 0;
        var retry = await catalog.CommitNativeMediaImportAsync(plan, new MediaStore(), checkpoint: _ => checkpoints++);
        Assert.Equal(NativeMediaImportCommitStatus.ReconciliationRequired, retry.Status);
        Assert.Equal(0, checkpoints);
        Assert.Null(await catalog.ReadNativeMediaImportReceiptAsync(plan.OperationId, plan.RequestSha256));
    }

    private static async Task AssertNoPublication(ArchiveCatalog catalog)
    {
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(Path.GetDirectoryName(catalog.DatabasePath)!);
        Assert.Null(await reader.ReadThreadAsync(Id));
        Assert.Empty((await reader.SearchThreadsAsync(new("synthetic-media-import-needle"))).Items);
        using SqliteConnection connection = OpenReadOnly(catalog.DatabasePath);
        foreach (string table in new[] { "archive_threads", "archive_posts", "archive_attachments", "archive_media_directories",
            "archive_source_contracts", "archive_quotes", "archive_thread_search", "archive_post_search", "media_decisions" })
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "SELECT count(*) FROM " + table;
            Assert.Equal(0L, (long)command.ExecuteScalar()!);
        }
    }

    private static SqliteConnection OpenReadOnly(string database)
    {
        SqliteConnection connection = new(new SqliteConnectionStringBuilder
        { DataSource = database, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        connection.Open();
        return connection;
    }

    private static string? OperationState(string database, Guid operation)
    {
        using SqliteConnection connection = OpenReadOnly(database);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT state FROM archive_media_import_operations WHERE operation_id=$op";
        command.Parameters.AddWithValue("$op", operation.ToString("D"));
        return command.ExecuteScalar() as string;
    }

    private static Task<(string Instance, long Generation)> ReadFence(ArchiveCatalog catalog)
        => catalog.ReadAsync(connection =>
        {
            using SqliteTransaction transaction = connection.BeginTransaction(deferred: true);
            return ArchiveSql.ReadImportFence(connection, transaction);
        });

    private static string Candidate(string parent) => Assert.Single(Directory.GetDirectories(parent));

    private sealed record Tree(string[] Directories, KeyValuePair<string, string>[] FileHashes)
    {
        internal static Tree Capture(string root) => new(
            Directory.GetDirectories(root, "*", SearchOption.AllDirectories).Select(p => Path.GetRelativePath(root, p))
                .OrderBy(p => p, StringComparer.Ordinal).ToArray(),
            Directory.GetFiles(root, "*", SearchOption.AllDirectories).OrderBy(p => p, StringComparer.Ordinal)
                .Select(p => new KeyValuePair<string, string>(Path.GetRelativePath(root, p), Hash(File.ReadAllBytes(p)))).ToArray());
        internal void AssertUnchanged(string root)
        {
            Tree after = Capture(root);
            Assert.Equal(Directories, after.Directories);
            Assert.Equal(FileHashes, after.FileHashes);
        }
    }
}
