using System.IO.Compression;
using MeowChan.Archive;
using MeowChan.Interfaces;
using MeowChan.Persistence;
using MeowChan.Storage;
using MeowChan.Transfer;
using Microsoft.Data.Sqlite;
using static MeowChan.Tests.Transfer.NativeV2ImportFixture;

namespace MeowChan.Tests.Transfer;

// Real writer -> validated stage -> admitted import -> owner-issued merge, not a
// fabricated inventory/plan. No original payload exists anywhere in this lane.
// These are storage-owner regressions, not Registry-startup or release acceptance.
public sealed class NativeV2MergeContextOnlyStatusTests
{
    [Theory]
    [InlineData((int)TransferContentStatus.Unknown, (int)MediaObservedContentStatus.Unknown)]
    [InlineData((int)TransferContentStatus.Failed, (int)MediaObservedContentStatus.Failed)]
    [InlineData((int)TransferContentStatus.Cancelled, (int)MediaObservedContentStatus.Cancelled)]
    public async Task PreviewAndIndependentPostSupplementPriorStatusWithoutOriginalPromotion(
        int status, int observedStatus)
    {
        TransferAttachment priorAttachment = Attachment(status: (TransferContentStatus)status, derived: false);
        TransferAttachment incomingAttachment = Attachment(status: TransferContentStatus.Unavailable);
        TransferOriginalByteVersion version = Assert.IsType<TransferOriginalByteVersion>(priorAttachment.OriginalByteVersion);
        Assert.Null(version.Sha1);
        Assert.Equal(priorAttachment.Metadata, incomingAttachment.Metadata);
        Assert.Equal(version, incomingAttachment.OriginalByteVersion);
        Assert.Null(priorAttachment.PayloadSha256);
        Assert.Empty(priorAttachment.DerivedRepresentations);
        Assert.Null(incomingAttachment.PayloadSha256);
        Assert.NotEqual(Hash(Original), Hash(Preview));
        NativeTransferCatalog basis = Content(originalContained: false, derived: false, presence: false);
        NativeTransferCatalog seed = basis with { Threads = [basis.Threads[0] with
        {
            Posts = [Post("15", priorAttachment)]
        }] };
        // Explicit [] is important: fixture defaults would otherwise supply original bytes.
        await using NativeV2ImportFixture f = await CreateAsync(seed, []);
        Assert.Empty(f.Stage.Package.Envelope.MeasuredPayloads);
        using ProfileAdmissionToken admission = await f.ExclusiveAsync();
        var imported = await f.CommitAsync(admission);
        Assert.Equal(ProfileExclusiveDisposition.SafeToResume, imported.Disposition);
        Assert.Equal(NativeMediaImportCommitStatus.Committed, imported.Result.Status);
        Assert.False(imported.Result.IsReplay);
        f.Catalog.EnsureTransferAdmission(admission);

        await using ArchiveReader reader = await ArchiveReader.OpenAsync(f.Files.Profile);
        ArchivedThreadView before = await ReadTargetAsync(reader);
        Assert.Equal(ThreadSourceState.NotFound, before.Summary.SourceState);
        Assert.Equal("15", Assert.Single(before.Posts).PostId);
        string priorRoot = Assert.Single(before.MediaDirectories);
        MediaAssetSnapshot prior = await MediaAssetReader.OpenAsync(priorRoot);
        Assert.Equal(MediaManifestStatus.Available, prior.Status);
        Assert.Equal(6, prior.ManifestVersion);
        Assert.Equal(0, prior.Count);
        Assert.Empty(prior.AttachmentBindings);
        Assert.Empty(prior.DerivedRepresentations);
        MediaOriginalEvidence priorEvidence = Assert.Single(prior.OriginalEvidence);
        Assert.Equal((MediaObservedContentStatus)observedStatus, priorEvidence.IncomingStatus);
        Assert.Equal(priorAttachment.Metadata, priorEvidence.Metadata);
        AssertSourceVersion(version, priorEvidence.OriginalByteVersion);
        Dictionary<string, string> priorFiles = NativeMediaImportTests.Files(priorRoot);
        Assert.NotEmpty(priorFiles); // Includes manifest/journal, not a vacuous empty .bin comparison.
        Assert.Empty(PayloadFiles(priorRoot));

        NativeTransferCatalog incoming = basis with { Threads = [basis.Threads[0] with
        {
            Posts = [Post("15", incomingAttachment), Post("18")]
        }] };
        await using NativeTransferImportStage stage = await StageAsync(f.Files, incoming, Preview);
        Assert.Equal(Hash(Preview), Assert.Single(stage.Package.Envelope.MeasuredPayloads).Key);
        Assert.False(stage.Package.Envelope.MeasuredPayloads.ContainsKey(Hash(Original)));
        Assert.NotNull(stage.Package.Envelope.MeasuredPayloads[Hash(Preview)].Sha1);
        NativeMediaMergePlan plan = await PrepareAsync(f, stage);
        NativeMediaMergeV2State state = Assert.IsType<NativeMediaMergeV2State>(plan.Representation);
        Assert.Equal(new NativeMediaMergeCounts(1, 0, 0, 0, 0, 0, 1, 1), state.Counts);
        Assert.Equal("18", Assert.Single(plan.AddedPosts).PostId);
        Assert.Empty(plan.UpdatedPosts);
        Assert.Empty(plan.AddedAttachments);
        Assert.Equal(0, plan.NewBindingCount);
        Assert.Equal(0, plan.Conflicts);
        Assert.True(plan.HasSafeCandidate); // Semantic Safe, despite zero original bindings.
        Assert.False(plan.ReuseFullAsSafe); // Full parent is evidence; Safe parent is context.
        Assert.All(state.FullSelection.Members, member => Assert.False(member.ContextOnly));
        Assert.Equal(2, state.SafeSelection.Members.Length);
        NativeMediaSelectedMember parent = Assert.Single(state.SafeSelection.Members,
            member => member.Id.Role == NativeMediaMemberRole.OriginalEvidence);
        Assert.True(parent.ContextOnly);
        Assert.Equal(priorEvidence.Occurrence, parent.Id.Occurrence);
        Assert.Equal(version, parent.Id.SourceVersion);
        Assert.Equal(TransferContentStatus.Unavailable, parent.Descriptor.Status);
        NativeMediaSelectedMember selectedPreview = Assert.Single(state.SafeSelection.Members,
            member => member.Id.Role == NativeMediaMemberRole.Preview);
        Assert.False(selectedPreview.ContextOnly);
        Assert.Equal(parent.Id.Occurrence, selectedPreview.Id.Occurrence);
        Assert.Equal(version, selectedPreview.Id.SourceVersion);
        Assert.DoesNotContain(state.SafeSelection.Members, member => member.Id.Role == NativeMediaMemberRole.OriginalPayload);
        NativeMediaCandidateInventory safeInventory = Assert.IsType<NativeMediaCandidateInventory>(state.SafeInventory);
        Assert.Empty(safeInventory.Bindings);
        Assert.Single(safeInventory.OriginalEvidence);
        Assert.Single(safeInventory.DerivedRepresentations);

        var merged = await f.Catalog.CommitNativeMediaMergeAdmittedAsync(admission, plan, f.Store);
        Assert.Equal(ProfileExclusiveDisposition.SafeToResume, merged.Disposition);
        Assert.Equal(new NativeMediaMergeResult(NativeMediaImportCommitStatus.Committed, NewPosts: 1,
            DerivedDescriptorAdditions: 1, ContainedDerivedAdditions: 1), merged.Result);
        f.Catalog.EnsureTransferAdmission(admission);
        NativeMediaPersistedMergeOperation operation = ReadCommittedOperation(f, plan);
        string safeRoot = Assert.IsType<string>(operation.Safe.Directory);
        Assert.NotEqual(operation.Full.Directory, safeRoot);
        Assert.Equal(state.SafeSelection.CanonicalJson, operation.Safe.Selection!.CanonicalJson);
        Assert.Equal(safeInventory.Sha256, operation.Safe.InventorySha256);
        Assert.True(Assert.Single(operation.Safe.Selection.Members,
            member => member.Id.Role == NativeMediaMemberRole.OriginalEvidence).ContextOnly);
        ArchivedThreadView after = await ReadTargetAsync(reader);
        Assert.Equal(before.Summary with { PostCount = 2, Revision = before.Summary.Revision + 1 }, after.Summary);
        Assert.Equal(before.DecimalPostOrder, after.DecimalPostOrder);
        Assert.Equal(2, after.PostCount);
        AssertPostEqual(before.Posts[0], Assert.Single(after.Posts, post => post.PostId == "15"));
        AssertPostEqual(Post("18").Metadata, Assert.Single(after.Posts, post => post.PostId == "18"));
        Assert.Equal(2, after.MediaDirectories.Count);
        Assert.Contains(priorRoot, after.MediaDirectories);
        Assert.Contains(safeRoot, after.MediaDirectories);
        Assert.DoesNotContain(operation.Full.Directory!, after.MediaDirectories);
        Assert.Empty(Directory.GetFiles(plan.OperationsParent, "*.original.bin", SearchOption.AllDirectories));
        await AssertPriorRootUnchangedAsync(prior, priorEvidence, priorFiles);

        MediaAssetSnapshot safe = await MediaAssetReader.OpenAsync(safeRoot);
        Assert.Equal(MediaManifestStatus.Available, safe.Status);
        Assert.Equal(6, safe.ManifestVersion);
        Assert.Equal(operation.Safe.ManifestGeneration, safe.Generation);
        Assert.Equal(0, safe.Count);
        Assert.Empty(safe.AttachmentBindings);
        Assert.Equal(safeInventory.OriginalEvidence.ToArray(), safe.OriginalEvidence.ToArray());
        Assert.Equal(safeInventory.DerivedRepresentations.ToArray(), safe.DerivedRepresentations.ToArray());
        MediaOriginalEvidence context = Assert.Single(safe.OriginalEvidence);
        Assert.Equal(priorEvidence.Occurrence, context.Occurrence);
        Assert.Equal(priorEvidence.Metadata, context.Metadata);
        Assert.Equal(MediaObservedContentStatus.Unavailable, context.IncomingStatus);
        AssertSourceVersion(version, context.OriginalByteVersion);
        MediaDerivedRepresentation preview = Assert.Single(safe.DerivedRepresentations);
        Assert.Equal(context.Occurrence, preview.Parent);
        Assert.Equal(MediaDerivedKind.Preview, preview.Kind);
        Assert.Equal(MediaDerivedStatus.Contained, preview.IncomingStatus);
        AssertSourceVersion(version, preview.SourceVersion);
        Assert.Equal(Hash(Preview), preview.Blob!.Actual.Sha256);
        Assert.NotNull(preview.Blob.Actual.Sha1); // Measured preview SHA1 must not fill source SHA1.
        Assert.Null(MediaAttachmentResolver.Resolve([prior, safe], Id, "15", incomingAttachment.Metadata));
        await using (Stream bytes = await safe.OpenVerifiedDerivedReadAsync(preview))
            await AssertPreviewBytesAsync(bytes);
        Assert.Equal(Hash(Preview) + ".derived.bin", Assert.Single(PayloadFiles(safeRoot)).Key);
        Dictionary<string, Dictionary<string, string>> activeFiles = after.MediaDirectories.ToDictionary(
            root => root, NativeMediaImportTests.Files, StringComparer.Ordinal);

        // A genuinely new writer/stage/owner-issued plan, not receipt replay. A durable
        // inactive Full is permitted; no second active root, preview or content revision is.
        await stage.DisposeAsync();
        await using NativeTransferImportStage duplicateStage = await StageAsync(f.Files, incoming, Preview);
        NativeMediaMergePlan duplicate = await PrepareAsync(f, duplicateStage);
        Assert.NotEqual(plan.OperationId, duplicate.OperationId);
        Assert.Equal(new NativeMediaMergeCounts(0, 0, 0, 0, 0, 0, 0, 0), duplicate.Representation!.Counts);
        Assert.False(duplicate.HasSafeCandidate);
        Assert.True(duplicate.Representation.SafeSelection.IsEmpty);
        Assert.Null(duplicate.Representation.SafeInventory);
        Assert.Empty(duplicate.AddedPosts);
        Assert.Empty(duplicate.UpdatedPosts);
        Assert.Empty(duplicate.AddedAttachments);
        var repeated = await f.Catalog.CommitNativeMediaMergeAdmittedAsync(admission, duplicate, f.Store);
        Assert.Equal(ProfileExclusiveDisposition.SafeToResume, repeated.Disposition);
        Assert.Equal(new NativeMediaMergeResult(NativeMediaImportCommitStatus.Committed), repeated.Result);
        NativeMediaPersistedMergeOperation repeatedOperation = ReadCommittedOperation(f, duplicate);
        Assert.Null(repeatedOperation.Safe.Directory);
        Assert.Null(repeatedOperation.Safe.Selection);
        ArchivedThreadView unchanged = await ReadTargetAsync(reader);
        Assert.Equal(after.Summary, unchanged.Summary);
        Assert.Equal(after.MediaDirectories.ToArray(), unchanged.MediaDirectories.ToArray());
        Assert.Equal(after.Posts.Select(post => post.PostId), unchanged.Posts.Select(post => post.PostId));
        foreach (CapturedPost post in after.Posts)
            AssertPostEqual(post, Assert.Single(unchanged.Posts, current => current.PostId == post.PostId));
        Assert.DoesNotContain(repeatedOperation.Full.Directory!, unchanged.MediaDirectories);
        foreach (var root in activeFiles) Assert.Equal(root.Value, NativeMediaImportTests.Files(root.Key));
        MediaAssetSnapshot[] active = await Task.WhenAll(unchanged.MediaDirectories.Select(root => MediaAssetReader.OpenAsync(root)));
        Assert.All(active, snapshot => { Assert.Equal(0, snapshot.Count); Assert.Empty(snapshot.AttachmentBindings); });
        Assert.Equal(2, active.Sum(snapshot => snapshot.OriginalEvidence.Length));
        Assert.Single(active.SelectMany(snapshot => snapshot.DerivedRepresentations));
        Assert.Null(MediaAttachmentResolver.Resolve(active, Id, "15", incomingAttachment.Metadata));
        await f.Catalog.EnsureNativeMediaRecoveryClearAsync();
        f.Catalog.EnsureTransferAdmission(admission);

        // Include originals deliberately: the exporter must still emit only the preview.
        // Validate the written package and actual ZIP payload, not just a frozen DTO.
        await AssertReexportAsync(reader, incomingAttachment, version);
        await AssertPriorRootUnchangedAsync(prior, priorEvidence, priorFiles);
        foreach (var root in activeFiles) Assert.Equal(root.Value, NativeMediaImportTests.Files(root.Key));
    }

    private static async Task<NativeMediaMergePlan> PrepareAsync(NativeV2ImportFixture f, NativeTransferImportStage stage)
    {
        NativeMediaMergePreparation preparation = await f.Catalog.PrepareNativeMediaMergeAsync(stage);
        Assert.Equal(NativeMediaMergeError.None, preparation.Error);
        NativeMediaMergePlan plan = Assert.IsType<NativeMediaMergePlan>(preparation.Plan);
        Assert.Equal(2, plan.PlanVersion);
        Assert.False(Directory.Exists(plan.OperationsParent));
        return plan;
    }

    private static NativeMediaPersistedMergeOperation ReadCommittedOperation(NativeV2ImportFixture f, NativeMediaMergePlan plan)
    {
        using SqliteConnection connection = ArchiveSql.OpenConnection(f.Catalog.DatabasePath, SqliteOpenMode.ReadOnly);
        using SqliteTransaction transaction = connection.BeginTransaction(deferred: true);
        NativeMediaPersistedMergeOperation operation = Assert.IsType<NativeMediaPersistedMergeOperation>(
            ArchiveSql.ReadPersistedMediaMergeOperation(connection, transaction, plan.OperationId, plan.RequestSha256));
        Assert.True(plan.Representation!.MatchesOperation(operation, NativeMediaImportOperationState.Committed));
        NativeMediaPersistedMergeReceipt receipt = Assert.IsType<NativeMediaPersistedMergeReceipt>(
            ArchiveSql.ReadPersistedMediaMergeReceipt(connection, transaction, plan.OperationId, plan.RequestSha256, 2));
        Assert.Equal(plan.Representation.Counts, receipt.Counts);
        Assert.True(ArchiveSql.PersistedMediaMergeReceiptMatches(operation, receipt));
        return operation;
    }

    private static async Task<ArchivedThreadView> ReadTargetAsync(ArchiveReader reader)
        => Assert.IsType<ArchivedThreadView>(await reader.ReadThreadAsync(Id));

    private static void AssertPostEqual(CapturedPost expected, CapturedPost actual)
    {
        Assert.Equal(expected with { Attachments = null }, actual with { Attachments = null });
        Assert.Equal(expected.Attachments is null, actual.Attachments is null);
        if (expected.Attachments != null) Assert.Equal(expected.Attachments.ToArray(), actual.Attachments!.ToArray());
    }

    private static void AssertSourceVersion(TransferOriginalByteVersion expected, MediaActualIntegrity? actual)
    {
        MediaActualIntegrity value = Assert.IsType<MediaActualIntegrity>(actual);
        Assert.Equal(expected.Length, value.Length);
        Assert.Equal(expected.Sha256, value.Sha256);
        Assert.Equal(expected.Md5, value.Md5);
        Assert.Equal(expected.Sha1, value.Sha1);
        Assert.Null(value.Sha1);
    }

    private static async Task AssertPriorRootUnchangedAsync(MediaAssetSnapshot prior,
        MediaOriginalEvidence evidence, Dictionary<string, string> files)
    {
        Assert.Equal(files, NativeMediaImportTests.Files(prior.Directory));
        MediaAssetSnapshot current = await MediaAssetReader.OpenAsync(prior.Directory);
        Assert.Equal(MediaManifestStatus.Available, current.Status);
        Assert.Equal(prior.Generation, current.Generation);
        Assert.Equal(evidence, Assert.Single(current.OriginalEvidence));
        Assert.Equal(0, current.Count);
        Assert.Empty(current.AttachmentBindings);
        Assert.Empty(current.DerivedRepresentations);
    }

    private static async Task AssertReexportAsync(ArchiveReader reader, TransferAttachment incoming,
        TransferOriginalByteVersion version)
    {
        TransferArchiveFreezeResult frozen = await NativeTransferArchiveExporter.FreezeAsync(reader, Id,
            At.AddDays(3), Guid.NewGuid(), new NativeTransferArchiveMediaOptions(true, true, true));
        Assert.Equal(TransferArchiveError.None, frozen.Error);
        NativeTransferArchiveSnapshot snapshot = Assert.IsType<NativeTransferArchiveSnapshot>(frozen.Snapshot);
        AssertPortable(snapshot.Catalog);
        using MemoryStream output = new();
        TransferArchiveWriteResult written = await NativeTransferArchiveExporter.WriteAsync(snapshot, output);
        Assert.True(written.IsComplete, written.ToString());
        output.Position = 0;
        TransferPackageInspection inspected = await NativeTransferPackageValidator.InspectAsync(output);
        Assert.True(inspected.IsPackageValid, inspected.ToString());
        NativeTransferPackage package = Assert.IsType<NativeTransferPackage>(inspected.Package);
        Assert.True(package.IsChanV4);
        Assert.Null(package.PrivateMetadata);
        AssertPortable(package.Catalog);
        Assert.Equal(Hash(Preview), Assert.Single(package.Envelope.MeasuredPayloads).Key);
        Assert.False(package.Envelope.MeasuredPayloads.ContainsKey(version.Sha256));
        output.Position = 0;
        using ZipArchive zip = new(output, ZipArchiveMode.Read, leaveOpen: true);
        ZipArchiveEntry payload = Assert.Single(zip.Entries,
            entry => entry.FullName.StartsWith("payload/", StringComparison.Ordinal));
        Assert.Equal("payload/" + Hash(Preview), payload.FullName);
        using Stream bytes = payload.Open();
        await AssertPreviewBytesAsync(bytes);

        void AssertPortable(NativeTransferCatalog catalog)
        {
            TransferThread thread = Assert.Single(catalog.Threads);
            Assert.Equal(Id, thread.Id);
            Assert.Equal(ThreadSourceState.NotFound, thread.SourceState);
            Assert.Equal(new[] { "15", "18" }, thread.Posts.Select(post => post.Metadata.PostId).Order(StringComparer.Ordinal));
            Assert.Empty(thread.RetainedBindings);
            TransferPost post = Assert.Single(thread.Posts, item => item.Metadata.PostId == "15");
            TransferAttachment original = Assert.Single(post.Attachments);
            Assert.Equal(incoming.Metadata, original.Metadata);
            Assert.Equal(TransferMediaRole.Original, original.Role);
            Assert.Equal(TransferContentStatus.Unavailable, original.Status);
            Assert.Null(original.PayloadSha256);
            Assert.Null(original.History);
            Assert.Equal(version, original.OriginalByteVersion);
            Assert.Null(original.OriginalByteVersion!.Sha1);
            TransferDerivedRepresentation preview = Assert.Single(original.DerivedRepresentations);
            Assert.Equal(TransferDerivedRepresentationKind.Preview, preview.Kind);
            Assert.Equal(original.Metadata.AttachmentRevisionKey, preview.SourceRevisionKey);
            Assert.Equal(version, preview.SourceByteVersion);
            Assert.Null(preview.SourceByteVersion.Sha1);
            Assert.Equal(TransferContentStatus.Contained, preview.Status);
            Assert.Equal(Hash(Preview), preview.PayloadSha256);
            TransferPost independent = Assert.Single(thread.Posts, item => item.Metadata.PostId == "18");
            Assert.False(independent.AttachmentsWereNull);
            Assert.Empty(independent.Attachments);
        }
    }

    private static async Task AssertPreviewBytesAsync(Stream stream)
    {
        using MemoryStream bytes = new();
        await stream.CopyToAsync(bytes);
        Assert.Equal(Preview, bytes.ToArray());
    }
}
