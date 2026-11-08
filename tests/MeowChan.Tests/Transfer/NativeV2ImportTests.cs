using MeowChan.Archive;
using MeowChan.Persistence;
using MeowChan.Storage;
using MeowChan.Transfer;

namespace MeowChan.Tests.Transfer;

// W32b3 positive lane only. Forged plans, races, crashes and unfinished-recovery
// startup cases belong to the other transfer test slices.
public sealed class NativeV2ImportTests
{
    [Fact]
    public async Task ChanV4ImportPublishesRootAndPreservesDerivedRetainedAndNullPresence()
    {
        NativeTransferCatalog content = NativeV2ImportFixture.Content();
        TransferThread mixed = content.Threads[0] with
        {
            RetainedBindings = [new("999", NativeV2ImportFixture.Attachment("999",
                TransferContentStatus.Omitted, derived: false))]
        };
        await using NativeV2ImportFixture f = await NativeV2ImportFixture.CreateAsync(content with { Threads = [mixed] });
        Guid operation = f.Plan.OperationId;
        string request = f.Plan.RequestSha256;
        using ProfileAdmissionToken admission = await f.ExclusiveAsync();

        ProfileExclusiveOutcome<NativeMediaImportCommitResult> outcome = await f.CommitAsync(admission);
        Assert.Equal(ProfileExclusiveDisposition.SafeToResume, outcome.Disposition);
        NativeMediaImportCommitResult result = outcome.Result;
        Assert.Equal(NativeMediaImportCommitStatus.Committed, result.Status);
        Assert.False(result.IsReplay);
        Assert.Equal(1, result.NewThreads);
        Assert.Equal(3, result.NewPosts);
        Assert.Equal(1, result.NewAttachments);
        Assert.Equal(2, result.EvidenceAdditions); // live original + retained historical evidence
        Assert.Equal(1, result.DerivedDescriptorAdditions);
        Assert.Equal(1, result.ContainedDerivedAdditions);
        Assert.Equal(1, result.OriginalBindingCount);

        await f.Stage.DisposeAsync();
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(f.Files.Profile);
        ArchivedThreadView view = Assert.IsType<ArchivedThreadView>(await reader.ReadThreadAsync(NativeV2ImportFixture.Id));
        Assert.Equal(3, view.PostCount);
        Assert.DoesNotContain(view.GetPosts(), post => post.PostId == "999");
        Assert.Equal(PostFieldPresence.All, Assert.Single(view.GetPosts(), post => post.PostId == "15").Fields);
        CapturedPost nullPost = Assert.Single(view.GetPosts(), post => post.PostId == "16");
        Assert.Equal(PostFieldPresence.Attachments, nullPost.Fields);
        Assert.Null(nullPost.Attachments);
        CapturedPost emptyPost = Assert.Single(view.GetPosts(), post => post.PostId == "17");
        Assert.Equal(PostFieldPresence.All, emptyPost.Fields);
        Assert.NotNull(emptyPost.Attachments);
        Assert.Empty(emptyPost.Attachments!);
        string root = Assert.Single(view.MediaDirectories);
        Assert.Equal(root, view.Summary.MediaDirectory);
        MediaAssetSnapshot snapshot = await MediaAssetReader.OpenAsync(root);
        Assert.Equal(MediaManifestStatus.Available, snapshot.Status);
        Assert.Equal(6, snapshot.ManifestVersion);
        Assert.Equal(2, snapshot.OriginalEvidence.Length);
        Assert.Single(snapshot.DerivedRepresentations);
        Assert.Single(snapshot.AttachmentBindings);
        Assert.Contains(snapshot.OriginalEvidence, evidence => evidence.Occurrence.PostId == "999");
        Assert.Equal(NativeMediaImportCommitStatus.Committed,
            (await f.Catalog.ReadNativeMediaImportReceiptAsync(operation, request, 2))!.Status);
    }

    [Fact]
    public async Task EvidenceOnlyZeroOriginalStillCreatesTheThreadRootAndContentFreeReceipt()
    {
        await using NativeV2ImportFixture f = await NativeV2ImportFixture.CreateAsync(
            NativeV2ImportFixture.Content(originalContained: false, derived: true, presence: false),
            [NativeV2ImportFixture.Preview]);
        using ProfileAdmissionToken admission = await f.ExclusiveAsync();

        ProfileExclusiveOutcome<NativeMediaImportCommitResult> outcome = await f.CommitAsync(admission);
        Assert.Equal(ProfileExclusiveDisposition.SafeToResume, outcome.Disposition);
        Assert.Equal(NativeMediaImportCommitStatus.Committed, outcome.Result.Status);
        Assert.Equal(1, outcome.Result.NewThreads);
        Assert.Equal(1, outcome.Result.NewPosts);
        Assert.Equal(1, outcome.Result.NewAttachments); // actual SQL metadata attachment, not an original binding
        Assert.Equal(1, outcome.Result.EvidenceAdditions);
        Assert.Equal(1, outcome.Result.DerivedDescriptorAdditions);
        Assert.Equal(1, outcome.Result.ContainedDerivedAdditions);
        Assert.Equal(0, outcome.Result.OriginalBindingCount);

        await using ArchiveReader reader = await ArchiveReader.OpenAsync(f.Files.Profile);
        ArchivedThreadView view = Assert.IsType<ArchivedThreadView>(await reader.ReadThreadAsync(NativeV2ImportFixture.Id));
        Assert.Equal(1, view.PostCount);
        CapturedPost omittedPost = view.GetPosts()[0];
        Assert.Equal(PostFieldPresence.All, omittedPost.Fields);
        Assert.NotNull(omittedPost.Attachments);
        Assert.Single(omittedPost.Attachments!); // metadata attachment, despite zero original bindings
        Assert.Equal("15", omittedPost.PostId);
        MediaAssetSnapshot snapshot = await MediaAssetReader.OpenAsync(Assert.Single(view.MediaDirectories));
        Assert.Empty(snapshot.AttachmentBindings); // not zero metadata attachments
        Assert.Single(snapshot.OriginalEvidence);
        Assert.Equal(MediaObservedContentStatus.Omitted, snapshot.OriginalEvidence[0].IncomingStatus);
        Assert.Single(snapshot.DerivedRepresentations);
        Assert.Equal(0, snapshot.Count);
    }

    [Fact]
    public async Task RetainedOnlyImportCreatesRootWithoutManufacturingPostOrOriginalBinding()
    {
        await using NativeV2ImportFixture f = await NativeV2ImportFixture.CreateAsync(
            NativeV2ImportFixture.Content(originalContained: false, derived: false, retainedOnly: true,
                presence: false), payloads: []);
        using ProfileAdmissionToken admission = await f.ExclusiveAsync();

        ProfileExclusiveOutcome<NativeMediaImportCommitResult> outcome = await f.CommitAsync(admission);
        Assert.Equal(ProfileExclusiveDisposition.SafeToResume, outcome.Disposition);
        NativeMediaImportCommitResult result = outcome.Result;
        Assert.Equal(NativeMediaImportCommitStatus.Committed, result.Status);
        Assert.Equal(1, result.NewThreads);
        Assert.Equal(0, result.NewPosts);
        Assert.Equal(0, result.NewAttachments);
        Assert.Equal(1, result.EvidenceAdditions);
        Assert.Equal(0, result.OriginalBindingCount);
        Assert.Equal(0, result.DerivedDescriptorAdditions);
        Assert.Equal(0, result.ContainedDerivedAdditions);

        await using ArchiveReader reader = await ArchiveReader.OpenAsync(f.Files.Profile);
        ArchivedThreadView view = Assert.IsType<ArchivedThreadView>(await reader.ReadThreadAsync(NativeV2ImportFixture.Id));
        Assert.Equal(0, view.PostCount);
        MediaAssetSnapshot snapshot = await MediaAssetReader.OpenAsync(Assert.Single(view.MediaDirectories));
        Assert.Single(snapshot.OriginalEvidence);
        Assert.Empty(snapshot.AttachmentBindings);
        Assert.Equal("999", snapshot.OriginalEvidence[0].Occurrence.PostId);
        Assert.Equal(MediaObservedContentStatus.Omitted, snapshot.OriginalEvidence[0].IncomingStatus);
        Assert.Empty(snapshot.DerivedRepresentations);
        Assert.Equal(0, snapshot.Count);
        Assert.Equal(result with { IsReplay = true },
            await f.Catalog.ReadNativeMediaImportReceiptAsync(f.Plan.OperationId, f.Plan.RequestSha256, 2));
    }

    [Fact]
    public async Task CommitReplayIsReceiptFirstAndDoesNotNeedStageOrFilesystem()
    {
        await using NativeV2ImportFixture f = await NativeV2ImportFixture.CreateAsync();
        Guid operation = f.Plan.OperationId;
        string request = f.Plan.RequestSha256;
        using (ProfileAdmissionToken firstAdmission = await f.ExclusiveAsync())
            Assert.Equal(NativeMediaImportCommitStatus.Committed, (await f.CommitAsync(firstAdmission)).Result.Status);
        string candidate;
        await using (ArchiveReader reader = await ArchiveReader.OpenAsync(f.Files.Profile))
        {
            ArchivedThreadView view = Assert.IsType<ArchivedThreadView>(await reader.ReadThreadAsync(NativeV2ImportFixture.Id));
            candidate = Assert.Single(view.MediaDirectories);
        }
        Assert.Equal(Path.GetFullPath(f.Plan.OperationsParent), Path.GetDirectoryName(candidate));
        Assert.True(File.Exists(Path.Combine(candidate, ".meowchan-manifest.json")));
        await f.Stage.DisposeAsync();
        // Move only this fixture's committed candidate, outside the registered root.
        // This deliberately removes both candidate and manifest without product cleanup.
        string movedCandidate = Path.Combine(f.Files.Root, "test-owned-moved-candidate");
        Directory.Move(candidate, movedCandidate);
        Assert.False(Directory.Exists(candidate));
        Assert.False(File.Exists(Path.Combine(candidate, ".meowchan-manifest.json")));
        Dictionary<string, string> before = NativeV2ImportFixture.PayloadFiles(f.Files.Root);
        using ProfileAdmissionToken replayAdmission = await f.ExclusiveAsync();
        ProfileExclusiveOutcome<NativeMediaImportCommitResult> replay = await f.CommitAsync(replayAdmission,
            new CancellationToken(true), _ => throw new InvalidOperationException("replay touched filesystem"));
        Assert.Equal(ProfileExclusiveDisposition.SafeToResume, replay.Disposition);
        Assert.Equal(NativeMediaImportCommitStatus.Committed, replay.Result.Status);
        Assert.True(replay.Result.IsReplay);
        Assert.Equal(3, replay.Result.NewPosts);
        Assert.Equal(1, replay.Result.NewAttachments);
        Assert.False(Directory.Exists(candidate)); // replay must not recreate a candidate
        Assert.True(Directory.Exists(movedCandidate));
        Assert.Equal(before, NativeV2ImportFixture.PayloadFiles(f.Files.Root));
        NativeMediaImportCommitResult receipt = Assert.IsType<NativeMediaImportCommitResult>(
            await f.Catalog.ReadNativeMediaImportReceiptAsync(operation, request, 2));
        Assert.True(receipt.IsReplay);
        Assert.Equal(replay.Result, receipt);
    }

    [Fact]
    public async Task CommittedV2ImportSurvivesTwoOfflineCatalogRestartsWithExplicitReceiptVersion()
    {
        await using NativeV2ImportFixture f = await NativeV2ImportFixture.CreateAsync();
        Guid operation = f.Plan.OperationId;
        string request = f.Plan.RequestSha256;
        using (ProfileAdmissionToken admission = await f.ExclusiveAsync())
            Assert.Equal(NativeMediaImportCommitStatus.Committed, (await f.CommitAsync(admission)).Result.Status);
        await f.CloseOwnerAsync();

        for (int restart = 0; restart < 2; restart++)
        {
            using ProfileLease lease = ProfileLease.Acquire(f.Files.Profile);
            await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(f.Files.Profile, lease);
            await catalog.EnsureNativeMediaRecoveryClearAsync();
            NativeMediaImportCommitResult receipt = Assert.IsType<NativeMediaImportCommitResult>(
                await catalog.ReadNativeMediaImportReceiptAsync(operation, request, 2));
            Assert.Equal(NativeMediaImportCommitStatus.Committed, receipt.Status);
            Assert.True(receipt.IsReplay);
            Assert.Equal(1, receipt.NewThreads);
            Assert.Equal(3, receipt.NewPosts);
            Assert.Equal(1, receipt.NewAttachments);
            await using ArchiveReader reader = await ArchiveReader.OpenAsync(f.Files.Profile);
            Assert.Equal(3, Assert.IsType<ArchivedThreadView>(await reader.ReadThreadAsync(NativeV2ImportFixture.Id)).PostCount);
        }
    }
}
