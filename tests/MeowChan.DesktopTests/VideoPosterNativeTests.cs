using System.Security.Cryptography;
using System.Text.Json;
using MeowChan.Desktop;
using MeowChan.Storage;
using SkiaSharp;

namespace MeowChan.DesktopTests;

public sealed class NativeVideoPosterFactAttribute : FactAttribute
{
    public NativeVideoPosterFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("MEOWCHAN_VIDEO_POSTER_NATIVE") != "1")
            Skip = "Native poster proof is opt-in; shipped apphost acceptance also exercises the production poster path.";
    }
}

public sealed class VideoPosterNativeTests
{
    [NativeVideoPosterFact]
    public async Task ShippedVp8AndVp9FixturesProduceRealFramesAndPersistentOfflinePostersWithoutAnyDisplay()
    {
        foreach (string codec in new[] { "vp8", "vp9" })
        {
            using TempDirectory temp = new();
            string? fixture = null;
            for (DirectoryInfo? parent = new(AppContext.BaseDirectory); parent != null; parent = parent.Parent)
            {
                string candidate = Path.Combine(parent.FullName, "tests", "Fixtures", "gallery-playback-" + codec + ".webm");
                if (File.Exists(candidate)) { fixture = candidate; break; }
            }
            Assert.NotNull(fixture);
            byte[] video = await File.ReadAllBytesAsync(fixture);
            string path = Path.Combine(temp.Path, "clip.webm");
            await File.WriteAllBytesAsync(path, video);
            await File.WriteAllTextAsync(Path.Combine(temp.Path, ".meowchan-manifest.json"), JsonSerializer.Serialize(new
            {
                Version = 1, Entries = new Dictionary<string, object> { ["clip.webm"] = new {
                    MediaIdentifier = "video", SourceUrl = "https://offline.invalid/never-contact.webm", FileName = "clip.webm",
                    Length = video.Length, Sha256 = Convert.ToHexString(SHA256.HashData(video)), Md5 = Convert.ToBase64String(MD5.HashData(video)) } }
            }));
            MediaAssetSnapshot manifest = await MediaAssetReader.OpenAsync(temp.Path);
            MediaAssetReference asset = manifest.GetPage().Single();
            string cache = Path.Combine(temp.Path, "posters");
            Task<byte[]?> generating = VideoPosterCache.GetPngAsync(manifest, asset, cache, CancellationToken.None);
            for (int cycle = 0; cycle < 5 && !generating.IsCompleted; cycle++)
            {
                GC.Collect(); GC.WaitForPendingFinalizers(); await Task.Delay(35);
            }
            byte[] png = Assert.IsType<byte[]>(await generating);
            Assert.True(VideoPosterCache.Valid(png));
            using SKBitmap decoded = SKBitmap.Decode(png);
            Assert.Equal(160, decoded.Width); Assert.Equal(90, decoded.Height);
            Assert.True(decoded.Pixels.Select(pixel => (uint)pixel).Distinct().Take(65).Count() > 64,
                "The decoded poster must contain the fixture's real colored pattern, not a blank placeholder.");
            string cached = Assert.Single(Directory.GetFiles(cache, "*.png"));
            DateTime modified = File.GetLastWriteTimeUtc(cached);
            MediaAssetSnapshot restarted = await MediaAssetReader.OpenAsync(temp.Path);
            Assert.Equal(png, await VideoPosterCache.GetPngAsync(restarted, restarted.GetPage().Single(), cache, CancellationToken.None));
            Assert.Equal(modified, File.GetLastWriteTimeUtc(cached));
            File.Move(path, path + ".renamed"); // Native stream/player were released; no outstanding file handle.
            File.Move(path + ".renamed", path);
        }
    }
}
