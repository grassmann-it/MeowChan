using MeowChan.Archive;
using MeowChan.Persistence;
using MeowChan.Storage;
using MeowChan.Transfer;
using Microsoft.Data.Sqlite;
using static MeowChan.Tests.Transfer.NativeMediaImportTests;

namespace MeowChan.Tests.Transfer;

// Bounded recovery-only coverage. These cases deliberately use the real import/merge
// producers and mutate only the isolated synthetic workspace needed to model a crash,
// receipt loss, a competing operation, or target corruption.
public sealed class NativeMediaMergeRecoveryReconciliationRegressionTests
{
    [Theory]
    [InlineData((int)NativeMediaImportPhase.PreparingSaved, (int)NativeMediaImportOperationState.Preparing)]
    [InlineData((int)NativeMediaImportPhase.CandidateReady, (int)NativeMediaImportOperationState.Preparing)]
    [InlineData((int)NativeMediaImportPhase.PreparedSaved, (int)NativeMediaImportOperationState.Prepared)]
    public async Task DurablePreparingPreparedOrClaimedWithoutReceiptNeverReportsInactive(
        int interruptionValue, int expectedStateValue)
    {
        // The durable claim is persisted as Preparing, not as a separate Claimed enum value.
        NativeMediaImportPhase interruption = (NativeMediaImportPhase)interruptionValue;
        NativeMediaImportOperationState expectedState = (NativeMediaImportOperationState)expectedStateValue;
        using Workspace files = new();
        using ProfileLease lease = ProfileLease.Acquire(files.Profile);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(files.Profile, lease);
        await SeedTargetAsync(lease, catalog, files);
        await using NativeTransferImportStage stage = await Stage(files, Incoming());
        NativeMediaMergePlan plan = Assert.IsType<NativeMediaMergePlan>(
            (await catalog.PrepareNativeMediaMergeAsync(stage)).Plan);

        using ProfileAdmissionToken admission = await lease.Admission.EnterExclusiveAsync();
        NativeMediaMergeResult interrupted = await AssertMergeAsync(admission, catalog, plan, new MediaStore(), ProfileExclusiveDisposition.RecoveryRequired,
            checkpoint: phase =>
            {
                if (phase == interruption)
                    throw new IOException("synthetic recovery interruption");
            });

        Assert.Equal(NativeMediaImportCommitStatus.ReconciliationRequired, interrupted.Status);
        Assert.NotEqual(NativeMediaImportCommitStatus.Inactive, interrupted.Status);
        Assert.Equal(nameof(IOException), interrupted.Diagnostic);
        NativeMediaMergeOperation operation = Assert.Single((await catalog.ReadNativeMediaMergeOperationsPageAsync()).Items);
        Assert.Equal(expectedState, operation.State);
        Assert.Null(await catalog.ReadNativeMediaMergeReceiptAsync(plan.OperationId, plan.RequestSha256));

        NativeMediaMergeInspection inspection = await catalog.InspectNativeMediaMergeOperationAsync(
            plan.OperationId, plan.RequestSha256, new MediaStore());
        Assert.Equal(expectedState, Assert.IsType<NativeMediaMergeOperation>(inspection.Operation).State);
        Assert.Null(inspection.Receipt);
        Assert.Equal(plan.OperationId, operation.OperationId);
        if (interruption == NativeMediaImportPhase.PreparingSaved)
        {
            Assert.False(Directory.Exists(plan.OperationsParent));
            Assert.Equal(NativeMediaImportFilesystemStatus.MissingParent, inspection.FilesystemStatus);
        }
        else
        {
            Assert.Contains(inspection.Children, child => child.MatchesOperation);
        }

        Dictionary<string, string> beforeReplay = Files(files.Profile);
        NativeMediaMergeResult retry = await AssertMergeAsync(admission, catalog, plan, new MediaStore(), ProfileExclusiveDisposition.RecoveryRequired);
        Assert.Equal(NativeMediaImportCommitStatus.ReconciliationRequired, retry.Status);
        Assert.Equal(beforeReplay, Files(files.Profile));
        await AssertUnchangedTargetAsync(files);
    }

    [Fact]
    public async Task HistoricalReceiptWinsOverCommittedOperationRowDuringPostCommitFailure()
    {
        using Workspace files = new();
        using ProfileLease lease = ProfileLease.Acquire(files.Profile);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(files.Profile, lease);
        await SeedTargetAsync(lease, catalog, files);
        await using NativeTransferImportStage stage = await Stage(files, Incoming());
        NativeMediaMergePlan plan = Assert.IsType<NativeMediaMergePlan>(
            (await catalog.PrepareNativeMediaMergeAsync(stage)).Plan);

        // The SQL transaction has already written both the receipt and the committed operation
        // when this callback fails. Recovery must return the historical receipt first.
        using ProfileAdmissionToken admission = await lease.Admission.EnterExclusiveAsync();
        NativeMediaMergeResult replay = await AssertMergeAsync(admission, catalog, plan, new MediaStore(), ProfileExclusiveDisposition.SafeToResume,
            checkpoint: phase =>
            {
                if (phase == NativeMediaImportPhase.SqlCommitted)
                    throw new IOException("synthetic post-commit observer failure");
            });

        Assert.Equal(NativeMediaImportCommitStatus.Committed, replay.Status);
        Assert.True(replay.IsReplay);
        Assert.Equal(nameof(IOException), replay.Diagnostic);
        NativeMediaMergeOperation operation = Assert.Single((await catalog.ReadNativeMediaMergeOperationsPageAsync()).Items);
        Assert.Equal(NativeMediaImportOperationState.Committed, operation.State);
        NativeMediaMergeResult receipt = Assert.IsType<NativeMediaMergeResult>(
            await catalog.ReadNativeMediaMergeReceiptAsync(plan.OperationId, plan.RequestSha256));
        Assert.Equal(NativeMediaImportCommitStatus.Committed, receipt.Status);

        NativeMediaMergeInspection inspection = await catalog.InspectNativeMediaMergeOperationAsync(
            plan.OperationId, plan.RequestSha256, new MediaStore());
        Assert.Equal(NativeMediaImportOperationState.Committed, inspection.Operation!.State);
        Assert.Equal(receipt, inspection.Receipt);
        Assert.Equal(receipt with { Diagnostic = nameof(IOException) }, replay);
        Assert.Equal(1, receipt.NewPosts);
        Dictionary<string, string> beforeReplay = Files(files.Profile);
        Assert.Equal(receipt, await AssertMergeAsync(admission, catalog, plan, new MediaStore(), ProfileExclusiveDisposition.SafeToResume));
        Assert.Equal(beforeReplay, Files(files.Profile));
    }

    [Fact]
    public async Task OtherUnresolvedResourceOperationPlusObserverFailureRequiresReconciliation()
    {
        using Workspace files = new();
        using ProfileLease lease = ProfileLease.Acquire(files.Profile);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(files.Profile, lease);
        await SeedTargetAsync(lease, catalog, files);

        await using NativeTransferImportStage firstStage = await Stage(files, Incoming());
        NativeMediaMergePlan first = Assert.IsType<NativeMediaMergePlan>(
            (await catalog.PrepareNativeMediaMergeAsync(firstStage)).Plan);
        using ProfileAdmissionToken admission = await lease.Admission.EnterExclusiveAsync();
        NativeMediaMergeResult firstInterrupted = await AssertMergeAsync(admission, catalog, first, new MediaStore(), ProfileExclusiveDisposition.RecoveryRequired,
            checkpoint: phase =>
            {
                if (phase == NativeMediaImportPhase.PreparingSaved)
                    throw new IOException("synthetic first-operation interruption");
            });
        Assert.Equal(NativeMediaImportCommitStatus.ReconciliationRequired, firstInterrupted.Status);

        await using NativeTransferImportStage secondStage = await Stage(files, Incoming());
        NativeMediaMergePlan second = Assert.IsType<NativeMediaMergePlan>(
            (await catalog.PrepareNativeMediaMergeAsync(secondStage)).Plan);
        Assert.NotEqual(first.OperationId, second.OperationId);

        Dictionary<string, string> beforeAttempt = Files(files.Profile);
        bool observerReached = false;
        NativeMediaMergeResult blocked = await AssertMergeAsync(admission, catalog, second, new MediaStore(), ProfileExclusiveDisposition.RecoveryRequired,
            beforeAdmission: () =>
            {
                observerReached = true;
                throw new IOException("synthetic observer/read failure");
            });

        Assert.True(observerReached);
        Assert.Equal(NativeMediaImportCommitStatus.ReconciliationRequired, blocked.Status);
        Assert.NotEqual(NativeMediaImportCommitStatus.Inactive, blocked.Status);
        Assert.Equal(nameof(IOException), blocked.Diagnostic);
        Assert.Null(await catalog.ReadNativeMediaMergeReceiptAsync(second.OperationId, second.RequestSha256));
        NativeMediaMergeOperation remaining = Assert.Single((await catalog.ReadNativeMediaMergeOperationsPageAsync()).Items);
        Assert.Equal(first.OperationId, remaining.OperationId);
        Assert.Equal(NativeMediaImportOperationState.Preparing, remaining.State);
        Assert.Equal(first.Resource, second.Resource);
        Assert.False(Directory.Exists(second.OperationsParent));
        Assert.Equal(beforeAttempt, Files(files.Profile));
        await AssertUnchangedTargetAsync(files);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CommittedOperationWithoutReceiptReportsUnknownInsteadOfHistoricalSuccess(bool observerFails)
    {
        using Workspace files = new();
        using ProfileLease lease = ProfileLease.Acquire(files.Profile);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(files.Profile, lease);
        await SeedTargetAsync(lease, catalog, files);
        await using NativeTransferImportStage stage = await Stage(files, Incoming());
        NativeMediaMergePlan plan = Assert.IsType<NativeMediaMergePlan>(
            (await catalog.PrepareNativeMediaMergeAsync(stage)).Plan);
        using ProfileAdmissionToken admission = await lease.Admission.EnterExclusiveAsync();
        Assert.Equal(NativeMediaImportCommitStatus.Committed,
            (await AssertMergeAsync(admission, catalog, plan, new MediaStore(), ProfileExclusiveDisposition.SafeToResume)).Status);

        // Synthetic crash damage, pinned by the actual Exclusive owner rather than an
        // independent ordinary writer/connection running underneath that owner.
        int deleted = await catalog.WriteTransferAdmittedAsync(admission, (connection, transaction) =>
        {
            using SqliteCommand delete = connection.CreateCommand();
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM archive_media_merge_receipts WHERE operation_id=$operation";
            delete.Parameters.AddWithValue("$operation", plan.OperationId.ToString("D"));
            return delete.ExecuteNonQuery();
        });
        Assert.Equal(1, deleted);

        Assert.Null(await catalog.ReadNativeMediaMergeReceiptAsync(plan.OperationId, plan.RequestSha256));
        Dictionary<string, string> beforeAttempt = Files(files.Profile);
        bool observerReached = false;
        NativeMediaMergeResult missingReceipt = await AssertMergeAsync(admission, catalog, plan, new MediaStore(), ProfileExclusiveDisposition.RecoveryRequired,
            beforeAdmission: () =>
            {
                observerReached = true;
                if (observerFails) throw new IOException("synthetic observer after receipt loss");
            });

        Assert.True(observerReached);
        Assert.Equal(NativeMediaImportCommitStatus.Unknown, missingReceipt.Status);
        Assert.False(missingReceipt.IsReplay);
        Assert.Equal(observerFails ? nameof(IOException) : null, missingReceipt.Diagnostic);
        Assert.Equal(beforeAttempt, Files(files.Profile));
        Assert.Null(await catalog.ReadNativeMediaMergeReceiptAsync(plan.OperationId, plan.RequestSha256));
        NativeMediaMergeOperation operation = Assert.Single((await catalog.ReadNativeMediaMergeOperationsPageAsync()).Items);
        Assert.Equal(NativeMediaImportOperationState.Committed, operation.State);
    }

    [Fact]
    public async Task TargetCorruptionBeforeClaimRemainsInactiveAndLeavesNoJournal()
    {
        using Workspace files = new();
        using ProfileLease lease = ProfileLease.Acquire(files.Profile);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(files.Profile, lease);
        await SeedTargetAsync(lease, catalog, files);
        await using NativeTransferImportStage stage = await Stage(files, Incoming());
        NativeMediaMergePlan plan = Assert.IsType<NativeMediaMergePlan>(
            (await catalog.PrepareNativeMediaMergeAsync(stage)).Plan);
        MediaAssetSnapshot target = await MediaAssetReader.OpenAsync(plan.Roots[0].Directory);
        MediaAssetReference asset = Assert.Single(target.GetPage(0, 500));

        Dictionary<string, string>? afterCorruption = null;
        bool claimCheckpointReached = false;
        using ProfileAdmissionToken admission = await lease.Admission.EnterExclusiveAsync();
        NativeMediaMergeResult result = await AssertMergeAsync(admission, catalog, plan, new MediaStore(), ProfileExclusiveDisposition.SafeToResume,
            beforeAdmission: () =>
            {
                File.WriteAllBytes(asset.Path, [1, 3, 5]);
                afterCorruption = Files(files.Profile);
            }, checkpoint: _ => claimCheckpointReached = true);

        Assert.Equal(NativeMediaImportCommitStatus.Inactive, result.Status);
        Assert.NotNull(result.Diagnostic);
        Assert.False(claimCheckpointReached);
        Assert.NotNull(afterCorruption);
        Assert.Equal(afterCorruption, Files(files.Profile));
        Assert.False(Directory.Exists(plan.OperationsParent));
        Assert.Empty((await catalog.ReadNativeMediaMergeOperationsPageAsync()).Items);
        Assert.Null(await catalog.ReadNativeMediaMergeReceiptAsync(plan.OperationId, plan.RequestSha256));
        await AssertUnchangedTargetAsync(files);
    }

    [Fact]
    public async Task FinalStaleWithPreparedJournalRequiresRecoveryAndDoesNotPublishCandidate()
    {
        using Workspace files = new();
        using ProfileLease lease = ProfileLease.Acquire(files.Profile);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(files.Profile, lease);
        await SeedTargetAsync(lease, catalog, files);
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(files.Profile);
        ArchivedThreadView before = Assert.IsType<ArchivedThreadView>(await reader.ReadThreadAsync(Id));
        string root = Assert.Single(before.MediaDirectories);
        Dictionary<string, string> rootBefore = Files(root);
        await using NativeTransferImportStage stage = await Stage(files, Incoming());
        NativeMediaMergePlan plan = Assert.IsType<NativeMediaMergePlan>(
            (await catalog.PrepareNativeMediaMergeAsync(stage)).Plan);
        using ProfileAdmissionToken admission = await lease.Admission.EnterExclusiveAsync();
        bool targetChanged = false;

        NativeMediaMergeResult stale = await AssertMergeAsync(admission, catalog, plan, new MediaStore(),
            ProfileExclusiveDisposition.RecoveryRequired, checkpoint: phase =>
            {
                if (phase != NativeMediaImportPhase.CandidateVerified) return;
                // Deliberate final-fence adverse state, not an ordinary mutation beneath
                // Exclusive. The actual owner pins this minimal target-only SQL change.
                int updated = catalog.WriteTransferAdmittedAsync(admission, (connection, transaction) =>
                {
                    using SqliteCommand command = ArchiveSql.Command(connection, transaction, """
                        UPDATE archive_threads SET title=$title, revision=revision+1
                        WHERE source=$source AND board=$board AND thread=$thread
                        """);
                    ArchiveSql.AddId(command, Id);
                    command.Parameters.AddWithValue("$title", "synthetic final-fence change");
                    return command.ExecuteNonQuery();
                }).GetAwaiter().GetResult();
                Assert.Equal(1, updated);
                targetChanged = true;
            });

        Assert.True(targetChanged);
        Assert.Equal(NativeMediaImportCommitStatus.StalePlan, stale.Status);
        NativeMediaMergeOperation operation = Assert.Single((await catalog.ReadNativeMediaMergeOperationsPageAsync()).Items);
        Assert.Equal(plan.OperationId, operation.OperationId);
        Assert.Equal(plan.RequestSha256, operation.RequestSha256);
        Assert.Equal(NativeMediaImportOperationState.Prepared, operation.State);
        Assert.Null(await catalog.ReadNativeMediaMergeReceiptAsync(plan.OperationId, plan.RequestSha256));
        NativeMediaMergeInspection inspection = await catalog.InspectNativeMediaMergeOperationAsync(
            plan.OperationId, plan.RequestSha256, new MediaStore());
        Assert.Null(inspection.Receipt);
        Assert.Contains(inspection.Children, child => child.MatchesOperation);
        ArchivedThreadView after = Assert.IsType<ArchivedThreadView>(await reader.ReadThreadAsync(Id));
        Assert.Equal(before.Summary with
        { Title = "synthetic final-fence change", Revision = before.Summary.Revision + 1 }, after.Summary);
        Assert.Equal(before.MediaDirectories, after.MediaDirectories);
        Assert.Equal(before.Posts.Select(post => post with { Attachments = null }),
            after.Posts.Select(post => post with { Attachments = null }));
        foreach (CapturedPost post in before.Posts)
        {
            CapturedPost unchanged = Assert.Single(after.Posts, value => value.PostId == post.PostId);
            Assert.Equal(post.Attachments is null, unchanged.Attachments is null);
            if (post.Attachments != null) Assert.Equal(post.Attachments.ToArray(), unchanged.Attachments!.ToArray());
        }
        Assert.Equal(rootBefore, Files(root));
        await AssertUnchangedTargetAsync(files);
    }

    private static async Task AssertUnchangedTargetAsync(Workspace files)
    {
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(files.Profile);
        ArchivedThreadView target = Assert.IsType<ArchivedThreadView>(await reader.ReadThreadAsync(Id));
        Assert.Equal(2, target.PostCount);
        Assert.Single(target.MediaDirectories);
        Assert.DoesNotContain(target.Posts, post => post.PostId == "17");
        Assert.False(target.Summary.CaptureEnabled);
    }

    // Token-first assertion helper: caller owns acquisition/release and all interleavings.
    // It does not reserve a lane or infer safety from the domain status.
    private static async Task<NativeMediaMergeResult> AssertMergeAsync(ProfileAdmissionToken admission,
        ArchiveCatalog catalog, NativeMediaMergePlan plan, MediaStore store,
        ProfileExclusiveDisposition expectedDisposition, CancellationToken cancellationToken = default,
        Action? beforeAdmission = null, Action<NativeMediaImportPhase>? checkpoint = null)
    {
        var outcome = await catalog.CommitNativeMediaMergeAdmittedAsync(admission, plan, store,
            cancellationToken, beforeAdmission, checkpoint);
        Assert.Equal(expectedDisposition, outcome.Disposition);
        catalog.EnsureTransferAdmission(admission); // full producer/reconciliation returned, owner still live
        return outcome.Result;
    }

    private static async Task SeedTargetAsync(ProfileLease lease, ArchiveCatalog catalog, Workspace files)
    {
        await using NativeTransferImportStage stage = await Stage(files, Content());
        NativeMediaImportPlan plan = Assert.IsType<NativeMediaImportPlan>(
            (await catalog.PrepareNativeMediaImportAsync(stage)).Plan);
        // Direct IVT storage-owner fixture: finish setup before borrowing real Exclusive.
        if (lease.Admission.Phase == ProfileAdmissionPhase.Starting) lease.Admission.CompleteStartup();
        using (ProfileAdmissionToken admission = await lease.Admission.EnterExclusiveAsync())
        {
            var outcome = await catalog.CommitNativeMediaImportAdmittedAsync(admission, plan, new MediaStore());
            Assert.Equal(ProfileExclusiveDisposition.SafeToResume, outcome.Disposition);
            catalog.EnsureTransferAdmission(admission); // producer did not retire its borrowed owner
            Assert.Equal(NativeMediaImportCommitStatus.Committed, outcome.Result.Status);
        }
    }

    private static NativeTransferCatalog Incoming()
    {
        NativeTransferCatalog content = Content();
        TransferThread thread = content.Threads[0];
        TransferPost added = new(new("17", 2, PostFieldPresence.Html, Html: "recovery-new-post"), []);
        return content with { Threads = [thread with { Posts = thread.Posts.Add(added) }] };
    }
}
