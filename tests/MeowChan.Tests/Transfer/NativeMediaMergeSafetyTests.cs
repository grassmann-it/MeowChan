using System.Collections.Immutable;
using MeowChan.Archive;
using MeowChan.Interfaces;
using MeowChan.Persistence;
using MeowChan.Storage;
using MeowChan.Transfer;
using Microsoft.Data.Sqlite;
using static MeowChan.Tests.Transfer.NativeMediaImportTests;

namespace MeowChan.Tests.Transfer;

public sealed class NativeMediaMergeSafetyTests
{
    [Theory]
    [InlineData((int)NativeMediaImportPhase.PreparingSaved)]
    [InlineData((int)NativeMediaImportPhase.OperationsParentCreated)]
    [InlineData((int)NativeMediaImportPhase.CandidateDirectoryCreated)]
    [InlineData((int)NativeMediaImportPhase.CandidateReady)]
    [InlineData((int)NativeMediaImportPhase.PreparedSaved)]
    [InlineData((int)NativeMediaImportPhase.CandidateVerified)]
    [InlineData((int)NativeMediaImportPhase.BeforeSqlCommit)]
    [InlineData((int)NativeMediaImportPhase.SqlCommitted)]
    public async Task CheckpointFailureHasDurableStateAndNeverBlindlyRestarts(int phaseValue)
    {
        NativeMediaImportPhase phase = (NativeMediaImportPhase)phaseValue;
        using Workspace files = new(); using ProfileLease lease = ProfileLease.Acquire(files.Profile);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(files.Profile, lease);
        await Seed(lease, catalog, files);
        await using NativeTransferImportStage stage = await Stage(files, Incoming());
        NativeMediaMergePlan plan = Assert.IsType<NativeMediaMergePlan>((await catalog.PrepareNativeMediaMergeAsync(stage)).Plan);
        bool committed = phase == NativeMediaImportPhase.SqlCommitted;
        using ProfileAdmissionToken admission = await EnterExclusiveAsync(lease);
        var result = await CommitMergeWithExclusiveAsync(admission, catalog, plan,
            committed ? ProfileExclusiveDisposition.SafeToResume : ProfileExclusiveDisposition.RecoveryRequired, checkpoint: point =>
        { if (point == phase) throw new IOException("synthetic-checkpoint"); });
        Assert.Equal(committed ? NativeMediaImportCommitStatus.Committed : NativeMediaImportCommitStatus.ReconciliationRequired, result.Status);
        NativeMediaMergeOperation operation = Assert.Single((await catalog.ReadNativeMediaMergeOperationsPageAsync()).Items);
        Assert.Equal(committed ? NativeMediaImportOperationState.Committed
            : phase >= NativeMediaImportPhase.PreparedSaved ? NativeMediaImportOperationState.Prepared
            : NativeMediaImportOperationState.Preparing, operation.State);
        var inspection = await catalog.InspectNativeMediaMergeOperationAsync(plan.OperationId, plan.RequestSha256, new MediaStore());
        Assert.Equal(committed, inspection.Receipt != null);
        Dictionary<string, string> beforeReplay = Files(files.Profile);
        // Same storage owner probes durable recovery state; this is not a Registry resume.
        var replay = await CommitMergeWithExclusiveAsync(admission, catalog, plan,
            committed ? ProfileExclusiveDisposition.SafeToResume : ProfileExclusiveDisposition.RecoveryRequired);
        Assert.Equal(committed ? NativeMediaImportCommitStatus.Committed : NativeMediaImportCommitStatus.ReconciliationRequired, replay.Status);
        Assert.Equal(beforeReplay, Files(files.Profile));
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(files.Profile);
        var view = Assert.IsType<ArchivedThreadView>(await reader.ReadThreadAsync(Id));
        Assert.Equal(committed ? 3 : 2, view.PostCount);
        Assert.Equal(ThreadSourceState.NotFound, view.Summary.SourceState);
        Assert.Equal(committed ? 2 : 1, view.MediaDirectories.Count);
    }

    [Fact]
    public async Task ValidManifestGenerationChangeMakesPlanStaleWithoutPublication()
    {
        using Workspace files = new(); using ProfileLease lease = ProfileLease.Acquire(files.Profile);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(files.Profile, lease);
        await Seed(lease, catalog, files);
        await using NativeTransferImportStage stage = await Stage(files, Incoming());
        NativeMediaMergePlan plan = Assert.IsType<NativeMediaMergePlan>((await catalog.PrepareNativeMediaMergeAsync(stage)).Plan);
        string root = Assert.Single(plan.Roots).Directory;
        await File.AppendAllTextAsync(Path.Combine(root, ".meowchan-manifest.json"), "\n ");
        using ProfileAdmissionToken admission = await EnterExclusiveAsync(lease);
        var result = await CommitMergeWithExclusiveAsync(admission, catalog, plan, ProfileExclusiveDisposition.SafeToResume);
        Assert.Equal(NativeMediaImportCommitStatus.StalePlan, result.Status);
        Assert.False(Directory.Exists(plan.OperationsParent));
        Assert.Null(await catalog.ReadNativeMediaMergeReceiptAsync(plan.OperationId, plan.RequestSha256));
        Assert.Empty((await catalog.ReadNativeMediaMergeOperationsPageAsync()).Items);
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(files.Profile);
        Assert.Single((await reader.ReadThreadAsync(Id))!.MediaDirectories);
    }

    [Fact]
    public async Task LegacyTargetAssociationBlocksNewRootWithoutChangingOriginalResolution()
    {
        using Workspace files = new(); using ProfileLease lease = ProfileLease.Acquire(files.Profile);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(files.Profile, lease);
        await Seed(lease, catalog, files);
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(files.Profile);
        var view = Assert.IsType<ArchivedThreadView>(await reader.ReadThreadAsync(Id));
        CapturedPost[] legacyPosts = view.Posts.Select(post => post with { Attachments = post.Attachments?
            .Select(attachment => attachment with { AttachmentRevisionKey = null }).ToArray() }).ToArray();
        await catalog.CapturePreservingRevisionsAsync(new(Id, Url, view.Summary.Title, ThreadSourceState.NotFound, At,
            view.Summary.MediaDirectory, legacyPosts));
        var legacy = (await reader.ReadThreadAsync(Id))!;
        MediaAssetSnapshot snapshot = await MediaAssetReader.OpenAsync(view.Summary.MediaDirectory);
        CapturedAttachment attachment = Assert.Single(legacy.Posts.Single(post => post.PostId == "15").Attachments!);
        var resolved = Assert.IsType<ResolvedMediaAttachment>(MediaAttachmentResolver.Resolve([snapshot], Id, "15", attachment));
        Assert.True(resolved.IsLegacy);
        Dictionary<string, string> before = Files(files.Profile);
        await using NativeTransferImportStage stage = await Stage(files, Incoming());
        var prepared = await catalog.PrepareNativeMediaMergeAsync(stage);
        Assert.Null(prepared.Plan); Assert.Equal(NativeMediaMergeError.TargetUnavailable, prepared.Error);
        Assert.Equal(before, Files(files.Profile));
        Assert.Empty((await catalog.ReadNativeMediaMergeOperationsPageAsync()).Items);
        Assert.Equal(resolved.Asset, MediaAttachmentResolver.Resolve([snapshot], Id, "15", attachment)!.Asset);
    }

    [Fact]
    public async Task SchemaSevenReaderStaysReadOnlyAndMigrationPreservesOldImportReceipt()
    {
        using Workspace files = new(); Guid operation; string request; NativeMediaImportCommitResult committed;
        using (ProfileLease lease = ProfileLease.Acquire(files.Profile))
        {
            await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(files.Profile, lease);
            await using var stage = await Stage(files, Content());
            var plan = (await catalog.PrepareNativeMediaImportAsync(stage)).Plan!;
            operation = plan.OperationId; request = plan.RequestSha256;
            using ProfileAdmissionToken admission = await EnterExclusiveAsync(lease);
            committed = await CommitSeedWithExclusiveAsync(admission, catalog, plan);
            Assert.Equal(NativeMediaImportCommitStatus.Committed, committed.Status);
        }
        string db = Path.Combine(files.Profile, "archive.db");
        using (SqliteConnection connection = ArchiveSql.OpenConnection(db, SqliteOpenMode.ReadWrite))
        using (SqliteCommand command = connection.CreateCommand())
        {
            // Schema-shape fixture, not a released-client provenance claim.
            command.CommandText = "DROP TABLE archive_media_merge_operations; DROP TABLE archive_media_merge_receipts; PRAGMA user_version=7";
            command.ExecuteNonQuery();
        }
        byte[] before = await File.ReadAllBytesAsync(db);
        await using (ArchiveReader reader = await ArchiveReader.OpenAsync(files.Profile)) Assert.NotNull(await reader.ReadThreadAsync(Id));
        Assert.Equal(before, await File.ReadAllBytesAsync(db));
        using ProfileLease migratedLease = ProfileLease.Acquire(files.Profile);
        await using ArchiveCatalog migrated = await ArchiveCatalog.OpenAsync(files.Profile, migratedLease);
        Assert.Equal(committed with { IsReplay = true }, await migrated.ReadNativeMediaImportReceiptAsync(operation, request));
        Assert.Empty((await migrated.ReadNativeMediaMergeOperationsPageAsync()).Items);
        using SqliteConnection read = ArchiveSql.OpenConnection(db, SqliteOpenMode.ReadOnly);
        Assert.Equal(9, ArchiveSql.GetPragma(read, "user_version"));
    }

    // Direct synthetic storage owner via existing Core IVT, not public Registry startup.
    // Await initial store setup before Ready; retain the real token through reconciliation.
    private static ValueTask<ProfileAdmissionToken> EnterExclusiveAsync(ProfileLease lease)
    {
        if (lease.Admission.Phase == ProfileAdmissionPhase.Starting) lease.Admission.CompleteStartup();
        return lease.Admission.EnterExclusiveAsync();
    }

    private static async Task<NativeMediaMergeResult> CommitMergeWithExclusiveAsync(
        ProfileAdmissionToken admission, ArchiveCatalog catalog, NativeMediaMergePlan plan,
        ProfileExclusiveDisposition expectedDisposition, Action<NativeMediaImportPhase>? checkpoint = null)
    {
        ProfileExclusiveOutcome<NativeMediaMergeResult> outcome =
            await catalog.CommitNativeMediaMergeAdmittedAsync(admission, plan, new MediaStore(), checkpoint: checkpoint);
        Assert.Equal(expectedDisposition, outcome.Disposition);
        catalog.EnsureTransferAdmission(admission); // Borrowed authority survives all work and reconciliation.
        return outcome.Result;
    }

    private static async Task<NativeMediaImportCommitResult> CommitSeedWithExclusiveAsync(
        ProfileAdmissionToken admission, ArchiveCatalog catalog, NativeMediaImportPlan plan)
    {
        ProfileExclusiveOutcome<NativeMediaImportCommitResult> outcome =
            await catalog.CommitNativeMediaImportAdmittedAsync(admission, plan, new MediaStore());
        Assert.Equal(ProfileExclusiveDisposition.SafeToResume, outcome.Disposition);
        catalog.EnsureTransferAdmission(admission); // The consumer must leave its borrowed owner live.
        return outcome.Result;
    }

    private static async Task Seed(ProfileLease lease, ArchiveCatalog catalog, Workspace files)
    {
        await using var stage = await Stage(files, Content());
        var plan = (await catalog.PrepareNativeMediaImportAsync(stage)).Plan!;
        using ProfileAdmissionToken admission = await EnterExclusiveAsync(lease);
        Assert.Equal(NativeMediaImportCommitStatus.Committed, (await CommitSeedWithExclusiveAsync(admission, catalog, plan)).Status);
    }

    private static NativeTransferCatalog Incoming()
    {
        var catalog = Content(); TransferThread thread = catalog.Threads[0];
        TransferAttachment attachment = Attachment(postId: "17");
        TransferPost added = new(new("17", 2, PostFieldPresence.All, At, "", "", "new-post", false, [attachment.Metadata]), [attachment]);
        return catalog with { Threads = [thread with { Posts = thread.Posts.Add(added) }] };
    }
}
