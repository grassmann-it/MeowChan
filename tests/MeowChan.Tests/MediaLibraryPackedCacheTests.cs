using System.IO.Compression;
using System.Text.Json.Nodes;
using MeowChan.Gallery;
using MeowChan.Persistence;
using static MeowChan.Tests.MediaLibraryIndexTests;

namespace MeowChan.Tests;

public sealed class MediaLibraryPackedCacheTests
{
    private const long Budget = 256L * 1024 * 1024;

    [Theory]
    [InlineData(1)]
    [InlineData(8)]
    [InlineData(32)]
    public async Task ClippingThePayloadOrTrailerFallsBackToValidatedJson(int removeBytes)
    {
        using TempDirectory temp = new();
        string path = CachePath(temp.Path);
        MediaLibrarySnapshot plain = MediaLibraryIndexBenchmark.Synthetic(100);
        MediaLibrarySnapshot packed = MediaLibraryIndexBenchmark.Synthetic(2_000);
        await StoreBothAsync(path, plain, packed);

        using (FileStream file = File.OpenWrite(path + ".gz")) file.SetLength(file.Length - removeBytes);

        Assert.True(MediaLibrarySnapshot.SameContent(plain, await new MediaLibraryIndexCacheFile(path).LoadAsync()));
        File.Delete(path);
        Assert.Null(await new MediaLibraryIndexCacheFile(path).LoadAsync());
    }

    [Fact]
    public async Task ExpandedByteCeilingFallsBackAndTheFallbackMustAlsoValidate()
    {
        using TempDirectory temp = new();
        string path = CachePath(temp.Path);
        MediaLibrarySnapshot plain = MediaLibraryIndexBenchmark.Synthetic(100);
        MediaLibrarySnapshot packed = MediaLibraryIndexBenchmark.Synthetic(2_000);
        await StoreBothAsync(path, plain, packed);

        MediaLibraryIndexCacheRead read = Assert.IsType<MediaLibraryIndexCacheRead>(await MediaLibraryIndexCache.LoadAsync(path, 64 * 1024));
        Assert.True(MediaLibrarySnapshot.SameContent(plain, read.Snapshot));
        await File.WriteAllTextAsync(path, "{");
        Assert.Null(await MediaLibraryIndexCache.LoadAsync(path, 64 * 1024));
    }

    [Fact]
    public async Task ExpandedByteCeilingCannotBeBypassedByAnUnderstatedTrailer()
    {
        using TempDirectory temp = new();
        string path = CachePath(temp.Path);
        MediaLibrarySnapshot packed = MediaLibraryIndexBenchmark.Synthetic(2_000);
        Assert.Equal(MediaLibraryIndexCacheWriteStatus.Written,
            (await new MediaLibraryIndexCacheFile(path).SaveAsync(packed, 768 * 1024)).Status);
        await using (FileStream file = File.OpenWrite(path + ".gz"))
        {
            file.Position = file.Length - 4;
            await file.WriteAsync(new byte[] { 0, 0, 0, 0 });
        }
        Assert.Null(await MediaLibraryIndexCache.LoadAsync(path, 64 * 1024));
    }

    [Fact]
    public async Task MissingPackedFileUsesOldJsonAndAnExternalJsonReplacementSupersedesPacked()
    {
        using TempDirectory temp = new();
        string path = CachePath(temp.Path);
        MediaLibrarySnapshot old = MediaLibraryIndexBenchmark.Synthetic(100);
        MediaLibrarySnapshot current = MediaLibraryIndexBenchmark.Synthetic(2_000);
        await StoreBothAsync(path, old, current);
        Assert.True(MediaLibrarySnapshot.SameContent(current, await new MediaLibraryIndexCacheFile(path).LoadAsync()));

        File.Delete(path + ".gz");
        Assert.True(MediaLibrarySnapshot.SameContent(old, await new MediaLibraryIndexCacheFile(path).LoadAsync()));
        await StoreBothAsync(path, old, current);
        MediaLibrarySnapshot newer = MediaLibraryIndexBenchmark.Synthetic(300);
        Assert.Equal(MediaLibraryIndexCacheWriteStatus.Written,
            (await MediaLibraryIndexCache.SaveAsync(path, newer, Budget)).Status);
        // A replaced old-format file wins even if its write time is earlier than the packed file's.
        File.SetLastWriteTimeUtc(path, DateTime.UnixEpoch);
        Assert.True(MediaLibrarySnapshot.SameContent(newer, await new MediaLibraryIndexCacheFile(path).LoadAsync()));
    }

    [Fact]
    public async Task OversizedPackedFileFallsBackToVersionThreeJson()
    {
        using TempDirectory temp = new();
        string path = CachePath(temp.Path);
        MediaLibrarySnapshot old = MediaLibraryIndexBenchmark.Synthetic(100);
        await MediaLibraryIndexCache.SaveAsync(path, old, Budget);
        JsonObject legacy = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
        legacy["Version"] = 3;
        await File.WriteAllTextAsync(path, legacy.ToJsonString());
        using (FileStream file = File.Create(path + ".gz")) file.SetLength(MediaLibraryIndexCache.MaximumBytes + 1);
        Assert.True(MediaLibrarySnapshot.SameContent(old, await new MediaLibraryIndexCacheFile(path).LoadAsync()));
    }

    [Fact]
    public async Task ConcurrentPagesSeeCompleteGenerationsAndCancellationLeavesThePreviousFile()
    {
        using TempDirectory temp = new();
        string path = CachePath(temp.Path);
        MediaLibrarySnapshot first = MediaLibraryIndexBenchmark.Synthetic(2_000);
        MediaLibrarySnapshot second = MediaLibraryIndexBenchmark.Synthetic(2_500);
        const long limit = 768 * 1024;
        MediaLibraryIndexCacheFile initial = new(path);
        Assert.Equal(MediaLibraryIndexCacheWriteStatus.Written, (await initial.SaveAsync(first, limit)).Status);
        Task<MediaLibraryIndexCacheWrite> save = new MediaLibraryIndexCacheFile(path).SaveAsync(second, limit);
        Task<MediaLibrarySnapshot?>[] reads = Enumerable.Range(0, 4).Select(_ => new MediaLibraryIndexCacheFile(path).LoadAsync()).ToArray();
        Assert.Equal(MediaLibraryIndexCacheWriteStatus.Written, (await save).Status);
        foreach (MediaLibrarySnapshot? seen in await Task.WhenAll(reads))
            Assert.True(MediaLibrarySnapshot.SameContent(first, seen) || MediaLibrarySnapshot.SameContent(second, seen));

        using CancellationTokenSource cancellation = new();
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => initial.SaveAsync(first, limit, cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => initial.LoadAsync(cancellation.Token));
        Assert.True(MediaLibrarySnapshot.SameContent(second, await new MediaLibraryIndexCacheFile(path).LoadAsync()));
        Assert.Equal(new[] { path + ".gz" }, Directory.GetFiles(Path.GetDirectoryName(path)!));
    }

    [Fact]
    public async Task PackedProfileReopenUsesTheSameFilterCountersAndOrderAsAColdBuild()
    {
        using TempDirectory first = new(), second = new();
        await SeedAsync(first.Path);
        await SeedAsync(second.Path);
        await WriteManifestAsync(Path.Combine(second.Path, "media", "b"), B, new File_("other.png", "x2", [9, 7], "200"));
        MediaLibrarySnapshot firstCold = await BuildAsync(first.Path);
        MediaLibrarySnapshot secondCold = await BuildAsync(second.Path);
        await PackSmallAsync(CachePath(first.Path), firstCold);
        await PackSmallAsync(CachePath(second.Path), secondCold);

        MediaLibrarySnapshot secondLoaded = Assert.IsType<MediaLibrarySnapshot>(await new MediaLibraryIndexCacheFile(CachePath(second.Path)).LoadAsync());
        MediaLibrarySnapshot firstLoaded = Assert.IsType<MediaLibrarySnapshot>(await new MediaLibraryIndexCacheFile(CachePath(first.Path)).LoadAsync());
        Assert.True(MediaLibrarySnapshot.SameContent(firstCold, firstLoaded));
        Assert.True(MediaLibrarySnapshot.SameContent(secondCold, secondLoaded));
        Assert.Contains(secondLoaded.Entries, entry => entry.Name == "other.png");
        Assert.DoesNotContain(firstLoaded.Entries, entry => entry.Name == "other.png");
        MediaLibrarySnapshot rebuilt = await BuildAsync(second.Path, secondLoaded);
        Assert.True(MediaLibrarySnapshot.SameContent(secondCold, rebuilt));

        MediaLibraryFilter filter = new(nameContains: "png");
        MediaLibraryFilterResult expected = MediaLibraryFilterEngine.Evaluate(secondCold.Entries, filter, MediaLibraryUserData.Unavailable);
        MediaLibraryFilterResult loaded = MediaLibraryFilterEngine.Evaluate(secondLoaded.Entries, filter, MediaLibraryUserData.Unavailable);
        Assert.Equal(expected.Passing, loaded.Passing);
        Assert.Equal(expected.UnknownSoundVideos, loaded.UnknownSoundVideos);
        Assert.Equal(expected.TextTally, loaded.TextTally);
        Assert.Equal(MediaLibraryView.Select(expected.Passing, MediaLibrarySort.ByThread, includeMissing: false),
            MediaLibraryView.Select(loaded.Passing, MediaLibrarySort.ByThread, includeMissing: false));
    }

    private static string CachePath(string profile) => Path.Combine(profile, "cache", MediaLibraryIndexCache.FileName);

    private static async Task PackSmallAsync(string path, MediaLibrarySnapshot index)
    {
        MediaLibraryIndexCacheWrite plain = await MediaLibraryIndexCache.SaveAsync(path, index, Budget);
        File.Delete(path);
        Assert.Equal(MediaLibraryIndexCacheWriteStatus.Written, (await MediaLibraryIndexCache.SaveAsync(path, index, plain.Bytes - 1)).Status);
        Assert.False(File.Exists(path));
        Assert.True(File.Exists(path + ".gz"));
    }

    private static async Task StoreBothAsync(string path, MediaLibrarySnapshot plain, MediaLibrarySnapshot packed)
    {
        MediaLibraryIndexCacheFile store = new(path);
        Assert.Equal(MediaLibraryIndexCacheWriteStatus.Written, (await store.SaveAsync(plain, Budget)).Status);
        Assert.Equal(MediaLibraryIndexCacheWriteStatus.Written, (await store.SaveAsync(packed, 768 * 1024)).Status);
        await using FileStream file = File.OpenRead(path + ".gz");
        await using GZipStream gzip = new(file, CompressionMode.Decompress);
        Assert.Equal((int)'{', gzip.ReadByte());
    }
}
