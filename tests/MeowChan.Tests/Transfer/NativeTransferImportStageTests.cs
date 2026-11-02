using System.Collections.Immutable;
using System.Security.Cryptography;
using MeowChan.Archive;
using MeowChan.Interfaces;
using MeowChan.Storage;
using MeowChan.Testing;
using MeowChan.Transfer;

namespace MeowChan.Tests.Transfer;

// Functional happy paths for the owned import stage. Every package comes from the real writer;
// all files and bytes are synthetic and live under this fixture's own GUID temporary directory.
public sealed class NativeTransferImportStageTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 1, 9, 0, 0, TimeSpan.Zero);
    private static readonly ResourceId Id = new("4chan", "b", "7001");
    private static readonly Guid PackageId = Guid.Parse("b73ef6b0-bd2d-44e3-aee0-016f06a39c75");
    private static readonly byte[] Original = Payload(150_123, 17);
    private static readonly byte[] Preview = Payload(53, 31);
    private static readonly byte[] Retained = Payload(257, 47);

    [Fact]
    public async Task TextOnlyPackageKeepsPassive404MetadataAndProducesAnEmptyMediaPreview()
    {
        NativeTransferCatalog catalog = Catalog(Thread(Post()));
        using Fixture fixture = await Fixture.CreateAsync(catalog);
        using FileStream source = fixture.OpenSource();
        NativeTransferStageResult result = await NativeTransferImportStage.CreateAsync(source, fixture.StagingDirectory);
        await using NativeTransferImportStage stage = SuccessfulStage(result);

        AssertMatchesWriter(fixture, stage.Package);
        Assert.Equal(source.Length, source.Position);
        TransferThread thread = Assert.Single(stage.Package.Catalog.Threads);
        Assert.Equal(Id, thread.Id);
        Assert.Equal(ThreadSourceState.NotFound, thread.SourceState);
        Assert.Equal(19L, thread.CaptureRevision);
        Assert.True(thread.DecimalPostOrder);
        CapturedPost post = Assert.Single(thread.Posts).Metadata;
        Assert.Equal("15", post.PostId);
        Assert.Equal(long.MaxValue, post.Ordinal);
        Assert.Equal(PostFieldPresence.All, post.Fields);
        Assert.Null(post.PostedAt);
        Assert.Null(post.Author);
        Assert.Equal("", post.Subject);
        Assert.Equal("<b>synthetic text</b><br>&gt;&gt;20", post.Html);
        Assert.Empty(post.Attachments!);
        Assert.Empty(stage.Package.Envelope.Payloads);
        Assert.Empty(stage.Package.Envelope.MeasuredPayloads);

        TransferImportPreviewInspection preview = NativeTransferImportPreview.Create(stage.Package);
        Assert.True(preview.HasPreview, preview.Error.ToString());
        Assert.Equal(1, preview.Preview!.Posts);
        Assert.Equal(0, preview.Preview.UniquePayloads);
        Assert.Equal(0L, preview.Preview.UniquePayloadBytes);
        Assert.Equal(0, preview.Preview.CurrentOriginals.Total);
        Assert.Equal(0, preview.Preview.CurrentPreviews.Total);
        Assert.Equal(0, preview.Preview.RetainedBindings.Total);
    }

    [Fact]
    public async Task OriginalPreviewAndRetainedPayloadsCopyExactlyIncludingRepeatedOutOfOrderCopies()
    {
        using Fixture fixture = await Fixture.CreateAsync(MediaCatalog(), Original, Preview, Retained);
        using FileStream source = fixture.OpenSource();
        await using NativeTransferImportStage stage = SuccessfulStage(
            await NativeTransferImportStage.CreateAsync(source, fixture.StagingDirectory));

        AssertMatchesWriter(fixture, stage.Package);
        Assert.Equal("chan-v2", stage.Package.Envelope.ContentSchema);
        Assert.Equal(3, stage.Package.Envelope.Payloads.Length);
        TransferThread thread = Assert.Single(stage.Package.Catalog.Threads);
        Assert.Equal(new[] { TransferMediaRole.Original, TransferMediaRole.Preview },
            Assert.Single(thread.Posts).Attachments.Select(a => a.Role));
        TransferRetainedBinding retained = Assert.Single(thread.RetainedBindings);
        Assert.Equal("999", retained.PostId);
        Assert.Equal("strict", retained.Attachment.History!.Kind);
        Assert.Equal(At, retained.Attachment.History.AcceptedAtUtc);
        Assert.Equal(Hash(Retained), retained.Attachment.PayloadSha256);

        // A multi-buffer original, a tiny preview and a different retained entry exercise offsets
        // rather than accidentally relying on the last ZIP read/copy position.
        foreach (byte[] expected in new[] { Retained, Original, Preview, Original, Retained, Preview })
            await AssertCopy(stage, expected);
        Assert.True(source.CanRead);
        Assert.Equal(source.Length, source.Position);
    }

    [Fact]
    public async Task PreviewOnlyPackageDoesNotPromoteAnOmittedOriginal()
    {
        TransferAttachment omitted = Media(0, "111", Original) with
        {
            Status = TransferContentStatus.Omitted,
            PayloadSha256 = null,
            History = null
        };
        NativeTransferCatalog catalog = Catalog(Thread(Post(omitted,
            Media(1, "112", Preview, TransferMediaRole.Preview))));
        using Fixture fixture = await Fixture.CreateAsync(catalog, Preview);
        using FileStream source = fixture.OpenSource();
        await using NativeTransferImportStage stage = SuccessfulStage(
            await NativeTransferImportStage.CreateAsync(source, fixture.StagingDirectory));

        AssertMatchesWriter(fixture, stage.Package);
        TransferPost post = Assert.Single(Assert.Single(stage.Package.Catalog.Threads).Posts);
        Assert.Equal(TransferContentStatus.Omitted, post.Attachments[0].Status);
        Assert.Null(post.Attachments[0].PayloadSha256);
        Assert.Equal(TransferMediaRole.Preview, post.Attachments[1].Role);
        Assert.Equal(Hash(Preview), Assert.Single(stage.Package.Envelope.Payloads).Sha256);
        TransferImportPreviewInspection preview = NativeTransferImportPreview.Create(stage.Package);
        Assert.True(preview.HasPreview, preview.Error.ToString());
        Assert.Equal(0, preview.Preview!.CurrentOriginals.Contained);
        Assert.Equal(1, preview.Preview.CurrentOriginals.Omitted);
        Assert.Equal(1, preview.Preview.CurrentPreviews.Contained);
        await AssertCopy(stage, Preview);
    }

    [Fact]
    public async Task SharedPayloadPreservesCurrentRolesAndRetainedRevisionWithoutInventingAPost()
    {
        TransferAttachment historical = Media(7, "113", Original, postId: "999");
        NativeTransferCatalog catalog = Catalog(Thread(Post(Media(0, "111", Original),
            Media(1, "112", Original, TransferMediaRole.Preview))) with
        {
            RetainedBindings = [new("999", historical)]
        });
        using Fixture fixture = await Fixture.CreateAsync(catalog, Original);
        using FileStream source = fixture.OpenSource();
        await using NativeTransferImportStage stage = SuccessfulStage(
            await NativeTransferImportStage.CreateAsync(source, fixture.StagingDirectory));

        AssertMatchesWriter(fixture, stage.Package);
        TransferThread thread = Assert.Single(stage.Package.Catalog.Threads);
        Assert.Equal("15", Assert.Single(thread.Posts).Metadata.PostId);
        Assert.Equal(historical.Metadata.AttachmentRevisionKey,
            Assert.Single(thread.RetainedBindings).Attachment.Metadata.AttachmentRevisionKey);
        TransferAttachment[] occurrences = [.. thread.Posts[0].Attachments,
            thread.RetainedBindings[0].Attachment];
        Assert.Equal(3, occurrences.Length);
        Assert.All(occurrences, a => Assert.Equal(Hash(Original), a.PayloadSha256));
        Assert.Equal(3, occurrences.Select(a => a.Metadata.AttachmentRevisionKey).Distinct().Count());
        Assert.Single(stage.Package.Envelope.Payloads);
        Assert.Single(stage.Package.Envelope.MeasuredPayloads);

        TransferImportPreviewInspection preview = NativeTransferImportPreview.Create(stage.Package);
        Assert.True(preview.HasPreview, preview.Error.ToString());
        Assert.Equal(1, preview.Preview!.Posts);
        Assert.Equal(1, preview.Preview.CurrentOriginals.Contained);
        Assert.Equal(1, preview.Preview.CurrentPreviews.Contained);
        Assert.Equal(1, preview.Preview.RetainedBindings.Contained);
        Assert.Equal(1, preview.Preview.UniquePayloads);
        Assert.Equal((long)Original.Length, preview.Preview.UniquePayloadBytes);
        foreach (TransferAttachment occurrence in occurrences)
            await AssertCopy(stage, Original, occurrence.PayloadSha256);
    }

    [Fact]
    public async Task MutatingCallerSourceAfterCreateCannotChangeMetadataOrLaterPayloadCopies()
    {
        using Fixture fixture = await Fixture.CreateAsync(MediaCatalog(), Original, Preview, Retained);
        using FileStream source = fixture.OpenSource();
        await using NativeTransferImportStage stage = SuccessfulStage(
            await NativeTransferImportStage.CreateAsync(source, fixture.StagingDirectory));
        byte[] catalogBefore = stage.Package.Envelope.CatalogBytes.ToArray();
        string fingerprintBefore = stage.Package.Envelope.Fingerprint;

        source.SetLength(0);
        source.Position = 0;
        await source.WriteAsync(new byte[] { 9, 8, 7, 6, 5, 4, 3, 2 });
        await source.FlushAsync();
        source.Position = 3;
        foreach (byte[] expected in new[] { Original, Preview, Retained, Original })
            await AssertCopy(stage, expected);

        AssertMatchesWriter(fixture, stage.Package);
        Assert.Equal(catalogBefore, stage.Package.Envelope.CatalogBytes.ToArray());
        Assert.Equal(fingerprintBefore, stage.Package.Envelope.Fingerprint);
        Assert.Equal(3L, source.Position);
        Assert.Equal(8L, source.Length);
    }

    [Fact]
    public async Task CallerCanDisposeAndRemoveSourceBeforeUsingTheStage()
    {
        using Fixture fixture = await Fixture.CreateAsync(MediaCatalog(), Original, Preview, Retained);
        FileStream source = fixture.OpenSource();
        NativeTransferStageResult result;
        using (source)
            result = await NativeTransferImportStage.CreateAsync(source, fixture.StagingDirectory);
        await using NativeTransferImportStage stage = SuccessfulStage(result);
        Assert.False(source.CanRead);
        File.Delete(fixture.SourcePath);
        Assert.False(File.Exists(fixture.SourcePath));

        AssertMatchesWriter(fixture, stage.Package);
        foreach (byte[] expected in new[] { Preview, Retained, Original, Preview })
            await AssertCopy(stage, expected);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadsExactlyOnceFromCurrentPositionToEofWithoutRequiringSeek(bool nonSeekable)
    {
        using Fixture fixture = await Fixture.CreateAsync(MediaCatalog(), Original, Preview, Retained);
        byte[] prefix = [71, 72, 73, 74, 75];
        string prefixedPath = Path.Combine(fixture.Root, "caller-prefixed-input.bin");
        byte[] prefixedBytes = [.. prefix, .. fixture.PackageBytes];
        await File.WriteAllBytesAsync(prefixedPath, prefixedBytes);
        using FileStream inner = File.OpenRead(prefixedPath);
        inner.Position = prefix.Length;
        using ObservedReadStream source = new(inner, nonSeekable);
        await using NativeTransferImportStage stage = SuccessfulStage(
            await NativeTransferImportStage.CreateAsync(source, fixture.StagingDirectory));

        Assert.Equal((long)fixture.PackageBytes.Length, source.BytesRead);
        Assert.Equal(inner.Length, inner.Position); // no restoration to the caller's starting position
        Assert.False(source.Disposed);
        Assert.True(source.CanRead);
        source.RejectFurtherReads = true;
        AssertMatchesWriter(fixture, stage.Package);
        await AssertCopy(stage, Original);
        await AssertCopy(stage, Retained);
        Assert.Equal((long)fixture.PackageBytes.Length, source.BytesRead);
        await stage.DisposeAsync();
        Assert.False(source.Disposed);
        Assert.True(source.CanRead);
    }

    [Fact]
    public async Task DestinationRemainsCallerOwnedAndCopyStartsAtItsCurrentPosition()
    {
        using Fixture fixture = await Fixture.CreateAsync(Catalog(Thread(Post(Media(0, "111", Original)))), Original);
        using FileStream source = fixture.OpenSource();
        await using NativeTransferImportStage stage = SuccessfulStage(
            await NativeTransferImportStage.CreateAsync(source, fixture.StagingDirectory));
        // An unpublished caller file in the very same working directory is not stage-owned.
        string destinationPath = Path.Combine(fixture.StagingDirectory, "caller-unpublished.pending");
        byte[] prefix = [61, 62, 63];
        using (FileStream destination = new(destinationPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
        {
            await destination.WriteAsync(prefix);
            TransferMeasuredBlob measured = await stage.CopyPayloadToAsync(Hash(Original), destination);
            AssertMeasured(Original, measured);
            Assert.True(destination.CanWrite);
            Assert.True(destination.CanRead);
            Assert.Equal(prefix.Length + (long)Original.Length, destination.Position);
            await stage.DisposeAsync();
            Assert.False(stage.StageRetained);
            Assert.True(destination.CanWrite);
            destination.WriteByte(99);
            Assert.True(source.CanRead);
        }
        byte[] expectedDestination = [.. prefix, .. Original, 99];
        Assert.Equal(expectedDestination, await File.ReadAllBytesAsync(destinationPath));
    }

    [Fact]
    public async Task DisposeRemovesOnlyItsOwnDirectPendingFileAndLeavesAllCallerFilesUntouched()
    {
        using Fixture fixture = await Fixture.CreateAsync(MediaCatalog(), Original, Preview, Retained);
        string nested = Directory.CreateDirectory(Path.Combine(fixture.StagingDirectory, "caller-subtree")).FullName;
        string[] callerPaths = [fixture.SourcePath,
            Path.Combine(fixture.StagingDirectory, ".meowchan-import-caller.pending"),
            Path.Combine(nested, "caller.pending"), Path.Combine(fixture.Root, "caller-sibling.pending")];
        foreach (string path in callerPaths.Skip(1))
            await File.WriteAllBytesAsync(path, new byte[] { 23, 42, 81 });
        Dictionary<string, byte[]> before = callerPaths.ToDictionary(path => path, File.ReadAllBytes);
        string[] entriesBefore = Directory.GetFileSystemEntries(fixture.StagingDirectory).Order().ToArray();
        using FileStream source = fixture.OpenSource();
        NativeTransferStageResult result = await NativeTransferImportStage.CreateAsync(source, fixture.StagingDirectory);
        await using NativeTransferImportStage stage = SuccessfulStage(result);

        string ownedPath = Assert.Single(Directory.GetFileSystemEntries(fixture.StagingDirectory).Except(entriesBefore));
        Assert.True(File.Exists(ownedPath));
        Assert.Equal(".pending", Path.GetExtension(ownedPath));
        Assert.Equal((long)fixture.PackageBytes.Length, new FileInfo(ownedPath).Length);
        Assert.Equal(new[] { nested }, Directory.GetDirectories(fixture.StagingDirectory));
        await AssertCopy(stage, Original);
        await stage.DisposeAsync();

        Assert.False(stage.StageRetained);
        Assert.False(result.StageRetained);
        Assert.False(File.Exists(ownedPath));
        Assert.True(Directory.Exists(fixture.StagingDirectory));
        Assert.Equal(entriesBefore, Directory.GetFileSystemEntries(fixture.StagingDirectory).Order().ToArray());
        Assert.True(source.CanRead);
        Assert.True(source.CanWrite);
        source.Dispose(); // release caller handle before independent file reads (also on Windows)
        foreach ((string path, byte[] expected) in before)
            Assert.Equal(expected, await File.ReadAllBytesAsync(path));
    }

    private static NativeTransferImportStage SuccessfulStage(NativeTransferStageResult result)
    {
        Assert.Equal(NativeTransferStageError.None, result.Error);
        Assert.Equal(TransferEnvelopeError.None, result.EnvelopeError);
        Assert.Equal(TransferCatalogError.None, result.CatalogError);
        Assert.False(result.StageRetained);
        NativeTransferImportStage stage = Assert.IsType<NativeTransferImportStage>(result.Stage);
        Assert.False(stage.StageRetained);
        return stage;
    }

    private static void AssertMatchesWriter(Fixture fixture, NativeTransferPackage package)
    {
        Assert.Equal(fixture.Expected.Envelope.PackageId, package.Envelope.PackageId);
        Assert.Equal(fixture.Expected.Envelope.ContentSchema, package.Envelope.ContentSchema);
        Assert.Equal(fixture.Expected.Envelope.Fingerprint, package.Envelope.Fingerprint);
        Assert.Equal(fixture.Expected.Envelope.Catalog, package.Envelope.Catalog);
        Assert.Equal(fixture.Expected.Envelope.CatalogBytes.ToArray(), package.Envelope.CatalogBytes.ToArray());
        Assert.Equal(NativeTransferCatalogSerializer.Serialize(fixture.Expected.Catalog),
            NativeTransferCatalogSerializer.Serialize(package.Catalog));
        Assert.Equal(fixture.Expected.Envelope.Payloads.ToArray(), package.Envelope.Payloads.ToArray());
        Assert.Equal(fixture.Expected.Envelope.MeasuredPayloads.Count, package.Envelope.MeasuredPayloads.Count);
        foreach ((string hash, TransferMeasuredBlob measured) in fixture.Expected.Envelope.MeasuredPayloads)
            Assert.Equal(measured, package.Envelope.MeasuredPayloads[hash]);
    }

    private static async Task AssertCopy(NativeTransferImportStage stage, byte[] expected, string? sha256 = null)
    {
        using MemoryStream destination = new();
        TransferMeasuredBlob measured = await stage.CopyPayloadToAsync(sha256 ?? Hash(expected), destination);
        AssertMeasured(expected, measured);
        Assert.Equal(expected, destination.ToArray());
        Assert.True(destination.CanRead);
        Assert.True(destination.CanWrite);
        destination.WriteByte(101); // success must not dispose the caller's destination
    }

    private static void AssertMeasured(byte[] expected, TransferMeasuredBlob measured)
    {
        Assert.Equal((long)expected.Length, measured.Length);
        Assert.Equal(Hash(expected), measured.Sha256);
        Assert.Equal(Convert.ToBase64String(MD5.HashData(expected)), measured.Md5);
        Assert.Equal(Convert.ToBase64String(SHA1.HashData(expected)), measured.Sha1);
    }

    private static byte[] Payload(int length, int seed) => Enumerable.Range(0, length)
        .Select(i => (byte)((i * 31 + seed) % 251)).ToArray();
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static TransferAttachment Media(int position, string mediaId, byte[] bytes,
        TransferMediaRole role = TransferMediaRole.Original, string postId = "15")
    {
        MediaProviderMetadata provider = new(mediaId, role == TransferMediaRole.Preview ? "preview" : "original",
            $"https://i.4cdn.org/b/{mediaId}.jpg", $"synthetic-{mediaId}.jpg", bytes.Length,
            Convert.ToBase64String(MD5.HashData(bytes)), Hash(bytes))
        { ExpectedSha1 = Convert.ToBase64String(SHA1.HashData(bytes)) };
        CapturedAttachment metadata = new(position, mediaId, provider.Variant, provider.Url,
            provider.OriginalFileName, ExpectedSize: provider.ExpectedSize, ExpectedMd5: provider.ExpectedMd5)
        {
            ExpectedSha1 = provider.ExpectedSha1,
            ExpectedSha256 = provider.ExpectedSha256,
            AttachmentRevisionKey = MediaRevisionKeys.CreateAttachmentKey(Id, postId, position, provider)
        };
        return new(metadata, role, TransferContentStatus.Contained, Hash(bytes), new("strict", At, false, false));
    }
    private static TransferPost Post(params TransferAttachment[] attachments) => new(
        new("15", long.MaxValue, PostFieldPresence.All, Subject: "", Html: "<b>synthetic text</b><br>&gt;&gt;20",
            Attachments: attachments.Select(a => a.Metadata).ToImmutableArray()), attachments.ToImmutableArray());
    private static TransferThread Thread(params TransferPost[] posts) => new(Id,
        "https://boards.4chan.org/b/thread/7001", "synthetic thread", ThreadSourceState.NotFound,
        At, At, 19, true, posts.ToImmutableArray());
    private static NativeTransferCatalog Catalog(params TransferThread[] threads) => new(At, threads.ToImmutableArray());
    private static NativeTransferCatalog MediaCatalog() => Catalog(Thread(Post(Media(0, "111", Original),
        Media(1, "112", Preview, TransferMediaRole.Preview))) with
    { RetainedBindings = [new("999", Media(7, "113", Retained, postId: "999"))] });

    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(TemporaryWorkspacePaths.TempRoot,
            "meowchan-native-import-stage-tests-" + Guid.NewGuid().ToString("N"));
        internal string StagingDirectory => Path.Combine(Root, "staging");
        internal string SourcePath => Path.Combine(Root, "caller-input.meowchan");
        internal NativeTransferPackage Expected { get; private set; } = null!;
        internal byte[] PackageBytes { get; private set; } = [];

        internal static async Task<Fixture> CreateAsync(NativeTransferCatalog catalog, params byte[][] payloads)
        {
            Fixture fixture = new();
            try
            {
                Directory.CreateDirectory(fixture.StagingDirectory);
                using (FileStream output = new(fixture.SourcePath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
                {
                    TransferPayloadSource[] sources = payloads.Select(bytes => new TransferPayloadSource(
                        new(bytes.Length, Hash(bytes)), _ => Task.FromResult<Stream>(new MemoryStream(bytes, false)))).ToArray();
                    TransferWriteResult written = await NativeTransferPackageWriter.WriteAsync(output, catalog, sources, PackageId);
                    Assert.True(written.IsComplete, written.Error.ToString());
                    fixture.Expected = Assert.IsType<NativeTransferPackage>(written.Package);
                }
                fixture.PackageBytes = await File.ReadAllBytesAsync(fixture.SourcePath);
                return fixture;
            }
            catch { fixture.Dispose(); throw; }
        }

        internal FileStream OpenSource() => new(SourcePath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        // Fixture teardown owns this entire GUID directory. Product cleanup is separately asserted
        // above to remove only its own file, never recursively traverse the caller's working folder.
        public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true); }
    }

    private sealed class ObservedReadStream(Stream inner, bool nonSeekable) : Stream
    {
        internal long BytesRead { get; private set; }
        internal bool Disposed { get; private set; }
        internal bool RejectFurtherReads { get; set; }
        public override bool CanRead => !Disposed && inner.CanRead;
        public override bool CanSeek => !nonSeekable && inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => nonSeekable ? throw new NotSupportedException() : inner.Length;
        public override long Position
        {
            get => nonSeekable ? throw new NotSupportedException() : inner.Position;
            set { if (nonSeekable) throw new NotSupportedException(); inner.Position = value; }
        }
        public override int Read(byte[] buffer, int offset, int count)
        {
            CheckRead();
            int read = inner.Read(buffer, offset, Math.Min(count, 137));
            BytesRead += read;
            return read;
        }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            CheckRead();
            int read = await inner.ReadAsync(buffer[..Math.Min(buffer.Length, 137)], cancellationToken);
            BytesRead += read;
            return read;
        }
        private void CheckRead()
        {
            if (RejectFurtherReads) throw new InvalidOperationException("Stage must not reread the caller's source.");
        }
        public override long Seek(long offset, SeekOrigin origin) => nonSeekable
            ? throw new NotSupportedException() : inner.Seek(offset, origin);
        public override void Flush() => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
