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
        for (int index = 0; index < 260; index++)
        {
            string path = Path.Combine(temp.Path, index.ToString("D4") + ".png");
            using var stream = File.Create(path); stream.SetLength(600_000);
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(index - 300));
        }
        VideoPosterCache.Trim(temp.Path, VideoPosterCache.MaximumPngBytes);
        var files = Directory.GetFiles(temp.Path).Select(path => new FileInfo(path)).ToArray();
        Assert.True(files.Length < VideoPosterCache.MaximumCacheFiles);
        Assert.True(files.Sum(file => file.Length) + VideoPosterCache.MaximumPngBytes <= VideoPosterCache.MaximumCacheBytes);
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
