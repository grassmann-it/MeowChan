using System;
using System.Collections.Generic;
using System.Linq;

namespace MeowChan.Gallery;

/// <summary>
/// One tile of the media page: the occurrence the tile shows and every occurrence of the same bytes in
/// the index. Several occurrences exist only for saved files with equal SHA-256; a file without a hash
/// (captured but never saved) is always a tile of its own. The tile carries no tags, favorites or
/// ratings: those stay with each occurrence and are read per occurrence.
/// </summary>
internal sealed record MediaLibraryTile(MediaLibraryEntry Entry, IReadOnlyList<MediaLibraryEntry> Occurrences)
{
    /// <summary>Distinct threads over all occurrences, not only the ones that passed a filter.</summary>
    internal int ThreadCount => Occurrences.Select(entry => entry.Thread).Distinct().Count();
    internal bool IsGroup => Occurrences.Count > 1;
}

/// <summary>Pure query-time step of the media page that merges equal bytes into one tile. No IO, not part of the index.</summary>
internal static class MediaLibraryGrouping
{
    /// <summary>
    /// Tiles in page order. <paramref name="passing"/> are the occurrences that passed the filter; a tile is
    /// shown when at least one of its occurrences passed, so a group matches if any member matches. The
    /// tile is placed by its first passing occurrence in the chosen order, which is also the shown one.
    /// Its occurrence list holds every occurrence of <paramref name="all"/> with the same bytes, in thread order.
    /// </summary>
    internal static IReadOnlyList<MediaLibraryTile> Build(IEnumerable<MediaLibraryEntry> all, IEnumerable<MediaLibraryEntry> passing,
        MediaLibrarySort sort, bool includeMissing, int seed = 0)
    {
        ArgumentNullException.ThrowIfNull(all);
        ArgumentNullException.ThrowIfNull(passing);
        ILookup<string, MediaLibraryEntry> byHash = all.Where(HasHash).ToLookup(entry => entry.Sha256!, StringComparer.Ordinal);
        HashSet<string> seen = new(StringComparer.Ordinal);
        List<MediaLibraryTile> tiles = new();
        foreach (MediaLibraryEntry entry in MediaLibraryView.Select(passing, sort, includeMissing, seed))
        {
            if (!HasHash(entry)) { tiles.Add(new MediaLibraryTile(entry, new[] { entry })); continue; }
            if (!seen.Add(entry.Sha256!)) continue;
            // The shown entry is always part of the list, even if the caller's "all" left it out.
            IEnumerable<MediaLibraryEntry> group = byHash[entry.Sha256!];
            MediaLibraryEntry[] occurrences = MediaLibraryView.Select(group.Contains(entry) ? group : group.Append(entry),
                MediaLibrarySort.ByThread, includeMissing: true).ToArray();
            tiles.Add(new MediaLibraryTile(entry, occurrences));
        }
        return tiles;
    }

    /// <summary>Number of tiles the unfiltered index would give; for "x of y match" counts.</summary>
    internal static int CountTiles(IEnumerable<MediaLibraryEntry> all, bool includeMissing)
    {
        ArgumentNullException.ThrowIfNull(all);
        HashSet<string> hashes = new(StringComparer.Ordinal);
        int count = 0;
        foreach (MediaLibraryEntry entry in all)
        {
            if (entry.Presence != MediaLibraryPresence.Present && !includeMissing) continue;
            if (!HasHash(entry) || hashes.Add(entry.Sha256!)) count++;
        }
        return count;
    }

    // Only saved files carry a measured hash; a missing attachment is never grouped with anything.
    internal static bool HasHash(MediaLibraryEntry entry) => entry.Presence == MediaLibraryPresence.Present && !string.IsNullOrEmpty(entry.Sha256);
}
