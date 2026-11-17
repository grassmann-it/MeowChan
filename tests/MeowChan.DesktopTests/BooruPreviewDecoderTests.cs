using Avalonia;
using MeowChan.Desktop;
using MeowChan.Desktop.Booru;
using SkiaSharp;

namespace MeowChan.DesktopTests;

public sealed class BooruPreviewDecoderTests
{
    [Theory]
    [InlineData(500, 800, 500, 500, 313, 500)]
    [InlineData(800, 500, 500, 500, 500, 313)]
    [InlineData(96, 96, 500, 500, 96, 96)]
    [InlineData(500, 800, 10000, 10000, 500, 800)]
    public async Task FitUsesBoundedAspectPreservingTarget(int width, int height, int fitWidth, int fitHeight,
        int expectedWidth, int expectedHeight)
    {
        PixelSize target = default;
        BooruPreviewDecoder decoder = new((_, size) => { target = size; throw new ProbeComplete(); });
        await Assert.ThrowsAsync<ProbeComplete>(() => decoder.DecodeAsync(Png(width, height),
            new GalleryDecodeRequest.Fit(fitWidth, fitHeight), CancellationToken.None));
        Assert.Equal(new PixelSize(expectedWidth, expectedHeight), target);
    }

    [Fact]
    public async Task CodecRunsOffTheCallingThread()
    {
        byte[] bytes = Png(96, 96);
        int caller = Environment.CurrentManagedThreadId, worker = caller;
        using ManualResetEventSlim started = new();
        BooruPreviewDecoder decoder = new((_, _) =>
        {
            worker = Environment.CurrentManagedThreadId;
            started.Set();
            throw new ProbeComplete();
        });
        Task pending = decoder.DecodeAsync(bytes, new GalleryDecodeRequest.Fit(50, 50), CancellationToken.None);
        Assert.True(started.Wait(TimeSpan.FromSeconds(10)));
        await Assert.ThrowsAsync<ProbeComplete>(() => pending);
        Assert.NotEqual(caller, worker);
    }

    [Fact]
    public async Task AlreadyCanceledRequestDoesNotEnterCodec()
    {
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        BooruPreviewDecoder decoder = new((_, _) => throw new ProbeComplete());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => decoder.DecodeAsync(Png(96, 96),
            new GalleryDecodeRequest.Fit(50, 50), cancellation.Token));
    }

    private sealed class ProbeComplete : Exception;

    private static byte[] Png(int width, int height)
    {
        using SKBitmap bitmap = new(width, height);
        bitmap.Erase(SKColors.Blue);
        using SKImage image = SKImage.FromBitmap(bitmap);
        using SKData data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }
}
