using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Interfaces;
using MeowChan.Storage;
using MeowChan.Transfer;
using Microsoft.Data.Sqlite;

namespace MeowChan.DesktopTests;

// Real writer -> retained ingress -> publicly started Desktop host -> admitted core ->
// fresh offline reader. No prepared-plan injection, manual admission or seeded SQL rows.
[Collection("Delivered plugin hosts")]
public sealed class NativeTransferImportHostTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(45);
    private static readonly ResourceId Id = new("4chan", "b", "7001");
    private static readonly DateTimeOffset At = new(2026, 11, 7, 12, 0, 0, TimeSpan.Zero);
    private const string Url = "https://boards.4chan.org/b/thread/7001";
    private const string Title = "synthetic native host import";
    private const string TargetHtml = "target-original-host-needle";
    private const string IncomingHtml = "incoming-conflict-host-needle";
    private const string AddedHtml = "safe-added-host-needle";
    private const string SafeSubject = "safe subject enrichment";
    private static readonly byte[] OriginalBytes = Enumerable.Range(0, 150_123).Select(i => (byte)(i * 31 % 251)).ToArray();
    private static readonly byte[] AddedBytes = Encoding.UTF8.GetBytes("synthetic independently exported safe original");
    private static readonly byte[] RetainedBytes = Encoding.UTF8.GetBytes("synthetic retained historical original");

    [Fact]
    public async Task ReadyRetainsStageWithoutCatalogWritesUntilExplicitApplyThenFreshOfflineReaderSeesCommit()
    {
        using Workspace files = new();
        string source = await files.WritePackageAsync(InitialCatalog(), OriginalBytes);
        byte[] sourceBefore = File.ReadAllBytes(source);
        await using (DesktopSessionHost host = files.CreateHost())
        {
            await StartAsync(host);
            CatalogState before = ReadCatalogState(files.Profile);
            AssertCounts(before, 0, 0, 0, 0, 0, 0);
            int sourceOpens = 0, commits = 0;
            await using NativeTransferImportSession session = host.CreateNativeTransferImportSession(files.Work);
            session.BeforeSourceOpenForTests = _ => { Interlocked.Increment(ref sourceOpens); return Task.CompletedTask; };
            host.NativeImportCheckpointForTests = phase =>
            { if (phase == NativeMediaImportPhase.SqlCommitted) Interlocked.Increment(ref commits); };
            Assert.Equal(NativeTransferImportState.Idle, session.Current.State);

            NativeTransferImportSnapshot ready = await session.InspectAsync(source).WaitAsync(Deadline);

            AssertReady(ready, new(Id, false, 2, 0, 1, 0));
            TransferImportPreview inventory = Assert.IsType<TransferImportPreview>(ready.Inventory);
            Assert.Equal(2, inventory.Posts);
            Assert.Equal(1, inventory.UniquePayloads);
            Assert.Equal((long)OriginalBytes.Length, inventory.UniquePayloadBytes);
            string stage = AssertOwnedStage(files.Work, sourceBefore.LongLength);
            Assert.Equal(before, ReadCatalogState(files.Profile));
            await using (ArchiveReader reader = await ArchiveReader.OpenAsync(files.Profile).WaitAsync(Deadline))
                Assert.Null(await reader.ReadThreadAsync(Id).WaitAsync(Deadline));
            Assert.Equal(0, Volatile.Read(ref commits));

            // A stale UI generation cannot execute the otherwise real, retained plan.
            Assert.Equal(ready, await session.ApplyAsync(ready.Generation - 1).WaitAsync(Deadline));
            Assert.True(File.Exists(stage));
            Assert.Equal(before, ReadCatalogState(files.Profile));

            NativeTransferImportSnapshot completed = await session.ApplyAsync(ready.Generation).WaitAsync(Deadline);

            AssertCompleted(completed, ready.Generation, new(NativeMediaImportCommitStatus.Committed, 1, 2, 0, 1, 0, false));
            Assert.Equal(1, Volatile.Read(ref sourceOpens));
            Assert.Equal(1, Volatile.Read(ref commits));
            Assert.Equal(DesktopSessionState.Ready, host.State);
            Assert.False(File.Exists(stage));
            Assert.Empty(Directory.GetFileSystemEntries(files.Work));
            Assert.Equal(sourceBefore, File.ReadAllBytes(source));
            AssertCounts(ReadCatalogState(files.Profile), 1, 2, 1, 1, 1, 0);
            AssertCommittedReceipt(files.Profile, merge: false, completed.Result!);
        }

        // No host, provider, input handle or ingress stage is needed for offline reads.
        await AssertInitialThreadOfflineAsync(files.Profile);
    }

    [Fact]
    public async Task ExplicitNewAndMergeScopesRejectTargetMismatchWithoutFallbackOrWrites()
    {
        using Workspace newFiles = new();
        string seed = await newFiles.WritePackageAsync(InitialCatalog(), OriginalBytes);
        string duplicate = await newFiles.WritePackageAsync(InitialCatalog(), OriginalBytes);
        await using (DesktopSessionHost host = newFiles.CreateHost())
        {
            await StartAsync(host);
            await ImportInitialAsync(host, newFiles, seed);
            CatalogState before = ReadCatalogState(newFiles.Profile);
            await using NativeTransferImportSession session = host.CreateNativeTransferImportSession(newFiles.Work);

            NativeTransferImportSnapshot rejectedNew = await session.InspectAsync(duplicate, NativeTransferImportScope.New)
                .WaitAsync(Deadline);

            Assert.Equal(NativeTransferImportState.Failed, rejectedNew.State);
            Assert.Equal(NativeTransferImportError.ScopeTargetMismatch, rejectedNew.Error);
            Assert.NotNull(rejectedNew.Inventory);
            Assert.Null(rejectedNew.Plan);
            Assert.Null(rejectedNew.Result);
            Assert.Equal(before, ReadCatalogState(newFiles.Profile));
            Assert.Empty(Directory.GetFileSystemEntries(newFiles.Work));
        }

        using Workspace mergeFiles = new();
        string missingTarget = await mergeFiles.WritePackageAsync(InitialCatalog(), OriginalBytes);
        await using (DesktopSessionHost host = mergeFiles.CreateHost())
        {
            await StartAsync(host);
            CatalogState before = ReadCatalogState(mergeFiles.Profile);
            await using NativeTransferImportSession session = host.CreateNativeTransferImportSession(mergeFiles.Work);

            NativeTransferImportSnapshot rejectedMerge = await session.InspectAsync(missingTarget, NativeTransferImportScope.Merge)
                .WaitAsync(Deadline);

            Assert.Equal(NativeTransferImportState.Failed, rejectedMerge.State);
            Assert.Equal(NativeTransferImportError.ScopeTargetMismatch, rejectedMerge.Error);
            Assert.NotNull(rejectedMerge.Inventory);
            Assert.Null(rejectedMerge.Plan);
            Assert.Null(rejectedMerge.Result);
            Assert.Equal(before, ReadCatalogState(mergeFiles.Profile));
            Assert.Empty(Directory.GetFileSystemEntries(mergeFiles.Work));
        }
    }

    [Theory]
    [InlineData("overwrite")]
    [InlineData("replace")]
    [InlineData("delete")]
    public async Task SourceMutationAfterReadyCannotChangeImportedMetadataOrVerifiedBytes(string change)
    {
        using Workspace files = new();
        string source = await files.WritePackageAsync(InitialCatalog(), OriginalBytes);
        byte[] replacementBytes = Encoding.UTF8.GetBytes("not the accepted source bytes");
        string replacement = await files.WritePackageAsync(InitialCatalog(replacementBytes, "replacement-only-text"), replacementBytes);
        byte[] replacementPackage = File.ReadAllBytes(replacement);
        await using (DesktopSessionHost host = files.CreateHost())
        {
            await StartAsync(host);
            await using NativeTransferImportSession session = host.CreateNativeTransferImportSession(files.Work);
            int opens = 0;
            session.BeforeSourceOpenForTests = _ => { Interlocked.Increment(ref opens); return Task.CompletedTask; };
            NativeTransferImportSnapshot ready = await session.InspectAsync(source).WaitAsync(Deadline);
            AssertReady(ready, new(Id, false, 2, 0, 1, 0));
            string stage = AssertOwnedStage(files.Work, new FileInfo(source).Length);

            // Mutate only after Ready. A different valid package detects an accidental reopen,
            // not merely rejection of a missing or corrupted source at Apply.
            if (change == "overwrite") File.WriteAllBytes(source, replacementPackage);
            else
            {
                File.Delete(source);
                if (change == "replace") File.Move(replacement, source);
            }
            Assert.True(File.Exists(stage));

            NativeTransferImportSnapshot completed = await session.ApplyAsync(ready.Generation).WaitAsync(Deadline);

            AssertCompleted(completed, ready.Generation, new(NativeMediaImportCommitStatus.Committed, 1, 2, 0, 1, 0, false));
            Assert.Equal(1, Volatile.Read(ref opens));
            Assert.Empty(Directory.GetFileSystemEntries(files.Work));
            Assert.Equal(DesktopSessionState.Ready, host.State);
            if (change == "delete") Assert.False(File.Exists(source));
            else Assert.Equal(replacementPackage, File.ReadAllBytes(source));
            AssertCounts(ReadCatalogState(files.Profile), 1, 2, 1, 1, 1, 0);
            AssertCommittedReceipt(files.Profile, merge: false, completed.Result!);
        }
        await AssertInitialThreadOfflineAsync(files.Profile);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IndependentPackageMerges404SafeAdditionsAndPreservesTargetAndDurableConflict(bool withRetainedBinding)
    {
        using Workspace files = new();
        string first = await files.WritePackageAsync(InitialCatalog(), OriginalBytes);
        string second = await files.WritePackageAsync(OverlappingCatalog(withRetainedBinding),
            withRetainedBinding ? [OriginalBytes, AddedBytes, RetainedBytes] : [OriginalBytes, AddedBytes]);
        ArchivedThreadView before;
        ResolvedMediaAttachment original;
        ReceiptIdentity importReceipt;
        ReceiptIdentity mergeReceipt;
        await using (DesktopSessionHost host = files.CreateHost())
        {
            await StartAsync(host);
            NativeTransferImportSnapshot seeded = await ImportInitialAsync(host, files, first);
            importReceipt = AssertCommittedReceipt(files.Profile, merge: false, seeded.Result!);
            before = await ReadThreadAsync(files.Profile);
            original = await ReadVerifiedAttachmentAsync(before, "15", OriginalBytes);
            Assert.Equal(ThreadSourceState.NotFound, before.Summary.SourceState);
            CatalogState catalogBeforePreview = ReadCatalogState(files.Profile);
            await using NativeTransferImportSession session = host.CreateNativeTransferImportSession(files.Work);

            NativeTransferImportSnapshot ready = await session.InspectAsync(second).WaitAsync(Deadline);

            // The retained variant requires the real merge-first planner; the narrower
            // new-thread importer must not reject it before the existing target is checked.
            AssertReady(ready, new(Id, true, 1, 1, 1, 1));
            Assert.Equal(catalogBeforePreview, ReadCatalogState(files.Profile));
            AssertOwnedStage(files.Work, new FileInfo(second).Length);
            NativeTransferImportSnapshot completed = await session.ApplyAsync(ready.Generation).WaitAsync(Deadline);

            AssertCompleted(completed, ready.Generation, new(NativeMediaImportCommitStatus.Committed, 0, 1, 1, 1, 1, false));
            mergeReceipt = AssertCommittedReceipt(files.Profile, merge: true, completed.Result!);
            Assert.NotEqual(importReceipt.OperationId, mergeReceipt.OperationId);
            Assert.NotEqual(importReceipt.Fingerprint, mergeReceipt.Fingerprint);
            AssertCounts(ReadCatalogState(files.Profile), 1, 3, 2, 2, 1, 1);
            Assert.Empty(Directory.GetFileSystemEntries(files.Work));
            Assert.Equal(DesktopSessionState.Ready, host.State);
        }

        ArchivedThreadView after = await ReadThreadAsync(files.Profile);
        Assert.Equal(before.Summary with { Revision = after.Summary.Revision, PostCount = 3 }, after.Summary);
        Assert.True(after.Summary.Revision > before.Summary.Revision);
        Assert.Equal(before.DecimalPostOrder, after.DecimalPostOrder);
        Assert.Equal(new[] { "15", "16", "17" }, after.Posts.Select(post => post.PostId));
        Assert.Equal(TargetHtml, after.Posts.Single(post => post.PostId == "15").Html);
        Assert.Equal(SafeSubject, after.Posts.Single(post => post.PostId == "16").Subject);
        Assert.Equal(before.Posts.Single(post => post.PostId == "16").Html, after.Posts.Single(post => post.PostId == "16").Html);
        Assert.Equal(AddedHtml, after.Posts.Single(post => post.PostId == "17").Html);
        ResolvedMediaAttachment unchanged = await ReadVerifiedAttachmentAsync(after, "15", OriginalBytes);
        Assert.Equal(original.Binding, unchanged.Binding);
        Assert.Equal(original.Asset.AssetId, unchanged.Asset.AssetId);
        Assert.Equal(original.Snapshot.Directory, unchanged.Snapshot.Directory);
        Assert.Equal(importReceipt.OperationId, unchanged.Binding!.PassiveImport!.OperationId);
        ResolvedMediaAttachment added = await ReadVerifiedAttachmentAsync(after, "17", AddedBytes);
        Assert.Equal(mergeReceipt.OperationId, added.Binding!.PassiveImport!.OperationId);
        Assert.Equal(mergeReceipt.Fingerprint, added.Binding.PassiveImport.PackageFingerprint);
        AssertDurableMergeConflict(files.Profile, mergeReceipt.OperationId, after.MediaDirectories);

        if (withRetainedBinding)
        {
            Assert.DoesNotContain(after.Posts, post => post.PostId == "900");
            ResolvedMediaAttachment retained = await ReadVerifiedAsync(after, "900",
                Attachment("900", "11900", RetainedBytes).Metadata, RetainedBytes);
            Assert.Equal(mergeReceipt.OperationId, retained.Binding!.PassiveImport!.OperationId);
        }
    }

    [Fact]
    public async Task RepeatedApplyOfOneGenerationExecutesOneRealCommitAndCannotBeDisplacedByInspect()
    {
        using Workspace files = new();
        string source = await files.WritePackageAsync(InitialCatalog(), OriginalBytes);
        string otherSource = await files.WritePackageAsync(OverlappingCatalog(), OriginalBytes, AddedBytes);
        await using DesktopSessionHost host = files.CreateHost();
        await StartAsync(host);
        await using NativeTransferImportSession session = host.CreateNativeTransferImportSession(files.Work);
        int opens = 0, preparations = 0, entries = 0, preparing = 0, commits = 0;
        session.BeforeSourceOpenForTests = _ => { Interlocked.Increment(ref opens); return Task.CompletedTask; };
        session.AfterPreparationForTests = _ => { Interlocked.Increment(ref preparations); return Task.CompletedTask; };
        NativeTransferImportSnapshot ready = await session.InspectAsync(source).WaitAsync(Deadline);
        AssertReady(ready, new(Id, false, 2, 0, 1, 0));
        CatalogState before = ReadCatalogState(files.Profile);
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        host.BeforeNativeImportCommitForTests = async () =>
        {
            Interlocked.Increment(ref entries);
            entered.TrySetResult();
            await release.Task.WaitAsync(Deadline);
        };
        host.NativeImportCheckpointForTests = phase =>
        {
            if (phase == NativeMediaImportPhase.PreparingSaved) Interlocked.Increment(ref preparing);
            if (phase == NativeMediaImportPhase.SqlCommitted) Interlocked.Increment(ref commits);
        };

        Task<NativeTransferImportSnapshot> first = session.ApplyAsync(ready.Generation);
        Task<NativeTransferImportSnapshot>[] duplicates = [];
        try
        {
            await entered.Task.WaitAsync(Deadline);
            duplicates = Enumerable.Range(0, 8).Select(_ => session.ApplyAsync(ready.Generation)).ToArray();
            Assert.False(first.IsCompleted);
            Assert.Equal(NativeTransferImportState.Applying, session.Current.State);
            Assert.Equal(ready.Generation, session.Current.Generation);
            Assert.Equal(1, Volatile.Read(ref entries));
            Assert.Equal(0, Volatile.Read(ref preparing));
            Assert.Equal(before, ReadCatalogState(files.Profile));
            AssertOwnedStage(files.Work, new FileInfo(source).Length);
            NativeTransferImportSnapshot whileApplying = await session.InspectAsync(otherSource).WaitAsync(Deadline);
            Assert.Equal(NativeTransferImportState.Applying, whileApplying.State);
            Assert.Equal(ready.Generation, whileApplying.Generation);
            Assert.Equal(ready.Plan, whileApplying.Plan);
            Assert.Equal(1, Volatile.Read(ref opens));
            Assert.Equal(1, Volatile.Read(ref preparations));
        }
        finally
        {
            release.TrySetResult();
            // Always drain the actual commit, including assertion-failure paths, before
            // fixture/session/host disposal. Watchdogs do not establish ordering.
            await Task.WhenAll(duplicates.Prepend(first)).WaitAsync(Deadline);
        }

        NativeTransferImportSnapshot completed = await first.WaitAsync(Deadline);
        AssertCompleted(completed, ready.Generation, new(NativeMediaImportCommitStatus.Committed, 1, 2, 0, 1, 0, false));
        foreach (Task<NativeTransferImportSnapshot> duplicate in duplicates)
            Assert.Equal(completed, await duplicate.WaitAsync(Deadline));
        CatalogState committed = ReadCatalogState(files.Profile);
        for (int retry = 0; retry < 3; retry++)
            Assert.Equal(completed, await session.ApplyAsync(ready.Generation).WaitAsync(Deadline));
        Assert.Equal(committed, ReadCatalogState(files.Profile));
        AssertCounts(committed, 1, 2, 1, 1, 1, 0);
        AssertCommittedReceipt(files.Profile, merge: false, completed.Result!);
        Assert.Equal(1, Volatile.Read(ref entries));
        Assert.Equal(1, Volatile.Read(ref preparing));
        Assert.Equal(1, Volatile.Read(ref commits));
        Assert.Equal(1, Volatile.Read(ref preparations));
        Assert.Empty(Directory.GetFileSystemEntries(files.Work));
        Assert.Equal(DesktopSessionState.Ready, host.State);
        await AssertInitialThreadOfflineAsync(files.Profile);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChangedTargetBetweenPrepareAndApplyNeedsReviewWithoutBlindReplan(bool existingTarget)
    {
        using Workspace files = new();
        string seed = await files.WritePackageAsync(InitialCatalog(), OriginalBytes);
        string incoming = await files.WritePackageAsync(OverlappingCatalog(), OriginalBytes, AddedBytes);
        await using DesktopSessionHost host = files.CreateHost();
        await StartAsync(host);
        if (existingTarget) await ImportInitialAsync(host, files, seed);
        await using NativeTransferImportSession session = host.CreateNativeTransferImportSession(files.Work);
        int opens = 0, preparations = 0, entries = 0, corePhases = 0;
        session.BeforeSourceOpenForTests = _ => { Interlocked.Increment(ref opens); return Task.CompletedTask; };
        session.AfterPreparationForTests = _ => { Interlocked.Increment(ref preparations); return Task.CompletedTask; };
        NativeTransferImportSnapshot ready = await session.InspectAsync(incoming).WaitAsync(Deadline);
        AssertReady(ready, existingTarget ? new(Id, true, 1, 1, 1, 1) : new(Id, false, 3, 0, 2, 0));
        long preparedGeneration = ReadCatalogState(files.Profile).Generation;
        string retainedStage = AssertOwnedStage(files.Work, new FileInfo(incoming).Length);

        // Both interleavings use normal host operations after the real preview. A new
        // target races an import plan; an ordinary capture toggle invalidates a merge.
        if (existingTarget) await host.SetCaptureEnabledAsync(Id, true).WaitAsync(Deadline);
        else await ImportInitialAsync(host, files, seed, expectEmptyWork: false);
        Assert.True(File.Exists(retainedStage)); // Another session cleaned only its own ingress.
        CatalogState changed = ReadCatalogState(files.Profile);
        Assert.True(changed.Generation > preparedGeneration);
        ArchivedThreadView target = await ReadThreadAsync(files.Profile);
        host.BeforeNativeImportCommitForTests = () => { Interlocked.Increment(ref entries); return Task.CompletedTask; };
        host.NativeImportCheckpointForTests = _ => Interlocked.Increment(ref corePhases);

        NativeTransferImportSnapshot stale = await session.ApplyAsync(ready.Generation).WaitAsync(Deadline);

        Assert.Equal(NativeTransferImportState.NeedsReview, stale.State);
        Assert.Equal(NativeTransferImportError.None, stale.Error);
        Assert.Equal(ready.Generation, stale.Generation);
        Assert.Equal(new NativeTransferImportResult(NativeMediaImportCommitStatus.StalePlan, 0, 0, 0, 0, 0, false), stale.Result);
        Assert.False(stale.CleanupRetained);
        Assert.Equal(changed, ReadCatalogState(files.Profile));
        AssertCounts(changed, 1, 2, 1, 1, 1, 0);
        Assert.Empty(Directory.GetFileSystemEntries(files.Work));
        Assert.Equal(DesktopSessionState.Ready, host.State);
        Assert.Equal(stale, await session.ApplyAsync(ready.Generation).WaitAsync(Deadline));
        Assert.Equal(1, Volatile.Read(ref opens));
        Assert.Equal(1, Volatile.Read(ref preparations));
        Assert.Equal(1, Volatile.Read(ref entries));
        Assert.Equal(0, Volatile.Read(ref corePhases)); // Stale before claim: no Preparing/candidate/commit.
        Assert.Equal(changed, ReadCatalogState(files.Profile));
        ArchivedThreadView after = await ReadThreadAsync(files.Profile);
        Assert.Equal(target.Summary, after.Summary);
        Assert.Equal(target.MediaDirectories, after.MediaDirectories);
        Assert.Equal(existingTarget, after.Summary.CaptureEnabled);
        Assert.Equal(ThreadSourceState.NotFound, after.Summary.SourceState);
        Assert.Equal(new[] { "15", "16" }, after.Posts.Select(post => post.PostId));
        Assert.Equal(TargetHtml, after.Posts.Single(post => post.PostId == "15").Html);
        Assert.Null(after.Posts.Single(post => post.PostId == "16").Subject);
        await ReadVerifiedAttachmentAsync(after, "15", OriginalBytes);
    }

    private static async Task StartAsync(DesktopSessionHost host)
    {
        Assert.Null(host.Plugins);
        await host.StartAsync().WaitAsync(Deadline);
        Assert.Equal(DesktopSessionState.Ready, host.State);
        Assert.Null(host.StartupError);
        Assert.Null(host.Plugins);
    }

    private static async Task<NativeTransferImportSnapshot> ImportInitialAsync(DesktopSessionHost host,
        Workspace files, string source, bool expectEmptyWork = true)
    {
        await using NativeTransferImportSession session = host.CreateNativeTransferImportSession(files.Work);
        NativeTransferImportSnapshot ready = await session.InspectAsync(source).WaitAsync(Deadline);
        AssertReady(ready, new(Id, false, 2, 0, 1, 0));
        NativeTransferImportSnapshot completed = await session.ApplyAsync(ready.Generation).WaitAsync(Deadline);
        AssertCompleted(completed, ready.Generation, new(NativeMediaImportCommitStatus.Committed, 1, 2, 0, 1, 0, false));
        if (expectEmptyWork) Assert.Empty(Directory.GetFileSystemEntries(files.Work));
        return completed;
    }

    private static void AssertReady(NativeTransferImportSnapshot value, NativeTransferImportPlanSummary plan)
    {
        Assert.Equal(NativeTransferImportState.Ready, value.State);
        Assert.Equal(NativeTransferImportError.None, value.Error);
        Assert.True(value.Generation > 0);
        Assert.NotNull(value.Inventory);
        Assert.Equal(plan, value.Plan);
        Assert.Null(value.Result);
        Assert.False(value.CleanupRetained); // A warning, not the ordinary retained-stage lifetime.
    }

    private static void AssertCompleted(NativeTransferImportSnapshot value, long generation, NativeTransferImportResult result)
    {
        Assert.Equal(NativeTransferImportState.Completed, value.State);
        Assert.Equal(NativeTransferImportError.None, value.Error);
        Assert.Equal(generation, value.Generation);
        Assert.Equal(result, value.Result);
        Assert.False(value.CleanupRetained);
    }

    private static string AssertOwnedStage(string work, long sourceLength)
    {
        string directory = Assert.Single(Directory.GetDirectories(work));
        Assert.StartsWith(".meowchan-native-import-", Path.GetFileName(directory));
        Assert.Empty(Directory.GetFiles(work));
        string stage = Assert.Single(Directory.GetFiles(directory));
        Assert.EndsWith(".pending", stage);
        Assert.Equal(sourceLength, new FileInfo(stage).Length);
        return stage; // Observe metadata only: the stage still owns its exclusive handle.
    }

    private static async Task<ArchivedThreadView> ReadThreadAsync(string profile)
    {
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(profile).WaitAsync(Deadline);
        return Assert.IsType<ArchivedThreadView>(await reader.ReadThreadAsync(Id).WaitAsync(Deadline));
    }

    private static async Task AssertInitialThreadOfflineAsync(string profile)
    {
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(profile).WaitAsync(Deadline);
        ArchivedThreadView view = Assert.IsType<ArchivedThreadView>(await reader.ReadThreadAsync(Id).WaitAsync(Deadline));
        Assert.Single((await reader.ListThreadsAsync().WaitAsync(Deadline)).Items);
        Assert.Equal(Title, view.Summary.Title);
        Assert.Equal(ThreadSourceState.NotFound, view.Summary.SourceState);
        Assert.False(view.Summary.CaptureEnabled);
        Assert.Null(view.Summary.LastCapturedAt);
        Assert.Equal(false, view.DecimalPostOrder);
        Assert.Equal(new[] { "15", "16" }, view.Posts.Select(post => post.PostId));
        Assert.Equal(TargetHtml, view.Posts.Single(post => post.PostId == "15").Html);
        Assert.Equal(2, view.PostCount);
        Assert.Single((await reader.SearchThreadsAsync(new(TargetHtml)).WaitAsync(Deadline)).Items);
        Assert.Single((await reader.GetBacklinksAsync(new(Id, "15")).WaitAsync(Deadline)).Items);
        Assert.Equal(view.MediaDirectories, await reader.GetMediaDirectoriesAsync(Id).WaitAsync(Deadline));
        Assert.Equal(view.Summary.MediaDirectory, Assert.Single(view.MediaDirectories));
        ResolvedMediaAttachment resolved = await ReadVerifiedAttachmentAsync(view, "15", OriginalBytes);
        ReceiptIdentity receipt = AssertCommittedReceipt(profile, merge: false,
            new(NativeMediaImportCommitStatus.Committed, 1, 2, 0, 1, 0, false));
        Assert.Equal(receipt.OperationId, resolved.Binding!.PassiveImport!.OperationId);
        Assert.Equal(receipt.Fingerprint, resolved.Binding.PassiveImport.PackageFingerprint);
    }

    private static Task<ResolvedMediaAttachment> ReadVerifiedAttachmentAsync(ArchivedThreadView view, string postId, byte[] expected)
        => ReadVerifiedAsync(view, postId, Assert.Single(view.Posts.Single(post => post.PostId == postId).Attachments!), expected);

    private static async Task<ResolvedMediaAttachment> ReadVerifiedAsync(ArchivedThreadView view, string postId,
        CapturedAttachment attachment, byte[] expected)
    {
        Assert.Null(attachment.PlannedPath);
        Assert.NotNull(attachment.AttachmentRevisionKey);
        MediaAssetSnapshot[] snapshots = await Task.WhenAll(view.MediaDirectories.Select(root => MediaAssetReader.OpenAsync(root))).WaitAsync(Deadline);
        ResolvedMediaAttachment resolved = Assert.IsType<ResolvedMediaAttachment>(MediaAttachmentResolver.Resolve(snapshots, Id, postId, attachment));
        Assert.False(resolved.IsLegacy);
        MediaAttachmentBinding binding = Assert.IsType<MediaAttachmentBinding>(resolved.Binding);
        Assert.Equal(MediaAcceptanceKind.PassiveImport, binding.Acceptance.Kind);
        Assert.Null(binding.Acceptance.ProfileId);
        Assert.Null(binding.Acceptance.PolicyRevision);
        Assert.Null(binding.Acceptance.PluginOwnerId);
        Assert.Null(binding.Acceptance.SourceId);
        Assert.NotNull(binding.PassiveImport);
        await using Stream verified = await resolved.Snapshot.OpenVerifiedReadAsync(resolved.Asset.AssetId).WaitAsync(Deadline);
        using MemoryStream output = new();
        await verified.CopyToAsync(output).WaitAsync(Deadline);
        Assert.Equal(expected, output.ToArray());
        return resolved;
    }

    // Read-only journal evidence. These helpers never create a catalog, acquire a writer,
    // manufacture a receipt, or alter the real planner/host lifecycle.
    private sealed record CatalogState(long Generation, long Threads, long Posts, long Attachments,
        long MediaRoots, long ImportOperations, long ImportReceipts, long MergeOperations, long MergeReceipts);
    private sealed record ReceiptIdentity(string OperationId, string Fingerprint);

    private static SqliteConnection OpenReadOnly(string profile)
    {
        SqliteConnection connection = new(new SqliteConnectionStringBuilder
        { DataSource = Path.Combine(profile, "archive.db"), Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        connection.Open();
        return connection;
    }

    private static CatalogState ReadCatalogState(string profile)
    {
        using SqliteConnection connection = OpenReadOnly(profile);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT generation,
                (SELECT count(*) FROM archive_threads), (SELECT count(*) FROM archive_posts),
                (SELECT count(*) FROM archive_attachments), (SELECT count(*) FROM archive_media_directories),
                (SELECT count(*) FROM archive_media_import_operations), (SELECT count(*) FROM archive_media_import_receipts),
                (SELECT count(*) FROM archive_media_merge_operations), (SELECT count(*) FROM archive_media_merge_receipts)
            FROM archive_import_fence WHERE singleton=1
            """;
        using SqliteDataReader row = command.ExecuteReader();
        Assert.True(row.Read());
        return new(row.GetInt64(0), row.GetInt64(1), row.GetInt64(2), row.GetInt64(3), row.GetInt64(4),
            row.GetInt64(5), row.GetInt64(6), row.GetInt64(7), row.GetInt64(8));
    }

    private static void AssertCounts(CatalogState actual, int threads, int posts, int attachments, int roots, int imports, int merges)
        => Assert.Equal(new CatalogState(actual.Generation, threads, posts, attachments, roots, imports, imports, merges, merges), actual);

    private static ReceiptIdentity AssertCommittedReceipt(string profile, bool merge, NativeTransferImportResult expected)
    {
        using SqliteConnection connection = OpenReadOnly(profile);
        using SqliteCommand command = connection.CreateCommand();
        string kind = merge ? "merge" : "import"; // Literal host schema names, not package input.
        string counts = merge ? "r.new_posts,r.updated_posts,r.new_attachments,r.conflicts" : "r.post_count,0,r.attachment_count,0";
        command.CommandText = $"""
            SELECT o.operation_id,o.package_fingerprint,o.state,o.request_sha256,r.request_sha256,{counts}
            FROM archive_media_{kind}_operations AS o
            JOIN archive_media_{kind}_receipts AS r ON r.operation_id=o.operation_id
            """;
        using SqliteDataReader row = command.ExecuteReader();
        Assert.True(row.Read());
        ReceiptIdentity identity = new(row.GetString(0), row.GetString(1));
        Assert.True(Guid.TryParseExact(identity.OperationId, "D", out _));
        Assert.Equal("Committed", row.GetString(2));
        Assert.Equal(row.GetString(3), row.GetString(4));
        Assert.Equal(new NativeTransferImportResult(NativeMediaImportCommitStatus.Committed, merge ? 0 : 1,
            row.GetInt32(5), row.GetInt32(6), row.GetInt32(7), row.GetInt32(8), false), expected);
        Assert.False(row.Read());
        return identity;
    }

    private static void AssertDurableMergeConflict(string profile, string operation, IReadOnlyList<string> activeRoots)
    {
        using SqliteConnection connection = OpenReadOnly(profile);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT catalog_bytes,full_directory,safe_directory FROM archive_media_merge_operations WHERE operation_id=$op";
        command.Parameters.AddWithValue("$op", operation);
        using SqliteDataReader row = command.ExecuteReader();
        Assert.True(row.Read());
        using JsonDocument catalog = JsonDocument.Parse(row.GetFieldValue<byte[]>(0));
        JsonElement thread = Assert.Single(catalog.RootElement.GetProperty("threads").EnumerateArray());
        JsonElement post = Assert.Single(thread.GetProperty("posts").EnumerateArray(), p => p.GetProperty("id").GetString() == "15");
        Assert.Equal(IncomingHtml, post.GetProperty("html").GetString());
        string full = row.GetString(1), safe = row.GetString(2);
        Assert.True(Directory.Exists(full));
        Assert.DoesNotContain(full, activeRoots); // Conflicting observation remains inactive, not silently discarded/adopted.
        Assert.Contains(safe, activeRoots);
        Assert.False(row.Read());
    }

    private static NativeTransferCatalog InitialCatalog(byte[]? payload = null, string? html = null)
    {
        TransferAttachment attachment = Attachment("15", "111", payload ?? OriginalBytes);
        return new(At, [new(Id, Url, Title, ThreadSourceState.NotFound, At, At, 9, false,
            [new(new("15", 0, PostFieldPresence.All, At, "", "", html ?? TargetHtml, false, [attachment.Metadata]), [attachment]),
             new(new("16", 1, PostFieldPresence.Html, Html: "<a href=\"#p15\">&gt;&gt;15</a>"), [])])]);
    }

    private static NativeTransferCatalog OverlappingCatalog(bool retained = false)
    {
        NativeTransferCatalog content = InitialCatalog();
        TransferThread thread = content.Threads[0];
        TransferAttachment added = Attachment("17", "117", AddedBytes);
        return content with { Threads = [thread with
        {
            Posts = [thread.Posts[0] with { Metadata = thread.Posts[0].Metadata with { Html = IncomingHtml } },
                thread.Posts[1] with { Metadata = thread.Posts[1].Metadata with
                    { Fields = PostFieldPresence.Html | PostFieldPresence.Subject, Subject = SafeSubject } },
                new(new("17", 2, PostFieldPresence.Html | PostFieldPresence.Attachments, Html: AddedHtml,
                    Attachments: [added.Metadata]), [added])],
            RetainedBindings = retained ? [new("900", Attachment("900", "11900", RetainedBytes))] : []
        }] };
    }

    private static TransferAttachment Attachment(string postId, string mediaId, byte[] bytes)
    {
        MediaProviderMetadata provider = new(mediaId, "original", "https://i.4cdn.org/b/" + mediaId + ".jpg",
            "synthetic-" + postId + ".bin", bytes.Length, Convert.ToBase64String(MD5.HashData(bytes)), Hash(bytes))
        { ExpectedSha1 = Convert.ToBase64String(SHA1.HashData(bytes)) };
        CapturedAttachment metadata = new(0, provider.MediaId, provider.Variant, provider.Url, provider.OriginalFileName,
            ExpectedSize: provider.ExpectedSize, ExpectedMd5: provider.ExpectedMd5)
        {
            ExpectedSha256 = provider.ExpectedSha256, ExpectedSha1 = provider.ExpectedSha1,
            AttachmentRevisionKey = MediaRevisionKeys.CreateAttachmentKey(Id, postId, 0, provider)
        };
        return new(metadata, TransferMediaRole.Original, TransferContentStatus.Contained, Hash(bytes), null);
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private sealed class Workspace : IDisposable
    {
        private readonly TempDirectory _directory = new();
        private readonly string _sources;
        internal string Profile { get; }
        internal string Work { get; }
        internal Workspace()
        {
            Profile = Directory.CreateDirectory(Path.Combine(_directory.Path, "profile")).FullName;
            Work = Directory.CreateDirectory(Path.Combine(_directory.Path, "ingress")).FullName;
            _sources = Directory.CreateDirectory(Path.Combine(_directory.Path, "sources")).FullName;
            new Config { AutomaticUpdateChecks = false, SaveUrlsOnExit = true, DownloadsPaused = true,
                DownloadPath = Path.Combine(Profile, "absent-media") }.SaveConfig(Profile);
        }
        internal DesktopSessionHost CreateHost() => new(Profile, Array.Empty<IImageBoard>());
        internal async Task<string> WritePackageAsync(NativeTransferCatalog content, params byte[][] payloads)
        {
            string path = Path.Combine(_sources, Guid.NewGuid().ToString("N") + ".mcw");
            using MemoryStream package = new();
            TransferPayloadSource[] sources = payloads.DistinctBy(Hash).Select(bytes =>
                new TransferPayloadSource(new(bytes.Length, Hash(bytes)),
                    _ => Task.FromResult<Stream>(new MemoryStream(bytes, writable: false)))).ToArray();
            TransferWriteResult result = await NativeTransferPackageWriter.WriteAsync(package, content, sources, Guid.NewGuid()).WaitAsync(Deadline);
            Assert.True(result.IsComplete, result.Error.ToString());
            await File.WriteAllBytesAsync(path, package.ToArray()).WaitAsync(Deadline);
            return path;
        }
        public void Dispose() => _directory.Dispose();
    }
}
