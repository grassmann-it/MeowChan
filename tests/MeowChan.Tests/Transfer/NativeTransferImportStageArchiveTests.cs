using System.Security.Cryptography;
using System.Text;
using MeowChan.Archive;
using MeowChan.Interfaces;
using MeowChan.Persistence;
using MeowChan.Storage;
using MeowChan.Tests.GoalFixtures;
using MeowChan.Transfer;

namespace MeowChan.Tests.Transfer;

// GP11 -> GP12.1 integration only: real synthetic catalog/store -> archive exporter ->
// owned import stage. Generic writer admission and adversarial lifecycle have separate suites.
public sealed class NativeTransferImportStageArchiveTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 1, 15, 0, 0, TimeSpan.Zero);
    private static readonly Guid PackageId = Guid.Parse("42b17f33-87ec-4019-a646-5e1c78f2be03");

    [Theory]
    [InlineData(ThreadSourceState.Archived, null, false)]
    [InlineData(ThreadSourceState.Archived, false, true)]
    [InlineData(ThreadSourceState.Archived, true, false)]
    [InlineData(ThreadSourceState.NotFound, null, true)]
    [InlineData(ThreadSourceState.NotFound, false, false)]
    [InlineData(ThreadSourceState.NotFound, true, true)]
    public async Task ArchiveOriginalsAndPassiveMetadataSurviveLossOfBothArchiveAndCallerPackage(
        ThreadSourceState sourceState, bool? order, bool disposeCaller)
    {
        await using NativeTransferArchiveFixture source = await NativeTransferArchiveFixture.CreateAsync(sourceState, order);
        await using NativeTransferArchiveFixture target = await NativeTransferArchiveFixture.CreateAsync();
        string staging = Directory.CreateDirectory(Path.Combine(target.ProfilePath, "incoming-work")).FullName;
        Dictionary<string, byte[]> targetBefore = ProfileFiles(target.ProfilePath, staging);
        Dictionary<string, byte[]> sourceBefore = AuthoritativeFiles(source);
        byte[] strictBytes = await File.ReadAllBytesAsync(source.StrictCommit.Path);
        byte[] legacyBytes = await File.ReadAllBytesAsync(source.LegacyCommit.Path);
        Assert.Equal(source.StrictBytes.ToArray(), strictBytes);
        Assert.Equal(source.LegacyBytes.ToArray(), legacyBytes);

        using MemoryStream callerPackage = new();
        (NativeTransferPackage written, ArchivedThreadView view) = await ExportAsync(source, callerPackage);
        AssertFilesEqual(sourceBefore, AuthoritativeFiles(source));
        callerPackage.Position = 0;
        NativeTransferStageResult created = await NativeTransferImportStage.CreateAsync(callerPackage, staging);
        await using NativeTransferImportStage stage = SuccessfulStage(created);
        Assert.True(callerPackage.CanRead); // Stage creation does not acquire caller ownership.
        AssertFilesEqual(sourceBefore, AuthoritativeFiles(source));
        AssertFilesEqual(targetBefore, ProfileFiles(target.ProfilePath, staging));
        AssertMatchesExport(written, stage.Package);
        AssertArchiveMetadata(source, view, stage.Package);
        TransferThread thread = Assert.Single(stage.Package.Catalog.Threads);
        Assert.Equal(sourceState, thread.SourceState);
        Assert.Equal(order, thread.DecimalPostOrder);
        Assert.Equal(source.ExpectedPostIds, thread.Posts.Select(p => p.Metadata.PostId));
        AssertCurrentMedia(source, thread);

        if (disposeCaller)
        {
            callerPackage.Dispose();
            Assert.False(callerPackage.CanRead);
        }
        else
        {
            callerPackage.SetLength(0);
            callerPackage.Position = 0;
            await callerPackage.WriteAsync(Encoding.ASCII.GetBytes("synthetic caller package replaced after admission"));
        }
        // Invalidate both original paths before destroying the entire synthetic source profile.
        byte[] changed = strictBytes.ToArray();
        changed[0] ^= 0xff;
        await File.WriteAllBytesAsync(source.StrictCommit.Path, changed);
        File.Delete(source.LegacyCommit.Path);
        await source.DisposeAsync();
        Assert.False(Directory.Exists(source.ProfilePath));

        AssertMatchesExport(written, stage.Package);
        AssertArchiveMetadata(source, view, stage.Package);
        AssertCurrentMedia(source, Assert.Single(stage.Package.Catalog.Threads));
        TransferImportPreview preview = Preview(stage);
        Assert.Equal(3, preview.Posts);
        Assert.Equal(new TransferImportStatusCounts(2, 0, 1, 0, 0, 0), preview.CurrentOriginals);
        Assert.Equal(0, preview.CurrentPreviews.Total);
        Assert.Equal(0, preview.RetainedBindings.Total);
        Assert.Equal(2, preview.UniquePayloads);
        Assert.Equal((long)strictBytes.Length + legacyBytes.Length, preview.UniquePayloadBytes);
        Assert.Equal(preview.CurrentOriginals, Assert.Single(preview.Threads).CurrentOriginals);
        await AssertVerifiedCopyAsync(stage, legacyBytes);
        await AssertVerifiedCopyAsync(stage, strictBytes);
        await AssertVerifiedCopyAsync(stage, legacyBytes);
        Assert.Equal(0, source.UnexpectedProviderCalls);
        Assert.Equal(0, target.UnexpectedProviderCalls);
        AssertFilesEqual(targetBefore, ProfileFiles(target.ProfilePath, staging));
        await stage.DisposeAsync();
        Assert.False(stage.StageRetained);
        AssertFilesEqual(targetBefore, ProfileFiles(target.ProfilePath, staging));
    }

    [Fact]
    public async Task RealOldBindingAndSparsePresenceReachStageWithoutBecomingCurrentObservationsOrAuthority()
    {
        await using NativeTransferArchiveFixture source = await NativeTransferArchiveFixture.CreateAsync(ThreadSourceState.NotFound, false);
        await using NativeTransferArchiveFixture target = await NativeTransferArchiveFixture.CreateAsync();
        string staging = Directory.CreateDirectory(Path.Combine(target.ProfilePath, "incoming-work")).FullName;
        Dictionary<string, byte[]> targetBefore = ProfileFiles(target.ProfilePath, staging);
        byte[] oldBytes = await File.ReadAllBytesAsync(source.StrictCommit.Path);
        byte[] legacyBytes = await File.ReadAllBytesAsync(source.LegacyCommit.Path);
        // Only the captured observation changes. The existing real manifest binding remains old;
        // no new store commit, fabricated retained DTO, direct SQL or extra fixture is needed.
        CapturedAttachment latest = source.StrictAttachment with
        {
            MediaId = "114", Url = "https://i.4cdn.org/b/114.jpg", OriginalFileName = "later-synthetic.jpg",
            PlannedPath = Path.Combine(source.MediaDirectory, "114.jpg")
        };
        MediaProviderMetadata provider = new(latest.MediaId!, latest.Variant, latest.Url!, latest.OriginalFileName,
            latest.ExpectedSize, latest.ExpectedMd5, latest.ExpectedSha256) { ExpectedSha1 = latest.ExpectedSha1 };
        latest = latest with { AttachmentRevisionKey = MediaRevisionKeys.CreateAttachmentKey(source.ThreadId,
            NativeTransferArchiveFixture.StrictPostId, latest.Position, provider) };
        CapturedPost sparse = new("7004", 2, PostFieldPresence.Subject | PostFieldPresence.Html,
            Subject: "", Html: null); // observed empty/null; attachment presence bit is absent
        CapturedPost observedEmpty = new("7005", 3, PostFieldPresence.Attachments, Attachments: []);
        using (ProfileLease lease = ProfileLease.Acquire(source.ProfilePath))
        {
            await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(source.ProfilePath, lease);
            await catalog.CapturePreservingRevisionsAsync(source.Snapshot with
            {
                Posts = source.Posts.Select(p => p.PostId == NativeTransferArchiveFixture.StrictPostId
                    ? p with { Attachments = new[] { latest } } : p).Concat(new[] { sparse, observedEmpty }).ToArray()
            });
        }
        Dictionary<string, byte[]> sourceBefore = AuthoritativeFiles(source);
        using MemoryStream callerPackage = new();
        (NativeTransferPackage written, ArchivedThreadView view) = await ExportAsync(source, callerPackage);
        callerPackage.Position = 0;
        await using NativeTransferImportStage stage = SuccessfulStage(
            await NativeTransferImportStage.CreateAsync(callerPackage, staging));
        AssertFilesEqual(sourceBefore, AuthoritativeFiles(source));
        callerPackage.Dispose();
        await source.DisposeAsync();
        Assert.False(Directory.Exists(source.ProfilePath));

        AssertMatchesExport(written, stage.Package);
        AssertArchiveMetadata(source, view, stage.Package);
        Assert.Equal("chan-v2", stage.Package.Envelope.ContentSchema);
        TransferThread thread = Assert.Single(stage.Package.Catalog.Threads);
        Assert.Equal(ThreadSourceState.NotFound, thread.SourceState);
        Assert.Equal(false, thread.DecimalPostOrder);
        TransferAttachment current = thread.Posts.Single(p => p.Metadata.PostId == NativeTransferArchiveFixture.StrictPostId).Attachments.Single();
        Assert.Equal(latest with { PlannedPath = null }, current.Metadata);
        Assert.Equal(TransferContentStatus.Unknown, current.Status);
        Assert.Null(current.PayloadSha256);
        Assert.Null(current.History);
        TransferRetainedBinding retained = Assert.Single(thread.RetainedBindings);
        Assert.Equal(NativeTransferArchiveFixture.StrictPostId, retained.PostId);
        Assert.Equal(source.StrictAttachment with { PlannedPath = null }, retained.Attachment.Metadata);
        Assert.Equal(TransferContentStatus.Contained, retained.Attachment.Status);
        Assert.Equal(Hash(oldBytes), retained.Attachment.PayloadSha256);
        Assert.Equal(new TransferHistoricalIntegrity("strict", source.StrictBinding.Acceptance.AcceptedAtUtc, false, false),
            retained.Attachment.History);
        Assert.NotEqual(current.Metadata.AttachmentRevisionKey, retained.Attachment.Metadata.AttachmentRevisionKey);
        Assert.Equal(5, thread.Posts.Length); // Retained binding is not an additional post observation.
        TransferPost partial = thread.Posts.Single(p => p.Metadata.PostId == sparse.PostId);
        Assert.Equal(sparse.Fields, partial.Metadata.Fields);
        Assert.Equal("", partial.Metadata.Subject);
        Assert.Null(partial.Metadata.Html);
        Assert.Null(partial.Metadata.Author);
        Assert.Null(partial.Metadata.PostedAt);
        // The existing SQL reader materializes [] even for missing attachment observations.
        // Preserve its actual projection AND missing presence bit, rather than inventing wire null.
        Assert.False(partial.Metadata.Fields.HasFlag(PostFieldPresence.Attachments));
        Assert.NotNull(partial.Metadata.Attachments);
        Assert.Empty(partial.Metadata.Attachments!);
        Assert.False(partial.AttachmentsWereNull);
        Assert.Empty(partial.Attachments);
        TransferPost empty = thread.Posts.Single(p => p.Metadata.PostId == observedEmpty.PostId);
        Assert.Equal(PostFieldPresence.Attachments, empty.Metadata.Fields);
        Assert.NotNull(empty.Metadata.Attachments);
        Assert.Empty(empty.Metadata.Attachments!);
        Assert.False(empty.AttachmentsWereNull);

        TransferImportPreview preview = Preview(stage);
        Assert.Equal(5, preview.Posts);
        Assert.Equal(new TransferImportStatusCounts(1, 0, 2, 0, 0, 0), preview.CurrentOriginals);
        Assert.Equal(new TransferImportStatusCounts(1, 0, 0, 0, 0, 0), preview.RetainedBindings);
        Assert.Equal(2, preview.UniquePayloads);
        Assert.Equal((long)oldBytes.Length + legacyBytes.Length, preview.UniquePayloadBytes);
        await AssertVerifiedCopyAsync(stage, oldBytes);
        await AssertVerifiedCopyAsync(stage, legacyBytes);
        Assert.Equal(0, source.UnexpectedProviderCalls);
        Assert.Equal(0, target.UnexpectedProviderCalls);
        AssertFilesEqual(targetBefore, ProfileFiles(target.ProfilePath, staging));
    }

    [Fact]
    public async Task ExplicitMetadataOnlyArchiveStaysOmittedAfterSourceDisposalDespiteExistingVerifiedOriginals()
    {
        await using NativeTransferArchiveFixture source = await NativeTransferArchiveFixture.CreateAsync(ThreadSourceState.NotFound);
        await using NativeTransferArchiveFixture target = await NativeTransferArchiveFixture.CreateAsync();
        string staging = Directory.CreateDirectory(Path.Combine(target.ProfilePath, "incoming-work")).FullName;
        Dictionary<string, byte[]> targetBefore = ProfileFiles(target.ProfilePath, staging);
        Dictionary<string, byte[]> sourceBefore = AuthoritativeFiles(source);
        using MemoryStream callerPackage = new();
        (NativeTransferPackage written, ArchivedThreadView view) = await ExportAsync(source, callerPackage, includeOriginals: false);
        callerPackage.Position = 0;
        await using NativeTransferImportStage stage = SuccessfulStage(
            await NativeTransferImportStage.CreateAsync(callerPackage, staging));
        AssertFilesEqual(sourceBefore, AuthoritativeFiles(source));
        callerPackage.Dispose();
        await source.DisposeAsync();

        AssertMatchesExport(written, stage.Package);
        AssertArchiveMetadata(source, view, stage.Package);
        TransferThread thread = Assert.Single(stage.Package.Catalog.Threads);
        Assert.Equal(ThreadSourceState.NotFound, thread.SourceState);
        Assert.Null(thread.DecimalPostOrder);
        Assert.Empty(thread.RetainedBindings);
        Assert.All(thread.Posts.SelectMany(p => p.Attachments), attachment =>
        {
            Assert.Equal(TransferContentStatus.Omitted, attachment.Status);
            Assert.Null(attachment.PayloadSha256);
            Assert.Null(attachment.History);
        });
        Assert.Empty(stage.Package.Envelope.Payloads);
        Assert.Empty(stage.Package.Envelope.MeasuredPayloads);
        TransferImportPreview preview = Preview(stage);
        Assert.Equal(3, preview.Posts);
        Assert.Equal(new TransferImportStatusCounts(0, 3, 0, 0, 0, 0), preview.CurrentOriginals);
        Assert.Equal(0, preview.UniquePayloads);
        Assert.Equal(0L, preview.UniquePayloadBytes);
        Assert.Equal(0, source.UnexpectedProviderCalls);
        Assert.Equal(0, target.UnexpectedProviderCalls);
        AssertFilesEqual(targetBefore, ProfileFiles(target.ProfilePath, staging));
    }

    private static async Task<(NativeTransferPackage Package, ArchivedThreadView View)> ExportAsync(
        NativeTransferArchiveFixture source, Stream destination, bool includeOriginals = true)
    {
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(source.ProfilePath);
        TransferArchiveFreezeResult frozen = await NativeTransferArchiveExporter.FreezeAsync(reader, source.ThreadId,
            At, PackageId, includeOriginals: includeOriginals);
        Assert.True(frozen.IsFrozen, frozen.Error.ToString());
        TransferArchiveWriteResult written = await NativeTransferArchiveExporter.WriteAsync(frozen.Snapshot!, destination);
        Assert.True(written.IsComplete, written.Error + "/" + written.WriteError);
        return (Assert.IsType<NativeTransferPackage>(written.Package), frozen.Snapshot!.View);
    }

    private static NativeTransferImportStage SuccessfulStage(NativeTransferStageResult result)
    {
        Assert.Equal(NativeTransferStageError.None, result.Error);
        Assert.Equal(TransferEnvelopeError.None, result.EnvelopeError);
        Assert.Equal(TransferCatalogError.None, result.CatalogError);
        Assert.False(result.StageRetained);
        return Assert.IsType<NativeTransferImportStage>(result.Stage);
    }

    private static TransferImportPreview Preview(NativeTransferImportStage stage)
    {
        TransferImportPreviewInspection result = NativeTransferImportPreview.Create(stage.Package);
        Assert.True(result.HasPreview, result.Error.ToString());
        return Assert.IsType<TransferImportPreview>(result.Preview);
    }

    private static void AssertMatchesExport(NativeTransferPackage expected, NativeTransferPackage actual)
    {
        Assert.Equal(expected.Envelope.PackageId, actual.Envelope.PackageId);
        Assert.Equal(expected.Envelope.Fingerprint, actual.Envelope.Fingerprint);
        Assert.Equal(expected.Envelope.ContentSchema, actual.Envelope.ContentSchema);
        Assert.Equal(expected.Envelope.CatalogBytes.ToArray(), actual.Envelope.CatalogBytes.ToArray());
        Assert.Equal(expected.Envelope.Payloads.ToArray(), actual.Envelope.Payloads.ToArray());
        Assert.Equal(NativeTransferCatalogSerializer.Serialize(expected.Catalog), NativeTransferCatalogSerializer.Serialize(actual.Catalog));
        Assert.Equal(expected.Envelope.MeasuredPayloads.Count, actual.Envelope.MeasuredPayloads.Count);
        foreach ((string hash, TransferMeasuredBlob measured) in expected.Envelope.MeasuredPayloads)
            Assert.Equal(measured, actual.Envelope.MeasuredPayloads[hash]);
    }

    private static void AssertArchiveMetadata(NativeTransferArchiveFixture source, ArchivedThreadView view, NativeTransferPackage package)
    {
        TransferThread thread = Assert.Single(package.Catalog.Threads);
        Assert.Equal(view.Summary.Id, thread.Id);
        Assert.Equal(view.Summary.Url, thread.PageUrl);
        Assert.Equal(view.Summary.Title, thread.Title);
        Assert.Equal(view.Summary.SourceState, thread.SourceState);
        Assert.Equal(view.Summary.FirstSeenAt, thread.FirstSeenAtUtc);
        Assert.Equal(view.Summary.LastCapturedAt, thread.CapturedAtUtc);
        Assert.Equal(view.Summary.Revision, thread.CaptureRevision);
        Assert.Equal(view.DecimalPostOrder, thread.DecimalPostOrder);
        Assert.Equal(view.Posts.Select(p => p.PostId), thread.Posts.Select(p => p.Metadata.PostId));
        foreach (TransferPost post in thread.Posts)
        {
            CapturedPost original = view.Posts.Single(p => p.PostId == post.Metadata.PostId);
            Assert.Equal(original with { Attachments = null }, post.Metadata with { Attachments = null });
            Assert.Equal(original.Attachments == null, post.AttachmentsWereNull);
            Assert.Equal((original.Attachments ?? []).Select(a => a with { PlannedPath = null }),
                post.Attachments.Select(a => a.Metadata));
            Assert.All(post.Attachments, a => Assert.Null(a.Metadata.PlannedPath));
        }
        Assert.All(thread.RetainedBindings, b => Assert.Null(b.Attachment.Metadata.PlannedPath));
        Assert.NotNull(source.StrictAttachment.PlannedPath); // Path exclusion is not a vacuous fixture assertion.
        Assert.NotNull(view.Posts.Single(p => p.PostId == NativeTransferArchiveFixture.StrictPostId).Attachments![0].PlannedPath);
        string wire = Encoding.UTF8.GetString(package.Envelope.CatalogBytes.AsSpan());
        Assert.DoesNotContain(source.ProfilePath, wire, StringComparison.Ordinal);
        Assert.DoesNotContain(source.MediaDirectory, wire, StringComparison.Ordinal);
        foreach (string field in new[] { "plannedPath", "mediaDirectory", "profileId", "pluginOwnerId", "policyRevision", "captureEnabled", "grants" })
            Assert.DoesNotContain('"' + field + '"', wire, StringComparison.Ordinal);
    }

    private static void AssertCurrentMedia(NativeTransferArchiveFixture source, TransferThread thread)
    {
        TransferAttachment strict = thread.Posts.Single(p => p.Metadata.PostId == NativeTransferArchiveFixture.StrictPostId).Attachments.Single();
        Assert.Equal(TransferMediaRole.Original, strict.Role);
        Assert.Equal(TransferContentStatus.Contained, strict.Status);
        Assert.Equal(source.StrictRevision, strict.Metadata.AttachmentRevisionKey);
        Assert.Equal(Hash(source.StrictBytes.ToArray()), strict.PayloadSha256);
        Assert.Equal(new TransferHistoricalIntegrity("strict", source.StrictBinding.Acceptance.AcceptedAtUtc, false, false), strict.History);
        TransferAttachment legacy = thread.Posts.Single(p => p.Metadata.PostId == NativeTransferArchiveFixture.LegacyPostId).Attachments.Single();
        Assert.Equal(TransferContentStatus.Contained, legacy.Status);
        Assert.Null(legacy.Metadata.AttachmentRevisionKey);
        Assert.Null(legacy.History);
        Assert.Equal(Hash(source.LegacyBytes.ToArray()), legacy.PayloadSha256);
        TransferAttachment unknown = thread.Posts.Single(p => p.Metadata.PostId == NativeTransferArchiveFixture.UnavailablePostId).Attachments.Single();
        Assert.Equal(TransferContentStatus.Unknown, unknown.Status);
        Assert.Null(unknown.PayloadSha256);
        Assert.Null(unknown.History);
    }

    private static async Task AssertVerifiedCopyAsync(NativeTransferImportStage stage, byte[] expected)
    {
        // Unpublished, caller-owned memory only; never a target store/original path.
        using MemoryStream destination = new();
        TransferMeasuredBlob measured = await stage.CopyPayloadToAsync(Hash(expected), destination);
        Assert.Equal(expected, destination.ToArray());
        Assert.Equal(new TransferMeasuredBlob(expected.LongLength, Hash(expected),
            Convert.ToBase64String(MD5.HashData(expected)), Convert.ToBase64String(SHA1.HashData(expected))), measured);
        Assert.Equal(stage.Package.Envelope.MeasuredPayloads[measured.Sha256], measured);
        Assert.True(destination.CanRead);
        Assert.True(destination.CanWrite);
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static Dictionary<string, byte[]> AuthoritativeFiles(NativeTransferArchiveFixture fixture) =>
        new[] { Path.Combine(fixture.ProfilePath, "archive.db"), Path.Combine(fixture.MediaDirectory, ".meowchan-manifest.json"),
            fixture.StrictCommit.Path, fixture.LegacyCommit.Path }.ToDictionary(path => path, File.ReadAllBytes, StringComparer.Ordinal);

    private static Dictionary<string, byte[]> ProfileFiles(string root, string staging) =>
        Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(path => !path.StartsWith(staging + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            .ToDictionary(path => path, File.ReadAllBytes, StringComparer.Ordinal);

    private static void AssertFilesEqual(Dictionary<string, byte[]> expected, Dictionary<string, byte[]> actual)
    {
        Assert.Equal(expected.Keys.Order(StringComparer.Ordinal), actual.Keys.Order(StringComparer.Ordinal));
        foreach ((string path, byte[] bytes) in expected)
            Assert.Equal(bytes, actual[path]);
    }
}
