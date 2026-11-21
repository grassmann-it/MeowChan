using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MeowChan.Downloader;
using MeowChan.Favorites;
using MeowChan.Persistence;
using MeowChan.Storage;

namespace MeowChan.Tests;

public sealed class FavoriteCoreTests
{
    private static readonly ResourceId Thread = new("stub", "g", "123");

    [Fact]
    public async Task FavoriteSurvivesRestartAndMissingOriginal()
    {
        using TempDirectory directory = new();
        using StubImageBoard board = new();
        FavoriteExportAsset original = await Commit(directory.Path, board, [1, 2, 3], "456");
        string profile = Path.Combine(directory.Path, "profile");
        using (ProfileLease lease = ProfileLease.Acquire(profile))
        {
            FavoriteStore store = new(profile, lease);
            Assert.True(store.SetFavorite(new FavoriteEntry(original.Key, original.Length, original.OriginalFileName), true));
            Assert.False(store.SetFavorite(new FavoriteEntry(original.Key, original.Length, original.OriginalFileName), true));
        }
        File.Delete(original.Manifest.FindById(original.AssetId)!.Path);
        using (ProfileLease lease = ProfileLease.Acquire(profile))
        {
            Assert.True(new FavoriteStore(profile, lease).Contains(original.Key));
        }
        FavoriteExportResult exported = await FavoriteOriginalExporter.ExportAsync([original], Path.Combine(directory.Path, "out"));
        Assert.Equal(1, exported.Missing);
    }

    [Fact]
    public async Task FrozenExportStillCopiesOriginalAfterFavoriteIsRemovedBeforeStart()
    {
        using TempDirectory directory = new();
        using StubImageBoard board = new();
        FavoriteExportAsset original = await Commit(directory.Path, board, [4, 8, 15, 16], "456");
        using ProfileLease lease = ProfileLease.Acquire(Path.Combine(directory.Path, "profile"));
        FavoriteStore store = new(lease.ProfileDirectory, lease);
        FavoriteEntry favorite = new(original.Key, original.Length, original.OriginalFileName);
        store.SetFavorite(favorite, true);
        FavoriteExportAsset[] frozen = [original];
        store.SetFavorite(favorite, false);
        Assert.Empty(store.List());
        FavoriteExportResult result = await FavoriteOriginalExporter.ExportAsync(frozen, Path.Combine(directory.Path, "export"));
        Assert.Equal(1, result.Copied);
        Assert.Equal(new byte[] { 4, 8, 15, 16 }, await File.ReadAllBytesAsync(result.Items[0].Path!));
        Assert.True(File.Exists(original.Manifest.FindById(original.AssetId)!.Path));
    }

    [Fact]
    public async Task ExportIsRepeatableAndPreservesConflictingExistingFile()
    {
        using TempDirectory directory = new();
        using StubImageBoard board = new();
        FavoriteExportAsset first = await Commit(directory.Path, board, [1, 2, 3], "456");
        FavoriteExportAsset second = await Commit(directory.Path, board, [4, 5, 6], "789");
        string target = Path.Combine(directory.Path, "out");
        FavoriteExportResult exported = await FavoriteOriginalExporter.ExportAsync([first, second], target);
        Assert.Equal(2, exported.Copied);
        Assert.NotEqual(exported.Items[0].Path, exported.Items[1].Path);
        Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(exported.Items[0].Path!));
        Assert.Equal(new byte[] { 4, 5, 6 }, await File.ReadAllBytesAsync(exported.Items[1].Path!));
        FavoriteExportResult again = await FavoriteOriginalExporter.ExportAsync([second, first], target);
        Assert.Equal(2, again.AlreadyExists);
        Assert.Equal(2, Directory.GetFiles(target, "*", SearchOption.AllDirectories).Length);
    }

    [Fact]
    public async Task SameAssetInDifferentThreadsGetsDifferentStableFolders()
    {
        using TempDirectory directory = new();
        using StubImageBoard board = new();
        FavoriteExportAsset first = await Commit(directory.Path, board, [1, 2, 3], "456");
        FavoriteExportAsset otherThread = first with { Key = first.Key with { Thread = new ResourceId("stub", "g", "124") } };
        string target = Path.Combine(directory.Path, "out");
        FavoriteExportResult result = await FavoriteOriginalExporter.ExportAsync([first, otherThread], target);
        Assert.Equal(2, result.Copied);
        Assert.NotEqual(Path.GetDirectoryName(result.Items[0].Path), Path.GetDirectoryName(result.Items[1].Path));
    }

    [Fact]
    public async Task CancellationReportsEachUnprocessedAsset()
    {
        using TempDirectory directory = new();
        using StubImageBoard board = new();
        FavoriteExportAsset first = await Commit(directory.Path, board, [1, 2, 3], "456");
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        FavoriteExportResult result = await FavoriteOriginalExporter.ExportAsync([first], Path.Combine(directory.Path, "out"), cancellation.Token);
        Assert.Equal(1, result.Cancelled);
        Assert.False(Directory.Exists(Path.Combine(directory.Path, "out")));
    }

    [Fact]
    public async Task ExistingDifferentBytesAreNeverOverwritten()
    {
        using TempDirectory directory = new();
        using StubImageBoard board = new();
        FavoriteExportAsset asset = await Commit(directory.Path, board, [1, 2, 3], "456");
        string target = Path.Combine(directory.Path, "out");
        string primary = (await FavoriteOriginalExporter.ExportAsync([asset], target)).Items[0].Path!;
        await File.WriteAllBytesAsync(primary, [9, 9, 9]);

        FavoriteExportResult recovered = await FavoriteOriginalExporter.ExportAsync([asset], target);

        Assert.Equal(1, recovered.Copied);
        Assert.NotEqual(primary, recovered.Items[0].Path);
        Assert.Equal(new byte[] { 9, 9, 9 }, await File.ReadAllBytesAsync(primary));
        Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(recovered.Items[0].Path!));
        Assert.Equal(1, (await FavoriteOriginalExporter.ExportAsync([asset], target)).AlreadyExists);
    }

    [Fact]
    public async Task DestinationNamesDoNotDependOnSelectionOrder()
    {
        using TempDirectory directory = new();
        using StubImageBoard board = new();
        FavoriteExportAsset first = await Commit(directory.Path, board, [1, 2, 3], "456");
        FavoriteExportAsset second = await Commit(directory.Path, board, [4, 5, 6], "789");
        FavoriteExportResult a = await FavoriteOriginalExporter.ExportAsync([first, second], Path.Combine(directory.Path, "a"));
        FavoriteExportResult b = await FavoriteOriginalExporter.ExportAsync([second, first], Path.Combine(directory.Path, "b"));
        Assert.Equal(Path.GetRelativePath(Path.Combine(directory.Path, "a"), a.Items[0].Path!),
            Path.GetRelativePath(Path.Combine(directory.Path, "b"), b.Items[1].Path!));
        Assert.Equal(Path.GetRelativePath(Path.Combine(directory.Path, "a"), a.Items[1].Path!),
            Path.GetRelativePath(Path.Combine(directory.Path, "b"), b.Items[0].Path!));
    }

    [Fact]
    public async Task RatingsPreserveFavoritesAndBytesAcrossPathChangesAndMissingOriginal()
    {
        using TempDirectory directory = new(); using StubImageBoard board = new();
        FavoriteExportAsset original = await Commit(directory.Path, board, [2, 4, 8], "456");
        MediaAssetReference asset = original.Manifest.FindById(original.AssetId)!;
        string profile = Path.Combine(directory.Path, "profile");
        using ProfileLease lease = ProfileLease.Acquire(profile);
        FavoriteStore favorites = new(profile, lease);
        favorites.SetFavorite(new(original.Key, original.Length, original.OriginalFileName), true);
        byte[] savedFavorites = await File.ReadAllBytesAsync(Path.Combine(profile, "media-favorites.json"));
        MeowChan.Ratings.MediaRatingStore ratings = new(profile, lease);
        ratings.Set(original.Key, 4);
        MediaAssetReference moved = asset with { Path = Path.Combine(directory.Path, "renamed.png"), RequestedFileName = "renamed.png" };
        Assert.Equal(original.Key, FavoriteAssetKey.From(Thread, moved));
        Assert.Equal(new byte[] { 2, 4, 8 }, await File.ReadAllBytesAsync(asset.Path));
        Assert.Equal(savedFavorites, await File.ReadAllBytesAsync(Path.Combine(profile, "media-favorites.json")));
        File.Delete(asset.Path);
        Assert.Equal(4, Assert.Single(ratings.List()).Value);
        Assert.Equal(1, (await FavoriteOriginalExporter.ExportAsync([original], Path.Combine(directory.Path, "out"))).Missing);
        ratings.Set(original.Key, null);
        Assert.True(favorites.Contains(original.Key));
    }

    [Fact]
    public void TransferSnapshotIsImmutableLeaseBoundAndRechecksAcrossStoreInstances()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        FavoriteStore store = new(temp.Path, lease);

        FavoriteStoreSnapshot missing = store.ReadTransferSnapshot();
        Assert.Empty(missing.Entries);
        Assert.Equal("missing", missing.Fingerprint);
        Assert.Null(missing.InstanceId);
        Assert.Equal(0, missing.Revision);
        Assert.True(store.IsTransferSnapshotCurrent(missing));
        Assert.Throws<NotSupportedException>(() => ((IList<FavoriteEntry>)missing.Entries).Add(Entry()));

        FavoriteEntry entry = Entry();
        Assert.True(store.SetFavorite(entry, true));
        Assert.Empty(missing.Entries);
        Assert.False(store.IsTransferSnapshotCurrent(missing));

        FavoriteStoreSnapshot saved = store.ReadTransferSnapshot();
        Assert.NotNull(saved.InstanceId);
        Assert.Equal(1, saved.Revision);
        Assert.True(store.IsTransferSnapshotCurrent(saved));
        Assert.True(new FavoriteStore(temp.Path, lease).IsTransferSnapshotCurrent(saved));
        Assert.False(store.SetFavorite(entry, true));
        Assert.True(store.IsTransferSnapshotCurrent(saved));
        using CancellationTokenSource cancelled = new();
        cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => store.SetFavorite(Entry("cancel"), true, cancelled.Token));
        Assert.True(store.IsTransferSnapshotCurrent(saved));
    }

    [Fact]
    public void TransferSnapshotRejectsForeignOwnerAndTracksRestartAndPersistentAbaFence()
    {
        using TempDirectory temp = new();
        using TempDirectory other = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        using ProfileLease otherLease = ProfileLease.Acquire(other.Path);
        FavoriteStore store = new(temp.Path, lease);
        FavoriteStore foreignStore = new(other.Path, otherLease);
        FavoriteEntry entry = Entry();

        FavoriteStoreSnapshot before = store.ReadTransferSnapshot();
        Assert.False(foreignStore.IsTransferSnapshotCurrent(before));
        Assert.True(store.SetFavorite(entry, true));
        FavoriteStoreSnapshot added = store.ReadTransferSnapshot();
        Assert.True(store.SetFavorite(entry, false));
        Assert.False(store.IsTransferSnapshotCurrent(added));
        Assert.True(store.SetFavorite(entry, true));
        Assert.False(store.IsTransferSnapshotCurrent(added));
        FavoriteStoreSnapshot current = store.ReadTransferSnapshot();
        Assert.Equal(3, current.Revision);
        Guid instance = Assert.IsType<Guid>(current.InstanceId);

        lease.Dispose();
        using ProfileLease restartedLease = ProfileLease.Acquire(temp.Path);
        FavoriteStore restarted = new(temp.Path, restartedLease);
        // Persisted fences survive restart, but old owner authority never does.
        Assert.False(restarted.IsTransferSnapshotCurrent(current));
        FavoriteStoreSnapshot restartedSnapshot = restarted.ReadTransferSnapshot();
        Assert.True(restarted.IsTransferSnapshotCurrent(restartedSnapshot));
        Assert.Equal(current.Fingerprint, restartedSnapshot.Fingerprint);
        Assert.Equal(current.Revision, restartedSnapshot.Revision);
        Assert.Equal(instance, Assert.IsType<Guid>(restartedSnapshot.InstanceId));
        Assert.Equal(1, restarted.RemoveThreads([entry.Key.Thread]));
        FavoriteStoreSnapshot removed = restarted.ReadTransferSnapshot();
        Assert.Empty(removed.Entries);
        Assert.Equal(4, removed.Revision);
        Assert.False(restarted.IsTransferSnapshotCurrent(restartedSnapshot));
        Assert.Equal(instance, Assert.IsType<Guid>(removed.InstanceId));
        byte[] committed = File.ReadAllBytes(Path.Combine(temp.Path, "media-favorites.json"));
        Assert.Equal(0, restarted.RemoveThreads([entry.Key.Thread]));
        Assert.Throws<OperationCanceledException>(() => restarted.RemoveThreads([entry.Key.Thread], new CancellationToken(true)));
        Assert.Equal(committed, File.ReadAllBytes(Path.Combine(temp.Path, "media-favorites.json")));
        Assert.True(restarted.IsTransferSnapshotCurrent(removed));
        Assert.Throws<ObjectDisposedException>(() => store.ReadTransferSnapshot());
        Assert.Throws<ObjectDisposedException>(() => store.IsTransferSnapshotCurrent(current));
    }

    [Fact]
    public void V1ReadIsByteStableAndFirstMutationAddsV2Fence()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        FavoriteStore store = new(temp.Path, lease);
        string path = Path.Combine(temp.Path, "media-favorites.json");
        byte[] v1 = Encoding.UTF8.GetBytes("{\"Version\":1,\"Entries\":[]}");
        File.WriteAllBytes(path, v1);

        FavoriteStoreSnapshot snapshot = store.ReadTransferSnapshot();
        Assert.Null(snapshot.InstanceId);
        Assert.Equal(0, snapshot.Revision);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(v1)), snapshot.Fingerprint);
        Assert.False(store.SetFavorite(Entry(), false));
        Assert.Equal(0, store.RemoveThreads([Thread]));
        Assert.Throws<OperationCanceledException>(() => store.SetFavorite(Entry(), true, new CancellationToken(true)));
        Assert.Equal(v1, File.ReadAllBytes(path));
        Assert.False(File.Exists(path + ".bak"));
        Assert.True(store.IsTransferSnapshotCurrent(snapshot));
        Assert.True(store.SetFavorite(Entry(), true));
        Assert.Equal(v1, File.ReadAllBytes(path + ".bak"));
        Assert.False(store.IsTransferSnapshotCurrent(snapshot));
        using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(path));
        Assert.Equal(2, document.RootElement.GetProperty("Version").GetInt32());
        Assert.NotEqual(Guid.Empty, document.RootElement.GetProperty("InstanceId").GetGuid());
        Assert.Equal(1, document.RootElement.GetProperty("Revision").GetInt64());
    }

    [Fact]
    public void MissingPrimaryWithBackupIsRecoveryRequiredAndMissingIsDistinct()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        FavoriteStore store = new(temp.Path, lease);
        string path = Path.Combine(temp.Path, "media-favorites.json");
        Assert.Equal("missing", store.ReadTransferSnapshot().Fingerprint);
        File.WriteAllText(path + ".bak", "{\"Version\":1,\"Entries\":[]}");
        Assert.Throws<InvalidDataException>(() => store.ReadTransferSnapshot());
        Assert.Throws<InvalidDataException>(() => store.SetFavorite(Entry(), true));
        Assert.False(File.Exists(path));
        Assert.Equal("{\"Version\":1,\"Entries\":[]}", File.ReadAllText(path + ".bak"));
    }

    [Fact]
    public void MalformedFutureInvalidUtf8AndBoundsFailClosedWithoutBackupRotation()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        FavoriteStore store = new(temp.Path, lease);
        string path = Path.Combine(temp.Path, "media-favorites.json");
        string[] invalid = ["{broken", "{\"Version\":99,\"Entries\":[]}", "{\"Version\":1,\"Entries\":[null]}",
            "{\"Version\":2,\"InstanceId\":\"b64b2d98-4ca8-4e9f-9c95-3e3f4b1d7a6c\",\"Revision\":9223372036854775808,\"Entries\":[]}"];
        foreach (string json in invalid)
        {
            File.WriteAllText(path, json);
            InvalidDataException error = Assert.Throws<InvalidDataException>(() => store.ReadTransferSnapshot());
            Assert.Null(error.InnerException);
            Assert.DoesNotContain("secret", error.ToString(), StringComparison.Ordinal);
            Assert.Throws<InvalidDataException>(() => store.SetFavorite(Entry("secret"), true));
            Assert.Equal(json, File.ReadAllText(path));
        }

        byte[] invalidUtf8 = Encoding.UTF8.GetBytes("{\"Version\":1,\"Entries\":[]}");
        invalidUtf8[^1] = 0xFF;
        File.WriteAllBytes(path, invalidUtf8);
        Assert.Throws<InvalidDataException>(() => store.ReadTransferSnapshot());
        Assert.Throws<InvalidDataException>(() => store.SetFavorite(Entry(), true));
        Assert.Equal(invalidUtf8, File.ReadAllBytes(path));

        File.WriteAllBytes(path, new byte[FavoriteStore.MaximumBytes + 1]);
        Assert.Throws<InvalidDataException>(() => store.ReadTransferSnapshot());
        Assert.Throws<InvalidDataException>(() => store.SetFavorite(Entry(), true));
        File.Delete(path);
        Directory.CreateDirectory(path);
        Assert.Throws<InvalidDataException>(() => store.ReadTransferSnapshot());
        Directory.Delete(path);
        File.WriteAllText(path, "{\"Version\":1,\"Entries\":[" + string.Join(',', Enumerable.Repeat("null", 50_001)) + "]}");
        Assert.Throws<InvalidDataException>(() => store.ReadTransferSnapshot());

        File.WriteAllText(path, "{\"Version\":1,\"Entries\":[]}");
        File.WriteAllText(path + ".bak", "{\"Version\":99,\"Entries\":[]}");
        Assert.Throws<InvalidDataException>(() => store.SetFavorite(Entry(), true));
        Assert.Equal("{\"Version\":99,\"Entries\":[]}", File.ReadAllText(path + ".bak"));
    }

    [Fact]
    public void TransferSnapshotDefensivelyCopiesInputAndNeverPrintsPrivateFields()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        List<FavoriteEntry> input = [Entry("private-media") with { OriginalFileName = "private-name" }];
        FavoriteEntry saved = input[0];
        FavoriteStoreSnapshot snapshot = new(lease, input, "private-fingerprint", null, 0);
        input.Clear();
        Assert.Equal(saved, Assert.Single(snapshot.Entries));
        Assert.Throws<NotSupportedException>(() => ((IList<FavoriteEntry>)snapshot.Entries).Clear());
        Assert.DoesNotContain("private-", snapshot.ToString()!, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingEmptyAndExactPrimaryBytesHaveDistinctFencesWithoutReadWrites()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        FavoriteStore store = new(temp.Path, lease);
        string path = Path.Combine(temp.Path, "media-favorites.json");
        FavoriteStoreSnapshot missing = store.ReadTransferSnapshot();
        Assert.False(store.SetFavorite(Entry(), false));
        Assert.False(File.Exists(path));
        byte[] bytes = Encoding.UTF8.GetBytes("{\"Version\":1,\"Entries\":[]} ");
        File.WriteAllBytes(path, bytes);
        FavoriteStoreSnapshot empty = store.ReadTransferSnapshot();
        Assert.NotEqual(missing.Fingerprint, empty.Fingerprint);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)), empty.Fingerprint);
        Assert.False(store.IsTransferSnapshotCurrent(missing));
        File.WriteAllText(path, "{\"Version\":1,\"Entries\":[]}");
        Assert.False(store.IsTransferSnapshotCurrent(empty));
        Assert.Empty(store.ReadTransferSnapshot().Entries);
        Assert.False(File.Exists(path + ".bak"));
    }

    [Fact]
    public void InvalidBackupBlocksOnlyActualWritesAndPreservesBothFiles()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        FavoriteStore store = new(temp.Path, lease);
        string path = Path.Combine(temp.Path, "media-favorites.json");
        store.SetFavorite(Entry(), true);
        byte[] primary = File.ReadAllBytes(path);
        foreach (string invalid in new[] { "{broken", "{\"Version\":99,\"Entries\":[]}" })
        {
            File.WriteAllText(path + ".bak", invalid);
            FavoriteStoreSnapshot snapshot = store.ReadTransferSnapshot();
            Assert.False(store.SetFavorite(Entry(), true));
            Assert.True(store.IsTransferSnapshotCurrent(snapshot));
            Assert.Throws<InvalidDataException>(() => store.SetFavorite(Entry(), false));
            Assert.Throws<InvalidDataException>(() => store.RemoveThreads([Thread]));
            Assert.Equal(primary, File.ReadAllBytes(path));
            Assert.Equal(invalid, File.ReadAllText(path + ".bak"));
        }
    }

    [Fact]
    public void LinkAndDanglingLinkPrimaryAndBackupAreNeverFollowedOrRepaired()
    {
        if (OperatingSystem.IsWindows()) return; // Creating symlinks may require an OS privilege.
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        FavoriteStore store = new(temp.Path, lease);
        string path = Path.Combine(temp.Path, "media-favorites.json");
        string target = Path.Combine(temp.Path, "target.json");
        const string valid = "{\"Version\":1,\"Entries\":[]}";
        File.WriteAllText(target, valid);
        foreach (bool dangling in new[] { false, true })
        {
            File.CreateSymbolicLink(path, dangling ? target + ".missing" : target);
            Assert.Throws<InvalidDataException>(() => store.ReadTransferSnapshot());
            Assert.Throws<InvalidDataException>(() => store.SetFavorite(Entry(), true));
            File.Delete(path);
            File.CreateSymbolicLink(path + ".bak", dangling ? target + ".missing" : target);
            Assert.Throws<InvalidDataException>(() => store.ReadTransferSnapshot());
            File.WriteAllText(path, valid);
            Assert.Empty(store.ReadTransferSnapshot().Entries);
            Assert.Throws<InvalidDataException>(() => store.SetFavorite(Entry(), true));
            File.Delete(path);
            File.Delete(path + ".bak");
        }
        Assert.Equal(valid, File.ReadAllText(target));
    }

    [Fact]
    public void BackupDirectoryMutationFailurePreservesIoContractAndOriginalEvidence()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        FavoriteStore store = new(temp.Path, lease);
        string path = Path.Combine(temp.Path, "media-favorites.json");
        store.SetFavorite(Entry(), true);
        byte[] before = File.ReadAllBytes(path);
        Directory.CreateDirectory(path + ".bak");
        string evidence = Path.Combine(path + ".bak", "foreign-evidence.txt");
        File.WriteAllText(evidence, "preserve");
        Assert.Throws<IOException>(() => store.SetFavorite(Entry(), false));
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Equal("preserve", File.ReadAllText(evidence));
        Assert.True(store.Contains(Entry().Key));
    }

    private static FavoriteEntry Entry(string media = "media") =>
        new(new(Thread, media, "original", new string('a', 64)), 12, "image.png");

    [Fact]
    public async Task FilenameTemplateNamesTheCopyAndAnUnusableTemplateKeepsTheCurrentName()
    {
        using TempDirectory directory = new();
        using StubImageBoard board = new();
        FavoriteExportAsset original = await Commit(directory.Path, board, [9, 8, 7], "456");
        string sha = original.Key.Sha256.ToLowerInvariant();

        string plain = Path.Combine(directory.Path, "plain");
        FavoriteExportResult unchanged = await FavoriteOriginalExporter.ExportAsync([original], plain);
        string currentName = Path.GetFileName(Assert.Single(unchanged.Items).Path!);
        Assert.Equal("same-name-" + sha + ".bin", currentName);

        string templated = Path.Combine(directory.Path, "templated");
        FavoriteExportResult named = await FavoriteOriginalExporter.ExportAsync([original], templated, "{source}_{post}.{ext}");
        string path = Assert.Single(named.Items).Path!;
        string name = Path.GetFileName(path);
        Assert.StartsWith("stub_123~", name);
        Assert.EndsWith("-" + sha + ".bin", name);
        Assert.Equal(new byte[] { 9, 8, 7 }, await File.ReadAllBytesAsync(path));
        // Repeating the export recognises the finished copy under the templated name.
        Assert.Equal(1, (await FavoriteOriginalExporter.ExportAsync([original], templated, "{source}_{post}.{ext}")).AlreadyExists);

        string fallback = Path.Combine(directory.Path, "fallback");
        FavoriteExportResult kept = await FavoriteOriginalExporter.ExportAsync([original], fallback, "{bogus}.{ext}");
        Assert.Equal(currentName, Path.GetFileName(Assert.Single(kept.Items).Path!));
    }

    private static async Task<FavoriteExportAsset> Commit(string root, StubImageBoard board, byte[] bytes, string mediaId)
    {
        DownloadItem item = new(new Uri($"https://stub.test/media/{mediaId}.bin"),
            Path.Combine(root, "media", "stub", "g", "123", mediaId + ".bin"), board,
            board.GetThread(new Uri("https://stub.test/g/thread/123")), mediaId,
            bytes.Length, Convert.ToBase64String(MD5.HashData(bytes)), "same-name.bin");
        using MemoryStream stream = new(bytes, writable: false);
        MediaCommitResult committed = await new MediaStore().CommitAsync(item, stream, CancellationToken.None);
        MediaAssetSnapshot snapshot = await MediaAssetReader.OpenAsync(Path.GetDirectoryName(committed.Path)!);
        MediaAssetReference asset = snapshot.FindById(committed.AssetId!)!;
        return FavoriteExportAsset.From(Thread, snapshot, asset);
    }
}
