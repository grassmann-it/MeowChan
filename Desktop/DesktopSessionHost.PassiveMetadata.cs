using System;
using System.IO;
using MeowChan.Ratings;

namespace MeowChan.Desktop;

internal sealed partial class DesktopSessionHost
{
    // Optional corrupt indexes stay feature-local. Destructive commands still revalidate their complete
    // on-disk protection sets; these passive indexes are never authority for deletion or file consumption.
    private void InitializePassiveMetadata()
    {
        try { lock (_favoritesSync) LoadFavoritesIfNeeded(); }
        catch (Exception error) when (MetadataReadError(error))
        {
            _favoriteReadError = error;
            _logger.Warning(error, "The passive favorite index is unavailable.");
        }
        try
        {
            lock (_ratingsSync)
            {
                _ratingsStore = new(_profileLease.ProfileDirectory, _profileLease);
                PublishMediaRatings(_ratingsStore.List());
            }
        }
        catch (Exception error) when (MetadataReadError(error))
        {
            // Do not poison the authoritative loader: the first destructive command still gets the exact
            // disk validation error and must refuse deletion before touching files.
            _ratingReadError = error;
            _logger.Warning(error, "The passive rating index is unavailable.");
        }
        GetThreadFavoriteSnapshot();
    }

    private static bool MetadataReadError(Exception error)
        => error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException;
}
