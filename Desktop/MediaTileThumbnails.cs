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

    /// <summary>The kept preview of a tile's bytes, leased to the caller (give it back with <see cref="Release"/>); null when none is kept.</summary>
    internal static Bitmap? TryGetCached(MediaLibraryTile tile) =>
        MediaLibraryGrouping.ByteKey(tile.Entry) is { } key ? Cache.TryAcquire(key) : null;

    /// <summary>Keeps a decoded preview of a tile's bytes and leases the bitmap to show to the caller; a tile without proven bytes is not kept.</summary>
    internal static Bitmap Remember(MediaLibraryTile tile, Bitmap bitmap) =>
        MediaLibraryGrouping.ByteKey(tile.Entry) is { } key ? Cache.Add(key, bitmap) : bitmap;

    /// <summary>Gives back a preview from <see cref="TryGetCached"/>, <see cref="Remember"/> or <see cref="LoadAsync"/>.</summary>
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

    internal static async Task<Bitmap?> LoadAsync(MediaLibraryTile tile, ManifestOpener openManifest, string posterCacheDirectory, CancellationToken token)
    {
        MediaLibraryEntry entry = tile.Entry;
        if (entry.Directory == null || entry.AssetId == null) return null;
        MediaAssetSnapshot manifest = await openManifest(entry.Directory, token).ConfigureAwait(false);
        MediaAssetReference? asset = Matching(manifest, entry);
        if (asset != null && SavedMediaTypes.Classify(asset.OriginalFileName ?? asset.Path) != SavedMediaKind.Video)
            return await ArchivePage.DecodeThumbnailAsync(new ArchiveAssetRow(manifest, asset), token, verifiedFiles: Verified).ConfigureAwait(false);
        if (entry.Kind != SavedMediaKind.Video && asset == null) return null;
        (byte[] png, string assetId)? poster = await PosterPngAsync(tile, openManifest, posterCacheDirectory, token).ConfigureAwait(false);
        return poster == null ? null
            : await VideoPosterCache.DecodeBitmapAsync(poster.Value.png, poster.Value.assetId, PosterWidth * 2, PosterHeight * 2, token).ConfigureAwait(false);
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
        manifest.FindById(entry.AssetId!) is { } asset && string.Equals(asset.Sha256, entry.Sha256, StringComparison.OrdinalIgnoreCase) ? asset : null;
}
