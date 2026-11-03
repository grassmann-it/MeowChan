using System.IO.Compression;
using MeowChan.Archive;
using MeowChan.Interfaces;
using MeowChan.Persistence;
using MeowChan.Storage;
using MeowChan.Transfer;
using static MeowChan.Tests.Transfer.NativeMediaImportTests;

namespace MeowChan.Tests.Transfer;

// Legacy-null SQL associations are created only through the preserving-capture API.
// Media assets, bindings and passive roots come from the real native import/merge path.
public sealed class NativeMediaMergeLegacyUnionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DifferentMediaIdIsAdditiveAndKeepsLegacyRootAcrossRestartAndReexport(bool retained)
    {
        using Workspace files = new();
        TransferAttachment incoming = IncomingAttachment("222", Bytes);
        NativeMediaMergePlan plan;
        NativeMediaMergeResult committed;
        string oldRoot;
        string oldGeneration;
        MediaAssetReference oldAsset;
        Dictionary<string, string> oldFiles;
        CapturedAttachment legacy;
        using (ProfileLease lease = ProfileLease.Acquire(files.Profile))
        {
            await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(files.Profile, lease);
            await SeedLegacyAsync(catalog, files, static a => a);
            await using ArchiveReader reader = await ArchiveReader.OpenAsync(files.Profile);
            ArchivedThreadView before = (await reader.ReadThreadAsync(Id))!;
            legacy = LegacyAttachment(before);
            oldRoot = Assert.Single(before.MediaDirectories);
            MediaAssetSnapshot oldSnapshot = await MediaAssetReader.OpenAsync(oldRoot);
            oldGeneration = oldSnapshot.Generation!;
            oldFiles = Files(oldRoot);
            ResolvedMediaAttachment oldResolved = Assert.IsType<ResolvedMediaAttachment>(
                MediaAttachmentResolver.Resolve([oldSnapshot], Id, "15", legacy));
            Assert.True(oldResolved.IsLegacy);
            Assert.Null(oldResolved.Binding);
            oldAsset = oldResolved.Asset;

            await using NativeTransferImportStage stage = await StageAsync(files, Incoming(incoming, retained));
            NativeMediaMergePreparation prepared = await catalog.PrepareNativeMediaMergeAsync(stage);
            Assert.Equal(NativeMediaMergeError.None, prepared.Error);
            plan = Assert.IsType<NativeMediaMergePlan>(prepared.Plan);
            Assert.Equal(incoming.Metadata.AttachmentRevisionKey, Assert.Single(plan.SafeKeys));
            Assert.Empty(plan.UpdatedPosts);
            Assert.Empty(plan.AddedAttachments);
            Assert.Equal(retained ? 0 : 1, plan.AddedPosts.Length);

            committed = await catalog.CommitNativeMediaMergeAsync(plan, new MediaStore());
            Assert.Equal(NativeMediaImportCommitStatus.Committed, committed.Status);
            Assert.Equal(retained ? 0 : 1, committed.NewPosts);
            Assert.Equal(retained ? 0 : 1, committed.NewAttachments);
            Assert.Equal(1, committed.NewBindings);
            Assert.Equal(0, committed.Conflicts);
            Assert.Equal(committed with { IsReplay = true },
                await catalog.CommitNativeMediaMergeAsync(plan, new MediaStore()));
            Assert.Equal(oldFiles, Files(oldRoot));
        }

        // A fresh catalog/reader must preserve the old SQL-null association and root identity.
        using ProfileLease reopenedLease = ProfileLease.Acquire(files.Profile);
        await using ArchiveCatalog reopened = await ArchiveCatalog.OpenAsync(files.Profile, reopenedLease);
        Assert.Equal(committed with { IsReplay = true },
            await reopened.ReadNativeMediaMergeReceiptAsync(plan.OperationId, plan.RequestSha256));
        await using ArchiveReader afterReader = await ArchiveReader.OpenAsync(files.Profile);
        ArchivedThreadView after = (await afterReader.ReadThreadAsync(Id))!;
        Assert.Contains(oldRoot, after.MediaDirectories);
        Assert.Equal(retained ? 2 : 3, after.PostCount);
        Assert.Equal(2, after.MediaDirectories.Count);
        Assert.Null(LegacyAttachment(after).AttachmentRevisionKey);

        MediaAssetSnapshot[] active = await SnapshotsAsync(after);
        MediaAssetSnapshot afterOld = active.Single(s => s.Directory == oldRoot);
        Assert.Equal(oldGeneration, afterOld.Generation);
        Assert.Equal(oldFiles, Files(oldRoot));
        ResolvedMediaAttachment oldAfter = Assert.IsType<ResolvedMediaAttachment>(
            MediaAttachmentResolver.Resolve(active, Id, "15", legacy));
        Assert.True(oldAfter.IsLegacy);
        Assert.Equal(oldRoot, oldAfter.Snapshot.Directory);
        Assert.Equal(oldAsset, oldAfter.Asset);
        await AssertBytesAsync(oldAfter, Bytes);

        CapturedAttachment addedMetadata = retained ? incoming.Metadata
            : Assert.Single(after.Posts.Single(p => p.PostId == "17").Attachments!);
        if (retained) Assert.DoesNotContain(after.Posts, post => post.PostId == "17");
        Assert.Equal(incoming.Metadata, addedMetadata);
        ResolvedMediaAttachment added = Assert.IsType<ResolvedMediaAttachment>(
            MediaAttachmentResolver.Resolve(active, Id, "17", addedMetadata));
        Assert.False(added.IsLegacy);
        Assert.NotEqual(oldRoot, added.Snapshot.Directory);
        Assert.NotEqual(oldAsset.AssetId, added.Asset.AssetId);
        Assert.Equal(MediaAcceptanceKind.PassiveImport, added.Binding!.Acceptance.Kind);
        await AssertBytesAsync(added, Bytes);

        // The plan's admission budget must describe the actual post-restart export byte-for-byte.
        TransferArchiveFreezeResult frozen = await NativeTransferArchiveExporter.FreezeAsync(
            afterReader, Id, At, Guid.NewGuid());
        Assert.True(frozen.IsFrozen, frozen.Error.ToString());
        using MemoryStream output = new();
        TransferArchiveWriteResult written = await NativeTransferArchiveExporter.WriteAsync(frozen.Snapshot!, output);
        Assert.True(written.IsComplete, written.Error + "/" + written.WriteError);
        output.Position = 0;
        TransferPackageInspection inspection = await NativeTransferPackageValidator.InspectAsync(output);
        Assert.True(inspection.IsPackageValid, inspection.EnvelopeError + "/" + inspection.CatalogError);
        NativeTransferPackage exported = inspection.Package!;
        using ZipArchive zip = new(output, ZipArchiveMode.Read, leaveOpen: true);
        long payloadBytes = exported.Envelope.Payloads.Sum(p => p.Length);
        long manifestBytes = zip.GetEntry("manifest.json")!.Length;
        Assert.Equal(plan.ExportBudget.ContainerBytes, output.Length);
        Assert.Equal(plan.ExportBudget.CatalogBytes, exported.Envelope.CatalogBytes.Length);
        Assert.Equal(plan.ExportBudget.ManifestBytes, manifestBytes);
        Assert.Equal(plan.ExportBudget.PayloadBytes, payloadBytes);
        Assert.Equal(plan.ExportBudget.ZipOverhead,
            output.Length - payloadBytes - manifestBytes - exported.Envelope.CatalogBytes.Length);
        Assert.Equal(Hash(Bytes), Assert.Single(exported.Envelope.Payloads).Sha256);
        Assert.Equal(retained ? 2 : 1, exported.Catalog.Threads[0].RetainedBindings.Length);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SameMediaIdentityAndIdenticalBytesIsRejectedBeforeClaimOrFilesystem(bool retained)
    {
        using Workspace files = new();
        using ProfileLease lease = ProfileLease.Acquire(files.Profile);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(files.Profile, lease);
        await SeedLegacyAsync(catalog, files, static a => a);
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(files.Profile);
        ArchivedThreadView before = (await reader.ReadThreadAsync(Id))!;
        CapturedAttachment legacy = LegacyAttachment(before);
        MediaAssetSnapshot[] snapshots = await SnapshotsAsync(before);
        ResolvedMediaAttachment old = Assert.IsType<ResolvedMediaAttachment>(
            MediaAttachmentResolver.Resolve(snapshots, Id, "15", legacy));
        Dictionary<string, string> profileBefore = Files(files.Profile);
        string[] directoriesBefore = Directories(files.Profile);

        // Different post => different revision key, but provider identity and measured bytes
        // are the same. This must not create a duplicate active asset or re-key post 15.
        TransferAttachment incoming = IncomingAttachment("111", Bytes);
        await using NativeTransferImportStage stage = await StageAsync(files, Incoming(incoming, retained));
        NativeMediaMergePreparation rejected = await catalog.PrepareNativeMediaMergeAsync(stage);
        Assert.Null(rejected.Plan);
        Assert.Equal(NativeMediaMergeError.TargetUnavailable, rejected.Error);
        Assert.Empty((await catalog.ReadNativeMediaMergeOperationsPageAsync()).Items);
        Assert.Equal(profileBefore, Files(files.Profile));
        Assert.Equal(directoriesBefore, Directories(files.Profile));
        ArchivedThreadView after = (await reader.ReadThreadAsync(Id))!;
        Assert.Equal(before.Summary, after.Summary);
        Assert.Equal(before.MediaDirectories, after.MediaDirectories);
        ResolvedMediaAttachment unchanged = Assert.IsType<ResolvedMediaAttachment>(
            MediaAttachmentResolver.Resolve(await SnapshotsAsync(after), Id, "15", legacy));
        Assert.Equal(old.Snapshot.Directory, unchanged.Snapshot.Directory);
        Assert.Equal(old.Asset, unchanged.Asset);
        Assert.Null(unchanged.Binding);
    }

    [Fact]
    public async Task LegacyUrlOnlyDifferentUrlIsAdditive()
    {
        using Workspace files = new();
        using ProfileLease lease = ProfileLease.Acquire(files.Profile);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(files.Profile, lease);
        await SeedLegacyAsync(catalog, files, static a => a with
        { ExpectedSize = null, ExpectedMd5 = null, ExpectedSha1 = null, ExpectedSha256 = null });
        TransferAttachment incoming = IncomingAttachment("111", Bytes,
            "https://i.4cdn.org/b/111.png");
        await using NativeTransferImportStage stage = await StageAsync(files, Incoming(incoming));
        NativeMediaMergePreparation prepared = await catalog.PrepareNativeMediaMergeAsync(stage);
        Assert.Equal(NativeMediaMergeError.None, prepared.Error);
        NativeMediaMergePlan plan = Assert.IsType<NativeMediaMergePlan>(prepared.Plan);
        Assert.Single(plan.SafeKeys);
        Assert.Equal(NativeMediaImportCommitStatus.Committed,
            (await catalog.CommitNativeMediaMergeAsync(plan, new MediaStore())).Status);
    }

    [Fact]
    public async Task LegacyUrlOnlySameUrlIsRejectedBeforeClaimOrFilesystem()
    {
        using Workspace files = new();
        using ProfileLease lease = ProfileLease.Acquire(files.Profile);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(files.Profile, lease);
        await SeedLegacyAsync(catalog, files, static a => a with
        { ExpectedSize = null, ExpectedMd5 = null, ExpectedSha1 = null, ExpectedSha256 = null });
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(files.Profile);
        ArchivedThreadView before = (await reader.ReadThreadAsync(Id))!;
        TransferAttachment incoming = IncomingAttachment("111", Bytes);
        Dictionary<string, string> profileBefore = Files(files.Profile);
        await using NativeTransferImportStage stage = await StageAsync(files, Incoming(incoming));
        NativeMediaMergePreparation rejected = await catalog.PrepareNativeMediaMergeAsync(stage);
        Assert.Equal(NativeMediaMergeError.TargetUnavailable, rejected.Error);
        Assert.Null(rejected.Plan);
        Assert.Empty((await catalog.ReadNativeMediaMergeOperationsPageAsync()).Items);
        Assert.Equal(profileBefore, Files(files.Profile));
        Assert.Equal(before.Summary, (await reader.ReadThreadAsync(Id))!.Summary);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task UnresolvedLegacyCannotBeAdoptedEvenWhenNoSafeRootIsNeeded(bool wrongHash, bool noSafeRoot)
    {
        using Workspace files = new();
        using ProfileLease lease = ProfileLease.Acquire(files.Profile);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(files.Profile, lease);
        await SeedLegacyAsync(catalog, files, a => wrongHash
            ? a with { ExpectedSha256 = Hash([7, 9]) }
            : a with { Url = null, ExpectedSize = null, ExpectedMd5 = null, ExpectedSha1 = null, ExpectedSha256 = null });
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(files.Profile);
        ArchivedThreadView before = (await reader.ReadThreadAsync(Id))!;
        Assert.Null(MediaAttachmentResolver.Resolve(await SnapshotsAsync(before), Id, "15", LegacyAttachment(before)));
        NativeTransferCatalog input = noSafeRoot
            ? Content() with { Threads = [Content().Threads[0] with { Posts = [], RetainedBindings = [new("15", Attachment())] }] }
            : Incoming(IncomingAttachment("222", Bytes));
        await using NativeTransferImportStage stage = await StageAsync(files, input);
        Dictionary<string, string> filesBefore = Files(files.Profile);
        string[] directoriesBefore = Directories(files.Profile);
        NativeMediaMergePreparation rejected = await catalog.PrepareNativeMediaMergeAsync(stage);
        Assert.Equal(NativeMediaMergeError.TargetUnavailable, rejected.Error);
        Assert.Null(rejected.Plan);
        Assert.Empty((await catalog.ReadNativeMediaMergeOperationsPageAsync()).Items);
        Assert.Equal(filesBefore, Files(files.Profile));
        Assert.Equal(directoriesBefore, Directories(files.Profile));
        ArchivedThreadView after = (await reader.ReadThreadAsync(Id))!;
        Assert.Equal(before.Summary, after.Summary);
        Assert.Equal(LegacyAttachment(before), LegacyAttachment(after));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TargetMutationAtAdmissionIsRejectedBeforeDurableClaim(bool corruptBytes)
    {
        using Workspace files = new();
        using ProfileLease lease = ProfileLease.Acquire(files.Profile);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(files.Profile, lease);
        await SeedLegacyAsync(catalog, files, static a => a);
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(files.Profile);
        ArchivedThreadView before = (await reader.ReadThreadAsync(Id))!;
        MediaAssetSnapshot original = await MediaAssetReader.OpenAsync(before.Summary.MediaDirectory);
        MediaAssetReference asset = Assert.Single(original.GetPage(0, 500));
        await using NativeTransferImportStage stage = await StageAsync(files, Incoming(IncomingAttachment("222", Bytes)));
        NativeMediaMergePlan plan = (await catalog.PrepareNativeMediaMergeAsync(stage)).Plan!;
        Dictionary<string, string>? afterMutation = null;
        NativeMediaMergeResult result = await catalog.CommitNativeMediaMergeAsync(plan, new MediaStore(), beforeAdmission: () =>
        {
            if (corruptBytes) File.WriteAllBytes(asset.Path, [1, 3, 5]);
            else File.AppendAllText(Path.Combine(original.Directory, ".meowchan-manifest.json"), "\n ");
            afterMutation = Files(files.Profile);
        });
        Assert.Equal(corruptBytes ? NativeMediaImportCommitStatus.Inactive : NativeMediaImportCommitStatus.StalePlan, result.Status);
        Assert.False(Directory.Exists(plan.OperationsParent));
        Assert.Empty((await catalog.ReadNativeMediaMergeOperationsPageAsync()).Items);
        Assert.Null(await catalog.ReadNativeMediaMergeReceiptAsync(plan.OperationId, plan.RequestSha256));
        Assert.Equal(afterMutation, Files(files.Profile));
        Assert.Equal(before.Summary, (await reader.ReadThreadAsync(Id))!.Summary);
    }

    [Fact]
    public async Task HistoricalReceiptAtAdmissionWinsBeforeBrokenTargetBytes()
    {
        using Workspace files = new();
        using ProfileLease lease = ProfileLease.Acquire(files.Profile);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(files.Profile, lease);
        await SeedLegacyAsync(catalog, files, static a => a);
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(files.Profile);
        ArchivedThreadView before = (await reader.ReadThreadAsync(Id))!;
        MediaAssetSnapshot original = await MediaAssetReader.OpenAsync(before.Summary.MediaDirectory);
        MediaAssetReference asset = Assert.Single(original.GetPage(0, 500));
        await using NativeTransferImportStage stage = await StageAsync(files, Incoming(IncomingAttachment("222", Bytes)));
        NativeMediaMergePlan plan = (await catalog.PrepareNativeMediaMergeAsync(stage)).Plan!;
        NativeMediaMergeResult? committed = null;
        Dictionary<string, string>? afterMutation = null;
        NativeMediaMergeResult replay = await catalog.CommitNativeMediaMergeAsync(plan, new MediaStore(), beforeAdmission: () =>
        {
            committed = catalog.CommitNativeMediaMergeAsync(plan, new MediaStore()).GetAwaiter().GetResult();
            Assert.Equal(NativeMediaImportCommitStatus.Committed, committed.Status);
            File.WriteAllBytes(asset.Path, [7, 9]);
            stage.DisposeAsync().GetAwaiter().GetResult();
            afterMutation = Files(files.Profile);
        });
        Assert.NotNull(committed);
        Assert.Equal(committed! with { IsReplay = true }, replay);
        Assert.Equal(afterMutation, Files(files.Profile));
    }

    [Fact]
    public async Task ExistingPreparingOperationWinsBeforeBrokenTargetBytes()
    {
        using Workspace files = new();
        using ProfileLease lease = ProfileLease.Acquire(files.Profile);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(files.Profile, lease);
        await SeedLegacyAsync(catalog, files, static a => a);
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(files.Profile);
        ArchivedThreadView before = (await reader.ReadThreadAsync(Id))!;
        MediaAssetSnapshot original = await MediaAssetReader.OpenAsync(before.Summary.MediaDirectory);
        MediaAssetReference asset = Assert.Single(original.GetPage(0, 500));
        await using NativeTransferImportStage stage = await StageAsync(files, Incoming(IncomingAttachment("222", Bytes)));
        NativeMediaMergePlan plan = (await catalog.PrepareNativeMediaMergeAsync(stage)).Plan!;
        NativeMediaMergeResult stopped = await catalog.CommitNativeMediaMergeAsync(plan, new MediaStore(), checkpoint: phase =>
        { if (phase == NativeMediaImportPhase.PreparingSaved) throw new IOException("synthetic stop"); });
        Assert.Equal(NativeMediaImportCommitStatus.ReconciliationRequired, stopped.Status);
        Assert.Equal(NativeMediaImportOperationState.Preparing,
            Assert.Single((await catalog.ReadNativeMediaMergeOperationsPageAsync()).Items).State);
        File.WriteAllBytes(asset.Path, [11, 13]);
        Dictionary<string, string> beforeReplay = Files(files.Profile);
        NativeMediaMergeResult replay = await catalog.CommitNativeMediaMergeAsync(plan, new MediaStore());
        Assert.Equal(NativeMediaImportCommitStatus.ReconciliationRequired, replay.Status);
        Assert.Equal(beforeReplay, Files(files.Profile));
        Assert.False(Directory.Exists(plan.OperationsParent));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DifferentUnresolvedOperationBlocksBeforeRootVerification(bool prepared)
    {
        using Workspace files = new();
        using ProfileLease lease = ProfileLease.Acquire(files.Profile);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(files.Profile, lease);
        await SeedLegacyAsync(catalog, files, static a => a);
        await using NativeTransferImportStage stageA = await StageAsync(files, Incoming(IncomingAttachment("222", Bytes)));
        NativeMediaMergePlan planA = (await catalog.PrepareNativeMediaMergeAsync(stageA)).Plan!;
        NativeMediaMergeResult stopped = await catalog.CommitNativeMediaMergeAsync(planA, new MediaStore(), checkpoint: phase =>
        { if (phase == (prepared ? NativeMediaImportPhase.PreparedSaved : NativeMediaImportPhase.PreparingSaved))
            throw new IOException("synthetic unresolved operation"); });
        Assert.Equal(NativeMediaImportCommitStatus.ReconciliationRequired, stopped.Status);
        NativeMediaMergeOperation operationA = Assert.Single((await catalog.ReadNativeMediaMergeOperationsPageAsync()).Items);
        Assert.Equal(prepared ? NativeMediaImportOperationState.Prepared : NativeMediaImportOperationState.Preparing, operationA.State);

        await using NativeTransferImportStage stageB = await StageAsync(files, Incoming(IncomingAttachment("333", Bytes)));
        NativeMediaMergePlan planB = (await catalog.PrepareNativeMediaMergeAsync(stageB)).Plan!;
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(files.Profile);
        ArchivedThreadView target = (await reader.ReadThreadAsync(Id))!;
        MediaAssetSnapshot snapshot = await MediaAssetReader.OpenAsync(target.Summary.MediaDirectory);
        MediaAssetReference asset = Assert.Single(snapshot.GetPage(0, 500));
        File.WriteAllBytes(asset.Path, [17, 19, 23]);
        Dictionary<string, string> afterMutation = Files(files.Profile);

        NativeMediaMergeResult blocked = await catalog.CommitNativeMediaMergeAsync(planB, new MediaStore());
        Assert.Equal(NativeMediaImportCommitStatus.ReconciliationRequired, blocked.Status);
        Assert.Equal(afterMutation, Files(files.Profile));
        Assert.False(Directory.Exists(planB.OperationsParent));
        NativeMediaMergeOperation unchanged = Assert.Single((await catalog.ReadNativeMediaMergeOperationsPageAsync()).Items);
        Assert.Equal(operationA.OperationId, unchanged.OperationId);
        Assert.Equal(operationA.State, unchanged.State);
        Assert.Equal(operationA.CatalogBytes.ToArray(), unchanged.CatalogBytes.ToArray());
    }

    private static async Task SeedLegacyAsync(ArchiveCatalog catalog, Workspace files,
        Func<CapturedAttachment, CapturedAttachment> transform)
    {
        await using NativeTransferImportStage stage = await StageAsync(files, Content());
        NativeMediaImportPlan plan = Assert.IsType<NativeMediaImportPlan>(
            (await catalog.PrepareNativeMediaImportAsync(stage)).Plan);
        Assert.Equal(NativeMediaImportCommitStatus.Committed,
            (await catalog.CommitNativeMediaImportAsync(plan, new MediaStore())).Status);
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(files.Profile);
        ArchivedThreadView view = (await reader.ReadThreadAsync(Id))!;
        CapturedPost[] posts = view.Posts.Select(post => post.PostId == "15" ? post with
        {
            Attachments = post.Attachments!.Select(a => transform(a) with { AttachmentRevisionKey = null }).ToArray()
        } : post).ToArray();
        await catalog.CapturePreservingRevisionsAsync(new(Id, Url, view.Summary.Title,
            ThreadSourceState.NotFound, At, view.Summary.MediaDirectory, posts));
    }

    private static NativeTransferCatalog Incoming(TransferAttachment attachment, bool retained = false)
    {
        NativeTransferCatalog content = Content();
        TransferThread thread = content.Threads[0] with
        {
            Posts = retained ? [] : [new(new("17", 2, PostFieldPresence.Attachments,
                Attachments: [attachment.Metadata]), [attachment])],
            RetainedBindings = retained ? [new("17", attachment)] : []
        };
        return content with { Threads = [thread] };
    }

    private static TransferAttachment IncomingAttachment(string mediaId, byte[] bytes, string? url = null)
    {
        MediaProviderMetadata provider = new(mediaId, "original",
            url ?? $"https://i.4cdn.org/b/{mediaId}.jpg", "synthetic.bin", bytes.Length,
            Md5(bytes), Hash(bytes)) { ExpectedSha1 = Sha1(bytes) };
        CapturedAttachment metadata = new(0, mediaId, "original", provider.Url,
            provider.OriginalFileName, ExpectedSize: provider.ExpectedSize, ExpectedMd5: provider.ExpectedMd5)
        {
            ExpectedSha1 = provider.ExpectedSha1,
            ExpectedSha256 = provider.ExpectedSha256,
            AttachmentRevisionKey = MediaRevisionKeys.CreateAttachmentKey(Id, "17", 0, provider)
        };
        return new(metadata, TransferMediaRole.Original, TransferContentStatus.Contained,
            Hash(bytes), null);
    }

    private static CapturedAttachment LegacyAttachment(ArchivedThreadView view)
        => Assert.Single(view.Posts.Single(post => post.PostId == "15").Attachments!);

    private static async Task<MediaAssetSnapshot[]> SnapshotsAsync(ArchivedThreadView view)
        => await Task.WhenAll(view.MediaDirectories.Select(directory => MediaAssetReader.OpenAsync(directory)));

    private static async Task<NativeTransferImportStage> StageAsync(Workspace files,
        NativeTransferCatalog catalog)
    {
        using MemoryStream package = new();
        TransferWriteResult written = await NativeTransferPackageWriter.WriteAsync(package, catalog,
            [new(new(Bytes.Length, Hash(Bytes)), _ =>
                Task.FromResult<Stream>(new MemoryStream(Bytes, false)))], Guid.NewGuid());
        Assert.True(written.IsComplete, written.Error.ToString());
        package.Position = 0;
        NativeTransferStageResult staged = await NativeTransferImportStage.CreateAsync(package, files.StageRoot);
        Assert.Equal(NativeTransferStageError.None, staged.Error);
        return Assert.IsType<NativeTransferImportStage>(staged.Stage);
    }

    private static async Task AssertBytesAsync(ResolvedMediaAttachment resolved, byte[] expected)
    {
        await using Stream stream = await resolved.Snapshot.OpenVerifiedReadAsync(resolved.Asset.AssetId);
        using MemoryStream actual = new();
        await stream.CopyToAsync(actual);
        Assert.Equal(expected, actual.ToArray());
    }

    private static string[] Directories(string directory)
        => Directory.GetDirectories(directory, "*", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.Ordinal).ToArray();
}
