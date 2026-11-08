using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using MeowChan.Archive;
using MeowChan.Downloader;
using MeowChan.Storage;

namespace MeowChan.Tests.Transfer;

// W32a contract fixtures: real active asset/binding first, then explicit synthetic
// historical/passive wire. This is NOT a journalled v4 import/publication witness.
public sealed class NativeV6BindingEvidenceTests
{
    private const string ManifestName = ".meowchan-manifest.json";
    private const string Post = "123";
    private const int Position = 2;
    private static readonly byte[] Bytes = [1, 3, 5, 7];
    private static readonly ResourceId Resource = new("stub", "g", "123");
    private static readonly MediaPassiveObservation Origin = new("2ec1e186-20cc-4ba3-80ab-94f9ee2581be",
        new string('c', 64), new DateTimeOffset(2026, 11, 7, 12, 0, 0, TimeSpan.Zero));

    public static IEnumerable<object[]> CompatibleCases()
    {
        foreach (bool passiveBinding in new[] { false, true })
        foreach (bool bindingSha1 in new[] { false, true })
        foreach (bool evidenceSha1 in new[] { false, true })
            yield return [passiveBinding, bindingSha1, evidenceSha1];
    }

    [Theory]
    [MemberData(nameof(CompatibleCases))]
    public async Task ReadJoinsCompatibleHistoricalEvidenceWithoutManufacturingEitherSha1(
        bool passiveBinding, bool bindingSha1, bool evidenceSha1)
    {
        using TempDirectory directory = new();
        using StubImageBoard board = new();
        Fixture fixture = await SeedAsync(directory.Path, board, Provider(), bindingSha1, passiveBinding);
        MediaOriginalEvidence evidence = Evidence(fixture.Binding,
            Actual() with { Sha1 = evidenceSha1 ? Actual().Sha1 : null });
        await PromoteAsync(fixture, evidence);
        byte[] before = await File.ReadAllBytesAsync(fixture.ManifestPath);
        string[] paths = Paths(directory.Path);

        foreach (MediaAssetSnapshot snapshot in new[]
        {
            await MediaAssetReader.OpenAsync(directory.Path),
            await MediaAssetReader.OpenAsync(directory.Path)
        })
        {
            AssertJoined(snapshot, fixture.Binding, evidence);
            Assert.Equal(bindingSha1 ? Actual().Sha1 : null,
                snapshot.FindAttachmentBinding(fixture.Binding.Reference.RevisionKey)!.Actual.Sha1);
            Assert.Equal(evidenceSha1 ? Actual().Sha1 : null, snapshot.OriginalEvidence[0].OriginalByteVersion!.Sha1);
            Assert.Equal(bindingSha1 ? Actual().Sha1 : null, Assert.Single(snapshot.GetPage()).Sha1);
            ResolvedMediaAttachment resolved = Assert.IsType<ResolvedMediaAttachment>(MediaAttachmentResolver.Resolve(
                snapshot, Resource, Post, evidence.Metadata));
            Assert.False(resolved.IsLegacy);
            Assert.Equal(fixture.Binding, resolved.Binding);
            Assert.Equal(fixture.Committed.AssetId, resolved.Asset.AssetId);
            await using Stream input = await snapshot.OpenVerifiedReadAsync(resolved.Asset.AssetId);
            Assert.Equal(0L, input.Position);
            using MemoryStream copy = new();
            await input.CopyToAsync(copy);
            Assert.Equal(Bytes, copy.ToArray());
            // Even explicitly verifying the Original cannot enrich the immutable read view.
            AssertJoined(snapshot, fixture.Binding, evidence);
        }

        Assert.Equal(before, await File.ReadAllBytesAsync(fixture.ManifestPath));
        Assert.Equal(Bytes, await File.ReadAllBytesAsync(fixture.Committed.Path));
        Assert.Equal(paths, Paths(directory.Path));
    }

    [Theory]
    [InlineData("sha1")]
    [InlineData("length")]
    [InlineData("md5")]
    [InlineData("sha256")]
    public async Task ContradictoryKnownEvidenceRejectsTheWholeManifestWithoutRepair(string field)
    {
        using TempDirectory directory = new();
        using StubImageBoard board = new();
        Fixture fixture = await SeedAsync(directory.Path, board, Provider(), bindingSha1: true);
        MediaOriginalEvidence valid = Evidence(fixture.Binding, Actual());
        await PromoteAsync(fixture, valid);
        AssertJoined(await MediaAssetReader.OpenAsync(directory.Path), fixture.Binding, valid);
        MediaActualIntegrity conflicting = field switch
        {
            "sha1" => Actual() with { Sha1 = DifferentSha1() },
            "length" => Actual() with { Length = Bytes.LongLength + 1 },
            "md5" => Actual() with { Md5 = Convert.ToBase64String(MD5.HashData([99])) },
            "sha256" => Actual() with { Sha256 = new string('a', 64) },
            _ => throw new ArgumentOutOfRangeException(nameof(field))
        };
        // All tuple fields remain well-formed; only the real-binding join contradicts.
        await PromoteAsync(fixture, valid with { OriginalByteVersion = conflicting });
        byte[] before = await File.ReadAllBytesAsync(fixture.ManifestPath);
        string[] paths = Paths(directory.Path);

        MediaAssetSnapshot snapshot = await MediaAssetReader.OpenAsync(directory.Path);

        Assert.Equal(MediaManifestStatus.Corrupt, snapshot.Status);
        Assert.Equal(0, snapshot.Count);
        Assert.Empty(snapshot.GetPage());
        Assert.Empty(snapshot.AttachmentBindings);
        Assert.Empty(snapshot.OriginalEvidence);
        Assert.Empty(snapshot.DerivedRepresentations);
        Assert.Null(snapshot.Generation);
        Assert.Null(snapshot.FindById(fixture.Binding.AssetId));
        Assert.Null(MediaAttachmentResolver.Resolve(snapshot, Resource, Post, valid.Metadata));
        Assert.Equal(before, await File.ReadAllBytesAsync(fixture.ManifestPath));
        Assert.Equal(Bytes, await File.ReadAllBytesAsync(fixture.Committed.Path));
        Assert.Equal(paths, Paths(directory.Path));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActiveCommitFreshlyMeasuresAndEnforcesStrongerPassiveSha1(bool contradictorySha1)
    {
        using TempDirectory directory = new();
        using StubImageBoard board = new();
        MediaProviderMetadata provider = Provider(); // No provider SHA1: passive evidence supplies it.
        Fixture fixture = await SeedAsync(directory.Path, board, provider, bindingSha1: false);
        MediaOriginalEvidence evidence = Evidence(fixture.Binding,
            Actual() with { Sha1 = contradictorySha1 ? DifferentSha1() : Actual().Sha1 });
        await PromoteAsync(fixture, evidence);
        JsonObject manifest = await ReadAsync(fixture.ManifestPath);
        manifest["AttachmentBindings"] = new JsonObject();
        await File.WriteAllTextAsync(fixture.ManifestPath, manifest.ToJsonString());
        MediaAssetSnapshot before = await MediaAssetReader.OpenAsync(directory.Path);
        Assert.Equal(MediaManifestStatus.Available, before.Status);
        Assert.Equal(evidence, Assert.Single(before.OriginalEvidence));
        Assert.Empty(before.AttachmentBindings);
        Assert.Null(Assert.Single(before.GetPage()).Sha1);
        byte[] wire = await File.ReadAllBytesAsync(fixture.ManifestPath);
        string[] paths = Paths(directory.Path);
        DownloadItem item = Item(board, directory.Path, provider, Post);
        using MemoryStream source = new(Bytes, writable: false);

        if (contradictorySha1)
        {
            // Equal Length/MD5/SHA256 and a known historical SHA1 are not a wildcard
            // once fresh bytes are available. In particular, no new binding may publish.
            await Assert.ThrowsAsync<InvalidDataException>(() => new MediaStore().CommitAsync(item, source, default));
            Assert.Equal(Bytes.LongLength, source.Position);
            Assert.Equal(wire, await File.ReadAllBytesAsync(fixture.ManifestPath));
            Assert.Equal(paths, Paths(directory.Path));
            MediaAssetSnapshot rejected = await MediaAssetReader.OpenAsync(directory.Path);
            Assert.Equal(MediaManifestStatus.Available, rejected.Status);
            Assert.Equal(evidence, Assert.Single(rejected.OriginalEvidence));
            Assert.Empty(rejected.AttachmentBindings);
        }
        else
        {
            MediaCommitResult committed = await new MediaStore().CommitAsync(item, source, default);
            Assert.Equal(Bytes.LongLength, source.Position);
            Assert.True(committed.Reused);
            Assert.Equal(fixture.Committed.AssetId, committed.AssetId);
            MediaAssetSnapshot restarted = await MediaAssetReader.OpenAsync(directory.Path);
            Assert.Equal(MediaManifestStatus.Available, restarted.Status);
            Assert.Equal(6, restarted.ManifestVersion);
            Assert.Equal(1, restarted.Count);
            Assert.Single(restarted.AttachmentBindings);
            Assert.Equal(evidence, Assert.Single(restarted.OriginalEvidence));
            Assert.Empty(restarted.DerivedRepresentations);
            MediaAttachmentBinding binding = Assert.Single(restarted.AttachmentBindings).Value;
            Assert.Equal(Actual(), binding.Actual); // Fresh active measurement adds SHA1; evidence stays unchanged.
            Assert.Equal(MediaAcceptanceKind.Strict, binding.Acceptance.Kind);
            Assert.Null(binding.PassiveImport);
            Assert.Equal(Actual().Sha1, Assert.Single(restarted.GetPage()).Sha1);
            Assert.Equal(evidence, Assert.Single(before.OriginalEvidence));
            JsonObject after = await ReadAsync(fixture.ManifestPath);
            JsonObject original = JsonNode.Parse(wire)!.AsObject();
            Assert.True(JsonNode.DeepEquals(original["OriginalEvidence"], after["OriginalEvidence"]));
            Assert.True(JsonNode.DeepEquals(original["DerivedRepresentations"], after["DerivedRepresentations"]));
        }
        Assert.Equal(Bytes, await File.ReadAllBytesAsync(fixture.Committed.Path));
        // A writer is not permitted to mutate an already-returned snapshot.
        Assert.Equal(evidence, Assert.Single(before.OriginalEvidence));
        Assert.Null(Assert.Single(before.GetPage()).Sha1);
        Assert.Empty(before.AttachmentBindings);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task HistoricalBindingCannotBypassKnownPassiveSha1AtFreshCommitOrRestore(
        bool restore, bool contradictorySha1)
    {
        using TempDirectory directory = new();
        using StubImageBoard board = new();
        MediaProviderMetadata provider = Provider();
        Fixture fixture = await SeedAsync(directory.Path, board, provider, bindingSha1: false);
        MediaOriginalEvidence evidence = Evidence(fixture.Binding,
            Actual() with { Sha1 = contradictorySha1 ? DifferentSha1() : Actual().Sha1 });
        await PromoteAsync(fixture, evidence);
        MediaAssetSnapshot before = await MediaAssetReader.OpenAsync(directory.Path);
        AssertJoined(before, fixture.Binding, evidence); // Read compatibility is not fresh-byte verification.
        byte[] wire = await File.ReadAllBytesAsync(fixture.ManifestPath);
        string[] paths = Paths(directory.Path);
        List<string> diskHashes = new();
        MediaStore store = new(File.Move, onFileHashStarting: path => diskHashes.Add(path));
        DownloadItem item = Item(board, directory.Path, provider, Post);
        using MemoryStream source = new(Bytes, writable: false);
        async Task<MediaCommitResult?> ExecuteAsync() => restore
            ? await store.TryRestoreAsync(item, default)
            : await store.CommitAsync(item, source, default);

        if (contradictorySha1)
        {
            await Assert.ThrowsAsync<InvalidDataException>(async () => { await ExecuteAsync(); });
            Assert.Equal(wire, await File.ReadAllBytesAsync(fixture.ManifestPath));
            Assert.Equal(paths, Paths(directory.Path));
        }
        else
        {
            MediaCommitResult result = Assert.IsType<MediaCommitResult>(await ExecuteAsync());
            Assert.True(result.Reused);
            Assert.Equal(fixture.Committed.AssetId, result.AssetId);
            Assert.Equal(Actual().Sha256, result.Sha256);
            if (restore) Assert.Equal(wire, await File.ReadAllBytesAsync(fixture.ManifestPath));
            else
            {
                JsonObject original = JsonNode.Parse(wire)!.AsObject();
                JsonObject after = await ReadAsync(fixture.ManifestPath);
                Assert.True(JsonNode.DeepEquals(original["OriginalEvidence"], after["OriginalEvidence"]));
                Assert.True(JsonNode.DeepEquals(original["AttachmentBindings"], after["AttachmentBindings"]));
            }
        }
        if (restore)
        {
            // Existing observer reports real disk hashing, not a fabricated digest/cache hit.
            Assert.Equal(fixture.Committed.Path, Assert.Single(diskHashes));
            Assert.Equal(0L, source.Position);
        }
        else Assert.Equal(Bytes.LongLength, source.Position);
        AssertJoined(await MediaAssetReader.OpenAsync(directory.Path), fixture.Binding, evidence);
        AssertJoined(before, fixture.Binding, evidence);
        Assert.Null(before.FindAttachmentBinding(fixture.Binding.Reference.RevisionKey)!.Actual.Sha1);
        Assert.Null(Assert.Single(before.GetPage()).Sha1);
        Assert.Equal(Bytes, await File.ReadAllBytesAsync(fixture.Committed.Path));
    }

    public static IEnumerable<object[]> ProviderMetadataCases()
    {
        // Every combination of optional expectations, plus all three size-hint precisions.
        for (int mask = 0; mask < 16; mask++) yield return [mask, -1];
        for (int precision = 0; precision <= 2; precision++) yield return [15, precision];
    }

    [Theory]
    [MemberData(nameof(ProviderMetadataCases))]
    public async Task ProviderRevisionMetadataRetainsExactSpellingsRatherThanCanonicalizingExpectations(int mask, int precision)
    {
        using TempDirectory directory = new();
        using StubImageBoard board = new();
        MediaProviderMetadata provider = Provider() with
        {
            OriginalFileName = "Original Ä.bin",
            Url = "https://stub.test/media/seed.bin?token=MixedCase&v=02",
            ExpectedSize = (mask & 1) != 0 ? Bytes.LongLength : null,
            ExpectedMd5 = (mask & 2) != 0 ? SpacedBase64(Actual().Md5) : null,
            ExpectedSha256 = (mask & 4) != 0 ? Actual().Sha256.ToUpperInvariant() : null,
            ExpectedSha1 = (mask & 8) != 0 ? SpacedBase64(Actual().Sha1!) : null,
            ProviderSizeHint = precision < 0 ? null : new("0004.00", " octets ", (MediaSizePrecision)precision)
        };
        Fixture fixture = await SeedAsync(directory.Path, board, provider, bindingSha1: true);
        MediaOriginalEvidence evidence = Evidence(fixture.Binding, Actual()) with
        {
            Metadata = Metadata(fixture.Binding) with { IsDeleted = true }
        };
        await PromoteAsync(fixture, evidence);
        byte[] before = await File.ReadAllBytesAsync(fixture.ManifestPath);
        string[] paths = Paths(directory.Path);

        for (int restart = 0; restart < 2; restart++)
        {
            MediaAssetSnapshot snapshot = await MediaAssetReader.OpenAsync(directory.Path);
            AssertJoined(snapshot, fixture.Binding, evidence);
            CapturedAttachment metadata = Assert.Single(snapshot.OriginalEvidence).Metadata;
            Assert.Equal(Position, metadata.Position);
            Assert.Equal(provider.MediaId, metadata.MediaId);
            Assert.Equal(provider.Variant, metadata.Variant);
            Assert.Equal(provider.Url, metadata.Url);
            Assert.Equal(provider.OriginalFileName, metadata.OriginalFileName);
            Assert.Null(metadata.PlannedPath);
            Assert.Equal(provider.ExpectedSize, metadata.ExpectedSize);
            Assert.Equal(provider.ExpectedMd5, metadata.ExpectedMd5);
            Assert.Equal(provider.ExpectedSha256, metadata.ExpectedSha256);
            Assert.Equal(provider.ExpectedSha1, metadata.ExpectedSha1);
            Assert.Equal(provider.ProviderSizeHint, metadata.ProviderSizeHint);
            Assert.True(metadata.IsDeleted);
            Assert.Equal(MediaRevisionKeys.CreateAttachmentKey(Resource, Post, Position, provider), metadata.AttachmentRevisionKey);
            Assert.Equal(MediaRevisionKeys.CreateAcquisitionKey(Resource, provider), fixture.Binding.AcquisitionRevisionKey);
            Assert.Equal(provider, snapshot.FindAttachmentBinding(metadata.AttachmentRevisionKey!)!.Provider);
            Assert.Equal(fixture.Binding, Assert.IsType<ResolvedMediaAttachment>(MediaAttachmentResolver.Resolve(
                snapshot, Resource, Post, metadata)).Binding);
        }
        Assert.Equal(before, await File.ReadAllBytesAsync(fixture.ManifestPath));
        Assert.Equal(paths, Paths(directory.Path));
        // An ordinary existing-binding writer must preserve these raw spellings too.
        using MemoryStream source = new(Bytes, writable: false);
        await new MediaStore().CommitAsync(Item(board, directory.Path, provider, Post), source, default);
        AssertJoined(await MediaAssetReader.OpenAsync(directory.Path), fixture.Binding, evidence);
        JsonObject originalWire = JsonNode.Parse(before)!.AsObject();
        JsonObject savedWire = await ReadAsync(fixture.ManifestPath);
        Assert.True(JsonNode.DeepEquals(originalWire["OriginalEvidence"], savedWire["OriginalEvidence"]));
        Assert.True(JsonNode.DeepEquals(originalWire["AttachmentBindings"], savedWire["AttachmentBindings"]));
    }

    private static async Task<Fixture> SeedAsync(string directory, StubImageBoard board,
        MediaProviderMetadata provider, bool bindingSha1, bool passiveBinding = false)
    {
        DownloadItem item = Item(board, directory, provider, Post);
        using MemoryStream input = new(Bytes, writable: false);
        MediaCommitResult committed = await new MediaStore().CommitAsync(item, input, default);
        MediaAssetSnapshot active = await MediaAssetReader.OpenAsync(directory);
        Assert.Equal(MediaManifestStatus.Available, active.Status);
        MediaAttachmentBinding binding = Assert.Single(active.AttachmentBindings).Value;
        Assert.Equal(Actual(), binding.Actual);
        string path = Path.Combine(directory, ManifestName);
        JsonObject manifest = await ReadAsync(path);
        if (!bindingSha1)
        {
            // Model a genuine historical tuple, not a fresh measurement with a fake digest.
            // Provider() omits expected SHA1, so this is a valid prior active binding.
            binding = binding with { Actual = binding.Actual with { Sha1 = null } };
            foreach (var entry in manifest["Entries"]!.AsObject()) entry.Value!["Sha1"] = null;
            foreach (var asset in manifest["Assets"]!.AsObject()) asset.Value!["Entry"]!["Sha1"] = null;
        }
        if (passiveBinding)
        {
            binding = binding with
            {
                Acceptance = new(MediaAcceptanceKind.PassiveImport, false, false, Origin.ImportedAtUtc),
                PassiveImport = new(Origin.OperationId, Origin.PackageFingerprint, Origin.ImportedAtUtc,
                    binding.Reference.RevisionKey, null)
            };
        }
        manifest["Version"] = passiveBinding ? 5 : 4;
        manifest["AttachmentBindings"]![binding.Reference.RevisionKey] = JsonSerializer.SerializeToNode(binding);
        await File.WriteAllTextAsync(path, manifest.ToJsonString());
        // Positively validate the prior real wire before adding the v6 join under test.
        MediaAssetSnapshot prior = await MediaAssetReader.OpenAsync(directory);
        Assert.Equal(MediaManifestStatus.Available, prior.Status);
        Assert.Equal(passiveBinding ? 5 : 4, prior.ManifestVersion);
        Assert.Equal(binding, Assert.Single(prior.AttachmentBindings).Value);
        Assert.Empty(prior.OriginalEvidence);
        return new(path, committed, binding);
    }

    private static void AssertJoined(MediaAssetSnapshot snapshot, MediaAttachmentBinding binding, MediaOriginalEvidence evidence)
    {
        Assert.Equal(MediaManifestStatus.Available, snapshot.Status);
        Assert.Equal(6, snapshot.ManifestVersion);
        Assert.Null(snapshot.Warning);
        Assert.Equal(1, snapshot.Count);
        Assert.Single(snapshot.GetPage());
        Assert.Equal(binding, Assert.Single(snapshot.AttachmentBindings).Value);
        Assert.Equal(binding, snapshot.FindAttachmentBinding(binding.Reference.RevisionKey));
        Assert.Equal(evidence, Assert.Single(snapshot.OriginalEvidence));
        Assert.Equal(evidence.Origin, snapshot.OriginalEvidence[0].Origin);
        Assert.Empty(snapshot.DerivedRepresentations);
    }

    private static MediaOriginalEvidence Evidence(MediaAttachmentBinding binding, MediaActualIntegrity version)
        => new(new(binding.Reference.Resource, binding.Reference.PostId, binding.Reference.Position, binding.Reference.RevisionKey),
            Metadata(binding), MediaObservedContentStatus.Omitted, version, Origin);

    private static CapturedAttachment Metadata(MediaAttachmentBinding binding)
        => new(binding.Reference.Position, binding.Provider.MediaId, binding.Provider.Variant,
            binding.Provider.Url, binding.Provider.OriginalFileName,
            ExpectedSize: binding.Provider.ExpectedSize, ExpectedMd5: binding.Provider.ExpectedMd5)
        {
            ExpectedSha256 = binding.Provider.ExpectedSha256, ExpectedSha1 = binding.Provider.ExpectedSha1,
            ProviderSizeHint = binding.Provider.ProviderSizeHint, AttachmentRevisionKey = binding.Reference.RevisionKey
        };

    private static MediaProviderMetadata Provider() => new("seed", "original", "https://stub.test/media/seed.bin",
        "seed.bin", Bytes.LongLength, Actual().Md5, Actual().Sha256);

    private static DownloadItem Item(StubImageBoard board, string directory, MediaProviderMetadata provider, params string[] posts)
        => new(new Uri(provider.Url), Path.Combine(directory, "seed.bin"), board,
            board.GetThread(new Uri("https://stub.test/g/thread/123")), provider.MediaId, provider.ExpectedSize,
            provider.ExpectedMd5, provider.OriginalFileName, null, provider.Variant)
        {
            ExpectedSha256 = provider.ExpectedSha256, ExpectedSha1 = provider.ExpectedSha1,
            ProviderSizeHint = provider.ProviderSizeHint,
            AcquisitionRevisionKey = MediaRevisionKeys.CreateAcquisitionKey(Resource, provider),
            AttachmentReferences = posts.Select(post => new AttachmentRevisionReference(Resource, post, Position,
                MediaRevisionKeys.CreateAttachmentKey(Resource, post, Position, provider))).ToArray()
        };

    private static async Task PromoteAsync(Fixture fixture, MediaOriginalEvidence evidence)
    {
        JsonObject manifest = await ReadAsync(fixture.ManifestPath);
        manifest["Version"] = 6;
        manifest["OriginalEvidence"] = JsonSerializer.SerializeToNode(new[] { evidence });
        manifest["DerivedRepresentations"] = new JsonArray();
        await File.WriteAllTextAsync(fixture.ManifestPath, manifest.ToJsonString());
    }

    private static MediaActualIntegrity Actual() => new(Bytes.LongLength, Convert.ToBase64String(MD5.HashData(Bytes)),
        Convert.ToHexString(SHA256.HashData(Bytes)).ToLowerInvariant()) { Sha1 = Convert.ToBase64String(SHA1.HashData(Bytes)) };
    private static string DifferentSha1() => Convert.ToBase64String(SHA1.HashData([99]));
    private static string SpacedBase64(string digest) => " \t" + digest[..4] + "\r\n" + digest[4..] + " \n";
    private static string[] Paths(string directory) => Directory.GetFileSystemEntries(directory).Order(StringComparer.Ordinal).ToArray();
    private static async Task<JsonObject> ReadAsync(string path) => JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
    private sealed record Fixture(string ManifestPath, MediaCommitResult Committed, MediaAttachmentBinding Binding);
}
