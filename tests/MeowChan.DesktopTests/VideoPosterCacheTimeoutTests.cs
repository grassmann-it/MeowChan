using System.Security.Cryptography;
using System.Text.Json;
using MeowChan.Desktop;
using MeowChan.Storage;
using SkiaSharp;

namespace MeowChan.DesktopTests;

public sealed class VideoPosterCacheTimeoutTests
{
    [Fact]
    public async Task CallerCancellationRemainsObservableAndAllowsALaterRetry()
    {
        using TempDirectory temp = new();
        var (manifest, asset) = await ManifestAsync(temp.Path);
        string cache = Path.Combine(temp.Path, "cache"); Directory.CreateDirectory(cache);
        byte[] png = Png(80, 60);
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using CancellationTokenSource caller = new();
        VideoPosterCacheTestSeam canceledDecoder = new()
        {
            Decoder = async (verified, token) =>
            {
                await using (verified)
                {
                    started.TrySetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                    return png;
                }
            }
        };

        Task<byte[]?> request = VideoPosterCache.GetPngAsync(manifest, asset, cache, caller.Token,
            testSeam: canceledDecoder);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        caller.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);

        VideoPosterCacheTestSeam retryDecoder = SuccessfulDecoder(png);
        Assert.Equal(png, await VideoPosterCache.GetPngAsync(manifest, asset, cache, CancellationToken.None,
            testSeam: retryDecoder));
    }

    [Fact]
    public async Task InternalWatchdogReturnsMissingPosterAndAllowsALaterRetry()
    {
        using TempDirectory temp = new();
        var (manifest, asset) = await ManifestAsync(temp.Path);
        string cache = Path.Combine(temp.Path, "cache"); Directory.CreateDirectory(cache);
        byte[] png = Png(80, 60);
        using CancellationTokenSource caller = new();
        TaskCompletionSource<Task> waitStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        VideoPosterCacheTestSeam timedOut = new()
        {
            BoundedTimeout = TimeSpan.FromMilliseconds(50),
            NativeGateWaitStarted = wait => waitStarted.TrySetResult(wait),
            Decoder = (_, _) => throw new InvalidOperationException("The watchdog must prevent decoder admission.")
        };

        SemaphoreSlim gate = VideoPosterCache.NativeDecodeGate;
        await gate.WaitAsync();
        bool released = false;
        try
        {
            Task<byte[]?> request = VideoPosterCache.GetPngAsync(manifest, asset, cache, caller.Token,
                testSeam: timedOut);
            await waitStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Null(await request.WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.False(caller.IsCancellationRequested);
            gate.Release(); released = true;

            Assert.Equal(png, await VideoPosterCache.GetPngAsync(manifest, asset, cache, CancellationToken.None,
                testSeam: SuccessfulDecoder(png)));
        }
        finally { if (!released) gate.Release(); }
    }

    private static VideoPosterCacheTestSeam SuccessfulDecoder(byte[] png) => new()
    {
        Decoder = (verified, _) =>
        {
            verified.Dispose();
            return Task.FromResult(png);
        }
    };

    private static byte[] Png(int width, int height)
    {
        using SKBitmap bitmap = new(width, height);
        using SKCanvas canvas = new(bitmap); canvas.Clear(SKColors.Coral);
        using SKImage image = SKImage.FromBitmap(bitmap);
        using SKData data = image.Encode(SKEncodedImageFormat.Png, 90);
        return data.ToArray();
    }

    private static async Task<(MediaAssetSnapshot Manifest, MediaAssetReference Asset)> ManifestAsync(string directory)
    {
        byte[] bytes = { 1, 2, 3, 4 };
        await File.WriteAllBytesAsync(Path.Combine(directory, "clip.webm"), bytes);
        await File.WriteAllTextAsync(Path.Combine(directory, ".meowchan-manifest.json"), JsonSerializer.Serialize(new
        {
            Version = 1, Entries = new Dictionary<string, object> { ["clip.webm"] = new
            {
                MediaIdentifier = "video", SourceUrl = "https://offline.invalid/never-request.webm", FileName = "clip.webm",
                Length = bytes.Length, Sha256 = Convert.ToHexString(SHA256.HashData(bytes)), Md5 = Convert.ToBase64String(MD5.HashData(bytes))
            } }
        }));
        MediaAssetSnapshot manifest = await MediaAssetReader.OpenAsync(directory);
        return (manifest, manifest.GetPage().Single());
    }
}
