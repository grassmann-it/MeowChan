using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using MeowChan.Transfer;

namespace MeowChan.Tests.Transfer;

public sealed class NativeTransferEnvelopeTests
{
    private static readonly byte[] Catalog = Encoding.UTF8.GetBytes("{\"threads\":[]}");
    private static readonly byte[] Payload = Encoding.UTF8.GetBytes("synthetic original bytes; not a personal media file");
    private const string PackageId = "30e89da7-0742-4142-bbab-56f7c59e318b";
    private static string Hash(byte[] value) => Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();
    private static JsonObject Descriptor(byte[] value) => new() { ["length"] = value.Length, ["sha256"] = Hash(value) };
    private static JsonObject Manifest(byte[]? payload = null) => new()
    {
        ["format"] = "meowchan-transfer", ["envelopeVersion"] = 1, ["packageId"] = PackageId,
        ["contentSchema"] = "chan-v1", ["requiredFeatures"] = new JsonArray("chan-v1"),
        ["catalog"] = Descriptor(Catalog), ["payloads"] = new JsonArray(Descriptor(payload ?? Payload))
    };
    private static MemoryStream Package(string? manifest = null, byte[]? payload = null,
        Action<ZipArchive>? extra = null, CompressionLevel compression = CompressionLevel.NoCompression)
    {
        MemoryStream stream = new();
        using (ZipArchive zip = new(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            Add(zip, "manifest.json", Encoding.UTF8.GetBytes(manifest ?? Manifest(payload).ToJsonString()), compression);
            Add(zip, "catalog.json", Catalog, compression);
            Add(zip, "payload/" + Hash(payload ?? Payload), payload ?? Payload, compression);
            extra?.Invoke(zip);
        }
        stream.Position = 0;
        return stream;
    }
    private static void Add(ZipArchive zip, string name, byte[] bytes, CompressionLevel compression)
    {
        ZipArchiveEntry entry = zip.CreateEntry(name, compression);
        using Stream target = entry.Open();
        target.Write(bytes);
    }
    private static async Task AssertError(MemoryStream package, TransferEnvelopeError expected, TransferEnvelopeLimits? limits = null)
    {
        package.Position = 7;
        byte[] before = package.ToArray();
        TransferEnvelopeInspection result = await NativeTransferEnvelopeValidator.InspectAsync(package, limits: limits);
        Assert.False(result.IsEnvelopeValid);
        Assert.Null(result.Envelope);
        Assert.Equal(expected, result.Error);
        Assert.Equal(7, package.Position);
        Assert.True(package.CanRead);
        Assert.Equal(before, package.ToArray());
    }

    [Fact]
    public async Task InspectsIntegrityOnlyAndLeavesCallerStreamUnchanged()
    {
        using MemoryStream package = Package();
        package.Position = 7;
        byte[] before = package.ToArray();
        TransferEnvelopeInspection result = await NativeTransferEnvelopeValidator.InspectAsync(package);
        Assert.True(result.IsEnvelopeValid);
        NativeTransferEnvelope envelope = Assert.IsType<NativeTransferEnvelope>(result.Envelope);
        Assert.Equal(PackageId, envelope.PackageId);
        Assert.Equal(Hash(Catalog), envelope.Catalog.Sha256);
        Assert.Equal(Hash(Payload), Assert.Single(envelope.Payloads).Sha256);
        Assert.Equal(7, package.Position);
        Assert.Equal(before, package.ToArray());
        Assert.True(package.CanRead);
        Assert.Equal(envelope.Fingerprint, (await NativeTransferEnvelopeValidator.InspectAsync(package)).Envelope!.Fingerprint);
    }

    [Fact]
    public async Task SamePackageIdDoesNotHideChangedContent()
    {
        using MemoryStream first = Package();
        using MemoryStream second = Package(payload: Encoding.UTF8.GetBytes("different synthetic payload"));
        NativeTransferEnvelope a = (await NativeTransferEnvelopeValidator.InspectAsync(first)).Envelope!;
        NativeTransferEnvelope b = (await NativeTransferEnvelopeValidator.InspectAsync(second)).Envelope!;
        Assert.Equal(a.PackageId, b.PackageId);
        Assert.NotEqual(a.Fingerprint, b.Fingerprint);
    }

    [Fact]
    public async Task CatalogIsNotSemanticallyApprovedByEnvelopeIntegrity()
    {
        // An opaque catalog is deliberately not an import grant. The caller must run the
        // separate catalog validator before exporter admission or import staging.
        byte[] notCatalog = Encoding.UTF8.GetBytes("not even JSON, only envelope-integrity data");
        JsonObject manifest = Manifest(); manifest["catalog"] = Descriptor(notCatalog);
        using MemoryStream package = new();
        using (ZipArchive zip = new(package, ZipArchiveMode.Create, leaveOpen: true))
        {
            Add(zip, "manifest.json", Encoding.UTF8.GetBytes(manifest.ToJsonString()), CompressionLevel.NoCompression);
            Add(zip, "catalog.json", notCatalog, CompressionLevel.NoCompression);
            Add(zip, "payload/" + Hash(Payload), Payload, CompressionLevel.NoCompression);
        }
        package.Position = 0;
        Assert.True((await NativeTransferEnvelopeValidator.InspectAsync(package)).IsEnvelopeValid);
    }

    [Theory]
    [InlineData("../SECRET")]
    [InlineData("/tmp/SECRET")]
    [InlineData("C:\\SECRET")]
    [InlineData("\\\\server\\SECRET")]
    [InlineData("payload/../SECRET")]
    [InlineData("payload/CON")]
    [InlineData("payload/abc:SECRET")]
    [InlineData("payload/")]
    [InlineData("PAYLOAD/a")]
    [InlineData("manifest.json/SECRET")]
    public async Task RejectsNoncanonicalEntriesWithoutEchoingNames(string name)
    {
        using MemoryStream package = Package(extra: zip => Add(zip, name, [], CompressionLevel.NoCompression));
        await AssertError(package, TransferEnvelopeError.InvalidEntry);
        TransferEnvelopeInspection result = await NativeTransferEnvelopeValidator.InspectAsync(package);
        Assert.DoesNotContain("SECRET", result.ToString());
        Assert.DoesNotContain(name, result.ToString());
    }

    [Fact]
    public async Task RejectsDuplicateEntriesAndMissingOrExtraReferences()
    {
        using MemoryStream duplicate = Package(extra: zip => Add(zip, "catalog.json", Catalog, CompressionLevel.NoCompression));
        await AssertError(duplicate, TransferEnvelopeError.DuplicateEntry);
        using MemoryStream extra = Package(extra: zip => Add(zip, "payload/" + Hash([1, 2, 3]), [1, 2, 3], CompressionLevel.NoCompression));
        await AssertError(extra, TransferEnvelopeError.InvalidReference);
        JsonObject manifest = Manifest(); manifest["payloads"]![0]!["sha256"] = new string('0', 64);
        using MemoryStream missing = Package(manifest.ToJsonString());
        await AssertError(missing, TransferEnvelopeError.InvalidReference);
        manifest = Manifest(); manifest["payloads"]!.AsArray().Add(Descriptor(Payload));
        using MemoryStream repeated = Package(manifest.ToJsonString());
        await AssertError(repeated, TransferEnvelopeError.InvalidReference);
    }

    [Theory]
    [InlineData(0xA000 << 16)]
    [InlineData(0x4000 << 16)]
    [InlineData(0x10)]
    [InlineData(0x400)]
    public async Task RejectsLinksDirectoriesAndReparseAttributes(int attributes)
    {
        using MemoryStream package = Package();
        using (ZipArchive zip = new(package, ZipArchiveMode.Update, leaveOpen: true))
            zip.GetEntry("catalog.json")!.ExternalAttributes = attributes;
        await AssertError(package, TransferEnvelopeError.InvalidEntry);
    }

    [Fact]
    public async Task HashAndLengthAreMeasuredNotTrusted()
    {
        JsonObject manifest = Manifest(); manifest["catalog"]!["sha256"] = new string('0', 64);
        using MemoryStream wrongHash = Package(manifest.ToJsonString());
        await AssertError(wrongHash, TransferEnvelopeError.HashMismatch);
        manifest = Manifest(); manifest["payloads"]![0]!["length"] = Payload.Length + 1;
        using MemoryStream wrongLength = Package(manifest.ToJsonString());
        await AssertError(wrongLength, TransferEnvelopeError.LengthMismatch);
    }

    [Fact]
    public async Task UnknownMissingDuplicateAndCredentialManifestFieldsAreRejectedWithoutEcho()
    {
        JsonObject manifest = Manifest(); manifest["policy"] = new JsonObject { ["token"] = "SECRET" };
        using MemoryStream policy = Package(manifest.ToJsonString());
        await AssertError(policy, TransferEnvelopeError.InvalidManifest);
        Assert.DoesNotContain("SECRET", (await NativeTransferEnvelopeValidator.InspectAsync(policy)).ToString());
        manifest = Manifest(); manifest.Remove("packageId");
        using MemoryStream missing = Package(manifest.ToJsonString());
        await AssertError(missing, TransferEnvelopeError.InvalidManifest);
        string text = Manifest().ToJsonString().Replace("\"envelopeVersion\":1", "\"envelopeVersion\":1,\"envelopeVersion\":1");
        using MemoryStream duplicate = Package(text);
        await AssertError(duplicate, TransferEnvelopeError.InvalidManifest);
        using MemoryStream rootArray = Package("[]");
        await AssertError(rootArray, TransferEnvelopeError.InvalidManifest);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("true")]
    [InlineData("\"1\"")]
    [InlineData("[]")]
    [InlineData("{}")]
    public async Task WrongVersionJsonKindCannotEscapeAsException(string value)
    {
        string text = Manifest().ToJsonString().Replace("\"envelopeVersion\":1", "\"envelopeVersion\":" + value);
        using MemoryStream package = Package(text);
        await AssertError(package, TransferEnvelopeError.UnsupportedVersion);
    }

    [Fact]
    public async Task VersionsAndMandatoryFeaturesFailClosed()
    {
        JsonObject manifest = Manifest(); manifest["envelopeVersion"] = 2;
        using MemoryStream version = Package(manifest.ToJsonString());
        await AssertError(version, TransferEnvelopeError.UnsupportedVersion);
        manifest = Manifest(); manifest["requiredFeatures"] = new JsonArray("chan-v1", "future-required");
        using MemoryStream feature = Package(manifest.ToJsonString());
        await AssertError(feature, TransferEnvelopeError.UnsupportedFeature);
    }

    [Fact]
    public async Task LowerBudgetsAreEnforcedAndSafetyCapsCannotBeRaised()
    {
        using MemoryStream package = Package();
        await AssertError(package, TransferEnvelopeError.ContainerLimit, new() { ContainerBytes = 10 });
        await AssertError(package, TransferEnvelopeError.EntryLimit, new() { Entries = 2 });
        await AssertError(package, TransferEnvelopeError.DirectoryLimit, new() { DirectoryBytes = 10 });
        await AssertError(package, TransferEnvelopeError.ManifestLimit, new() { ManifestBytes = 10 });
        await AssertError(package, TransferEnvelopeError.PayloadLimit, new() { CatalogBytes = 1 });
        await AssertError(package, TransferEnvelopeError.PayloadLimit, new() { PayloadBytes = 1 });
        await AssertError(package, TransferEnvelopeError.PayloadLimit, new() { TotalBytes = 1 });
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => NativeTransferEnvelopeValidator.InspectAsync(package,
            limits: new() { Entries = 10_001 }));
    }

    [Fact]
    public async Task UnsupportedCompressionIsRejectedBeforeHashingPayload()
    {
        using MemoryStream package = Package(payload: new byte[1024 * 1024], compression: CompressionLevel.SmallestSize);
        await AssertError(package, TransferEnvelopeError.UnsupportedZip);
    }

    [Fact]
    public async Task CentralCountAndSizeAreBoundedBeforeEntryAllocation()
    {
        using MemoryStream package = Package();
        byte[] bytes = package.ToArray();
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(bytes.Length - 14), 20_000);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(bytes.Length - 12), 20_000);
        using MemoryStream forged = new(bytes);
        await AssertError(forged, TransferEnvelopeError.EntryLimit);
    }

    [Fact]
    public async Task CancellationPropagatesAndRestoresPosition()
    {
        using MemoryStream package = Package();
        using CancellationTokenSource cts = new(); cts.Cancel();
        package.Position = 7;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => NativeTransferEnvelopeValidator.InspectAsync(package, cts.Token));
        Assert.Equal(7, package.Position);
    }

    [Fact]
    public async Task RejectsNonseekableInputWithoutReadingIt()
    {
        using Nonseekable input = new();
        TransferEnvelopeInspection result = await NativeTransferEnvelopeValidator.InspectAsync(input);
        Assert.Equal(TransferEnvelopeError.UnreadableStream, result.Error);
        Assert.Equal(0, input.Reads);
    }
    [Theory]
    [InlineData(4)] // version
    [InlineData(6)] // encryption/flags
    [InlineData(8)] // method
    [InlineData(14)] // CRC
    [InlineData(18)] // compressed size
    [InlineData(22)] // expanded size
    [InlineData(28)] // extra length
    [InlineData(30)] // local name only (central name remains canonical)
    public async Task ContradictoryLocalHeadersCannotHideBehindSafeCentralRecords(int offset)
    {
        using MemoryStream original = Package();
        byte[] bytes = original.ToArray(); bytes[offset] ^= 1;
        using MemoryStream forged = new(bytes);
        await AssertError(forged, TransferEnvelopeError.InvalidZip);
    }

    [Fact]
    public async Task StoredHiddenSuffixAndCentralOverlapsAreRejected()
    {
        using MemoryStream original = Package();
        byte[] bytes = original.ToArray();
        int directory = (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(bytes.Length - 6));
        // Both local and central short-length claims agree, but leave hidden data before the next header.
        uint length = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(18));
        foreach (int at in new[] { 18, 22, directory + 20, directory + 24 })
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(at), length - 1);
        using MemoryStream suffix = new(bytes);
        await AssertError(suffix, TransferEnvelopeError.InvalidZip);
        bytes = original.ToArray();
        int second = directory + 46 + "manifest.json".Length;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(second + 42), 0);
        using MemoryStream overlap = new(bytes);
        await AssertError(overlap, TransferEnvelopeError.InvalidZip);
    }

    [Fact]
    public async Task CrcMustBeConsistentWithBytesEvenWhenShaBindingIsValid()
    {
        using MemoryStream original = Package();
        byte[] bytes = original.ToArray();
        int directory = (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(bytes.Length - 6));
        bytes[14] ^= 1; bytes[directory + 16] ^= 1; // matching local/central CRC lies
        using MemoryStream forged = new(bytes);
        await AssertError(forged, TransferEnvelopeError.InvalidZip);
        TransferCrc32 crc = new(); crc.Add(Encoding.ASCII.GetBytes("123456789"));
        Assert.Equal(0xcbf43926u, crc.Value); // standard IEEE CRC32 golden vector
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(4u)]
    public async Task DeflatedPrefixAndZeroLengthLiesNeverBecomeValidEnvelopes(uint declaredLength)
    {
        // The prefix-hash attack is rejected as unsupported compression BEFORE decoder/JSON access.
        using MemoryStream original = Package(compression: CompressionLevel.SmallestSize);
        byte[] bytes = original.ToArray();
        int directory = (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(bytes.Length - 6));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(22), declaredLength);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(directory + 24), declaredLength);
        using MemoryStream forged = new(bytes);
        await AssertError(forged, TransferEnvelopeError.UnsupportedZip);
    }

    [Theory]
    [InlineData(0x08)] // data descriptor
    [InlineData(0x01)] // encrypted
    [InlineData(0x40)] // strong encryption
    public async Task UnsupportedCentralFlagsAreRejected(int flags)
    {
        using MemoryStream original = Package();
        byte[] bytes = original.ToArray();
        int directory = (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(bytes.Length - 6));
        bytes[directory + 8] |= (byte)flags;
        using MemoryStream forged = new(bytes);
        await AssertError(forged, TransferEnvelopeError.UnsupportedZip);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RestorationIoFailureReturnsNoEnvelopeAndNeverEchoesRawMessages(bool malformed)
    {
        using MemoryStream package = Package();
        byte[] bytes = package.ToArray(); if (malformed) bytes[0] ^= 1;
        using FaultingStream input = new(bytes) { FailRestore = true };
        TransferEnvelopeInspection result = await NativeTransferEnvelopeValidator.InspectAsync(input);
        Assert.Equal(TransferEnvelopeError.ReadFailure, result.Error);
        Assert.Null(result.Envelope);
        Assert.DoesNotContain("SECRET", result.ToString());
        Assert.False(input.WasDisposed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MidReadCancellationWinsOverRestorationFailure(bool failRestore)
    {
        using MemoryStream package = Package();
        using CancellationTokenSource cts = new();
        using FaultingStream input = new(package.ToArray()) { FailRestore = failRestore, CancelAfterRead = cts };
        OperationCanceledException error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            NativeTransferEnvelopeValidator.InspectAsync(input, cts.Token));
        Assert.DoesNotContain("SECRET", error.ToString());
        Assert.False(input.WasDisposed);
        if (!failRestore) Assert.Equal(7, input.Position);
    }

    [Theory]
    [InlineData("capability")]
    [InlineData("length")]
    [InlineData("read")]
    [InlineData("seek")]
    public async Task StreamFaultsAreSanitized(string fault)
    {
        using MemoryStream package = Package();
        using FaultingStream input = new(package.ToArray()) { Fault = fault };
        TransferEnvelopeInspection result = await NativeTransferEnvelopeValidator.InspectAsync(input);
        Assert.Equal(TransferEnvelopeError.ReadFailure, result.Error);
        Assert.Null(result.Envelope);
        Assert.DoesNotContain("SECRET", result.ToString());
        Assert.False(input.WasDisposed);
    }

    [Theory]
    [InlineData("\\uD800", false)]
    [InlineData("\\uDC00", false)]
    [InlineData("\\uD800", true)]
    [InlineData("\\uDC00", true)]
    public async Task MalformedUnicodeEscapesReturnInvalidManifest(string escape, bool property)
    {
        string manifest = Manifest().ToJsonString();
        // Inline data is a literal JSON escape, not a .NET unpaired-surrogate string.
        string text = property ? manifest.Replace("\"packageId\"", "\"" + escape + "\"")
            : manifest.Replace(PackageId, escape);
        using MemoryStream package = PackageWithRawManifest(Encoding.UTF8.GetBytes(text));
        await AssertError(package, TransferEnvelopeError.InvalidManifest);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MalformedUtf8InsideJsonValueOrNameReturnsInvalidManifest(bool property)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(Manifest().ToJsonString());
        byte[] marker = Encoding.UTF8.GetBytes(property ? "packageId" : PackageId);
        int offset = bytes.AsSpan().IndexOf(marker); Assert.True(offset >= 0);
        bytes[offset] = 0xff;
        using MemoryStream package = PackageWithRawManifest(bytes);
        await AssertError(package, TransferEnvelopeError.InvalidManifest);
    }

    private static MemoryStream PackageWithRawManifest(byte[] manifest)
    {
        MemoryStream stream = new();
        using (ZipArchive zip = new(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            Add(zip, "manifest.json", manifest, CompressionLevel.NoCompression);
            Add(zip, "catalog.json", Catalog, CompressionLevel.NoCompression);
            Add(zip, "payload/" + Hash(Payload), Payload, CompressionLevel.NoCompression);
        }
        stream.Position = 0; return stream;
    }


    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task AccessDeniedReadOrRestoreIsSanitizedAndCancellationWins(bool cancel)
    {
        using MemoryStream package = Package(); using CancellationTokenSource cancellation = new();
        using FaultingStream input = new(package.ToArray()) { AccessDenied = true,
            Fault = cancel ? null : "read", FailRestore = cancel, CancelAfterRead = cancel ? cancellation : null };
        if (cancel)
        {
            OperationCanceledException error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                NativeTransferEnvelopeValidator.InspectAsync(input, cancellation.Token));
            Assert.Equal(cancellation.Token, error.CancellationToken); Assert.DoesNotContain("SECRET", error.Message);
        }
        else
        {
            TransferEnvelopeInspection result = await NativeTransferEnvelopeValidator.InspectAsync(input);
            Assert.Equal(TransferEnvelopeError.ReadFailure, result.Error); Assert.Null(result.Envelope);
            Assert.DoesNotContain("SECRET", result.ToString());
        }
        Assert.False(input.WasDisposed);
    }
    private sealed class FaultingStream(byte[] bytes) : Stream
    {
        private readonly MemoryStream inner = new(bytes) { Position = 7 };
        private int reads;
        internal bool FailRestore;
        internal bool AccessDenied;
        internal bool WasDisposed;
        internal string? Fault;
        internal CancellationTokenSource? CancelAfterRead;
        private Exception Failure() => AccessDenied ? new UnauthorizedAccessException("SECRET access path") : new IOException("SECRET synthetic IO message must never escape");
        public override bool CanRead => Fault == "capability" ? throw Failure() : true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => Fault == "length" ? throw Failure() : inner.Length;
        public override long Position
        {
            get => inner.Position;
            set { if (Fault == "seek" || FailRestore && reads > 0 && value == 7) throw Failure(); inner.Position = value; }
        }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (Fault == "read") throw Failure();
            int result = await inner.ReadAsync(buffer, ct); reads++; CancelAfterRead?.Cancel(); return result;
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { WasDisposed = true; if (disposing) inner.Dispose(); base.Dispose(disposing); }
    }
    private sealed class Nonseekable : Stream
    {
        internal int Reads;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) { Reads++; throw new IOException("SECRET"); }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
