using System.Security.Cryptography;
using System.Text.Json;
using MeowChan.Desktop;
using MeowChan.Storage;
using SkiaSharp;

namespace MeowChan.DesktopTests;

public sealed class GalleryPosterIntegrityTests
{
    [Fact]
    public async Task WarmPosterDoesNotHideAMissingOriginal()
    {
        using TempDirectory temp = new();
        byte[] bytes = [1, 2, 3, 4];
        string original = Path.Combine(temp.Path, "clip.webm");
        await File.WriteAllBytesAsync(original, bytes);
        await File.WriteAllTextAsync(Path.Combine(temp.Path, ".meowchan-manifest.json"), JsonSerializer.Serialize(new
        {
            Version = 1, Entries = new Dictionary<string, object> { ["clip.webm"] = new
            {
                MediaIdentifier = "video", SourceUrl = "https://offline.invalid/never-request.webm", FileName = "clip.webm",
                Length = bytes.Length, Sha256 = Convert.ToHexString(SHA256.HashData(bytes)),
                Md5 = Convert.ToBase64String(MD5.HashData(bytes))
            } }
        }));
        MediaAssetSnapshot manifest = await MediaAssetReader.OpenAsync(temp.Path);
        MediaAssetReference asset = Assert.Single(manifest.GetPage());
        string cache = Path.Combine(temp.Path, "cache");
        Directory.CreateDirectory(cache);
        using SKBitmap bitmap = new(8, 6);
        using SKCanvas canvas = new(bitmap);
        canvas.Clear(SKColors.Coral);
        using SKImage image = SKImage.FromBitmap(bitmap);
        using SKData encoded = image.Encode(SKEncodedImageFormat.Png, 90);
        byte[] png = encoded.ToArray();
        string cached = Path.Combine(cache, asset.Sha256.ToLowerInvariant() + ".png");
        await File.WriteAllBytesAsync(cached, png);
        Assert.Equal(png, await VideoPosterCache.GetPngAsync(manifest, asset, cache, CancellationToken.None));
        File.Delete(original);
        Assert.Null(await VideoPosterCache.GetPngAsync(manifest, asset, cache, CancellationToken.None));
        Assert.Equal(png, await File.ReadAllBytesAsync(cached));
    }
}
