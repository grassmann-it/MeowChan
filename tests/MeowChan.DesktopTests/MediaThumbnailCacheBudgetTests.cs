using MeowChan.Desktop;

namespace MeowChan.DesktopTests;

public sealed class MediaThumbnailCacheBudgetTests
{
    private sealed class Preview(long bytes) : IDisposable
    {
        internal long Bytes { get; } = bytes;
        internal bool Disposed;
        public void Dispose() => Disposed = true;
    }

    private static MediaThumbnailCache<Preview> Cache(int capacity, long budget) =>
        new(preview => preview.Bytes, capacity, budget);

    [Theory]
    [InlineData(240, 180, 172_800L)]
    [InlineData(480, 360, 691_200L)]
    [InlineData(1040, 720, 2_995_200L)]
    public void DecodedPixelDimensionsAreTheDeterministicByteCost(int width, int height, long expected)
    {
        Assert.Equal(expected, MediaTileThumbnails.BitmapBytes(width, height));
    }

    [Fact]
    public void TileSettingDerivesTheBudgetWithoutChangingItsConfiguredUpperBound()
    {
        Assert.Equal(Config.ThumbnailCacheTilesDefault, MediaThumbnailCache.DefaultCapacity);
        Assert.Equal(384L * MediaThumbnailCache.ReferencePreviewBytes,
            MediaThumbnailCache.ByteBudgetForTiles(Config.ThumbnailCacheTilesDefault));
        Assert.Equal(32L * MediaThumbnailCache.ReferencePreviewBytes,
            MediaThumbnailCache.ByteBudgetForTiles(Config.ThumbnailCacheTilesMinimum));
        Assert.Equal(8192L * MediaThumbnailCache.ReferencePreviewBytes,
            MediaThumbnailCache.ByteBudgetForTiles(Config.ThumbnailCacheTilesMaximum));

        MediaThumbnailCache<Preview> cache = Cache(2, MediaThumbnailCache.ByteBudgetForTiles(2));
        Preview first = cache.Add("a", new(MediaThumbnailCache.ReferencePreviewBytes));
        Preview second = cache.Add("b", new(MediaThumbnailCache.ReferencePreviewBytes));
        cache.Release(first); cache.Release(second);
        Preview overCount = cache.Add("c", new(MediaThumbnailCache.ReferencePreviewBytes));
        Assert.Equal(2, cache.Capacity);
        Assert.Equal(2, cache.Count);
        cache.Release(overCount);
    }

    [Fact]
    public void SameBytesAtTwoDecodeSizesShareTheByteBudgetButKeepSeparateKeys()
    {
        MediaThumbnailCache<Preview> cache = Cache(4, 10);
        Preview normal = cache.Add("sha:length@reader240x180", new(4));
        cache.Release(normal);
        Preview wide = cache.Add("sha:length@reader480x360", new(6));
        cache.Release(wide);

        Assert.Equal(2, cache.Count);
        Assert.Equal(10, cache.Bytes);
        Assert.Same(normal, cache.TryAcquire("sha:length@reader240x180"));
        Assert.Same(wide, cache.TryAcquire("sha:length@reader480x360"));
        cache.Release(normal); cache.Release(wide);
    }

    [Fact]
    public void MixedSizesUseOneBudgetAndAnOversizedLargeDecodeIsNotRetained()
    {
        long budget = MediaTileThumbnails.BitmapBytes(240, 180) + MediaTileThumbnails.BitmapBytes(480, 360);
        MediaThumbnailCache<Preview> cache = Cache(8, budget);
        Preview normal = cache.Add("normal", new(MediaTileThumbnails.BitmapBytes(240, 180)));
        cache.Release(normal);
        Preview wide = cache.Add("wide", new(MediaTileThumbnails.BitmapBytes(480, 360)));
        cache.Release(wide);
        Assert.Equal(MediaTileThumbnails.BitmapBytes(240, 180) + MediaTileThumbnails.BitmapBytes(480, 360), cache.Bytes);

        Preview large = cache.Add("large-reader", new(MediaTileThumbnails.BitmapBytes(1040, 720)));
        Assert.Equal(2, cache.Count);
        Assert.Null(cache.TryAcquire("large-reader"));
        Assert.False(large.Disposed); // it is caller-owned until its lease is released
        cache.Release(large);
        Assert.True(large.Disposed);
        Assert.Equal(2, cache.Count);
    }

    [Fact]
    public void LoweringTheBudgetEvictsImmediatelyInLruOrderButNeverDisposesActiveLeases()
    {
        MediaThumbnailCache<Preview> cache = Cache(4, 8);
        Preview oldest = cache.Add("oldest", new(4));
        cache.Release(oldest);
        Preview active = cache.Add("active", new(4));
        Preview activeLease = Assert.IsType<Preview>(cache.TryAcquire("active"));

        cache.BudgetBytes = 4;
        Assert.Equal(1, cache.Count);
        Assert.Equal(4, cache.Bytes);
        Assert.True(oldest.Disposed);
        Assert.False(active.Disposed);

        cache.BudgetBytes = 0;
        Assert.Equal(0, cache.Count);
        Assert.Equal(0, cache.Bytes);
        Assert.False(active.Disposed);
        cache.Release(activeLease);
        Assert.False(active.Disposed);
        cache.Release(active);
        Assert.True(active.Disposed);
    }

    [Fact]
    public void CountDowngradeAndByteDowngradeBothDisposeOnlyUnleasedLruEntries()
    {
        MediaThumbnailCache<Preview> cache = Cache(3, 12);
        Preview a = cache.Add("a", new(4)); cache.Release(a);
        Preview b = cache.Add("b", new(4)); cache.Release(b);
        Preview c = cache.Add("c", new(4)); cache.Release(c);

        cache.Capacity = 2;
        Assert.Equal(2, cache.Count);
        Assert.True(a.Disposed);
        Assert.False(b.Disposed && c.Disposed);

        cache.BudgetBytes = 4;
        Assert.Equal(1, cache.Count);
        Assert.Equal(4, cache.Bytes);
        Assert.True(b.Disposed);
        Assert.False(c.Disposed);
        cache.BudgetBytes = 0;
        Assert.True(c.Disposed);
    }
}
