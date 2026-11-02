using System.Security.Cryptography;
using MeowChan.Archive;
using MeowChan.Interfaces;
using MeowChan.Persistence;
using MeowChan.Transfer;

namespace MeowChan.Tests.Transfer;

public sealed class NativeTransferArchiveExporterTests
{
    private static readonly ResourceId Id = new("4chan", "b", "7001");
    private static readonly DateTimeOffset At = new(2026, 11, 1, 9, 0, 0, TimeSpan.Zero);
    private static readonly Guid PackageId = Guid.Parse("7601965c-5360-453e-bc6e-b649da14e2d4");
    private static CapturedThreadSnapshot Capture(string media, string title = "synthetic", string? html = "<p>synthetic &amp; offline</p>") =>
        new(Id, "https://boards.4chan.org/b/thread/7001", title, ThreadSourceState.NotFound, At, media,
            [new("7001", 0, PostFieldPresence.Html | PostFieldPresence.Attachments, Html: html,
                Attachments: [new(0, "7001001", Url: "https://i.4cdn.org/b/7001001.jpg", OriginalFileName: "synthetic.jpg", PlannedPath: Path.Combine(media, "private-local-only.jpg"))])]);

    [Fact]
    public async Task Real404ArchiveWritesOfflineMetadataPackageAndRemainsUnchangedAfterReopen()
    {
        using TempDirectory profile = new(); using ProfileLease lease = ProfileLease.Acquire(profile.Path);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(profile.Path, lease);
        CapturedThreadSnapshot capture = Capture(Path.Combine(profile.Path, "never-created-media"));
        await catalog.CaptureAsync(capture);
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(profile.Path);
        ArchivedThreadView before = (await reader.ReadThreadAsync(Id))!;
        TransferArchiveFreezeResult frozen = await NativeTransferArchiveExporter.FreezeAsync(reader, Id, At, PackageId, includeOriginals: false);
        Assert.True(frozen.IsFrozen, frozen.Error.ToString());
        using MemoryStream stage = new();
        TransferArchiveWriteResult result = await NativeTransferArchiveExporter.WriteAsync(frozen.Snapshot!, stage);
        Assert.True(result.IsComplete, result.Error.ToString());
        Assert.Equal(stage.Length, result.BytesWritten); Assert.Equal(0, stage.Position); Assert.True(stage.CanWrite);
        TransferThread thread = result.Package!.Catalog.Threads.Single();
        Assert.Equal(ThreadSourceState.NotFound, thread.SourceState); Assert.Null(thread.DecimalPostOrder);
        Assert.Equal(before.Summary.LastCapturedAt, thread.CapturedAtUtc); Assert.Equal(before.Summary.Revision, thread.CaptureRevision);
        Assert.Equal(TransferContentStatus.Omitted, thread.Posts[0].Attachments[0].Status);
        Assert.Null(thread.Posts[0].Attachments[0].Metadata.PlannedPath);
        Assert.True((await NativeTransferPackageValidator.InspectAsync(stage)).IsPackageValid);
        Assert.False(Directory.Exists(capture.MediaDirectory)); // not even media-directory discovery for omitted originals
        await using ArchiveReader reopened = await ArchiveReader.OpenAsync(profile.Path);
        ArchivedThreadView after = (await reopened.ReadThreadAsync(Id))!;
        Assert.Equal(before.Summary, after.Summary); Assert.Equal(before.Posts[0].Html, after.Posts[0].Html);
        Assert.Equal(before.Posts[0].Attachments![0], after.Posts[0].Attachments![0]);
        Assert.DoesNotContain(profile.Path, frozen.Snapshot!.ToString());
    }
    [Fact]
    public async Task MissingManifestIsUnknownNotInventedUnavailableOrContained()
    {
        using TempDirectory profile = new(); using ProfileLease lease = ProfileLease.Acquire(profile.Path);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(profile.Path, lease);
        await catalog.CaptureAsync(Capture(Path.Combine(profile.Path, "missing")));
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(profile.Path);
        TransferArchiveFreezeResult frozen = await NativeTransferArchiveExporter.FreezeAsync(reader, Id, At, PackageId);
        Assert.True(frozen.IsFrozen); Assert.Empty(frozen.Snapshot!.Payloads);
        Assert.Equal(TransferContentStatus.Unknown, frozen.Snapshot.Catalog.Threads[0].Posts[0].Attachments[0].Status);
        using MemoryStream stage = new(); Assert.True((await NativeTransferArchiveExporter.WriteAsync(frozen.Snapshot, stage)).IsComplete);
    }
    [Fact]
    public async Task UnsafeMetadataRejectsBeforeMediaManifestAccessAndDoesNotEchoSecret()
    {
        using TempDirectory profile = new(); using ProfileLease lease = ProfileLease.Acquire(profile.Path);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(profile.Path, lease);
        string media = Path.Combine(profile.Path, "corrupt-media"); Directory.CreateDirectory(media);
        await File.WriteAllTextAsync(Path.Combine(media, ".meowchan-manifest.json"), "not a manifest");
        await catalog.CaptureAsync(Capture(media, html: "<script>SECRET</script>"));
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(profile.Path);
        TransferArchiveFreezeResult result = await NativeTransferArchiveExporter.FreezeAsync(reader, Id, At, PackageId);
        Assert.Equal(TransferArchiveError.InvalidCatalog, result.Error); Assert.Null(result.Snapshot);
        Assert.DoesNotContain("SECRET", result.ToString()); Assert.DoesNotContain(profile.Path, result.ToString());
    }
    [Theory]
    [InlineData("preview")] [InlineData("thumbnail")] [InlineData("ORIGINAL")] [InlineData("source-specific-variant")]
    public async Task UnknownVariantIsNeverGuessedToBeAnOriginalOrPreview(string variant)
    {
        using TempDirectory profile = new(); using ProfileLease lease = ProfileLease.Acquire(profile.Path);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(profile.Path, lease);
        CapturedThreadSnapshot capture = Capture(Path.Combine(profile.Path, "media"));
        CapturedPost post = capture.Posts[0] with { Attachments = [capture.Posts[0].Attachments![0] with { Variant = variant }] };
        await catalog.CaptureAsync(capture with { Posts = [post] });
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(profile.Path);
        TransferArchiveFreezeResult result = await NativeTransferArchiveExporter.FreezeAsync(reader, Id, At, PackageId);
        Assert.Equal(TransferArchiveError.InvalidArchive, result.Error); Assert.Null(result.Snapshot);
    }
    [Fact]
    public async Task CaptureChangeBeforeWriteRejectsWithoutTouchingStageOrReplanning()
    {
        using TempDirectory profile = new(); using ProfileLease lease = ProfileLease.Acquire(profile.Path);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(profile.Path, lease);
        CapturedThreadSnapshot capture = Capture(Path.Combine(profile.Path, "media")); await catalog.CaptureAsync(capture);
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(profile.Path);
        NativeTransferArchiveSnapshot snapshot = (await NativeTransferArchiveExporter.FreezeAsync(reader, Id, At, PackageId, false)).Snapshot!;
        await catalog.CaptureAsync(capture with { Title = "changed after snapshot", CapturedAt = At.AddMinutes(1) });
        using MemoryStream stage = new();
        TransferArchiveWriteResult result = await NativeTransferArchiveExporter.WriteAsync(snapshot, stage);
        Assert.Equal(TransferArchiveError.StaleSnapshot, result.Error); Assert.Null(result.Package); Assert.Equal(0, stage.Length);
        Assert.Equal("synthetic", snapshot.Catalog.Threads[0].Title); // no silent replan
    }
    [Fact]
    public async Task CaptureChangeDuringWritingRejectsEvenAValidStageAndPreservesSourceChange()
    {
        using TempDirectory profile = new(); using ProfileLease lease = ProfileLease.Acquire(profile.Path);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(profile.Path, lease);
        CapturedThreadSnapshot capture = Capture(Path.Combine(profile.Path, "media")); await catalog.CaptureAsync(capture);
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(profile.Path);
        NativeTransferArchiveSnapshot snapshot = (await NativeTransferArchiveExporter.FreezeAsync(reader, Id, At, PackageId, false)).Snapshot!;
        using OnWriteStream stage = new(() => catalog.CaptureAsync(capture with { Title = "local concurrent change", CapturedAt = At.AddMinutes(1) }).GetAwaiter().GetResult());
        TransferArchiveWriteResult result = await NativeTransferArchiveExporter.WriteAsync(snapshot, stage);
        Assert.Equal(TransferArchiveError.StaleSnapshot, result.Error); Assert.Null(result.Package); Assert.Null(result.BytesWritten);
        Assert.True(stage.Length > 0); Assert.True(stage.CanWrite);
        Assert.True((await NativeTransferPackageValidator.InspectAsync(stage)).IsPackageValid); // still MUST NOT publish
        Assert.Equal("local concurrent change", (await reader.ReadThreadAsync(Id))!.Summary.Title);
    }
    [Fact]
    public async Task PrecancellationAndObservedWriteCancellationRemainCancellation()
    {
        using TempDirectory profile = new(); using ProfileLease lease = ProfileLease.Acquire(profile.Path);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(profile.Path, lease);
        await catalog.CaptureAsync(Capture(Path.Combine(profile.Path, "media")));
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(profile.Path);
        using CancellationTokenSource cancelled = new(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => NativeTransferArchiveExporter.FreezeAsync(reader, Id, At, PackageId, false, cancelled.Token));
        NativeTransferArchiveSnapshot snapshot = (await NativeTransferArchiveExporter.FreezeAsync(reader, Id, At, PackageId, false)).Snapshot!;
        using CancellationTokenSource during = new(); using OnWriteStream stage = new(during.Cancel);
        OperationCanceledException error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => NativeTransferArchiveExporter.WriteAsync(snapshot, stage, during.Token));
        Assert.Equal(during.Token, error.CancellationToken); Assert.True(stage.CanWrite);
    }
    [Fact]
    public async Task UnknownThreadCannotBecomeAnEmptySuccessfulPackage()
    {
        using TempDirectory profile = new(); using ProfileLease lease = ProfileLease.Acquire(profile.Path);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(profile.Path, lease);
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(profile.Path);
        TransferArchiveFreezeResult result = await NativeTransferArchiveExporter.FreezeAsync(reader, Id, At, PackageId);
        Assert.Equal(TransferArchiveError.MissingThread, result.Error); Assert.Null(result.Snapshot);
    }
    private sealed class OnWriteStream(Action once) : MemoryStream
    {
        private Action? _once = once;
        private void Before() { Action? current = Interlocked.Exchange(ref _once, null); current?.Invoke(); }
        public override void Write(byte[] buffer, int offset, int count) { Before(); base.Write(buffer, offset, count); }
        public override void Write(ReadOnlySpan<byte> buffer) { Before(); base.Write(buffer); }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        { Before(); return base.WriteAsync(buffer, cancellationToken); }
    }
}
