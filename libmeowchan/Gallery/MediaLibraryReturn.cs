using System;
using System.Collections.Generic;
using System.Linq;

namespace MeowChan.Gallery;

/// <summary>
/// Where the Medien page stood when "show in thread" left it: the tile of the opened occurrence, the selection and
/// the tile at the top of the view with its offset. Transient and never saved; filters, order and the random seed
/// stay in the page itself, which is hidden, not rebuilt, while the archive is in front.
/// </summary>
/// <param name="Target">Identity of the opened tile.</param>
/// <param name="Occurrence">The occurrence whose thread is opened; for a tile of equal bytes it may be any of them.</param>
/// <param name="Selected">Identities of the tiles selected when the page was left.</param>
/// <param name="AnchorTile">Identity of the tile at the top of the view, null when none is known.</param>
/// <param name="AnchorOffset">How far the anchor tile was scrolled past the top edge.</param>
internal sealed record MediaLibraryReturnPoint(MediaLibraryTileKey Target, MediaLibraryEntry Occurrence,
    IReadOnlyList<MediaLibraryTileKey> Selected, MediaLibraryTileKey? AnchorTile, double AnchorOffset);

/// <summary>The return point matched against the tiles shown now.</summary>
/// <param name="Target">Position of the opened tile, -1 when it is no longer shown.</param>
/// <param name="Selected">Positions of the previously selected tiles that are still shown, in page order.</param>
/// <param name="Anchor">Position to scroll back to: the former top tile, else the opened tile, else -1 (leave the view).</param>
/// <param name="AnchorOffset">How far the anchor tile was scrolled past the top edge; 0 when the opened tile stands in for a lost top tile.</param>
internal readonly record struct MediaLibraryReturnMatch(int Target, IReadOnlyList<int> Selected, int Anchor, double AnchorOffset);

/// <summary>Pure rules for the way back from a thread to the Medien page. No IO, no UI.</summary>
internal static class MediaLibraryReturn
{
    /// <summary>The return point for leaving the page: the opened tile, the selection and the view position.</summary>
    /// <param name="occurrence">The occurrence whose thread is opened; for a tile of equal bytes it may be any of them.</param>
    /// <param name="selected">The tiles selected when the page was left.</param>
    /// <param name="anchorTile">The tile at the top of the view, null when none is known.</param>
    /// <param name="anchorOffset">How far the anchor tile was scrolled past the top edge.</param>
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
    /// <param name="point">The return point captured when the page was left.</param>
    /// <param name="tiles">The tiles shown now.</param>
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

    /// <summary>
    /// Where a tile the page kept in view stands after the rows were rebuilt (the index, own state; MediaPage.ViewKeep.cs): the new position of
    /// <paramref name="before"/>[<paramref name="pivot"/>] by identity, else the new position of its nearest neighbour in the old order (after it
    /// first, then before it, out to <paramref name="reach"/> tiles each way) that is still shown, else -1 (the view stays where it is).
    /// </summary>
    internal static int LocateKept(IReadOnlyList<MediaLibraryTile> before, int pivot, IReadOnlyList<MediaLibraryTile> now, int reach = 512)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(now);
        if (pivot < 0 || pivot >= before.Count || now.Count == 0) return -1;
        MediaLibraryTileKey key = MediaLibraryTileKey.Of(before[pivot]);
        // Usually nothing moved: the same position holds the same tile, and no map of the new rows is needed.
        if (pivot < now.Count && MediaLibraryTileKey.Of(now[pivot]) == key) return pivot;
        Dictionary<MediaLibraryTileKey, int> positions = new(now.Count);
        for (int index = 0; index < now.Count; index++) positions.TryAdd(MediaLibraryTileKey.Of(now[index]), index);
        if (positions.TryGetValue(key, out int found)) return found;
        for (int distance = 1; distance <= reach; distance++)
            foreach (int neighbour in new[] { pivot + distance, pivot - distance })
                if (neighbour >= 0 && neighbour < before.Count && positions.TryGetValue(MediaLibraryTileKey.Of(before[neighbour]), out int kept)) return kept;
        return -1;
    }
}
