using System.Buffers.Binary;
using System.Collections.Immutable;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using MeowChan.Transfer;

namespace MeowChan.Tests.Transfer;

// Synthetic framing only: neither the package writer nor semantic/package admission
// participates. In particular, opaque bytes here are not a valid private projection.
public sealed class NativeTransferPrivateEnvelopeTests
{
    private const string PackageId = "29106c80-d16d-4d1c-a345-861b6c85681b";
    private static readonly byte[] Catalog = Encoding.UTF8.GetBytes("synthetic opaque public catalog");
    private static readonly byte[] Payload = [1, 2, 3, 4];
    private static readonly byte[] Private = Encoding.UTF8.GetBytes("SYNTHETIC-PRIVATE-SECRET framing bytes, not a semantic private document");
    private const int PrivateCap = 16 * 1024 * 1024;

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static JsonObject Blob(byte[] bytes) => new() { ["length"] = bytes.Length, ["sha256"] = Hash(bytes) };
    private static JsonObject Manifest(int version = 2, string schema = "chan-v1", byte[]? privateBytes = null, bool payload = true)
    {
        JsonObject manifest = new()
        {
            ["format"] = "meowchan-transfer", ["envelopeVersion"] = version, ["packageId"] = PackageId,
            ["contentSchema"] = schema,
            ["requiredFeatures"] = version == 2 ? new JsonArray(schema, "meowchan-private-v1") : new JsonArray(schema),
            ["catalog"] = Blob(Catalog), ["payloads"] = payload ? new JsonArray(Blob(Payload)) : new JsonArray()
        };
        if (version == 2) manifest["privateMetadata"] = Blob(privateBytes ?? Private);
        return manifest;
    }
    private static MemoryStream Package(JsonObject? manifest = null, byte[]? privateBytes = null,
        bool includePrivate = true, bool includePayload = true, Action<ZipArchive>? extra = null,
        string? rawManifest = null, CompressionLevel privateCompression = CompressionLevel.NoCompression)
    {
        MemoryStream stream = new();
        using (ZipArchive zip = new(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            Add(zip, "manifest.json", Encoding.UTF8.GetBytes(rawManifest ?? (manifest ?? Manifest()).ToJsonString()));
            Add(zip, "catalog.json", Catalog);
            if (includePrivate) Add(zip, "private.json", privateBytes ?? Private, privateCompression);
            if (includePayload) Add(zip, "payload/" + Hash(Payload), Payload);
            extra?.Invoke(zip);
        }
        stream.Position = 0;
        return stream;
    }
    private static void Add(ZipArchive zip, string name, byte[] bytes, CompressionLevel compression = CompressionLevel.NoCompression)
    {
        ZipArchiveEntry entry = zip.CreateEntry(name, compression);
        entry.LastWriteTime = new(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
        entry.ExternalAttributes = 0;
        using Stream output = entry.Open();
        output.Write(bytes);
    }
    private static async Task AssertError(Stream package, TransferEnvelopeError error, TransferEnvelopeLimits? limits = null)
    {
        long original = package.Position;
        TransferEnvelopeInspection inspection = await NativeTransferEnvelopeValidator.InspectAsync(package, limits: limits);
        Assert.Equal(error, inspection.Error);
        Assert.Null(inspection.Envelope);
        Assert.False(inspection.IsEnvelopeValid);
        Assert.DoesNotContain("SECRET", inspection.ToString());
        Assert.Equal(original, package.Position);
        Assert.True(package.CanRead);
    }

    [Fact]
    public void NewPropertiesDefaultToPublicOnlyV1AndIndependentBoundedPrivateLimit()
    {
        NativeTransferEnvelope envelope = new(PackageId, "chan-v1", "", new(0, Hash([])), []);
        Assert.Equal(1, envelope.EnvelopeVersion);
        Assert.Null(envelope.PrivateMetadata);
        Assert.False(envelope.PrivateMetadataBytes.IsDefault);
        Assert.Empty(envelope.PrivateMetadataBytes);
        Assert.Equal(PrivateCap, new TransferEnvelopeLimits().PrivateMetadataBytes);
    }

    [Theory]
    [InlineData("chan-v1")]
    [InlineData("chan-v2")]
    [InlineData("chan-v3")]
    public async Task V2CapturesExactSeparatelyBoundBytesWithoutSemanticAdmission(string schema)
    {
        JsonObject manifest = Manifest(schema: schema);
        using MemoryStream package = Package(manifest);
        package.Position = 7;
        TransferEnvelopeInspection inspection = await NativeTransferEnvelopeValidator.InspectAsync(package);
        Assert.True(inspection.IsEnvelopeValid);
        NativeTransferEnvelope envelope = Assert.IsType<NativeTransferEnvelope>(inspection.Envelope);
        Assert.Equal(2, envelope.EnvelopeVersion);
        Assert.Equal(schema, envelope.ContentSchema);
        Assert.Equal(PackageId, envelope.PackageId);
        Assert.Equal(new TransferBlobDescriptor(Private.Length, Hash(Private)), envelope.PrivateMetadata);
        Assert.Equal(Private, envelope.PrivateMetadataBytes.ToArray());
        Assert.Equal(Catalog, envelope.CatalogBytes.ToArray());
        Assert.Equal(Hash(Encoding.UTF8.GetBytes(manifest.ToJsonString())), envelope.Fingerprint);
        Assert.Equal(new TransferBlobDescriptor(Payload.Length, Hash(Payload)), Assert.Single(envelope.Payloads));
        Assert.Equal(Hash(Payload), Assert.Single(envelope.MeasuredPayloads).Key);
        Assert.False(envelope.MeasuredPayloads.ContainsKey(Hash(Private)));
        Assert.Equal(7, package.Position);
        Assert.True(package.CanWrite);
        // Captured bytes do not alias the caller's mutable backing buffer.
        package.GetBuffer()[checked((int)Offsets(package.ToArray(), "private.json").Data)] ^= 1;
        Assert.Equal(Private, envelope.PrivateMetadataBytes.ToArray());
    }

    [Theory]
    [InlineData("chan-v1")]
    [InlineData("chan-v2")]
    [InlineData("chan-v3")]
    public async Task ExistingSevenFieldPublicOnlyV1KeepsItsFeaturesAndBytes(string schema)
    {
        JsonObject manifest = Manifest(1, schema);
        using MemoryStream package = Package(manifest, includePrivate: false);
        TransferEnvelopeInspection inspection = await NativeTransferEnvelopeValidator.InspectAsync(package);
        Assert.True(inspection.IsEnvelopeValid);
        NativeTransferEnvelope envelope = inspection.Envelope!;
        Assert.Equal(1, envelope.EnvelopeVersion);
        Assert.Null(envelope.PrivateMetadata);
        Assert.Empty(envelope.PrivateMetadataBytes);
        Assert.False(envelope.PrivateMetadataBytes.IsDefault);
        Assert.Equal(Catalog, envelope.CatalogBytes.ToArray());
        Assert.Equal(Hash(Encoding.UTF8.GetBytes(manifest.ToJsonString())), envelope.Fingerprint);
        Assert.Single(envelope.Payloads);
        Assert.Single(envelope.MeasuredPayloads);
    }

    [Fact]
    public async Task PrivateComponentDoesNotRequireAnyMediaPayloadAndMayHaveZeroFramingBytes()
    {
        using MemoryStream package = Package(Manifest(privateBytes: [], payload: false), privateBytes: [], includePayload: false);
        TransferEnvelopeInspection inspection = await NativeTransferEnvelopeValidator.InspectAsync(package);
        Assert.True(inspection.IsEnvelopeValid); // The semantic codec must reject an empty document later.
        Assert.Equal(2, inspection.Envelope!.EnvelopeVersion);
        Assert.Equal(new TransferBlobDescriptor(0, Hash([])), inspection.Envelope.PrivateMetadata);
        Assert.Empty(inspection.Envelope.PrivateMetadataBytes);
        Assert.Empty(inspection.Envelope.MeasuredPayloads);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("null")]
    [InlineData("unknown")]
    [InlineData("duplicate")]
    [InlineData("escapedDuplicate")]
    [InlineData("missingCatalog")]
    public async Task V2RequiresExactlyEightFieldsAndNonnullPrivateDescriptor(string mutation)
    {
        JsonObject manifest = Manifest();
        string? raw = null;
        switch (mutation)
        {
            case "missing": manifest.Remove("privateMetadata"); break;
            case "null": manifest["privateMetadata"] = null; break;
            case "unknown": manifest["SYNTHETIC-PRIVATE-SECRET"] = "SECRET"; break;
            case "missingCatalog": manifest.Remove("catalog"); break;
            case "duplicate": raw = manifest.ToJsonString().Replace("\"privateMetadata\":", "\"privateMetadata\":{},\"privateMetadata\":"); break;
            case "escapedDuplicate": raw = manifest.ToJsonString().Replace("\"privateMetadata\":", "\"private\\u004detadata\":{},\"privateMetadata\":"); break;
        }
        using MemoryStream package = Package(manifest, rawManifest: raw);
        await AssertError(package, mutation == "missing" ? TransferEnvelopeError.UnsupportedVersion : TransferEnvelopeError.InvalidManifest);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("[\"chan-v1\"]")]
    [InlineData("[\"meowchan-private-v1\",\"chan-v1\"]")]
    [InlineData("[\"chan-v1\",\"chan-v1\"]")]
    [InlineData("[\"chan-v1\",\"meowchan-private-v1\",\"meowchan-private-v1\"]")]
    [InlineData("[\"chan-v2\",\"meowchan-private-v1\"]")]
    [InlineData("[\"chan-v1\",\"SYNTHETIC-PRIVATE-SECRET\"]")]
    [InlineData("[\"chan-v1\",null]")]
    [InlineData("[null,\"meowchan-private-v1\"]")]
    [InlineData("[\"chan-v1\",{}]")]
    [InlineData("null")]
    [InlineData("{}")]
    public async Task PrivateFeaturesAreExactlyTheFixedOrderedPair(string features)
    {
        JsonObject manifest = Manifest();
        manifest["requiredFeatures"] = JsonNode.Parse(features);
        using MemoryStream package = Package(manifest);
        await AssertError(package, TransferEnvelopeError.UnsupportedFeature);
    }

    [Theory]
    [InlineData("chan-v4")]
    [InlineData("CHAN-V1")]
    [InlineData("SYNTHETIC-PRIVATE-SECRET")]
    public async Task V2DoesNotIntroduceAnotherPublicContentSchema(string schema)
    {
        using MemoryStream package = Package(Manifest(schema: schema));
        await AssertError(package, TransferEnvelopeError.UnsupportedVersion);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"length\":0}")]
    [InlineData("{\"sha256\":\"SECRET\"}")]
    [InlineData("{\"length\":-1,\"sha256\":\"SECRET\"}")]
    [InlineData("{\"length\":1.5,\"sha256\":\"SECRET\"}")]
    [InlineData("{\"length\":\"1\",\"sha256\":\"SECRET\"}")]
    [InlineData("{\"length\":null,\"sha256\":\"SECRET\"}")]
    [InlineData("{\"length\":9223372036854775808,\"sha256\":\"SECRET\"}")]
    [InlineData("{\"length\":1,\"sha256\":\"SECRET\"}")]
    [InlineData("{\"length\":1,\"sha256\":null}")]
    public async Task PrivateDescriptorUsesTheSameStrictBlobGrammar(string descriptor)
    {
        JsonObject manifest = Manifest();
        manifest["privateMetadata"] = JsonNode.Parse(descriptor);
        using MemoryStream package = Package(manifest);
        await AssertError(package, TransferEnvelopeError.InvalidManifest);
    }

    [Theory]
    [InlineData("extra")]
    [InlineData("duplicateLength")]
    [InlineData("duplicateHash")]
    [InlineData("uppercaseHash")]
    public async Task PrivateDescriptorRejectsExtraDuplicateAndNoncanonicalFields(string mutation)
    {
        JsonObject manifest = Manifest();
        string? raw = null;
        JsonObject descriptor = manifest["privateMetadata"]!.AsObject();
        switch (mutation)
        {
            case "extra": descriptor["SYNTHETIC-PRIVATE-SECRET"] = "SECRET"; break;
            case "duplicateLength": raw = manifest.ToJsonString().Replace("\"privateMetadata\":{", "\"privateMetadata\":{\"length\":1,"); break;
            case "duplicateHash": raw = manifest.ToJsonString().Replace("\"privateMetadata\":{", "\"privateMetadata\":{\"sha256\":\"SECRET\","); break;
            case "uppercaseHash": descriptor["sha256"] = Hash(Private).ToUpperInvariant(); break;
        }
        using MemoryStream package = Package(manifest, rawManifest: raw);
        await AssertError(package, TransferEnvelopeError.InvalidManifest);
    }

    [Theory]
    [InlineData("missingPrivate")]
    [InlineData("duplicatePrivate")]
    [InlineData("extraPayload")]
    [InlineData("privateAsPayload")]
    [InlineData("missingPayload")]
    public async Task V2EntrySetIsExactAndPayloadCannotSubstituteForPrivateEntry(string mutation)
    {
        using MemoryStream package = Package(includePrivate: mutation is not ("missingPrivate" or "privateAsPayload"),
            includePayload: mutation != "missingPayload", extra: zip =>
            {
                if (mutation == "duplicatePrivate") Add(zip, "private.json", Private);
                if (mutation is "extraPayload" or "privateAsPayload") Add(zip, "payload/" + Hash(Private), Private);
            });
        await AssertError(package, mutation == "duplicatePrivate" ? TransferEnvelopeError.DuplicateEntry : TransferEnvelopeError.InvalidReference);
    }

    [Theory]
    [InlineData("PRIVATE.json")]
    [InlineData("private.json/SECRET")]
    [InlineData("../private.json")]
    [InlineData("private/SECRET.json")]
    public async Task PrivateEntryHasOneFixedCanonicalName(string name)
    {
        using MemoryStream package = Package(includePrivate: false, extra: zip => Add(zip, name, Private));
        await AssertError(package, TransferEnvelopeError.InvalidEntry);
    }

    [Fact]
    public async Task V1RejectsPrivateDescriptorFeatureAndUnreferencedPrivateEntry()
    {
        JsonObject manifest = Manifest(1);
        manifest["privateMetadata"] = Blob(Private);
        using (MemoryStream package = Package(manifest))
            await AssertError(package, TransferEnvelopeError.InvalidManifest);
        manifest = Manifest(1);
        manifest["requiredFeatures"] = new JsonArray("chan-v1", "meowchan-private-v1");
        using (MemoryStream package = Package(manifest, includePrivate: false))
            await AssertError(package, TransferEnvelopeError.UnsupportedFeature);
        using (MemoryStream package = Package(Manifest(1)))
            await AssertError(package, TransferEnvelopeError.InvalidReference);
        // Legacy seven-field version=2 inputs still fail closed as before.
        manifest = Manifest(1); manifest["envelopeVersion"] = 2;
        using (MemoryStream package = Package(manifest, includePrivate: false))
            await AssertError(package, TransferEnvelopeError.UnsupportedVersion);
    }

    [Fact]
    public async Task PrivateLengthAndHashAreIndependentlyMeasured()
    {
        JsonObject manifest = Manifest();
        manifest["privateMetadata"]!["length"] = Private.Length + 1;
        using (MemoryStream package = Package(manifest)) await AssertError(package, TransferEnvelopeError.LengthMismatch);
        manifest = Manifest();
        manifest["privateMetadata"]!["sha256"] = Hash(Payload);
        using (MemoryStream package = Package(manifest)) await AssertError(package, TransferEnvelopeError.HashMismatch);
        manifest = Manifest();
        manifest["catalog"]!["sha256"] = Hash(Private);
        using (MemoryStream package = Package(manifest)) await AssertError(package, TransferEnvelopeError.HashMismatch);
    }

    [Theory]
    [InlineData("crc")]
    [InlineData("bytes")]
    [InlineData("bytesWithUpdatedCrc")]
    public async Task PrivateCrcAndShaMustBothMatchTheSameCapturedBytes(string tamper)
    {
        using MemoryStream original = Package();
        byte[] bytes = original.ToArray();
        var offsets = Offsets(bytes, "private.json");
        if (tamper == "crc")
        {
            bytes[offsets.Local + 14] ^= 1;
            bytes[offsets.Central + 16] ^= 1;
        }
        else
        {
            bytes[offsets.Data] ^= 1;
            if (tamper == "bytesWithUpdatedCrc")
            {
                TransferCrc32 crc = new(); crc.Add(bytes.AsSpan(offsets.Data, Private.Length));
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offsets.Local + 14), crc.Value);
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offsets.Central + 16), crc.Value);
            }
        }
        using MemoryStream forged = new(bytes);
        await AssertError(forged, tamper == "bytesWithUpdatedCrc" ? TransferEnvelopeError.HashMismatch : TransferEnvelopeError.InvalidZip);
    }

    [Fact]
    public async Task PrivateLimitIsIndependentOfMediaPayloadLimitAndCannotBeRaised()
    {
        using MemoryStream package = Package();
        TransferEnvelopeLimits exact = new() { PrivateMetadataBytes = Private.Length, PayloadBytes = Payload.Length };
        Assert.True((await NativeTransferEnvelopeValidator.InspectAsync(package, limits: exact)).IsEnvelopeValid);
        await AssertError(package, TransferEnvelopeError.PayloadLimit, exact with { PrivateMetadataBytes = Private.Length - 1 });
        foreach (int value in new[] { 0, -1, PrivateCap + 1, int.MaxValue })
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => NativeTransferEnvelopeValidator.InspectAsync(package,
                limits: new() { PrivateMetadataBytes = value }));
        JsonObject manifest = Manifest(); manifest["privateMetadata"]!["length"] = PrivateCap + 1;
        using MemoryStream oversizedDescriptor = Package(manifest);
        await AssertError(oversizedDescriptor, TransferEnvelopeError.PayloadLimit);
    }

    [Fact]
    public async Task DefaultPrivateCapAcceptsExactBoundaryAndRejectsOneAdditionalByte()
    {
        byte[] privateBytes = new byte[PrivateCap + 1];
        byte[] atLimit = new byte[PrivateCap];
        using (MemoryStream exact = Package(Manifest(privateBytes: atLimit, payload: false), privateBytes: atLimit, includePayload: false))
        {
            TransferEnvelopeInspection inspection = await NativeTransferEnvelopeValidator.InspectAsync(exact);
            Assert.True(inspection.IsEnvelopeValid);
            Assert.Equal(PrivateCap, inspection.Envelope!.PrivateMetadataBytes.Length);
        }
        using MemoryStream over = Package(Manifest(privateBytes: privateBytes, payload: false), privateBytes: privateBytes, includePayload: false);
        await AssertError(over, TransferEnvelopeError.PayloadLimit);
    }

    [Fact]
    public async Task ThreeEntryPrivatePackageUsesTheLastAvailableEntrySlot()
    {
        using MemoryStream package = Package(Manifest(payload: false), includePayload: false);
        Assert.True((await NativeTransferEnvelopeValidator.InspectAsync(package, limits: new() { Entries = 3 })).IsEnvelopeValid);
        await AssertError(package, TransferEnvelopeError.EntryLimit, new() { Entries = 2 });
    }

    [Fact]
    public async Task PrivateEntryLengthIsBoundedEvenIfDescriptorLiesBelowTheLimit()
    {
        JsonObject manifest = Manifest(privateBytes: []);
        using MemoryStream package = Package(manifest);
        await AssertError(package, TransferEnvelopeError.PayloadLimit, new() { PrivateMetadataBytes = 1 });
    }

    [Fact]
    public async Task ExactContainerDirectoryEntryAndTotalBudgetsIncludePrivateComponent()
    {
        JsonObject manifest = Manifest();
        byte[] manifestBytes = Encoding.UTF8.GetBytes(manifest.ToJsonString());
        using MemoryStream package = Package(manifest);
        byte[] bytes = package.ToArray();
        int directoryBytes = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(bytes.Length - 10)));
        long total = manifestBytes.Length + Catalog.Length + Private.Length + Payload.Length;
        long expectedOverhead = 22 + 4 * (30 + 46) + 2 * ("manifest.json".Length + "catalog.json".Length + "private.json".Length + 72);
        Assert.Equal(total + expectedOverhead, package.Length);
        TransferEnvelopeLimits exact = new()
        {
            ContainerBytes = package.Length, DirectoryBytes = directoryBytes, Entries = 4,
            TotalBytes = total, ManifestBytes = manifestBytes.Length, CatalogBytes = Catalog.Length,
            PrivateMetadataBytes = Private.Length, PayloadBytes = Payload.Length
        };
        Assert.True((await NativeTransferEnvelopeValidator.InspectAsync(package, limits: exact)).IsEnvelopeValid);
        await AssertError(package, TransferEnvelopeError.ContainerLimit, exact with { ContainerBytes = package.Length - 1 });
        await AssertError(package, TransferEnvelopeError.DirectoryLimit, exact with { DirectoryBytes = directoryBytes - 1 });
        await AssertError(package, TransferEnvelopeError.EntryLimit, exact with { Entries = 3 });
        await AssertError(package, TransferEnvelopeError.PayloadLimit, exact with { TotalBytes = total - 1 });
    }

    [Fact]
    public async Task ManifestDeclaredTotalIncludesPrivateLengthBeforeDataIsCaptured()
    {
        JsonObject manifest = Manifest(); manifest["privateMetadata"]!["length"] = Private.Length + 1;
        long actualTotal = Encoding.UTF8.GetByteCount(manifest.ToJsonString()) + Catalog.Length + Private.Length + Payload.Length;
        using MemoryStream package = Package(manifest);
        await AssertError(package, TransferEnvelopeError.PayloadLimit, new() { TotalBytes = actualTotal });
    }

    [Fact]
    public async Task PrivateCompressionIsNotAcceptedAsAStoredComponent()
    {
        byte[] privateBytes = new byte[128 * 1024];
        using MemoryStream package = Package(Manifest(privateBytes: privateBytes), privateBytes: privateBytes,
            privateCompression: CompressionLevel.SmallestSize);
        await AssertError(package, TransferEnvelopeError.UnsupportedZip);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InFlightPrivateReadCancellationRestoresCallerPositionAndWinsOverRestoreFailure(bool failRestore)
    {
        byte[] privateBytes = new byte[128 * 1024];
        using MemoryStream package = Package(Manifest(privateBytes: privateBytes), privateBytes: privateBytes);
        using CancellationTokenSource cancellation = new();
        using PrivateReadStream source = new(package.ToArray(), cancellation, failRestore);
        OperationCanceledException error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            NativeTransferEnvelopeValidator.InspectAsync(source, cancellation.Token));
        Assert.True(source.PrivateReadStarted);
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.False(source.Disposed);
        Assert.DoesNotContain("SECRET", error.ToString());
        if (!failRestore) Assert.Equal(7, source.Position);
    }

    [Fact]
    public async Task PrivateReadFailureReturnsNoEnvelopeAndNoSourceDiagnostic()
    {
        using MemoryStream package = Package();
        using PrivateReadStream source = new(package.ToArray(), null, false);
        await AssertError(source, TransferEnvelopeError.ReadFailure);
        Assert.True(source.PrivateReadStarted);
        Assert.False(source.Disposed);
    }

    private static (int Central, int Local, int Data) Offsets(byte[] bytes, string name)
    {
        int central = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(bytes.Length - 6)));
        while (BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(central)) == 0x02014b50)
        {
            int nameLength = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(central + 28));
            if (Encoding.ASCII.GetString(bytes, central + 46, nameLength) == name)
            {
                int local = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(central + 42)));
                return (central, local, local + 30 + nameLength);
            }
            central += 46 + nameLength;
        }
        throw new InvalidOperationException("Synthetic entry missing.");
    }

    private sealed class PrivateReadStream : MemoryStream
    {
        private readonly int privateOffset;
        private readonly CancellationTokenSource? cancellation;
        private readonly bool failRestore;
        internal bool PrivateReadStarted { get; private set; }
        internal bool Disposed { get; private set; }
        internal PrivateReadStream(byte[] bytes, CancellationTokenSource? cancellation, bool failRestore) : base(bytes)
        {
            privateOffset = Offsets(bytes, "private.json").Data;
            this.cancellation = cancellation; this.failRestore = failRestore;
            Position = 7;
        }
        public override long Position
        {
            get => base.Position;
            set
            {
                if (failRestore && PrivateReadStarted && value == 7) throw new UnauthorizedAccessException("SECRET restore path");
                base.Position = value;
            }
        }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Position != privateOffset) return await base.ReadAsync(buffer, cancellationToken);
            PrivateReadStarted = true;
            if (cancellation == null) throw new IOException("SECRET private read path");
            int read = await base.ReadAsync(buffer, cancellationToken);
            cancellation.Cancel();
            return read;
        }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }
}
