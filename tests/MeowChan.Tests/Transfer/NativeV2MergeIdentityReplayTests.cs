using System.Text.Json;
using MeowChan.Archive;
using MeowChan.Interfaces;
using MeowChan.Persistence;
using MeowChan.Storage;
using MeowChan.Transfer;
using Microsoft.Data.Sqlite;

namespace MeowChan.Tests.Transfer;

// W32b5 identity/replay lane: real writer -> validated v4 stage -> admitted import
// seed -> owner-issued merge. Authored against WIP; UnsupportedPackage is not a pass.
// Direct synthetic storage owners, not a Registry startup/resume or provider proof.
public sealed class NativeV2MergeIdentityReplayTests
{
    private const string AddedPost = "18";
    private static readonly NativeMediaMergeCounts AddedCounts = new(1, 0, 1, 1, 0, 1, 1, 1);
    private static readonly NativeMediaMergeResult AddedResult = new(NativeMediaImportCommitStatus.Committed,
        NewPosts: 1, NewAttachments: 1, NewBindings: 1, EvidenceAdditions: 1,
        DerivedDescriptorAdditions: 1, ContainedDerivedAdditions: 1);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SameIssuedPlanReplaysTypedReceiptBeforeDisposedStageAndMissingMedia(bool canceledReplay)
    {
        await using NativeV2ImportFixture f = await NativeV2ImportFixture.CreateAsync();
        await SeedTargetAsync(f);
        await using NativeTransferImportStage stage = await IncomingStageAsync(f);
        NativeMediaMergePlan plan = await PrepareMergeAsync(f, stage);
        NativeMediaMergeResult committed = await CommitFreshAsync(f, plan);
        AssertReceipt(f, plan);
        AssertCommittedOperation(f, plan);
        ArchivedThreadView target = Assert.IsType<ArchivedThreadView>(await ReadTargetAsync(f));
        Assert.Equal(4, target.PostCount);
        Assert.Equal(2, target.MediaDirectories.Count);

        await stage.DisposeAsync();
        await f.Stage.DisposeAsync();
        Assert.Empty(Directory.EnumerateFileSystemEntries(f.Files.StageRoot));
        // Move only test-owned trees, including both Full and Safe, rather than delete
        // product data. Receipt-first must not try to open, verify or recreate them.
        Directory.Move(f.Plan.OperationsParent, Path.Combine(f.Files.Root, "unavailable-import"));
        Directory.Move(plan.OperationsParent, Path.Combine(f.Files.Root, "unavailable-merge"));
        Assert.All(target.MediaDirectories, root => Assert.False(Directory.Exists(root)));
        Snapshot before = await SnapshotAsync(f);
        int callbacks = 0, dispatches = 0;
        f.Catalog.BeforeWriteDispatchForTests = _ => dispatches++;
        using ProfileAdmissionToken admission = await f.ExclusiveAsync();

        // Reusing this exact object twice must not become a second publication, even
        // with cancellation already requested and no surviving staged payload stream.
        for (int attempt = 0; attempt < 2; attempt++)
        {
            ProfileExclusiveOutcome<NativeMediaMergeResult> replay =
                await f.Catalog.CommitNativeMediaMergeAdmittedAsync(admission, plan, f.Store,
                    new CancellationToken(canceledReplay), beforeAdmission: () => callbacks++,
                    checkpoint: _ => callbacks++);
            Assert.Equal(ProfileExclusiveDisposition.SafeToResume, replay.Disposition);
            Assert.Equal(committed with { IsReplay = true }, replay.Result);
            Assert.Equal(0, callbacks);
            Assert.Equal(0, dispatches);
            AssertReceipt(f, plan);
            AssertCommittedOperation(f, plan);
            Assert.False(Directory.Exists(plan.OperationsParent));
            Assert.False(Directory.Exists(f.Plan.OperationsParent));
            AssertSnapshotEqual(before, await SnapshotAsync(f));
            f.Catalog.EnsureTransferAdmission(admission);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CommittedReceiptSurvivesTargetDeletionAndNeverTouchesAReplacement(bool recreate)
    {
        await using NativeV2ImportFixture f = await NativeV2ImportFixture.CreateAsync();
        await SeedTargetAsync(f);
        await using NativeTransferImportStage stage = await IncomingStageAsync(f);
        NativeMediaMergePlan plan = await PrepareMergeAsync(f, stage);
        NativeMediaMergeResult committed = await CommitFreshAsync(f, plan);
        AssertCommittedOperation(f, plan);
        await stage.DisposeAsync();

        // Ordinary catalog mutation occurs only after the merge released Exclusive.
        // Catalog deletion retains receipts and media files; no filesystem cleanup.
        Assert.Single(await f.Catalog.DeleteThreadsAsync([NativeV2ImportFixture.Id]));
        Assert.Null(await ReadTargetAsync(f));
        Assert.Empty(ReadMergeOperations(f));
        AssertReceipt(f, plan); // An orphan typed receipt is still historical evidence.
        if (recreate)
        {
            NativeTransferCatalog content = NativeV2ImportFixture.Content();
            content = content with { Threads = [content.Threads[0] with
            {
                Title = "synthetic replacement, not the old merge target",
                Posts = [NativeV2ImportFixture.Post("91", NativeV2ImportFixture.Attachment("91"))]
            }] };
            await using NativeTransferImportStage replacementStage = await NativeV2ImportFixture.StageAsync(
                f.Files, content, NativeV2ImportFixture.Original, NativeV2ImportFixture.Preview);
            NativeMediaImportPreparation prepared = await f.Catalog.PrepareNativeMediaImportAsync(replacementStage);
            Assert.Equal(NativeMediaImportError.None, prepared.Error);
            NativeMediaImportPlan replacement = Assert.IsType<NativeMediaImportPlan>(prepared.Plan);
            Assert.Equal(2, replacement.PlanVersion);
            using ProfileAdmissionToken admission = await f.ExclusiveAsync();
            var imported = await f.Catalog.CommitNativeMediaImportAdmittedAsync(admission, replacement, f.Store);
            Assert.Equal(ProfileExclusiveDisposition.SafeToResume, imported.Disposition);
            Assert.Equal(NativeMediaImportCommitStatus.Committed, imported.Result.Status);
            Assert.False(imported.Result.IsReplay);
            f.Catalog.EnsureTransferAdmission(admission);
        }

        Snapshot before = await SnapshotAsync(f);
        if (recreate)
        {
            ArchivedThreadView replacement = Assert.IsType<ArchivedThreadView>(before.Target);
            Assert.Equal("synthetic replacement, not the old merge target", replacement.Summary.Title);
            Assert.Equal("91", Assert.Single(replacement.Posts).PostId);
            Assert.DoesNotContain(replacement.MediaDirectories, root => plan.Roots.Any(old => old.Directory == root));
        }
        int callbacks = 0, dispatches = 0;
        f.Catalog.BeforeWriteDispatchForTests = _ => dispatches++;
        using ProfileAdmissionToken replayAdmission = await f.ExclusiveAsync();
        var replay = await f.Catalog.CommitNativeMediaMergeAdmittedAsync(replayAdmission, plan, f.Store,
            beforeAdmission: () => callbacks++, checkpoint: _ => callbacks++);

        Assert.Equal(ProfileExclusiveDisposition.SafeToResume, replay.Disposition);
        Assert.Equal(committed with { IsReplay = true }, replay.Result);
        Assert.Equal(0, callbacks);
        Assert.Equal(0, dispatches);
        Assert.Empty(ReadMergeOperations(f)); // No operation resurrection from the receipt.
        AssertReceipt(f, plan);
        AssertSnapshotEqual(before, await SnapshotAsync(f));
        f.Catalog.EnsureTransferAdmission(replayAdmission);
    }

    [Fact]
    public async Task TargetMutationBeforeClaimMakesIssuedPlanStaleWithoutChangingTargetOrRoots()
    {
        await using NativeV2ImportFixture f = await NativeV2ImportFixture.CreateAsync();
        await SeedTargetAsync(f);
        await using NativeTransferImportStage stage = await IncomingStageAsync(f);
        NativeMediaMergePlan plan = await PrepareMergeAsync(f, stage);
        Snapshot prepared = await SnapshotAsync(f);

        // A real ordinary metadata write, not a fabricated fence or a second token
        // nested under Exclusive. Snapshot AFTER this authorized mutation.
        await f.Catalog.UpdateObservedTitleAsync(NativeV2ImportFixture.Id, "synthetic title changed after planning");
        Snapshot before = await SnapshotAsync(f);
        ArchivedThreadView changed = Assert.IsType<ArchivedThreadView>(before.Target);
        Assert.Equal("synthetic title changed after planning", changed.Summary.Title);
        Assert.True(changed.Summary.Revision > plan.Target.Summary.Revision);
        Assert.Equal(ThreadSourceState.NotFound, changed.Summary.SourceState);
        Assert.Equal(prepared.Files, before.Files);
        Assert.NotEqual(plan.Generation, ReadFence(f).Generation);
        int phases = 0;
        using ProfileAdmissionToken admission = await f.ExclusiveAsync();
        var rejected = await f.Catalog.CommitNativeMediaMergeAdmittedAsync(admission, plan, f.Store,
            checkpoint: _ => phases++);

        Assert.Equal(ProfileExclusiveDisposition.SafeToResume, rejected.Disposition);
        Assert.Equal(new NativeMediaMergeResult(NativeMediaImportCommitStatus.StalePlan), rejected.Result);
        Assert.Equal(0, phases); // In particular, no PreparingSaved / durable claim.
        AssertNoMergePublication(f, plan);
        AssertSnapshotEqual(before, await SnapshotAsync(f));
        f.Catalog.EnsureTransferAdmission(admission);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConstructedPlanCannotBorrowIssuedIdentityOrHistoricalReceipt(bool alreadyCommitted)
    {
        await using NativeV2ImportFixture f = await NativeV2ImportFixture.CreateAsync();
        await SeedTargetAsync(f);
        await using NativeTransferImportStage stage = await IncomingStageAsync(f);
        NativeMediaMergePlan issued = await PrepareMergeAsync(f, stage);
        if (alreadyCommitted) await CommitFreshAsync(f, issued);
        NativeMediaMergeV2State state = Assert.IsType<NativeMediaMergeV2State>(issued.Representation);
        // Actual v2 constructor; copying all proposed changes and identity does not
        // insert this new reference into the owner's issued-plan weak table.
        NativeMediaMergePlan forged = new(f.Catalog, state.Materialization, issued.Instance, issued.Generation,
            issued.Target, issued.Roots, issued.AddedPosts, issued.UpdatedPosts, issued.AddedAttachments,
            state.SafeSelection, state.Counts, issued.OperationId, issued.RequestSha256,
            issued.OperationsParent, issued.ExportBudget);
        Assert.NotSame(issued, forged);
        Assert.Same(issued.Owner, forged.Owner);
        Assert.Equal(2, forged.PlanVersion);
        Assert.Equal(issued.OperationId, forged.OperationId);
        Assert.Equal(issued.RequestSha256, forged.RequestSha256);
        Assert.Same(state.Materialization, forged.Representation!.Materialization);
        Assert.Equal(state.SafeSelection.CanonicalJson, forged.Representation.SafeSelection.CanonicalJson);
        Snapshot before = await SnapshotAsync(f);
        int callbacks = 0, dispatches = 0;
        f.Catalog.BeforeWriteDispatchForTests = _ => dispatches++;
        using ProfileAdmissionToken admission = await f.ExclusiveAsync();
        Assert.Same(state, f.Catalog.RequireIssuedNativeMediaMergeV2Plan(admission, issued));
        Assert.Throws<InvalidOperationException>(() => f.Catalog.RequireIssuedNativeMediaMergeV2Plan(admission, forged));
        var rejected = await f.Catalog.CommitNativeMediaMergeAdmittedAsync(admission, forged, f.Store,
            beforeAdmission: () => callbacks++, checkpoint: _ => callbacks++);

        Assert.Equal(ProfileExclusiveDisposition.SafeToResume, rejected.Disposition);
        Assert.Equal(new NativeMediaMergeResult(NativeMediaImportCommitStatus.StalePlan), rejected.Result);
        Assert.Equal(0, callbacks);
        Assert.Equal(0, dispatches);
        AssertSnapshotEqual(before, await SnapshotAsync(f));
        if (alreadyCommitted) { AssertReceipt(f, issued); AssertCommittedOperation(f, issued); }
        else AssertNoMergePublication(f, issued);
        Assert.Same(state, f.Catalog.RequireIssuedNativeMediaMergeV2Plan(admission, issued));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ForeignOwnerIssuedPlanCannotMutateEitherProfileOrBorrowItsReceipt(bool alreadyCommitted)
    {
        await using NativeV2ImportFixture local = await NativeV2ImportFixture.CreateAsync();
        await using NativeV2ImportFixture foreign = await NativeV2ImportFixture.CreateAsync();
        await SeedTargetAsync(local);
        await SeedTargetAsync(foreign);
        await using NativeTransferImportStage stage = await IncomingStageAsync(foreign);
        NativeMediaMergePlan plan = await PrepareMergeAsync(foreign, stage);
        if (alreadyCommitted) await CommitFreshAsync(foreign, plan);
        Snapshot localBefore = await SnapshotAsync(local), foreignBefore = await SnapshotAsync(foreign);
        int callbacks = 0, dispatches = 0;
        local.Catalog.BeforeWriteDispatchForTests = _ => dispatches++;
        foreign.Catalog.BeforeWriteDispatchForTests = _ => dispatches++;
        using ProfileAdmissionToken localAdmission = await local.ExclusiveAsync();
        using ProfileAdmissionToken foreignAdmission = await foreign.ExclusiveAsync();
        Assert.Same(plan.Representation, foreign.Catalog.RequireIssuedNativeMediaMergeV2Plan(foreignAdmission, plan));
        Assert.Throws<InvalidOperationException>(() => local.Catalog.RequireIssuedNativeMediaMergeV2Plan(localAdmission, plan));
        var rejected = await local.Catalog.CommitNativeMediaMergeAdmittedAsync(localAdmission, plan, local.Store,
            beforeAdmission: () => callbacks++, checkpoint: _ => callbacks++);

        Assert.Equal(ProfileExclusiveDisposition.SafeToResume, rejected.Disposition);
        Assert.Equal(new NativeMediaMergeResult(NativeMediaImportCommitStatus.StalePlan), rejected.Result);
        Assert.Equal(0, callbacks);
        Assert.Equal(0, dispatches);
        Assert.Empty(ReadMergeOperations(local));
        Assert.Null(ReadReceipt(local, plan));
        AssertSnapshotEqual(localBefore, await SnapshotAsync(local));
        AssertSnapshotEqual(foreignBefore, await SnapshotAsync(foreign));
        if (alreadyCommitted) { AssertReceipt(foreign, plan); AssertCommittedOperation(foreign, plan); }
        else AssertNoMergePublication(foreign, plan);
        local.Catalog.EnsureTransferAdmission(localAdmission);
        foreign.Catalog.EnsureTransferAdmission(foreignAdmission);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnresolvedImportForSameResourceBlocksFreshMergePublication(bool preparedImport)
    {
        await using NativeV2ImportFixture f = await NativeV2ImportFixture.CreateAsync();
        await SeedTargetAsync(f);
        ArchivedThreadView seed = Assert.IsType<ArchivedThreadView>(await ReadTargetAsync(f));
        // Manufacture the overlap through existing owners, never INSERT fabricated
        // operation rows: delete only catalog content, interrupt a real new import,
        // then restore the old captured state against its still-existing v6 root.
        Assert.Single(await f.Catalog.DeleteThreadsAsync([NativeV2ImportFixture.Id]));
        NativeMediaImportPreparation preparation = await f.Catalog.PrepareNativeMediaImportAsync(f.Stage);
        Assert.Equal(NativeMediaImportError.None, preparation.Error);
        NativeMediaImportPlan pending = Assert.IsType<NativeMediaImportPlan>(preparation.Plan);
        Assert.Equal(2, pending.PlanVersion);
        NativeMediaImportPhase stopAt = preparedImport ? NativeMediaImportPhase.PreparedSaved : NativeMediaImportPhase.PreparingSaved;
        bool interrupted = false;
        using (ProfileAdmissionToken admission = await f.ExclusiveAsync())
        {
            var stopped = await f.Catalog.CommitNativeMediaImportAdmittedAsync(admission, pending, f.Store,
                checkpoint: phase =>
                {
                    if (phase != stopAt) return;
                    interrupted = true;
                    throw new IOException("synthetic same-resource import interruption");
                });
            Assert.True(interrupted);
            Assert.Equal(NativeMediaImportCommitStatus.ReconciliationRequired, stopped.Result.Status);
            Assert.Equal(ProfileExclusiveDisposition.RecoveryRequired, stopped.Disposition);
            f.Catalog.EnsureTransferAdmission(admission);
        }
        // Deliberate synthetic recovery-fixture mutation after releasing the owner;
        // NOT permission for Registry to resume over RecoveryRequired.
        await f.Catalog.CapturePreservingRevisionsAsync(new(NativeV2ImportFixture.Id, seed.Summary.Url,
            seed.Summary.Title, ThreadSourceState.NotFound, NativeV2ImportFixture.At,
            seed.Summary.MediaDirectory, seed.Posts) { DecimalPostOrder = seed.DecimalPostOrder });
        await using NativeTransferImportStage stage = await IncomingStageAsync(f);
        NativeMediaMergePlan plan = await PrepareMergeAsync(f, stage);
        Assert.Equal(pending.Resource, plan.Resource);
        Assert.NotEqual(pending.OperationId, plan.OperationId);
        Assert.Equal((plan.Instance, plan.Generation), ReadFence(f));
        using (SqliteConnection connection = ArchiveSql.OpenConnection(f.Catalog.DatabasePath, SqliteOpenMode.ReadOnly))
        {
            NativeMediaPersistedImportOperation unfinished = Assert.IsType<NativeMediaPersistedImportOperation>(
                ArchiveSql.ReadPersistedMediaImportOperation(connection, null, pending.OperationId, pending.RequestSha256));
            Assert.Equal(preparedImport ? NativeMediaImportOperationState.Prepared : NativeMediaImportOperationState.Preparing,
                unfinished.State);
            Assert.True(pending.Representation!.MatchesOperation(unfinished, unfinished.State));
            Assert.Null(ArchiveSql.ReadPersistedMediaImportReceipt(connection, null, pending.OperationId, pending.RequestSha256, 2));
            Assert.True(ArchiveSql.HasUnresolvedMediaOperation(connection, null, plan.Resource));
        }
        Snapshot before = await SnapshotAsync(f);
        Assert.Equal(3, Assert.IsType<ArchivedThreadView>(before.Target).PostCount);
        int phases = 0;
        using ProfileAdmissionToken mergeAdmission = await f.ExclusiveAsync();
        var rejected = await f.Catalog.CommitNativeMediaMergeAdmittedAsync(mergeAdmission, plan, f.Store,
            checkpoint: _ => phases++);

        Assert.Equal(ProfileExclusiveDisposition.RecoveryRequired, rejected.Disposition);
        Assert.Equal(new NativeMediaMergeResult(NativeMediaImportCommitStatus.ReconciliationRequired), rejected.Result);
        Assert.Equal(0, phases);
        AssertNoMergePublication(f, plan);
        AssertSnapshotEqual(before, await SnapshotAsync(f)); // Includes the unfinished import, unchanged.
        f.Catalog.EnsureTransferAdmission(mergeAdmission);
    }

    private static async Task SeedTargetAsync(NativeV2ImportFixture f)
    {
        using ProfileAdmissionToken admission = await f.ExclusiveAsync();
        var imported = await f.CommitAsync(admission);
        Assert.Equal(ProfileExclusiveDisposition.SafeToResume, imported.Disposition);
        Assert.Equal(NativeMediaImportCommitStatus.Committed, imported.Result.Status);
        Assert.False(imported.Result.IsReplay);
        ArchivedThreadView target = Assert.IsType<ArchivedThreadView>(await ReadTargetAsync(f));
        Assert.Equal(ThreadSourceState.NotFound, target.Summary.SourceState);
        Assert.Equal(3, target.PostCount);
        Assert.Single(target.MediaDirectories);
        f.Catalog.EnsureTransferAdmission(admission);
    }

    private static Task<NativeTransferImportStage> IncomingStageAsync(NativeV2ImportFixture f)
    {
        NativeTransferCatalog content = NativeV2ImportFixture.Content();
        TransferThread thread = content.Threads[0];
        return NativeV2ImportFixture.StageAsync(f.Files, content with { Threads = [thread with
        {
            Posts = thread.Posts.Add(NativeV2ImportFixture.Post(AddedPost, NativeV2ImportFixture.Attachment(AddedPost)))
        }] }, NativeV2ImportFixture.Original, NativeV2ImportFixture.Preview);
    }

    private static async Task<NativeMediaMergePlan> PrepareMergeAsync(NativeV2ImportFixture f, NativeTransferImportStage stage)
    {
        NativeMediaMergePreparation prepared = await f.Catalog.PrepareNativeMediaMergeAsync(stage);
        Assert.Equal(NativeMediaMergeError.None, prepared.Error);
        NativeMediaMergePlan plan = Assert.IsType<NativeMediaMergePlan>(prepared.Plan);
        Assert.Equal(2, plan.PlanVersion);
        NativeMediaMergeV2State state = Assert.IsType<NativeMediaMergeV2State>(plan.Representation);
        Assert.Equal(AddedCounts, state.Counts);
        Assert.True(plan.HasSafeCandidate);
        Assert.False(state.SafeSelection.IsEmpty);
        Assert.False(Directory.Exists(plan.OperationsParent));
        Assert.Equal(ThreadSourceState.NotFound, plan.Target.Summary.SourceState);
        return plan;
    }

    private static async Task<NativeMediaMergeResult> CommitFreshAsync(NativeV2ImportFixture f, NativeMediaMergePlan plan)
    {
        using ProfileAdmissionToken admission = await f.ExclusiveAsync();
        var committed = await f.Catalog.CommitNativeMediaMergeAdmittedAsync(admission, plan, f.Store);
        Assert.Equal(ProfileExclusiveDisposition.SafeToResume, committed.Disposition);
        Assert.Equal(AddedResult, committed.Result); // All v2 addition counters; not just Committed.
        f.Catalog.EnsureTransferAdmission(admission);
        return committed.Result;
    }

    private static NativeMediaPersistedMergeOperation[] ReadMergeOperations(NativeV2ImportFixture f)
    {
        using SqliteConnection connection = ArchiveSql.OpenConnection(f.Catalog.DatabasePath, SqliteOpenMode.ReadOnly);
        var page = ArchiveSql.ReadPersistedMediaMergeOperationsPage(connection, null);
        Assert.Null(page.NextOperationId);
        return page.Items.ToArray();
    }

    private static NativeMediaPersistedMergeReceipt? ReadReceipt(NativeV2ImportFixture f, NativeMediaMergePlan plan)
    {
        using SqliteConnection connection = ArchiveSql.OpenConnection(f.Catalog.DatabasePath, SqliteOpenMode.ReadOnly);
        return ArchiveSql.ReadPersistedMediaMergeReceipt(connection, null, plan.OperationId, plan.RequestSha256, 2);
    }

    private static void AssertReceipt(NativeV2ImportFixture f, NativeMediaMergePlan plan)
    {
        NativeMediaPersistedMergeReceipt receipt = Assert.IsType<NativeMediaPersistedMergeReceipt>(ReadReceipt(f, plan));
        Assert.Equal(new NativeMediaReceiptIdentity(plan.OperationId, plan.RequestSha256, 2), receipt.Identity);
        Assert.Equal(AddedCounts, receipt.Counts);
    }

    private static void AssertCommittedOperation(NativeV2ImportFixture f, NativeMediaMergePlan plan)
    {
        NativeMediaPersistedMergeOperation operation = Assert.Single(ReadMergeOperations(f));
        Assert.True(plan.Representation!.MatchesOperation(operation, NativeMediaImportOperationState.Committed));
        Assert.True(ArchiveSql.PersistedMediaMergeReceiptMatches(operation,
            Assert.IsType<NativeMediaPersistedMergeReceipt>(ReadReceipt(f, plan))));
    }

    private static void AssertNoMergePublication(NativeV2ImportFixture f, NativeMediaMergePlan plan)
    {
        Assert.Empty(ReadMergeOperations(f));
        Assert.Null(ReadReceipt(f, plan));
        Assert.False(Directory.Exists(plan.OperationsParent));
        Assert.False(File.Exists(plan.OperationsParent));
    }

    private static (string Instance, long Generation) ReadFence(NativeV2ImportFixture f)
    {
        using SqliteConnection connection = ArchiveSql.OpenConnection(f.Catalog.DatabasePath, SqliteOpenMode.ReadOnly);
        using SqliteTransaction transaction = connection.BeginTransaction(deferred: true);
        return ArchiveSql.ReadImportFence(connection, transaction);
    }

    private static async Task<ArchivedThreadView?> ReadTargetAsync(NativeV2ImportFixture f)
    {
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(f.Files.Profile);
        return await reader.ReadThreadAsync(NativeV2ImportFixture.Id);
    }

    private static async Task<Snapshot> SnapshotAsync(NativeV2ImportFixture f)
        => new(await ReadTargetAsync(f), SnapshotSql(f), SnapshotFiles(f));

    private static void AssertSnapshotEqual(Snapshot before, Snapshot after)
    {
        Assert.Equal(before.Rows, after.Rows);
        Assert.Equal(before.Files, after.Files);
        if (before.Target is null) { Assert.Null(after.Target); return; }
        ArchivedThreadView actual = Assert.IsType<ArchivedThreadView>(after.Target);
        Assert.Equal(before.Target.Summary, actual.Summary);
        Assert.Equal(before.Target.DecimalPostOrder, actual.DecimalPostOrder);
        Assert.Equal(before.Target.MediaDirectories.ToArray(), actual.MediaDirectories.ToArray());
        Assert.Equal(before.Target.PostCount, actual.PostCount);
        foreach (CapturedPost expected in before.Target.Posts)
        {
            CapturedPost found = Assert.Single(actual.Posts, post => post.PostId == expected.PostId);
            Assert.Equal(expected with { Attachments = null }, found with { Attachments = null });
            Assert.Equal(expected.Attachments is null, found.Attachments is null);
            if (expected.Attachments != null) Assert.Equal(expected.Attachments.ToArray(), found.Attachments!.ToArray());
        }
    }

    private static string[] SnapshotSql(NativeV2ImportFixture f)
    {
        using SqliteConnection connection = ArchiveSql.OpenConnection(f.Catalog.DatabasePath, SqliteOpenMode.ReadOnly);
        using SqliteTransaction transaction = connection.BeginTransaction(deferred: true);
        List<string> rows = [];
        foreach (string table in Tables)
        {
            // Identifiers are the fixed allowlist below; bytes/WAL layout are not the
            // catalog oracle. Include indexes, receipts, journals and content fence.
            using SqliteCommand command = ArchiveSql.Command(connection, transaction, "SELECT rowid,* FROM " + table + " ORDER BY rowid");
            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                object?[] values = new object?[reader.FieldCount];
                for (int i = 0; i < values.Length; i++) values[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                rows.Add(table + ":" + JsonSerializer.Serialize(values));
            }
        }
        return rows.ToArray();
    }

    private static string[] SnapshotFiles(NativeV2ImportFixture f)
        => Directory.EnumerateFileSystemEntries(f.Files.Root, "*", SearchOption.AllDirectories)
            .Where(path => path != f.Catalog.DatabasePath && !path.StartsWith(f.Catalog.DatabasePath + "-", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal).Select(path =>
            {
                string relative = Path.GetRelativePath(f.Files.Root, path);
                if (Directory.Exists(path)) return "directory:" + relative;
                // Live profile/store locks and retained stages are exclusive handles:
                // record their presence, not read bytes through someone else's lease.
                if (Path.GetFileName(path).EndsWith(".lock", StringComparison.Ordinal)
                    || path.StartsWith(f.Files.StageRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                    return "owned-handle:" + relative;
                return "file:" + relative + ":" + NativeV2ImportFixture.Hash(File.ReadAllBytes(path));
            }).ToArray();

    private static readonly string[] Tables =
    [
        "archive_threads", "archive_posts", "archive_attachments", "archive_media_directories",
        "archive_source_contracts", "archive_quotes", "archive_thread_search", "archive_post_search",
        "media_decisions", "archive_import_candidates", "archive_import_receipts", "archive_import_fence",
        "archive_media_import_operations", "archive_media_import_receipts",
        "archive_media_merge_operations", "archive_media_merge_receipts"
    ];

    private sealed record Snapshot(ArchivedThreadView? Target, string[] Rows, string[] Files);
}
