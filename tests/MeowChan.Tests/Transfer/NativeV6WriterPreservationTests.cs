using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using MeowChan.Archive;
using MeowChan.Downloader;
using MeowChan.Storage;

namespace MeowChan.Tests.Transfer;

// Synthetic v6 wire over real MediaStore fixtures; not a journalled native importer.
public sealed class NativeV6WriterPreservationTests
{
    private const string ManifestName = ".meowchan-manifest.json";
    private static readonly byte[] OriginalBytes = [1, 3, 5, 7];
    private static readonly byte[] PreviewBytes = [2, 4, 6, 8, 10];
    private static readonly ResourceId Resource = new("stub", "g", "123");
    private static readonly MediaPassiveObservation Origin = new("b8cce56f-562e-4c44-8a65-a68879108f11",
        new string('c', 64), new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero));

    public static IEnumerable<object[]> PreservationCases()
    {
        foreach (bool passive in new[] { false, true })
        foreach (int inventory in new[] { 0, 1, 2 }) // Empty; evidence only; evidence + derived.
        foreach (string writer in new[] { "new-commit", "existing-commit", "restore-untracked" })
            yield return [passive, inventory, writer];
    }

    [Theory]
    [MemberData(nameof(PreservationCases))]
    public async Task ExistingWriterPreservesV6InventoryExactlyAcrossRestart(bool passive, int inventory, string writer)
    {
        using TempDirectory directory = new();
        using StubImageBoard board = new();
        MediaAttachmentBinding existing = await SeedOriginalAsync(directory.Path, board, passive);
        (MediaOriginalEvidence[] evidence, MediaDerivedRepresentation[] derived) = Inventory(inventory);
        await PromoteAsync(directory.Path, evidence, derived);
        JsonObject before = await ReadAsync(directory.Path);
        byte[] originalBefore = await File.ReadAllBytesAsync(Path.Combine(directory.Path, "seed.bin"));
        MediaAssetSnapshot oldSnapshot = await MediaAssetReader.OpenAsync(directory.Path);
        AssertInventory(oldSnapshot, evidence, derived);
        Assert.Equal(existing, oldSnapshot.FindAttachmentBinding(existing.Reference.RevisionKey));

        MediaCommitResult written = await ExerciseWriterAsync(directory.Path, board, writer);

        MediaAssetSnapshot restarted = await MediaAssetReader.OpenAsync(directory.Path);
        AssertInventory(restarted, evidence, derived);
        AssertInventory(oldSnapshot, evidence, derived); // Writer cannot mutate a previously returned view.
        Assert.Equal(existing, restarted.FindAttachmentBinding(existing.Reference.RevisionKey));
        Assert.Equal(originalBefore, await File.ReadAllBytesAsync(Path.Combine(directory.Path, "seed.bin")));
        Assert.Equal(writer == "existing-commit" ? 1 : 2, restarted.Count);
        Assert.Equal(writer switch { "new-commit" or "existing-commit" => 2, _ => 1 }, restarted.AttachmentBindings.Count);
        Assert.NotNull(restarted.FindById(written.AssetId!));
        JsonObject after = await ReadAsync(directory.Path);
        Assert.Equal(6, after["Version"]!.GetValue<int>());
        AssertJsonEqual(before["OriginalEvidence"], after["OriginalEvidence"]);
        AssertJsonEqual(before["DerivedRepresentations"], after["DerivedRepresentations"]);
        AssertJsonEqual(before["AttachmentBindings"]![existing.Reference.RevisionKey],
            after["AttachmentBindings"]![existing.Reference.RevisionKey]);
        foreach (MediaDerivedRepresentation member in derived.Where(value => value.Blob != null))
        {
            Assert.Equal(PreviewBytes, await File.ReadAllBytesAsync(Path.Combine(directory.Path, member.Blob!.RelativeFileName)));
            await using Stream input = await restarted.OpenVerifiedDerivedReadAsync(member);
            Assert.Equal(0, input.Position);
            using MemoryStream copy = new();
            await input.CopyToAsync(copy);
            Assert.Equal(PreviewBytes, copy.ToArray());
        }
        Assert.Empty(Directory.GetFiles(directory.Path, "*.part"));
        Assert.Empty(Directory.GetFiles(directory.Path, "*.corrupt-*"));
    }

    [Theory]
    [InlineData(false, "new-commit")]
    [InlineData(false, "existing-commit")]
    [InlineData(false, "restore-untracked")]
    [InlineData(true, "new-commit")]
    [InlineData(true, "existing-commit")]
    [InlineData(true, "restore-untracked")]
    public async Task OldActiveV4AndPassiveV5WritesDoNotAcquireV6Shape(bool passive, string writer)
    {
        using TempDirectory directory = new();
        using StubImageBoard board = new();
        MediaAttachmentBinding existing = await SeedOriginalAsync(directory.Path, board, passive);
        JsonObject before = await ReadAsync(directory.Path);

        await ExerciseWriterAsync(directory.Path, board, writer);

        JsonObject after = await ReadAsync(directory.Path);
        Assert.Equal(passive ? 5 : 4, after["Version"]!.GetValue<int>());
        Assert.Equal(before.Select(pair => pair.Key).Order(StringComparer.Ordinal).ToArray(),
            after.Select(pair => pair.Key).Order(StringComparer.Ordinal).ToArray());
        Assert.False(after.ContainsKey("OriginalEvidence"));
        Assert.False(after.ContainsKey("DerivedRepresentations"));
        AssertJsonEqual(before["AttachmentBindings"]![existing.Reference.RevisionKey],
            after["AttachmentBindings"]![existing.Reference.RevisionKey]);
        MediaAssetSnapshot restarted = await MediaAssetReader.OpenAsync(directory.Path);
        Assert.Equal(MediaManifestStatus.Available, restarted.Status);
        Assert.Equal(passive ? 5 : 4, restarted.ManifestVersion);
        Assert.Equal(existing, restarted.FindAttachmentBinding(existing.Reference.RevisionKey));
        Assert.Empty(restarted.OriginalEvidence);
        Assert.Empty(restarted.DerivedRepresentations);
        Assert.Equal(writer == "existing-commit" ? 1 : 2, restarted.Count);
        Assert.Equal(writer switch { "new-commit" or "existing-commit" => 2, _ => 1 }, restarted.AttachmentBindings.Count);
        foreach (var binding in after["AttachmentBindings"]!.AsObject())
        {
            bool isPassive = binding.Key == existing.Reference.RevisionKey && passive;
            Assert.Equal(isPassive, binding.Value!.AsObject().ContainsKey("PassiveImport"));
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task FirstRealOriginalAddedToV6DoesNotDownlevelAnOtherwiseEmptyOriginalStore(int inventory)
    {
        using TempDirectory directory = new();
        using StubImageBoard board = new();
        await File.WriteAllTextAsync(Path.Combine(directory.Path, ManifestName),
            "{\"Version\":6,\"Entries\":{},\"Assets\":{},\"AttachmentBindings\":{},\"OriginalEvidence\":[],\"DerivedRepresentations\":[]}");
        (MediaOriginalEvidence[] evidence, MediaDerivedRepresentation[] derived) = Inventory(inventory);
        await PromoteAsync(directory.Path, evidence, derived);
        MediaAssetSnapshot before = await MediaAssetReader.OpenAsync(directory.Path);
        AssertInventory(before, evidence, derived);
        Assert.Equal(0, before.Count);
        Assert.Empty(before.AttachmentBindings);

        MediaCommitResult committed = await CommitAsync(Item(board, directory.Path, "fresh", "124"));

        MediaAssetSnapshot restarted = await MediaAssetReader.OpenAsync(directory.Path);
        AssertInventory(restarted, evidence, derived);
        Assert.Equal(committed.AssetId, Assert.Single(restarted.GetPage()).AssetId);
        Assert.Equal(MediaAcceptanceKind.Strict, Assert.Single(restarted.AttachmentBindings).Value.Acceptance.Kind);
        Assert.Equal(6, (await ReadAsync(directory.Path))["Version"]!.GetValue<int>());
        Assert.Equal(0, before.Count);
        AssertInventory(before, evidence, derived);
    }

    private static async Task<MediaAttachmentBinding> SeedOriginalAsync(string directory, StubImageBoard board, bool passive)
    {
        await CommitAsync(Item(board, directory, "seed", "123"));
        MediaAttachmentBinding binding = Assert.Single((await MediaAssetReader.OpenAsync(directory)).AttachmentBindings).Value;
        if (passive)
        {
            // Same fixture technique as PassiveMediaManifestTests: a real asset with explicit passive wire provenance.
            binding = binding with
            {
                Acceptance = new(MediaAcceptanceKind.PassiveImport, false, false, Origin.ImportedAtUtc),
                PassiveImport = new(Origin.OperationId, Origin.PackageFingerprint, Origin.ImportedAtUtc,
                    binding.Reference.RevisionKey, new(MediaAcceptanceKind.Strict, Origin.ImportedAtUtc.AddDays(-2), false, false))
            };
            JsonObject manifest = await ReadAsync(directory);
            manifest["Version"] = 5;
            manifest["AttachmentBindings"]![binding.Reference.RevisionKey] = JsonSerializer.SerializeToNode(binding);
            await File.WriteAllTextAsync(Path.Combine(directory, ManifestName), manifest.ToJsonString());
        }
        MediaAssetSnapshot snapshot = await MediaAssetReader.OpenAsync(directory);
        Assert.Equal(MediaManifestStatus.Available, snapshot.Status);
        Assert.Equal(passive ? 5 : 4, snapshot.ManifestVersion);
        Assert.Equal(binding, Assert.Single(snapshot.AttachmentBindings).Value);
        return binding;
    }

    private static (MediaOriginalEvidence[], MediaDerivedRepresentation[]) Inventory(int mode)
    {
        if (mode == 0) return ([], []);
        MediaProviderMetadata provider = Provider("absent");
        string key = MediaRevisionKeys.CreateAttachmentKey(Resource, "900", 0, provider);
        CapturedAttachment metadata = new(0, provider.MediaId, provider.Variant, provider.Url, provider.OriginalFileName,
            ExpectedSize: provider.ExpectedSize, ExpectedMd5: provider.ExpectedMd5)
        { AttachmentRevisionKey = key, ExpectedSha256 = provider.ExpectedSha256, ExpectedSha1 = provider.ExpectedSha1 };
        // Historical source tuple with null SHA1 must stay null, even though active writes hash SHA1.
        MediaOriginalEvidence known = new(new(Resource, "900", 0, key), metadata,
            MediaObservedContentStatus.Omitted, Actual(OriginalBytes) with { Sha1 = null }, Origin);
        MediaOriginalEvidence unknown = new(new(Resource, "901", 0, null), new(0, null, IsDeleted: true),
            MediaObservedContentStatus.Unknown, null, Origin with { ImportedAtUtc = Origin.ImportedAtUtc.AddSeconds(1) });
        if (mode == 1) return ([known, unknown], []);
        MediaActualIntegrity actual = Actual(PreviewBytes);
        return ([known, unknown],
        [
            new(known.Occurrence, known.OriginalByteVersion!, MediaDerivedKind.Preview, MediaDerivedStatus.Contained,
                new(actual.Sha256 + ".derived.bin", actual), known.Origin),
            new(known.Occurrence, known.OriginalByteVersion!, MediaDerivedKind.VideoPosterPng, MediaDerivedStatus.Unavailable,
                null, known.Origin)
        ]);
    }

    private static async Task PromoteAsync(string directory, MediaOriginalEvidence[] evidence, MediaDerivedRepresentation[] derived)
    {
        foreach (var member in derived.Where(value => value.Blob != null))
            await File.WriteAllBytesAsync(Path.Combine(directory, member.Blob!.RelativeFileName), PreviewBytes);
        JsonObject manifest = await ReadAsync(directory);
        manifest["Version"] = 6;
        manifest["OriginalEvidence"] = JsonSerializer.SerializeToNode(evidence);
        manifest["DerivedRepresentations"] = JsonSerializer.SerializeToNode(derived);
        await File.WriteAllTextAsync(Path.Combine(directory, ManifestName), manifest.ToJsonString());
    }

    private static async Task<MediaCommitResult> ExerciseWriterAsync(string directory, StubImageBoard board, string writer)
    {
        if (writer == "new-commit") return await CommitAsync(Item(board, directory, "new", "124"));
        if (writer == "existing-commit") return await CommitAsync(Item(board, directory, "seed", "124"));
        Assert.Equal("restore-untracked", writer);
        // No attachment references: strict provider size+MD5 permits the existing restore writer to add this real file.
        DownloadItem item = Item(board, directory, "restored", null);
        await File.WriteAllBytesAsync(item.DestinationPath, OriginalBytes);
        MediaCommitResult? restored = await new MediaStore().TryRestoreAsync(item, default);
        Assert.NotNull(restored);
        Assert.True(restored.Reused);
        return restored;
    }

    private static void AssertInventory(MediaAssetSnapshot snapshot, MediaOriginalEvidence[] evidence, MediaDerivedRepresentation[] derived)
    {
        Assert.Equal(MediaManifestStatus.Available, snapshot.Status);
        Assert.Equal(6, snapshot.ManifestVersion);
        Assert.False(snapshot.OriginalEvidence.IsDefault);
        Assert.False(snapshot.DerivedRepresentations.IsDefault);
        Assert.Equal(evidence, snapshot.OriginalEvidence.ToArray());
        Assert.Equal(derived, snapshot.DerivedRepresentations.ToArray());
        foreach (var item in evidence)
            Assert.Null(MediaAttachmentResolver.Resolve(snapshot, item.Occurrence.Resource, item.Occurrence.PostId, item.Metadata));
    }

    private static MediaProviderMetadata Provider(string id) => new(id, "original", "https://stub.test/media/" + id + ".bin",
        id + ".bin", OriginalBytes.Length, Convert.ToBase64String(MD5.HashData(OriginalBytes)), Actual(OriginalBytes).Sha256)
    { ExpectedSha1 = Actual(OriginalBytes).Sha1 };

    private static DownloadItem Item(StubImageBoard board, string directory, string id, string? post)
    {
        MediaProviderMetadata provider = Provider(id);
        return new(new Uri(provider.Url), Path.Combine(directory, id + ".bin"), board,
            board.GetThread(new Uri("https://stub.test/g/thread/123")), id, provider.ExpectedSize,
            provider.ExpectedMd5, provider.OriginalFileName, null, provider.Variant)
        {
            ExpectedSha256 = provider.ExpectedSha256, ExpectedSha1 = provider.ExpectedSha1,
            AcquisitionRevisionKey = MediaRevisionKeys.CreateAcquisitionKey(Resource, provider),
            AttachmentReferences = post == null ? [] : [new(Resource, post, 0,
                MediaRevisionKeys.CreateAttachmentKey(Resource, post, 0, provider))]
        };
    }

    private static async Task<MediaCommitResult> CommitAsync(DownloadItem item)
    {
        using MemoryStream stream = new(OriginalBytes, writable: false);
        return await new MediaStore().CommitAsync(item, stream, default);
    }

    private static MediaActualIntegrity Actual(byte[] bytes) => new(bytes.Length, Convert.ToBase64String(MD5.HashData(bytes)),
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()) { Sha1 = Convert.ToBase64String(SHA1.HashData(bytes)) };

    private static Task<JsonObject> ReadAsync(string directory) => ReadFileAsync(Path.Combine(directory, ManifestName));
    private static async Task<JsonObject> ReadFileAsync(string path) => JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
    private static void AssertJsonEqual(JsonNode? expected, JsonNode? actual)
    {
        Assert.NotNull(expected);
        Assert.NotNull(actual);
        Assert.True(JsonNode.DeepEquals(expected, actual), "Existing manifest evidence must survive a writer cycle without value loss.");
    }
}
