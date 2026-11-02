using System.Security.Cryptography;
using MeowChan.Archive;
using MeowChan.Boards.Base;
using MeowChan.Downloader;
using MeowChan.Interfaces;
using MeowChan.Persistence;
using MeowChan.Storage;
using MeowChan.Transfer;
using static MeowChan.Tests.Transfer.NativeMediaImportTests;

namespace MeowChan.Tests.Transfer;

// R01 contract cases. These deliberately use real writer/stage/store producers; no
// manifest, SQL row, binding or candidate is fabricated by the fixture.
public sealed class NativeMediaMergeRetainedReuseTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExactTargetRetainedReuseKeepsIdentityHistoryAndBudget(bool existingPost)
    {
        using Workspace files = new();
        byte[] bytes = [11, 17, 23, 29, 31];
        string postId = existingPost ? "16" : "900";
        TransferAttachment current = SmallAttachment(postId, "900", bytes) with
        { History = new("strict", At.AddDays(3), false, false) };
        string root; Dictionary<string, string> oldFiles;
        MediaAttachmentBinding targetBinding; MediaAssetReference targetAsset;
        NativeTransferPackageWriter.TransferPackageBudget budget;
        Guid operationId; string request;
        using (ProfileLease lease = ProfileLease.Acquire(files.Profile))
        {
            await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(files.Profile, lease);
            await SeedAsync(catalog, files);
            await using ArchiveReader reader = await ArchiveReader.OpenAsync(files.Profile);
            ArchivedThreadView before = (await reader.ReadThreadAsync(Id))!;
            root = Assert.Single(before.MediaDirectories);
            await AddRetainedAsync(root, postId, bytes, "900");
            MediaAssetSnapshot snapshot = await MediaAssetReader.OpenAsync(root);
            targetBinding = Assert.Single(snapshot.AttachmentBindings.Values, b => b.Reference.PostId == postId);
            targetAsset = Assert.IsType<MediaAssetReference>(snapshot.FindById(targetBinding.AssetId));
            Assert.Equal(MediaAcceptanceKind.Strict, targetBinding.Acceptance.Kind);
            Assert.NotEqual(current.History!.AcceptedAtUtc, targetBinding.Acceptance.AcceptedAtUtc);
            oldFiles = Files(root);
            CapturedPost metadata = existingPost ? before.Posts.Single(p => p.PostId == postId)
                : NewPost(postId, 3, current).Metadata;
            TransferPost inputPost = new(metadata with
            { Fields = metadata.Fields | PostFieldPresence.Attachments, Attachments = [current.Metadata] }, [current]);
            NativeTransferCatalog incoming = Content() with
            { Threads = [Content().Threads[0] with { Posts = [inputPost] }] };
            await using NativeTransferImportStage stage = await StageWithPayloadsAsync(files, incoming, bytes);
            NativeMediaMergePreparation prepared = await catalog.PrepareNativeMediaMergeAsync(stage);
            Assert.Equal(NativeMediaMergeError.None, prepared.Error);
            NativeMediaMergePlan plan = Assert.IsType<NativeMediaMergePlan>(prepared.Plan);
            operationId = plan.OperationId; request = plan.RequestSha256; budget = plan.ExportBudget;
            Assert.Equal(current.Metadata.AttachmentRevisionKey, Assert.Single(plan.FullKeys));
            Assert.Empty(plan.SafeKeys);
            NativeMediaMergeResult result = await catalog.CommitNativeMediaMergeAsync(plan, new MediaStore());
            Assert.Equal(NativeMediaImportCommitStatus.Committed, result.Status);
            Assert.Equal(existingPost ? 0 : 1, result.NewPosts);
            Assert.Equal(1, result.NewAttachments);
            Assert.Equal(0, result.NewBindings);
            Assert.Equal(0, result.Conflicts);
            Assert.Equal(result with { IsReplay = true }, await catalog.CommitNativeMediaMergeAsync(plan, new MediaStore()));
        }
        using ProfileLease reopenedLease = ProfileLease.Acquire(files.Profile);
        await using ArchiveCatalog reopened = await ArchiveCatalog.OpenAsync(files.Profile, reopenedLease);
        NativeMediaMergeOperation operation = Assert.Single((await reopened.ReadNativeMediaMergeOperationsPageAsync()).Items);
        Assert.Equal(operationId, operation.OperationId); Assert.Equal(request, operation.RequestSha256);
        Assert.Null(operation.SafeDirectory);
        Assert.NotNull(operation.FullDirectory);
        await using ArchiveReader afterReader = await ArchiveReader.OpenAsync(files.Profile);
        ArchivedThreadView after = (await afterReader.ReadThreadAsync(Id))!;
        Assert.Equal(root, Assert.Single(after.MediaDirectories));
        Assert.DoesNotContain(operation.FullDirectory!, after.MediaDirectories);
        Assert.Equal(ThreadSourceState.NotFound, after.Summary.SourceState);
        MediaAssetSnapshot afterSnapshot = await MediaAssetReader.OpenAsync(root);
        Assert.Equal(targetBinding, afterSnapshot.FindAttachmentBinding(targetBinding.Reference.RevisionKey));
        Assert.Equal(targetAsset, afterSnapshot.FindById(targetAsset.AssetId));
        CapturedAttachment attached = Assert.Single(after.Posts.Single(p => p.PostId == postId).Attachments!);
        await AssertBytesAsync([afterSnapshot], postId, attached, bytes);
        ResolvedMediaAttachment resolved = Assert.IsType<ResolvedMediaAttachment>(MediaAttachmentResolver.Resolve([afterSnapshot], Id, postId, attached));
        Assert.Equal(targetBinding, resolved.Binding);
        NativeTransferPackage exported = await ExportAsync(afterReader, budget);
        TransferThread thread = Assert.Single(exported.Catalog.Threads);
        TransferAttachment exportedCurrent = Assert.Single(thread.Posts.Single(p => p.Metadata.PostId == postId).Attachments);
        Assert.Equal(Hash(bytes), exportedCurrent.PayloadSha256);
        Assert.Equal(new TransferHistoricalIntegrity("strict", targetBinding.Acceptance.AcceptedAtUtc, false, false), exportedCurrent.History);
        Assert.DoesNotContain(thread.RetainedBindings, b => b.Attachment.Metadata.AttachmentRevisionKey == targetBinding.Reference.RevisionKey);
        Assert.Equal(oldFiles, Files(root));
    }

    [Fact]
    public async Task SameKeyDifferentBytesConflictsWhileOtherRetainedBindingPublishes()
    {
        using Workspace files = new();
        byte[] oldBytes = [41, 43, 47], conflictBytes = [53, 59, 61, 67], safeBytes = [71, 73, 79, 83];
        TransferAttachment target = SmallAttachment("901", "901", oldBytes);
        TransferAttachment conflict = target with { PayloadSha256 = Hash(conflictBytes) };
        TransferAttachment safe = SmallAttachment("902", "902", safeBytes);
        NativeTransferCatalog incoming = Content() with
        { Threads = [Content().Threads[0] with { Posts = [NewPost("901", 4, conflict)], RetainedBindings = [new("902", safe)] }] };
        Guid operationId; string request, root; Dictionary<string, string> oldFiles;
        MediaAttachmentBinding oldBinding;
        using (ProfileLease lease = ProfileLease.Acquire(files.Profile))
        {
            await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(files.Profile, lease);
            await SeedAsync(catalog, files);
            await using ArchiveReader reader = await ArchiveReader.OpenAsync(files.Profile);
            root = Assert.Single(((await reader.ReadThreadAsync(Id))!).MediaDirectories);
            await AddRetainedAsync(root, "901", oldBytes, "901");
            oldBinding = (await MediaAssetReader.OpenAsync(root)).FindAttachmentBinding(target.Metadata.AttachmentRevisionKey!)!;
            oldFiles = Files(root);
            await using NativeTransferImportStage stage = await StageWithPayloadsAsync(files, incoming, conflictBytes, safeBytes);
            NativeMediaMergePreparation prepared = await catalog.PrepareNativeMediaMergeAsync(stage);
            Assert.Equal(NativeMediaMergeError.None, prepared.Error);
            NativeMediaMergePlan plan = Assert.IsType<NativeMediaMergePlan>(prepared.Plan);
            operationId = plan.OperationId; request = plan.RequestSha256;
            Assert.Equal(1, plan.Conflicts); Assert.Equal(2, plan.FullKeys.Length);
            Assert.Equal(safe.Metadata.AttachmentRevisionKey, Assert.Single(plan.SafeKeys));
            NativeMediaMergeResult result = await catalog.CommitNativeMediaMergeAsync(plan, new MediaStore());
            Assert.Equal(NativeMediaImportCommitStatus.Committed, result.Status);
            Assert.Equal(1, result.NewBindings); Assert.Equal(0, result.NewAttachments); Assert.Equal(1, result.Conflicts);
            Assert.Equal(result with { IsReplay = true }, await catalog.CommitNativeMediaMergeAsync(plan, new MediaStore()));
        }
        using ProfileLease reopenedLease = ProfileLease.Acquire(files.Profile);
        await using ArchiveCatalog reopened = await ArchiveCatalog.OpenAsync(files.Profile, reopenedLease);
        NativeMediaMergeOperation operation = Assert.Single((await reopened.ReadNativeMediaMergeOperationsPageAsync()).Items);
        Assert.Equal(operationId, operation.OperationId); Assert.Equal(request, operation.RequestSha256);
        Assert.Equal(NativeTransferCatalogSerializer.Serialize(incoming), operation.CatalogBytes.ToArray());
        string fullRoot = Assert.IsType<string>(operation.FullDirectory), safeRoot = Assert.IsType<string>(operation.SafeDirectory);
        Assert.NotEqual(fullRoot, safeRoot);
        MediaAssetSnapshot full = await MediaAssetReader.OpenAsync(fullRoot);
        await AssertBytesAsync([full], "901", conflict.Metadata, conflictBytes);
        await AssertBytesAsync([full], "902", safe.Metadata, safeBytes);
        await using ArchiveReader afterReader = await ArchiveReader.OpenAsync(files.Profile);
        ArchivedThreadView after = (await afterReader.ReadThreadAsync(Id))!;
        Assert.Equal(2, after.MediaDirectories.Count); Assert.Contains(root, after.MediaDirectories);
        Assert.Contains(safeRoot, after.MediaDirectories); Assert.DoesNotContain(fullRoot, after.MediaDirectories);
        Assert.DoesNotContain(after.Posts, p => p.PostId == "902"); // retained never invents a post
        Assert.Empty(after.Posts.Single(p => p.PostId == "901").Attachments!);
        MediaAssetSnapshot[] active = await Task.WhenAll(after.MediaDirectories.Select(d => MediaAssetReader.OpenAsync(d)));
        await AssertBytesAsync(active, "901", target.Metadata, oldBytes);
        await AssertBytesAsync(active, "902", safe.Metadata, safeBytes);
        Assert.Equal(oldBinding, active.Single(s => s.Directory == root).FindAttachmentBinding(target.Metadata.AttachmentRevisionKey!));
        Assert.DoesNotContain(active.SelectMany(s => s.AttachmentBindings.Values), b => b.Actual.Sha256 == Hash(conflictBytes));
        NativeTransferPackage exported = await ExportAsync(afterReader);
        Assert.DoesNotContain(exported.Envelope.Payloads, p => p.Sha256 == Hash(conflictBytes));
        Assert.Contains(exported.Catalog.Threads[0].RetainedBindings, b => b.PostId == "902" && b.Attachment.PayloadSha256 == Hash(safeBytes));
        Assert.Equal(oldFiles, Files(root));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExactReuseWithoutSafeRootCannotCommitAfterTargetManifestOrBytesChange(bool corruptBytes)
    {
        using Workspace files = new();
        byte[] bytes = [101, 103, 107, 109];
        using ProfileLease lease = ProfileLease.Acquire(files.Profile);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(files.Profile, lease);
        await SeedAsync(catalog, files);
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(files.Profile);
        ArchivedThreadView before = (await reader.ReadThreadAsync(Id))!;
        string root = Assert.Single(before.MediaDirectories);
        await AddRetainedAsync(root, "16", bytes, "910");
        TransferAttachment current = SmallAttachment("16", "910", bytes);
        CapturedPost previous = before.Posts.Single(post => post.PostId == "16");
        NativeTransferCatalog incoming = Content() with
        {
            Threads = [Content().Threads[0] with { Posts = [new(previous with
                { Fields = previous.Fields | PostFieldPresence.Attachments, Attachments = [current.Metadata] }, [current])] }]
        };
        await using NativeTransferImportStage stage = await StageWithPayloadsAsync(files, incoming, bytes);
        NativeMediaMergePlan plan = Assert.IsType<NativeMediaMergePlan>((await catalog.PrepareNativeMediaMergeAsync(stage)).Plan);
        Assert.Empty(plan.SafeKeys);
        MediaAssetSnapshot snapshot = await MediaAssetReader.OpenAsync(root);
        if (corruptBytes)
        {
            MediaAttachmentBinding binding = snapshot.FindAttachmentBinding(current.Metadata.AttachmentRevisionKey!)!;
            string path = snapshot.FindById(binding.AssetId)!.Path;
            File.WriteAllBytes(path, [113, 127, 131, 137]);
        }
        else await AddRetainedAsync(root, "911", bytes, "911");
        Dictionary<string, string> afterMutation = Files(root);
        NativeMediaMergeResult result = await catalog.CommitNativeMediaMergeAsync(plan, new MediaStore());
        Assert.Equal(corruptBytes ? NativeMediaImportCommitStatus.Inactive : NativeMediaImportCommitStatus.StalePlan, result.Status);
        Assert.False(Directory.Exists(plan.OperationsParent));
        Assert.Null(await catalog.ReadNativeMediaMergeReceiptAsync(plan.OperationId, plan.RequestSha256));
        Assert.Equal(afterMutation, Files(root));
        ArchivedThreadView after = (await reader.ReadThreadAsync(Id))!;
        Assert.Equal(before.Summary, after.Summary);
        Assert.Empty(after.Posts.Single(post => post.PostId == "16").Attachments!);
        Assert.Equal(root, Assert.Single(after.MediaDirectories));
        // The target is now reverified under leases before the durable claim. A known
        // stale/invalid target must not leave an otherwise unnecessary recovery reservation.
        Assert.Empty((await catalog.ReadNativeMediaMergeOperationsPageAsync()).Items);
    }

    private static async Task SeedAsync(ArchiveCatalog catalog, Workspace files)
    {
        await using NativeTransferImportStage stage = await Stage(files, Content());
        NativeMediaImportPlan plan = Assert.IsType<NativeMediaImportPlan>((await catalog.PrepareNativeMediaImportAsync(stage)).Plan);
        Assert.Equal(NativeMediaImportCommitStatus.Committed, (await catalog.CommitNativeMediaImportAsync(plan, new MediaStore())).Status);
    }

    private static TransferPost NewPost(string id, long ordinal, TransferAttachment attachment)
        => new(new(id, ordinal, PostFieldPresence.All, At, "", "", "r01", false, [attachment.Metadata]), [attachment]);

    private static MediaProviderMetadata Provider(string id, byte[] bytes)
        => new(id, "original", $"https://i.4cdn.org/b/{id}.jpg", $"r01-{id}.bin", bytes.Length,
            Md5(bytes), Hash(bytes)) { ExpectedSha1 = Sha1(bytes) };

    private static CapturedAttachment Metadata(MediaProviderMetadata p, string post)
        => new(0, p.MediaId, p.Variant, p.Url, p.OriginalFileName, ExpectedSize: p.ExpectedSize, ExpectedMd5: p.ExpectedMd5)
        { ExpectedSha1 = p.ExpectedSha1, ExpectedSha256 = p.ExpectedSha256,
          AttachmentRevisionKey = MediaRevisionKeys.CreateAttachmentKey(Id, post, 0, p) };

    private static TransferAttachment SmallAttachment(string post, string id, byte[] bytes)
        => new(Metadata(Provider(id, bytes), post), TransferMediaRole.Original, TransferContentStatus.Contained, Hash(bytes), null);

    private static async Task AddRetainedAsync(string root, string post, byte[] bytes, string mediaId)
    {
        MediaProviderMetadata p = Provider(mediaId, bytes);
        OfflineDescriptor descriptor = new();
        ProbeThread thread = new(new OfflineBoard(descriptor), new Uri(Url))
        { OnCheck = () => throw new InvalidOperationException("No provider work in R01 fixture.") };
        DownloadItem item = new(new Uri(p.Url), Path.Combine(root, $"r01-{mediaId}.bin"), descriptor, thread,
            p.MediaId, p.ExpectedSize, p.ExpectedMd5, p.OriginalFileName)
        { ExpectedSha1 = p.ExpectedSha1, ExpectedSha256 = p.ExpectedSha256,
          AcquisitionRevisionKey = MediaRevisionKeys.CreateAcquisitionKey(Id, p),
          AttachmentReferences = [new(Id, post, 0, MediaRevisionKeys.CreateAttachmentKey(Id, post, 0, p))] };
        using MemoryStream input = new(bytes, false);
        Assert.Equal(Hash(bytes), (await new MediaStore().CommitAsync(item, input, CancellationToken.None)).Sha256);
    }

    private static async Task<NativeTransferImportStage> StageWithPayloadsAsync(Workspace files, NativeTransferCatalog catalog, params byte[][] payloads)
    {
        using MemoryStream package = new();
        TransferPayloadSource[] sources = payloads.Select(b => new TransferPayloadSource(new(b.Length, Hash(b)), _ => Task.FromResult<Stream>(new MemoryStream(b, false)))).ToArray();
        TransferWriteResult written = await NativeTransferPackageWriter.WriteAsync(package, catalog, sources, Guid.NewGuid());
        Assert.True(written.IsComplete, written.Error.ToString()); package.Position = 0;
        NativeTransferStageResult staged = await NativeTransferImportStage.CreateAsync(package, files.StageRoot);
        Assert.Equal(NativeTransferStageError.None, staged.Error);
        return Assert.IsType<NativeTransferImportStage>(staged.Stage);
    }

    private static async Task<NativeTransferPackage> ExportAsync(ArchiveReader reader, NativeTransferPackageWriter.TransferPackageBudget? expectedBudget = null)
    {
        TransferArchiveFreezeResult frozen = await NativeTransferArchiveExporter.FreezeAsync(reader, Id, At.AddDays(1), Guid.NewGuid());
        Assert.True(frozen.IsFrozen, frozen.Error.ToString()); using MemoryStream output = new();
        TransferArchiveWriteResult written = await NativeTransferArchiveExporter.WriteAsync(frozen.Snapshot!, output);
        Assert.True(written.IsComplete, written.Error + "/" + written.WriteError); output.Position = 0;
        TransferPackageInspection inspected = await NativeTransferPackageValidator.InspectAsync(output);
        Assert.True(inspected.IsPackageValid, inspected.EnvelopeError + "/" + inspected.CatalogError);
        NativeTransferPackage package = inspected.Package!;
        if (expectedBudget is { } budget)
        {
            using System.IO.Compression.ZipArchive zip = new(output, System.IO.Compression.ZipArchiveMode.Read, leaveOpen: true);
            long payloadBytes = package.Envelope.Payloads.Sum(p => p.Length);
            long manifestBytes = zip.GetEntry("manifest.json")!.Length;
            Assert.Equal(budget.ContainerBytes, output.Length);
            Assert.Equal(budget.CatalogBytes, package.Envelope.CatalogBytes.Length);
            Assert.Equal(budget.ManifestBytes, manifestBytes);
            Assert.Equal(budget.PayloadBytes, payloadBytes);
            Assert.Equal(budget.ZipOverhead, output.Length - payloadBytes - manifestBytes - package.Envelope.CatalogBytes.Length);
        }
        return package;
    }

    private static async Task AssertBytesAsync(IReadOnlyList<MediaAssetSnapshot> snapshots, string post, CapturedAttachment attachment, byte[] expected)
    {
        ResolvedMediaAttachment resolved = Assert.IsType<ResolvedMediaAttachment>(MediaAttachmentResolver.Resolve(snapshots, Id, post, attachment));
        await using Stream input = await resolved.Snapshot.OpenVerifiedReadAsync(resolved.Asset.AssetId);
        using MemoryStream actual = new(); await input.CopyToAsync(actual); Assert.Equal(expected, actual.ToArray());
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static string Md5(byte[] bytes) => Convert.ToBase64String(MD5.HashData(bytes));
    private static string Sha1(byte[] bytes) => Convert.ToBase64String(SHA1.HashData(bytes));

    private sealed class OfflineDescriptor : IImageBoard
    {
        public string Name => "4chan"; public string NiceName => "R01"; public Uri BaseUri => new("https://boards.4chan.org/");
        public bool CanHandle(Uri uri) => throw Forbidden(); public bool IsThreadUri(Uri uri) => throw Forbidden(); public bool IsBoardUri(Uri uri) => throw Forbidden();
        public IBoard GetBoard(Uri uri) => throw Forbidden(); public IThread GetThread(Uri uri) => throw Forbidden(); public HttpClient GetHttpClient() => throw Forbidden();
        private static InvalidOperationException Forbidden() => new("No provider work in R01 fixture.");
    }

    private sealed class OfflineBoard(IImageBoard descriptor) : BaseBoard
    {
        public override IImageBoard ImageBoard => descriptor;
        public override string BoardCode => "b";
        public override string Name { get; protected set; } = "R01";
        public override string NiceName => Name;
        public override Uri BoardUri => new("https://boards.4chan.org/b/");
        public override Task<IEnumerable<IThread>> GetThreadsAsync(CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("No provider work in R01 fixture.");
    }
}
