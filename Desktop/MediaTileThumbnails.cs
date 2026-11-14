using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using MeowChan.Gallery;
using MeowChan.Storage;

namespace MeowChan.Desktop;

/// <summary>
/// Local, verified tile previews of the media page. Images are decoded from their verified file; videos use the
/// same derived poster the thread reader shows (<see cref="VideoPosterCache"/>, keyed by the verified bytes).
/// Nothing is fetched from the network and nothing new is generated beyond what the reader would generate.
/// </summary>
internal static class MediaTileThumbnails
{
    internal const int MaximumPosterCandidates = 4;
    internal const int PosterWidth = 170, PosterHeight = 125;
    /// <summary>Verified files kept per preview in the memory set: a file whose check is remembered is not hashed again this session.</summary>
    internal const int VerifiedFilesPerPreview = 32;

    /// <summary>Decoded previews of this session, shared by every media page (see <see cref="MediaThumbnailCache"/>).</summary>
    internal static MediaThumbnailCache<Bitmap> Cache { get; } = new();
    /// <summary>Files fully verified for a preview in this session and unchanged since (see <see cref="VerifiedFileMemo"/>).</summary>
    internal static VerifiedFileMemo Verified { get; } = new(MediaThumbnailCache.DefaultCapacity * VerifiedFilesPerPreview);

    /// <summary>Sets how many previews stay in memory (a setting); the memory of verified files follows it.</summary>
    internal static void Configure(int tiles)
    {
        Cache.Capacity = Math.Max(1, tiles);
        Verified.Capacity = Math.Max(1, tiles) * VerifiedFilesPerPreview;
    }

    /// <summary>
    /// An image preview leased to the caller (give it back with <see cref="Release"/>). Videos deliberately have no synchronous
    /// shortcut: even a warm poster must pass the current local-original check in <see cref="LoadAsync"/> first.
    /// </summary>
    internal static Bitmap? TryGetCached(MediaLibraryTile tile) =>
        tile.Entry.Kind == SavedMediaKind.Image && MediaLibraryGrouping.ByteKey(tile.Entry) is { } key ? Cache.TryAcquire(key) : null;

    /// <summary>
    /// The key of a preview of a saved file in <see cref="Cache"/>: the same bytes identity as <see cref="MediaLibraryGrouping"/> (hash and
    /// length), so the archive tiles, the reader and the media page share one decoded preview; <paramref name="size"/> names a
    /// preview made at another size than the tile default. Null when the file has no hash.
    /// </summary>
    internal static string? ByteKey(MediaAssetReference asset, string size = "") =>
        string.IsNullOrEmpty(asset.Sha256) ? null : asset.Sha256.ToLowerInvariant() + ":" + asset.Length + size;

    /// <summary>
    /// The kept preview under <paramref name="key"/>, leased to the caller, or the one <paramref name="decode"/> makes, kept under it.
    /// Give the result back with <see cref="Release"/>. Without a key nothing is kept.
    /// </summary>
    internal static async Task<Bitmap?> KeepAsync(string? key, Func<Task<Bitmap?>> decode)
    {
        if (key != null && Cache.TryAcquire(key) is { } hit) return hit;
        Bitmap? bitmap = await decode().ConfigureAwait(false);
        return bitmap != null && key != null ? Cache.Add(key, bitmap) : bitmap;
    }

    internal static string? PosterKey(MediaAssetReference asset, int width, int height) =>
        ByteKey(asset, $"@poster{width}x{height}");

    /// <summary>Every local video surface checks the original before leasing a decoded poster, even on a memory-cache hit.</summary>
    internal static Task<Bitmap?> LoadPosterAsync(MediaAssetSnapshot manifest, MediaAssetReference asset, string posterCacheDirectory,
        int maximumWidth, int maximumHeight, string? key, CancellationToken token) =>
        KeepVerifiedPosterAsync(Cache, key,
            cancellation => VideoPosterCache.GetPngAsync(manifest, asset, posterCacheDirectory, cancellation, Verified),
            (png, cancellation) => VideoPosterCache.DecodeBitmapAsync(png, asset.AssetId, maximumWidth, maximumHeight, cancellation), token);

    // The lease ordering is independent of Avalonia so warm-cache/integrity/cancellation cases can be exercised without a GUI.
    internal static async Task<T?> KeepVerifiedPosterAsync<T>(MediaThumbnailCache<T> cache, string? key,
        Func<CancellationToken, Task<byte[]?>> verifiedPoster, Func<byte[], CancellationToken, Task<T?>> decode,
        CancellationToken token) where T : class, IDisposable
    {
        token.ThrowIfCancellationRequested();
        byte[]? png = await verifiedPoster(token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (png == null) return null;
        if (key != null && cache.TryAcquire(key) is { } hit) return hit;
        T? preview = await decode(png, token).ConfigureAwait(false);
        if (token.IsCancellationRequested) { preview?.Dispose(); token.ThrowIfCancellationRequested(); }
        return preview != null && key != null ? cache.Add(key, preview) : preview;
    }

    /// <summary>Gives back a preview from <see cref="TryGetCached"/>, <see cref="LoadPosterAsync"/> or <see cref="LoadAsync"/>.</summary>
    internal static void Release(Bitmap bitmap) => Cache.Release(bitmap);

    /// <summary>Opens the manifest of an entry's media directory.</summary>
    internal delegate Task<MediaAssetSnapshot> ManifestOpener(string directory, CancellationToken token);

    /// <summary>
    /// The entries of a tile that may supply a video poster: the representative first, then a few other saved
    /// occurrences of the same bytes, in page order. A video moves on when the poster of one occurrence cannot be
    /// produced (for example its file no longer verifies); an image only ever uses the representative.
    /// </summary>
    internal static IReadOnlyList<MediaLibraryEntry> Candidates(MediaLibraryTile tile) =>
        new[] { tile.Entry }.Concat(tile.Occurrences.Where(entry => entry != tile.Entry))
            .Where(entry => entry.Presence == MediaLibraryPresence.Present && entry.Directory != null && entry.AssetId != null)
            .Take(MaximumPosterCandidates).ToArray();

    /// <summary>Loads and leases a preview. The caller releases it once; do not add an already leased result to the cache again.</summary>
    internal static async Task<Bitmap?> LoadAsync(MediaLibraryTile tile, ManifestOpener openManifest, string posterCacheDirectory, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        MediaLibraryEntry entry = tile.Entry;
        // Do not open only the representative first: if that directory disappeared, another verified occurrence can still supply the poster.
        if (entry.Kind == SavedMediaKind.Video)
            return await LoadLibraryPosterAsync(tile, openManifest, posterCacheDirectory, token).ConfigureAwait(false);
        if (entry.Directory == null || entry.AssetId == null) return null;
        MediaAssetSnapshot manifest = await openManifest(entry.Directory, token).ConfigureAwait(false);
        MediaAssetReference? asset = Matching(manifest, entry);
        if (asset == null) return null;
        if (SavedMediaTypes.Classify(asset.OriginalFileName ?? asset.Path) == SavedMediaKind.Video)
            return await LoadLibraryPosterAsync(tile, openManifest, posterCacheDirectory, token).ConfigureAwait(false);
        return await KeepAsync(MediaLibraryGrouping.ByteKey(entry), () =>
            ArchivePage.DecodeThumbnailAsync(new ArchiveAssetRow(manifest, asset), token, verifiedFiles: Verified)).ConfigureAwait(false);
    }

    private static async Task<Bitmap?> LoadLibraryPosterAsync(MediaLibraryTile tile, ManifestOpener openManifest,
        string posterCacheDirectory, CancellationToken token)
    {
        string? key = MediaLibraryGrouping.ByteKey(tile.Entry) is { } bytes ? bytes + $"@poster{PosterWidth * 2}x{PosterHeight * 2}" : null;
        string assetId = tile.Entry.AssetId ?? string.Empty;
        return await KeepVerifiedPosterAsync(Cache, key, async cancellation =>
        {
            var poster = await PosterPngAsync(tile, openManifest, posterCacheDirectory, cancellation).ConfigureAwait(false);
            if (poster == null) return null;
            assetId = poster.Value.AssetId;
            return poster.Value.Png;
        }, (png, cancellation) => VideoPosterCache.DecodeBitmapAsync(png, assetId, PosterWidth * 2, PosterHeight * 2, cancellation), token).ConfigureAwait(false);
    }

    /// <summary>The verified local poster of the first occurrence that can supply one, or null.</summary>
    internal static async Task<(byte[] Png, string AssetId)?> PosterPngAsync(MediaLibraryTile tile, ManifestOpener openManifest,
        string posterCacheDirectory, CancellationToken token)
    {
        foreach (MediaLibraryEntry entry in Candidates(tile))
        {
            token.ThrowIfCancellationRequested();
            MediaAssetSnapshot manifest;
            try { manifest = await openManifest(entry.Directory!, token).ConfigureAwait(false); }
            catch (Exception exception) when (exception is not OperationCanceledException) { continue; }
            if (Matching(manifest, entry) is not { } asset) continue;
            byte[]? png = await VideoPosterCache.GetPngAsync(manifest, asset, posterCacheDirectory, token, Verified).ConfigureAwait(false);
            if (png != null) return (png, asset.AssetId);
        }
        return null;
    }

    // The index is derived data: only bytes that still match it are shown.
    private static MediaAssetReference? Matching(MediaAssetSnapshot manifest, MediaLibraryEntry entry) =>
        manifest.FindById(entry.AssetId!) is { } asset && asset.Length == entry.Length
        && string.Equals(asset.Sha256, entry.Sha256, StringComparison.OrdinalIgnoreCase) ? asset : null;
}
