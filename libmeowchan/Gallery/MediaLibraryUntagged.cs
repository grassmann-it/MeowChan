using System;
using System.Collections.Generic;

namespace MeowChan.Gallery;

/// <summary>
/// The media page's "N without media tags" count. Which occurrences of one index lack media tags is decided once per index and
/// tag state by <see cref="MediaLibraryUserData.LacksMediaTags"/>, the same rule as the filter, so recounting after every filter
/// change is one lookup per passing occurrence. No IO.
/// </summary>
internal sealed class MediaLibraryUntagged
{
    // Untagged occurrences (by reference: the index's own entries) and their bytes identity.
    private readonly Dictionary<MediaLibraryEntry, string?> _untagged = new(ReferenceEqualityComparer.Instance);

    private MediaLibraryUntagged(IReadOnlyList<MediaLibraryEntry> entries, MediaLibraryUserData data)
    {
        Entries = entries;
        Data = data;
        foreach (MediaLibraryEntry entry in entries)
            if (data.LacksMediaTags(entry)) _untagged[entry] = MediaLibraryGrouping.ByteKey(entry);
    }

    internal IReadOnlyList<MediaLibraryEntry> Entries { get; }
    internal MediaLibraryUserData Data { get; }

    /// <summary>Null when tags are unavailable: unknown is never counted as untagged.</summary>
    internal static MediaLibraryUntagged? Of(IReadOnlyList<MediaLibraryEntry> entries, MediaLibraryUserData data)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(data);
        return data.TagSnapshot == null ? null : new MediaLibraryUntagged(entries, data);
    }

    /// <summary>True when this count was made from exactly these index entries and these store states.</summary>
    internal bool IsFor(IReadOnlyList<MediaLibraryEntry> entries, MediaLibraryUserData data) =>
        ReferenceEquals(Entries, entries) && ReferenceEquals(Data, data);

    /// <summary>
    /// Tiles the page would show if "without media tags" were added to the filter that let <paramref name="passing"/> through:
    /// equal bytes are one tile, shown when any of its occurrences lacks media tags (as <see cref="MediaLibraryGrouping.Build"/>).
    /// </summary>
    internal int CountTiles(IEnumerable<MediaLibraryEntry> passing)
    {
        ArgumentNullException.ThrowIfNull(passing);
        HashSet<string> bytes = new(StringComparer.Ordinal);
        int count = 0;
        foreach (MediaLibraryEntry entry in passing)
            if (_untagged.TryGetValue(entry, out string? key) && (key == null || bytes.Add(key))) count++;
        return count;
    }
}
