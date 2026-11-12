using System.Collections.Generic;
using System.Linq;
using MeowChan.Desktop.Localization;
using MeowChan.Gallery;

namespace MeowChan.Desktop;

/// <summary>
/// Wording for the favorite and own state shown on the Medien page. Favorite, user rating and tags are separate
/// lines; "unknown" (store unavailable, file cannot carry it) is worded differently from "no".
/// </summary>
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

    /// <summary>Short tile text: only what is known to be set, so a quiet tile stays quiet. A grouped tile leaves the favorite to its group line.</summary>
    internal static string Compact(MediaLibraryEntryState state, bool grouped = false)
    {
        List<string> parts = new();
        if (state.Favorite == MediaLibraryFlag.Yes && !grouped) parts.Add("★");
        if (state.UserRating is int rating) parts.Add(rating.ToString(Texts.Culture) + "/5");
        int tags = state.MediaTags.Count + state.ThreadTags.Count;
        if (tags > 0) parts.Add(Texts.T("media.state.tagCount", tags));
        return string.Join(" · ", parts);
    }

    /// <summary>Full description for tooltip and automation: every fact with its own wording.</summary>
    internal static string Description(MediaLibraryEntryState state) => string.Join(System.Environment.NewLine,
        Texts.T("media.state.favorite", state.Favorite switch
        {
            MediaLibraryFlag.Yes => Texts.T("media.state.yes"),
            MediaLibraryFlag.No => Texts.T("media.state.no"),
            _ => Texts.T("media.state.unknown")
        }),
        Texts.T("media.state.rating", !state.RatingKnown ? Texts.T("media.state.unknown")
            : state.UserRating is int rating ? Texts.T("media.state.ratingValue", rating) : Texts.T("media.state.unrated")),
        Texts.T("media.state.mediaTags", !state.TagsKnown ? Texts.T("media.state.unknown")
            : state.TagSubject == null ? Texts.T("media.state.notTaggable") : List(state.MediaTags)),
        Texts.T("media.state.threadTags", state.TagsKnown ? List(state.ThreadTags) : Texts.T("media.state.unknown")));

    private static string List(IReadOnlyList<string> names) =>
        names.Count == 0 ? Texts.T("media.state.noTags") : string.Join(", ", names);
}
