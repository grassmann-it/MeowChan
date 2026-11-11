using System;
using System.Collections.Generic;
using System.Linq;

namespace MeowChan.Gallery;

/// <summary>
/// Where the Medien page stood when "show in thread" left it: the tile of the opened occurrence, the selection and
/// the tile at the top of the view with its offset. Transient and never saved; filters, order and the random seed
/// stay in the page itself, which is hidden, not rebuilt, while the archive is in front.
/// </summary>
internal sealed record MediaLibraryReturnPoint(MediaLibraryTileKey Target, MediaLibraryEntry Occurrence,
    IReadOnlyList<MediaLibraryTileKey> Selected, MediaLibraryTileKey? AnchorTile, double AnchorOffset);

/// <summary>The return point matched against the tiles shown now.</summary>
/// <param name="Target">Position of the opened tile, -1 when it is no longer shown.</param>
/// <param name="Anchor">Position to scroll back to: the former top tile, else the opened tile, else -1 (leave the view).</param>
internal readonly record struct MediaLibraryReturnMatch(int Target, IReadOnlyList<int> Selected, int Anchor, double AnchorOffset);

/// <summary>Pure rules for the way back from a thread to the Medien page. No IO, no UI.</summary>
internal static class MediaLibraryReturn
{
    /// <param name="occurrence">The occurrence whose thread is opened; for a tile of equal bytes it may be any of them.</param>
    internal static MediaLibraryReturnPoint Capture(MediaLibraryEntry occurrence, IEnumerable<MediaLibraryTile> selected,
        MediaLibraryTile? anchorTile, double anchorOffset)
    {
        ArgumentNullException.ThrowIfNull(occurrence);
        ArgumentNullException.ThrowIfNull(selected);
        return new(MediaLibraryTileKey.Of(occurrence), occurrence, selected.Select(MediaLibraryTileKey.Of).ToArray(),
            anchorTile == null ? null : MediaLibraryTileKey.Of(anchorTile), anchorOffset);
    }

    /// <summary>
    /// Finds the tiles again by identity, not by position: a rebuild or a refreshed tag can move them. A tile that is
    /// gone is reported as -1 and never replaced by whatever now stands at its old position.
    /// </summary>
    internal static MediaLibraryReturnMatch Locate(MediaLibraryReturnPoint point, IReadOnlyList<MediaLibraryTile> tiles)
    {
        ArgumentNullException.ThrowIfNull(point);
        ArgumentNullException.ThrowIfNull(tiles);
        int IndexOf(MediaLibraryTileKey key)
        {
            for (int index = 0; index < tiles.Count; index++) if (MediaLibraryTileKey.Of(tiles[index]) == key) return index;
            return -1;
        }
        int target = IndexOf(point.Target);
        int anchor = point.AnchorTile is { } top ? IndexOf(top) : -1;
        double offset = point.AnchorOffset;
        if (anchor < 0 && target >= 0) { anchor = target; offset = 0; }
        return new(target, MediaLibrarySelection.Restore(point.Selected, tiles), anchor, offset);
    }
}
