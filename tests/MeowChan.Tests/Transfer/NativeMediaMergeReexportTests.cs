using System.IO.Compression;
using MeowChan.Archive;
using MeowChan.Boards.Base;
using MeowChan.Downloader;
using MeowChan.Interfaces;
using MeowChan.Persistence;
using MeowChan.Storage;
using MeowChan.Transfer;
using static MeowChan.Tests.Transfer.NativeMediaImportTests;

namespace MeowChan.Tests.Transfer;

// Real writer -> stage -> merge -> owner restart -> exporter -> writer -> reinspect.
// Small synthetic payloads exercise the complete representation; boundary arithmetic
// belongs to the separate budget tests, not to multi-GiB fixtures here.
public sealed class NativeMediaMergeReexportTests
{
    [Fact]
    public async Task MixedSafeAndConflictingMergeKeepsInactiveFullCandidateButReexportsOnlyActiveUnion()
    {
        using Workspace files = new();
        byte[] conflictBytes = [23, 29, 31, 37, 41];
        byte[] safeBytes = [43, 47, 53, 59, 61, 67];
        TransferAttachment original = Attachment();
        // Same provider/revision, different bytes: the exact existing target binding wins.
        TransferAttachment conflict = original with { PayloadSha256 = Hash(conflictBytes) };
        TransferAttachment safe = SmallAttachment("17", "222", safeBytes);
        NativeTransferCatalog seed = Content();
        TransferPost conflictingPost = seed.Threads[0].Posts[0] with { Attachments = [conflict] };
        NativeTransferCatalog incoming = seed with
        { Threads = [seed.Threads[0] with { Posts = [conflictingPost, NewPost("17", 2, safe)] }] };
        Guid operationId;
        string requestSha256, oldRoot;
        NativeTransferPackageWriter.TransferPackageBudget expectedBudget;
        ArchiveThreadSummary beforeSummary;
        MediaAttachmentBinding oldBinding;
        MediaAssetReference oldAsset;
        Dictionary<string, string> oldRootFiles;

        using (ProfileLease lease = ProfileLease.Acquire(files.Profile))
        {
            await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(files.Profile, lease);
            await SeedAsync(lease, catalog, files);
            await using ArchiveReader reader = await ArchiveReader.OpenAsync(files.Profile);
            ArchivedThreadView before = Assert.IsType<ArchivedThreadView>(await reader.ReadThreadAsync(Id));
            beforeSummary = before.Summary;
            oldRoot = Assert.Single(before.MediaDirectories);
            oldRootFiles = Files(oldRoot);
            MediaAssetSnapshot manifest = await MediaAssetReader.OpenAsync(oldRoot);
            ResolvedMediaAttachment resolved = Assert.IsType<ResolvedMediaAttachment>(
                MediaAttachmentResolver.Resolve([manifest], Id, "15", original.Metadata));
            oldBinding = Assert.IsType<MediaAttachmentBinding>(resolved.Binding);
            oldAsset = resolved.Asset;

            await using NativeTransferImportStage stage = await StageWithPayloadsAsync(files, incoming, conflictBytes, safeBytes);
            NativeMediaMergePreparation prepared = await catalog.PrepareNativeMediaMergeAsync(stage);
            Assert.Equal(NativeMediaMergeError.None, prepared.Error);
            NativeMediaMergePlan plan = Assert.IsType<NativeMediaMergePlan>(prepared.Plan);
            operationId = plan.OperationId;
            requestSha256 = plan.RequestSha256;
            Assert.Equal(1, plan.Conflicts);
            Assert.Equal(2, plan.FullKeys.Length);
            Assert.Equal(safe.Metadata.AttachmentRevisionKey, Assert.Single(plan.SafeKeys));
            Assert.False(plan.ReuseFullAsSafe);
            expectedBudget = plan.ExportBudget;
            Assert.True(expectedBudget.WithinContainerCap);
            using ProfileAdmissionToken admission = await lease.Admission.EnterExclusiveAsync();
            NativeMediaMergeResult result = await AssertMergeAsync(admission, catalog, plan, new MediaStore(), ProfileExclusiveDisposition.SafeToResume);
            Assert.Equal(NativeMediaImportCommitStatus.Committed, result.Status);
            Assert.Equal(1, result.NewPosts);
            Assert.Equal(0, result.UpdatedPosts);
            Assert.Equal(1, result.NewAttachments);
            Assert.Equal(1, result.NewBindings);
            Assert.Equal(1, result.Conflicts);
        }

        // No input-stage/catalog/lease remains alive. Discover both candidates and the
        // exact incoming catalog through durable recovery, not through the old plan.
        using ProfileLease reopenedLease = ProfileLease.Acquire(files.Profile);
        await using ArchiveCatalog reopened = await ArchiveCatalog.OpenAsync(files.Profile, reopenedLease);
        NativeMediaMergeOperation operation = Assert.Single((await reopened.ReadNativeMediaMergeOperationsPageAsync()).Items);
        Assert.Equal(operationId, operation.OperationId);
        Assert.Equal(requestSha256, operation.RequestSha256);
        Assert.Equal(NativeMediaImportOperationState.Committed, operation.State);
        Assert.Equal(NativeTransferCatalogSerializer.Serialize(incoming), operation.CatalogBytes.ToArray());
        string fullRoot = Assert.IsType<string>(operation.FullDirectory);
        string safeRoot = Assert.IsType<string>(operation.SafeDirectory);
        Assert.NotEqual(fullRoot, safeRoot);
        NativeMediaMergeInspection inspection = await reopened.InspectNativeMediaMergeOperationAsync(
            operationId, requestSha256, new MediaStore());
        Assert.Equal(NativeMediaImportFilesystemStatus.Inspected, inspection.FilesystemStatus);
        Assert.Equal(2, inspection.Children.Length);
        Assert.All(inspection.Children, child =>
        {
            Assert.Equal(PassiveMediaCandidateStatus.Ready, child.Status);
            Assert.True(child.MatchesOperation);
        });
        Assert.Equal(NativeMediaImportCommitStatus.Committed, inspection.Receipt!.Status);

        MediaAssetSnapshot full = await MediaAssetReader.OpenAsync(fullRoot);
        MediaAssetSnapshot safeManifest = await MediaAssetReader.OpenAsync(safeRoot);
        Assert.Equal(operation.FullManifestGeneration, full.Generation);
        Assert.Equal(operation.SafeManifestGeneration, safeManifest.Generation);
        Assert.Equal(operation.FullKeys.ToArray(), full.AttachmentBindings.Keys.OrderBy(key => key, StringComparer.Ordinal).ToArray());
        Assert.Equal(safe.Metadata.AttachmentRevisionKey, Assert.Single(safeManifest.AttachmentBindings).Key);
        await AssertResolvedAsync([full], "15", conflict.Metadata, conflictBytes);
        await AssertResolvedAsync([full], "17", safe.Metadata, safeBytes);
        Assert.Null(safeManifest.FindAttachmentBinding(conflict.Metadata.AttachmentRevisionKey!));
        Dictionary<string, string> fullRootFiles = Files(fullRoot);

        await using ArchiveReader afterReader = await ArchiveReader.OpenAsync(files.Profile);
        ArchivedThreadView after = Assert.IsType<ArchivedThreadView>(await afterReader.ReadThreadAsync(Id));
        Assert.Equal(beforeSummary with { PostCount = 3, Revision = beforeSummary.Revision + 1 }, after.Summary);
        Assert.Equal(2, after.MediaDirectories.Count);
        Assert.Contains(oldRoot, after.MediaDirectories);
        Assert.Contains(safeRoot, after.MediaDirectories);
        Assert.DoesNotContain(fullRoot, after.MediaDirectories);
        MediaAssetSnapshot[] active = await Task.WhenAll(after.MediaDirectories.Select(directory => MediaAssetReader.OpenAsync(directory)));
        CapturedAttachment unchanged = Assert.Single(after.Posts.Single(post => post.PostId == "15").Attachments!);
        ResolvedMediaAttachment existing = await AssertResolvedAsync(active, "15", unchanged, Bytes);
        Assert.Equal(oldBinding, existing.Binding);
        Assert.Equal(oldAsset, existing.Asset);
        await AssertResolvedAsync(active, "17", Assert.Single(after.Posts.Single(post => post.PostId == "17").Attachments!), safeBytes);

        NativeTransferPackage exported = await ExportAndCheckPayloadsAsync(afterReader, expectedBudget, Bytes, safeBytes);
        TransferThread thread = Assert.Single(exported.Catalog.Threads);
        Assert.Equal(new[] { "15", "16", "17" }, thread.Posts.Select(post => post.Metadata.PostId));
        Assert.Empty(thread.RetainedBindings);
        Assert.Equal(original.Metadata, Assert.Single(thread.Posts.Single(post => post.Metadata.PostId == "15").Attachments).Metadata);
        Assert.Equal(Hash(Bytes), Assert.Single(thread.Posts.Single(post => post.Metadata.PostId == "15").Attachments).PayloadSha256);
        Assert.Equal(Hash(safeBytes), Assert.Single(thread.Posts.Single(post => post.Metadata.PostId == "17").Attachments).PayloadSha256);
        Assert.DoesNotContain(exported.Envelope.Payloads, payload => payload.Sha256 == Hash(conflictBytes));
        Assert.Equal(oldRootFiles, Files(oldRoot));
        Assert.Equal(fullRootFiles, Files(fullRoot));
    }

    [Fact]
    public async Task CurrentOnlyMergeIntoRetainedRichTargetPreservesAllExistingBindingsInRealReexport()
    {
        using Workspace files = new();
        byte[] retainedBytesOne = [71, 73, 79, 83];
        byte[] retainedBytesTwo = [89, 97, 101, 103, 107];
        byte[] addedBytes = [109, 113, 127, 131, 137, 139];
        string oldRoot;
        Dictionary<string, string> oldRootFiles;
        MediaAttachmentBinding[] retainedBefore;
        NativeTransferPackageWriter.TransferPackageBudget expectedBudget;
        TransferAttachment added = SmallAttachment("17", "333", addedBytes);
        NativeTransferCatalog incoming = Content() with
        { Threads = [Content().Threads[0] with { Posts = [NewPost("17", 2, added)] }] };

        using (ProfileLease lease = ProfileLease.Acquire(files.Profile))
        {
            await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(files.Profile, lease);
            await SeedAsync(lease, catalog, files);
            await using ArchiveReader beforeReader = await ArchiveReader.OpenAsync(files.Profile);
            ArchivedThreadView before = Assert.IsType<ArchivedThreadView>(await beforeReader.ReadThreadAsync(Id));
            oldRoot = Assert.Single(before.MediaDirectories);
            // Genuine store bindings for 24 uncaptured post identities. No SQL/manifests
            // are fabricated and no captured post/deletion observations are invented.
            await AddRetainedAsync(oldRoot, "900", retainedBytesOne, Enumerable.Range(100, 12));
            await AddRetainedAsync(oldRoot, "901", retainedBytesTwo, Enumerable.Range(200, 12));
            MediaAssetSnapshot snapshot = await MediaAssetReader.OpenAsync(oldRoot);
            Assert.Equal(MediaManifestStatus.Available, snapshot.Status);
            Assert.Equal(25, snapshot.AttachmentBindings.Count);
            retainedBefore = snapshot.AttachmentBindings.Values.Where(binding => binding.Reference.PostId != "15")
                .OrderBy(binding => binding.Reference.RevisionKey, StringComparer.Ordinal).ToArray();
            Assert.Equal(24, retainedBefore.Length);
            Assert.All(retainedBefore, binding => Assert.Equal(MediaAcceptanceKind.Strict, binding.Acceptance.Kind));
            oldRootFiles = Files(oldRoot);

            await using NativeTransferImportStage stage = await StageWithPayloadsAsync(files, incoming, addedBytes);
            Assert.Empty(stage.Package.Catalog.Threads[0].RetainedBindings);
            NativeMediaMergePreparation preparation = await catalog.PrepareNativeMediaMergeAsync(stage);
            Assert.Equal(NativeMediaMergeError.None, preparation.Error);
            NativeMediaMergePlan plan = Assert.IsType<NativeMediaMergePlan>(preparation.Plan);
            Assert.Equal(0, plan.Conflicts);
            expectedBudget = plan.ExportBudget;
            Assert.True(expectedBudget.WithinContainerCap);
            using ProfileAdmissionToken admission = await lease.Admission.EnterExclusiveAsync();
            NativeMediaMergeResult result = await AssertMergeAsync(admission, catalog, plan, new MediaStore(), ProfileExclusiveDisposition.SafeToResume);
            Assert.Equal(NativeMediaImportCommitStatus.Committed, result.Status);
            Assert.Equal(1, result.NewPosts);
            Assert.Equal(1, result.NewAttachments);
            Assert.Equal(1, result.NewBindings);
            Assert.Equal(0, result.Conflicts);
        }

        await using ArchiveReader reader = await ArchiveReader.OpenAsync(files.Profile);
        ArchivedThreadView after = Assert.IsType<ArchivedThreadView>(await reader.ReadThreadAsync(Id));
        Assert.Equal(ThreadSourceState.NotFound, after.Summary.SourceState);
        Assert.Equal(3, after.PostCount);
        Assert.Equal(new[] { "15", "16", "17" }, after.Posts.Select(post => post.PostId));
        Assert.Equal(2, after.MediaDirectories.Count);
        Assert.Contains(oldRoot, after.MediaDirectories);
        MediaAssetSnapshot originalSnapshot = await MediaAssetReader.OpenAsync(oldRoot);
        foreach (MediaAttachmentBinding binding in retainedBefore)
            Assert.Equal(binding, originalSnapshot.FindAttachmentBinding(binding.Reference.RevisionKey));

        NativeTransferPackage exported = await ExportAndCheckPayloadsAsync(reader, expectedBudget,
            Bytes, retainedBytesOne, retainedBytesTwo, addedBytes);
        TransferThread thread = Assert.Single(exported.Catalog.Threads);
        Assert.Equal("chan-v3", exported.Envelope.ContentSchema); // new passive current bytes have no invented history
        Assert.Equal(24, thread.RetainedBindings.Length);
        Assert.Equal(3, thread.Posts.Length); // retained bindings never become synthetic posts
        foreach (MediaAttachmentBinding binding in retainedBefore)
        {
            TransferRetainedBinding retained = Assert.Single(thread.RetainedBindings,
                value => value.Attachment.Metadata.AttachmentRevisionKey == binding.Reference.RevisionKey);
            Assert.Equal(binding.Reference.PostId, retained.PostId);
            Assert.Equal(Metadata(binding.Provider, binding.Reference.PostId), retained.Attachment.Metadata);
            Assert.Equal(TransferContentStatus.Contained, retained.Attachment.Status);
            Assert.Equal(binding.Actual.Sha256, retained.Attachment.PayloadSha256);
            Assert.Equal(new TransferHistoricalIntegrity("strict", binding.Acceptance.AcceptedAtUtc,
                binding.Acceptance.SizeMismatch, binding.Acceptance.Md5Mismatch), retained.Attachment.History);
            Assert.DoesNotContain(thread.Posts, post => post.Metadata.PostId == binding.Reference.PostId);
        }
        TransferAttachment currentAdded = Assert.Single(thread.Posts.Single(post => post.Metadata.PostId == "17").Attachments);
        Assert.Equal(added.Metadata, currentAdded.Metadata);
        Assert.Equal(Hash(addedBytes), currentAdded.PayloadSha256);
        Assert.Null(currentAdded.History);
        Assert.Equal(oldRootFiles, Files(oldRoot));
    }

    [Fact]
    public async Task ExportableRetainedRichTargetRejectsIndividuallyValidAdditionWhenFullCatalogWouldOverflow()
    {
        using Workspace files = new();
        using ProfileLease lease = ProfileLease.Acquire(files.Profile);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(files.Profile, lease);
        await SeedAsync(lease, catalog, files);
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(files.Profile);
        ArchivedThreadView before = (await reader.ReadThreadAsync(Id))!;
        string root = Assert.Single(before.MediaDirectories);
        byte[] retainedBytes = [149, 151, 157, 163];
        // Real store bindings stay small. Large but valid captured HTML puts the complete
        // target near the catalog cap, without exceeding provider-name or manifest limits.
        const int cap = 16 * 1024 * 1024;
        const int retainedCount = 24;
        await AddRetainedAsync(root, "909", retainedBytes, Enumerable.Range(1000, retainedCount));
        List<CapturedPost> posts = before.Posts.ToList();
        for (int i = 0; i < 15; i++)
            posts.Add(new((100 + i).ToString(), 100 + i, PostFieldPresence.Html, Html: new string('x', 1024 * 1024)));
        posts.Add(new("200", 200, PostFieldPresence.Html, Html: string.Empty));
        await catalog.CapturePreservingRevisionsAsync(new(Id, Url, before.Summary.Title, before.Summary.SourceState,
            At, root, posts));
        TransferArchiveFreezeResult unpadded = await NativeTransferArchiveExporter.FreezeAsync(reader, Id, At.AddDays(1), Guid.NewGuid());
        Assert.True(unpadded.IsFrozen, unpadded.Error.ToString());
        int padding = cap - 8 * 1024 - NativeTransferCatalogSerializer.Serialize(unpadded.Snapshot!.Catalog).Length;
        Assert.InRange(padding, 1, 1024 * 1024);
        posts[^1] = posts[^1] with { Html = new string('x', padding) };
        await catalog.CapturePreservingRevisionsAsync(new(Id, Url, before.Summary.Title, before.Summary.SourceState,
            At, root, posts));
        ArchivedThreadView target = (await reader.ReadThreadAsync(Id))!;
        // Baseline must be exportable through the actual exporter/writer/validator, not
        // merely under an estimate. Existing retained metadata and bytes are all present.
        NativeTransferPackage exported = await ExportAndCheckPayloadsAsync(reader, null, Bytes, retainedBytes);
        Assert.Equal(retainedCount, exported.Catalog.Threads[0].RetainedBindings.Length);
        Assert.Equal(cap - 8 * 1024, exported.Envelope.CatalogBytes.Length);

        byte[] incomingBytes = [167, 173, 179, 181];
        TransferAttachment added = SmallAttachment("17", "444", incomingBytes);
        TransferPost post = NewPost("17", 2, added);
        post = post with { Metadata = post.Metadata with { Html = new string('p', 12 * 1024) } };
        NativeTransferCatalog incoming = Content() with
        { Threads = [Content().Threads[0] with { Posts = [post] }] };
        // Incoming is independently admitted by the actual native writer and stage.
        await using NativeTransferImportStage stage = await StageWithPayloadsAsync(files, incoming, incomingBytes);
        Assert.True(stage.Package.Envelope.CatalogBytes.Length < 16 * 1024);
        Assert.Empty(stage.Package.Catalog.Threads[0].RetainedBindings);

        // Current-only prospective metadata would still fit. The same union with the
        // retained target bindings and contained SHA/history fields cannot be serialized.
        // This distinguishes the original C01 gap from an already overfull input/target.
        TransferThread currentOnly = NativeTransferArchiveProjection.From(new ArchivedThreadView(
            target.Summary with { Revision = target.Summary.Revision + 1 }, target.Posts.Append(post.Metadata),
            target.MediaDirectories, target.DecimalPostOrder));
        Assert.True(NativeTransferCatalogSerializer.Serialize(new(At, [currentOnly])).Length < cap);
        TransferThread complete = exported.Catalog.Threads[0];
        Assert.Throws<InvalidDataException>(() => NativeTransferCatalogSerializer.Serialize(exported.Catalog with
        { Threads = [complete with { CaptureRevision = complete.CaptureRevision + 1, Posts = complete.Posts.Add(post) }] }));
        Dictionary<string, string> oldFiles = Files(files.Profile);
        NativeMediaMergePreparation rejected = await catalog.PrepareNativeMediaMergeAsync(stage);
        Assert.Null(rejected.Plan);
        Assert.Equal(NativeMediaMergeError.TargetBudgetExceeded, rejected.Error);
        Assert.Empty((await catalog.ReadNativeMediaMergeOperationsPageAsync()).Items);
        Assert.False(Directory.Exists(Path.Combine(files.Profile, "merge-media-operations")));
        Assert.Equal(oldFiles, Files(files.Profile));
        Assert.Equal(target.Summary, (await reader.ReadThreadAsync(Id))!.Summary);
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

    private static async Task SeedAsync(ProfileLease lease, ArchiveCatalog catalog, Workspace files)
    {
        await using NativeTransferImportStage stage = await Stage(files, Content());
        NativeMediaImportPreparation prepared = await catalog.PrepareNativeMediaImportAsync(stage);
        Assert.Equal(NativeMediaImportError.None, prepared.Error);
        NativeMediaImportPlan plan = Assert.IsType<NativeMediaImportPlan>(prepared.Plan);
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

    private static TransferPost NewPost(string postId, long ordinal, TransferAttachment attachment)
        => new(new(postId, ordinal, PostFieldPresence.All, At, "", "", "new-safe-post", false, [attachment.Metadata]), [attachment]);

    private static MediaProviderMetadata Provider(string mediaId, byte[] bytes)
        => new(mediaId, "original", $"https://i.4cdn.org/b/{mediaId}.jpg", $"synthetic-{mediaId}.bin",
            bytes.Length, Md5(bytes), Hash(bytes)) { ExpectedSha1 = Sha1(bytes) };

    private static CapturedAttachment Metadata(MediaProviderMetadata provider, string postId)
        => new(0, provider.MediaId, provider.Variant, provider.Url, provider.OriginalFileName,
            ExpectedSize: provider.ExpectedSize, ExpectedMd5: provider.ExpectedMd5)
        {
            ExpectedSha1 = provider.ExpectedSha1, ExpectedSha256 = provider.ExpectedSha256,
            ProviderSizeHint = provider.ProviderSizeHint,
            AttachmentRevisionKey = MediaRevisionKeys.CreateAttachmentKey(Id, postId, 0, provider)
        };

    private static TransferAttachment SmallAttachment(string postId, string mediaId, byte[] bytes)
        => new(Metadata(Provider(mediaId, bytes), postId), TransferMediaRole.Original,
            TransferContentStatus.Contained, Hash(bytes), null);

    private static async Task AddRetainedAsync(string root, string mediaId, byte[] bytes, IEnumerable<int> postIds)
    {
        MediaProviderMetadata provider = Provider(mediaId, bytes);
        OfflineDescriptor descriptor = new();
        ProbeThread thread = new(new OfflineBoard(descriptor), new Uri(Url))
        { OnCheck = () => throw new InvalidOperationException("No provider work in a transfer fixture.") };
        DownloadItem item = new(new Uri(provider.Url), Path.Combine(root, $"retained-{mediaId}.bin"), descriptor, thread,
            provider.MediaId, provider.ExpectedSize, provider.ExpectedMd5, provider.OriginalFileName)
        {
            ExpectedSha1 = provider.ExpectedSha1, ExpectedSha256 = provider.ExpectedSha256,
            AcquisitionRevisionKey = MediaRevisionKeys.CreateAcquisitionKey(Id, provider),
            AttachmentReferences = postIds.Select(postId => new AttachmentRevisionReference(Id, postId.ToString(), 0,
                MediaRevisionKeys.CreateAttachmentKey(Id, postId.ToString(), 0, provider))).ToArray()
        };
        using MemoryStream source = new(bytes, writable: false);
        MediaCommitResult result = await new MediaStore().CommitAsync(item, source, CancellationToken.None);
        Assert.Equal(Hash(bytes), result.Sha256);
    }

    private static async Task<NativeTransferImportStage> StageWithPayloadsAsync(Workspace files,
        NativeTransferCatalog catalog, params byte[][] payloads)
    {
        using MemoryStream package = new();
        TransferPayloadSource[] sources = payloads.Select(bytes => new TransferPayloadSource(new(bytes.Length, Hash(bytes)),
            _ => Task.FromResult<Stream>(new MemoryStream(bytes, writable: false)))).ToArray();
        TransferWriteResult written = await NativeTransferPackageWriter.WriteAsync(package, catalog, sources, Guid.NewGuid());
        Assert.True(written.IsComplete, written.Error.ToString());
        package.Position = 0;
        NativeTransferStageResult staged = await NativeTransferImportStage.CreateAsync(package, files.StageRoot);
        Assert.Equal(NativeTransferStageError.None, staged.Error);
        return Assert.IsType<NativeTransferImportStage>(staged.Stage);
    }

    private static async Task<ResolvedMediaAttachment> AssertResolvedAsync(IReadOnlyList<MediaAssetSnapshot> snapshots,
        string postId, CapturedAttachment attachment, byte[] expected)
    {
        ResolvedMediaAttachment resolved = Assert.IsType<ResolvedMediaAttachment>(
            MediaAttachmentResolver.Resolve(snapshots, Id, postId, attachment));
        Assert.False(resolved.IsLegacy);
        await using Stream input = await resolved.Snapshot.OpenVerifiedReadAsync(resolved.Asset.AssetId);
        using MemoryStream actual = new();
        await input.CopyToAsync(actual);
        Assert.Equal(expected, actual.ToArray());
        return resolved;
    }

    private static async Task<NativeTransferPackage> ExportAndCheckPayloadsAsync(ArchiveReader reader,
        NativeTransferPackageWriter.TransferPackageBudget? expectedBudget, params byte[][] expected)
    {
        // Different export time, same canonical date length; package GUIDs also have
        // fixed wire length, so the prospective and actual byte budgets must match.
        TransferArchiveFreezeResult frozen = await NativeTransferArchiveExporter.FreezeAsync(reader, Id, At.AddDays(1), Guid.NewGuid());
        Assert.True(frozen.IsFrozen, frozen.Error.ToString());
        NativeTransferArchiveSnapshot snapshot = Assert.IsType<NativeTransferArchiveSnapshot>(frozen.Snapshot);
        using MemoryStream output = new();
        // Exporter.WriteAsync calls the actual NativeTransferPackageWriter, including
        // catalog/manifest serialization and measured payload validation.
        TransferArchiveWriteResult written = await NativeTransferArchiveExporter.WriteAsync(snapshot, output);
        Assert.True(written.IsComplete, written.Error + "/" + written.WriteError);
        Assert.Equal(output.Length, written.BytesWritten);
        TransferPackageInspection reinspected = await NativeTransferPackageValidator.InspectAsync(output);
        Assert.True(reinspected.IsPackageValid, reinspected.EnvelopeError + "/" + reinspected.CatalogError);
        NativeTransferPackage package = Assert.IsType<NativeTransferPackage>(reinspected.Package);
        string[] expectedHashes = expected.Select(Hash).OrderBy(hash => hash, StringComparer.Ordinal).ToArray();
        Assert.Equal(expectedHashes, package.Envelope.Payloads.Select(payload => payload.Sha256).OrderBy(hash => hash, StringComparer.Ordinal).ToArray());
        Assert.Equal(NativeTransferCatalogSerializer.Serialize(snapshot.Catalog), package.Envelope.CatalogBytes.ToArray());
        output.Position = 0;
        using ZipArchive zip = new(output, ZipArchiveMode.Read, leaveOpen: true);
        Assert.Equal(expected.Length + 2, zip.Entries.Count); // no inactive/conflicting payload accidentally included
        if (expectedBudget is { } budget)
        {
            long payloadBytes = package.Envelope.Payloads.Sum(payload => payload.Length);
            long zipOverhead = output.Length - payloadBytes - package.Envelope.CatalogBytes.Length
                - zip.GetEntry("manifest.json")!.Length;
            Assert.Equal(budget.ContainerBytes, output.Length);
            Assert.Equal(budget.CatalogBytes, package.Envelope.CatalogBytes.Length);
            Assert.Equal(budget.ManifestBytes, zip.GetEntry("manifest.json")!.Length);
            Assert.Equal(budget.PayloadBytes, payloadBytes);
            Assert.Equal(budget.ZipOverhead, zipOverhead);
        }
        foreach (byte[] bytes in expected)
        {
            ZipArchiveEntry entry = Assert.IsType<ZipArchiveEntry>(zip.GetEntry("payload/" + Hash(bytes)));
            await using Stream input = entry.Open();
            using MemoryStream actual = new();
            await input.CopyToAsync(actual);
            Assert.Equal(bytes, actual.ToArray());
        }
        return package;
    }

    // Identity only, deliberately no HTTP client or provider behind this seed.
    private sealed class OfflineDescriptor : IImageBoard
    {
        public string Name => "4chan";
        public string NiceName => "Synthetic merge fixture";
        public Uri BaseUri => new("https://boards.4chan.org/");
        public bool CanHandle(Uri uri) => throw Forbidden();
        public bool IsThreadUri(Uri uri) => throw Forbidden();
        public bool IsBoardUri(Uri uri) => throw Forbidden();
        public IBoard GetBoard(Uri uri) => throw Forbidden();
        public IThread GetThread(Uri uri) => throw Forbidden();
        public HttpClient GetHttpClient() => throw Forbidden();
        private static InvalidOperationException Forbidden() => new("No provider/network work in a transfer fixture.");
    }

    private sealed class OfflineBoard(IImageBoard descriptor) : BaseBoard
    {
        public override IImageBoard ImageBoard => descriptor;
        public override string BoardCode => "b";
        public override string Name { get; protected set; } = "Synthetic";
        public override string NiceName => Name;
        public override Uri BoardUri => new("https://boards.4chan.org/b/");
        public override Task<IEnumerable<IThread>> GetThreadsAsync(CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("No provider work in a transfer fixture.");
    }
}
