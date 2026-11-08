using System.IO.Compression;
using System.Text.Json.Nodes;
using MeowChan.Archive;
using MeowChan.Persistence;
using MeowChan.Storage;
using MeowChan.Transfer;
using Microsoft.Data.Sqlite;
using static MeowChan.Tests.Transfer.NativeMediaImportTests;

namespace MeowChan.Tests.Transfer;

// Public one-thread v4 imports and merges use lossless v2 plans, never legacy projections.
// Preparation is not Apply. Exercise real framing/staging and the actual owner APIs.
public sealed class NativeTransferV4ConsumerBoundaryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FullOriginalV4PreparesV2ImportOrMergeWithoutLegacyProjection(bool existingTarget)
    {
        using Workspace files = new();
        using ProfileLease lease = ProfileLease.Acquire(files.Profile);
        await using ArchiveCatalog target = await ArchiveCatalog.OpenAsync(files.Profile, lease);
        NativeTransferCatalog content = Content();
        TransferThread thread = content.Threads[0];
        TransferPost post = thread.Posts[0];
        TransferAttachment original = post.Attachments[0] with
        {
            OriginalByteVersion = new(Bytes.Length, Hash(Bytes), Md5(Bytes), Sha1(Bytes))
        };
        content = content with { Threads = [thread with
        {
            Posts = thread.Posts.SetItem(0, post with { Attachments = [original] })
        }] };
        await using NativeTransferImportStage stage = await Stage(files, content);
        Assert.Equal("chan-v4", stage.Package.Envelope.ContentSchema);
        Assert.NotNull(stage.Package.Catalog.Threads[0].Posts[0].Attachments[0].OriginalByteVersion);
        await AssertV2ImportOrMergePreparationAsync(files, target, stage, existingTarget);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EqualHashOriginalAndPreviewV4PrepareDistinctV2ImportOrMergeMembers(bool existingTarget)
    {
        using Workspace files = new();
        using ProfileLease lease = ProfileLease.Acquire(files.Profile);
        await using ArchiveCatalog target = await ArchiveCatalog.OpenAsync(files.Profile, lease);
        TransferAttachment original = Attachment() with
        {
            OriginalByteVersion = new(Bytes.Length, Hash(Bytes), Md5(Bytes), Sha1(Bytes))
        };
        original = original with
        {
            DerivedRepresentations = [new(TransferDerivedRepresentationKind.Preview,
                original.Metadata.AttachmentRevisionKey!, original.OriginalByteVersion!,
                TransferContentStatus.Contained, original.PayloadSha256)]
        };
        await using NativeTransferImportStage stage = await StageValidatedV4(files, WithAttachment(original), Bytes);
        TransferAttachment parsed = Assert.Single(stage.Package.Catalog.Threads[0].Posts[0].Attachments);
        Assert.Equal(TransferMediaRole.Original, parsed.Role);
        Assert.Equal(TransferContentStatus.Contained, parsed.Status);
        Assert.Equal(original.OriginalByteVersion, parsed.OriginalByteVersion);
        TransferDerivedRepresentation preview = Assert.Single(parsed.DerivedRepresentations);
        Assert.Equal(TransferDerivedRepresentationKind.Preview, preview.Kind);
        Assert.Equal(TransferContentStatus.Contained, preview.Status);
        Assert.Equal(parsed.Metadata.AttachmentRevisionKey, preview.SourceRevisionKey);
        Assert.Equal(parsed.OriginalByteVersion, preview.SourceByteVersion);
        Assert.Equal(parsed.PayloadSha256, preview.PayloadSha256);
        Assert.Equal(Hash(Bytes), Assert.Single(stage.Package.Envelope.Payloads).Sha256);
        Assert.Single(stage.Package.Envelope.MeasuredPayloads);
        using MemoryStream copied = new();
        TransferMeasuredBlob measured = await stage.CopyPayloadToAsync(parsed.PayloadSha256!, copied);
        Assert.Equal(Bytes, copied.ToArray());
        Assert.Equal(new TransferMeasuredBlob(Bytes.Length, Hash(Bytes), Md5(Bytes), Sha1(Bytes)), measured);
        // One physical blob is not permission to drop the separately bound Preview.
        await AssertV2ImportOrMergePreparationAsync(files, target, stage, existingTarget);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AbsentOriginalV4PreparesEvidenceOnlyV2ImportOrMerge(bool existingTarget)
    {
        using Workspace files = new();
        using ProfileLease lease = ProfileLease.Acquire(files.Profile);
        await using ArchiveCatalog target = await ArchiveCatalog.OpenAsync(files.Profile, lease);
        TransferAttachment original = Attachment() with
        {
            Status = TransferContentStatus.Omitted,
            PayloadSha256 = null,
            OriginalByteVersion = new(Bytes.Length, Hash(Bytes), Md5(Bytes), Sha1(Bytes))
        };
        await using NativeTransferImportStage stage = await StageValidatedV4(files, WithAttachment(original));
        TransferAttachment parsed = Assert.Single(stage.Package.Catalog.Threads[0].Posts[0].Attachments);
        Assert.Equal(TransferMediaRole.Original, parsed.Role);
        Assert.Equal(TransferContentStatus.Omitted, parsed.Status);
        Assert.Equal(original.OriginalByteVersion, parsed.OriginalByteVersion);
        Assert.Null(parsed.PayloadSha256);
        Assert.Null(parsed.History);
        Assert.Empty(parsed.DerivedRepresentations);
        Assert.Empty(stage.Package.Envelope.Payloads);
        Assert.Empty(stage.Package.Envelope.MeasuredPayloads);
        using MemoryStream absent = new();
        await Assert.ThrowsAsync<ArgumentException>(() => stage.CopyPayloadToAsync(parsed.OriginalByteVersion!.Sha256, absent));
        Assert.Equal(0L, absent.Length); // Evidence must not invent even an unpublished payload.
        await AssertV2ImportOrMergePreparationAsync(files, target, stage, existingTarget);
    }

    [Fact]
    public async Task TextOnlyV4IsRejectedByTextImportAndRootlessMediaConsumers()
    {
        using Workspace files = new();
        using ProfileLease lease = ProfileLease.Acquire(files.Profile);
        await using ArchiveCatalog target = await ArchiveCatalog.OpenAsync(files.Profile, lease);
        TransferThread thread = Content().Threads[0];
        NativeTransferCatalog text = new(At, [thread with
        {
            Posts = [new(thread.Posts[1].Metadata, [])]
        }]);
        using MemoryStream older = new();
        Assert.True((await NativeTransferPackageWriter.WriteAsync(older, text, [], Guid.NewGuid())).IsComplete);
        using MemoryStream v4 = AsTextOnlyV4(older.ToArray());
        TransferPackageInspection inspection = await NativeTransferPackageValidator.InspectAsync(v4);
        Assert.True(inspection.IsPackageValid, inspection.ToString());
        Assert.Equal("chan-v4", inspection.Package!.Envelope.ContentSchema);
        // The DTO can downlevel to v1; the consumer must inspect the actual wire.
        Assert.Equal("chan-v1", NativeTransferCatalogSerializer.ContentSchema(inspection.Package.Catalog));
        v4.Position = 0;
        NativeTransferStageResult staged = await NativeTransferImportStage.CreateAsync(v4, files.StageRoot);
        Assert.Equal(NativeTransferStageError.None, staged.Error);
        await using NativeTransferImportStage stage = Assert.IsType<NativeTransferImportStage>(staged.Stage);
        Assert.True(stage.Package.IsChanV4);
        Assert.Empty(NativeMediaMaterializationPlan.Create(stage, Id).FullSelection.Members);
        Dictionary<string, string> before = Files(files.Profile);
        string[] directoriesBefore = Directory.GetDirectories(files.Profile, "*", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.Ordinal).ToArray();
        v4.Position = 0;
        NativeTextImportPreparation result = await target.PrepareNativeTextImportAsync(v4);
        Assert.Equal(NativeTextImportError.InvalidPackage, result.Error);
        Assert.Null(result.Plan);
        await AssertRootlessMediaRejectedAsync(target, stage);
        Assert.Equal(before, Files(files.Profile));
        AssertNoOperations(files.Profile);
        // Unlike valid one-thread media v4, rootless v4 still rejects before SQL.
        await target.DisposeAsync();
        v4.Position = 0;
        NativeTextImportPreparation rejected = await target.PrepareNativeTextImportAsync(v4);
        Assert.Equal(NativeTextImportError.InvalidPackage, rejected.Error);
        Assert.Null(rejected.Plan);
        await AssertRootlessMediaRejectedAsync(target, stage);
        Assert.Equal(before, Files(files.Profile));
        Assert.Equal(directoriesBefore, Directory.GetDirectories(files.Profile, "*", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.Ordinal).ToArray());
        AssertNoOperations(files.Profile);
    }

    [Fact]
    public async Task PrivateV4IsValidWireButCannotCreateAnImportOrMergeStage()
    {
        using Workspace files = new();
        using ProfileLease lease = ProfileLease.Acquire(files.Profile);
        await using ArchiveCatalog target = await ArchiveCatalog.OpenAsync(files.Profile, lease);
        TransferAttachment attachment = Attachment() with
        {
            OriginalByteVersion = new(Bytes.Length, Hash(Bytes), Md5(Bytes), Sha1(Bytes))
        };
        NativePrivateMetadataProjection privateMetadata = new([Id], new(true, false, false), [Id], [], []);
        using MemoryStream package = new();
        TransferWriteResult written = await NativeTransferPackageWriter.WriteAsync(package, WithAttachment(attachment),
            [new(new(Bytes.Length, Hash(Bytes)), _ => Task.FromResult<Stream>(new MemoryStream(Bytes, false)))],
            Guid.NewGuid(), privateMetadata: privateMetadata);
        Assert.True(written.IsComplete, written.ToString());
        package.Position = 0;
        TransferPackageInspection inspection = await NativeTransferPackageValidator.InspectAsync(package);
        Assert.True(inspection.IsPackageValid, inspection.ToString());
        Assert.True(inspection.Package!.IsChanV4);
        Assert.NotNull(inspection.Package.PrivateMetadata);
        Assert.NotNull(inspection.Package.Envelope.PrivateMetadata);
        Dictionary<string, string> before = Files(files.Profile);
        string[] directoriesBefore = Directory.GetDirectories(files.Profile, "*", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.Ordinal).ToArray();
        package.Position = 0;
        NativeTransferStageResult staged = await NativeTransferImportStage.CreateAsync(package, files.StageRoot);
        // The real ingress gate rejects private metadata before a stage can reach
        // either Prepare API. Do not forge an otherwise unreachable private stage.
        Assert.Equal(NativeTransferStageError.PrivateMetadataNotYetSupported, staged.Error);
        Assert.Null(staged.Stage);
        Assert.False(staged.StageRetained);
        Assert.Empty(Directory.GetFileSystemEntries(files.StageRoot));
        Assert.Equal(before, Files(files.Profile));
        Assert.Equal(directoriesBefore, Directory.GetDirectories(files.Profile, "*", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.Ordinal).ToArray());
        AssertNoOperations(files.Profile);
    }

    [Theory]
    [InlineData("multiThread")]
    [InlineData("missingPageUrl")]
    [InlineData("legacyPreview")]
    public async Task UnsupportedPackagesStillRejectBeforeReadingEvenADisposedTarget(string kind)
    {
        using Workspace files = new();
        using ProfileLease lease = ProfileLease.Acquire(files.Profile);
        await using ArchiveCatalog target = await ArchiveCatalog.OpenAsync(files.Profile, lease);
        TransferAttachment attachment = Attachment() with
        {
            OriginalByteVersion = new(Bytes.Length, Hash(Bytes), Md5(Bytes), Sha1(Bytes))
        };
        if (kind == "legacyPreview")
            attachment = Attachment() with { Role = TransferMediaRole.Preview,
                Metadata = Attachment().Metadata with { AttachmentRevisionKey = null, Variant = "preview" } };
        NativeTransferCatalog content = WithAttachment(attachment);
        TransferThread thread = content.Threads[0];
        if (kind == "multiThread")
            content = content with { Threads = [thread, thread with
            { Id = new("4chan", "b", "7002"), PageUrl = "https://boards.4chan.org/b/thread/7002", Posts = [] }] };
        if (kind == "missingPageUrl")
            content = content with { Threads = [thread with { PageUrl = null }] };
        using MemoryStream package = new();
        TransferWriteResult written = await NativeTransferPackageWriter.WriteAsync(package, content,
            [new(new(Bytes.Length, Hash(Bytes)), _ => Task.FromResult<Stream>(new MemoryStream(Bytes, false)))],
            Guid.NewGuid());
        Assert.True(written.IsComplete, written.ToString());
        package.Position = 0;
        NativeTransferStageResult staged = await NativeTransferImportStage.CreateAsync(package, files.StageRoot);
        Assert.Equal(NativeTransferStageError.None, staged.Error);
        await using NativeTransferImportStage stage = Assert.IsType<NativeTransferImportStage>(staged.Stage);
        Assert.Equal(kind != "legacyPreview", stage.Package.IsChanV4);
        Assert.Equal(kind == "multiThread" ? 2 : 1, stage.Package.Catalog.Threads.Length);
        Assert.Null(stage.Package.PrivateMetadata);
        if (kind == "missingPageUrl") Assert.Null(stage.Package.Catalog.Threads[0].PageUrl);
        if (kind == "legacyPreview")
            Assert.Equal(TransferMediaRole.Preview, stage.Package.Catalog.Threads[0].Posts[0].Attachments[0].Role);
        Dictionary<string, string> before = Files(files.Profile);
        string[] directoriesBefore = Directory.GetDirectories(files.Profile, "*", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.Ordinal).ToArray();
        await AssertRejectedAsync();
        AssertNoOperations(files.Profile);
        await target.DisposeAsync(); // Only unsupported input keeps this pre-target sentinel.
        await AssertRejectedAsync();
        Assert.Equal(before, Files(files.Profile));
        Assert.Equal(directoriesBefore, Directory.GetDirectories(files.Profile, "*", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.Ordinal).ToArray());
        AssertNoOperations(files.Profile);

        async Task AssertRejectedAsync()
        {
            NativeMediaMergePreparation merge = await target.PrepareNativeMediaMergeAsync(stage);
            Assert.Equal(NativeMediaMergeError.UnsupportedPackage, merge.Error);
            Assert.Null(merge.Plan);
            NativeMediaImportPreparation import = await target.PrepareNativeMediaImportAsync(stage);
            Assert.Equal(kind == "missingPageUrl" ? NativeMediaImportError.MissingPageUrl
                : NativeMediaImportError.UnsupportedPackage, import.Error);
            Assert.Null(import.Plan);
        }
    }

    private static async Task AssertRootlessMediaRejectedAsync(ArchiveCatalog target, NativeTransferImportStage stage)
    {
        NativeMediaImportPreparation import = await target.PrepareNativeMediaImportAsync(stage);
        Assert.Equal(NativeMediaImportError.UnsupportedPackage, import.Error);
        Assert.Null(import.Plan);
        NativeMediaMergePreparation merge = await target.PrepareNativeMediaMergeAsync(stage);
        Assert.Equal(NativeMediaMergeError.UnsupportedPackage, merge.Error);
        Assert.Null(merge.Plan);
    }

    private static NativeTransferCatalog WithAttachment(TransferAttachment attachment)
    {
        NativeTransferCatalog content = Content();
        TransferThread thread = content.Threads[0];
        TransferPost post = thread.Posts[0];
        return content with { Threads = [thread with
        {
            Posts = thread.Posts.SetItem(0, post with
            { Metadata = post.Metadata with { Attachments = [attachment.Metadata] }, Attachments = [attachment] })
        }] };
    }

    private static async Task<NativeTransferImportStage> StageValidatedV4(Workspace files,
        NativeTransferCatalog content, params byte[][] payloads)
    {
        using MemoryStream package = new();
        TransferPayloadSource[] sources = payloads.Select(bytes => new TransferPayloadSource(new(bytes.Length, Hash(bytes)),
            _ => Task.FromResult<Stream>(new MemoryStream(bytes, false)))).ToArray();
        TransferWriteResult written = await NativeTransferPackageWriter.WriteAsync(package, content, sources, Guid.NewGuid());
        Assert.True(written.IsComplete, written.ToString());
        Assert.True(written.Package!.IsChanV4);
        package.Position = 0;
        using (ZipArchive zip = new(package, ZipArchiveMode.Read, leaveOpen: true))
        {
            Assert.NotNull(zip.GetEntry("manifest.json"));
            Assert.NotNull(zip.GetEntry("catalog.json"));
            Assert.Equal(2 + payloads.Length, zip.Entries.Count);
            Assert.Equal(payloads.Select(bytes => "payload/" + Hash(bytes)).OrderBy(name => name, StringComparer.Ordinal),
                zip.Entries.Where(entry => entry.FullName.StartsWith("payload/", StringComparison.Ordinal))
                    .Select(entry => entry.FullName).OrderBy(name => name, StringComparer.Ordinal));
        }
        package.Position = 0;
        TransferPackageInspection inspection = await NativeTransferPackageValidator.InspectAsync(package);
        Assert.True(inspection.IsPackageValid, inspection.ToString());
        Assert.True(inspection.Package!.IsChanV4);
        Assert.Null(inspection.Package.Envelope.PrivateMetadata);
        package.Position = 0;
        NativeTransferStageResult staged = await NativeTransferImportStage.CreateAsync(package, files.StageRoot);
        Assert.Equal(NativeTransferStageError.None, staged.Error);
        NativeTransferImportStage stage = Assert.IsType<NativeTransferImportStage>(staged.Stage);
        Assert.True(stage.Package.IsChanV4);
        Assert.Equal("chan-v4", stage.Package.Envelope.ContentSchema);
        Assert.Equal(NativeTransferCatalogSerializer.Serialize(content), NativeTransferCatalogSerializer.Serialize(stage.Package.Catalog));
        return stage;
    }

    private static async Task AssertV2ImportOrMergePreparationAsync(Workspace files, ArchiveCatalog target,
        NativeTransferImportStage stage, bool existingTarget)
    {
        TransferThread thread = Assert.Single(stage.Package.Catalog.Threads);
        MediaAssetSnapshot? targetMedia = null;
        if (existingTarget)
        {
            // Accepted preparation legitimately reads SQL and verifies target roots. An
            // inert registration/missing directory is not a valid success fixture anymore.
            string root = Directory.CreateDirectory(Path.Combine(files.Profile, "media")).FullName;
            await File.WriteAllTextAsync(Path.Combine(root, ".meowchan-manifest.json"),
                """
                {"Version":6,"Entries":{},"Assets":{},"AttachmentBindings":{},"OriginalEvidence":[],"DerivedRepresentations":[]}
                """);
            await File.WriteAllBytesAsync(Path.Combine(root, ".meowchan-store.lock"), []);
            await target.CapturePreservingRevisionsAsync(new(thread.Id, thread.PageUrl!, thread.Title,
                thread.SourceState, At, root, []) { DecimalPostOrder = thread.DecimalPostOrder });
            targetMedia = await MediaAssetReader.OpenAsync(root);
            Assert.Equal(MediaManifestStatus.Available, targetMedia.Status);
            Assert.Equal(6, targetMedia.ManifestVersion);
            Assert.Empty(targetMedia.OriginalEvidence);
            Assert.Empty(targetMedia.DerivedRepresentations);
            Assert.Empty(targetMedia.AttachmentBindings);
        }
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(files.Profile);
        ArchivedThreadView? targetBefore = await reader.ReadThreadAsync(Id);
        if (existingTarget)
        {
            Assert.NotNull(targetBefore);
            Assert.Empty(targetBefore.GetPosts());
        }
        else Assert.Null(targetBefore);
        Dictionary<string, string> before = Files(files.Profile);
        string[] directoriesBefore = Directory.GetDirectories(files.Profile, "*", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.Ordinal).ToArray();
        if (existingTarget)
        {
            NativeMediaMergePreparation result = await target.PrepareNativeMediaMergeAsync(stage);
            Assert.Equal(NativeMediaMergeError.None, result.Error);
            NativeMediaMergePlan plan = Assert.IsType<NativeMediaMergePlan>(result.Plan);
            Assert.Equal(2, plan.PlanVersion);
            Assert.Same(target, plan.Owner);
            Assert.Same(stage, plan.Stage);
            Assert.Same(stage.Package, plan.Package);
            Assert.Equal(Id, plan.Resource);
            Assert.Equal(targetBefore!.Summary, plan.Target.Summary);
            Assert.Equal(thread.DecimalPostOrder, plan.Target.DecimalPostOrder);
            NativeMediaMergeRoot root = Assert.Single(plan.Roots);
            Assert.Equal(targetMedia!.Directory, root.Directory);
            Assert.Equal(targetMedia.Generation, root.Generation);
            NativeMediaMergeV2State representation = Assert.IsType<NativeMediaMergeV2State>(plan.Representation);
            Assert.Equal(plan.Roots, representation.Roots);
            Assert.Equal(plan.OperationId, representation.Identity.OperationId);
            Assert.Equal(stage.Package.Envelope.CatalogBytes.ToArray(), representation.CatalogBytes.ToArray());
            AssertV2Members(stage, representation.FullSelection, representation.FullInventory);
            NativeMediaCandidateInventory safeInventory = Assert.IsType<NativeMediaCandidateInventory>(representation.SafeInventory);
            AssertV2Members(stage, representation.SafeSelection, safeInventory);
            Assert.Equal(representation.FullSelection.CanonicalJson, representation.SafeSelection.CanonicalJson);
            Assert.Equal(representation.FullSelection.Sha256, representation.SafeSelection.Sha256);
            Assert.Equal(representation.FullInventory.Sha256, safeInventory.Sha256);
            Assert.True(plan.HasSafeCandidate); // Evidence-only Safe also has zero Original bindings.
            Assert.True(plan.ReuseFullAsSafe);
            Assert.Equal(0, plan.Conflicts);
            Assert.Equal(thread.Posts.Length, plan.AddedPosts.Length);
            Assert.Empty(plan.UpdatedPosts);
            Assert.Empty(plan.AddedAttachments);
            Assert.Equal(thread.Posts.Sum(post => post.Attachments.Length), plan.NewAttachmentCount);
            Assert.Equal(safeInventory.Bindings.Length, plan.NewBindingCount);
            Assert.Equal(plan.AddedPosts.Length, representation.Counts.NewPosts);
            Assert.Equal(0, representation.Counts.UpdatedPosts);
            Assert.Equal(plan.NewAttachmentCount, representation.Counts.NewAttachments);
            Assert.Equal(safeInventory.OriginalEvidence.Length, representation.Counts.EvidenceAdditions);
            Assert.Equal(safeInventory.DerivedRepresentations.Length, representation.Counts.DerivedDescriptorAdditions);
            Assert.Equal(safeInventory.DerivedRepresentations.Count(d => d.IncomingStatus == MediaDerivedStatus.Contained),
                representation.Counts.ContainedDerivedAdditions);
            Assert.Throws<InvalidOperationException>(() => { _ = plan.Inputs; });
            Assert.Throws<InvalidOperationException>(() => { _ = plan.FullKeys; });
            Assert.Throws<InvalidOperationException>(() => { _ = plan.SafeKeys; });
            Assert.Throws<InvalidOperationException>(() => { _ = plan.FullKeysJson; });
            Assert.Throws<InvalidOperationException>(() => { _ = plan.SafeKeysJson; });
            Assert.False(Directory.Exists(plan.OperationsParent));
        }
        else
        {
            // This supported package reaches the real target lookup. Absence is not
            // UnsupportedPackage, and an import plan does not create the missing target.
            NativeMediaMergePreparation missing = await target.PrepareNativeMediaMergeAsync(stage);
            Assert.Equal(NativeMediaMergeError.MissingTarget, missing.Error);
            Assert.Null(missing.Plan);
            NativeMediaImportPreparation result = await target.PrepareNativeMediaImportAsync(stage);
            Assert.Equal(NativeMediaImportError.None, result.Error);
            NativeMediaImportPlan plan = Assert.IsType<NativeMediaImportPlan>(result.Plan);
            Assert.Equal(2, plan.PlanVersion);
            Assert.Same(target, plan.Owner);
            Assert.Same(stage, plan.Stage);
            Assert.Same(stage.Package, plan.Package);
            Assert.Equal(Id, plan.Resource);
            NativeMediaImportV2State representation = Assert.IsType<NativeMediaImportV2State>(plan.Representation);
            Assert.Equal(stage.Package.Envelope.CatalogBytes.ToArray(), representation.CatalogBytes.ToArray());
            AssertV2Members(stage, representation.Selection, representation.Inventory);
            Assert.Throws<InvalidOperationException>(() => { _ = plan.Attachments; });
            Assert.Throws<InvalidOperationException>(() => { _ = plan.RevisionKeysJson; });
            Assert.False(Directory.Exists(plan.OperationsParent));
        }
        ArchivedThreadView? after = await reader.ReadThreadAsync(Id);
        if (existingTarget)
        {
            Assert.NotNull(after);
            Assert.Equal(targetBefore!.Summary, after.Summary);
            Assert.Equal(targetBefore.MediaDirectories, after.MediaDirectories);
            Assert.Empty(after.GetPosts());
            using FileStream released = new(Path.Combine(targetMedia!.Directory, ".meowchan-store.lock"),
                FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            Assert.Equal(0L, released.Length);
        }
        else Assert.Null(after);
        Assert.Equal(before, Files(files.Profile));
        Assert.Equal(directoriesBefore, Directory.GetDirectories(files.Profile, "*", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.Ordinal).ToArray());
        AssertNoOperations(files.Profile);
    }

    private static void AssertV2Members(NativeTransferImportStage stage, NativeMediaCandidateSelection selection,
        NativeMediaCandidateInventory inventory)
    {
        NativeMediaCandidateSelection expected = NativeMediaMaterializationPlan.Create(stage, Id).FullSelection;
        Assert.False(selection.IsEmpty);
        Assert.Equal(expected.CanonicalJson, selection.CanonicalJson);
        Assert.Equal(expected.Sha256, selection.Sha256);
        TransferPost post = stage.Package.Catalog.Threads[0].Posts[0];
        TransferAttachment attachment = Assert.Single(post.Attachments);
        int originals = attachment.Status == TransferContentStatus.Contained ? 1 : 0;
        Assert.Equal(1 + originals + attachment.DerivedRepresentations.Length, selection.Members.Length);
        NativeMediaSelectedMember evidence = Assert.Single(selection.Members,
            member => member.Id.Role == NativeMediaMemberRole.OriginalEvidence);
        Assert.Equal(attachment.Metadata, evidence.Descriptor.Metadata);
        Assert.Equal(attachment.Status, evidence.Descriptor.Status);
        Assert.Equal(attachment.OriginalByteVersion, evidence.Id.SourceVersion);
        Assert.All(selection.Members, member =>
        {
            Assert.False(member.ContextOnly);
            Assert.False(member.Descriptor.Retained);
            Assert.Equal(Id, member.Id.Occurrence.Resource);
            Assert.Equal(post.Metadata.PostId, member.Id.Occurrence.PostId);
            Assert.Equal(attachment.Metadata.AttachmentRevisionKey, member.Id.Occurrence.OriginalRevisionKey);
        });
        Assert.Equal(originals, selection.Members.Count(member => member.Id.Role == NativeMediaMemberRole.OriginalPayload));
        Assert.Equal(attachment.DerivedRepresentations.Length,
            selection.Members.Count(member => member.Id.Role == NativeMediaMemberRole.Preview));
        Assert.Single(inventory.OriginalEvidence);
        Assert.Equal(originals, inventory.Bindings.Length);
        Assert.Equal(attachment.DerivedRepresentations.Length, inventory.DerivedRepresentations.Length);
    }

    private static MemoryStream AsTextOnlyV4(byte[] bytes)
    {
        using MemoryStream input = new(bytes);
        using ZipArchive source = new(input, ZipArchiveMode.Read);
        JsonObject Read(string name)
        {
            using Stream stream = source.GetEntry(name)!.Open();
            return JsonNode.Parse(stream)!.AsObject();
        }
        JsonObject catalog = Read("catalog.json");
        catalog["schema"] = "chan-v4";
        foreach (JsonNode? thread in catalog["threads"]!.AsArray())
            thread!["retainedBindings"] = new JsonArray();
        byte[] catalogBytes = System.Text.Encoding.UTF8.GetBytes(catalog.ToJsonString());
        JsonObject manifest = Read("manifest.json");
        manifest["contentSchema"] = "chan-v4";
        manifest["requiredFeatures"] = new JsonArray("chan-v4");
        manifest["catalog"] = new JsonObject { ["length"] = catalogBytes.Length, ["sha256"] = Hash(catalogBytes) };
        MemoryStream output = new();
        using (ZipArchive zip = new(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (Stream entry = zip.CreateEntry("catalog.json", CompressionLevel.NoCompression).Open())
                entry.Write(catalogBytes);
            using (Stream entry = zip.CreateEntry("manifest.json", CompressionLevel.NoCompression).Open())
                entry.Write(System.Text.Encoding.UTF8.GetBytes(manifest.ToJsonString()));
        }
        output.Position = 0;
        return output;
    }

    private static void AssertNoOperations(string profile)
    {
        using SqliteConnection connection = ArchiveSql.OpenConnection(Path.Combine(profile, "archive.db"), SqliteOpenMode.ReadOnly);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT (SELECT count(*) FROM archive_media_import_operations) + (SELECT count(*) FROM archive_media_merge_operations) + (SELECT count(*) FROM archive_import_candidates) + (SELECT count(*) FROM archive_media_import_receipts) + (SELECT count(*) FROM archive_media_merge_receipts)";
        Assert.Equal(0L, command.ExecuteScalar());
    }
}
