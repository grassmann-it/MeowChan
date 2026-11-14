using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeowChan.Desktop;
using SkiaSharp;

namespace MeowChan.DesktopTests;

/// <summary>Filesystem-level accounting tests for the scrub cache. They use real PNGs and real parallel stores, not timing or source inspection.</summary>
public sealed class VideoScrubPreviewCacheBudgetTests
{
    private const long EightMiB = 8L * 1024 * 1024;

    [Fact]
    public async Task ReplacingAKeyCountsOnlyThePublishedRevisionAcrossInstances()
    {
        using TempDirectory temp = new();
        string directory = Path.Combine(temp.Path, "scrub");
        VideoScrubFrameKey key = Key(1);
        byte[] first = Png(11);
        byte[] replacement = Png(12);
        VideoScrubPreviewCache firstInstance = Cache(directory);
        VideoScrubPreviewCache secondInstance = Cache(directory);

        await firstInstance.StoreAsync(key, first, CancellationToken.None);
        await secondInstance.StoreAsync(key, replacement, CancellationToken.None);

        string[] files = Directory.GetFiles(directory, "*.png");
        Assert.Single(files);
        Assert.Equal(replacement, await firstInstance.ReadAsync(key, CancellationToken.None));
        Assert.Equal(replacement.LongLength, files.Sum(path => new FileInfo(path).Length));
    }

    [Fact]
    public async Task ParallelInstancesShareOneByteBudgetAndPublishOnlyCompletedStores()
    {
        using TempDirectory temp = new();
        string directory = Path.Combine(temp.Path, "scrub");
        VideoScrubPreviewCache first = Cache(directory);
        // The separator variant must resolve to the same process-wide coordinator as the canonical path.
        VideoScrubPreviewCache second = Cache(directory + Path.DirectorySeparatorChar);
        Task[] stores = Enumerable.Range(0, 48)
            .Select(index => (index & 1) == 0
                ? first.StoreAsync(Key(index), Png(index), CancellationToken.None)
                : second.StoreAsync(Key(index), Png(index), CancellationToken.None))
            .ToArray();

        await Task.WhenAll(stores);

        string[] files = Directory.GetFiles(directory, "*.png");
        Assert.NotEmpty(files);
        Assert.True(files.Sum(path => new FileInfo(path).Length) <= EightMiB);
        Assert.All(files, path => Assert.True(VideoScrubPreviewCache.Valid(File.ReadAllBytes(path))));
        Assert.Empty(Directory.GetFiles(directory, ".poster-scrub-*.tmp"));
    }

    [Fact]
    public async Task AReadHitMovesTheFileToTheLruTailWithoutAReadDirectoryScan()
    {
        using TempDirectory temp = new();
        string directory = Path.Combine(temp.Path, "scrub");
        List<VideoScrubFrameKey> seeded = Enumerable.Range(0, 48).Select(Key).ToList();
        for (int index = 0; index < seeded.Count; index++)
        {
            string path = Path.Combine(directory, seeded[index].FileName);
            Directory.CreateDirectory(directory);
            await File.WriteAllBytesAsync(path, Png(index));
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-seeded.Count + index));
        }

        VideoScrubPreviewCache cache = Cache(directory);
        cache.TrimToBudget();
        HashSet<string> beforeHit = Directory.GetFiles(directory, "*.png").ToHashSet(StringComparer.Ordinal);
        Assert.True(beforeHit.Count < seeded.Count);

        VideoScrubFrameKey hitKey = seeded.First(key => File.Exists(Path.Combine(directory, key.FileName)));
        Assert.NotNull(await cache.ReadAsync(hitKey, CancellationToken.None));
        byte[] newPng = Png(1000);
        VideoScrubFrameKey newKey = Key(1000);

        // Ensure the next store has to evict at least one file, without relying on filesystem timestamp resolution.
        int extra = seeded.Count;
        while (Directory.GetFiles(directory, "*.png").Sum(path => new FileInfo(path).Length) + newPng.LongLength <= EightMiB)
        {
            string extraPath = Path.Combine(directory, Key(extra).FileName);
            await File.WriteAllBytesAsync(extraPath, Png(extra));
            extra++;
            cache.Resync();
        }

        await cache.StoreAsync(newKey, newPng, CancellationToken.None);
        HashSet<string> afterStore = Directory.GetFiles(directory, "*.png").ToHashSet(StringComparer.Ordinal);

        Assert.Contains(Path.Combine(directory, hitKey.FileName), afterStore);
        Assert.Contains(Path.Combine(directory, newKey.FileName), afterStore);
        Assert.True(beforeHit.Except(afterStore).Any());
        Assert.True(afterStore.Sum(path => new FileInfo(path).Length) <= EightMiB);
    }

    [Fact]
    public void LoweringTheBudgetTrimsImmediatelyAndKeepsExternalFilesOutOfTheLedger()
    {
        using TempDirectory temp = new();
        string directory = Path.Combine(temp.Path, "scrub");
        VideoScrubPreviewCache cache = Cache(directory, 64);
        Directory.CreateDirectory(directory);
        for (int index = 0; index < 12; index++)
        {
            string path = Path.Combine(directory, index.ToString("D2") + ".png");
            using FileStream stream = File.Create(path);
            stream.SetLength(1024 * 1024);
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(index - 100));
        }

        cache.TrimToBudget();
        cache.Configure(new VideoScrubPreviewOptions(VideoScrubPreviewOptions.CacheLimitMiBMinimum));

        Assert.True(Directory.GetFiles(directory, "*.png").Sum(path => new FileInfo(path).Length) <= EightMiB);
        Assert.False(File.Exists(Path.Combine(directory, "00.png")));
        Assert.True(File.Exists(Path.Combine(directory, "11.png")));
    }

    [Fact]
    public async Task ExplicitRepairReconcilesDeletedAndExternallyResizedEntriesBeforeTheNextStore()
    {
        using TempDirectory temp = new();
        string directory = Path.Combine(temp.Path, "scrub");
        VideoScrubPreviewCache cache = Cache(directory);
        Directory.CreateDirectory(directory);
        for (int index = 0; index < 8; index++)
        {
            string path = Path.Combine(directory, index.ToString("D2") + ".png");
            using FileStream stream = File.Create(path);
            stream.SetLength(1024 * 1024);
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(index - 100));
        }

        cache.TrimToBudget();
        File.Delete(Path.Combine(directory, "00.png"));
        using (FileStream resized = File.Open(Path.Combine(directory, "01.png"), FileMode.Open, FileAccess.Write, FileShare.Read))
            resized.SetLength(2 * 1024 * 1024);
        cache.Resync();

        VideoScrubFrameKey key = Key(3000);
        await cache.StoreAsync(key, Png(3000), CancellationToken.None);

        Assert.False(File.Exists(Path.Combine(directory, "01.png")));
        Assert.True(File.Exists(Path.Combine(directory, "02.png")));
        Assert.True(File.Exists(cache.PathOf(key)));
        Assert.True(Directory.GetFiles(directory, "*.png").Sum(path => new FileInfo(path).Length) <= EightMiB);
    }

    [Fact]
    public async Task FailedPublishReleasesCoordinatorAndRemovesTemporary()
    {
        using TempDirectory temp = new();
        string directory = Path.Combine(temp.Path, "scrub");
        VideoScrubPreviewCache cache = Cache(directory);
        Directory.CreateDirectory(directory);
        VideoScrubFrameKey key = Key(3001);
        Directory.CreateDirectory(cache.PathOf(key));

        await Assert.ThrowsAnyAsync<IOException>(() => cache.StoreAsync(key, Png(3001), CancellationToken.None));
        Assert.Empty(Directory.GetFiles(directory, ".poster-scrub-*.tmp"));

        Directory.Delete(cache.PathOf(key));
        await cache.StoreAsync(key, Png(3002), CancellationToken.None);
        Assert.True(File.Exists(cache.PathOf(key)));
    }

    [Fact]
    public void ExplicitRepairRemovesStaleTemporaryFilesWithoutRemovingFreshWrites()
    {
        using TempDirectory temp = new();
        string directory = Path.Combine(temp.Path, "scrub");
        VideoScrubPreviewCache cache = Cache(directory);
        Directory.CreateDirectory(directory);
        string stale = Path.Combine(directory, ".poster-scrub-stale.tmp");
        string fresh = Path.Combine(directory, ".poster-scrub-fresh.tmp");
        File.WriteAllBytes(stale, new byte[] { 1 });
        File.WriteAllBytes(fresh, new byte[] { 2 });
        File.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddDays(-2));

        cache.Resync();

        Assert.False(File.Exists(stale));
        Assert.True(File.Exists(fresh));
    }

    [Fact]
    public async Task DeletedFilesAndCanceledWritesDoNotCreatePhantomBytes()
    {
        using TempDirectory temp = new();
        string directory = Path.Combine(temp.Path, "scrub");
        VideoScrubPreviewCache cache = Cache(directory);
        Directory.CreateDirectory(directory);
        for (int index = 0; index < 8; index++)
        {
            string path = Path.Combine(directory, index.ToString("D2") + ".png");
            using FileStream stream = File.Create(path);
            stream.SetLength(1024 * 1024);
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(index - 100));
        }
        cache.TrimToBudget();
        File.Delete(Path.Combine(directory, "00.png"));

        VideoScrubFrameKey storedKey = Key(2000);
        byte[] png = Png(2000);
        await cache.StoreAsync(storedKey, png, CancellationToken.None);
        Assert.True(File.Exists(Path.Combine(directory, "01.png")));
        Assert.Equal(8, Directory.GetFiles(directory, "*.png").Length);
        Assert.True(Directory.GetFiles(directory, "*.png").Sum(path => new FileInfo(path).Length) <= EightMiB);

        using CancellationTokenSource canceled = new();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cache.StoreAsync(Key(2001), png, canceled.Token));
        Assert.DoesNotContain(Directory.GetFiles(directory), path => path.EndsWith(".tmp", StringComparison.Ordinal));
        Assert.Equal(8, Directory.GetFiles(directory, "*.png").Length);
    }

    private static VideoScrubPreviewCache Cache(string directory, int limitMiB = VideoScrubPreviewOptions.CacheLimitMiBMinimum) =>
        new(directory, new VideoScrubPreviewOptions(limitMiB));

    private static VideoScrubFrameKey Key(int value) =>
        new(value.ToString("x").PadLeft(64, '0'), value + 1, value % VideoScrubSlots.Count);

    private static byte[] Png(int seed)
    {
        const int width = VideoScrubPreviewOptions.ThumbnailMaximumWidth;
        const int height = VideoScrubPreviewOptions.ThumbnailMaximumHeight;
        using SKBitmap bitmap = new(width, height);
        Random random = new(seed);
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
            bitmap.SetPixel(x, y, new SKColor((byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256)));
        using SKImage image = SKImage.FromBitmap(bitmap);
        using SKData data = image.Encode(SKEncodedImageFormat.Png, 100);
        byte[] png = data.ToArray();
        Assert.True(VideoScrubPreviewCache.Valid(png));
        return png;
    }
}
