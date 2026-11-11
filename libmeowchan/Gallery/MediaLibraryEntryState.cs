using System;
using System.Collections.Generic;
using System.Linq;
using MeowChan.Tags;

namespace MeowChan.Gallery;

/// <summary>Three-valued answer: an unavailable store or a file that cannot carry the state is Unknown, never No.</summary>
internal enum MediaLibraryFlag { Unknown, No, Yes }

/// <summary>
/// What the profile itself says about one media occurrence, read from the very stores the gallery and
/// the archive use. Favorite, user rating and tags stay separate facts; thread tags are listed beside
/// the media tags and are never merged into them (no inheritance). A provider rating is not part of this.
/// </summary>
internal sealed record MediaLibraryEntryState(MediaLibraryFlag Favorite, bool RatingKnown, int? UserRating,
    bool TagsKnown, IReadOnlyList<string> MediaTags, IReadOnlyList<string> ThreadTags, PrivateMediaTagTarget? TagTarget)
{
    /// <summary>Media tags can only be changed for a saved file bound to a captured post version.</summary>
    internal bool CanEditMediaTags => TagsKnown && TagTarget != null;

    internal static MediaLibraryEntryState Of(MediaLibraryEntry entry, MediaLibraryUserData data)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(data);
        bool keyed = MediaLibraryFilterEngine.AssetKey(entry) != null;
        MediaLibraryFlag favorite = !keyed || !data.FavoritesAvailable ? MediaLibraryFlag.Unknown
            : data.IsFavorite(entry) ? MediaLibraryFlag.Yes : MediaLibraryFlag.No;
        bool ratingKnown = keyed && data.RatingsAvailable;
        PrivateMediaTagTarget? target = MediaLibraryFilterEngine.TagTarget(entry);
        PrivateTagStoreSnapshot? tags = data.TagSnapshot;
        return new MediaLibraryEntryState(favorite, ratingKnown, ratingKnown ? data.RatingOf(entry) : null,
            tags != null,
            tags == null || target == null ? Array.Empty<string>() : tags.TagsOf(target).Select(tag => tag.Name.Display).ToArray(),
            tags == null ? Array.Empty<string>() : tags.TagsOf(entry.Thread).Select(tag => tag.Name.Display).ToArray(),
            target);
    }
}
