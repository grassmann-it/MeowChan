using MeowChan.Archive;
using MeowChan.Persistence;
using MeowChan.Storage;
using MeowChan.Transfer;
using static MeowChan.Tests.Transfer.NativeV2ImportFixture;

namespace MeowChan.Tests.Transfer;

public sealed class NativeV2MergeSupplementationTests
{
    [Theory]
    [InlineData((int)TransferContentStatus.Omitted)]
    [InlineData((int)TransferContentStatus.Unavailable)]
    [InlineData((int)TransferContentStatus.Unknown)]
    [InlineData((int)TransferContentStatus.Failed)]
    [InlineData((int)TransferContentStatus.Cancelled)]
    public async Task OriginalSupplementsExactPreviewOnlyOccurrenceWithoutRewritingPriorEvidence(int status)
    {
        NativeTransferCatalog seed = Content(originalContained: false);
        seed = seed with { Threads = [seed.Threads[0] with { Posts = [Post("15", Attachment(status: (TransferContentStatus)status))] }] };
        await using NativeV2ImportFixture f = await CreateAsync(seed, [Preview]);
        using ProfileAdmissionToken admission = await f.ExclusiveAsync();
        Assert.Equal(NativeMediaImportCommitStatus.Committed, (await f.CommitAsync(admission)).Result.Status);
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(f.Files.Profile);
        ArchivedThreadView before = (await reader.ReadThreadAsync(Id))!;
        string priorRoot = Assert.Single(before.MediaDirectories);
        var priorBytes = PayloadFiles(priorRoot);
        MediaAssetSnapshot prior = await MediaAssetReader.OpenAsync(priorRoot);
        Assert.Empty(prior.AttachmentBindings);
        await using NativeTransferImportStage stage = await StageAsync(f.Files, Content(), [Original, Preview]);
        NativeMediaMergePreparation prepared = await f.Catalog.PrepareNativeMediaMergeAsync(stage);
        Assert.Equal(NativeMediaMergeError.None, prepared.Error);
        NativeMediaMergePlan plan = Assert.IsType<NativeMediaMergePlan>(prepared.Plan);
        Assert.Equal(0, plan.Conflicts);
        Assert.Equal(1, plan.NewBindingCount);
        Assert.All(plan.Representation!.SafeSelection.Members.Where(m => m.Id.Role == NativeMediaMemberRole.OriginalEvidence), m => Assert.True(m.ContextOnly));
        var outcome = await f.Catalog.CommitNativeMediaMergeAdmittedAsync(admission, plan, f.Store);
        Assert.Equal(ProfileExclusiveDisposition.SafeToResume, outcome.Disposition);
        Assert.Equal(NativeMediaImportCommitStatus.Committed, outcome.Result.Status);
        Assert.Equal(1, outcome.Result.NewBindings);
        Assert.Equal(0, outcome.Result.EvidenceAdditions);
        Assert.Equal(0, outcome.Result.DerivedDescriptorAdditions);
        Assert.Equal(priorBytes, PayloadFiles(priorRoot));
        Assert.Equal(prior.Generation, (await MediaAssetReader.OpenAsync(priorRoot)).Generation);
        ArchivedThreadView after = (await reader.ReadThreadAsync(Id))!;
        Assert.Equal(before.Summary.SourceState, after.Summary.SourceState);
        MediaAssetSnapshot[] snapshots = await Task.WhenAll(after.MediaDirectories.Select(x => MediaAssetReader.OpenAsync(x)));
        var actual = Assert.IsType<ResolvedMediaAttachment>(MediaAttachmentResolver.Resolve(snapshots, Id, "15", Attachment().Metadata));
        await using Stream original = await actual.Snapshot.OpenVerifiedReadAsync(actual.Asset.AssetId);
        using MemoryStream data = new(); await original.CopyToAsync(data);
        Assert.Equal(Original, data.ToArray());
        Assert.Single(snapshots.SelectMany(s => s.DerivedRepresentations));
        var exported = await NativeTransferArchiveExporter.FreezeAsync(reader, Id, At.AddDays(2), Guid.NewGuid(),
            new NativeTransferArchiveMediaOptions(true, true, true));
        Assert.NotNull(exported.Snapshot);
        TransferAttachment output = Assert.Single(exported.Snapshot!.Catalog.Threads[0].Posts[0].Attachments);
        Assert.Equal(TransferContentStatus.Contained, output.Status);
        Assert.Null(output.OriginalByteVersion!.Sha1);
        Assert.Equal(TransferContentStatus.Contained, Assert.Single(output.DerivedRepresentations).Status);
    }
}
