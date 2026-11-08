using System.IO.Compression;
using System.Text;
using MeowChan.Archive;
using MeowChan.Interfaces;
using MeowChan.Persistence;
using MeowChan.Storage;
using MeowChan.Transfer;

namespace MeowChan.Tests.Transfer;

// Positive offline lane: real admitted import, owner restart, inventory export,
// package inspection, then the actual exported stream admitted into another profile.
public sealed class NativeV2InventoryReexportTests
{
    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, false, true)]
    [InlineData(true, true, true)]
    [InlineData(false, true, false)]
    public async Task RestartReexportAndSecondImportPreserveExactInventory(
        bool originalContained, bool retainedOnly, bool selectionOverload)
    {
        NativeTransferCatalog input = NativeV2ImportFixture.Content(originalContained, derived: true,
            retainedOnly: retainedOnly);
        TransferThread expectedThread = Assert.Single(input.Threads);
        TransferAttachment expected = retainedOnly ? Assert.Single(expectedThread.RetainedBindings).Attachment
            : Assert.Single(expectedThread.Posts[0].Attachments);
        byte[][] bytes = originalContained
            ? [NativeV2ImportFixture.Original, NativeV2ImportFixture.Preview]
            : [NativeV2ImportFixture.Preview];
        Assert.NotEqual(NativeV2ImportFixture.Hash(NativeV2ImportFixture.Original),
            NativeV2ImportFixture.Hash(NativeV2ImportFixture.Preview));
        await using NativeV2ImportFixture source = await NativeV2ImportFixture.CreateAsync(input, bytes);
        using (ProfileAdmissionToken admission = await source.ExclusiveAsync())
        {
            var committed = await source.CommitAsync(admission);
            Assert.Equal(ProfileExclusiveDisposition.SafeToResume, committed.Disposition);
            Assert.Equal(NativeMediaImportCommitStatus.Committed, committed.Result.Status);
        }
        await source.CloseOwnerAsync();

        using ProfileLease reopenedLease = ProfileLease.Acquire(source.Files.Profile);
        await using ArchiveCatalog reopened = await ArchiveCatalog.OpenAsync(source.Files.Profile, reopenedLease);
        await reopened.EnsureNativeMediaRecoveryClearAsync();
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(source.Files.Profile);
        ArchivedThreadView view = Assert.IsType<ArchivedThreadView>(await reader.ReadThreadAsync(NativeV2ImportFixture.Id));
        MediaAssetSnapshot inventory = await MediaAssetReader.OpenAsync(Assert.Single(view.MediaDirectories));
        Assert.Equal(6, inventory.ManifestVersion);
        MediaOriginalEvidence evidence = Assert.Single(inventory.OriginalEvidence);
        Assert.Null(evidence.OriginalByteVersion!.Sha1);
        Assert.Single(inventory.DerivedRepresentations);
        Guid packageId = Guid.NewGuid();
        NativeTransferArchiveMediaOptions options = new(true, true, true);
        TransferArchiveFreezeResult frozen = selectionOverload
            ? await NativeTransferArchiveExporter.FreezeAsync(reader,
                new NativeTransferArchiveSelection([NativeV2ImportFixture.Id]), NativeV2ImportFixture.At.AddDays(1), packageId, options)
            : await NativeTransferArchiveExporter.FreezeAsync(reader, NativeV2ImportFixture.Id,
                NativeV2ImportFixture.At.AddDays(1), packageId, options);
        Assert.Equal(TransferArchiveError.None, frozen.Error);
        NativeTransferArchiveSnapshot snapshot = Assert.IsType<NativeTransferArchiveSnapshot>(frozen.Snapshot);
        using MemoryStream package = new();
        TransferArchiveWriteResult written = await NativeTransferArchiveExporter.WriteAsync(snapshot, package);
        Assert.True(written.IsComplete, written.ToString());
        package.Position = 0;
        TransferPackageInspection inspection = await NativeTransferPackageValidator.InspectAsync(package);
        Assert.True(inspection.IsPackageValid, inspection.ToString());
        NativeTransferPackage portable = Assert.IsType<NativeTransferPackage>(inspection.Package);
        Assert.True(portable.IsChanV4);
        Assert.Null(portable.PrivateMetadata);
        AssertPortableThread(portable.Catalog, expected, retainedOnly, originalContained);
        string catalogText = Encoding.UTF8.GetString(NativeTransferCatalogSerializer.Serialize(portable.Catalog));
        foreach (string secret in new[] { evidence.Origin.OperationId, evidence.Origin.PackageFingerprint,
                     source.Files.Root, Assert.Single(view.MediaDirectories) })
            Assert.DoesNotContain(secret, catalogText);
        Assert.DoesNotContain("plannedPath", catalogText, StringComparison.OrdinalIgnoreCase);
        package.Position = 0;
        using (ZipArchive zip = new(package, ZipArchiveMode.Read, leaveOpen: true))
        {
            Assert.Equal(bytes.Length, zip.Entries.Count(e => e.FullName.StartsWith("payload/", StringComparison.Ordinal)));
            foreach (byte[] expectedBytes in bytes)
            {
                ZipArchiveEntry entry = Assert.IsType<ZipArchiveEntry>(zip.GetEntry("payload/" + NativeV2ImportFixture.Hash(expectedBytes)));
                using Stream stream = entry.Open();
                using MemoryStream actual = new();
                await stream.CopyToAsync(actual);
                Assert.Equal(expectedBytes, actual.ToArray());
            }
            if (!originalContained)
                Assert.Null(zip.GetEntry("payload/" + NativeV2ImportFixture.Hash(NativeV2ImportFixture.Original)));
        }

        using NativeV2ImportFixture.Workspace target = new();
        using ProfileLease targetLease = ProfileLease.Acquire(target.Profile);
        await using ArchiveCatalog targetCatalog = await ArchiveCatalog.OpenAsync(target.Profile, targetLease);
        await targetCatalog.EnsureNativeMediaRecoveryClearAsync();
        package.Position = 0;
        NativeTransferStageResult staged = await NativeTransferImportStage.CreateAsync(package, target.StageRoot);
        Assert.Equal(NativeTransferStageError.None, staged.Error);
        await using NativeTransferImportStage stage = Assert.IsType<NativeTransferImportStage>(staged.Stage);
        NativeMediaImportPreparation preparation = await targetCatalog.PrepareNativeMediaImportAsync(stage);
        Assert.Equal(NativeMediaImportError.None, preparation.Error);
        NativeMediaImportPlan plan = Assert.IsType<NativeMediaImportPlan>(preparation.Plan);
        Assert.Equal(2, plan.PlanVersion);
        targetLease.Admission.CompleteStartup();
        using (ProfileAdmissionToken admission = await targetLease.Admission.EnterExclusiveAsync())
        {
            var result = await targetCatalog.CommitNativeMediaImportAdmittedAsync(admission, plan, new MediaStore());
            Assert.Equal(ProfileExclusiveDisposition.SafeToResume, result.Disposition);
            Assert.Equal(NativeMediaImportCommitStatus.Committed, result.Result.Status);
        }
        await using ArchiveReader targetReader = await ArchiveReader.OpenAsync(target.Profile);
        ArchivedThreadView copied = Assert.IsType<ArchivedThreadView>(await targetReader.ReadThreadAsync(NativeV2ImportFixture.Id));
        Assert.Equal(retainedOnly ? 0 : 3, copied.PostCount);
        MediaAssetSnapshot copiedInventory = await MediaAssetReader.OpenAsync(Assert.Single(copied.MediaDirectories));
        Assert.Equal(MediaManifestStatus.Available, copiedInventory.Status);
        Assert.Equal(6, copiedInventory.ManifestVersion);
        MediaOriginalEvidence copiedEvidence = Assert.Single(copiedInventory.OriginalEvidence);
        Assert.Equal(evidence.Occurrence, copiedEvidence.Occurrence);
        Assert.Equal(evidence.OriginalByteVersion, copiedEvidence.OriginalByteVersion);
        Assert.Null(copiedEvidence.OriginalByteVersion!.Sha1);
        Assert.NotEqual(evidence.Origin.OperationId, copiedEvidence.Origin.OperationId);
        MediaDerivedRepresentation preview = Assert.Single(copiedInventory.DerivedRepresentations);
        Assert.Equal(copiedEvidence.Occurrence, preview.Parent);
        Assert.Equal(copiedEvidence.OriginalByteVersion, preview.SourceVersion);
        await using (Stream previewStream = await copiedInventory.OpenVerifiedDerivedReadAsync(preview))
        {
            using MemoryStream actual = new();
            await previewStream.CopyToAsync(actual);
            Assert.Equal(NativeV2ImportFixture.Preview, actual.ToArray());
        }
        Assert.Equal(originalContained ? 1 : 0, copiedInventory.AttachmentBindings.Count);
        Assert.Equal(originalContained ? 1 : 0, copiedInventory.Count);
        if (originalContained)
        {
            ResolvedMediaAttachment resolved = Assert.IsType<ResolvedMediaAttachment>(MediaAttachmentResolver.Resolve(
                [copiedInventory], NativeV2ImportFixture.Id, retainedOnly ? "999" : "15", expected.Metadata));
            await using Stream originalStream = await copiedInventory.OpenVerifiedReadAsync(resolved.Asset.AssetId);
            using MemoryStream actual = new();
            await originalStream.CopyToAsync(actual);
            Assert.Equal(NativeV2ImportFixture.Original, actual.ToArray());
        }
        // Reproject the second durable store too: no retained surrogate becomes a post.
        TransferArchiveFreezeResult copiedFreeze = await NativeTransferArchiveExporter.FreezeAsync(targetReader,
            NativeV2ImportFixture.Id, NativeV2ImportFixture.At.AddDays(2), Guid.NewGuid(), options);
        Assert.Equal(TransferArchiveError.None, copiedFreeze.Error);
        AssertPortableThread(Assert.IsType<NativeTransferArchiveSnapshot>(copiedFreeze.Snapshot).Catalog,
            expected, retainedOnly, originalContained);
    }

    private static void AssertPortableThread(NativeTransferCatalog catalog, TransferAttachment expected,
        bool retainedOnly, bool originalContained)
    {
        TransferThread thread = Assert.Single(catalog.Threads);
        Assert.Equal(NativeV2ImportFixture.Id, thread.Id);
        Assert.Equal(ThreadSourceState.NotFound, thread.SourceState);
        TransferAttachment attachment;
        if (retainedOnly)
        {
            Assert.Empty(thread.Posts);
            TransferRetainedBinding retained = Assert.Single(thread.RetainedBindings);
            Assert.Equal("999", retained.PostId);
            attachment = retained.Attachment;
        }
        else
        {
            Assert.Empty(thread.RetainedBindings);
            Assert.Equal(new[] { "15", "16", "17" }, thread.Posts.Select(p => p.Metadata.PostId));
            TransferPost nullPost = thread.Posts[1], emptyPost = thread.Posts[2];
            Assert.True(nullPost.AttachmentsWereNull);
            Assert.Null(nullPost.Metadata.Attachments);
            Assert.Equal(PostFieldPresence.Attachments, nullPost.Metadata.Fields);
            Assert.Empty(nullPost.Attachments);
            Assert.False(emptyPost.AttachmentsWereNull);
            Assert.NotNull(emptyPost.Metadata.Attachments);
            Assert.Empty(emptyPost.Metadata.Attachments!);
            Assert.Empty(emptyPost.Attachments);
            Assert.Equal(PostFieldPresence.All, emptyPost.Metadata.Fields);
            attachment = Assert.Single(thread.Posts[0].Attachments);
        }
        Assert.Equal(expected.Metadata, attachment.Metadata);
        Assert.Null(attachment.Metadata.PlannedPath);
        Assert.Equal(TransferMediaRole.Original, attachment.Role);
        Assert.Equal(originalContained ? TransferContentStatus.Contained : TransferContentStatus.Omitted, attachment.Status);
        Assert.Equal(expected.PayloadSha256, attachment.PayloadSha256);
        if (!originalContained) Assert.Null(attachment.History);
        Assert.Equal(expected.OriginalByteVersion, attachment.OriginalByteVersion);
        Assert.Null(attachment.OriginalByteVersion!.Sha1);
        Assert.Equal(Assert.Single(expected.DerivedRepresentations), Assert.Single(attachment.DerivedRepresentations));
    }
}
