using System.Security.Cryptography;
using System.Text.Json;
using MeowChan.Archive;
using MeowChan.Gallery;
using MeowChan.Persistence;
using MeowChan.Interfaces;
using MeowChan.Storage;
using SkiaSharp;

namespace MeowChan.DesktopSmoke;

// Like ExifOrientationTests: stored RGBY quadrants plus a minimal big-endian APP1 Orientation entry.
// Larger than the viewer's Fit viewport, so 100% must load Original instead of reusing a complete Fit.
internal static class ExifOrientationFixture
{
    internal const int StoredWidth = 1024, StoredHeight = 512;
    internal static readonly ResourceId Resource = new("exif-smoke", "synthetic", "42");
    internal static IReadOnlyList<GalleryEntry> Entries { get; private set; } = [];

    internal static async Task SeedAsync(string profile)
    {
        string directory = Path.Combine(profile, "media", "exif");
        Directory.CreateDirectory(directory);
        Dictionary<string, object> entries = new();
        List<CapturedAttachment> attachments = [];
        foreach (int orientation in new[] { 6, 8 })
        {
            byte[] bytes = Jpeg(orientation);
            // Check the input independently of the product orientation helper: a broken fixture cannot pass the smoke.
            using (SKCodec codec = SKCodec.Create(new MemoryStream(bytes)))
                if ((int)codec.EncodedOrigin != orientation || codec.Info.Width != StoredWidth || codec.Info.Height != StoredHeight)
                    throw new InvalidOperationException("Invalid synthetic EXIF fixture.");
            string file = $"orientation-{orientation}.jpg", media = $"exif-{orientation}", url = "https://offline.invalid/" + file;
            await File.WriteAllBytesAsync(Path.Combine(directory, file), bytes);
            entries[file] = new { MediaIdentifier = media, Variant = "original", SourceUrl = url, FileName = file,
                OriginalFileName = file, Length = bytes.Length, Md5 = Convert.ToBase64String(MD5.HashData(bytes)),
                Sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant() };
            attachments.Add(new(attachments.Count, media, "original", url, file));
        }
        await File.WriteAllTextAsync(Path.Combine(directory, ".meowchan-manifest.json"),
            JsonSerializer.Serialize(new { Version = 1, Entries = entries }));
        CapturedPost[] posts = await StrictMediaFixture.BindAsync(directory, Resource,
            [new CapturedPost("42", 0, PostFieldPresence.All, Html: "<p>Synthetic EXIF orientations 6 and 8.</p>", Attachments: attachments)]);
        using (ProfileLease lease = ProfileLease.Acquire(profile))
        await using (ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(profile, lease))
            await catalog.CaptureAsync(new(Resource, "https://offline.invalid/42", "Synthetic EXIF orientations",
                ThreadSourceState.Archived, DateTimeOffset.UnixEpoch, directory, posts));
        MediaAssetSnapshot snapshot = await MediaAssetReader.OpenAsync(directory);
        Entries = posts[0].Attachments!.Select(attachment =>
        {
            MediaAssetReference asset = snapshot.GetPage().Single(asset => asset.MediaIdentifier == attachment.MediaId);
            ResolvedMediaAttachment resolution = MediaAttachmentResolver.Resolve(snapshot, Resource, "42", attachment)
                ?? throw new InvalidOperationException("EXIF fixture lacks a strict binding.");
            return new GalleryEntry(new(Resource, directory, asset.AssetId), snapshot, asset, posts[0], attachment, false, false)
                { AttachmentResolution = resolution };
        }).ToArray();
    }

    private static byte[] Jpeg(int orientation)
    {
        using SKBitmap bitmap = new(StoredWidth, StoredHeight, SKColorType.Bgra8888, SKAlphaType.Opaque);
        using (SKCanvas canvas = new(bitmap))
        using (SKPaint paint = new())
        {
            SKColor[] colors = [SKColors.Red, SKColors.Lime, SKColors.Blue, SKColors.Yellow];
            for (int i = 0; i < 4; i++)
            {
                paint.Color = colors[i];
                canvas.DrawRect(i % 2 * StoredWidth / 2, i / 2 * StoredHeight / 2, StoredWidth / 2, StoredHeight / 2, paint);
            }
        }
        using SKData data = bitmap.Encode(SKEncodedImageFormat.Jpeg, 100);
        byte[] jpeg = data.ToArray();
        byte[] segment = [0xff, 0xe1, 0, 34, (byte)'E', (byte)'x', (byte)'i', (byte)'f', 0, 0,
            (byte)'M', (byte)'M', 0, 42, 0, 0, 0, 8, 0, 1, 1, 0x12, 0, 3, 0, 0, 0, 1, 0, (byte)orientation, 0, 0, 0, 0, 0, 0];
        return [.. jpeg[..2], .. segment, .. jpeg[2..]];
    }
}
