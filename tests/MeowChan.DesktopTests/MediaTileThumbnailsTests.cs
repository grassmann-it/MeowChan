using System.Security.Cryptography;
using System.Text.Json;
using MeowChan.Desktop;
using MeowChan.Gallery;
using MeowChan.Persistence;
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
    public async Task GroupedVideoSkipsAnUnreadableRepresentativeManifestWithoutLosingTheOtherOccurrence()
    {
        using TempDirectory temp = new();
        string cache = PosterFor(temp.Path);
        MediaLibraryEntry first = await VideoEntryAsync(temp.Path, "a"), second = await VideoEntryAsync(temp.Path, "b");
        Task<MediaAssetSnapshot> Open(string directory, CancellationToken token) => directory == first.Directory
            ? throw new IOException("Synthetic missing directory.") : Opener(directory, token);
        var poster = await MediaTileThumbnails.PosterPngAsync(Group(first, second), Open, cache, CancellationToken.None);
        Assert.NotNull(poster); Assert.Equal(second.AssetId, poster.Value.AssetId);
    }

    [Fact]
    public async Task AStaleIndexLengthCannotAuthorizeThePosterOfDifferentBytes()
    {
        using TempDirectory temp = new();
        string cache = PosterFor(temp.Path);
        MediaLibraryEntry entry = await VideoEntryAsync(temp.Path, "a");
        Assert.NotNull(await MediaTileThumbnails.PosterPngAsync(Group(entry), Opener, cache, CancellationToken.None));
        Assert.Null(await MediaTileThumbnails.PosterPngAsync(Group(entry with { Length = entry.Length + 1 }), Opener, cache, CancellationToken.None));
    }

    [Fact]
    public async Task GroupedPosterChecksEveryOccurrenceEvenWhenTheDecodedPreviewIsWarm()
    {
        using TempDirectory temp = new();
        string directory = PosterFor(temp.Path);
        MediaLibraryEntry first = await VideoEntryAsync(temp.Path, "a"), second = await VideoEntryAsync(temp.Path, "b");
        MediaLibraryTile group = Group(first, second);
        MediaThumbnailCache<Preview> cache = new(1);
        int decodes = 0;
        Task<Preview?> Load() => MediaTileThumbnails.KeepVerifiedPosterAsync(cache, "same-bytes", async token =>
            (await MediaTileThumbnails.PosterPngAsync(group, Opener, directory, token))?.Png,
            (_, _) => { decodes++; return Task.FromResult<Preview?>(new()); }, CancellationToken.None);
        Preview initial = Assert.IsType<Preview>(await Load()); cache.Release(initial);
        File.Delete(Path.Combine(first.Directory!, "clip.webm"));
        Assert.Same(initial, await Load()); cache.Release(initial); // the second original still verifies
        File.Delete(Path.Combine(second.Directory!, "clip.webm"));
        Assert.Null(await Load());
        Assert.Equal(1, decodes);
        Preview next = cache.Add("another", new());
        Assert.True(initial.Disposed); cache.Release(next); // no double lease in the warm/failed paths
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

    [Fact]
    public async Task AFileVerifiedThisSessionIsHashedAgainWhenItsIdentityChangesEvenWithSameLengthAndTime()
    {
        using TempDirectory temp = new();
        MediaLibraryEntry entry = await VideoEntryAsync(temp.Path, "a");
        MediaAssetSnapshot manifest = await MediaAssetReader.OpenAsync(entry.Directory!);
        string file = Path.Combine(entry.Directory!, "clip.webm");
        VerifiedFileMemo memo = new();
        DateTime stamp = File.GetLastWriteTimeUtc(file);
        await using (await manifest.OpenSessionVerifiedReadAsync(entry.AssetId!, memo)) { }
        Assert.Equal(1, memo.Count);
        // Untouched: the remembered check stands.
        await using (await manifest.OpenSessionVerifiedReadAsync(entry.AssetId!, memo)) { }
        // Same length, last write time restored (cp -p, rsync -a, a backup restore): the status-change time and the inode differ,
        // so the file is verified again and the changed bytes are refused.
        await File.WriteAllBytesAsync(file, new byte[] { 9, 9, 9, 9, 9, 9 });
        File.SetLastWriteTimeUtc(file, stamp);
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(file));
        await Assert.ThrowsAsync<InvalidDataException>(() => manifest.OpenSessionVerifiedReadAsync(entry.AssetId!, memo));
        // The same swap by rename-over (mv keeps the time): a new inode.
        await File.WriteAllBytesAsync(file, VideoBytes);
        File.SetLastWriteTimeUtc(file, stamp);
        await using (await manifest.OpenSessionVerifiedReadAsync(entry.AssetId!, memo)) { }
        string other = file + ".other";
        await File.WriteAllBytesAsync(other, new byte[] { 7, 7, 7, 7, 7, 7 });
        File.SetLastWriteTimeUtc(other, stamp);
        File.Move(other, file, overwrite: true);
        await Assert.ThrowsAsync<InvalidDataException>(() => manifest.OpenSessionVerifiedReadAsync(entry.AssetId!, memo));
        // Without the memory the whole file is always checked.
        await Assert.ThrowsAsync<InvalidDataException>(() => manifest.OpenVerifiedReadAsync(entry.AssetId!));
        await Assert.ThrowsAsync<InvalidDataException>(() => manifest.OpenSessionVerifiedReadAsync(entry.AssetId!, new VerifiedFileMemo()));
        // A file of another length is refused whatever the memory says.
        await File.WriteAllBytesAsync(file, new byte[] { 1, 2 });
        await Assert.ThrowsAsync<InvalidDataException>(() => manifest.OpenSessionVerifiedReadAsync(entry.AssetId!, memo));
    }

    [Fact]
    public async Task AFileWhoseIdentityCannotBeReadNeverCountsAsVerified()
    {
        using TempDirectory temp = new();
        string file = Path.Combine(temp.Path, "f.bin");
        await File.WriteAllBytesAsync(file, VideoBytes);
        RegularProfileFileIdentity? identity = RegularProfileFile.TryGetIdentity(file);
        Assert.NotNull(identity);
        VerifiedFileMemo memo = new();
        memo.Remember(file, null, "aa");
        Assert.Equal(0, memo.Count);
        memo.Remember(file, identity, "aa");
        Assert.True(memo.IsVerified(file, identity, "AA"));
        Assert.False(memo.IsVerified(file, null, "aa"));
        Assert.False(memo.IsVerified(file, identity!.Value with { ChangedNanoseconds = identity.Value.ChangedNanoseconds + 1 }, "aa"));
        Assert.False(memo.IsVerified(file, identity.Value with { Inode = identity.Value.Inode + 1 }, "aa"));
        // A path that is not a regular file has no identity: nothing is remembered for it.
        Assert.Null(RegularProfileFile.TryGetIdentity(Path.Combine(temp.Path, "missing")));
    }

    [Fact]
    public void TheVerifiedMemoryForgetsItsOldestFilesAtItsCapacity()
    {
        VerifiedFileMemo memo = new(2);
        RegularProfileFileIdentity one = new(1, 1, 1, 1, 0, 1, 0);
        RegularProfileFileIdentity two = one with { ModifiedSeconds = 2 };
        memo.Remember("/a", one, "aa"); memo.Remember("/b", one, "bb"); memo.Remember("/c", one, "cc");
        Assert.False(memo.IsVerified("/a", one, "aa"));
        Assert.True(memo.IsVerified("/c", one, "CC"));
        Assert.False(memo.IsVerified("/c", two, "cc"));
        memo.Capacity = 1;
        Assert.Equal(1, memo.Count);
    }

    private sealed class Preview : IDisposable
    {
        internal bool Disposed;
        public void Dispose() => Disposed = true;
    }

    [Fact]
    public void ThePreviewSetKeepsTheMostRecentlyUsedAndNeverDisposesAPreviewATileStillShows()
    {
        MediaThumbnailCache<Preview> cache = new(2);
        Preview a = cache.Add("a", new Preview()), b = cache.Add("b", new Preview());
        cache.Release(b);
        Assert.Same(a, cache.TryAcquire("a")); // a is used again, b is now the oldest
        Preview c = cache.Add("c", new Preview());
        Assert.Equal(2, cache.Count);
        Assert.True(b.Disposed); // pushed out and nobody shows it
        Assert.Null(cache.TryAcquire("b"));
        cache.Release(c);
        cache.Capacity = 1; // a is the oldest but two tiles still show it (Add and TryAcquire leases)
        Assert.False(a.Disposed);
        cache.Release(a);
        Assert.False(a.Disposed);
        cache.Release(a);
        Assert.True(a.Disposed); // the last tile let go of a preview that was already pushed out
        Assert.False(c.Disposed);
    }

    [Fact]
    public void ADuplicateAddSharesTheStoredPreviewAndDisposesTheNewDecode()
    {
        MediaThumbnailCache<Preview> cache = new(4);
        Preview first = cache.Add("k", new Preview()), second = new();
        Assert.Same(first, cache.Add("k", second));
        Assert.True(second.Disposed);
        Assert.False(first.Disposed);
        Assert.Equal(1, cache.Count);
        Preview unknown = new();
        cache.Release(unknown); // not from the set: the caller's own bitmap
        Assert.True(unknown.Disposed);
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
