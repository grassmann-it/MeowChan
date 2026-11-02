using System.Collections;
using System.Collections.Immutable;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MeowChan.Archive;
using MeowChan.Favorites;
using MeowChan.Interfaces;
using MeowChan.Persistence;
using MeowChan.Storage;
using MeowChan.Transfer;

namespace MeowChan.Tests.Transfer;

public sealed class NativeTransferPrivateWriterTests
{
    private static readonly ResourceId Thread = new("4chan", "b", "7001");
    private static readonly Guid PackageId = Guid.Parse("29106c80-d16d-4d1c-a345-861b6c85681b");
    private static readonly DateTimeOffset At = new(2026, 11, 1, 9, 0, 0, TimeSpan.Zero);
    private static readonly byte[] Original = Encoding.UTF8.GetBytes("synthetic private writer payload, never user media");
    private const string PrivateNote = "SYNTHETIC-PRIVATE-NOTE 雪 🐈";
    private const long ContainerCap = 2L * 1024 * 1024 * 1024;
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static TransferBlobDescriptor Descriptor => new(Original.Length, Hash(Original));
    private static TransferPayloadSource Payload(Func<Stream>? open = null) => new(Descriptor,
        _ => Task.FromResult(open?.Invoke() ?? new MemoryStream(Original, false)));

    private static NativeTransferCatalog Catalog(bool retained = false, bool omitted = false)
    {
        CapturedAttachment metadata = new(0, "7001001", "original", "https://i.4cdn.org/b/7001001.jpg", "synthetic.jpg");
        if (retained)
        {
            MediaProviderMetadata provider = new("7001001", "original", metadata.Url!, "synthetic.jpg", null, null, null);
            metadata = metadata with { AttachmentRevisionKey = MediaRevisionKeys.CreateAttachmentKey(Thread, "999", 0, provider) };
        }
        TransferAttachment attachment = new(metadata, TransferMediaRole.Original,
            omitted ? TransferContentStatus.Omitted : TransferContentStatus.Contained, omitted ? null : Hash(Original), null);
        CapturedPost post = new("1", 1, PostFieldPresence.All, At, "", null, "synthetic post", false, retained ? [] : [metadata]);
        TransferThread thread = new(Thread, "https://boards.4chan.org/b/thread/7001", "synthetic", ThreadSourceState.NotFound,
            At, At, 1, true, [new(post, retained ? [] : [attachment])]);
        if (retained) thread = thread with { RetainedBindings = [new("999", attachment)] };
        return new(At, [thread]);
    }

    private static NativePrivateMetadataSelection Selection(int bits) => new((bits & 1) != 0, (bits & 2) != 0, (bits & 4) != 0);
    private static NativePrivateMetadataProjection Projection() => new([Thread], new(true, true, true), [Thread],
        [new(Thread, "7001001", "original", Hash(Original))], [new(new(Thread, "1"), PrivateNote)]);
    private static NativePrivateMetadataProjection Empty(NativePrivateMetadataSelection selection) => new([Thread], selection, [], [], []);
    private static byte[] ReadEntry(ZipArchive zip, string name)
    {
        using Stream entry = Assert.IsType<ZipArchiveEntry>(zip.GetEntry(name)).Open();
        using MemoryStream bytes = new(); entry.CopyTo(bytes); return bytes.ToArray();
    }

    [Fact]
    public async Task NullPrivateArgumentPreservesExactLegacyV1BytesAndOldCallShape()
    {
        NativeTransferCatalog catalog = Catalog();
        using MemoryStream implicitNull = new(); using MemoryStream explicitNull = new();
        Assert.True((await NativeTransferPackageWriter.WriteAsync(implicitNull, catalog, [Payload()], PackageId, default)).IsComplete);
        TransferWriteResult result = await NativeTransferPackageWriter.WriteAsync(explicitNull, catalog, [Payload()], PackageId, default, null);
        Assert.True(result.IsComplete);
        Assert.Null(result.Package!.PrivateMetadata);
        Assert.Equal(LegacyPublicBytes(catalog), implicitNull.ToArray());
        Assert.Equal(implicitNull.ToArray(), explicitNull.ToArray());
        var originalBudget = NativeTransferPackageWriter.MeasureBudget(catalog, [Descriptor], PackageId, default);
        Assert.Equal(originalBudget, NativeTransferPackageWriter.MeasureBudget(catalog, [Descriptor], PackageId, default, null));
        Assert.Equal(0, originalBudget.PrivateMetadataBytes);
        Assert.Equal(implicitNull.Length, originalBudget.ContainerBytes);
        using ZipArchive zip = new(implicitNull, ZipArchiveMode.Read, leaveOpen: true);
        using JsonDocument manifest = JsonDocument.Parse(ReadEntry(zip, "manifest.json"));
        Assert.Equal(7, manifest.RootElement.EnumerateObject().Count());
        Assert.Equal(1, manifest.RootElement.GetProperty("envelopeVersion").GetInt32());
        Assert.Null(zip.GetEntry("private.json"));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task PrivateRoundtripIsDeterministicAndBindsCurrentOrRetainedContainedMedia(bool retained)
    {
        NativeTransferCatalog catalog = Catalog(retained);
        NativePrivateMetadataProjection projection = Projection();
        byte[] privateBytes = NativePrivateMetadataCodec.Serialize(projection);
        using MemoryStream first = new(); using MemoryStream second = new();
        TrackingInput input = new(Original);
        TransferWriteResult written = await NativeTransferPackageWriter.WriteAsync(first, catalog, [Payload(() => input)], PackageId, default, projection);
        Assert.True(written.IsComplete, written.Error.ToString());
        Assert.True(input.Disposed); Assert.True(first.CanRead); Assert.True(first.CanWrite);
        Assert.Equal(0, first.Position); Assert.Equal(first.Length, written.BytesWritten);
        Assert.Equal(privateBytes, NativePrivateMetadataCodec.Serialize(Assert.IsType<NativePrivateMetadataProjection>(written.Package!.PrivateMetadata)));
        Assert.True((await NativeTransferPackageWriter.WriteAsync(second, catalog, [Payload()], PackageId, default, projection)).IsComplete);
        Assert.Equal(first.ToArray(), second.ToArray());
        TransferPackageInspection inspected = await NativeTransferPackageValidator.InspectAsync(first);
        Assert.True(inspected.IsPackageValid);
        Assert.Equal(privateBytes, NativePrivateMetadataCodec.Serialize(Assert.IsType<NativePrivateMetadataProjection>(inspected.Package!.PrivateMetadata)));
        Assert.Equal(catalog.Threads[0].Posts.Length, inspected.Package.Catalog.Threads[0].Posts.Length);
        using ZipArchive zip = new(first, ZipArchiveMode.Read, leaveOpen: true);
        Assert.Equal(new[] { "manifest.json", "catalog.json", "private.json", "payload/" + Descriptor.Sha256 }, zip.Entries.Select(e => e.FullName).ToArray());
        Assert.Equal(privateBytes, ReadEntry(zip, "private.json"));
        byte[] publicBytes = ReadEntry(zip, "catalog.json");
        Assert.Equal(NativeTransferCatalogSerializer.Serialize(catalog), publicBytes);
        Assert.DoesNotContain("SYNTHETIC-PRIVATE-NOTE", Encoding.UTF8.GetString(publicBytes));
        using JsonDocument manifest = JsonDocument.Parse(ReadEntry(zip, "manifest.json"));
        JsonElement root = manifest.RootElement;
        Assert.Equal(new[] { "format", "envelopeVersion", "packageId", "contentSchema", "requiredFeatures", "catalog", "payloads", "privateMetadata" },
            root.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal(2, root.GetProperty("envelopeVersion").GetInt32());
        string schema = NativeTransferCatalogSerializer.ContentSchema(catalog);
        Assert.Equal(schema, root.GetProperty("contentSchema").GetString());
        Assert.Equal(new[] { schema, "meowchan-private-v1" }, root.GetProperty("requiredFeatures").EnumerateArray().Select(e => e.GetString()).ToArray());
        JsonElement descriptor = root.GetProperty("privateMetadata");
        Assert.Equal(new[] { "length", "sha256" }, descriptor.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal(privateBytes.Length, descriptor.GetProperty("length").GetInt64());
        Assert.Equal(Hash(privateBytes), descriptor.GetProperty("sha256").GetString());
        Assert.Single(root.GetProperty("payloads").EnumerateArray());
        Assert.DoesNotContain("SYNTHETIC-PRIVATE-NOTE", root.GetRawText());
        foreach (ZipArchiveEntry entry in zip.Entries)
        {
            Assert.Equal(entry.Length, entry.CompressedLength);
            Assert.Equal(new DateTime(1980, 1, 1), entry.LastWriteTime.DateTime);
        }
    }

    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    [InlineData(5)] [InlineData(6)] [InlineData(7)]
    public async Task SelectedEmptyCategoriesStillProducePrivateComponent(int bits)
    {
        NativePrivateMetadataProjection projection = Empty(Selection(bits));
        using MemoryStream stage = new();
        TransferWriteResult result = await NativeTransferPackageWriter.WriteAsync(stage, Catalog(omitted: true), [], PackageId, default, projection);
        Assert.True(result.IsComplete, result.Error.ToString());
        NativePrivateMetadataProjection received = Assert.IsType<NativePrivateMetadataProjection>(result.Package!.PrivateMetadata);
        Assert.Equal(projection.Selection, received.Selection);
        Assert.Empty(received.ThreadFavorites); Assert.Empty(received.MediaFavorites); Assert.Empty(received.PostBookmarksAndNotes);
        using ZipArchive zip = new(stage, ZipArchiveMode.Read, leaveOpen: true);
        Assert.Equal(3, zip.Entries.Count);
        using JsonDocument wire = JsonDocument.Parse(ReadEntry(zip, "private.json"));
        string[] categories = ["threadFavorites", "mediaFavorites", "postBookmarksAndNotes"];
        for (int i = 0; i < categories.Length; i++)
        {
            JsonElement category = wire.RootElement.GetProperty(categories[i]);
            if ((bits & (1 << i)) != 0) Assert.Empty(category.EnumerateArray());
            else Assert.Equal(JsonValueKind.Null, category.ValueKind);
        }
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task BudgetIncludesPrivateBytesAndExactlyOneExtraEntryWithBothFilenameHeaders(bool withPayload)
    {
        NativeTransferCatalog catalog = Catalog(omitted: !withPayload);
        NativePrivateMetadataProjection projection = withPayload ? Projection() : Empty(new(ThreadFavorites: true));
        TransferBlobDescriptor[] descriptors = withPayload ? [Descriptor] : [];
        var publicBudget = NativeTransferPackageWriter.MeasureBudget(catalog, descriptors, PackageId);
        var privateBudget = NativeTransferPackageWriter.MeasureBudget(catalog, descriptors, PackageId, default, projection);
        using MemoryStream stage = new();
        Assert.True((await NativeTransferPackageWriter.WriteAsync(stage, catalog, withPayload ? [Payload()] : [], PackageId, default, projection)).IsComplete);
        Assert.True(privateBudget.WithinContainerCap);
        Assert.Equal(stage.Length, privateBudget.ContainerBytes);
        Assert.Equal(NativePrivateMetadataCodec.Serialize(projection).Length, privateBudget.PrivateMetadataBytes);
        Assert.Equal(publicBudget.PayloadBytes, privateBudget.PayloadBytes);
        Assert.Equal(publicBudget.CatalogBytes, privateBudget.CatalogBytes);
        Assert.Equal(30 + 46 + 2 * 12, privateBudget.ZipOverhead - publicBudget.ZipOverhead);
        Assert.Equal(privateBudget.ContainerBytes, privateBudget.PayloadBytes + privateBudget.CatalogBytes
            + privateBudget.ManifestBytes + privateBudget.PrivateMetadataBytes + privateBudget.ZipOverhead);
        using ZipArchive zip = new(stage, ZipArchiveMode.Read, leaveOpen: true);
        Assert.Equal(ReadEntry(zip, "manifest.json").Length, privateBudget.ManifestBytes);
    }

    [Theory]
    [InlineData("default-off")] [InlineData("codec-presence")] [InlineData("codec-note")]
    [InlineData("thread-missing")] [InlineData("thread-extra")] [InlineData("thread-case")]
    [InlineData("post-missing")] [InlineData("media-id")] [InlineData("media-variant")]
    [InlineData("media-hash")] [InlineData("media-thread")] [InlineData("omitted")]
    [InlineData("retained-post-is-not-captured")]
    public async Task InvalidPrivateSelectionIsRejectedBeforeAnyOutputOrSourceOpen(string defect)
    {
        NativeTransferCatalog catalog = Catalog();
        NativePrivateMetadataProjection valid = Projection();
        NativePrivateMetadataProjection invalid = defect switch
        {
            "default-off" => Empty(default),
            "codec-presence" => new([Thread], default, [Thread], [], []),
            "codec-note" => new([Thread], new(PostBookmarksAndNotes: true), [], [], [new(new(Thread, "1"), "SYNTHETIC-PRIVATE-NOTE\rbroken")]),
            "thread-missing" => new([], new(ThreadFavorites: true), [], [], []),
            "thread-extra" => new([Thread, Thread with { Thread = "7002" }], new(ThreadFavorites: true), [], [], []),
            "thread-case" => new([Thread with { Board = "B" }], new(ThreadFavorites: true), [], [], []),
            "post-missing" => new([Thread], new(PostBookmarksAndNotes: true), [], [], [new(new(Thread, "absent"), PrivateNote)]),
            "media-id" => new([Thread], new(MediaFavorites: true), [], [valid.MediaFavorites[0] with { MediaIdentifier = "absent" }], []),
            "media-variant" => new([Thread], new(MediaFavorites: true), [], [valid.MediaFavorites[0] with { Variant = "preview" }], []),
            "media-hash" => new([Thread], new(MediaFavorites: true), [], [new(Thread, "7001001", "original", new string('a', 64))], []),
            "media-thread" => new([Thread], new(MediaFavorites: true), [], [new(Thread with { Thread = "7002" }, "7001001", "original", Hash(Original))], []),
            "retained-post-is-not-captured" => new([Thread], new(PostBookmarksAndNotes: true), [], [], [new(new(Thread, "999"), PrivateNote)]),
            _ => valid
        };
        if (defect == "omitted") catalog = Catalog(omitted: true);
        if (defect == "retained-post-is-not-captured") catalog = Catalog(retained: true);
        bool opened = false;
        using MemoryStream stage = new();
        TransferWriteResult result = await NativeTransferPackageWriter.WriteAsync(stage, catalog,
            [Payload(() => { opened = true; return new MemoryStream(Original, false); })], PackageId, default, invalid);
        Assert.Equal(TransferWriteError.InvalidSelection, result.Error);
        Assert.Null(result.Package); Assert.Null(result.BytesWritten);
        Assert.Equal(0, stage.Length); Assert.Equal(0, stage.Position); Assert.False(opened); Assert.True(stage.CanWrite);
        Assert.DoesNotContain("SYNTHETIC-PRIVATE-NOTE", result.ToString());
        InvalidDataException error = Assert.Throws<InvalidDataException>(() =>
            NativeTransferPackageWriter.MeasureBudget(catalog, [Descriptor], PackageId, default, invalid));
        Assert.DoesNotContain("SYNTHETIC-PRIVATE-NOTE", error.ToString());
    }

    [Theory]
    [InlineData("default-threads")] [InlineData("null-thread")] [InlineData("too-many-threads")]
    [InlineData("default-posts")] [InlineData("default-retained")] [InlineData("null-post")]
    [InlineData("null-post-metadata")] [InlineData("null-attachment-metadata")] [InlineData("too-many-posts")]
    public async Task PublicShapeAndBudgetsAreCheckedBeforePrivateBinding(string defect)
    {
        NativeTransferCatalog catalog = Catalog();
        TransferThread thread = catalog.Threads[0];
        TransferPost post = thread.Posts[0];
        catalog = defect switch
        {
            "default-threads" => catalog with { Threads = default },
            "null-thread" => catalog with { Threads = [null!] },
            "too-many-threads" => catalog with { Threads = Enumerable.Repeat(thread, 1001).ToImmutableArray() },
            "default-posts" => catalog with { Threads = [thread with { Posts = default }] },
            "default-retained" => catalog with { Threads = [thread with { RetainedBindings = default }] },
            "null-post" => catalog with { Threads = [thread with { Posts = [null!] }] },
            "null-post-metadata" => catalog with { Threads = [thread with { Posts = [post with { Metadata = null! }] }] },
            "null-attachment-metadata" => catalog with { Threads = [thread with { Posts =
                [post with { Attachments = [post.Attachments[0] with { Metadata = null! }] }] }] },
            "too-many-posts" => catalog with { Threads = [thread with { Posts = Enumerable.Repeat(post, 100001).ToImmutableArray() }] },
            _ => throw new InvalidOperationException()
        };
        // Also-invalid private input makes ordering observable: public invalidity
        // must win over the private default-off InvalidSelection result.
        NativePrivateMetadataProjection projection = Empty(default);
        bool opened = false;
        using MemoryStream stage = new();
        TransferWriteResult result = await NativeTransferPackageWriter.WriteAsync(stage, catalog,
            [Payload(() => { opened = true; return new MemoryStream(Original); })], PackageId, default, projection);
        Assert.Equal(TransferWriteError.InvalidCatalog, result.Error);
        Assert.Equal(0, stage.Length); Assert.False(opened); Assert.True(stage.CanWrite);
        InvalidDataException expected = Assert.Throws<InvalidDataException>(() => NativeTransferCatalogSerializer.Serialize(catalog));
        InvalidDataException actual = Assert.Throws<InvalidDataException>(() =>
            NativeTransferPackageWriter.MeasureBudget(catalog, [Descriptor], PackageId, default, projection));
        Assert.Equal(expected.Message, actual.Message);
    }

    [Fact]
    public void PublicOnlyOverBudgetMeasurementRetainsItsOriginalEarlyReturn()
    {
        NativeTransferCatalog malformed = Catalog() with { Threads = default };
        TransferBlobDescriptor[] descriptors =
        [new(ContainerCap / 2, new string('a', 64)), new(ContainerCap / 2, new string('b', 64)), new(1, new string('c', 64))];
        var result = NativeTransferPackageWriter.MeasureBudget(malformed, descriptors, PackageId);
        Assert.False(result.WithinContainerCap); Assert.Equal(long.MaxValue, result.ContainerBytes);
        Assert.Equal(0, result.CatalogBytes); Assert.Equal(0, result.PrivateMetadataBytes);
        Assert.Throws<InvalidDataException>(() => NativeTransferPackageWriter.MeasureBudget(malformed, descriptors,
            PackageId, default, Empty(new(ThreadFavorites: true))));
    }

    [Fact]
    public async Task PrivateEntryReservesOneOfTheExistingTenThousandEntrySlots()
    {
        NativePrivateMetadataProjection projection = Empty(new(ThreadFavorites: true));
        TransferBlobDescriptor[] maximum = Enumerable.Range(0, 9998)
            .Select(i => new TransferBlobDescriptor(0, i.ToString("x64"))).ToArray();
        Assert.True(NativeTransferPackageWriter.MeasureBudget(Catalog(omitted: true), maximum, PackageId).WithinContainerCap);
        Assert.True(NativeTransferPackageWriter.MeasureBudget(Catalog(omitted: true), maximum[..9997], PackageId, default, projection).WithinContainerCap);
        Assert.Throws<InvalidDataException>(() => NativeTransferPackageWriter.MeasureBudget(Catalog(omitted: true), maximum, PackageId, default, projection));
        using MemoryStream stage = new();
        TransferWriteResult result = await NativeTransferPackageWriter.WriteAsync(stage, Catalog(), new CountOnlySources(9998), PackageId, default, projection);
        Assert.Equal(TransferWriteError.InvalidSelection, result.Error); Assert.Equal(0, stage.Length);
    }

    [Fact]
    public async Task PrivateComponentCannotRaiseContainerCapAndIsChargedBeforeOpeningPayloads()
    {
        NativeTransferCatalog catalog = Catalog(omitted: true);
        NativePrivateMetadataProjection projection = Empty(new(ThreadFavorites: true));
        TransferBlobDescriptor[] descriptors = [new(ContainerCap / 2, new string('a', 64)), new(ContainerCap / 2, new string('b', 64))];
        var initial = NativeTransferPackageWriter.MeasureBudget(catalog, descriptors, PackageId);
        descriptors[1] = descriptors[1] with { Length = descriptors[1].Length - (initial.ContainerBytes - ContainerCap) };
        var publicBudget = NativeTransferPackageWriter.MeasureBudget(catalog, descriptors, PackageId);
        Assert.True(publicBudget.WithinContainerCap); Assert.Equal(ContainerCap, publicBudget.ContainerBytes);
        var measurement = NativeTransferPackageWriter.MeasureBudget(catalog, descriptors, PackageId, default, projection);
        Assert.False(measurement.WithinContainerCap); Assert.True(measurement.ContainerBytes > ContainerCap);
        Assert.True(measurement.PrivateMetadataBytes > 0);
        bool opened = false;
        TransferPayloadSource[] sources = descriptors.Select(d => new TransferPayloadSource(d,
            _ => { opened = true; throw new IOException("SYNTHETIC-PRIVATE-NOTE"); })).ToArray();
        using MemoryStream stage = new();
        TransferWriteResult result = await NativeTransferPackageWriter.WriteAsync(stage, catalog, sources, PackageId, default, projection);
        Assert.Equal(TransferWriteError.OutputLimit, result.Error); Assert.False(opened); Assert.Equal(0, stage.Length);
    }

    [Fact]
    public void BudgetRetainsItsSixPositionalMembers()
    {
        var budget = new NativeTransferPackageWriter.TransferPackageBudget(true, 10, 2, 3, 1, 4) { PrivateMetadataBytes = 7 };
        var (within, container, payload, catalog, manifest, overhead) = budget;
        Assert.True(within); Assert.Equal(10, container); Assert.Equal(2, payload);
        Assert.Equal(3, catalog); Assert.Equal(1, manifest); Assert.Equal(4, overhead); Assert.Equal(7, budget.PrivateMetadataBytes);
    }

    [Fact]
    public async Task PreCancelledPrivateWriteAndMeasurementTouchNeitherOutputNorSources()
    {
        using CancellationTokenSource cancel = new(); cancel.Cancel();
        using MemoryStream stage = new();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => NativeTransferPackageWriter.WriteAsync(stage, Catalog(),
            new CountOnlySources(1), PackageId, cancel.Token, Empty(default)));
        Assert.ThrowsAny<OperationCanceledException>(() => NativeTransferPackageWriter.MeasureBudget(Catalog(), [], PackageId, cancel.Token, Empty(default)));
        Assert.Equal(0, stage.Length); Assert.True(stage.CanWrite);
    }

    [Fact]
    public async Task CancellationDuringPrivateEntryWinsOverZipFinalizationFailure()
    {
        NativePrivateMetadataProjection projection = Projection();
        using CancellationTokenSource cancel = new();
        using CancelPrivateOutput stage = new(cancel, NativePrivateMetadataCodec.Serialize(projection));
        bool opened = false;
        OperationCanceledException error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            NativeTransferPackageWriter.WriteAsync(stage, Catalog(), [Payload(() => { opened = true; return new MemoryStream(Original); })],
                PackageId, cancel.Token, projection));
        Assert.Equal(cancel.Token, error.CancellationToken);
        Assert.True(stage.PrivateWriteAttempted); Assert.True(stage.FinalizationFailed); Assert.False(opened);
        Assert.True(stage.CanWrite); Assert.DoesNotContain("SYNTHETIC-PRIVATE-NOTE", error.Message);
    }

    [Fact]
    public async Task PostwriteValidationReadsSameCallerHandleAndRejectsTamperedPrivateEntry()
    {
        NativePrivateMetadataProjection projection = Projection();
        using TamperOnFlushOutput stage = new(NativePrivateMetadataCodec.Serialize(projection));
        TransferWriteResult result = await NativeTransferPackageWriter.WriteAsync(stage, Catalog(), [Payload()], PackageId, default, projection);
        Assert.True(stage.Tampered); Assert.True(stage.ReadCalls > 0);
        Assert.Equal(TransferWriteError.ValidationFailed, result.Error); Assert.Null(result.Package);
        Assert.Equal(stage.Length, result.BytesWritten); Assert.True(stage.CanRead); Assert.True(stage.CanWrite);
        Assert.DoesNotContain("SYNTHETIC-PRIVATE-NOTE", result.ToString());
    }

    [Fact]
    public async Task PrivateWriteDoesNotDisposeOrOverwriteNonemptyCallerOutput()
    {
        using MemoryStream stage = new(); stage.WriteByte(7);
        TransferWriteResult result = await NativeTransferPackageWriter.WriteAsync(stage, Catalog(), [], PackageId, default, Projection());
        Assert.Equal(TransferWriteError.OutputNotEmpty, result.Error);
        Assert.Equal(new byte[] { 7 }, stage.ToArray()); Assert.True(stage.CanWrite);
    }

    // Independent legacy framing oracle: strict seven-field v1 manifest and original entry order.
    private static byte[] LegacyPublicBytes(NativeTransferCatalog catalog)
    {
        byte[] publicBytes = NativeTransferCatalogSerializer.Serialize(catalog);
        byte[] manifest = Encoding.UTF8.GetBytes("{\"format\":\"meowchan-transfer\",\"envelopeVersion\":1,\"packageId\":\"" + PackageId.ToString("D")
            + "\",\"contentSchema\":\"chan-v1\",\"requiredFeatures\":[\"chan-v1\"],\"catalog\":{\"length\":" + publicBytes.Length
            + ",\"sha256\":\"" + Hash(publicBytes) + "\"},\"payloads\":[{\"length\":" + Original.Length + ",\"sha256\":\"" + Hash(Original) + "\"}]}");
        using MemoryStream output = new();
        using (ZipArchive zip = new(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            Add("manifest.json", manifest); Add("catalog.json", publicBytes); Add("payload/" + Hash(Original), Original);
            void Add(string name, byte[] bytes)
            {
                ZipArchiveEntry entry = zip.CreateEntry(name, CompressionLevel.NoCompression);
                entry.LastWriteTime = new(1980, 1, 1, 0, 0, 0, TimeSpan.Zero); entry.ExternalAttributes = 0;
                using Stream target = entry.Open(); target.Write(bytes);
            }
        }
        return output.ToArray();
    }

    private sealed class CountOnlySources(int count) : IReadOnlyList<TransferPayloadSource>
    {
        public int Count => count;
        public TransferPayloadSource this[int index] => throw new InvalidOperationException("Selection should be rejected before enumeration.");
        public IEnumerator<TransferPayloadSource> GetEnumerator() => throw new InvalidOperationException("Selection should be rejected before enumeration.");
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
    private sealed class TrackingInput(byte[] bytes) : MemoryStream(bytes, false)
    {
        internal bool Disposed { get; private set; }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }
    private sealed class CancelPrivateOutput(CancellationTokenSource cancel, byte[] privateBytes) : MemoryStream
    {
        internal bool PrivateWriteAttempted { get; private set; }
        internal bool FinalizationFailed { get; private set; }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (buffer.Span.SequenceEqual(privateBytes))
            {
                PrivateWriteAttempted = true; cancel.Cancel();
                return ValueTask.FromException(new OperationCanceledException(cancellationToken));
            }
            return base.WriteAsync(buffer, cancellationToken);
        }
        public override void Write(byte[] buffer, int offset, int count) { Check(); base.Write(buffer, offset, count); }
        public override void Write(ReadOnlySpan<byte> buffer) { Check(); base.Write(buffer); }
        private void Check()
        {
            if (cancel.IsCancellationRequested) { FinalizationFailed = true; throw new IOException("SYNTHETIC-PRIVATE-NOTE finalization"); }
        }
    }
    private sealed class TamperOnFlushOutput(byte[] privateBytes) : MemoryStream
    {
        internal bool Tampered { get; private set; }
        internal int ReadCalls { get; private set; }
        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            byte[] bytes = ToArray(); int offset = bytes.AsSpan().IndexOf(privateBytes);
            Assert.True(offset >= 0);
            long position = Position; Position = offset; WriteByte((byte)'!'); Position = position; Tampered = true;
            return Task.CompletedTask;
        }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        { ReadCalls++; return base.ReadAsync(buffer, cancellationToken); }
    }
}
