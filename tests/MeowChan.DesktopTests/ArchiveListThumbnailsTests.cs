using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using MeowChan.Desktop;
using MeowChan.Gallery;
using MeowChan.Storage;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
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
        var page = Source("Views/Archive/ArchivePage.axaml.cs");
        var list = page.Method("LoadListThumbnailAsync");
        Assert.Single(list.Calls(nameof(ArchivePage.SelectListPreview)));
        Assert.Single(page.Method(nameof(ArchivePage.SelectListPreview)).Calls("Resolve", "MediaAttachmentResolver"));
        var loadList = Assert.Single(list.Calls(nameof(ArchiveListThumbnails.LoadAsync), nameof(ArchiveListThumbnails)));
        Assert.Contains(loadList.ArgumentList.Arguments, argument => argument.Expression is IdentifierNameSyntax { Identifier.ValueText: "_thumbnailGate" });
        Assert.Empty(list.Calls("WaitAsync", "_thumbnailGate"));
        Assert.Single(Source("ArchiveListThumbnails.cs").Method(nameof(ArchiveListThumbnails.LoadAsync))
            .Calls(nameof(MediaTileThumbnails.LoadPosterAsync), nameof(MediaTileThumbnails)));

        var reader = page.Method("CreateAttachmentRow");
        var readerPoster = Assert.Single(reader.Calls(nameof(MediaTileThumbnails.LoadPosterAsync), nameof(MediaTileThumbnails)));
        var returnPoster = Assert.IsType<ReturnStatementSyntax>(readerPoster.FirstAncestorOrSelf<ReturnStatementSyntax>());
        Assert.Contains(returnPoster.Ancestors().OfType<IfStatementSyntax>(), branch =>
            branch.Condition is IdentifierNameSyntax { Identifier.ValueText: "video" } && branch.Statement.Span.Contains(returnPoster.Span));
        Assert.Single(reader.Calls(nameof(MediaTileThumbnails.KeepGatedAsync), nameof(MediaTileThumbnails)));
        Assert.Empty(reader.Calls("WaitAsync", "_thumbnailGate"));
        Assert.Empty(reader.Calls(nameof(MediaTileThumbnails.KeepAsync), nameof(MediaTileThumbnails)));
        Assert.Empty(reader.Calls("LoadAsync", nameof(VideoPosterCache)));

        var tiles = Source("ArchiveMediaTileView.cs");
        Assert.Single(tiles.Method(nameof(ArchiveMediaTileView.DecodeAsync))
            .Calls(nameof(MediaTileThumbnails.LoadPosterAsync), nameof(MediaTileThumbnails)));
        var tileCacheKey = Assert.Single(tiles.Method("LoadAsync").DescendantNodes().OfType<ConditionalExpressionSyntax>(),
            conditional => conditional.WhenTrue.Calls(nameof(MediaTileThumbnails.ByteKey), nameof(MediaTileThumbnails)).Any());
        Assert.Contains(tileCacheKey.Condition.DescendantNodes().OfType<PrefixUnaryExpressionSyntax>(),
            guard => guard.IsKind(SyntaxKind.LogicalNotExpression)
                && guard.Operand is InvocationExpressionSyntax { Expression: IdentifierNameSyntax { Identifier.ValueText: "UsesVideoPoster" } });
        Assert.True(tileCacheKey.Condition.IsKind(SyntaxKind.LogicalAndExpression));
        var decoderCheck = Assert.Single(tileCacheKey.Condition.Calls("ReferenceEquals"));
        Assert.Contains(decoderCheck.ArgumentList.Arguments,
            argument => argument.Expression is IdentifierNameSyntax { Identifier.ValueText: "DefaultThumbnailDecoder" });

        var media = Source("Views/Media/MediaPage.cs").Method("LoadThumbnailAsync");
        Assert.Single(media.Calls(nameof(MediaTileThumbnails.LoadAsync), nameof(MediaTileThumbnails)));
        var shared = Source("MediaTileThumbnails.cs");
        var posterLoader = shared.Method(nameof(MediaTileThumbnails.LoadPosterAsync));
        Assert.Single(posterLoader.Calls(nameof(MediaTileThumbnails.KeepVerifiedPosterAsync)));
        AssertUsesVerifiedMemo(Assert.Single(posterLoader.Calls("GetPngAsync", nameof(VideoPosterCache))));
        Assert.Single(shared.Method("LoadLibraryPosterAsync").Calls(nameof(MediaTileThumbnails.KeepVerifiedPosterAsync)));
        AssertUsesVerifiedMemo(Assert.Single(shared.Method(nameof(MediaTileThumbnails.PosterPngAsync)).Calls("GetPngAsync", nameof(VideoPosterCache))));
    }

    private static void AssertUsesVerifiedMemo(InvocationExpressionSyntax poster) =>
        Assert.Contains(poster.ArgumentList.Arguments,
            argument => argument.Expression is IdentifierNameSyntax { Identifier.ValueText: nameof(MediaTileThumbnails.Verified) });

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

    private static SyntaxNode Source(string file) =>
        CSharpSource.Parse(File.ReadAllText(Path.Combine(RepositoryRoot(), "Desktop", file)));

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
