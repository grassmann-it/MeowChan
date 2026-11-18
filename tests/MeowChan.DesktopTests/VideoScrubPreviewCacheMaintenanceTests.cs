using System.Runtime.CompilerServices;
using MeowChan.Desktop;
using SkiaSharp;

namespace MeowChan.DesktopTests;

/// <summary>
/// Scrub cache maintenance: reads hold the directory coordinator only for bookkeeping, eviction tolerates locked candidates,
/// warm hits do not decode twice, corrupt files are never returned, and collected coordinators are pruned. No native decoder.
/// </summary>
public sealed class VideoScrubPreviewCacheMaintenanceTests
{
    private const long EightMiB = 8L * 1024 * 1024;

    [Fact]
    public async Task ABudgetChangeAndAStoreDoNotWaitForASlowRead()
    {
        using TempDirectory temp = new();
        string directory = Path.Combine(temp.Path, "scrub");
        TaskCompletionSource reading = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        VideoScrubPreviewCache cache = new(directory, new VideoScrubPreviewOptions(64), testSeam: new VideoScrubPreviewCacheTestSeam
        {
            AfterFileRead = async (_, token) =>
            {
                reading.TrySetResult();
                await release.Task.WaitAsync(token);
            },
        });
        VideoScrubFrameKey key = Key(1);
        byte[] png = Png(32, 18, SKColors.Coral);
        await cache.StoreAsync(key, png, CancellationToken.None);

        Task<byte[]?> read = cache.ReadAsync(key, CancellationToken.None);
        await reading.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // The synchronous form (the profile host calls it on the thread pool) and the asynchronous form both finish while the read
        // is still in progress: the read does not hold the coordinator over file I/O or validation.
        await Task.Run(() => cache.Configure(new VideoScrubPreviewOptions(32))).WaitAsync(TimeSpan.FromSeconds(5));
        await cache.ConfigureAsync(new VideoScrubPreviewOptions(VideoScrubPreviewOptions.CacheLimitMiBMinimum), CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));
        await new VideoScrubPreviewCache(directory, new VideoScrubPreviewOptions(VideoScrubPreviewOptions.CacheLimitMiBMinimum))
            .StoreAsync(Key(2), Png(32, 18, SKColors.Teal), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(read.IsCompleted);

        release.SetResult();
        Assert.Equal(png, await read.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(VideoScrubPreviewOptions.CacheLimitMiBMinimum, cache.Options.CacheLimitMiB);
    }

    [Fact]
    public async Task AnInvalidReadDoesNotDeleteAThumbnailStoredWhileItWasBeingRead()
    {
        using TempDirectory temp = new();
        string directory = Path.Combine(temp.Path, "scrub");
        TaskCompletionSource reading = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        VideoScrubPreviewCache slowReader = new(directory, Options(), testSeam: new VideoScrubPreviewCacheTestSeam
        {
            AfterFileRead = async (_, token) =>
            {
                reading.TrySetResult();
                await release.Task.WaitAsync(token);
            },
        });
        VideoScrubPreviewCache writer = new(directory, Options());
        VideoScrubFrameKey key = Key(7);
        Directory.CreateDirectory(directory);
        await File.WriteAllBytesAsync(slowReader.PathOf(key), [1, 2, 3, 4, 5, 6, 7, 8, 9, 10]);

        Task<byte[]?> read = slowReader.ReadAsync(key, CancellationToken.None);
        await reading.Task.WaitAsync(TimeSpan.FromSeconds(5));
        byte[] valid = Png(32, 18, SKColors.Coral);
        await writer.StoreAsync(key, valid, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        release.SetResult();

        Assert.Null(await read.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(valid, await File.ReadAllBytesAsync(writer.PathOf(key)));
        Assert.Equal(valid, await writer.ReadAsync(key, CancellationToken.None));
    }

    [Fact]
    public async Task AReadWhoseFileVanishedMeanwhileReturnsTheVerifiedBytesWithoutLeavingPhantomBytes()
    {
        using TempDirectory temp = new();
        string directory = Path.Combine(temp.Path, "scrub");
        Directory.CreateDirectory(directory);
        byte[] png = Png(32, 18, SKColors.Coral);
        byte[] next = Png(32, 18, SKColors.Teal);
        // The filler leaves room for exactly one of the two thumbnails. It exists before the first use, so the initial scan accounts
        // it as the least recently used entry: a phantom entry for the vanished first thumbnail would force its eviction below.
        string filler = Path.Combine(directory, "filler.png");
        using (FileStream stream = File.Create(filler)) stream.SetLength(EightMiB - Math.Max(png.Length, next.Length) - 1);
        TaskCompletionSource reading = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        VideoScrubPreviewCache cache = new(directory, Options(), testSeam: new VideoScrubPreviewCacheTestSeam
        {
            AfterFileRead = async (_, token) =>
            {
                reading.TrySetResult();
                await release.Task.WaitAsync(token);
            },
        });
        VideoScrubFrameKey key = Key(8);
        await cache.StoreAsync(key, png, CancellationToken.None);
        Assert.True(File.Exists(filler));

        Task<byte[]?> read = cache.ReadAsync(key, CancellationToken.None);
        await reading.Task.WaitAsync(TimeSpan.FromSeconds(5));
        File.Delete(cache.PathOf(key));
        release.SetResult();
        Assert.Equal(png, await read.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.False(File.Exists(cache.PathOf(key)));

        VideoScrubFrameKey nextKey = Key(9);
        await new VideoScrubPreviewCache(directory, Options()).StoreAsync(nextKey, next, CancellationToken.None);
        Assert.True(File.Exists(filler));
        Assert.True(File.Exists(cache.PathOf(nextKey)));
    }

    [Fact]
    public async Task ALockedEvictionCandidateIsSkippedAndTheStoreStillFitsItsBudget()
    {
        using TempDirectory temp = new();
        string directory = Path.Combine(temp.Path, "scrub");
        Directory.CreateDirectory(directory);
        for (int index = 0; index < 8; index++)
        {
            string path = Path.Combine(directory, index.ToString("D2") + ".png");
            using (FileStream stream = File.Create(path)) stream.SetLength(1024 * 1024);
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(index - 100));
        }
        string locked = Path.Combine(directory, "00.png");
        VideoScrubPreviewCache cache = new(directory, Options(), testSeam: new VideoScrubPreviewCacheTestSeam
        {
            DeleteCacheFile = path =>
            {
                if (path == locked) throw new IOException("The file is in use.");
                File.Delete(path);
            },
        });
        cache.Resync();

        VideoScrubFrameKey key = Key(10);
        await cache.StoreAsync(key, Png(32, 18, SKColors.Coral), CancellationToken.None);

        Assert.True(File.Exists(locked));
        Assert.False(File.Exists(Path.Combine(directory, "01.png")));
        Assert.True(File.Exists(Path.Combine(directory, "02.png")));
        Assert.True(File.Exists(cache.PathOf(key)));
        Assert.True(Directory.GetFiles(directory, "*.png").Sum(path => new FileInfo(path).Length) <= EightMiB);
    }

    [Fact]
    public void AnExplicitTrimEvictsEverythingDeletableAndReportsWhatLockedFilesKeptOverBudget()
    {
        using TempDirectory temp = new();
        string directory = Path.Combine(temp.Path, "scrub");
        Directory.CreateDirectory(directory);
        for (int index = 0; index < 12; index++)
        {
            string path = Path.Combine(directory, index.ToString("D2") + ".png");
            using (FileStream stream = File.Create(path)) stream.SetLength(1024 * 1024);
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(index - 100));
        }
        HashSet<string> locked = Enumerable.Range(0, 9).Select(index => Path.Combine(directory, index.ToString("D2") + ".png"))
            .ToHashSet(StringComparer.Ordinal);
        VideoScrubPreviewCache cache = new(directory, Options(), testSeam: new VideoScrubPreviewCacheTestSeam
        {
            DeleteCacheFile = path =>
            {
                if (locked.Contains(path)) throw new UnauthorizedAccessException("Protected.");
                File.Delete(path);
            },
        });

        Assert.Throws<IOException>(() => cache.TrimToBudget());
        // Nine locked files (9 MiB) cannot fit 8 MiB; every deletable file was still evicted instead of stopping at the first lock.
        Assert.Equal(9, Directory.GetFiles(directory, "*.png").Length);
        Assert.All(locked, path => Assert.True(File.Exists(path)));
    }

    [Fact]
    public void DeleteSeamsArePerCacheCallAndDoNotLeakThroughSharedState()
    {
        using TempDirectory temp = new();
        string directory = Path.Combine(temp.Path, "scrub");
        Directory.CreateDirectory(directory);
        for (int index = 0; index < 9; index++)
        {
            string path = Path.Combine(directory, index.ToString("D2") + ".png");
            using FileStream stream = File.Create(path);
            stream.SetLength(1024 * 1024);
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(index - 100));
        }

        VideoScrubPreviewCache blocked = new(directory, Options(), testSeam: new VideoScrubPreviewCacheTestSeam
        {
            DeleteCacheFile = _ => throw new IOException("The candidate is locked."),
        });
        blocked.Resync();
        VideoScrubPreviewCache deleting = new(directory, Options(), testSeam: new VideoScrubPreviewCacheTestSeam
        {
            DeleteCacheFile = File.Delete,
        });

        Assert.Throws<IOException>(() => blocked.TrimToBudget());
        Assert.Equal(9, Directory.GetFiles(directory, "*.png").Length);
        deleting.TrimToBudget();
        Assert.Equal(8, Directory.GetFiles(directory, "*.png").Length);
    }

    [Fact]
    public async Task AWarmHitPerformsNoValidationDecode()
    {
        using TempDirectory temp = new();
        int fullDecodes = 0;
        VideoScrubPreviewCache cache = new(Path.Combine(temp.Path, "scrub"), Options(), testSeam: new VideoScrubPreviewCacheTestSeam
        {
            FullDecodeValidation = () => Interlocked.Increment(ref fullDecodes),
        });
        VideoScrubFrameKey key = Key(11);
        byte[] png = Png(32, 18, SKColors.Coral);

        await cache.StoreAsync(key, png, CancellationToken.None);
        Assert.Equal(1, fullDecodes);
        Assert.Equal(png, await cache.ReadAsync(key, CancellationToken.None));
        Assert.Equal(png, await cache.ReadAsync(key, CancellationToken.None));
        Assert.Equal(1, fullDecodes);
    }

    public static TheoryData<string> Corruptions => new() { "drop-iend", "truncate-middle", "flip-idat", "append" };

    [Theory]
    [MemberData(nameof(Corruptions))]
    public async Task ACorruptStoredThumbnailIsDeletedAndNeverReturned(string corruption)
    {
        using TempDirectory temp = new();
        string directory = Path.Combine(temp.Path, "scrub");
        VideoScrubPreviewCache cache = new(directory, Options());
        VideoScrubFrameKey key = Key(12);
        Directory.CreateDirectory(directory);
        await File.WriteAllBytesAsync(cache.PathOf(key),
            VideoPosterCacheMaintenanceTests.Corrupt(Png(32, 18, SKColors.Coral), corruption));

        Assert.Null(await cache.ReadAsync(key, CancellationToken.None));
        Assert.False(File.Exists(cache.PathOf(key)));
    }

    [Fact]
    public void CoordinatorsOfCollectedCachesArePruned()
    {
        using TempDirectory temp = new();
        string[] directories = Enumerable.Range(0, 32).Select(index => Path.Combine(temp.Path, "scrub-" + index)).ToArray();
        CreateAndDrop(directories);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        VideoScrubPreviewCache survivor = new(Path.Combine(temp.Path, "survivor"), Options());

        Assert.All(directories, directory => Assert.False(VideoScrubPreviewCache.HasRegisteredState(directory)));
        Assert.True(VideoScrubPreviewCache.HasRegisteredState(survivor.Directory));
        GC.KeepAlive(survivor);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void CreateAndDrop(string[] directories)
    {
        foreach (string directory in directories)
            _ = new VideoScrubPreviewCache(directory, Options());
    }

    private static VideoScrubPreviewOptions Options() => new(VideoScrubPreviewOptions.CacheLimitMiBMinimum);

    private static VideoScrubFrameKey Key(int value) =>
        new(value.ToString("x").PadLeft(64, '0'), value + 1, value % VideoScrubSlots.Count);

    private static byte[] Png(int width, int height, SKColor color) => VideoPosterCacheMaintenanceTests.Png(width, height, color);
}
