using System.Security.Cryptography;
using System.Text.Json.Nodes;
using MeowChan.Downloader;
using MeowChan.Storage;

namespace MeowChan.Tests;

public sealed class MediaAssetReaderTests
{
    private static readonly byte[] FirstBytes = [1, 2, 3, 5, 8];
    private static readonly byte[] SecondBytes = [8, 5, 3, 2, 1];

    [Fact]
    public async Task MissingArchiveReadDoesNotCreateAnyDirectoryOrLock()
    {
        using TempDirectory directory = new TempDirectory();
        string missing = Path.Combine(directory.Path, "not-created", "thread");

        MediaAssetSnapshot snapshot = await MediaAssetReader.OpenAsync(missing);

        Assert.Equal(MediaManifestStatus.Missing, snapshot.Status);
        Assert.Null(snapshot.Generation);
        Assert.Empty(snapshot.GetPage());
        Assert.Empty(Directory.GetFileSystemEntries(directory.Path));
    }

    [Fact]
    public async Task ChangedBytesRetainBothAssetsAcrossRestartAndEarlierViewDoesNotChange()
    {
        using TempDirectory directory = new TempDirectory();
        using StubImageBoard board = new StubImageBoard();
        DownloadItem firstItem = Item(board, directory.Path, FirstBytes);
        MediaCommitResult first = await CommitAsync(new MediaStore(), firstItem, FirstBytes);
        string threadDirectory = Path.GetDirectoryName(first.Path)!;
        MediaAssetSnapshot earlier = await MediaAssetReader.OpenAsync(threadDirectory);
        MediaAssetReference earlierAsset = Assert.Single(earlier.GetPage());

        DownloadItem secondItem = Item(board, directory.Path, SecondBytes);
        MediaCommitResult second = await CommitAsync(new MediaStore(), secondItem, SecondBytes);
        MediaAssetSnapshot latest = await MediaAssetReader.OpenAsync(threadDirectory);

        Assert.Equal(2, latest.Count);
        Assert.NotEqual(first.Path, second.Path);
        Assert.NotEqual(earlier.Generation, latest.Generation);
        MediaAssetReference current = Assert.Single(latest.GetPage(), asset => asset.IsCurrent);
        Assert.Equal(second.AssetId, current.AssetId);
        Assert.Equal(first.AssetId, earlierAsset.AssetId);
        Assert.Equal(second.Path, current.Path);
        Assert.Equal(second.Sha256, current.Sha256);
        Assert.Equal("original", current.Variant);
        Assert.Equal("456.bin", current.RequestedFileName);
        Assert.False(latest.FindById(earlierAsset.AssetId)!.IsCurrent);
        Assert.True(earlierAsset.IsCurrent);
        Assert.Equal(earlierAsset, Assert.Single(earlier.GetPage()));
        Assert.Equal(FirstBytes, await ReadBytesAsync(earlier, earlierAsset.AssetId));
        Assert.Equal(FirstBytes, await ReadBytesAsync(latest, earlierAsset.AssetId));
        Assert.Equal(SecondBytes, await ReadBytesAsync(latest, current.AssetId));
        Assert.Equal(second.Path, (await new MediaStore().TryRestoreAsync(secondItem, CancellationToken.None))!.Path);
    }

    [Fact]
    public async Task RecommittingEarlierBytesSelectsExistingAssetWithoutDuplicatingHistory()
    {
        using TempDirectory directory = new TempDirectory();
        using StubImageBoard board = new StubImageBoard();
        DownloadItem firstItem = Item(board, directory.Path, FirstBytes);
        MediaCommitResult first = await CommitAsync(new MediaStore(), firstItem, FirstBytes);
        await CommitAsync(new MediaStore(), Item(board, directory.Path, SecondBytes), SecondBytes);

        MediaCommitResult repeated = await CommitAsync(new MediaStore(), firstItem, FirstBytes);
        MediaAssetSnapshot snapshot = await MediaAssetReader.OpenAsync(Path.GetDirectoryName(first.Path)!);

        Assert.True(repeated.Reused);
        Assert.Equal(first.Path, repeated.Path);
        Assert.Equal(2, snapshot.Count);
        Assert.Equal(first.Path, Assert.Single(snapshot.GetPage(), asset => asset.IsCurrent).Path);
    }

    [Fact]
    public async Task VersionOneIsReadWithoutMutationAndMigratesOnlyItsDeclaredCurrentAsset()
    {
        using TempDirectory directory = new TempDirectory();
        using StubImageBoard board = new StubImageBoard();
        DownloadItem firstItem = Item(board, directory.Path, FirstBytes);
        MediaCommitResult first = await CommitAsync(new MediaStore(), firstItem, FirstBytes);
        string threadDirectory = Path.GetDirectoryName(first.Path)!;
        string manifestPath = Path.Combine(threadDirectory, ".meowchan-manifest.json");
        JsonObject manifest = JsonNode.Parse(await File.ReadAllTextAsync(manifestPath))!.AsObject();
        manifest["Version"] = 1;
        manifest.Remove("Assets");
        string versionOne = manifest.ToJsonString();
        string legacyManifestPath = Path.Combine(threadDirectory, ".ochan-manifest.json");
        await File.WriteAllTextAsync(legacyManifestPath, versionOne);
        File.Delete(manifestPath);
        File.Delete(Path.Combine(threadDirectory, ".meowchan-store.lock"));
        string unknown = Path.Combine(threadDirectory, "456." + new string('a', 64) + ".bin");
        await File.WriteAllBytesAsync(unknown, SecondBytes);

        MediaAssetSnapshot old = await MediaAssetReader.OpenAsync(threadDirectory);

        Assert.Equal(1, old.ManifestVersion);
        Assert.Single(old.GetPage());
        Assert.Equal(versionOne, await File.ReadAllTextAsync(legacyManifestPath));
        Assert.False(File.Exists(manifestPath));
        Assert.False(File.Exists(Path.Combine(threadDirectory, ".meowchan-store.lock")));

        await CommitAsync(new MediaStore(), Item(board, directory.Path, SecondBytes), SecondBytes);
        MediaAssetSnapshot migrated = await MediaAssetReader.OpenAsync(threadDirectory);

        Assert.Equal(3, migrated.ManifestVersion);
        Assert.True(File.Exists(manifestPath));
        Assert.Equal(versionOne, await File.ReadAllTextAsync(legacyManifestPath));
        Assert.Equal(2, migrated.Count);
        Assert.DoesNotContain(migrated.GetPage(), asset => asset.Path == unknown);
        Assert.Equal(SecondBytes, await File.ReadAllBytesAsync(unknown));
        Assert.Equal(FirstBytes, await ReadBytesAsync(migrated, Assert.Single(old.GetPage()).AssetId));
    }

    [Fact]
    public async Task ReaderDoesNotNeedTheWriterLease()
    {
        using TempDirectory directory = new TempDirectory();
        using StubImageBoard board = new StubImageBoard();
        MediaCommitResult committed = await CommitAsync(new MediaStore(), Item(board, directory.Path, FirstBytes), FirstBytes);
        string threadDirectory = Path.GetDirectoryName(committed.Path)!;
        using FileStream heldLease = new FileStream(Path.Combine(threadDirectory, ".meowchan-store.lock"),
            FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        MediaAssetSnapshot snapshot = await MediaAssetReader.OpenAsync(threadDirectory).WaitAsync(TestWait.Timeout);

        Assert.Single(snapshot.GetPage());
    }

    [Fact]
    public async Task ConcurrentPublicationExposesOnlyACompleteOldOrNewManifest()
    {
        using TempDirectory directory = new TempDirectory();
        using StubImageBoard board = new StubImageBoard();
        MediaCommitResult first = await CommitAsync(new MediaStore(), Item(board, directory.Path, FirstBytes), FirstBytes);
        string threadDirectory = Path.GetDirectoryName(first.Path)!;
        using ManualResetEventSlim release = new ManualResetEventSlim();
        TaskCompletionSource publishing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        MediaStore gated = new MediaStore((source, destination, overwrite) =>
        {
            if (Path.GetFileName(destination) == ".meowchan-manifest.json")
            {
                publishing.TrySetResult();
                if (!release.Wait(TestWait.Timeout)) throw new TimeoutException("The test did not release publication.");
            }
            File.Move(source, destination, overwrite);
        });
        Task<MediaCommitResult> writer = Task.Run(() => CommitAsync(gated, Item(board, directory.Path, SecondBytes), SecondBytes));
        MediaAssetSnapshot before;
        try
        {
            await publishing.Task.WaitAsync(TestWait.Timeout);
            before = await MediaAssetReader.OpenAsync(threadDirectory).WaitAsync(TestWait.Timeout);
            Assert.Single(before.GetPage());
            Assert.Equal(first.AssetId, Assert.Single(before.GetPage()).AssetId);
        }
        finally
        {
            release.Set();
            await writer.WaitAsync(TestWait.Timeout);
        }
        MediaAssetSnapshot after = await MediaAssetReader.OpenAsync(threadDirectory);
        Assert.Equal(2, after.Count);
        Assert.Single(before.GetPage());
        Assert.NotEqual(before.Generation, after.Generation);
    }

    [Fact]
    public async Task FailedNewManifestPublicationLeavesEarlierViewAndNoFalseAssetUntilRetry()
    {
        using TempDirectory directory = new TempDirectory();
        using StubImageBoard board = new StubImageBoard();
        DownloadItem firstItem = Item(board, directory.Path, FirstBytes);
        MediaCommitResult first = await CommitAsync(new MediaStore(), firstItem, FirstBytes);
        string threadDirectory = Path.GetDirectoryName(first.Path)!;
        MediaAssetSnapshot earlier = await MediaAssetReader.OpenAsync(threadDirectory);
        DownloadItem changed = Item(board, directory.Path, SecondBytes);
        MediaStore broken = new MediaStore((source, destination, overwrite) =>
        {
            if (Path.GetFileName(destination) == ".meowchan-manifest.json") throw new IOException("Synthetic commit failure.");
            File.Move(source, destination, overwrite);
        });

        await Assert.ThrowsAsync<IOException>(() => CommitAsync(broken, changed, SecondBytes));
        MediaAssetSnapshot failed = await MediaAssetReader.OpenAsync(threadDirectory);

        Assert.Equal(earlier.Generation, failed.Generation);
        Assert.Single(failed.GetPage());
        Assert.Equal(FirstBytes, await ReadBytesAsync(failed, Assert.Single(failed.GetPage()).AssetId));
        Assert.NotNull(await new MediaStore().TryRestoreAsync(changed, CancellationToken.None));
        MediaAssetSnapshot recovered = await MediaAssetReader.OpenAsync(threadDirectory);
        Assert.Equal(2, recovered.Count);
        Assert.Equal(SecondBytes, await ReadBytesAsync(recovered, Assert.Single(recovered.GetPage(), asset => asset.IsCurrent).AssetId));
    }

    [Theory]
    [InlineData("{broken")]
    [InlineData("{\"Version\":0,\"Entries\":{}}")]
    [InlineData("{\"Version\":2,\"Entries\":null}")]
    public async Task CorruptManifestReadPreservesTheInputAndReportsCorruption(string json)
    {
        using TempDirectory directory = new TempDirectory();
        string path = Path.Combine(directory.Path, ".meowchan-manifest.json");
        await File.WriteAllTextAsync(path, json);

        MediaAssetSnapshot snapshot = await MediaAssetReader.OpenAsync(directory.Path);

        Assert.Equal(MediaManifestStatus.Corrupt, snapshot.Status);
        Assert.NotNull(snapshot.Warning);
        Assert.Empty(snapshot.GetPage());
        Assert.Equal(json, await File.ReadAllTextAsync(path));
        Assert.Single(Directory.GetFileSystemEntries(directory.Path));
    }

    [Fact]
    public async Task UnknownManifestVersionIsNotModifiedOrTreatedAsEmpty()
    {
        using TempDirectory directory = new TempDirectory();
        string path = Path.Combine(directory.Path, ".meowchan-manifest.json");
        const string json = "{\"Version\":999,\"Entries\":{}}";
        await File.WriteAllTextAsync(path, json);

        await Assert.ThrowsAsync<InvalidDataException>(() => MediaAssetReader.OpenAsync(directory.Path));

        Assert.Equal(json, await File.ReadAllTextAsync(path));
        Assert.Single(Directory.GetFileSystemEntries(directory.Path));
    }

    [Fact]
    public async Task OversizedManifestFailsBeforeLoadingItAndIsNotModified()
    {
        using TempDirectory directory = new TempDirectory();
        string path = Path.Combine(directory.Path, ".meowchan-manifest.json");
        const long length = 32 * 1024 * 1024 + 1;
        using (FileStream large = File.Create(path)) large.SetLength(length);

        await Assert.ThrowsAsync<InvalidDataException>(() => MediaAssetReader.OpenAsync(directory.Path));

        Assert.Equal(length, new FileInfo(path).Length);
        Assert.Single(Directory.GetFileSystemEntries(directory.Path));
    }

    [Fact]
    public async Task ReaderRejectsManifestPathTraversalWithoutOpeningTheForeignFile()
    {
        using TempDirectory directory = new TempDirectory();
        using StubImageBoard board = new StubImageBoard();
        MediaCommitResult committed = await CommitAsync(new MediaStore(), Item(board, directory.Path, FirstBytes), FirstBytes);
        string threadDirectory = Path.GetDirectoryName(committed.Path)!;
        string manifestPath = Path.Combine(threadDirectory, ".meowchan-manifest.json");
        JsonObject manifest = JsonNode.Parse(await File.ReadAllTextAsync(manifestPath))!.AsObject();
        manifest["Entries"]!["456.bin"]!["FileName"] = "../foreign.bin";
        string invalid = manifest.ToJsonString();
        await File.WriteAllTextAsync(manifestPath, invalid);

        MediaAssetSnapshot snapshot = await MediaAssetReader.OpenAsync(threadDirectory);

        Assert.Equal(MediaManifestStatus.Corrupt, snapshot.Status);
        Assert.Empty(snapshot.GetPage());
        Assert.Equal(invalid, await File.ReadAllTextAsync(manifestPath));
    }

    [Theory]
    [InlineData(".ochan-manifest.json")]
    [InlineData(".meowchan-manifest.json")]
    [InlineData(".ochan-store.lock")]
    [InlineData(".meowchan-store.lock")]
    public async Task ReaderRejectsMetadataNamesInManifestInventory(string metadataName)
    {
        using TempDirectory directory = new TempDirectory();
        using StubImageBoard board = new StubImageBoard();
        MediaCommitResult committed = await CommitAsync(new MediaStore(), Item(board, directory.Path, FirstBytes), FirstBytes);
        string threadDirectory = Path.GetDirectoryName(committed.Path)!;
        string manifestPath = Path.Combine(threadDirectory, ".meowchan-manifest.json");
        JsonObject manifest = JsonNode.Parse(await File.ReadAllTextAsync(manifestPath))!.AsObject();
        manifest["Entries"]!["456.bin"]!["FileName"] = metadataName;
        await File.WriteAllTextAsync(manifestPath, manifest.ToJsonString());

        MediaAssetSnapshot snapshot = await MediaAssetReader.OpenAsync(threadDirectory);

        Assert.Equal(MediaManifestStatus.Corrupt, snapshot.Status);
        Assert.Empty(snapshot.GetPage());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task VerifiedReadRejectsChangedOrMissingFileWithoutRepair(bool remove)
    {
        using TempDirectory directory = new TempDirectory();
        using StubImageBoard board = new StubImageBoard();
        MediaCommitResult committed = await CommitAsync(new MediaStore(), Item(board, directory.Path, FirstBytes), FirstBytes);
        MediaAssetSnapshot snapshot = await MediaAssetReader.OpenAsync(Path.GetDirectoryName(committed.Path)!);
        MediaAssetReference reference = Assert.Single(snapshot.GetPage());
        if (remove) File.Delete(committed.Path);
        else await File.WriteAllBytesAsync(committed.Path, SecondBytes);

        if (remove) await Assert.ThrowsAsync<FileNotFoundException>(() => snapshot.OpenVerifiedReadAsync(reference.AssetId));
        else await Assert.ThrowsAsync<InvalidDataException>(() => snapshot.OpenVerifiedReadAsync(reference.AssetId));

        Assert.Equal(reference, Assert.Single(snapshot.GetPage()));
        if (remove) Assert.False(File.Exists(committed.Path));
        else Assert.Equal(SecondBytes, await File.ReadAllBytesAsync(committed.Path));
    }

    [Fact]
    public async Task PaginationIsBoundedReadOnlyAndIndependentOfSubsequentCommits()
    {
        using TempDirectory directory = new TempDirectory();
        using StubImageBoard board = new StubImageBoard();
        for (int index = 1; index <= 3; index++)
            await CommitAsync(new MediaStore(), Item(board, directory.Path, FirstBytes, index.ToString()), FirstBytes);
        string threadDirectory = Path.Combine(directory.Path, "stub", "g", "123");
        MediaAssetSnapshot snapshot = await MediaAssetReader.OpenAsync(threadDirectory);
        IReadOnlyList<MediaAssetReference> page = snapshot.GetPage(1, 1);

        Assert.Equal("2", Assert.Single(page).MediaIdentifier);
        Assert.Throws<NotSupportedException>(() => ((IList<MediaAssetReference>)page)[0] = page[0]);
        Assert.Throws<ArgumentOutOfRangeException>(() => snapshot.GetPage(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => snapshot.GetPage(0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => snapshot.GetPage(0, 501));
        Assert.Empty(snapshot.GetPage(int.MaxValue));
        Assert.Null(snapshot.FindById("unknown"));
        await Assert.ThrowsAsync<ArgumentException>(() => snapshot.OpenVerifiedReadAsync("unknown"));
        await CommitAsync(new MediaStore(), Item(board, directory.Path, FirstBytes, "4"), FirstBytes);
        Assert.Equal(3, snapshot.Count);
        Assert.Equal(4, (await MediaAssetReader.OpenAsync(threadDirectory)).Count);
    }

    [Fact]
    public async Task CancelledReaderDoesNotCreateFiles()
    {
        using TempDirectory directory = new TempDirectory();
        using CancellationTokenSource cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => MediaAssetReader.OpenAsync(directory.Path, cancellation.Token));

        Assert.Empty(Directory.GetFileSystemEntries(directory.Path));
    }

    [Fact]
    public async Task ReaderUsesLegacyOnlyWhenNewManifestIsTrulyMissing()
    {
        using TempDirectory directory = new TempDirectory();
        using StubImageBoard board = new StubImageBoard();
        MediaCommitResult committed = await CommitAsync(new MediaStore(), Item(board, directory.Path, FirstBytes), FirstBytes);
        string threadDirectory = Path.GetDirectoryName(committed.Path)!;
        string current = Path.Combine(threadDirectory, ".meowchan-manifest.json");
        string legacy = Path.Combine(threadDirectory, ".ochan-manifest.json");
        File.Move(current, legacy);
        File.Delete(Path.Combine(threadDirectory, ".meowchan-store.lock"));

        MediaAssetSnapshot old = await MediaAssetReader.OpenAsync(threadDirectory);
        Assert.Equal(MediaManifestStatus.Available, old.Status);
        Assert.Equal(committed.AssetId, Assert.Single(old.GetPage()).AssetId);
        Assert.False(File.Exists(current));
        Assert.False(File.Exists(Path.Combine(threadDirectory, ".meowchan-store.lock")));
        Assert.False(File.Exists(Path.Combine(threadDirectory, ".ochan-store.lock")));

        await File.WriteAllTextAsync(current, "{broken");
        MediaAssetSnapshot corrupt = await MediaAssetReader.OpenAsync(threadDirectory);
        Assert.Equal(MediaManifestStatus.Corrupt, corrupt.Status);
        Assert.Empty(corrupt.GetPage());
        Assert.False(File.Exists(Path.Combine(threadDirectory, ".ochan-store.lock")));
        Assert.True(File.Exists(legacy));
    }

    [Fact]
    public async Task NewManifestWinsWhenBothNamesExist()
    {
        using TempDirectory directory = new TempDirectory();
        using StubImageBoard board = new StubImageBoard();
        MediaCommitResult first = await CommitAsync(new MediaStore(), Item(board, directory.Path, FirstBytes), FirstBytes);
        string threadDirectory = Path.GetDirectoryName(first.Path)!;
        string current = Path.Combine(threadDirectory, ".meowchan-manifest.json");
        string legacy = Path.Combine(threadDirectory, ".ochan-manifest.json");
        File.Copy(current, legacy);
        MediaCommitResult second;
        using (FileStream heldOldLease = new FileStream(Path.Combine(threadDirectory, ".ochan-store.lock"),
                   FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            second = await CommitAsync(new MediaStore(), Item(board, directory.Path, SecondBytes), SecondBytes)
                .WaitAsync(TestWait.Timeout);
        }

        MediaAssetSnapshot snapshot = await MediaAssetReader.OpenAsync(threadDirectory);
        Assert.Equal(2, snapshot.Count);
        Assert.Contains(snapshot.GetPage(), asset => asset.AssetId == second.AssetId);
        Assert.Equal(first.AssetId, Assert.Single((await MediaAssetReader.OpenAsync(threadDirectory)).GetPage(),
            asset => asset.AssetId == first.AssetId).AssetId);
    }

    private static DownloadItem Item(StubImageBoard board, string root, byte[] bytes, string mediaId = "456")
        => new DownloadItem(new Uri($"https://stub.test/media/{mediaId}.bin"),
            Path.Combine(root, "stub", "g", "123", mediaId + ".bin"), board,
            board.GetThread(new Uri("https://stub.test/g/thread/123")), mediaId,
            expectedSize: bytes.Length, expectedMd5: Convert.ToBase64String(MD5.HashData(bytes)),
            originalFileName: "same-name.bin");

    private static async Task<MediaCommitResult> CommitAsync(MediaStore store, DownloadItem item, byte[] bytes)
    {
        using MemoryStream source = new MemoryStream(bytes, writable: false);
        return await store.CommitAsync(item, source, CancellationToken.None);
    }

    private static async Task<byte[]> ReadBytesAsync(MediaAssetSnapshot snapshot, string assetId)
    {
        await using Stream stream = await snapshot.OpenVerifiedReadAsync(assetId);
        Assert.Equal(0, stream.Position);
        using MemoryStream bytes = new MemoryStream();
        await stream.CopyToAsync(bytes);
        return bytes.ToArray();
    }
}
