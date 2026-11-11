using MeowChan.Desktop.Localization;
using MeowChan.Gallery;

namespace MeowChan.Desktop;

/// <summary>Wording for the favorite and own state shown on the Medien page.</summary>
internal static class MediaLibraryStateText
{
    /// <summary>Favorite state over all saved occurrences of one tile's bytes; unreadable favorites read "unknown", never "none".</summary>
    internal static string Favorite(MediaLibraryFavoriteState state) => state switch
    {
        MediaLibraryFavoriteState.All => Texts.T("media.group.favorite.all"),
        MediaLibraryFavoriteState.Some => Texts.T("media.group.favorite.some"),
        MediaLibraryFavoriteState.None => Texts.T("media.group.favorite.none"),
        _ => Texts.T("media.group.favorite.unknown")
    };
}
