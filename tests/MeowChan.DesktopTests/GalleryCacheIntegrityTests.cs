using System.Security.Cryptography;
using System.Text.Json;
using Avalonia;
using MeowChan.Desktop;
using MeowChan.Gallery;
using MeowChan.Storage;

namespace MeowChan.DesktopTests;

public sealed class GalleryCacheIntegrityTests
{
    private sealed class Img(int width, int height) : IDisposable
    {
        internal PixelSize Size { get; } = new(width, height);
        internal bool Disposed;
        public void Dispose() => Disposed = true;
    }

    private static readonly GalleryDecodeRequest.Fit Fit = new(100, 100);
    private static readonly GalleryImageInfo Info = new(1000, 1000, "Png", false);
    private const long Budget = 100 * 100 * 4 * 4;

    [Fact]
    public async Task SameLengthRewriteWithRestoredWriteTimeInvalidatesTheCapturedIdentity()
    {
        using TempDirectory temp = new();
        GalleryEntry entry = await EntryAsync(Path.Combine(temp.Path, "one"), "image.png", "original image"u8.ToArray());
        Func<bool> valid = Assert.IsType<Func<bool>>(GalleryImageCache.Validity(entry));
        DateTime writeTime = File.GetLastWriteTimeUtc(entry.Asset.Path);
        await File.WriteAllBytesAsync(entry.Asset.Path, "replaced image"u8.ToArray());
        File.SetLastWriteTimeUtc(entry.Asset.Path, writeTime);

        Assert.Equal(entry.Asset.Length, new FileInfo(entry.Asset.Path).Length);
        Assert.Equal(writeTime, File.GetLastWriteTimeUtc(entry.Asset.Path));
        Assert.False(valid());

        // A rename-over has a new regular-file identity too, even when its bytes and timestamps look like the old file.
        await File.WriteAllBytesAsync(entry.Asset.Path, "original image"u8.ToArray());
        File.SetLastWriteTimeUtc(entry.Asset.Path, writeTime);
        Func<bool> renameValid = Assert.IsType<Func<bool>>(GalleryImageCache.Validity(entry));
        string replacement = entry.Asset.Path + ".replacement";
        await File.WriteAllBytesAsync(replacement, "replaced image"u8.ToArray());
        File.SetLastWriteTimeUtc(replacement, writeTime);
        File.Move(replacement, entry.Asset.Path, overwrite: true);

        Assert.False(renameValid());
    }

    [Fact]
    public async Task AWarmBytesHitProvesTheCurrentOccurrenceBeforeSharingAcrossFolders()
    {
        using TempDirectory temp = new();
        byte[] bytes = "same image bytes"u8.ToArray();
        GalleryEntry firstEntry = await EntryAsync(Path.Combine(temp.Path, "first"), "image.png", bytes);
        GalleryEntry secondEntry = await EntryAsync(Path.Combine(temp.Path, "second"), "image.png", bytes);
        GalleryImageCache<Img> cache = new(image => image.Size, Budget);
        int decodes = 0;

        async Task<(Img Bitmap, GalleryImageInfo Info)> Decode(GalleryEntry entry, GalleryDecodeRequest _, CancellationToken token)
        {
            decodes++;
            await using Stream verified = await entry.Manifest.OpenVerifiedReadAsync(entry.Asset.AssetId, token);
            return (new Img(100, 100), Info);
        }

        (Img first, _) = await GalleryImageCache.DecodeThrough(cache, Decode, firstEntry, Fit, CancellationToken.None);
        cache.Release(first);
        (Img shared, _) = await GalleryImageCache.DecodeThrough(cache, Decode, secondEntry, Fit, CancellationToken.None);

        Assert.Same(first, shared);
        Assert.Equal(1, decodes);
        cache.Release(shared);
    }

    [Fact]
    public async Task AChangedOtherOccurrenceCannotUseTheWarmBytesHit()
    {
        using TempDirectory temp = new();
        byte[] bytes = "same image bytes"u8.ToArray();
        GalleryEntry firstEntry = await EntryAsync(Path.Combine(temp.Path, "first"), "image.png", bytes);
        GalleryEntry secondEntry = await EntryAsync(Path.Combine(temp.Path, "second"), "image.png", bytes);
        GalleryImageCache<Img> cache = new(image => image.Size, Budget);
        int decodes = 0;

        async Task<(Img Bitmap, GalleryImageInfo Info)> Decode(GalleryEntry entry, GalleryDecodeRequest _, CancellationToken token)
        {
            decodes++;
            await using Stream verified = await entry.Manifest.OpenVerifiedReadAsync(entry.Asset.AssetId, token);
            return (new Img(100, 100), Info);
        }

        (Img first, _) = await GalleryImageCache.DecodeThrough(cache, Decode, firstEntry, Fit, CancellationToken.None);
        cache.Release(first);
        DateTime writeTime = File.GetLastWriteTimeUtc(secondEntry.Asset.Path);
        await File.WriteAllBytesAsync(secondEntry.Asset.Path, "changed image!!!"u8.ToArray());
        File.SetLastWriteTimeUtc(secondEntry.Asset.Path, writeTime);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            GalleryImageCache.DecodeThrough(cache, Decode, secondEntry, Fit, CancellationToken.None));
        Assert.Equal(2, decodes);
        Assert.Equal(1, cache.Count); // The valid first occurrence remains available; the failed second one took no lease.
    }

    [Fact]
    public async Task MissingOrSymlinkedOccurrenceNeverBecomesAWarmHit()
    {
        using TempDirectory temp = new();
        byte[] bytes = "same image bytes"u8.ToArray();
        GalleryEntry firstEntry = await EntryAsync(Path.Combine(temp.Path, "first"), "image.png", bytes);
        GalleryEntry secondEntry = await EntryAsync(Path.Combine(temp.Path, "second"), "image.png", bytes);
        GalleryImageCache<Img> cache = new(image => image.Size, Budget);
        int decodes = 0;

        async Task<(Img Bitmap, GalleryImageInfo Info)> Decode(GalleryEntry entry, GalleryDecodeRequest _, CancellationToken token)
        {
            decodes++;
            await using Stream verified = await entry.Manifest.OpenVerifiedReadAsync(entry.Asset.AssetId, token);
            return (new Img(100, 100), Info);
        }

        (Img first, _) = await GalleryImageCache.DecodeThrough(cache, Decode, firstEntry, Fit, CancellationToken.None);
        cache.Release(first);
        File.Delete(secondEntry.Asset.Path);
        Assert.Null(GalleryImageCache.Validity(secondEntry));
        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            GalleryImageCache.DecodeThrough(cache, Decode, secondEntry, Fit, CancellationToken.None));
        Assert.Equal(2, decodes);

        // A final symlink is rejected by the regular-file identity before the cache can even attempt a warm hit.
        if (OperatingSystem.IsWindows()) return;
        File.CreateSymbolicLink(secondEntry.Asset.Path, firstEntry.Asset.Path);
        Assert.Null(GalleryImageCache.Validity(secondEntry));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            GalleryImageCache.DecodeThrough(cache, Decode, secondEntry, Fit, CancellationToken.None));
        Assert.Equal(3, decodes);
    }

    [Fact]
    public async Task CancelledWarmVerificationAcquiresNoLease()
    {
        using TempDirectory temp = new();
        byte[] bytes = "same image bytes"u8.ToArray();
        GalleryEntry firstEntry = await EntryAsync(Path.Combine(temp.Path, "first"), "image.png", bytes);
        GalleryEntry secondEntry = await EntryAsync(Path.Combine(temp.Path, "second"), "image.png", bytes);
        GalleryImageCache<Img> cache = new(image => image.Size, Budget);
        async Task<(Img Bitmap, GalleryImageInfo Info)> Decode(GalleryEntry entry, GalleryDecodeRequest _, CancellationToken token)
        {
            await using Stream verified = await entry.Manifest.OpenVerifiedReadAsync(entry.Asset.AssetId, token);
            return (new Img(100, 100), Info);
        }

        (Img first, _) = await GalleryImageCache.DecodeThrough(cache, Decode, firstEntry, Fit, CancellationToken.None);
        cache.Release(first);
        using CancellationTokenSource canceled = new();
        canceled.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            GalleryImageCache.DecodeThrough(cache, Decode, secondEntry, Fit, canceled.Token));

        Assert.True(cache.TryAcquire(GalleryImageCache.KeyOf(firstEntry)!, Fit, out Img? stillThere, out _));
        Assert.Same(first, stillThere);
        cache.Release(stillThere);
    }

    [Fact]
    public async Task PreloaderTouchProvesAnOtherFolderBeforeKeepingItsWarmImage()
    {
        using TempDirectory temp = new();
        byte[] bytes = "same image bytes"u8.ToArray();
        GalleryEntry firstEntry = await EntryAsync(Path.Combine(temp.Path, "first"), "image.png", bytes);
        GalleryEntry secondEntry = await EntryAsync(Path.Combine(temp.Path, "second"), "image.png", bytes);
        GalleryImageCache<Img> cache = new(image => image.Size, Budget);
        async Task<(Img Bitmap, GalleryImageInfo Info)> Decode(GalleryEntry entry, GalleryDecodeRequest _, CancellationToken token)
        {
            await using Stream verified = await entry.Manifest.OpenVerifiedReadAsync(entry.Asset.AssetId, token);
            return (new Img(100, 100), Info);
        }

        (Img first, _) = await GalleryImageCache.DecodeThrough(cache, Decode, firstEntry, Fit, CancellationToken.None);
        cache.Release(first);
        int preloaded = 0;
        GalleryPreloader<Img> preloader = new(cache, async (entry, request, token) =>
        {
            preloaded++;
            return await Decode(entry, request, token);
        }, TimeSpan.Zero);

        await preloader.Schedule(new[] { firstEntry, secondEntry }, 0, Fit, ahead: 1, behind: 0);

        Assert.Equal(0, preloaded);
        Assert.Equal(1, cache.Count);
    }

    private static async Task<GalleryEntry> EntryAsync(string directory, string name, byte[] bytes)
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, name);
        await File.WriteAllBytesAsync(path, bytes);
        string sha = Convert.ToHexString(SHA256.HashData(bytes));
        Dictionary<string, object> entries = new()
        {
            [name] = new
            {
                MediaIdentifier = name, SourceUrl = "https://offline.invalid/never-request/" + name, FileName = name,
                Length = bytes.Length, Sha256 = sha, Md5 = Convert.ToBase64String(MD5.HashData(bytes))
            }
        };
        await File.WriteAllTextAsync(Path.Combine(directory, ".meowchan-manifest.json"),
            JsonSerializer.Serialize(new { Version = 1, Entries = entries }));
        MediaAssetSnapshot manifest = await MediaAssetReader.OpenAsync(directory);
        MediaAssetReference asset = manifest.GetPage()[0];
        return new GalleryEntry(new GalleryAssetKey(new ResourceId("test", "gallery", directory), directory, asset.AssetId),
            manifest, asset, null, null, false, true);
    }
}
