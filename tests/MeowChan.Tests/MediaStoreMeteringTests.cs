using System.Security.Cryptography;
using MeowChan.Downloader;
using MeowChan.Http;
using MeowChan.Interfaces;
using MeowChan.Storage;

namespace MeowChan.Tests;

/// <summary>Disk counting of the media store. Each test uses a board with its own unique name, hence its own counters.</summary>
public sealed class MediaStoreMeteringTests
{
    private static readonly byte[] Payload = Enumerable.Range(0, 250_000).Select(value => (byte)(value * 31)).ToArray();

    private static long Read(ref long field) => Volatile.Read(ref field);

    private static DownloadItem CreateItem(IImageBoard board, string root, string mediaId = "456")
    {
        IThread thread = board.GetThread(new Uri("https://meter.test/g/thread/123"));
        return new DownloadItem(new Uri($"https://meter.test/media/{mediaId}.bin"),
            Path.Combine(root, board.Name, "g", "123", mediaId + ".bin"), board, thread, mediaId,
            expectedSize: Payload.Length, expectedMd5: Convert.ToBase64String(MD5.HashData(Payload)),
            originalFileName: "same-name.bin", legacyPath: null);
    }

    [Fact]
    public async Task CommitCountsWrittenBytesAndALaterRestoreCountsTheReadBack()
    {
        using TempDirectory directory = new();
        using StubImageBoard inner = new();
        NamedBoard board = new(inner, "meter-disk-" + Guid.NewGuid().ToString("N"));
        NamedBoard other = new(inner, "meter-disk-" + Guid.NewGuid().ToString("N"));
        DownloadItem item = CreateItem(board, directory.Path);
        SourceCounters counters = TransferMeter.For(board.Name);

        await new MediaStore().CommitAsync(item, new MemoryStream(Payload), CancellationToken.None);

        Assert.Equal(Payload.Length, Read(ref counters.DiskWritten));
        Assert.Equal(0, Read(ref counters.DiskRead));

        // A fresh store has no verified-file memo: restoring hashes the published file from disk.
        Assert.NotNull(await new MediaStore().TryRestoreAsync(item, CancellationToken.None));
        Assert.Equal(Payload.Length, Read(ref counters.DiskWritten));
        Assert.Equal(Payload.Length, Read(ref counters.DiskRead));

        // Another source's counters never move.
        SourceCounters untouched = TransferMeter.For(other.Name);
        Assert.Equal(0, Read(ref untouched.DiskWritten) + Read(ref untouched.DiskRead));
    }

    [Fact]
    public async Task AFailedCopyCountsTheBytesThatReachedTheDisk()
    {
        using TempDirectory directory = new();
        using StubImageBoard inner = new();
        NamedBoard board = new(inner, "meter-disk-" + Guid.NewGuid().ToString("N"));
        DownloadItem item = CreateItem(board, directory.Path);
        using PartialFailureStream broken = new(Payload);

        await Assert.ThrowsAsync<IOException>(() => new MediaStore().CommitAsync(item, broken, CancellationToken.None));

        SourceCounters counters = TransferMeter.For(board.Name);
        Assert.Equal(3, Read(ref counters.DiskWritten));
        Assert.Equal(0, Read(ref counters.DiskRead));
    }

    [Fact]
    public async Task ACommitOfAlreadyPresentBytesCountsTheTemporaryWriteAndTheComparisonRead()
    {
        using TempDirectory directory = new();
        using StubImageBoard inner = new();
        NamedBoard board = new(inner, "meter-disk-" + Guid.NewGuid().ToString("N"));
        DownloadItem item = CreateItem(board, directory.Path);
        MediaStore store = new();
        await store.CommitAsync(item, new MemoryStream(Payload), CancellationToken.None);
        SourceCounters counters = TransferMeter.For(board.Name);
        long written = Read(ref counters.DiskWritten), read = Read(ref counters.DiskRead);

        MediaCommitResult again = await new MediaStore().CommitAsync(item, new MemoryStream(Payload), CancellationToken.None);

        Assert.True(again.Reused);
        Assert.Equal(written + Payload.Length, Read(ref counters.DiskWritten));
        Assert.True(Read(ref counters.DiskRead) >= read + Payload.Length);
    }

    [Fact]
    public async Task LocalImportsCountUnderTheLocalKeyBecauseTheyHaveNoDownloadSource()
    {
        using TempDirectory directory = new();
        SourceCounters local = TransferMeter.For(TransferMeter.LocalKey);
        long before = Read(ref local.DiskWritten);
        string sha = Convert.ToHexString(SHA256.HashData(Payload)).ToLowerInvariant();

        await new MediaStore().CommitLocalFileAsync(directory.Path, "imported.bin", "import-1", "imported.bin",
            new MemoryStream(Payload), sha, Payload.Length, CancellationToken.None);

        // Other tests may import concurrently, so only the lower bound is exact.
        Assert.True(Read(ref local.DiskWritten) - before >= Payload.Length);
    }

    /// <summary>Delegates to a stub board but reports a unique source name.</summary>
    private sealed class NamedBoard(IImageBoard inner, string name) : IImageBoard
    {
        public string Name => name;
        public string NiceName => inner.NiceName;
        public Uri BaseUri => inner.BaseUri;
        public bool CanHandle(Uri uri) => inner.CanHandle(uri);
        public bool IsThreadUri(Uri uri) => inner.IsThreadUri(uri);
        public bool IsBoardUri(Uri uri) => inner.IsBoardUri(uri);
        public IBoard GetBoard(Uri boardUri) => inner.GetBoard(boardUri);
        public IThread GetThread(Uri threadUri) => inner.GetThread(threadUri);
        public HttpClient GetHttpClient() => inner.GetHttpClient();
    }
}
