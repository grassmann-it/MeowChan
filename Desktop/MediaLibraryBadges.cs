using System;
using System.IO;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using MeowChan.Gallery;

namespace MeowChan.Desktop;

/// <summary>
/// MB1b (media page): what a media page tile tells the shared media badges (<see cref="MediaBadgeView"/>). Pure functions: the state of a tile
/// from its row, nothing drawn here. The caption parts of the Layout menu (<see cref="MediaTileCaption"/>) keep their meaning for the three
/// facts they were always named for (the tag count, the queue mark and the source), so a person who switched one off still sees it off; every
/// other badge (favourite, kind, missing file, the untagged mark) follows <see cref="MediaBadgeOptions.MediaPage"/> and the three limits alone.
/// The name and the thread line of the caption are caption only and never touch a badge.
/// </summary>
internal static class MediaLibraryBadges
{
    /// <summary>
    /// Every badge fact of a tile: favourite, kind, missing file, queue, own tags (or none yet) and the booru source. This is also what the
    /// tile's spoken sentence says; <see cref="Visible"/> takes from it what the Layout menu's caption parts hide from the picture.
    /// </summary>
    internal static MediaBadgeState ForRow(MediaLibraryTileRow row, bool queued)
    {
        int carried = row.TagChips.Count;
        return new MediaBadgeState(
            Favorite: IsFavorite(row),
            Kind: KindOf(row.Entry),
            TagCount: carried > 0 ? carried : row.IsUntagged ? 0 : null,
            Queued: queued,
            Missing: row.Entry.Presence == MediaLibraryPresence.Missing,
            Booru: row.IsProviderMedium);
    }

    /// <summary>
    /// What is drawn: the facts without the three the person switched off in the caption parts (tags, queue, source). The untagged mark is the
    /// tags' own absence and stays; the name and thread parts of the caption never touch a badge.
    /// </summary>
    internal static MediaBadgeState Visible(in MediaBadgeState full, MediaTileAppearance appearance) => full with
    {
        TagCount = full.TagCount is > 0 && !appearance.Shows(MediaTileCaption.TagCount) ? null : full.TagCount,
        Queued = full.Queued && appearance.Shows(MediaTileCaption.QueueMark),
        Booru = full.Booru && appearance.Shows(MediaTileCaption.Source)
    };

    /// <summary>
    /// Video, animated image (GIF by the file ending, as <see cref="MediaLibraryFilterEngine.KindOf"/> decides it) or still. Nothing is allocated:
    /// a recycled tile asks on every refresh, and Path.GetExtension of a string would build a new one each time.
    /// </summary>
    internal static MediaBadgeKind KindOf(MediaLibraryEntry entry) => entry.Kind switch
    {
        SavedMediaKind.Video => MediaBadgeKind.Video,
        SavedMediaKind.Image when MemoryExtensions.Equals(Path.GetExtension(entry.Name.AsSpan()), ".gif", StringComparison.OrdinalIgnoreCase) => MediaBadgeKind.Gif,
        _ => MediaBadgeKind.Image
    };

    /// <summary>A single occurrence is a favourite when its own state says so; a grouped tile when one of its occurrences is.</summary>
    internal static bool IsFavorite(MediaLibraryTileRow row) => row.Tile.IsGroup
        ? row.Tile.Favorite is MediaLibraryFavoriteState.All or MediaLibraryFavoriteState.Some
        : row.State?.Favorite == MediaLibraryFlag.Yes;

    /// <summary>
    /// How far the badges of the top-right corner reach into the area they are placed on (the slot of a media tile) from its right edge, plus a gap;
    /// 0 when the corner holds none. The selection check is arranged beside them, never on top.
    /// </summary>
    internal static double TopRightExtent(MediaBadgePlan plan, double areaWidth)
    {
        double reach = 0;
        foreach (PlacedBadge placed in plan.Badges)
            if (placed.Badge.Corner == MediaBadgeCorner.TopRight) reach = Math.Max(reach, areaWidth - placed.Bounds.Left);
        return reach > 0 ? reach + CornerGap : 0;
    }

    /// <summary>The space the check mark keeps from the tile's top-right edge, and from a badge beside it (dip).</summary>
    internal const double CornerInset = 2, CornerGap = 2;
}
