using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MeowChan.Archive;
using MeowChan.Downloader;
using MeowChan.Storage;

namespace MeowChan.Tests.Transfer;

// Synthetic wire fixtures over the existing store; not journalled import/materialization evidence.
public sealed class NativeV6MalformedJsonBoundaryTests
{
    private const string ManifestName = ".meowchan-manifest.json";
    private const string LockName = ".meowchan-store.lock";
    private static readonly byte[] Payload = [1, 3, 5, 7, 11];
    private static readonly ResourceId Resource = new("stub", "g", "123");

    public static IEnumerable<object[]> MalformedJsonCases()
    {
        yield return ["trailing-garbage"];
        yield return ["truncated-after-inventories"];
    }

    public static IEnumerable<object[]> MalformedJsonWriterCases()
    {
        foreach (object[] malformed in MalformedJsonCases())
        foreach (string writer in new[] { "commit", "restore" })
            yield return [malformed[0], writer];
    }

    [Theory]
    [MemberData(nameof(MalformedJsonCases))]
    public async Task V6InventoryMarkersFollowedByMalformedJsonReadCorruptEmptyAndReadOnly(string shape)
    {
        using TempDirectory directory = new();
        await WriteFixtureAsync(directory.Path, MalformedV6Manifest(shape));

        await AssertCorruptReadOnlyAsync(directory.Path);
    }

    [Theory]
    [MemberData(nameof(MalformedJsonWriterCases))]
    public async Task V6InventoryMarkersFollowedByMalformedJsonRejectReferenceFreeWriterWithoutMutation(
        string shape, string writer)
    {
        using TempDirectory directory = new();
        using StubImageBoard board = new();
        await WriteFixtureAsync(directory.Path, MalformedV6Manifest(shape));

        await AssertWriterRejectsWithoutMutationAsync(directory.Path, board, writer);
    }

    [Theory]
    [InlineData("commit")]
    [InlineData("restore")]
    public async Task InventoryFreeLegacyCorruptionRetainsRepairAndBackupBehavior(string writer)
    {
        using TempDirectory directory = new();
        using StubImageBoard board = new();
        JsonObject manifest = EmptyManifest(1);
        manifest["Entries"] = null;
        byte[] originalManifest = Encoding.UTF8.GetBytes(" \n" + manifest.ToJsonString() + "\n ");
        await WriteFixtureAsync(directory.Path, originalManifest);
        await AssertCorruptReadOnlyAsync(directory.Path);

        DownloadItem item = await PrepareWriterAsync(directory.Path, board, writer);
        MediaCommitResult result = await ExerciseWriterAsync(item, writer);

        string backup = Assert.Single(Directory.GetFiles(directory.Path, ManifestName + ".corrupt-*"));
        Assert.Equal(originalManifest, await File.ReadAllBytesAsync(backup));
        Assert.Equal(Payload, await File.ReadAllBytesAsync(result.Path));
        Assert.Equal(writer == "restore", result.Reused);

        MediaAssetSnapshot after = await MediaAssetReader.OpenAsync(directory.Path);
        Assert.Equal(MediaManifestStatus.Available, after.Status);
        Assert.Equal(4, after.ManifestVersion);
        Assert.Equal(result.AssetId, Assert.Single(after.GetPage()).AssetId);
        Assert.Empty(after.OriginalEvidence);
        Assert.Empty(after.DerivedRepresentations);
        JsonObject saved = JsonNode.Parse(await File.ReadAllTextAsync(
            Path.Combine(directory.Path, ManifestName)))!.AsObject();
        Assert.False(saved.ContainsKey("OriginalEvidence"));
        Assert.False(saved.ContainsKey("DerivedRepresentations"));
        Assert.Empty(Directory.GetFiles(directory.Path, "*.part"));
    }

    private static JsonObject EmptyManifest(int version) => new()
    {
        ["Version"] = version,
        ["Entries"] = new JsonObject(),
        ["Assets"] = new JsonObject(),
        ["AttachmentBindings"] = new JsonObject()
    };

    private static JsonObject ValidV6Manifest()
    {
        JsonObject root = EmptyManifest(6);
        MediaActualIntegrity actual = new(Payload.Length, Convert.ToBase64String(MD5.HashData(Payload)),
            Convert.ToHexString(SHA256.HashData(Payload)).ToLowerInvariant())
        { Sha1 = Convert.ToBase64String(SHA1.HashData(Payload)) };
        MediaProviderMetadata provider = new("absent", "original", "https://stub.test/media/absent.bin",
            "absent.bin", actual.Length, actual.Md5, actual.Sha256)
        { ExpectedSha1 = actual.Sha1 };
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
        return root;
    }

    private static byte[] MalformedV6Manifest(string shape)
    {
        string validJson = ValidV6Manifest().ToJsonString();
        string malformed = shape switch
        {
            "trailing-garbage" => validJson + "x",
            "truncated-after-inventories" => validJson[..^1],
            _ => throw new ArgumentOutOfRangeException(nameof(shape))
        };
        return Encoding.UTF8.GetBytes(" \n" + malformed + "\n ");
    }

    private static async Task WriteFixtureAsync(string directory, byte[] manifest)
    {
        await File.WriteAllBytesAsync(Path.Combine(directory, ManifestName), manifest);
        await File.WriteAllBytesAsync(Path.Combine(directory, "unowned.bin"), [23, 29, 31]);
    }

    private static async Task AssertCorruptReadOnlyAsync(string directory)
    {
        Dictionary<string, byte[]> before = await CaptureFilesAsync(directory);
        MediaAssetSnapshot? snapshot = null;
        Exception? error = await Record.ExceptionAsync(async () =>
        {
            snapshot = await MediaAssetReader.OpenAsync(directory);
        });

        await AssertFilesUnchangedAsync(directory, before, allowWriterLock: false);
        Assert.Null(error);
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
    }

    private static async Task AssertWriterRejectsWithoutMutationAsync(string directory, StubImageBoard board,
        string writer)
    {
        DownloadItem item = await PrepareWriterAsync(directory, board, writer);
        Dictionary<string, byte[]> before = await CaptureFilesAsync(directory);
        Exception? error = await Record.ExceptionAsync(async () =>
        {
            await ExerciseWriterAsync(item, writer);
        });

        await AssertFilesUnchangedAsync(directory, before, allowWriterLock: true);
        Assert.IsType<InvalidDataException>(error);
    }

    private static async Task<DownloadItem> PrepareWriterAsync(string directory, StubImageBoard board, string writer)
    {
        DownloadItem item = Item(board, directory, writer == "commit" ? "fresh" : "restore");
        Assert.Empty(item.AttachmentReferences);
        if (writer == "restore") await File.WriteAllBytesAsync(item.DestinationPath, Payload);
        return item;
    }

    private static async Task<MediaCommitResult> ExerciseWriterAsync(DownloadItem item, string writer)
    {
        if (writer == "commit")
        {
            using MemoryStream source = new(Payload, writable: false);
            return await new MediaStore().CommitAsync(item, source, default);
        }

        MediaCommitResult? result = await new MediaStore().TryRestoreAsync(item, default);
        Assert.NotNull(result);
        return result;
    }

    private static DownloadItem Item(StubImageBoard board, string directory, string id) => new(
        new Uri("https://stub.test/media/" + id + ".bin"), Path.Combine(directory, id + ".bin"), board,
        board.GetThread(new Uri("https://stub.test/g/thread/123")), id, Payload.Length,
        Convert.ToBase64String(MD5.HashData(Payload)), id + ".bin") { AttachmentReferences = [] };

    private static async Task<Dictionary<string, byte[]>> CaptureFilesAsync(string directory)
    {
        Dictionary<string, byte[]> files = new(StringComparer.Ordinal);
        foreach (string path in Directory.GetFiles(directory))
            files.Add(Path.GetFileName(path), await File.ReadAllBytesAsync(path));
        return files;
    }

    private static async Task AssertFilesUnchangedAsync(string directory, Dictionary<string, byte[]> before,
        bool allowWriterLock)
    {
        string[] expected = before.Keys
            .Where(name => !allowWriterLock || name != LockName)
            .Order(StringComparer.Ordinal).ToArray();
        string[] actual = Directory.GetFileSystemEntries(directory)
            .Select(path => Path.GetFileName(path)!)
            .Where(name => !allowWriterLock || name != LockName)
            .Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(expected, actual);
        foreach (KeyValuePair<string, byte[]> file in before)
            Assert.Equal(file.Value, await File.ReadAllBytesAsync(Path.Combine(directory, file.Key)));
        if (allowWriterLock && File.Exists(Path.Combine(directory, LockName)))
            Assert.Empty(await File.ReadAllBytesAsync(Path.Combine(directory, LockName)));
    }
}
