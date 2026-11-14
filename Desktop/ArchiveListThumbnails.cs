using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using MeowChan.Gallery;

namespace MeowChan.Desktop;

/// <summary>
/// Small previews in the archive's thread list. Videos use the same verified local poster as the reader;
/// no source thumbnail URL is fetched and a cached poster never substitutes for a missing original.
/// </summary>
internal static class ArchiveListThumbnails
{
    internal const int MaximumWidth = 160, MaximumHeight = 120;

    internal static SavedMediaKind Kind(ArchiveAssetRow row) =>
        SavedMediaTypes.Classify(row.Asset.OriginalFileName ?? row.Asset.Path);

    internal static string? CacheKey(ArchiveAssetRow row) => Kind(row) switch
    {
        SavedMediaKind.Image => MediaTileThumbnails.ByteKey(row.Asset, "@160x120"),
        SavedMediaKind.Video => MediaTileThumbnails.ByteKey(row.Asset, "@poster160x120"),
        _ => null
    };

    internal static async Task<Bitmap?> LoadAsync(ArchiveAssetRow row, string posterCacheDirectory, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (Kind(row) == SavedMediaKind.Video)
            return await MediaTileThumbnails.LoadPosterAsync(row.Manifest, row.Asset, posterCacheDirectory,
                MaximumWidth, MaximumHeight, CacheKey(row), token).ConfigureAwait(false);
        if (Kind(row) != SavedMediaKind.Image) return null;
        return await MediaTileThumbnails.KeepAsync(CacheKey(row), () =>
            ArchivePage.DecodeThumbnailAsync(row, token, MaximumWidth, MaximumHeight)).ConfigureAwait(false);
    }

    // The byte path is also testable without Avalonia or a native decoder: a verified cached poster is enough.
    internal static Task<byte[]?> PosterPngAsync(ArchiveAssetRow row, string posterCacheDirectory, CancellationToken token) =>
        VideoPosterCache.GetPngAsync(row.Manifest, row.Asset, posterCacheDirectory, token, MediaTileThumbnails.Verified);
}
