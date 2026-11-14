using System.Security.Cryptography;
using System.Text.Json;
using MeowChan.Desktop;
using MeowChan.Storage;
using SkiaSharp;

namespace MeowChan.DesktopTests;

public sealed class VideoPosterCacheTests
{
    [Fact]
    public async Task PersistedPosterWorksWithNoDecoderAndRequiresTheOriginalChecksumOnEveryRead()
    {
        using TempDirectory temp = new();
        string cache = Path.Combine(temp.Path, "cache"); Directory.CreateDirectory(cache);
        var (manifest, asset) = await ManifestAsync(temp.Path);
        byte[] png = Png(160, 90);
        string path = Path.Combine(cache, asset.Sha256.ToLowerInvariant() + ".png");
        await File.WriteAllBytesAsync(path, png);
        DateTime timestamp = File.GetLastWriteTimeUtc(path);
        Assert.Equal(png, await VideoPosterCache.GetPngAsync(manifest, asset, cache, CancellationToken.None));
        // A fresh immutable manifest simulates a restarted reader. Fixture bytes aren't a video;
        // a cache miss would fail decoding, so this also proves cache hits don't create a player.
        MediaAssetSnapshot reopened = await MediaAssetReader.OpenAsync(temp.Path);
        Assert.Equal(png, await VideoPosterCache.GetPngAsync(reopened, reopened.GetPage().Single(), cache, CancellationToken.None));
        Assert.Equal(timestamp, File.GetLastWriteTimeUtc(path));
        await File.WriteAllBytesAsync(asset.Path, new byte[] { 5, 6, 7, 8 });
        Assert.Null(await VideoPosterCache.GetPngAsync(manifest, asset, cache, CancellationToken.None));
        Assert.Equal(png, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task WarmVerifiedPosterDoesNotWaitForTheNativeGate()
    {
        using TempDirectory temp = new();
        var (manifest, asset) = await ManifestAsync(temp.Path);
        string cache = Path.Combine(temp.Path, "cache"); Directory.CreateDirectory(cache);
        byte[] png = Png(80, 60);
        string cached = Path.Combine(cache, asset.Sha256.ToLowerInvariant() + ".png");
        await File.WriteAllBytesAsync(cached, png);
        int decodeCalls = 0;
        VideoPosterCacheTestSeam seam = new()
        {
            Decoder = (_, _) =>
            {
                Interlocked.Increment(ref decodeCalls);
                throw new InvalidOperationException("A warm hit must not enter the decoder.");
            }
        };

        SemaphoreSlim gate = VideoPosterCache.NativeDecodeGate;
        await gate.WaitAsync();
        try
        {
            byte[]? result = await VideoPosterCache.GetPngAsync(manifest, asset, cache, CancellationToken.None,
                testSeam: seam).WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(png, result);
            Assert.Equal(0, decodeCalls);
        }
        finally { gate.Release(); }
    }

    [Fact]
    public async Task CancellationWhileWaitingForTheNativeGateDoesNotStartDecoderWork()
    {
        using TempDirectory temp = new();
        var (manifest, asset) = await ManifestAsync(temp.Path);
        string cache = Path.Combine(temp.Path, "cache"); Directory.CreateDirectory(cache);
        int decodeCalls = 0;
        TaskCompletionSource<Task> waitStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        VideoPosterCacheTestSeam seam = new()
        {
            NativeGateWaitStarted = wait => waitStarted.TrySetResult(wait),
            Decoder = async (verified, token) =>
            {
                await using (verified)
                {
                    Interlocked.Increment(ref decodeCalls);
                    token.ThrowIfCancellationRequested();
                    return Png(80, 60);
                }
            }
        };

        SemaphoreSlim gate = VideoPosterCache.NativeDecodeGate;
        await gate.WaitAsync();
        try
        {
            using CancellationTokenSource canceled = new();
            Task<byte[]?> request = VideoPosterCache.GetPngAsync(manifest, asset, cache, canceled.Token,
                testSeam: seam);
            Task gateWait = await waitStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(gateWait.IsCompleted);
            canceled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await gateWait);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
            Assert.Equal(0, decodeCalls);
        }
        finally { gate.Release(); }
    }

    [Fact]
    public async Task OriginalChangedWhileWaitingCannotAuthorizeAStalePoster()
    {
        using TempDirectory temp = new();
        var (manifest, asset) = await ManifestAsync(temp.Path);
        string cache = Path.Combine(temp.Path, "cache"); Directory.CreateDirectory(cache);
        string cached = Path.Combine(cache, asset.Sha256.ToLowerInvariant() + ".png");
        byte[] png = Png(80, 60);
        int decodeCalls = 0;
        TaskCompletionSource<Task> waitStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        VideoPosterCacheTestSeam seam = new()
        {
            NativeGateWaitStarted = wait => waitStarted.TrySetResult(wait),
            Decoder = async (verified, token) =>
            {
                await using (verified)
                {
                    Interlocked.Increment(ref decodeCalls);
                    token.ThrowIfCancellationRequested();
                    return png;
                }
            }
        };

        SemaphoreSlim gate = VideoPosterCache.NativeDecodeGate;
        await gate.WaitAsync();
        bool released = false;
        try
        {
            Task<byte[]?> request = VideoPosterCache.GetPngAsync(manifest, asset, cache, CancellationToken.None,
                testSeam: seam);
            Task gateWait = await waitStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(gateWait.IsCompleted);
            // The cache file appears while the request is actually waiting. It is stale once the recorded original changes.
            await File.WriteAllBytesAsync(cached, png);
            string replacement = asset.Path + ".replacement";
            await File.WriteAllBytesAsync(replacement, new byte[] { 9, 8, 7, 6 });
            File.Move(replacement, asset.Path, overwrite: true);
            gate.Release(); released = true;
            Assert.Null(await request.WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.Equal(0, decodeCalls);
            Assert.Equal(png, await File.ReadAllBytesAsync(cached));
        }
        finally { if (!released) gate.Release(); }
    }

    [Fact]
    public async Task ColdPosterStillUsesTheDecoderAndPublishesTheVerifiedResult()
    {
        using TempDirectory temp = new();
        var (manifest, asset) = await ManifestAsync(temp.Path);
        string cache = Path.Combine(temp.Path, "cache"); Directory.CreateDirectory(cache);
        byte[] png = Png(80, 60);
        int decodeCalls = 0;
        VideoPosterCacheTestSeam seam = new()
        {
            Decoder = async (verified, token) =>
            {
                await using (verified)
                {
                    Interlocked.Increment(ref decodeCalls);
                    token.ThrowIfCancellationRequested();
                    return png;
                }
            }
        };

        Assert.Equal(png, await VideoPosterCache.GetPngAsync(manifest, asset, cache, CancellationToken.None,
            testSeam: seam));
        Assert.Equal(1, decodeCalls);
        Assert.Equal(png, await File.ReadAllBytesAsync(Path.Combine(cache, asset.Sha256.ToLowerInvariant() + ".png")));
    }

    [Fact]
    public async Task SynchronousDecoderThrowDisposesTheVerifiedStreamAndReleasesTheNativeGate()
    {
        using TempDirectory temp = new();
        var (manifest, asset) = await ManifestAsync(temp.Path);
        string cache = Path.Combine(temp.Path, "cache"); Directory.CreateDirectory(cache);
        Stream? observed = null;
        TaskCompletionSource<Task> waitStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        VideoPosterCacheTestSeam seam = new()
        {
            NativeGateWaitStarted = wait => waitStarted.TrySetResult(wait),
            Decoder = (verified, _) =>
            {
                observed = verified;
                throw new InvalidOperationException("synchronous decoder failure");
            }
        };

        SemaphoreSlim gate = VideoPosterCache.NativeDecodeGate;
        await gate.WaitAsync();
        bool released = false;
        try
        {
            Task<byte[]?> request = VideoPosterCache.GetPngAsync(manifest, asset, cache, CancellationToken.None,
                testSeam: seam);
            await waitStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            gate.Release(); released = true;
            Assert.Null(await request.WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.NotNull(observed);
            Assert.Throws<ObjectDisposedException>(() => observed!.ReadByte());

            bool reacquired = await gate.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(reacquired);
            if (reacquired) gate.Release();
        }
        finally { if (!released) gate.Release(); }
    }

    [Fact]
    public async Task DecoderCancellationDoesNotCreateANegativeCacheEntry()
    {
        using TempDirectory temp = new();
        var (manifest, asset) = await ManifestAsync(temp.Path);
        string cache = Path.Combine(temp.Path, "cache"); Directory.CreateDirectory(cache);
        using CancellationTokenSource decoderCanceled = new();
        decoderCanceled.Cancel();
        VideoPosterCacheTestSeam canceledDecoder = new()
        {
            Decoder = (verified, _) =>
            {
                verified.Dispose();
                return Task.FromCanceled<byte[]>(decoderCanceled.Token);
            }
        };

        Assert.Null(await VideoPosterCache.GetPngAsync(
            manifest, asset, cache, CancellationToken.None, testSeam: canceledDecoder));

        byte[] png = Png(80, 60);
        VideoPosterCacheTestSeam retryDecoder = new()
        {
            Decoder = (verified, _) =>
            {
                verified.Dispose();
                return Task.FromResult(png);
            }
        };
        Assert.Equal(png, await VideoPosterCache.GetPngAsync(manifest, asset, cache, CancellationToken.None,
            testSeam: retryDecoder));
    }

    [Fact]
    public async Task CancellationAfterDecodeDoesNotCreateANegativeCacheEntry()
    {
        using TempDirectory temp = new();
        var (manifest, asset) = await ManifestAsync(temp.Path);
        string cache = Path.Combine(temp.Path, "cache"); Directory.CreateDirectory(cache);
        byte[] png = Png(80, 60);
        using CancellationTokenSource canceled = new();
        VideoPosterCacheTestSeam cancelAfterDecode = new()
        {
            Decoder = (verified, _) =>
            {
                verified.Dispose();
                canceled.Cancel();
                return Task.FromResult(png);
            }
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => VideoPosterCache.GetPngAsync(
            manifest, asset, cache, canceled.Token, testSeam: cancelAfterDecode));

        VideoPosterCacheTestSeam retryDecoder = new()
        {
            Decoder = (verified, _) =>
            {
                verified.Dispose();
                return Task.FromResult(png);
            }
        };
        Assert.Equal(png, await VideoPosterCache.GetPngAsync(manifest, asset, cache, CancellationToken.None,
            testSeam: retryDecoder));
    }

    [Fact]
    public async Task CancellationDoesNotPoisonPersistentCacheOrAdmitDecoderWork()
    {
        using TempDirectory temp = new();
        var (manifest, asset) = await ManifestAsync(temp.Path);
        string cache = Path.Combine(temp.Path, "cache"); Directory.CreateDirectory(cache);
        byte[] png = Png(80, 60);
        await File.WriteAllBytesAsync(Path.Combine(cache, asset.Sha256.ToLowerInvariant() + ".png"), png);
        using CancellationTokenSource canceled = new(); canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => VideoPosterCache.GetPngAsync(manifest, asset, cache, canceled.Token));
        Assert.Equal(png, await VideoPosterCache.GetPngAsync(manifest, asset, cache, CancellationToken.None));
        Assert.Single(Directory.GetFiles(cache));
    }

    [Fact]
    public void PngValidationAndTotalCacheEvictionAreBounded()
    {
        Assert.True(VideoPosterCache.Valid(Png(960, 640)));
        Assert.False(VideoPosterCache.Valid(Png(961, 640)));
        Assert.False(VideoPosterCache.Valid(new byte[] { 1, 2, 3 }));
        Assert.False(VideoPosterCache.Valid(new byte[VideoPosterCache.MaximumPngBytes + 1]));
        using TempDirectory temp = new();
        for (int index = 0; index < 2100; index++)
        {
            string path = Path.Combine(temp.Path, index.ToString("D4") + ".png");
            using var stream = File.Create(path); stream.SetLength(600_000);
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(index - 3000));
        }
        VideoPosterCache.Trim(temp.Path, VideoPosterCache.MaximumPngBytes);
        var files = Directory.GetFiles(temp.Path).Select(path => new FileInfo(path)).ToArray();
        Assert.True(files.Length < VideoPosterCache.DefaultCacheFiles);
        Assert.True(files.Sum(file => file.Length) + VideoPosterCache.MaximumPngBytes <= VideoPosterCache.DefaultCacheBytes);
        Assert.False(File.Exists(Path.Combine(temp.Path, "0000.png")));
    }

    [Theory]
    [InlineData(20, 5, 1_000_000_000L, 5)]
    [InlineData(20, 100, 2_500_000L, 4)]
    public void ConfiguredFileAndByteLimitsDecideHowManyOfTheNewestPreviewsStay(int created, int maximumFiles, long maximumBytes, int expectedAtMost)
    {
        using TempDirectory temp = new();
        for (int index = 0; index < created; index++)
        {
            string path = Path.Combine(temp.Path, index.ToString("D4") + ".png");
            using var stream = File.Create(path); stream.SetLength(600_000);
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(index - 300));
        }
        VideoPosterCache.Trim(temp.Path, 100_000, maximumBytes, maximumFiles);
        string[] left = Directory.GetFiles(temp.Path).OrderBy(path => path).ToArray();
        Assert.InRange(left.Length, 1, expectedAtMost);
        // The newest previews stay; only the oldest are evicted.
        Assert.EndsWith((created - 1).ToString("D4") + ".png", left[^1]);
        Assert.False(File.Exists(Path.Combine(temp.Path, "0000.png")));
    }

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
            Version = 1, Entries = new Dictionary<string, object> { ["clip.webm"] = new {
                MediaIdentifier = "video", SourceUrl = "https://offline.invalid/never-request.webm", FileName = "clip.webm",
                Length = bytes.Length, Sha256 = Convert.ToHexString(SHA256.HashData(bytes)), Md5 = Convert.ToBase64String(MD5.HashData(bytes)) } }
        }));
        MediaAssetSnapshot manifest = await MediaAssetReader.OpenAsync(directory);
        return (manifest, manifest.GetPage().Single());
    }
}
