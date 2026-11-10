using System.Collections.Immutable;
using System.Text;
using MeowChan.Archive;
using MeowChan.Interfaces;
using MeowChan.Persistence;
using MeowChan.Storage;
using MeowChan.Transfer;
using static MeowChan.Tests.Transfer.NativeV2ImportFixture;

namespace MeowChan.Tests.Transfer;

// Multi-thread apply is composed sequentially from per-thread slices of one validated stage.
// Each slice goes through the unchanged single-thread v2 pipeline; no new storage authority exists.
public sealed class NativeTransferThreadSliceTests
{
    private static readonly ResourceId SecondId = new("4chan", "b", "7002");
    private static readonly byte[] SecondOriginal = Encoding.UTF8.GetBytes("synthetic slice second original");
    private static readonly byte[] SecondPreview = Encoding.UTF8.GetBytes("synthetic slice second preview");

    private static TransferThread SecondThread()
    {
        MediaProviderMetadata provider = new("media-21", "original", "https://i.4cdn.org/b/21.webm", "synthetic.webm",
            SecondOriginal.Length, Md5(SecondOriginal), Hash(SecondOriginal));
        CapturedAttachment metadata = new(0, provider.MediaId, provider.Variant, provider.Url, provider.OriginalFileName,
            ExpectedSize: provider.ExpectedSize, ExpectedMd5: provider.ExpectedMd5)
        { ExpectedSha256 = provider.ExpectedSha256, AttachmentRevisionKey = MediaRevisionKeys.CreateAttachmentKey(SecondId, "21", 0, provider) };
        TransferOriginalByteVersion version = new(SecondOriginal.LongLength, Hash(SecondOriginal), Md5(SecondOriginal), null);
        TransferAttachment attachment = new(metadata, TransferMediaRole.Original, TransferContentStatus.Contained, Hash(SecondOriginal), null)
        {
            OriginalByteVersion = version,
            DerivedRepresentations = [new(TransferDerivedRepresentationKind.Preview, metadata.AttachmentRevisionKey!, version,
                TransferContentStatus.Contained, Hash(SecondPreview))]
        };
        return new(SecondId, "https://boards.4chan.org/b/thread/7002", "synthetic second thread", ThreadSourceState.NotFound,
            At, At, 1, true, [Post("21", attachment)]);
    }

    private static NativeTransferCatalog TwoThreads() => new(At, [Content().Threads[0], SecondThread()]);

    [Fact]
    public async Task SlicesExposeOnlyTheirOwnPayloadsAndNeverCloseTheParent()
    {
        using Workspace files = new();
        await using NativeTransferImportStage stage = await StageAsync(files, TwoThreads(), Original, Preview, SecondOriginal, SecondPreview);
        NativeTransferImportStage first = Assert.IsType<NativeTransferImportStage>(stage.CreateThreadSlice(Id));
        NativeTransferImportStage second = Assert.IsType<NativeTransferImportStage>(stage.CreateThreadSlice(SecondId));
        Assert.Null(stage.CreateThreadSlice(new("4chan", "b", "404")));
        Assert.True(first.IsThreadSlice);
        Assert.Single(first.Package.Catalog.Threads);
        Assert.Equal(2, first.Package.Envelope.Payloads.Length);
        Assert.Equal(2, second.Package.Envelope.MeasuredPayloads.Count);
        Assert.NotEqual(first.Package.Envelope.Fingerprint, second.Package.Envelope.Fingerprint);
        Assert.NotEqual(stage.Package.Envelope.Fingerprint, first.Package.Envelope.Fingerprint);
        Assert.Null(stage.CreateThreadSlice(Id) is { } again ? null : "slice-recreation-failed");

        using MemoryStream copy = new();
        TransferMeasuredBlob blob = await first.CopyPayloadToAsync(Hash(Original), copy);
        Assert.Equal(Original, copy.ToArray());
        Assert.Equal(Hash(Original), blob.Sha256);
        await Assert.ThrowsAsync<ArgumentException>(() => first.CopyPayloadToAsync(Hash(SecondOriginal), new MemoryStream()));
        await first.DisposeAsync();
        using MemoryStream afterDispose = new();
        await stage.CopyPayloadToAsync(Hash(SecondOriginal), afterDispose); // Parent remains usable.
        Assert.Equal(SecondOriginal, afterDispose.ToArray());
    }

    [Fact]
    public async Task EachSliceCommitsThroughTheSingleThreadPipelineAndReplaysIdempotently()
    {
        using Workspace files = new();
        using ProfileLease lease = ProfileLease.Acquire(files.Profile);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(files.Profile, lease);
        await using NativeTransferImportStage stage = await StageAsync(files, TwoThreads(), Original, Preview, SecondOriginal, SecondPreview);
        // The whole multi-thread stage is still rejected by the single-thread pipeline: no silent partial import.
        Assert.Equal(NativeMediaImportError.UnsupportedPackage, (await catalog.PrepareNativeMediaImportAsync(stage)).Error);
        lease.Admission.CompleteStartup();
        MediaStore store = new();
        foreach (ResourceId resource in new[] { Id, SecondId })
        {
            NativeTransferImportStage slice = Assert.IsType<NativeTransferImportStage>(stage.CreateThreadSlice(resource));
            NativeMediaImportPreparation prepared = await catalog.PrepareNativeMediaImportAsync(slice);
            Assert.Equal(NativeMediaImportError.None, prepared.Error);
            using ProfileAdmissionToken admission = await lease.Admission.EnterExclusiveAsync();
            var outcome = await catalog.CommitNativeMediaImportAdmittedAsync(admission, Assert.IsType<NativeMediaImportPlan>(prepared.Plan), store);
            Assert.Equal(ProfileExclusiveDisposition.SafeToResume, outcome.Disposition);
            Assert.Equal(NativeMediaImportCommitStatus.Committed, outcome.Result.Status);
            Assert.Equal(1, outcome.Result.NewThreads);
        }
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(files.Profile);
        Assert.NotNull(await reader.ReadThreadAsync(Id));
        Assert.NotNull(await reader.ReadThreadAsync(SecondId));
        // Re-preparing an already imported slice reports the existing resource instead of duplicating it.
        NativeTransferImportStage replay = Assert.IsType<NativeTransferImportStage>(stage.CreateThreadSlice(SecondId));
        Assert.Equal(NativeMediaImportError.ResourceExists, (await catalog.PrepareNativeMediaImportAsync(replay)).Error);
    }

    [Fact]
    public async Task ImportedSetSurvivesRestartOfflineAndReexportsBothThreads()
    {
        using Workspace files = new();
        await using (NativeTransferImportStage stage = await StageAsync(files, TwoThreads(), Original, Preview, SecondOriginal, SecondPreview))
        {
            using ProfileLease lease = ProfileLease.Acquire(files.Profile);
            await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(files.Profile, lease);
            lease.Admission.CompleteStartup();
            MediaStore store = new();
            foreach (ResourceId resource in new[] { Id, SecondId })
            {
                NativeTransferImportStage slice = Assert.IsType<NativeTransferImportStage>(stage.CreateThreadSlice(resource));
                NativeMediaImportPreparation prepared = await catalog.PrepareNativeMediaImportAsync(slice);
                using ProfileAdmissionToken admission = await lease.Admission.EnterExclusiveAsync();
                var outcome = await catalog.CommitNativeMediaImportAdmittedAsync(admission, Assert.IsType<NativeMediaImportPlan>(prepared.Plan), store);
                Assert.Equal(NativeMediaImportCommitStatus.Committed, outcome.Result.Status);
            }
        } // Catalog, lease and stage are closed: nothing below may need a source plugin, network or the package.

        await using ArchiveReader reader = await ArchiveReader.OpenAsync(files.Profile);
        TransferArchiveFreezeResult frozen = await NativeTransferArchiveExporter.FreezeAsync(reader,
            new NativeTransferArchiveSelection([Id, SecondId]), At.AddDays(3), Guid.NewGuid(),
            new NativeTransferArchiveMediaOptions(true, true, true));
        Assert.Equal(TransferArchiveError.None, frozen.Error);
        using MemoryStream output = new();
        TransferArchiveWriteResult written = await NativeTransferArchiveExporter.WriteAsync(Assert.IsType<NativeTransferArchiveSnapshot>(frozen.Snapshot), output);
        Assert.True(written.IsComplete, written.ToString());
        output.Position = 0;
        TransferPackageInspection inspected = await NativeTransferPackageValidator.InspectAsync(output);
        Assert.True(inspected.IsPackageValid, inspected.ToString());
        Assert.Equal(new[] { Id, SecondId }.OrderBy(x => x.Thread), inspected.Package!.Catalog.Threads.Select(t => t.Id).OrderBy(x => x.Thread));
        Assert.All(inspected.Package.Catalog.Threads, thread => Assert.NotEmpty(thread.Posts));
        Assert.Contains(Hash(SecondOriginal), inspected.Package.Envelope.Payloads.Select(p => p.Sha256));
        Assert.Contains(Hash(Original), inspected.Package.Envelope.Payloads.Select(p => p.Sha256));
    }
}
