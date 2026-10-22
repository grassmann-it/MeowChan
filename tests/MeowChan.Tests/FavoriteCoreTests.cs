using System.Security.Cryptography;
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
