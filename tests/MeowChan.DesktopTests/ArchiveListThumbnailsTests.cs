using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using MeowChan.Desktop;
using MeowChan.Gallery;
using MeowChan.Storage;
using SkiaSharp;

namespace MeowChan.DesktopTests;

public sealed class ArchiveListThumbnailsTests
{
    [Theory]
    [InlineData("webm")]
    [InlineData("mp4")]
    public async Task ListVideoUsesTheVerifiedReaderPosterWithoutStartingANativeDecoder(string extension)
    {
        using TempDirectory temp = new();
        var (row, cache, png) = await FixtureAsync(temp.Path, extension);
        Assert.Equal(SavedMediaKind.Video, ArchiveListThumbnails.Kind(row));
        Assert.EndsWith("@poster160x120", ArchiveListThumbnails.CacheKey(row));
        Assert.Equal(png, await ArchiveListThumbnails.PosterPngAsync(row, cache, CancellationToken.None));
        MediaAssetSnapshot restarted = await MediaAssetReader.OpenAsync(row.Manifest.Directory);
        Assert.Equal(png, await ArchiveListThumbnails.PosterPngAsync(new(restarted, restarted.GetPage().Single()), cache, CancellationToken.None));
        // The fixture original deliberately is not an encoded video. This can only succeed via the existing cached PNG.
        Assert.Single(Directory.GetFiles(cache));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task APreviouslyLoadedPosterDoesNotHideAChangedOrMissingOriginal(bool remove)
    {
        using TempDirectory temp = new();
        var (row, cache, png) = await FixtureAsync(temp.Path, "webm");
        Assert.Equal(png, await ArchiveListThumbnails.PosterPngAsync(row, cache, CancellationToken.None));
        if (remove) File.Delete(row.Asset.Path);
        else await File.WriteAllBytesAsync(row.Asset.Path, new byte[] { 8, 7, 6, 5 });
        Assert.Null(await ArchiveListThumbnails.PosterPngAsync(row, cache, CancellationToken.None));
        // Derived data may remain for another identical original; it must not be served as this row's preview.
        Assert.Equal(png, await File.ReadAllBytesAsync(Path.Combine(cache, row.Asset.Sha256.ToLowerInvariant() + ".png")));
    }

    [Fact]
    public async Task CancellationDoesNotInvalidateAnExistingPoster()
    {
        using TempDirectory temp = new();
        var (row, cache, png) = await FixtureAsync(temp.Path, "mp4");
        using CancellationTokenSource stopped = new(); stopped.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ArchiveListThumbnails.PosterPngAsync(row, cache, stopped.Token));
        Assert.Equal(png, await ArchiveListThumbnails.PosterPngAsync(row, cache, CancellationToken.None));
    }

    [Theory]
    [InlineData("png", "@160x120")]
    [InlineData("jpg", "@160x120")]
    [InlineData("webm", "@poster160x120")]
    [InlineData("mp4", "@poster160x120")]
    [InlineData("txt", null)]
    public async Task ImageAndVideoKeysKeepTheirSeparateSmallPreviewRepresentations(string extension, string? suffix)
    {
        using TempDirectory temp = new();
        var (row, _, _) = await FixtureAsync(temp.Path, extension);
        if (suffix == null) Assert.Null(ArchiveListThumbnails.CacheKey(row));
        else Assert.EndsWith(suffix, ArchiveListThumbnails.CacheKey(row));
    }

    [Fact]
    public void ListWiringUsesTheSharedLoaderAndVerifiesBeforeAcquiringAMemoryPoster()
    {
        string root = RepositoryRoot();
        string page = File.ReadAllText(Path.Combine(root, "Desktop/Views/Archive/ArchivePage.axaml.cs"));
        string load = page[page.IndexOf("private Task<Bitmap?> LoadListThumbnailAsync", StringComparison.Ordinal)..];
        load = load[..load.IndexOf("internal static ArchiveAssetRow? FindMatchingAsset", StringComparison.Ordinal)];
        Assert.Contains("ArchiveListThumbnails.LoadAsync", load);
        Assert.Contains("MediaAttachmentResolver.Resolve", load);
        // The gate is handed to the loader, which takes a slot only for a real decode (a memory hit never waits).
        Assert.Contains("loading.Token, _thumbnailGate)", load);
        Assert.DoesNotContain("_thumbnailGate.WaitAsync", load);
        Assert.DoesNotContain("!= SavedMediaKind.Image", load);
        string loader = File.ReadAllText(Path.Combine(root, "Desktop/ArchiveListThumbnails.cs"));
        Assert.Contains("MediaTileThumbnails.LoadPosterAsync", loader);
        string reader = page[page.IndexOf("private ArchiveAttachmentRow CreateAttachmentRow", StringComparison.Ordinal)..];
        reader = reader[..reader.IndexOf("private ArchiveAttachmentRow CreateListAttachmentRow", StringComparison.Ordinal)];
        Assert.Contains("MediaTileThumbnails.LoadPosterAsync", reader);
        Assert.DoesNotContain("VideoPosterCache.LoadAsync", reader);
        Assert.True(reader.IndexOf("MediaTileThumbnails.LoadPosterAsync", StringComparison.Ordinal)
            < reader.IndexOf("MediaTileThumbnails.KeepGatedAsync", StringComparison.Ordinal));
        Assert.DoesNotContain("_thumbnailGate.WaitAsync", reader); // the cache lookup precedes the gate; only decodes take a slot
        Assert.DoesNotContain("MediaTileThumbnails.KeepAsync(", reader);
        string tiles = File.ReadAllText(Path.Combine(root, "Desktop/ArchiveMediaTileView.cs"));
        Assert.Contains("MediaTileThumbnails.LoadPosterAsync", tiles);
        Assert.Contains("ReferenceEquals(decode, DefaultThumbnailDecoder) && !UsesVideoPoster(row)", tiles);
        string media = File.ReadAllText(Path.Combine(root, "Desktop/Views/Media/MediaPage.cs"));
        Assert.Contains("MediaTileThumbnails.LoadAsync(tile, OpenManifestAsync, PosterCacheDirectory, token)", media);
        Assert.DoesNotContain("MediaTileThumbnails.Remember", media); // one lease, not a second Add of the same bitmap
        string shared = File.ReadAllText(Path.Combine(root, "Desktop/MediaTileThumbnails.cs"));
        Assert.Contains("tile.Entry.Kind == SavedMediaKind.Image &&", shared); // no synchronous poster shortcut
        Assert.Contains("LoadLibraryPosterAsync", shared);
        Assert.Contains("cancellation, Verified)", shared); // changed identity rehashes, unchanged originals do not

    }

    private sealed class Preview : IDisposable
    {
        internal int Disposals;
        public void Dispose() => Disposals++;
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("changed-same-length-and-time")]
    [InlineData("replaced-same-length-and-time")]
    public async Task SharedPosterLoaderChecksTheOriginalBeforeLeasingAWarmMemoryPreview(string mutation)
    {
        using TempDirectory temp = new();
        var (row, directory, _) = await FixtureAsync(temp.Path, "webm");
        MediaThumbnailCache<Preview> cache = new(1);
        string key = MediaTileThumbnails.PosterKey(row.Asset, 340, 250)!;
        int decodes = 0;
        Task<Preview?> Load() => MediaTileThumbnails.KeepVerifiedPosterAsync(cache, key,
            token => ArchiveListThumbnails.PosterPngAsync(row, directory, token),
            (_, _) => { decodes++; return Task.FromResult<Preview?>(new()); }, CancellationToken.None);
        Preview first = Assert.IsType<Preview>(await Load());
        cache.Release(first);
        Preview warm = Assert.IsType<Preview>(await Load());
        Assert.Same(first, warm); Assert.Equal(1, decodes);
        cache.Release(warm);
        DateTime stamp = File.GetLastWriteTimeUtc(row.Asset.Path);
        switch (mutation)
        {
            case "missing": File.Delete(row.Asset.Path); break;
            case "changed-same-length-and-time":
                await File.WriteAllBytesAsync(row.Asset.Path, new byte[] { 4, 3, 2, 1 });
                File.SetLastWriteTimeUtc(row.Asset.Path, stamp); break;
            default:
                string replacement = row.Asset.Path + ".replacement";
                await File.WriteAllBytesAsync(replacement, new byte[] { 4, 3, 2, 1 });
                File.SetLastWriteTimeUtc(replacement, stamp);
                File.Move(replacement, row.Asset.Path, overwrite: true); break;
        }
        Assert.Null(await Load());
        Assert.Equal(1, decodes);
        Assert.Equal(0, first.Disposals); // kept for other, still valid occurrences of those bytes
        Preview other = cache.Add("another-video", new Preview());
        Assert.Equal(1, first.Disposals); // failed verification did not acquire/leak an invisible lease
        cache.Release(other);
    }

    [Fact]
    public async Task AWarmPosterOfAnotherOccurrenceCannotAuthorizeAnUnverifiedOriginal()
    {
        using TempDirectory temp = new();
        var (good, directory, _) = await FixtureAsync(Path.Combine(temp.Path, "good"), "webm");
        var (bad, badDirectory, _) = await FixtureAsync(Path.Combine(temp.Path, "bad"), "webm");
        MediaThumbnailCache<Preview> cache = new(1);
        string key = MediaTileThumbnails.PosterKey(good.Asset, 160, 120)!;
        int decodes = 0;
        Task<Preview?> Load(ArchiveAssetRow row, string cacheDirectory) => MediaTileThumbnails.KeepVerifiedPosterAsync(cache, key,
            token => ArchiveListThumbnails.PosterPngAsync(row, cacheDirectory, token),
            (_, _) => { decodes++; return Task.FromResult<Preview?>(new()); }, CancellationToken.None);
        Preview valid = Assert.IsType<Preview>(await Load(good, directory));
        cache.Release(valid);
        File.Delete(bad.Asset.Path);
        Assert.Null(await Load(bad, badDirectory));
        Assert.Same(valid, await Load(good, directory));
        Assert.Equal(1, decodes);
        cache.Release(valid);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CanceledVerificationNeverLeasesOrDecodesAWarmPoster(bool canceledBeforeStart)
    {
        MediaThumbnailCache<Preview> cache = new(1);
        Preview warm = cache.Add("video", new()); cache.Release(warm);
        using CancellationTokenSource canceled = new();
        if (canceledBeforeStart) canceled.Cancel();
        int checks = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => MediaTileThumbnails.KeepVerifiedPosterAsync(cache, "video", _ =>
        {
            checks++; canceled.Cancel(); return Task.FromResult<byte[]?>([1]); // models a producer that returns despite cancellation
        }, (_, _) => throw new InvalidOperationException("A canceled view must not decode."), canceled.Token));
        Assert.Equal(canceledBeforeStart ? 0 : 1, checks);
        Preview other = cache.Add("other", new());
        Assert.Equal(1, warm.Disposals);
        cache.Release(other);
    }

    [Fact]
    public async Task CancellationDuringDecodeDisposesTheLatePreviewWithoutCachingIt()
    {
        MediaThumbnailCache<Preview> cache = new(1);
        using CancellationTokenSource canceled = new();
        Preview late = new();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => MediaTileThumbnails.KeepVerifiedPosterAsync(cache, "video",
            _ => Task.FromResult<byte[]?>([1]), (_, _) =>
            {
                canceled.Cancel(); return Task.FromResult<Preview?>(late);
            }, canceled.Token));
        Assert.Equal(1, late.Disposals);
        Assert.Equal(0, cache.Count);
        Assert.Null(cache.TryAcquire("video"));
    }

    [Fact]
    public async Task LargeUncachedPosterStillVerifiesAndRemainsOwnedByItsView()
    {
        MediaThumbnailCache<Preview> cache = new(1);
        Preview preview = new(); int checks = 0;
        Assert.Same(preview, await MediaTileThumbnails.KeepVerifiedPosterAsync(cache, null,
            _ => { checks++; return Task.FromResult<byte[]?>([1]); },
            (_, _) => Task.FromResult<Preview?>(preview), CancellationToken.None));
        Assert.Equal(1, checks); Assert.Equal(0, cache.Count);
        cache.Release(preview); Assert.Equal(1, preview.Disposals);
    }

    private static string RepositoryRoot([CallerFilePath] string path = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, "../.."));

    private static async Task<(ArchiveAssetRow Row, string Cache, byte[] Png)> FixtureAsync(string root, string extension)
    {
        string directory = Path.Combine(root, "media"); Directory.CreateDirectory(directory);
        byte[] original = [1, 2, 3, 4];
        string name = "clip." + extension;
        await File.WriteAllBytesAsync(Path.Combine(directory, name), original);
        await File.WriteAllTextAsync(Path.Combine(directory, ".meowchan-manifest.json"), JsonSerializer.Serialize(new
        {
            Version = 1, Entries = new Dictionary<string, object> { [name] = new
            {
                MediaIdentifier = "preview", SourceUrl = "https://offline.invalid/never-request." + extension, FileName = name,
                Length = original.Length, Sha256 = Convert.ToHexString(SHA256.HashData(original)),
                Md5 = Convert.ToBase64String(MD5.HashData(original))
            } }
        }));
        MediaAssetSnapshot manifest = await MediaAssetReader.OpenAsync(directory);
        MediaAssetReference asset = Assert.Single(manifest.GetPage());
        string cache = Path.Combine(root, "posters"); Directory.CreateDirectory(cache);
        using SKBitmap bitmap = new(160, 90);
        using SKCanvas canvas = new(bitmap); canvas.Clear(SKColors.Coral);
        using SKImage image = SKImage.FromBitmap(bitmap);
        using SKData data = image.Encode(SKEncodedImageFormat.Png, 90);
        byte[] png = data.ToArray();
        await File.WriteAllBytesAsync(Path.Combine(cache, asset.Sha256.ToLowerInvariant() + ".png"), png);
        return (new(manifest, asset), cache, png);
    }
}
