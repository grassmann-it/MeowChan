using System.Security.Cryptography;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using MeowChan.Desktop;
using MeowChan.Gallery;
using MeowChan.Storage;

namespace MeowChan.DesktopTests;

[Collection("Localization")]
public sealed class AnimatedWebpPlaybackTests
{
    [Fact]
    public async Task DecodesVariableDelaysAndTransparentCompositesInPlaybackAndSeekOrder()
    {
        using TempDirectory temp = new();
        using JsonDocument manifest = JsonDocument.Parse(await File.ReadAllTextAsync(Fixture("manifest.json")));
        JsonElement expected = manifest.RootElement;
        JsonElement[] frames = expected.GetProperty("frames").EnumerateArray().ToArray();
        GalleryEntry entry = await CopyAsync(temp, "animated.webp", "ANIMATED.WEBP");
        await using var decoder = await GifFrameDecoder.OpenAnimationAsync(entry, CancellationToken.None);
        Assert.NotNull(decoder);
        Assert.Equal("WEBP", decoder.Info.Format);
        Assert.False(decoder.Info.StillFrame);
        Assert.Equal(expected.GetProperty("width").GetInt32(), decoder.Info.Width);
        Assert.Equal(expected.GetProperty("height").GetInt32(), decoder.Info.Height);
        Assert.Equal(frames.Length, decoder.FrameCount);
        Assert.Equal(expected.GetProperty("total_passes").GetInt32() - 1, decoder.RepetitionCount);

        foreach (int index in new[] { 0, 1, 2, 0, 2, 1 })
        {
            using GifDecodedFrame frame = await decoder.DecodeAsync(index, CancellationToken.None);
            JsonElement golden = frames[index];
            int delay = golden.GetProperty("duration_ms").GetInt32();
            Assert.InRange(frame.DurationMs, delay - 10, delay + 10);
            Assert.Equal(frame.DurationMs, decoder.DurationMs(index));
            byte[] pixels = new byte[frame.Width * frame.Height * 4];
            for (int row = 0; row < frame.Height; row++)
                frame.Pixels.AsSpan(row * frame.RowBytes, frame.Width * 4).CopyTo(pixels.AsSpan(row * frame.Width * 4));
            Assert.Equal(golden.GetProperty("bgra_premul_sha256").GetString(), Hash(pixels));
        }
    }

    [Theory]
    [InlineData("still.webp", "still.webp")]
    [InlineData("animated.webp", "animated.png")]
    [InlineData("animated.webp", "animated.gif")]
    public async Task PlayerRequiresBothTheMatchingExtensionAndMultipleFrames(string fixture, string name)
    {
        using TempDirectory temp = new();
        GalleryEntry entry = await CopyAsync(temp, fixture, name);
        await using var decoder = await GifFrameDecoder.OpenAnimationAsync(entry, CancellationToken.None);
        Assert.Null(decoder);
    }

    [Fact]
    public void PreloaderDecodesAndKeepsTheStillWebpOnly() => BooruComponentDispatcher.Run(async () =>
    {
        using TempDirectory temp = new();
        GalleryEntry current = await CopyAsync(temp, "still.webp", "current.webp");
        GalleryEntry animated = await CopyAsync(temp, "animated.webp");
        GalleryEntry still = await CopyAsync(temp, "still.webp");
        GalleryImageCache<Bitmap> cache = new(bitmap => bitmap.PixelSize);
        List<GalleryEntry> requested = [];
        GalleryPreloader<Bitmap> preloader = new(cache, (entry, request, token) =>
        {
            requested.Add(entry);
            return GalleryImageDecoder.DecodeBackgroundAsync(entry, request, token);
        }, TimeSpan.Zero);
        try
        {
            GalleryDecodeRequest.Fit fit = new(48, 48);
            await preloader.Schedule([current, animated, still], 0, fit, ahead: 2, behind: 0);
            Assert.Equal(still, Assert.Single(requested));
            Assert.Equal(1, preloader.Decoded);
            Assert.True(cache.TryAcquire(GalleryImageCache.KeyOf(still)!, fit, out Bitmap bitmap, out GalleryImageInfo info));
            try { Assert.False(info.StillFrame); }
            finally { cache.Release(bitmap); }
        }
        finally { cache.Clear(); }
    });

    [Fact]
    public void ViewerReportsTruncationAndRecoversToFinitePlaybackThenAStillImage() =>
        BooruComponentDispatcher.Run(async () =>
        {
            using IDisposable platform = BooruReviewTestPlatform.Enter();
            using TempDirectory temp = new();
            GalleryEntry truncated = await CopyAsync(temp, "truncated.webp");
            GalleryEntry animated = await CopyAsync(temp, "animated.webp");
            GalleryEntry still = await CopyAsync(temp, "still.webp");
            await using GalleryViewer viewer = new();
            Window window = new() { Width = 840, Height = 680, Content = viewer };
            List<GalleryDiagnosticNotice> diagnostics = [];
            viewer.DiagnosticNotice += diagnostics.Add;
            try
            {
                window.Show();
                window.Activate();
                ContextGestures.Settle(window);
                Image image = SidePanelDesignTests.ById<Image>(viewer, "GalleryImage");
                viewer.SetInteractionAvailable(true);
                await viewer.ShowAsync(truncated, CancellationToken.None);
                Assert.Contains(diagnostics, notice => notice.MediaUnavailable && notice.Entry == truncated);
                Assert.Null(image.Source);
                Assert.Null(viewer.GifPlayback);
                using (File.Open(truncated.Asset.Path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }

                await viewer.ShowAsync(animated, CancellationToken.None);
                var player = Assert.IsType<GalleryGifPlayback>(viewer.GifPlayback);
                Assert.Same(player.Bitmap, image.Source);
                player.SetActive(true);
                await BooruTestSupport.WaitAsync(() => player.Evidence.Ended, "the WebP's two encoded passes");
                Assert.Equal(2, player.Evidence.CompletedPasses);
                Assert.Equal(2, player.Evidence.FrameIndex);
                Assert.Null(player.Evidence.Failure);

                await viewer.ShowAsync(still, CancellationToken.None);
                Assert.True(player.Evidence.Disposed);
                Assert.Null(viewer.GifPlayback);
                Assert.NotNull(image.Source);
                Assert.NotSame(player.Bitmap, image.Source);
            }
            finally { window.Close(); }
        });

    [Theory]
    [InlineData("oversized-canvas.webp", "16 million pixel")]
    [InlineData("too-many-frames.webp", "1024 frame")]
    public async Task RejectsWebpThatExceedsTheCanvasOrFrameLimit(string fixture, string limit)
    {
        using TempDirectory temp = new();
        GalleryEntry entry = await CopyAsync(temp, fixture);
        var error = await Assert.ThrowsAsync<InvalidDataException>(
            () => GifFrameDecoder.OpenAnimationAsync(entry, CancellationToken.None));
        Assert.Contains(limit, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RejectsWebpAboveTheSourceLimitBeforeReadingTheAsset()
    {
        using TempDirectory temp = new();
        GalleryEntry entry = await CopyAsync(temp, "animated.webp");
        File.Delete(entry.Asset.Path);
        entry = entry with { Asset = entry.Asset with { Length = GifFrameDecoder.MaximumBytes + 1 } };
        var error = await Assert.ThrowsAsync<InvalidDataException>(
            () => GifFrameDecoder.OpenAnimationAsync(entry, CancellationToken.None));
        Assert.Contains("64 MiB", error.Message, StringComparison.Ordinal);
    }

    private static async Task<GalleryEntry> CopyAsync(TempDirectory temp, string fixture, string? name = null)
    {
        name ??= fixture;
        byte[] bytes = await File.ReadAllBytesAsync(Fixture(fixture));
        string path = Path.Combine(temp.Path, name);
        await File.WriteAllBytesAsync(path, bytes);
        MediaAssetReference asset = new(name, name, "original", name, path, bytes.Length, Hash(bytes), "", name, true);
        MediaAssetSnapshot snapshot = new(temp.Path, MediaManifestStatus.Available, 1, "fixture", [asset]);
        return new(new(new("fixture", "b", "webp"), temp.Path, name), snapshot, asset, null, null, false, false);
    }

    private static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static string Fixture(string name)
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
        {
            string file = Path.Combine(directory.FullName, "tests", "Fixtures", "webp-playback", name);
            if (File.Exists(file)) return file;
        }
        throw new FileNotFoundException("WebP playback fixture was not found.", name);
    }
}
