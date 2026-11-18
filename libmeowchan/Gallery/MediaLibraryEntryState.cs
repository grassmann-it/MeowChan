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
    bool TagsKnown, IReadOnlyList<string> MediaTags, IReadOnlyList<string> ThreadTags, MediaTagSubject? TagSubject)
{
    /// <summary>Media tags can be changed for a saved file with a bound post version or a loose local occurrence.</summary>
    internal bool CanEditMediaTags => TagsKnown && TagSubject != null;

    /// <summary>
    /// The tags the media filter finds this occurrence by: its media tags, then its thread's tags it does not already carry, each in
    /// vocabulary order. The tile shows them as filter chips; they are the same tags as <see cref="MediaTags"/> and <see cref="ThreadTags"/>.
    /// </summary>
    internal IReadOnlyList<PrivateTagDefinition> CarriedTags { get; init; } = Array.Empty<PrivateTagDefinition>();

    internal static MediaLibraryEntryState Of(MediaLibraryEntry entry, MediaLibraryUserData data)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(data);
        bool keyed = MediaLibraryFilterEngine.AssetKey(entry) != null;
        MediaLibraryFlag favorite = !keyed || !data.FavoritesAvailable ? MediaLibraryFlag.Unknown
            : data.IsFavorite(entry) ? MediaLibraryFlag.Yes : MediaLibraryFlag.No;
        bool ratingKnown = keyed && data.RatingsAvailable;
        MediaTagSubject? target = MediaTagSubjects.Of(entry);
        PrivateTagStoreSnapshot? tags = data.TagSnapshot;
        IReadOnlyList<PrivateTagDefinition> mediaTags = tags == null || target == null ? Array.Empty<PrivateTagDefinition>() : tags.TagsOf(target);
        IReadOnlyList<PrivateTagDefinition> threadTags = tags == null ? Array.Empty<PrivateTagDefinition>() : tags.TagsOf(entry.Thread);
        return new MediaLibraryEntryState(favorite, ratingKnown, ratingKnown ? data.RatingOf(entry) : null,
            tags != null, mediaTags.Select(tag => tag.Name.Display).ToArray(), threadTags.Select(tag => tag.Name.Display).ToArray(), target)
        {
            CarriedTags = mediaTags.Concat(threadTags.Where(tag => !mediaTags.Any(own => own.Id == tag.Id))).ToArray()
        };
    }
}
