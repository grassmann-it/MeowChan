using MeowChan.Gallery;
using MeowChan.Persistence;
using static MeowChan.Tests.MediaLibraryIndexTests;

namespace MeowChan.Tests;

/// <summary>
/// When the page is told about the media index cache limit: from 75 percent of it on, and whenever a save was stopped.
/// Below that share a save stays silent, so the status bar does not warn about a limit that is nowhere near.
/// </summary>
public sealed class MediaLibraryIndexCacheWarningTests
{
    private static MediaLibraryIndexCacheWrite Write(MediaLibraryIndexCacheWriteStatus status, long bytes, long limit) =>
        new(status, bytes, limit, null);

    [Theory]
    // 75 percent of 1000 bytes is the first share that is reported; a byte less stays silent.
    [InlineData(0, false)]
    [InlineData(749, false)]
    [InlineData(750, true)]
    [InlineData(751, true)]
    [InlineData(1_000, true)]
    [InlineData(4_000, true)]
    public void AWrittenIndexIsReportedFromThreeQuartersOfTheLimitOn(long bytes, bool reported)
    {
        Assert.Equal(reported, Write(MediaLibraryIndexCacheWriteStatus.Written, bytes, 1_000).NearOrOverLimit);
        // An index that was already saved is the same case: the page says the same thing on the next visit.
        Assert.Equal(reported, Write(MediaLibraryIndexCacheWriteStatus.Unchanged, bytes, 1_000).NearOrOverLimit);
    }

    [Fact]
    public void ASaveStoppedByTheSizeLimitIsAlwaysReportedAndACountStoppedSaveCarriesItsOwnStatus()
    {
        // Over the size limit the page has to say so, however small the share looks.
        Assert.True(Write(MediaLibraryIndexCacheWriteStatus.TooLarge, 4_000, 1_000).NearOrOverLimit);
        // The entry and thread limits are other settings: the page names them from the status, not from the share,
        // so a count-stopped save is not turned into a sentence about the size limit.
        MediaLibraryIndexCacheWrite counted = Write(MediaLibraryIndexCacheWriteStatus.TooManyEntries, 10, 1_000);
        Assert.False(counted.NearOrOverLimit);
        Assert.Equal(MediaLibraryIndexCacheWriteStatus.TooManyEntries, counted.Status);
        // An empty index against the real default limit is as far from it as it gets.
        Assert.False(Write(MediaLibraryIndexCacheWriteStatus.Written, 0, MediaLibraryIndexCache.DefaultLimitBytes).NearOrOverLimit);
    }

    [Fact]
    public void TheShareIsThreeQuartersAndTheSettingsRangeCannotExceedTheTechnicalCeiling()
    {
        Assert.Equal(0.75, MediaLibraryIndexCache.WarningShare);
        Assert.True(MediaLibraryIndexCache.DefaultLimitBytes <= MediaLibraryIndexCache.MaximumBytes);
        Assert.True(MediaLibraryIndexCache.MinimumLimitBytes < MediaLibraryIndexCache.DefaultLimitBytes);
    }

    [Fact]
    public async Task ASavedIndexFarBelowItsLimitSaysNothingAndTheSameIndexJustUnderItDoesNotEither()
    {
        using TempDirectory temp = new();
        await SeedAsync(temp.Path);
        string path = Path.Combine(temp.Path, "cache", MediaLibraryIndexCache.FileName);
        MediaLibrarySnapshot built = await BuildAsync(temp.Path);

        MediaLibraryIndexCacheWrite roomy = await new MediaLibraryIndexCacheFile(path).SaveAsync(built, MediaLibraryIndexCache.DefaultLimitBytes);
        Assert.Equal(MediaLibraryIndexCacheWriteStatus.Written, roomy.Status);
        Assert.False(roomy.NearOrOverLimit);

        // The same index against a limit it fills to just under three quarters: still saved, still silent.
        File.Delete(path);
        MediaLibraryIndexCacheWrite quiet = await new MediaLibraryIndexCacheFile(path).SaveAsync(built, roomy.Bytes * 2);
        Assert.Equal(MediaLibraryIndexCacheWriteStatus.Written, quiet.Status);
        Assert.False(quiet.NearOrOverLimit);
        Assert.Equal(built.Entries, MediaLibraryIndexCacheSync.Load(path)!.Entries);
    }
}
