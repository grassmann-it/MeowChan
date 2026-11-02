using System.Collections.Immutable;
using MeowChan.Archive;
using MeowChan.Persistence;
using MeowChan.Storage;
using MeowChan.Transfer;
using static MeowChan.Tests.Transfer.NativeMediaImportTests;

namespace MeowChan.Tests.Transfer;

public sealed class NativeMediaMergeBudgetTests
{
    private const long GiB = 1024L * 1024 * 1024;
    private static readonly Guid PackageId = Guid.Parse("54374850-00e2-4a7f-bd4c-e3a2e62b955f");

    [Fact]
    public async Task TwoOneGiBPayloadsExceedContainerBeforeOpeningAnySource()
    {
        TransferBlobDescriptor[] descriptors = [new(GiB, new('1', 64)), new(GiB, new('2', 64))];
        var budget = NativeTransferPackageWriter.MeasureBudget(Content(), descriptors, PackageId);
        Assert.False(budget.WithinContainerCap);
        Assert.Equal(2 * GiB, budget.PayloadBytes);
        Assert.True(budget.ContainerBytes > 2 * GiB);
        Assert.True(budget.CatalogBytes > 0 && budget.ManifestBytes > 0 && budget.ZipOverhead > 0);
        bool opened = false;
        TransferPayloadSource[] sources = descriptors.Select(descriptor => new TransferPayloadSource(descriptor, _ =>
        { opened = true; throw new InvalidOperationException("Budget must reject before IO."); })).ToArray();
        using MemoryStream output = new();
        TransferWriteResult rejected = await NativeTransferPackageWriter.WriteAsync(output, Content(), sources, PackageId);
        Assert.Equal(TransferWriteError.OutputLimit, rejected.Error);
        Assert.False(opened);
        Assert.Equal(0, output.Length);
    }

    [Fact]
    public async Task StoredZipOverheadIsIncludedAtExactArithmeticBoundary()
    {
        TransferBlobDescriptor[] descriptors = [new(GiB, new('1', 64)), new(GiB, new('2', 64))];
        var first = NativeTransferPackageWriter.MeasureBudget(Content(), descriptors, PackageId);
        long excess = first.ContainerBytes - 2 * GiB;
        descriptors[1] = descriptors[1] with { Length = GiB - excess };
        var exact = NativeTransferPackageWriter.MeasureBudget(Content(), descriptors, PackageId);
        Assert.True(exact.WithinContainerCap);
        Assert.Equal(2 * GiB, exact.ContainerBytes);
        Assert.Equal(22 + 4 * 76 + 2 * (13 + 12 + 2 * 72), exact.ZipOverhead);

        descriptors[1] = descriptors[1] with { Length = descriptors[1].Length + 1 };
        var exceeded = NativeTransferPackageWriter.MeasureBudget(Content(), descriptors, PackageId);
        Assert.False(exceeded.WithinContainerCap);
        Assert.Equal(2 * GiB + 1, exceeded.ContainerBytes);
        // The declared contents alone still fit. It is exactly the ZIP framing that now
        // exceeds the budget; no GiB-sized allocation/read is needed to test this seam.
        Assert.True(exceeded.PayloadBytes + exceeded.CatalogBytes + exceeded.ManifestBytes < 2 * GiB);
        using MemoryStream output = new();
        TransferWriteResult rejected = await NativeTransferPackageWriter.WriteAsync(output, Content(),
            descriptors.Select(descriptor => new TransferPayloadSource(descriptor, _ =>
                throw new InvalidOperationException("No source may be opened."))).ToArray(), PackageId);
        Assert.Equal(TransferWriteError.OutputLimit, rejected.Error);
        Assert.Equal(0, output.Length);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BudgetMatchesSmallActualNativeWriterIncludingHistoricalMetadata(bool withHistory)
    {
        NativeTransferCatalog content = Content();
        if (withHistory)
        {
            TransferThread thread = content.Threads[0];
            TransferPost post = thread.Posts[0];
            TransferAttachment attachment = post.Attachments[0] with
            { History = new("strict", At, false, false) };
            content = content with { Threads = [thread with { Posts = [post with { Attachments = [attachment] }, thread.Posts[1]] }] };
        }
        TransferBlobDescriptor descriptor = new(Bytes.Length, Hash(Bytes));
        var budget = NativeTransferPackageWriter.MeasureBudget(content, [descriptor], PackageId);
        Assert.True(budget.WithinContainerCap);
        using MemoryStream output = new();
        TransferWriteResult written = await NativeTransferPackageWriter.WriteAsync(output, content,
            [new(descriptor, _ => Task.FromResult<Stream>(new MemoryStream(Bytes, false)))], PackageId);
        Assert.True(written.IsComplete, written.Error.ToString());
        Assert.Equal(budget.ContainerBytes, output.Length);
        Assert.Equal(budget.ContainerBytes, written.BytesWritten);
    }

    [Fact]
    public async Task CurrentProjectionAtCatalogBoundaryCannotHideContainedPayloadAndHistoryCost()
    {
        using Workspace files = new();
        using ProfileLease lease = ProfileLease.Acquire(files.Profile);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(files.Profile, lease);
        NativeTransferCatalog content = Content();
        TransferThread initialThread = content.Threads[0];
        TransferPost initialPost = initialThread.Posts[0];
        TransferAttachment historical = initialPost.Attachments[0] with { History = new("strict", At, false, false) };
        NativeTransferCatalog initial = content with
        { Threads = [initialThread with { Posts = [initialPost with { Attachments = [historical] }, initialThread.Posts[1]] }] };
        await using (NativeTransferImportStage seed = await Stage(files, initial))
        {
            NativeMediaImportPlan seedPlan = Assert.IsType<NativeMediaImportPlan>((await catalog.PrepareNativeMediaImportAsync(seed)).Plan);
            Assert.Equal(NativeMediaImportCommitStatus.Committed, (await catalog.CommitNativeMediaImportAsync(seedPlan, new MediaStore())).Status);
        }
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(files.Profile);
        ArchivedThreadView old = (await reader.ReadThreadAsync(Id))!;
        List<CapturedPost> posts = old.Posts.ToList();
        for (int i = 0; i < 15; i++)
            posts.Add(new((100 + i).ToString(), 100 + i, PostFieldPresence.Html, Html: new string('x', 1024 * 1024)));
        posts.Add(new("200", 200, PostFieldPresence.Html, Html: string.Empty));
        await catalog.CapturePreservingRevisionsAsync(new(Id, Url, old.Summary.Title, old.Summary.SourceState, At,
            old.Summary.MediaDirectory, posts));
        ArchivedThreadView view = (await reader.ReadThreadAsync(Id))!;
        int unpadded = NativeTransferCatalogSerializer.Serialize(new(At, [NativeTransferArchiveProjection.From(view)])).Length;
        int padding = 16 * 1024 * 1024 - unpadded - 8;
        Assert.InRange(padding, 1, 1024 * 1024);
        posts[^1] = posts[^1] with { Html = new string('x', padding) };
        await catalog.CapturePreservingRevisionsAsync(new(Id, Url, old.Summary.Title, old.Summary.SourceState, At,
            old.Summary.MediaDirectory, posts));
        ArchivedThreadView target = (await reader.ReadThreadAsync(Id))!;
        byte[] currentOnly = NativeTransferCatalogSerializer.Serialize(new(At, [NativeTransferArchiveProjection.From(target)]));
        Assert.Equal(16 * 1024 * 1024 - 8, currentOnly.Length);
        // The old check passed this exact projection. Real history and contained SHA fields
        // do not fit, so preparation now fails before it can claim a journal or create roots.
        await using NativeTransferImportStage stage = await Stage(files, Content());
        Dictionary<string, string> before = Files(files.Profile);
        NativeMediaMergePreparation rejected = await catalog.PrepareNativeMediaMergeAsync(stage);
        Assert.Null(rejected.Plan);
        Assert.Equal(NativeMediaMergeError.TargetBudgetExceeded, rejected.Error);
        Assert.Empty((await catalog.ReadNativeMediaMergeOperationsPageAsync()).Items);
        Assert.False(Directory.Exists(Path.Combine(files.Profile, "merge-media-operations")));
        Assert.Equal(before, Files(files.Profile));
    }
}
