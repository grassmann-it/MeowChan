using System;
using System.Collections.Generic;
using MeowChan.Tags;

namespace MeowChan.Gallery;

/// <summary>
/// The sort keys of the media page that come from the profile's own data instead of the index: the number of tags an occurrence is found
/// by (<see cref="MediaLibrarySort.TagCount"/>) and its rating (<see cref="MediaLibrarySort.Rating"/>). Decided once per index and data
/// state, like <see cref="MediaLibraryUntagged"/>, so changing the filter or the direction never reads a store again; a new tag
/// snapshot (another revision) or another rating state needs a new instance (<see cref="IsFor"/>). No IO; safe to build and use from any thread.
/// <para>
/// Keys are per occurrence and then merged over the bytes group: a tile counts the <b>maximum</b> over all its occurrences, also those a
/// filter hid, so every occurrence of one tile has the same key and the shown one does not decide the place. Unknown is -1 and sorts last
/// in both directions: all tag counts when tags are unavailable, a rating when the store is unavailable or the occurrence is unrated
/// (or cannot be rated). An occurrence without any tag has the known count 0.
/// </para>
/// </summary>
internal sealed class MediaLibraryUserOrder
{
    internal const int Unknown = -1;

    private readonly int[] _tagCount;   // by position in Entries: maximum over the bytes group, Unknown without tags
    private readonly int[] _rating;     // by position in Entries: maximum over the bytes group, Unknown when unrated or unavailable
    private readonly object _lock = new();
    private Dictionary<MediaLibraryEntry, int>? _position;

    private MediaLibraryUserOrder(IReadOnlyList<MediaLibraryEntry> entries, MediaLibraryUserData data)
    {
        Entries = entries;
        Data = data;
        TagRevision = data.TagSnapshot?.Revision;
        _tagCount = Keys(entries, data.TagSnapshot == null ? null : entry => data.TagsOf(entry).Count);
        _rating = Keys(entries, data.RatingsAvailable ? entry => data.RatingOf(entry) ?? Unknown : null);
    }

    internal IReadOnlyList<MediaLibraryEntry> Entries { get; }
    internal MediaLibraryUserData Data { get; }

    /// <summary>Revision of the tag snapshot the tag counts were read from; null when tags were unavailable.</summary>
    internal long? TagRevision { get; }

    /// <summary>False when tags were unavailable: every tag count is unknown and the tag count sort keeps the by-thread order.</summary>
    internal bool TagsKnown => Data.TagSnapshot != null;

    /// <summary>False when ratings were unavailable: every rating is unknown and the rating sort keeps the by-thread order.</summary>
    internal bool RatingsKnown => Data.RatingsAvailable;

    /// <summary>Never null: unavailable data gives an order whose keys are all unknown.</summary>
    internal static MediaLibraryUserOrder Of(IReadOnlyList<MediaLibraryEntry> entries, MediaLibraryUserData data)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(data);
        return new MediaLibraryUserOrder(entries, data);
    }

    /// <summary>
    /// True when these keys were made from exactly these index entries and from the same tag state (the same snapshot, or one of equal
    /// revision, fingerprint and assignment counts) and the same rating list (by reference: the session publishes a new list on every change). A page keeps the instance until this is false, as it does for
    /// <see cref="MediaLibraryUntagged.IsFor"/>; favorites do not matter here.
    /// </summary>
    internal bool IsFor(IReadOnlyList<MediaLibraryEntry> entries, MediaLibraryUserData data)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (!ReferenceEquals(Entries, entries) || !ReferenceEquals(Data.RatingSource, data.RatingSource)) return false;
        PrivateTagStoreSnapshot? mine = Data.TagSnapshot, theirs = data.TagSnapshot;
        // Equal revision and fingerprint name one committed state; the sizes only guard against a placeholder fingerprint.
        return ReferenceEquals(mine, theirs)
            || (mine != null && theirs != null && mine.Revision == theirs.Revision && string.Equals(mine.Fingerprint, theirs.Fingerprint, StringComparison.Ordinal)
                && mine.Tags.Count == theirs.Tags.Count && mine.ThreadAssignments.Count == theirs.ThreadAssignments.Count
                && mine.MediaAssignments.Count == theirs.MediaAssignments.Count && mine.LocalAssignments.Count == theirs.LocalAssignments.Count);
    }

    /// <summary>The group maximum of the tag count, or <see cref="Unknown"/>. An entry that is not part of <see cref="Entries"/> is unknown.</summary>
    internal int TagCountOf(MediaLibraryEntry entry) => KeyOf(MediaLibrarySort.TagCount, entry);

    /// <summary>The group maximum of the rating, or <see cref="Unknown"/>. An entry that is not part of <see cref="Entries"/> is unknown.</summary>
    internal int RatingOf(MediaLibraryEntry entry) => KeyOf(MediaLibrarySort.Rating, entry);

    /// <summary>The key of <paramref name="sort"/> (tag count or rating) for an entry, or <see cref="Unknown"/>. Other sorts have no user key.</summary>
    internal int KeyOf(MediaLibrarySort sort, MediaLibraryEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        int[] keys = KeysOf(sort);
        Dictionary<MediaLibraryEntry, int> positions;
        lock (_lock)
        {
            if (_position == null)
            {
                _position = new(ReferenceEqualityComparer.Instance);
                for (int i = 0; i < Entries.Count; i++) _position.TryAdd(Entries[i], i);
            }
            positions = _position;
        }
        return positions.TryGetValue(entry, out int at) ? keys[at] : Unknown;
    }

    /// <summary>The key at a position of <see cref="Entries"/>; what an index uses without a lookup.</summary>
    internal int KeyAt(MediaLibrarySort sort, int position) => KeysOf(sort)[position];

    private int[] KeysOf(MediaLibrarySort sort) => sort switch
    {
        MediaLibrarySort.TagCount => _tagCount,
        MediaLibrarySort.Rating => _rating,
        _ => throw new ArgumentOutOfRangeException(nameof(sort), sort, "Only the tag count and the rating come from the profile's own data.")
    };

    // One value per occurrence (Unknown: none), then the maximum over equal bytes. An occurrence that cannot be proven equal stands alone.
    private static int[] Keys(IReadOnlyList<MediaLibraryEntry> entries, Func<MediaLibraryEntry, int>? read)
    {
        int[] keys = new int[entries.Count];
        Array.Fill(keys, Unknown);
        if (read == null) return keys;
        Dictionary<string, int> best = new(StringComparer.Ordinal);
        for (int i = 0; i < keys.Length; i++)
        {
            int value = Math.Max(read(entries[i]), Unknown);
            keys[i] = value;
            if (value > Unknown && MediaLibraryGrouping.ByteKey(entries[i]) is { } key && (!best.TryGetValue(key, out int seen) || value > seen)) best[key] = value;
        }
        for (int i = 0; i < keys.Length; i++)
            if (MediaLibraryGrouping.ByteKey(entries[i]) is { } key && best.TryGetValue(key, out int group)) keys[i] = group;
        return keys;
    }
}
