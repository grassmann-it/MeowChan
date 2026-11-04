using System.IO.Compression;
using MeowChan.Archive;
using MeowChan.Interfaces;
using MeowChan.Persistence;
using MeowChan.Transfer;
using static MeowChan.Tests.Transfer.NativeMediaImportTests;

namespace MeowChan.Tests.Transfer;

// Codec/staging and passive comparison boundaries only: native commit APIs are internal.
// In particular, New below is NOT a successful import, and fixture files are not resolver grants.
public sealed class NativeTransferDamagedPayloadIsolationTests
{
    [Theory]
    [InlineData("missing", false)]
    [InlineData("missing", true)]
    [InlineData("short", false)]
    [InlineData("short", true)]
    [InlineData("changed", false)]
    [InlineData("changed", true)]
    public async Task DamagedCurrentOrSharedRetainedPayloadNeverIssuesStageOrTouchesExisting404(
        string fault, bool retained)
    {
        using Workspace files = new();
        await SeedPublic404(files);
        ArchivedThreadView old404 = await AssertPublic404(files);
        NativeTransferCatalog incoming = Content();
        // The retained case shares the current payload; it is not independent retained-only corruption.
        if (retained)
        {
            TransferAttachment historical = Attachment(postId: "900");
            incoming = incoming with { Threads = [incoming.Threads[0] with {
                RetainedBindings = [new("900", historical)] }] };
        }
        using MemoryStream valid = await Write(incoming, true);
        var control = await NativeTransferPackageValidator.InspectAsync(valid);
        Assert.True(control.IsPackageValid, control.ToString());
        Assert.True(NativeTransferImportPreview.Create(control.Package).HasPreview);
        byte[] damaged = RewritePayload(valid.ToArray(), fault);
        var before = Files(files.Profile);
        for (int attempt = 0; attempt < 2; attempt++)
        {
            using MemoryStream input = new(damaged, false);
            var inspection = await NativeTransferPackageValidator.InspectAsync(input);
            Assert.False(inspection.IsPackageValid);
            Assert.Null(inspection.Package);
            Assert.Equal(TransferCatalogError.None, inspection.CatalogError);
            Assert.Equal(fault switch {
                "missing" => TransferEnvelopeError.InvalidReference,
                "short" => TransferEnvelopeError.LengthMismatch,
                _ => TransferEnvelopeError.HashMismatch
            }, inspection.EnvelopeError);
            var preview = NativeTransferImportPreview.Create(inspection.Package);
            Assert.False(preview.HasPreview);
            Assert.Equal(TransferImportPreviewError.InvalidPackage, preview.Error);
            Assert.Null(preview.Preview); // No inventory/report of accepted bytes after failed validation.
            input.Position = 0;
            var staged = await NativeTransferImportStage.CreateAsync(input, files.StageRoot);
            Assert.Equal(NativeTransferStageError.InvalidPackage, staged.Error);
            Assert.Equal(inspection.EnvelopeError, staged.EnvelopeError);
            Assert.Equal(TransferCatalogError.None, staged.CatalogError);
            Assert.Null(staged.Stage);
            Assert.False(staged.StageRetained);
            Assert.Empty(Directory.GetFileSystemEntries(files.StageRoot));
            Assert.Equal(before, Files(files.Profile));
            await AssertPublic404(files, old404);
        }
    }

    [Theory]
    [InlineData((int)TransferContentStatus.Unavailable, (int)TransferContentStatus.Unavailable)]
    [InlineData((int)TransferContentStatus.Failed, (int)TransferContentStatus.Unavailable)]
    [InlineData((int)TransferContentStatus.Omitted, (int)TransferContentStatus.Omitted)]
    public async Task RepeatedMissingMediaInventoryAnd404ComparisonRemainPassiveAndTruthful(int currentStatusValue, int retainedStatusValue)
    {
        using Workspace files = new();
        await SeedPublic404(files);
        ArchivedThreadView old404 = await AssertPublic404(files);
        TransferContentStatus status = (TransferContentStatus)currentStatusValue;
        // Failed is valid for current media, never for retained bindings.
        TransferContentStatus retainedStatus = (TransferContentStatus)retainedStatusValue;
        TransferAttachment absent = Attachment() with { Status = status, PayloadSha256 = null };
        TransferAttachment historical = Attachment(postId: "900") with { Status = retainedStatus, PayloadSha256 = null };
        // Analyze requires admitted projections. Content() is a writer-input builder: its
        // post 16 has no Attachments presence bit and null metadata, which serializes to
        // canonical empty attachments (not wire null). Do not pass that raw DTO to Analyze.
        using MemoryStream targetBytes = await Write(Content(), true);
        var targetInspection = await NativeTransferPackageValidator.InspectAsync(targetBytes);
        Assert.True(targetInspection.IsPackageValid, targetInspection.ToString());
        Assert.Equal(TransferEnvelopeError.None, targetInspection.EnvelopeError);
        Assert.Equal(TransferCatalogError.None, targetInspection.CatalogError);
        NativeTransferCatalog source = Assert.IsType<NativeTransferPackage>(targetInspection.Package).Catalog;
        TransferThread target = Assert.Single(source.Threads);
        Assert.Equal(new[] { "15", "16" }, target.Posts.Select(p => p.Metadata.PostId));
        TransferPost targetOnly = target.Posts[1];
        Assert.False(targetOnly.Metadata.Fields.HasFlag(PostFieldPresence.Attachments));
        Assert.False(targetOnly.AttachmentsWereNull);
        Assert.NotNull(targetOnly.Metadata.Attachments);
        Assert.Empty(targetOnly.Metadata.Attachments!);
        Assert.Empty(targetOnly.Attachments);
        TransferPost post = target.Posts[0];
        Assert.Equal(TransferContentStatus.Contained, Assert.Single(post.Attachments).Status);
        Assert.Equal(Hash(Bytes), post.Attachments[0].PayloadSha256);
        NativeTransferCatalog incoming = source with { Threads = [source.Threads[0] with {
            Posts = [post with { Attachments = [absent] }],
            RetainedBindings = [new("900", historical)] }] };
        using MemoryStream bytes = await Write(incoming, false);
        var before = Files(files.Profile);
        for (int attempt = 0; attempt < 2; attempt++)
        {
            var inspected = await NativeTransferPackageValidator.InspectAsync(bytes);
            Assert.True(inspected.IsPackageValid, inspected.ToString());
            NativeTransferPackage package = Assert.IsType<NativeTransferPackage>(inspected.Package);
            var preview = NativeTransferImportPreview.Create(package);
            Assert.True(preview.HasPreview);
            Assert.Equal(TransferImportPreviewError.None, preview.Error);
            TransferImportPreview report = Assert.IsType<TransferImportPreview>(preview.Preview);
            TransferImportStatusCounts expected = status switch {
                TransferContentStatus.Unavailable => new(0, 0, 0, 1, 0, 0),
                TransferContentStatus.Failed => new(0, 0, 0, 0, 1, 0),
                _ => new(0, 1, 0, 0, 0, 0)
            };
            Assert.Equal(expected, report.CurrentOriginals);
            TransferImportStatusCounts expectedRetained = retainedStatus == TransferContentStatus.Unavailable
                ? new(0, 0, 0, 1, 0, 0) : new(0, 1, 0, 0, 0, 0);
            Assert.Equal(expectedRetained, report.RetainedBindings);
            Assert.Equal(new TransferImportStatusCounts(0, 0, 0, 0, 0, 0), report.CurrentPreviews);
            TransferImportThreadPreview threadReport = Assert.Single(report.Threads);
            Assert.Equal(Id, threadReport.Id);
            Assert.Equal(1, threadReport.Posts);
            Assert.Equal(expected, threadReport.CurrentOriginals);
            Assert.Equal(expectedRetained, threadReport.RetainedBindings);
            Assert.Equal(1, report.Posts); // Historical post 900 is never synthesized.
            Assert.Equal(0, report.UniquePayloads);
            Assert.Equal(0L, report.UniquePayloadBytes);
            Assert.Empty(package.Envelope.Payloads);
            Assert.Empty(package.Envelope.MeasuredPayloads);
            TransferThread wire = Assert.Single(package.Catalog.Threads);
            Assert.Equal(status, Assert.Single(wire.Posts[0].Attachments).Status);
            Assert.Null(wire.Posts[0].Attachments[0].PayloadSha256);
            Assert.Equal(retainedStatus, Assert.Single(wire.RetainedBindings).Attachment.Status);
            Assert.Null(wire.RetainedBindings[0].Attachment.PayloadSha256);
            Assert.Equal(absent.Metadata.Url, wire.Posts[0].Attachments[0].Metadata.Url);
            Assert.Equal(historical.Metadata.Url, Assert.Single(wire.RetainedBindings).Attachment.Metadata.Url);
            // Both projections are validator output, still not a catalog-loaded merge plan.
            var comparison = NativeTransferMergeAnalysis.Analyze(target, wire);
            Assert.Equal(TransferMergeAnalysisError.None, comparison.Error);
            Assert.Equal(TransferMergeDisposition.TargetOnly,
                Assert.Single(comparison.Posts.Where(p => p.PostId == "16")).Disposition);
            Assert.Equal(TransferMergeDisposition.Conflict,
                Assert.Single(comparison.Posts.Where(p => p.PostId == "15")).Disposition);
            Assert.Equal(TransferPostDifference.Attachments,
                Assert.Single(comparison.Posts.Where(p => p.PostId == "15")).Differences);
            Assert.Equal(2, comparison.Posts.Length);
            TransferMergeRetainedAnalysis retained = Assert.Single(comparison.RetainedBindings);
            Assert.Equal("900", retained.PostId);
            Assert.Equal(historical.Metadata.AttachmentRevisionKey, retained.RevisionKey);
            Assert.Equal(TransferMergeDisposition.New, retained.Disposition);
            Assert.Equal(before, Files(files.Profile)); // Comparison cannot apply even a New candidate.
            await AssertPublic404(files, old404);
        }
    }

    private static async Task<MemoryStream> Write(NativeTransferCatalog content, bool includePayload)
    {
        MemoryStream output = new();
        TransferPayloadSource[] payloads = includePayload
            ? [new(new(Bytes.Length, Hash(Bytes)), _ => Task.FromResult<Stream>(new MemoryStream(Bytes, false)))] : [];
        var written = await NativeTransferPackageWriter.WriteAsync(output, content, payloads, Guid.NewGuid());
        Assert.True(written.IsComplete, written.Error.ToString());
        output.Position = 0;
        return output;
    }

    // Rebuild a valid stored ZIP so the negative case reaches payload validation, not broken ZIP framing.
    private static byte[] RewritePayload(byte[] original, string fault)
    {
        using MemoryStream source = new(original, false);
        using ZipArchive input = new(source, ZipArchiveMode.Read);
        using MemoryStream output = new();
        using (ZipArchive zip = new(output, ZipArchiveMode.Create, true))
        foreach (ZipArchiveEntry entry in input.Entries)
        {
            bool payload = entry.FullName == "payload/" + Hash(Bytes);
            if (payload && fault == "missing") continue;
            using Stream read = entry.Open();
            using MemoryStream buffer = new();
            read.CopyTo(buffer);
            byte[] data = buffer.ToArray();
            if (payload && fault == "short") data = data[..^1];
            if (payload && fault == "changed") data[0] ^= 0xff;
            using Stream write = zip.CreateEntry(entry.FullName, CompressionLevel.NoCompression).Open();
            write.Write(data);
        }
        return output.ToArray();
    }

    private static async Task SeedPublic404(Workspace files)
    {
        string media = Directory.CreateDirectory(Path.Combine(files.Profile, "existing-media")).FullName;
        await File.WriteAllBytesAsync(Path.Combine(media, "good-original.bin"), Bytes);
        using ProfileLease lease = ProfileLease.Acquire(files.Profile);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(files.Profile, lease);
        await catalog.CaptureAsync(new(Id, Url, "local 404", ThreadSourceState.NotFound, At, media,
            Content().Threads[0].Posts.Select(p => p.Metadata).ToArray()));
        await catalog.SetCaptureEnabledAsync(Id, false);
    }

    private static async Task<ArchivedThreadView> AssertPublic404(Workspace files, ArchivedThreadView? before = null)
    {
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(files.Profile);
        ArchivedThreadView view = Assert.IsType<ArchivedThreadView>(await reader.ReadThreadAsync(Id));
        Assert.Equal(ThreadSourceState.NotFound, view.Summary.SourceState);
        Assert.False(view.Summary.CaptureEnabled);
        Assert.Equal("local 404", view.Summary.Title);
        var posts = view.GetPosts();
        Assert.Equal(new[] { "15", "16" }, posts.Select(p => p.PostId));
        Assert.Equal(Content().Threads[0].Posts[0].Metadata.Html, posts[0].Html);
        Assert.Equal(Attachment().Metadata.Url, Assert.Single(posts[0].Attachments!).Url);
        if (before != null)
        {
            Assert.Equal(before.Summary, view.Summary); // Includes revision, timestamps, URL and media root.
            Assert.Equal(before.MediaDirectories, view.MediaDirectories);
            Assert.Equal(before.PostCount, view.PostCount);
            var oldPosts = before.GetPosts();
            for (int i = 0; i < posts.Count; i++)
            {
                Assert.Equal(oldPosts[i] with { Attachments = null }, posts[i] with { Attachments = null });
                Assert.Equal(oldPosts[i].Attachments, posts[i].Attachments);
            }
        }
        Assert.Equal(Bytes, await File.ReadAllBytesAsync(Path.Combine(view.Summary.MediaDirectory, "good-original.bin")));
        return view;
    }
}
