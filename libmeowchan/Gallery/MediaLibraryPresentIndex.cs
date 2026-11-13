using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;

namespace MeowChan.Gallery;

/// <summary>
/// Everything about one index (one <see cref="MediaLibrarySnapshot"/>'s entries) that does not depend on the filter, so the media
/// page decides it once per index instead of once per keystroke: the full order of every entry for each sort (the 9-key
/// comparison and the stable shuffle run once, not for every filter change), the bytes group of every entry with its occurrences
/// in thread order, and the tile counts of the unfiltered index. Filtering a library then costs one pass over a precomputed order.
/// <see cref="Build"/> gives exactly what <see cref="MediaLibraryGrouping.Build"/> gives for the same input. No IO; safe to build
/// and use from any thread.
/// </summary>
internal sealed class MediaLibraryPresentIndex
{
    private readonly IReadOnlyList<MediaLibraryEntry> _entries;
    private readonly Dictionary<MediaLibraryEntry, int> _position = new(ReferenceEqualityComparer.Instance);
    private readonly int[] _group;                       // group id of the entry at a position, -1 for an entry of its own
    private readonly MediaLibraryEntry[][] _groups;      // occurrences of each group, in thread order
    private readonly int _hashless, _hashlessPresent;
    private readonly object _lock = new();
    private int[]? _byThread, _newest;
    private int _randomSeed;
    private int[]? _random;

    internal MediaLibraryPresentIndex(IReadOnlyList<MediaLibraryEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        _entries = entries;
        for (int i = 0; i < entries.Count; i++) _position[entries[i]] = i;
        // Occurrences of equal bytes are listed in thread order, so the groups are made walking that order once.
        int[] byThread = Order(MediaLibrarySort.ByThread, 0);
        _byThread = byThread;
        _group = new int[entries.Count];
        Array.Fill(_group, -1);
        Dictionary<string, int> ids = new(StringComparer.Ordinal);
        List<List<MediaLibraryEntry>> lists = new();
        foreach (int position in byThread)
        {
            MediaLibraryEntry entry = entries[position];
            if (MediaLibraryGrouping.ByteKey(entry) is not { } key)
            {
                _hashless++;
                if (entry.Presence == MediaLibraryPresence.Present) _hashlessPresent++;
                continue;
            }
            if (!ids.TryGetValue(key, out int id)) { ids[key] = id = lists.Count; lists.Add(new List<MediaLibraryEntry>()); }
            lists[id].Add(entry);
            _group[position] = id;
        }
        _groups = new MediaLibraryEntry[lists.Count][];
        for (int i = 0; i < lists.Count; i++) _groups[i] = lists[i].ToArray();
    }

    internal IReadOnlyList<MediaLibraryEntry> Entries => _entries;

    /// <summary>Number of tiles the unfiltered index gives (<see cref="MediaLibraryGrouping.CountTiles"/>), without a pass over the entries.</summary>
    internal int CountTiles(bool includeMissing) => _groups.Length + (includeMissing ? _hashless : _hashlessPresent);

    /// <summary>Whether <paramref name="entries"/> is the list this index was made from, so the index may be used for it.</summary>
    internal bool IsFor(IReadOnlyList<MediaLibraryEntry> entries) => ReferenceEquals(_entries, entries);

    /// <summary>
    /// Tiles in page order, as <see cref="MediaLibraryGrouping.Build"/> gives them for this index's entries. <paramref name="passing"/>
    /// are the occurrences that passed the filter: null or the index's own list means all of them. Entries that are not part of this
    /// index are handled by the general path.
    /// </summary>
    internal IReadOnlyList<MediaLibraryTile> Build(IReadOnlyList<MediaLibraryEntry>? passing, MediaLibrarySort sort, bool includeMissing,
        int seed = 0, Func<MediaLibraryEntry, bool>? isFavorite = null, CancellationToken cancellationToken = default)
    {
        bool[]? mask = null;
        if (passing != null && !ReferenceEquals(passing, _entries))
        {
            mask = new bool[_entries.Count];
            foreach (MediaLibraryEntry entry in passing)
            {
                if (!_position.TryGetValue(entry, out int position))
                    return MediaLibraryGrouping.Build(_entries, passing, sort, includeMissing, seed, isFavorite);
                mask[position] = true;
            }
        }
        int[] order = Order(sort, seed);
        bool[] seen = new bool[_groups.Length];
        List<MediaLibraryTile> tiles = new();
        bool favoritesReadable = isFavorite != null;
        int visited = 0;
        foreach (int position in order)
        {
            if ((++visited & 0x3FFF) == 0) cancellationToken.ThrowIfCancellationRequested();
            if (mask != null && !mask[position]) continue;
            MediaLibraryEntry entry = _entries[position];
            if (!includeMissing && entry.Presence != MediaLibraryPresence.Present) continue;
            int group = _group[position];
            if (group < 0)
            {
                tiles.Add(new MediaLibraryTile(entry, new[] { entry }, MediaLibraryGrouping.FavoriteOf(new[] { entry }, isFavorite, ref favoritesReadable)));
                continue;
            }
            if (seen[group]) continue;
            seen[group] = true;
            MediaLibraryEntry[] occurrences = _groups[group];
            tiles.Add(new MediaLibraryTile(entry, occurrences, MediaLibraryGrouping.FavoriteOf(occurrences, isFavorite, ref favoritesReadable)));
        }
        return tiles;
    }

    // Positions of every entry in the order of a sort, missing ones included; kept for the sorts that do not depend on the seed.
    private int[] Order(MediaLibrarySort sort, int seed)
    {
        lock (_lock)
        {
            switch (sort)
            {
                case MediaLibrarySort.ByThread: return _byThread ??= Sorted(ByThread());
                case MediaLibrarySort.Newest: return _newest ??= Sorted(Newest());
                default:
                    if (_random == null || _randomSeed != seed) { _random = Sorted(Random(seed)); _randomSeed = seed; }
                    return _random;
            }
        }
    }

    private int[] Sorted(Comparison<int> comparison)
    {
        int[] order = new int[_entries.Count];
        for (int i = 0; i < order.Length; i++) order[i] = i;
        // The position is the last key: equal entries keep the order of the index, like the stable sort of the general path.
        Array.Sort(order, (a, b) =>
        {
            int result = comparison(a, b);
            return result != 0 ? result : a.CompareTo(b);
        });
        return order;
    }

    // Same keys as MediaLibraryView.Select(ByThread): threads by latest capture (newest first), then the thread, post media in post
    // order before saved files without a post, then position, name and asset.
    private Comparison<int> ByThread()
    {
        long[] when = new long[_entries.Count];
        int[] thread = new int[_entries.Count];
        Dictionary<ResourceId, int> ids = new();
        List<ResourceId> threads = new();
        for (int i = 0; i < when.Length; i++)
        {
            MediaLibraryEntry entry = _entries[i];
            when[i] = (entry.LastCapturedAt ?? entry.FirstSeenAt).UtcTicks;
            if (!ids.TryGetValue(entry.Thread, out int id)) { ids[entry.Thread] = id = threads.Count; threads.Add(entry.Thread); }
            thread[i] = id;
        }
        ResourceId[] sorted = threads.ToArray();
        Array.Sort(sorted, (a, b) =>
        {
            int result = Ordinal(a.Source, b.Source);
            if (result == 0) result = Ordinal(a.Board, b.Board);
            return result != 0 ? result : Ordinal(a.Thread, b.Thread);
        });
        int[] rank = new int[threads.Count];
        for (int i = 0; i < sorted.Length; i++) rank[ids[sorted[i]]] = i;
        return (a, b) =>
        {
            if (when[a] != when[b]) return when[a] > when[b] ? -1 : 1;
            if (thread[a] != thread[b] && rank[thread[a]] != rank[thread[b]]) return rank[thread[a]].CompareTo(rank[thread[b]]);
            MediaLibraryEntry x = _entries[a], y = _entries[b];
            int result = (x.PostOrdinal.HasValue ? 0 : 1).CompareTo(y.PostOrdinal.HasValue ? 0 : 1);
            if (result != 0) return result;
            result = (x.PostOrdinal ?? long.MaxValue).CompareTo(y.PostOrdinal ?? long.MaxValue);
            if (result != 0) return result;
            result = (x.Position ?? int.MaxValue).CompareTo(y.Position ?? int.MaxValue);
            if (result != 0) return result;
            result = Ordinal(x.Name, y.Name);
            return result != 0 ? result : Ordinal(x.AssetId, y.AssetId);
        };
    }

    // Same keys as MediaLibraryView.Select(Newest): post time, else capture time (newest first), later posts first, then thread and place.
    private Comparison<int> Newest()
    {
        long[] when = new long[_entries.Count];
        for (int i = 0; i < when.Length; i++)
        {
            MediaLibraryEntry entry = _entries[i];
            when[i] = (entry.PostedAt ?? entry.LastCapturedAt ?? entry.FirstSeenAt).UtcTicks;
        }
        return (a, b) =>
        {
            if (when[a] != when[b]) return when[a] > when[b] ? -1 : 1;
            MediaLibraryEntry x = _entries[a], y = _entries[b];
            int result = (y.PostOrdinal ?? -1).CompareTo(x.PostOrdinal ?? -1);
            if (result != 0) return result;
            result = Ordinal(x.Thread.Source, y.Thread.Source);
            if (result != 0) return result;
            result = Ordinal(x.Thread.Thread, y.Thread.Thread);
            if (result != 0) return result;
            result = (x.Position ?? int.MaxValue).CompareTo(y.Position ?? int.MaxValue);
            return result != 0 ? result : Ordinal(x.AssetId, y.AssetId);
        };
    }

    private Comparison<int> Random(int seed)
    {
        ulong[] key = new ulong[_entries.Count];
        for (int i = 0; i < key.Length; i++) key[i] = MediaLibraryView.StableKey(_entries[i], seed);
        return (a, b) => key[a] != key[b] ? key[a].CompareTo(key[b]) : Ordinal(_entries[a].AssetId, _entries[b].AssetId);
    }

    private static int Ordinal(string? a, string? b) => string.CompareOrdinal(a, b);
}
