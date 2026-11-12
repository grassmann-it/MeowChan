using System;
using System.Collections.Generic;
using System.Linq;

namespace MeowChan.Gallery;

/// <summary>Favorite state across all saved occurrences of one tile's bytes. Unknown: favorites could not be read.</summary>
internal enum MediaLibraryFavoriteState { Unknown, None, Some, All }

/// <summary>
/// One tile of the media page: the occurrence the tile shows and every occurrence of the same bytes in
/// the index. Several occurrences exist only for saved files with equal SHA-256; a file without a hash
/// (captured but never saved) is always a tile of its own. The tile carries no tags, favorites or
/// ratings: those stay with each occurrence and are read per occurrence. Only the favorite summary
/// <paramref name="Favorite"/> is derived over all occurrences, also those a filter hid.
/// </summary>
internal sealed record MediaLibraryTile(MediaLibraryEntry Entry, IReadOnlyList<MediaLibraryEntry> Occurrences,
    MediaLibraryFavoriteState Favorite = MediaLibraryFavoriteState.Unknown)
{
    /// <summary>Distinct threads over all occurrences, not only the ones that passed a filter.</summary>
    internal int ThreadCount => Occurrences.Select(entry => entry.Thread).Distinct().Count();
    /// <summary>Distinct downloaded provider posts (Danbooru) among the occurrences; they are no chan threads.</summary>
    internal int ProviderPostCount => Occurrences.Where(entry => entry.BooruPostId != null).Select(entry => entry.Thread).Distinct().Count();
    /// <summary>Distinct archived threads among the occurrences, without provider posts.</summary>
    internal int ArchivedThreadCount => ThreadCount - ProviderPostCount;
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
    /// <paramref name="isFavorite"/> (may throw <see cref="MediaLibraryFilterException"/> when favorites are unavailable)
    /// gives each tile its favorite state over all those occurrences; without it, or when it throws, the state is unknown.
    /// </summary>
    internal static IReadOnlyList<MediaLibraryTile> Build(IEnumerable<MediaLibraryEntry> all, IEnumerable<MediaLibraryEntry> passing,
        MediaLibrarySort sort, bool includeMissing, int seed = 0, Func<MediaLibraryEntry, bool>? isFavorite = null)
    {
        ArgumentNullException.ThrowIfNull(all);
        ArgumentNullException.ThrowIfNull(passing);
        ILookup<string, MediaLibraryEntry> byHash = all.Where(HasHash).ToLookup(entry => ByteKey(entry)!, StringComparer.Ordinal);
        HashSet<string> seen = new(StringComparer.Ordinal);
        List<MediaLibraryTile> tiles = new();
        bool favoritesReadable = isFavorite != null;
        foreach (MediaLibraryEntry entry in MediaLibraryView.Select(passing, sort, includeMissing, seed))
        {
            if (ByteKey(entry) is not { } key)
            {
                tiles.Add(new MediaLibraryTile(entry, new[] { entry }, FavoriteOf(new[] { entry }, isFavorite, ref favoritesReadable)));
                continue;
            }
            if (!seen.Add(key)) continue;
            // The shown entry is always part of the list, even if the caller's "all" left it out.
            IEnumerable<MediaLibraryEntry> group = byHash[key];
            MediaLibraryEntry[] occurrences = MediaLibraryView.Select(group.Contains(entry) ? group : group.Append(entry),
                MediaLibrarySort.ByThread, includeMissing: true).ToArray();
            tiles.Add(new MediaLibraryTile(entry, occurrences, FavoriteOf(occurrences, isFavorite, ref favoritesReadable)));
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
            if (ByteKey(entry) is not { } key || hashes.Add(key)) count++;
        }
        return count;
    }

    /// <summary>
    /// Bytes identity of an occurrence, or null when it cannot be proven: only saved files with a hash and a
    /// length group. Hash case does not matter; the same hash with another length is another group, and a
    /// missing attachment never groups with anything.
    /// </summary>
    internal static string? ByteKey(MediaLibraryEntry entry) =>
        entry.Presence == MediaLibraryPresence.Present && !string.IsNullOrEmpty(entry.Sha256) && entry.Length is long length
            ? entry.Sha256!.ToLowerInvariant() + ":" + length : null;

    internal static bool HasHash(MediaLibraryEntry entry) => ByteKey(entry) != null;

    // Unreadable favorites are "unknown", never "none": once one read fails the rest are not tried again.
    private static MediaLibraryFavoriteState FavoriteOf(IReadOnlyList<MediaLibraryEntry> occurrences, Func<MediaLibraryEntry, bool>? isFavorite, ref bool readable)
    {
        if (!readable || isFavorite == null) return MediaLibraryFavoriteState.Unknown;
        try
        {
            int favorites = occurrences.Count(isFavorite);
            return favorites == 0 ? MediaLibraryFavoriteState.None : favorites == occurrences.Count ? MediaLibraryFavoriteState.All : MediaLibraryFavoriteState.Some;
        }
        catch (MediaLibraryFilterException) { readable = false; return MediaLibraryFavoriteState.Unknown; }
    }
}
