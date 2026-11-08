using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MeowChan.Archive;
using MeowChan.Storage;

namespace MeowChan.Tests.Transfer;

// Synthetic manifest6 wire fixtures only. These do not exercise a journalled v4 importer.
public sealed class NativeV6PassiveInventoryTests
{
    private const string ManifestName = ".meowchan-manifest.json";
    private static readonly ResourceId Thread = new("4chan", "b", "7001");
    private static readonly MediaPassiveObservation Origin = new("c1819e85-76da-4581-b240-dc9524498ea9",
        new string('a', 64), new DateTimeOffset(2026, 11, 7, 12, 0, 0, TimeSpan.Zero));
    private static readonly byte[] Original = Encoding.UTF8.GetBytes("synthetic original version; not a stored original");
    private static readonly byte[] Preview = Encoding.UTF8.GetBytes("synthetic preview bytes");

    [Fact]
    public async Task EmptyV6InventoryIsAvailableWithoutManufacturingAssetsOrBindings()
    {
        using TempDirectory directory = new();
        await WriteManifestAsync(directory.Path, [], []);
        byte[] before = await File.ReadAllBytesAsync(Path.Combine(directory.Path, ManifestName));

        MediaAssetSnapshot snapshot = await MediaAssetReader.OpenAsync(directory.Path);

        Assert.Equal(MediaManifestStatus.Available, snapshot.Status);
        Assert.Equal(6, snapshot.ManifestVersion);
        Assert.Equal(Hash(before), snapshot.Generation);
        Assert.Null(snapshot.Warning);
        Assert.False(snapshot.OriginalEvidence.IsDefault);
        Assert.False(snapshot.DerivedRepresentations.IsDefault);
        Assert.Empty(snapshot.OriginalEvidence);
        Assert.Empty(snapshot.DerivedRepresentations);
        AssertOriginalOnlyApisEmpty(snapshot);
        Assert.Equal(before, await File.ReadAllBytesAsync(Path.Combine(directory.Path, ManifestName)));
        Assert.Equal(ManifestName, Path.GetFileName(Assert.Single(Directory.GetFiles(directory.Path))));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(3, false)]
    [InlineData(4, false)]
    [InlineData(5, false)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(3, true)]
    [InlineData(4, true)]
    [InlineData(5, true)]
    public async Task IncomingOriginalStatusPreservesExactEvidenceButNeverCreatesLocalSuccess(int status, bool historicalSha1)
    {
        using TempDirectory directory = new();
        MediaOriginalEvidence expected = Evidence((MediaObservedContentStatus)status,
            version: historicalSha1 ? Actual(Original) with { Sha1 = null } : Actual(Original));
        await WriteManifestAsync(directory.Path, [expected], []);
        string path = Path.Combine(directory.Path, ManifestName);
        byte[] before = await File.ReadAllBytesAsync(path);

        MediaAssetSnapshot first = await MediaAssetReader.OpenAsync(directory.Path);
        MediaAssetSnapshot restarted = await MediaAssetReader.OpenAsync(directory.Path);

        Assert.Equal(MediaManifestStatus.Available, first.Status);
        Assert.Equal(6, first.ManifestVersion);
        Assert.Equal(expected, Assert.Single(first.OriginalEvidence));
        Assert.Equal(expected, Assert.Single(restarted.OriginalEvidence));
        Assert.NotSame(expected, first.OriginalEvidence[0]);
        Assert.NotSame(first.OriginalEvidence[0], restarted.OriginalEvidence[0]);
        Assert.Equal(first.OriginalEvidence.ToArray(), restarted.OriginalEvidence.ToArray());
        Assert.Empty(first.DerivedRepresentations);
        Assert.Equal(expected.Metadata, first.OriginalEvidence[0].Metadata);
        Assert.Null(first.OriginalEvidence[0].Metadata.PlannedPath);
        Assert.Equal(expected.OriginalByteVersion, first.OriginalEvidence[0].OriginalByteVersion);
        Assert.Equal(historicalSha1 ? null : Actual(Original).Sha1, first.OriginalEvidence[0].OriginalByteVersion!.Sha1);
        Assert.Equal(expected.Origin, first.OriginalEvidence[0].Origin);
        AssertOriginalOnlyApisEmpty(first, expected);
        AssertOriginalOnlyApisEmpty(restarted, expected);
        Assert.Equal(before, await File.ReadAllBytesAsync(path));
        Assert.Equal(ManifestName, Path.GetFileName(Assert.Single(Directory.GetFiles(directory.Path))));
    }

    [Fact]
    public async Task UnknownUnkeyedMetadataRemainsUnkeyedWithNoByteVersion()
    {
        using TempDirectory directory = new();
        CapturedAttachment metadata = new(2, null, OriginalFileName: "historical-unknown.bin", IsDeleted: true);
        MediaOriginalEvidence expected = new(new(Thread, "999", 2, null), metadata,
            MediaObservedContentStatus.Unknown, null, Origin);
        await WriteManifestAsync(directory.Path, [expected], []);

        MediaAssetSnapshot snapshot = await MediaAssetReader.OpenAsync(directory.Path);

        Assert.Equal(MediaManifestStatus.Available, snapshot.Status);
        Assert.Equal(expected, Assert.Single(snapshot.OriginalEvidence));
        Assert.True(snapshot.OriginalEvidence[0].Metadata.IsDeleted);
        Assert.Null(snapshot.OriginalEvidence[0].Occurrence.OriginalRevisionKey);
        Assert.Null(snapshot.OriginalEvidence[0].Metadata.AttachmentRevisionKey);
        Assert.Null(snapshot.OriginalEvidence[0].OriginalByteVersion);
        AssertOriginalOnlyApisEmpty(snapshot, expected);
        Assert.Empty(snapshot.DerivedRepresentations);
    }

    [Fact]
    public async Task KeyedMetadataMayRemainWithoutKnownOriginalBytes()
    {
        using TempDirectory directory = new();
        MediaOriginalEvidence expected = Evidence(MediaObservedContentStatus.Unavailable) with { OriginalByteVersion = null };
        await WriteManifestAsync(directory.Path, [expected], []);

        MediaAssetSnapshot snapshot = await MediaAssetReader.OpenAsync(directory.Path);

        Assert.Equal(MediaManifestStatus.Available, snapshot.Status);
        Assert.Equal(expected, Assert.Single(snapshot.OriginalEvidence));
        Assert.NotNull(snapshot.OriginalEvidence[0].Occurrence.OriginalRevisionKey);
        Assert.Null(snapshot.OriginalEvidence[0].OriginalByteVersion);
        AssertOriginalOnlyApisEmpty(snapshot, expected);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ContainedDerivedBytesOpenBySemanticValueWithoutBecomingOriginal(bool poster, bool historicalSha1)
    {
        using TempDirectory directory = new();
        byte[] payload = poster ? CreatePoster() : Preview;
        MediaOriginalEvidence evidence = Evidence(MediaObservedContentStatus.Omitted,
            version: historicalSha1 ? Actual(Original) with { Sha1 = null } : Actual(Original));
        MediaDerivedRepresentation member = Derived(evidence, poster ? MediaDerivedKind.VideoPosterPng : MediaDerivedKind.Preview,
            MediaDerivedStatus.Contained, payload);
        await WritePayloadAsync(directory.Path, member, payload);
        await WriteManifestAsync(directory.Path, [evidence], [member]);
        byte[] wire = await File.ReadAllBytesAsync(Path.Combine(directory.Path, ManifestName));
        string[] entries = Directory.GetFileSystemEntries(directory.Path).Order(StringComparer.Ordinal).ToArray();

        MediaAssetSnapshot snapshot = await MediaAssetReader.OpenAsync(directory.Path);

        Assert.Equal(MediaManifestStatus.Available, snapshot.Status);
        Assert.Equal(evidence, Assert.Single(snapshot.OriginalEvidence));
        Assert.Equal(member, Assert.Single(snapshot.DerivedRepresentations));
        AssertOriginalOnlyApisEmpty(snapshot, evidence);
        Assert.Null(snapshot.FindById(member.Blob!.Actual.Sha256));

        // Independent JSON allocation must still select the same semantic member, not a reference identity.
        MediaDerivedRepresentation equalMember = JsonSerializer.Deserialize<MediaDerivedRepresentation>(JsonSerializer.Serialize(member))!;
        Assert.Equal(member, equalMember);
        Assert.NotSame(snapshot.DerivedRepresentations[0], equalMember);
        Assert.NotSame(snapshot.DerivedRepresentations[0].Parent, equalMember.Parent);
        Assert.NotSame(snapshot.DerivedRepresentations[0].SourceVersion, equalMember.SourceVersion);
        Assert.NotSame(snapshot.DerivedRepresentations[0].Blob, equalMember.Blob);
        Assert.NotSame(snapshot.DerivedRepresentations[0].Origin, equalMember.Origin);
        await AssertVerifiedPayloadAsync(snapshot, equalMember, payload);

        MediaAssetSnapshot restarted = await MediaAssetReader.OpenAsync(directory.Path);
        Assert.Equal(snapshot.Generation, restarted.Generation);
        Assert.Equal(snapshot.OriginalEvidence.ToArray(), restarted.OriginalEvidence.ToArray());
        Assert.Equal(snapshot.DerivedRepresentations.ToArray(), restarted.DerivedRepresentations.ToArray());
        Assert.Equal(historicalSha1 ? null : Actual(Original).Sha1, restarted.DerivedRepresentations[0].SourceVersion.Sha1);
        Assert.NotNull(restarted.DerivedRepresentations[0].Blob!.Actual.Sha1);
        await AssertVerifiedPayloadAsync(restarted, equalMember, payload);
        Assert.Equal(wire, await File.ReadAllBytesAsync(Path.Combine(directory.Path, ManifestName)));
        Assert.Equal(payload, await File.ReadAllBytesAsync(Path.Combine(directory.Path, member.Blob.RelativeFileName)));
        Assert.Equal(entries, Directory.GetFileSystemEntries(directory.Path).Order(StringComparer.Ordinal).ToArray());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task NoncontainedDerivedObservationsNeedNoLocalFilesAndKeepTheirStatus(int status)
    {
        using TempDirectory directory = new();
        MediaOriginalEvidence evidence = Evidence(MediaObservedContentStatus.Failed);
        MediaDerivedRepresentation preview = Derived(evidence, MediaDerivedKind.Preview, (MediaDerivedStatus)status);
        MediaDerivedRepresentation poster = Derived(evidence, MediaDerivedKind.VideoPosterPng, (MediaDerivedStatus)status);
        await WriteManifestAsync(directory.Path, [evidence], [preview, poster]);

        MediaAssetSnapshot snapshot = await MediaAssetReader.OpenAsync(directory.Path);

        Assert.Equal(MediaManifestStatus.Available, snapshot.Status);
        Assert.Equal(evidence, Assert.Single(snapshot.OriginalEvidence));
        Assert.Equal(new[] { preview, poster }, snapshot.DerivedRepresentations.ToArray());
        Assert.All(snapshot.DerivedRepresentations, value => Assert.Null(value.Blob));
        AssertOriginalOnlyApisEmpty(snapshot, evidence);
        Assert.Equal(ManifestName, Path.GetFileName(Assert.Single(Directory.GetFiles(directory.Path))));
    }

    [Fact]
    public async Task SharedDerivedFileKeepsExactOccurrenceKindsAndNullableSourceTuplesSeparate()
    {
        using TempDirectory directory = new();
        byte[] payload = CreatePoster();
        MediaOriginalEvidence first = Evidence(MediaObservedContentStatus.Contained, post: "15");
        MediaOriginalEvidence historical = Evidence(MediaObservedContentStatus.Cancelled, post: "999",
            version: Actual(Original) with { Sha1 = null });
        MediaDerivedRepresentation[] members =
        [
            Derived(first, MediaDerivedKind.Preview, MediaDerivedStatus.Contained, payload),
            Derived(first, MediaDerivedKind.VideoPosterPng, MediaDerivedStatus.Contained, payload),
            Derived(historical, MediaDerivedKind.Preview, MediaDerivedStatus.Contained, payload),
            Derived(historical, MediaDerivedKind.VideoPosterPng, MediaDerivedStatus.Contained, payload)
        ];
        await WritePayloadAsync(directory.Path, members[0], payload);
        await WriteManifestAsync(directory.Path, [first, historical], members);

        MediaAssetSnapshot snapshot = await MediaAssetReader.OpenAsync(directory.Path);

        Assert.Equal(MediaManifestStatus.Available, snapshot.Status);
        Assert.Equal(new[] { first, historical }, snapshot.OriginalEvidence.ToArray());
        Assert.Equal(members, snapshot.DerivedRepresentations.ToArray());
        Assert.Single(snapshot.DerivedRepresentations.Select(value => value.Blob!.RelativeFileName).Distinct());
        Assert.Equal(4, snapshot.DerivedRepresentations.Select(value => (value.Parent, value.Kind)).Distinct().Count());
        Assert.NotEqual(first.OriginalByteVersion, historical.OriginalByteVersion);
        Assert.All(snapshot.DerivedRepresentations.Where(value => value.Parent.PostId == "15"),
            value => Assert.Equal(first.OriginalByteVersion, value.SourceVersion));
        Assert.All(snapshot.DerivedRepresentations.Where(value => value.Parent.PostId == "999"),
            value => Assert.Null(value.SourceVersion.Sha1));
        foreach (MediaDerivedRepresentation member in members)
            await AssertVerifiedPayloadAsync(snapshot, member, payload);
        AssertOriginalOnlyApisEmpty(snapshot, first, historical);
        Assert.Equal(2, Directory.GetFiles(directory.Path).Length); // One manifest and one shared regular payload.
    }

    [Fact]
    public async Task ExistingSnapshotInventoryDoesNotChangeWhenManifestIsReplaced()
    {
        using TempDirectory directory = new();
        MediaOriginalEvidence evidence = Evidence(MediaObservedContentStatus.Omitted);
        MediaDerivedRepresentation derived = Derived(evidence, MediaDerivedKind.Preview, MediaDerivedStatus.Contained, Preview);
        await WritePayloadAsync(directory.Path, derived, Preview);
        await WriteManifestAsync(directory.Path, [evidence], [derived]);
        MediaAssetSnapshot original = await MediaAssetReader.OpenAsync(directory.Path);
        Assert.Equal(MediaManifestStatus.Available, original.Status);
        string? generation = original.Generation;

        await WriteManifestAsync(directory.Path, [], []);
        MediaAssetSnapshot replacement = await MediaAssetReader.OpenAsync(directory.Path);

        Assert.Equal(MediaManifestStatus.Available, replacement.Status);
        Assert.NotEqual(generation, replacement.Generation);
        Assert.Empty(replacement.OriginalEvidence);
        Assert.Empty(replacement.DerivedRepresentations);
        Assert.Equal(generation, original.Generation);
        Assert.Equal(evidence, Assert.Single(original.OriginalEvidence));
        Assert.Equal(derived, Assert.Single(original.DerivedRepresentations));
        await AssertVerifiedPayloadAsync(original, derived, Preview);
        AssertOriginalOnlyApisEmpty(original, evidence);
        AssertOriginalOnlyApisEmpty(replacement, evidence);
    }

    private static MediaOriginalEvidence Evidence(MediaObservedContentStatus status, string post = "15", MediaActualIntegrity? version = null)
    {
        // Provider expectations deliberately differ from observed bytes: evidence is not active acceptance.
        MediaProviderMetadata provider = new("111", "original", "https://i.4cdn.org/b/111.webm", "synthetic.webm",
            987654321, Convert.ToBase64String(MD5.HashData([42])), new string('b', 64))
        { ProviderSizeHint = new("941.90", "MiB", MediaSizePrecision.Rounded) };
        string key = MediaRevisionKeys.CreateAttachmentKey(Thread, post, 0, provider);
        CapturedAttachment metadata = new(0, provider.MediaId, provider.Variant, provider.Url, provider.OriginalFileName,
            ExpectedSize: provider.ExpectedSize, ExpectedMd5: provider.ExpectedMd5)
        {
            ExpectedSha256 = provider.ExpectedSha256, ProviderSizeHint = provider.ProviderSizeHint,
            AttachmentRevisionKey = key
        };
        return new(new(Thread, post, 0, key), metadata, status, version ?? Actual(Original), Origin);
    }

    private static MediaDerivedRepresentation Derived(MediaOriginalEvidence evidence, MediaDerivedKind kind,
        MediaDerivedStatus status, byte[]? payload = null)
        => new(evidence.Occurrence with { Resource = evidence.Occurrence.Resource with { } },
            evidence.OriginalByteVersion! with { }, kind, status,
            payload == null ? null : new(Hash(payload) + ".derived.bin", Actual(payload)), Origin with { });

    private static async Task WriteManifestAsync(string directory, MediaOriginalEvidence[] originals, MediaDerivedRepresentation[] derived)
    {
        JsonObject manifest = new()
        {
            ["Version"] = 6,
            ["Entries"] = new JsonObject(),
            ["Assets"] = new JsonObject(),
            ["AttachmentBindings"] = new JsonObject(),
            ["OriginalEvidence"] = JsonSerializer.SerializeToNode(originals),
            ["DerivedRepresentations"] = JsonSerializer.SerializeToNode(derived)
        };
        await File.WriteAllTextAsync(Path.Combine(directory, ManifestName), manifest.ToJsonString());
    }

    private static Task WritePayloadAsync(string directory, MediaDerivedRepresentation member, byte[] bytes)
        => File.WriteAllBytesAsync(Path.Combine(directory, member.Blob!.RelativeFileName), bytes);

    private static void AssertOriginalOnlyApisEmpty(MediaAssetSnapshot snapshot, params MediaOriginalEvidence[] evidence)
    {
        Assert.Equal(0, snapshot.Count);
        Assert.Empty(snapshot.GetPage());
        Assert.Empty(snapshot.GetPage(1));
        Assert.Empty(snapshot.AttachmentBindings);
        Assert.Null(snapshot.FindById(Hash(Original)));
        foreach (MediaOriginalEvidence value in evidence)
        {
            if (value.Occurrence.OriginalRevisionKey != null)
                Assert.Null(snapshot.FindAttachmentBinding(value.Occurrence.OriginalRevisionKey));
            Assert.Null(MediaAttachmentResolver.Resolve(snapshot, value.Occurrence.Resource, value.Occurrence.PostId, value.Metadata));
        }
    }

    private static async Task AssertVerifiedPayloadAsync(MediaAssetSnapshot snapshot, MediaDerivedRepresentation member, byte[] expected)
    {
        await using Stream verified = await snapshot.OpenVerifiedDerivedReadAsync(member);
        Assert.True(verified.CanRead);
        Assert.False(verified.CanWrite);
        Assert.Equal(0L, verified.Position);
        Assert.Equal((long)expected.Length, verified.Length);
        using MemoryStream copy = new();
        await verified.CopyToAsync(copy);
        Assert.Equal(expected, copy.ToArray());
        Assert.Equal(Actual(expected), member.Blob!.Actual);
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static MediaActualIntegrity Actual(byte[] bytes) => new(bytes.LongLength,
        Convert.ToBase64String(MD5.HashData(bytes)), Hash(bytes)) { Sha1 = Convert.ToBase64String(SHA1.HashData(bytes)) };

    // Same bounded structural PNG fixture pattern as NativeTransferV4ImportPreviewTests; no decoder/playability claim.
    private static byte[] CreatePoster()
    {
        byte[] header = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(header, 1);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), 1);
        header[8] = 8;
        byte[] Chunk(string type, byte[] data)
        {
            byte[] result = new byte[data.Length + 12];
            BinaryPrimitives.WriteUInt32BigEndian(result, (uint)data.Length);
            Encoding.ASCII.GetBytes(type).CopyTo(result, 4);
            data.CopyTo(result, 8);
            uint crc = uint.MaxValue;
            foreach (byte value in result.AsSpan(4, data.Length + 4))
            {
                crc ^= value;
                for (int bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xedb88320u : 0u);
            }
            BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(result.Length - 4), ~crc);
            return result;
        }
        return [137, 80, 78, 71, 13, 10, 26, 10, .. Chunk("IHDR", header),
            .. Chunk("IDAT", [0x78, 0x9c, 0x63, 0x60, 0x00, 0x00, 0x00, 0x02, 0x00, 0x01]), .. Chunk("IEND", [])];
    }
}
