using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MeowChan.Archive;
using MeowChan.Downloader;
using MeowChan.Storage;

namespace MeowChan.Tests.Transfer;

// Synthetic wire fixtures over the existing store; not journalled import/materialization evidence.
public sealed class NativeV6RepairBoundaryTests
{
    private const string ManifestName = ".meowchan-manifest.json";
    private const string LockName = ".meowchan-store.lock";
    private static readonly byte[] Payload = [1, 3, 5, 7, 11];
    private static readonly ResourceId Resource = new("stub", "g", "123");
    private static readonly string[] Writers = ["commit-new", "restore-untracked"];

    public static IEnumerable<object[]> InvalidVersionCases()
    {
        foreach (string version in new[] { "missing", "null", "string", "object", "array", "boolean",
                     "fractional", "overflow", "zero", "negative" })
        {
            foreach (string field in new[] { "OriginalEvidence", "DerivedRepresentations", "both" })
            foreach (string shape in new[] { "null", "empty" })
                yield return [version, field, shape];
            // Both valid, populated inventories must also survive a damaged Version discriminator.
            yield return [version, "both", "populated"];
        }
    }

    public static IEnumerable<object[]> InvalidVersionWriterCases()
    {
        foreach (object[] input in InvalidVersionCases())
        foreach (string writer in Writers)
            yield return [input[0], input[1], input[2], writer];
    }

    [Theory]
    [MemberData(nameof(InvalidVersionCases))]
    public async Task InvalidVersionWithInventoryPresenceReadsCorruptWithoutRepair(
        string version, string field, string shape)
    {
        using TempDirectory temp = new();
        await WriteFixtureAsync(temp.Path, InvalidVersionManifest(version, field, shape));
        await AssertCorruptReadOnlyAsync(temp.Path);
    }

    [Theory]
    [MemberData(nameof(InvalidVersionWriterCases))]
    public async Task InvalidVersionWithInventoryPresenceCannotBeRepairedByReferenceFreeWriter(
        string version, string field, string shape, string writer)
    {
        using TempDirectory temp = new();
        using StubImageBoard board = new();
        await WriteFixtureAsync(temp.Path, InvalidVersionManifest(version, field, shape));
        await AssertWriterRejectsWithoutMutationAsync(temp.Path, board, writer);
    }

    public static IEnumerable<object[]> MalformedRootCases()
    {
        foreach (int version in new[] { 1, 2, 6 })
        foreach (string field in new[] { "Entries", "Assets", "AttachmentBindings" })
        foreach (string shape in new[] { "null", "array", "string" })
            yield return [version, field, shape];
    }

    public static IEnumerable<object[]> MalformedRootWriterCases()
    {
        foreach (object[] input in MalformedRootCases())
        foreach (string writer in Writers)
            yield return [input[0], input[1], input[2], writer];
    }

    [Theory]
    [MemberData(nameof(MalformedRootCases))]
    public async Task MalformedRootWithInventoriesReadsCorruptWithoutExposingInventories(
        int version, string field, string shape)
    {
        using TempDirectory temp = new();
        await WriteFixtureAsync(temp.Path, MalformedRootManifest(version, field, shape));
        await AssertCorruptReadOnlyAsync(temp.Path);
    }

    [Theory]
    [MemberData(nameof(MalformedRootWriterCases))]
    public async Task MalformedRootWithInventoriesCannotFallThroughLegacyRepair(
        int version, string field, string shape, string writer)
    {
        using TempDirectory temp = new();
        using StubImageBoard board = new();
        await WriteFixtureAsync(temp.Path, MalformedRootManifest(version, field, shape));
        await AssertWriterRejectsWithoutMutationAsync(temp.Path, board, writer);
    }

    [Theory]
    [InlineData("asset")]
    [InlineData("asset-entry")]
    [InlineData("current-entry")]
    public async Task EmptyV6InventoriesWithNullOriginalRecordsReturnCorruptNotNullReference(string field)
    {
        using TempDirectory temp = new();
        using StubImageBoard board = new();
        string assetId = await SeedNullOriginalFixtureAsync(temp.Path, board, field);
        await AssertCorruptReadOnlyAsync(temp.Path, assetId);
    }

    public static IEnumerable<object[]> NullOriginalWriterCases()
    {
        foreach (string field in new[] { "asset", "asset-entry", "current-entry" })
        foreach (string writer in new[] { "commit-new", "commit-existing", "restore-untracked", "restore-existing" })
            yield return [field, writer];
    }

    [Theory]
    [MemberData(nameof(NullOriginalWriterCases))]
    public async Task EmptyV6InventoriesWithNullOriginalRecordsRejectEveryReferenceFreeWriter(
        string field, string writer)
    {
        using TempDirectory temp = new();
        using StubImageBoard board = new();
        await SeedNullOriginalFixtureAsync(temp.Path, board, field);
        await AssertWriterRejectsWithoutMutationAsync(temp.Path, board, writer);
    }

    public static IEnumerable<object[]> InventoryFreeControls()
    {
        // 0 means no manifest at all; explicit v1..v5 have neither inventory property.
        for (int version = 0; version <= 5; version++)
        foreach (string writer in Writers)
            yield return [version, writer];
    }

    [Theory]
    [MemberData(nameof(InventoryFreeControls))]
    public async Task MissingAndValidLegacyInventoryFreeManifestsStillAllowReferenceFreeWriters(
        int version, string writer)
    {
        using TempDirectory temp = new();
        using StubImageBoard board = new();
        string manifestPath = Path.Combine(temp.Path, ManifestName);
        if (version != 0) await File.WriteAllTextAsync(manifestPath, EmptyManifest(version).ToJsonString());
        MediaAssetSnapshot before = await MediaAssetReader.OpenAsync(temp.Path);
        Assert.Equal(version == 0 ? MediaManifestStatus.Missing : MediaManifestStatus.Available, before.Status);
        if (version != 0) Assert.Equal(version, before.ManifestVersion);
        Assert.Empty(before.OriginalEvidence);
        Assert.Empty(before.DerivedRepresentations);
        DownloadItem item = await PrepareWriterAsync(temp.Path, board, writer);

        MediaCommitResult result = await ExerciseWriterAsync(item, writer);

        Assert.Equal(writer == "restore-untracked", result.Reused);
        Assert.Equal(Payload, await File.ReadAllBytesAsync(result.Path));
        MediaAssetSnapshot after = await MediaAssetReader.OpenAsync(temp.Path);
        Assert.Equal(MediaManifestStatus.Available, after.Status);
        Assert.Equal(4, after.ManifestVersion);
        Assert.Equal(result.AssetId, Assert.Single(after.GetPage()).AssetId);
        Assert.Empty(after.AttachmentBindings);
        Assert.Empty(after.OriginalEvidence);
        Assert.Empty(after.DerivedRepresentations);
        JsonObject wire = JsonNode.Parse(await File.ReadAllTextAsync(manifestPath))!.AsObject();
        Assert.False(wire.ContainsKey("OriginalEvidence"));
        Assert.False(wire.ContainsKey("DerivedRepresentations"));
        Assert.Empty(Directory.GetFiles(temp.Path, "*.corrupt-*"));
        Assert.Empty(Directory.GetFiles(temp.Path, "*.part"));
        Assert.Equal(0, before.Count); // An old empty snapshot is not changed by the writer.
    }

    [Theory]
    [InlineData(1, "commit-new")]
    [InlineData(1, "restore-untracked")]
    [InlineData(2, "commit-new")]
    [InlineData(2, "restore-untracked")]
    [InlineData(0, "commit-new")]
    [InlineData(0, "restore-untracked")]
    [InlineData(-1, "commit-new")]
    [InlineData(-1, "restore-untracked")]
    public async Task InventoryFreeOldCorruptionRetainsExistingBackupAndRepairBehavior(int version, string writer)
    {
        using TempDirectory temp = new();
        using StubImageBoard board = new();
        JsonObject manifest = EmptyManifest(version);
        manifest["Entries"] = null;
        await WriteFixtureAsync(temp.Path, manifest);
        byte[] original = await File.ReadAllBytesAsync(Path.Combine(temp.Path, ManifestName));
        await AssertCorruptReadOnlyAsync(temp.Path);
        DownloadItem item = await PrepareWriterAsync(temp.Path, board, writer);

        MediaCommitResult result = await ExerciseWriterAsync(item, writer);

        string backup = Assert.Single(Directory.GetFiles(temp.Path, ManifestName + ".corrupt-*"));
        Assert.Equal(original, await File.ReadAllBytesAsync(backup));
        Assert.Equal(Payload, await File.ReadAllBytesAsync(result.Path));
        MediaAssetSnapshot after = await MediaAssetReader.OpenAsync(temp.Path);
        Assert.Equal(MediaManifestStatus.Available, after.Status);
        Assert.Equal(4, after.ManifestVersion);
        Assert.Equal(result.AssetId, Assert.Single(after.GetPage()).AssetId);
        Assert.Empty(after.OriginalEvidence);
        Assert.Empty(after.DerivedRepresentations);
        JsonObject saved = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(temp.Path, ManifestName)))!.AsObject();
        Assert.False(saved.ContainsKey("OriginalEvidence"));
        Assert.False(saved.ContainsKey("DerivedRepresentations"));
        Assert.Empty(Directory.GetFiles(temp.Path, "*.part"));
    }

    private static JsonObject EmptyManifest(int version) => new()
    {
        ["Version"] = version, ["Entries"] = new JsonObject(), ["Assets"] = new JsonObject(),
        ["AttachmentBindings"] = new JsonObject()
    };

    private static JsonObject InvalidVersionManifest(string version, string field, string shape)
    {
        JsonObject root = EmptyManifest(6);
        AddInventories(root, field, shape);
        if (version == "missing") root.Remove("Version");
        else root["Version"] = version switch
        {
            "null" => null,
            "string" => JsonValue.Create("6"),
            "object" => new JsonObject(),
            "array" => new JsonArray(),
            "boolean" => JsonValue.Create(true),
            "fractional" => JsonValue.Create(6.5),
            "overflow" => JsonValue.Create(2147483648L),
            "zero" => JsonValue.Create(0),
            "negative" => JsonValue.Create(-1),
            _ => throw new ArgumentOutOfRangeException(nameof(version))
        };
        return root;
    }

    private static JsonObject MalformedRootManifest(int version, string field, string shape)
    {
        JsonObject root = EmptyManifest(version);
        AddInventories(root, "both", "empty");
        root[field] = shape switch
        {
            "null" => null,
            "array" => new JsonArray(),
            "string" => JsonValue.Create("not an object"),
            _ => throw new ArgumentOutOfRangeException(nameof(shape))
        };
        return root;
    }

    private static void AddInventories(JsonObject root, string field, string shape)
    {
        if (shape == "populated")
        {
            MediaActualIntegrity actual = new(Payload.Length, Convert.ToBase64String(MD5.HashData(Payload)),
                Convert.ToHexString(SHA256.HashData(Payload)).ToLowerInvariant())
                { Sha1 = Convert.ToBase64String(SHA1.HashData(Payload)) };
            MediaProviderMetadata provider = new("absent", "original", "https://stub.test/media/absent.bin",
                "absent.bin", actual.Length, actual.Md5, actual.Sha256) { ExpectedSha1 = actual.Sha1 };
            string key = MediaRevisionKeys.CreateAttachmentKey(Resource, "900", 0, provider);
            CapturedAttachment metadata = new(0, provider.MediaId, provider.Variant, provider.Url,
                provider.OriginalFileName, ExpectedSize: provider.ExpectedSize, ExpectedMd5: provider.ExpectedMd5)
                { AttachmentRevisionKey = key, ExpectedSha256 = provider.ExpectedSha256, ExpectedSha1 = provider.ExpectedSha1 };
            MediaOccurrence occurrence = new(Resource, "900", 0, key);
            MediaPassiveObservation origin = new("b8cce56f-562e-4c44-8a65-a68879108f11", new string('c', 64),
                new DateTimeOffset(2026, 11, 7, 12, 0, 0, TimeSpan.Zero));
            root["OriginalEvidence"] = new JsonArray(JsonSerializer.SerializeToNode(new MediaOriginalEvidence(
                occurrence, metadata, MediaObservedContentStatus.Omitted, actual, origin)));
            root["DerivedRepresentations"] = new JsonArray(JsonSerializer.SerializeToNode(new MediaDerivedRepresentation(
                occurrence, actual, MediaDerivedKind.Preview, MediaDerivedStatus.Unavailable, null, origin)));
            return;
        }
        foreach (string name in field == "both" ? new[] { "OriginalEvidence", "DerivedRepresentations" } : new[] { field })
            root[name] = shape == "null" ? null : new JsonArray();
    }

    private static async Task<string> SeedNullOriginalFixtureAsync(string directory, StubImageBoard board, string field)
    {
        MediaCommitResult seeded = await CommitAsync(Item(board, directory, "seed"));
        string manifestPath = Path.Combine(directory, ManifestName);
        JsonObject root = JsonNode.Parse(await File.ReadAllTextAsync(manifestPath))!.AsObject();
        root["Version"] = 6;
        AddInventories(root, "both", "empty");
        await WriteFixtureAsync(directory, root);
        MediaAssetSnapshot baseline = await MediaAssetReader.OpenAsync(directory);
        Assert.Equal(MediaManifestStatus.Available, baseline.Status);
        Assert.Equal(6, baseline.ManifestVersion);
        Assert.Equal(seeded.AssetId, Assert.Single(baseline.GetPage()).AssetId);
        Assert.Empty(baseline.OriginalEvidence);
        Assert.Empty(baseline.DerivedRepresentations);
        switch (field)
        {
            case "asset": root["Assets"]![seeded.AssetId!] = null; break;
            case "asset-entry": root["Assets"]![seeded.AssetId!]!["Entry"] = null; break;
            case "current-entry": root["Entries"]!["seed.bin"] = null; break;
            default: throw new ArgumentOutOfRangeException(nameof(field));
        }
        await WriteFixtureAsync(directory, root);
        return seeded.AssetId!;
    }

    private static async Task WriteFixtureAsync(string directory, JsonObject root)
    {
        // Noncanonical whitespace makes byte-for-byte checks stronger than parsed JSON equality.
        byte[] bytes = Encoding.UTF8.GetBytes(" \n" + root.ToJsonString() + "\n ");
        await File.WriteAllBytesAsync(Path.Combine(directory, ManifestName), bytes);
        await File.WriteAllBytesAsync(Path.Combine(directory, "unowned.bin"), [23, 29, 31]);
    }

    private static async Task AssertCorruptReadOnlyAsync(string directory, string? knownAssetId = null)
    {
        Dictionary<string, byte[]> before = await CaptureFilesAsync(directory);
        MediaAssetSnapshot? snapshot = null;
        Exception? error = await Record.ExceptionAsync(async () => { snapshot = await MediaAssetReader.OpenAsync(directory); });
        await AssertFilesUnchangedAsync(directory, before, allowWriterLock: false);
        Assert.Null(error); // In particular, NullReferenceException is not an acceptable corruption result.
        Assert.NotNull(snapshot);
        Assert.Equal(MediaManifestStatus.Corrupt, snapshot.Status);
        Assert.Null(snapshot.ManifestVersion);
        Assert.Null(snapshot.Generation);
        Assert.Equal(0, snapshot.Count);
        Assert.Empty(snapshot.GetPage());
        Assert.Empty(snapshot.AttachmentBindings);
        Assert.False(snapshot.OriginalEvidence.IsDefault);
        Assert.False(snapshot.DerivedRepresentations.IsDefault);
        Assert.Empty(snapshot.OriginalEvidence);
        Assert.Empty(snapshot.DerivedRepresentations);
        if (knownAssetId != null) Assert.Null(snapshot.FindById(knownAssetId));
    }

    private static async Task AssertWriterRejectsWithoutMutationAsync(string directory, StubImageBoard board, string writer)
    {
        DownloadItem item = await PrepareWriterAsync(directory, board, writer);
        Dictionary<string, byte[]> before = await CaptureFilesAsync(directory);
        Exception? error = await Record.ExceptionAsync(async () => { await ExerciseWriterAsync(item, writer); });
        // Check preservation even if the writer unexpectedly succeeded or threw the wrong exception.
        await AssertFilesUnchangedAsync(directory, before, allowWriterLock: true);
        Assert.IsType<InvalidDataException>(error);
    }

    private static async Task<DownloadItem> PrepareWriterAsync(string directory, StubImageBoard board, string writer)
    {
        DownloadItem item = Item(board, directory, writer.EndsWith("existing", StringComparison.Ordinal) ? "seed" : "fresh");
        Assert.Empty(item.AttachmentReferences); // Do not let the attachment-binding guard mask the repair bypass.
        if (writer.StartsWith("restore", StringComparison.Ordinal))
            await File.WriteAllBytesAsync(item.DestinationPath, Payload); // Provider size+MD5 make restore actually eligible.
        return item;
    }

    private static async Task<MediaCommitResult> ExerciseWriterAsync(DownloadItem item, string writer)
    {
        if (writer.StartsWith("commit", StringComparison.Ordinal)) return await CommitAsync(item);
        MediaCommitResult? result = await new MediaStore().TryRestoreAsync(item, default);
        Assert.NotNull(result);
        Assert.True(result.Reused);
        return result;
    }

    private static DownloadItem Item(StubImageBoard board, string directory, string id) => new(
        new Uri("https://stub.test/media/" + id + ".bin"), Path.Combine(directory, id + ".bin"), board,
        board.GetThread(new Uri("https://stub.test/g/thread/123")), id, Payload.Length,
        Convert.ToBase64String(MD5.HashData(Payload)), id + ".bin") { AttachmentReferences = [] };

    private static async Task<MediaCommitResult> CommitAsync(DownloadItem item)
    {
        using MemoryStream source = new(Payload, writable: false);
        return await new MediaStore().CommitAsync(item, source, default);
    }

    private static async Task<Dictionary<string, byte[]>> CaptureFilesAsync(string directory)
    {
        Dictionary<string, byte[]> files = new(StringComparer.Ordinal);
        foreach (string path in Directory.GetFiles(directory)) files.Add(Path.GetFileName(path), await File.ReadAllBytesAsync(path));
        return files;
    }

    private static async Task AssertFilesUnchangedAsync(string directory, Dictionary<string, byte[]> before, bool allowWriterLock)
    {
        string[] expected = before.Keys.Where(name => !allowWriterLock || name != LockName).Order(StringComparer.Ordinal).ToArray();
        string[] actual = Directory.GetFileSystemEntries(directory).Select(path => Path.GetFileName(path)!)
            .Where(name => !allowWriterLock || name != LockName).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(expected, actual); // No published payload, repair copy, manifest replacement or leftover part.
        foreach (var file in before)
            Assert.Equal(file.Value, await File.ReadAllBytesAsync(Path.Combine(directory, file.Key)));
        if (allowWriterLock && File.Exists(Path.Combine(directory, LockName)))
            Assert.Empty(await File.ReadAllBytesAsync(Path.Combine(directory, LockName)));
    }
}
