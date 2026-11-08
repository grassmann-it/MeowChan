using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using MeowChan.Archive;
using MeowChan.Downloader;
using MeowChan.Storage;

namespace MeowChan.Tests.Transfer;

// Real writers over synthetic v6 wire, following NativeV6WriterPreservationTests.
// This is not a journalled native-import or source-policy acceptance fixture.
public sealed class NativeV6OriginalAliasTests
{
    private const string ManifestName = ".meowchan-manifest.json";
    private static readonly byte[] SeedBytes = [1, 3, 5, 7];
    private static readonly byte[] IncomingBytes = [2, 4, 6, 8, 10];
    private static readonly ResourceId Resource = new("stub", "g", "123");
    private static readonly MediaPassiveObservation Origin = new("b8cce56f-562e-4c44-8a65-a68879108f11",
        new string('c', 64), new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero));
    private static string DerivedName => Actual(IncomingBytes).Sha256 + ".derived.bin";

    public static IEnumerable<object[]> CommitCases()
    {
        foreach (bool upperCase in new[] { false, true })
        foreach (bool payloadPresent in new[] { false, true })
        foreach (bool withReferences in new[] { false, true })
            yield return [upperCase, payloadPresent, withReferences];
    }

    [Theory]
    [MemberData(nameof(CommitCases))]
    public async Task CommitRejectsReservedDerivedNameBeforePublishingEvenWhenMissingOrByteIdentical(
        bool upperCase, bool payloadPresent, bool withReferences)
    {
        using TempDirectory directory = new();
        using StubImageBoard board = new();
        Fixture fixture = await SeedV6Async(directory.Path, board, payloadPresent);
        string destinationName = AliasName(upperCase);
        // On a case-sensitive filesystem this creates a second equal-byte file so
        // the uppercase case exercises reuse, not just publication to a missing path.
        if (payloadPresent && upperCase)
            await File.WriteAllBytesAsync(Path.Combine(directory.Path, destinationName), IncomingBytes);
        Dictionary<string, byte[]> filesBefore = await CaptureFilesAsync(directory.Path);
        DownloadItem item = Item(board, directory.Path, "incoming", destinationName, IncomingBytes,
            withReferences ? "900" : null);
        using MemoryStream source = new(IncomingBytes, writable: false);
        MediaCommitResult? result = null;

        Exception? failure = await Record.ExceptionAsync(async () =>
        {
            result = await new MediaStore().CommitAsync(item, source, default);
        });

        // Check disk before the exception assertion: throwing from SaveManifestAsync
        // AFTER moving a payload into the reserved namespace is still a regression.
        await AssertRejectedStateAsync(directory.Path, fixture, filesBefore, payloadPresent);
        Assert.Null(result);
        Assert.IsType<InvalidDataException>(failure);
        Assert.True(source.CanRead); // Commit never owns the caller's source.
        Assert.Equal(IncomingBytes, source.ToArray());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task RestoreCannotAdoptEqualDerivedBytesOrPublishLegacyBytesIntoAMissingReservedName(
        bool upperCase, bool payloadPresent)
    {
        using TempDirectory directory = new();
        using StubImageBoard board = new();
        Fixture fixture = await SeedV6Async(directory.Path, board, payloadPresent);
        string destinationName = AliasName(upperCase);
        if (payloadPresent && upperCase)
            await File.WriteAllBytesAsync(Path.Combine(directory.Path, destinationName), IncomingBytes);
        string? legacyPath = null;
        if (!payloadPresent)
        {
            // A missing destination alone would trivially return null. Supply a real,
            // independently matching legacy source to exercise the restore->commit path.
            legacyPath = Path.Combine(directory.Path, "legacy-original.bin");
            await File.WriteAllBytesAsync(legacyPath, IncomingBytes);
        }
        Dictionary<string, byte[]> filesBefore = await CaptureFilesAsync(directory.Path);
        // Deliberately no new references: otherwise restore's acquisition-only gate
        // would return null before trying to adopt this untracked equal-byte file.
        DownloadItem item = Item(board, directory.Path, "incoming", destinationName, IncomingBytes,
            post: null, legacyPath: legacyPath);
        MediaCommitResult? result = null;

        Exception? failure = await Record.ExceptionAsync(async () =>
        {
            result = await new MediaStore().TryRestoreAsync(item, default);
        });

        await AssertRejectedStateAsync(directory.Path, fixture, filesBefore, payloadPresent);
        Assert.Null(result);
        // TryRestore has a nullable failure contract; explicit invalid-data rejection
        // is also safe. Do not accept arbitrary IO/fixture failures as protection.
        if (failure != null) Assert.IsType<InvalidDataException>(failure);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task NoncollidingOriginalWithSameBytesStillCommitsOrRestoresAndPreservesInventory(
        bool restore, bool payloadPresent)
    {
        using TempDirectory directory = new();
        using StubImageBoard board = new();
        Fixture fixture = await SeedV6Async(directory.Path, board, payloadPresent);
        // Same SHA256 stem and bytes, different filename: reject aliases, not equal content.
        string destinationName = Actual(IncomingBytes).Sha256 + ".original.bin";
        Assert.False(string.Equals(destinationName, DerivedName, StringComparison.OrdinalIgnoreCase));
        DownloadItem item = Item(board, directory.Path, "incoming", destinationName, IncomingBytes,
            restore ? null : "900");
        MediaCommitResult result;
        if (restore)
        {
            await File.WriteAllBytesAsync(item.DestinationPath, IncomingBytes);
            result = Assert.IsType<MediaCommitResult>(await new MediaStore().TryRestoreAsync(item, default));
        }
        else
        {
            using MemoryStream source = new(IncomingBytes, writable: false);
            result = await new MediaStore().CommitAsync(item, source, default);
        }

        Assert.Equal(restore, result.Reused);
        Assert.Equal(item.DestinationPath, result.Path);
        Assert.Equal(Actual(IncomingBytes).Sha256, result.Sha256);
        Assert.Equal(IncomingBytes.Length, result.Length);
        MediaAssetSnapshot restarted = await MediaAssetReader.OpenAsync(directory.Path);
        AssertInventory(restarted, fixture);
        Assert.Equal(2, restarted.Count);
        Assert.Equal(restore ? 1 : 2, restarted.AttachmentBindings.Count);
        Assert.Equal(fixture.ExistingBinding, restarted.FindAttachmentBinding(fixture.ExistingBinding.Reference.RevisionKey));
        Assert.NotNull(restarted.FindById(result.AssetId!));
        Assert.Equal(item.DestinationPath, restarted.FindById(result.AssetId!)!.Path);
        await using (Stream original = await restarted.OpenVerifiedReadAsync(result.AssetId!))
        {
            using MemoryStream copy = new();
            await original.CopyToAsync(copy);
            Assert.Equal(IncomingBytes, copy.ToArray());
        }
        MediaOriginalEvidence parent = fixture.Evidence[0];
        ResolvedMediaAttachment? resolved = MediaAttachmentResolver.Resolve(restarted, Resource, "900", parent.Metadata);
        if (restore)
        {
            Assert.Null(restarted.FindAttachmentBinding(parent.Occurrence.OriginalRevisionKey!));
            Assert.Null(resolved); // Legacy adoption is not a fabricated exact attachment binding.
        }
        else
        {
            Assert.NotNull(resolved);
            Assert.Equal(result.AssetId, resolved.Asset.AssetId);
            Assert.Equal(MediaAcceptanceKind.Strict, resolved.Binding!.Acceptance.Kind);
        }
        JsonObject before = JsonNode.Parse(fixture.ManifestBytes)!.AsObject();
        JsonObject after = await ReadManifestAsync(directory.Path);
        foreach (string field in new[] { "OriginalEvidence", "DerivedRepresentations" })
            Assert.True(JsonNode.DeepEquals(before[field], after[field]), field + " changed during a noncolliding write.");
        Assert.True(JsonNode.DeepEquals(before["AttachmentBindings"]![fixture.ExistingBinding.Reference.RevisionKey],
            after["AttachmentBindings"]![fixture.ExistingBinding.Reference.RevisionKey]));
        Assert.Equal(6, after["Version"]!.GetValue<int>());
        Assert.Equal(SeedBytes, await File.ReadAllBytesAsync(Path.Combine(directory.Path, "seed.bin")));
        await AssertDerivedPayloadAsync(directory.Path, restarted, fixture, payloadPresent);
        AssertOriginalOnlySeed(fixture.Snapshot, fixture); // Old snapshots remain immutable.
        Assert.Empty(Directory.GetFiles(directory.Path, "*.part"));
        Assert.Empty(Directory.GetFiles(directory.Path, "*.corrupt-*"));
    }

    private static async Task<Fixture> SeedV6Async(string directory, StubImageBoard board, bool payloadPresent)
    {
        DownloadItem seed = Item(board, directory, "seed", "seed.bin", SeedBytes, "123");
        using (MemoryStream source = new(SeedBytes, writable: false))
            await new MediaStore().CommitAsync(seed, source, default);
        MediaAttachmentBinding existing = Assert.Single((await MediaAssetReader.OpenAsync(directory)).AttachmentBindings).Value;
        MediaProviderMetadata provider = Provider("incoming", IncomingBytes);
        string key = MediaRevisionKeys.CreateAttachmentKey(Resource, "900", 0, provider);
        CapturedAttachment metadata = new(0, provider.MediaId, provider.Variant, provider.Url, provider.OriginalFileName,
            ExpectedSize: provider.ExpectedSize, ExpectedMd5: provider.ExpectedMd5)
        { AttachmentRevisionKey = key, ExpectedSha256 = provider.ExpectedSha256, ExpectedSha1 = provider.ExpectedSha1 };
        MediaOriginalEvidence parent = new(new(Resource, "900", 0, key), metadata,
            MediaObservedContentStatus.Omitted, Actual(IncomingBytes) with { Sha1 = null }, Origin);
        MediaOriginalEvidence unknown = new(new(Resource, "901", 0, null), new(0, null, IsDeleted: true),
            MediaObservedContentStatus.Unknown, null, Origin with { ImportedAtUtc = Origin.ImportedAtUtc.AddSeconds(1) });
        MediaOriginalEvidence[] evidence = [parent, unknown];
        MediaDerivedRepresentation[] derived =
        [
            new(parent.Occurrence, parent.OriginalByteVersion!, MediaDerivedKind.Preview, MediaDerivedStatus.Contained,
                new(DerivedName, Actual(IncomingBytes)), Origin),
            new(parent.Occurrence, parent.OriginalByteVersion!, MediaDerivedKind.VideoPosterPng, MediaDerivedStatus.Unavailable,
                null, Origin)
        ];
        if (payloadPresent)
            await File.WriteAllBytesAsync(Path.Combine(directory, DerivedName), IncomingBytes);
        JsonObject manifest = await ReadManifestAsync(directory);
        manifest["Version"] = 6;
        manifest["OriginalEvidence"] = JsonSerializer.SerializeToNode(evidence);
        manifest["DerivedRepresentations"] = JsonSerializer.SerializeToNode(derived);
        await File.WriteAllTextAsync(Path.Combine(directory, ManifestName), manifest.ToJsonString());
        Fixture fixture = new(existing, evidence, derived, await MediaAssetReader.OpenAsync(directory),
            await File.ReadAllBytesAsync(Path.Combine(directory, ManifestName)));
        AssertOriginalOnlySeed(fixture.Snapshot, fixture);
        // Missing bytes do not invalidate passive inventory; physical verification is separate.
        await AssertDerivedPayloadAsync(directory, fixture.Snapshot, fixture, payloadPresent);
        return fixture;
    }

    private static async Task AssertRejectedStateAsync(string directory, Fixture fixture,
        Dictionary<string, byte[]> filesBefore, bool payloadPresent)
    {
        Dictionary<string, byte[]> filesAfter = await CaptureFilesAsync(directory);
        Assert.Equal(filesBefore.Keys.Order(StringComparer.Ordinal).ToArray(), filesAfter.Keys.Order(StringComparer.Ordinal).ToArray());
        foreach (var file in filesBefore)
            Assert.Equal(file.Value, filesAfter[file.Key]);
        Assert.Equal(fixture.ManifestBytes, await File.ReadAllBytesAsync(Path.Combine(directory, ManifestName)));
        MediaAssetSnapshot restarted = await MediaAssetReader.OpenAsync(directory);
        AssertOriginalOnlySeed(restarted, fixture);
        Assert.Equal(fixture.Snapshot.Generation, restarted.Generation);
        Assert.Equal(fixture.Snapshot.GetPage().ToArray(), restarted.GetPage().ToArray());
        AssertOriginalOnlySeed(fixture.Snapshot, fixture);
        await AssertDerivedPayloadAsync(directory, restarted, fixture, payloadPresent);
        Assert.Empty(Directory.GetDirectories(directory));
        Assert.Empty(Directory.GetFiles(directory, "*.part"));
        Assert.Empty(Directory.GetFiles(directory, "*.corrupt-*"));
    }

    private static void AssertOriginalOnlySeed(MediaAssetSnapshot snapshot, Fixture fixture)
    {
        AssertInventory(snapshot, fixture);
        Assert.Equal(1, snapshot.Count);
        Assert.Equal(fixture.ExistingBinding, Assert.Single(snapshot.AttachmentBindings).Value);
        Assert.Equal(fixture.ExistingBinding.AssetId, Assert.Single(snapshot.GetPage()).AssetId);
        Assert.NotNull(snapshot.FindById(fixture.ExistingBinding.AssetId));
        MediaOriginalEvidence parent = fixture.Evidence[0];
        Assert.Null(snapshot.FindAttachmentBinding(parent.Occurrence.OriginalRevisionKey!));
        Assert.Null(MediaAttachmentResolver.Resolve(snapshot, Resource, "900", parent.Metadata));
        Assert.Null(MediaAttachmentResolver.Resolve(snapshot, Resource, "900", parent.Metadata with { AttachmentRevisionKey = null }));
    }

    private static void AssertInventory(MediaAssetSnapshot snapshot, Fixture fixture)
    {
        Assert.Equal(MediaManifestStatus.Available, snapshot.Status);
        Assert.Equal(6, snapshot.ManifestVersion);
        Assert.Equal(fixture.Evidence, snapshot.OriginalEvidence.ToArray());
        Assert.Equal(fixture.Derived, snapshot.DerivedRepresentations.ToArray());
        Assert.Null(snapshot.OriginalEvidence[0].OriginalByteVersion!.Sha1);
        Assert.Null(snapshot.DerivedRepresentations[0].SourceVersion.Sha1);
        Assert.Null(snapshot.OriginalEvidence[1].Occurrence.OriginalRevisionKey);
    }

    private static async Task AssertDerivedPayloadAsync(string directory, MediaAssetSnapshot snapshot,
        Fixture fixture, bool payloadPresent)
    {
        string path = Path.Combine(directory, DerivedName);
        Assert.Equal(payloadPresent, File.Exists(path));
        if (!payloadPresent) return;
        Assert.Equal(IncomingBytes, await File.ReadAllBytesAsync(path));
        await using Stream verified = await snapshot.OpenVerifiedDerivedReadAsync(fixture.Derived[0]);
        Assert.Equal(0, verified.Position);
        using MemoryStream copy = new();
        await verified.CopyToAsync(copy);
        Assert.Equal(IncomingBytes, copy.ToArray());
    }

    private static string AliasName(bool upperCase)
    {
        string name = upperCase ? DerivedName.ToUpperInvariant() : DerivedName;
        Assert.Equal(DerivedName, name, ignoreCase: true);
        Assert.Equal(!upperCase, string.Equals(name, DerivedName, StringComparison.Ordinal));
        return name;
    }

    private static MediaProviderMetadata Provider(string id, byte[] bytes)
    {
        MediaActualIntegrity actual = Actual(bytes);
        return new(id, "original", "https://stub.test/media/" + id + ".bin", id + ".bin",
            actual.Length, actual.Md5, actual.Sha256) { ExpectedSha1 = actual.Sha1 };
    }

    private static DownloadItem Item(StubImageBoard board, string directory, string id, string fileName,
        byte[] bytes, string? post, string? legacyPath = null)
    {
        MediaProviderMetadata provider = Provider(id, bytes);
        return new(new Uri(provider.Url), Path.Combine(directory, fileName), board,
            board.GetThread(new Uri("https://stub.test/g/thread/123")), id, provider.ExpectedSize,
            provider.ExpectedMd5, provider.OriginalFileName, legacyPath, provider.Variant)
        {
            ExpectedSha256 = provider.ExpectedSha256, ExpectedSha1 = provider.ExpectedSha1,
            AcquisitionRevisionKey = MediaRevisionKeys.CreateAcquisitionKey(Resource, provider),
            AttachmentReferences = post == null ? [] : [new(Resource, post, 0,
                MediaRevisionKeys.CreateAttachmentKey(Resource, post, 0, provider))]
        };
    }

    private static MediaActualIntegrity Actual(byte[] bytes) => new(bytes.Length, Convert.ToBase64String(MD5.HashData(bytes)),
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()) { Sha1 = Convert.ToBase64String(SHA1.HashData(bytes)) };

    private static async Task<JsonObject> ReadManifestAsync(string directory)
        => JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(directory, ManifestName)))!.AsObject();

    private static async Task<Dictionary<string, byte[]>> CaptureFilesAsync(string directory)
    {
        Dictionary<string, byte[]> files = new(StringComparer.Ordinal);
        foreach (string path in Directory.GetFiles(directory))
            files.Add(Path.GetFileName(path), await File.ReadAllBytesAsync(path));
        return files;
    }

    private sealed record Fixture(MediaAttachmentBinding ExistingBinding, MediaOriginalEvidence[] Evidence,
        MediaDerivedRepresentation[] Derived, MediaAssetSnapshot Snapshot, byte[] ManifestBytes);
}
