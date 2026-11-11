using System.Security.Cryptography;
using System.Text.Json;
using MeowChan.Desktop;
using MeowChan.Gallery;
using MeowChan.Storage;
using SkiaSharp;

namespace MeowChan.DesktopTests;

public sealed class MediaTileThumbnailsTests
{
    private static readonly byte[] VideoBytes = { 1, 2, 3, 4, 5, 6 };

    [Fact]
    public async Task VideoTileUsesTheReaderPosterFromTheLocalCacheWithoutAnyDecoder()
    {
        using TempDirectory temp = new();
        string cache = PosterFor(temp.Path);
        MediaLibraryEntry entry = await VideoEntryAsync(temp.Path, "a");
        (byte[] Png, string AssetId)? poster = await MediaTileThumbnails.PosterPngAsync(Group(entry), Opener, cache, CancellationToken.None);
        Assert.NotNull(poster);
        Assert.Equal(entry.AssetId, poster.Value.AssetId);
    }

    [Fact]
    public async Task GroupedVideoFallsBackToAnotherOccurrenceWhenTheRepresentativeNoLongerVerifies()
    {
        using TempDirectory temp = new();
        string cache = PosterFor(temp.Path);
        MediaLibraryEntry first = await VideoEntryAsync(temp.Path, "a");
        MediaLibraryEntry second = await VideoEntryAsync(temp.Path, "b");
        MediaLibraryTile group = Group(first, second);
        await File.WriteAllBytesAsync(Path.Combine(first.Directory!, "clip.webm"), new byte[] { 9, 9, 9, 9, 9, 9 });
        (byte[] Png, string AssetId)? poster = await MediaTileThumbnails.PosterPngAsync(group, Opener, cache, CancellationToken.None);
        Assert.NotNull(poster);
        Assert.Equal(second.AssetId, poster.Value.AssetId);
        // Nothing verifies: the tile stays without a poster rather than showing unverified bytes.
        await File.WriteAllBytesAsync(Path.Combine(second.Directory!, "clip.webm"), new byte[] { 8, 8, 8, 8, 8, 8 });
        Assert.Null(await MediaTileThumbnails.PosterPngAsync(group, Opener, cache, CancellationToken.None));
    }

    [Fact]
    public async Task OnlySavedOccurrencesAreCandidatesAndTheListIsBounded()
    {
        using TempDirectory temp = new();
        MediaLibraryEntry present = await VideoEntryAsync(temp.Path, "a");
        MediaLibraryEntry missing = present with { Presence = MediaLibraryPresence.Missing, Directory = null, AssetId = null };
        List<MediaLibraryEntry> all = [missing, present];
        for (int i = 0; i < 10; i++) all.Add(present with { Name = "n" + i });
        MediaLibraryTile group = new(missing, all);
        IReadOnlyList<MediaLibraryEntry> candidates = MediaTileThumbnails.Candidates(group);
        Assert.Equal(MediaTileThumbnails.MaximumPosterCandidates, candidates.Count);
        Assert.All(candidates, candidate => Assert.Equal(MediaLibraryPresence.Present, candidate.Presence));
    }

    private static Task<MediaAssetSnapshot> Opener(string directory, CancellationToken token) => MediaAssetReader.OpenAsync(directory, token);

    private static MediaLibraryTile Group(params MediaLibraryEntry[] entries) => new(entries[0], entries);

    private static string PosterFor(string root)
    {
        string cache = Path.Combine(root, "cache"); Directory.CreateDirectory(cache);
        using SKBitmap bitmap = new(160, 90);
        using SKCanvas canvas = new(bitmap); canvas.Clear(SKColors.Coral);
        using SKImage image = SKImage.FromBitmap(bitmap);
        using SKData data = image.Encode(SKEncodedImageFormat.Png, 90);
        File.WriteAllBytes(Path.Combine(cache, Convert.ToHexString(SHA256.HashData(VideoBytes)).ToLowerInvariant() + ".png"), data.ToArray());
        return cache;
    }

    private static async Task<MediaLibraryEntry> VideoEntryAsync(string root, string name)
    {
        string directory = Path.Combine(root, name); Directory.CreateDirectory(directory);
        string sha = Convert.ToHexString(SHA256.HashData(VideoBytes));
        await File.WriteAllBytesAsync(Path.Combine(directory, "clip.webm"), VideoBytes);
        await File.WriteAllTextAsync(Path.Combine(directory, ".meowchan-manifest.json"), JsonSerializer.Serialize(new
        {
            Version = 1, Entries = new Dictionary<string, object> { ["clip.webm"] = new {
                MediaIdentifier = "video", SourceUrl = "https://offline.invalid/never-request.webm", FileName = "clip.webm",
                Length = VideoBytes.Length, Sha256 = sha, Md5 = Convert.ToBase64String(MD5.HashData(VideoBytes)) } }
        }));
        MediaAssetSnapshot manifest = await MediaAssetReader.OpenAsync(directory);
        MediaAssetReference asset = manifest.GetPage().Single();
        return new MediaLibraryEntry(new ResourceId("t", "g", name), "T", 1, DateTimeOffset.UnixEpoch, null, name, 0, null, 0, null,
            directory, asset.AssetId, sha, VideoBytes.Length, "video", "original", "clip.webm", SavedMediaKind.Video,
            MediaLibraryPresence.Present, false, false);
    }
}
