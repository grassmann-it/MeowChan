using System.Collections.Generic;
using MeowChan.Archive;
using MeowChan.Gallery;

namespace MeowChan;

/// <summary>Thread positions for the navigable files in a frozen gallery.</summary>
internal sealed class ThreadGalleryPositions
{
    private readonly int?[] _numbers;

    private ThreadGalleryPositions(int?[] numbers, int count, int unavailable)
    {
        _numbers = numbers;
        Count = count;
        Unavailable = unavailable;
    }

    internal int Count { get; }
    internal int Unavailable { get; }
    internal int? NumberAt(int index) => _numbers[index];

    internal static ThreadGalleryPositions? Create(ThreadMediaPositions media, IReadOnlyList<GalleryEntry> entries, bool capturedOrder = true)
    {
        if (media.Count == 0) return null; // A media-only thread can still have saved files.
        int?[] numbers = new int?[entries.Count];
        HashSet<int> available = new();
        for (int index = 0; index < entries.Count; index++)
        {
            GalleryEntry entry = entries[index];
            if (media.NumberOf(entry.Post?.PostId, entry.Attachment?.Position) is int number && available.Add(number))
                numbers[index] = capturedOrder ? number : available.Count;
        }
        return new ThreadGalleryPositions(numbers, media.Count, media.Count - available.Count);
    }

    internal static IReadOnlyDictionary<int, int> RankMediaView(ThreadMediaPositions media, IReadOnlyList<MediaLibraryEntry> shown)
    {
        Dictionary<int, int> ranks = new();
        foreach (MediaLibraryEntry entry in shown)
            if (entry.Presence == MediaLibraryPresence.Present && media.NumberOf(entry.PostId, entry.Position) is int slot)
                ranks.TryAdd(slot, ranks.Count + 1);
        return ranks;
    }

    internal static ThreadGalleryPositions? ForMediaView(ThreadMediaPositions media, IReadOnlyDictionary<int, int> ranks,
        IReadOnlyList<MediaLibraryEntry> window, IReadOnlyList<GalleryEntry> entries, IEnumerable<MediaLibraryEntry> groupedOccurrences)
    {
        if (media.Count == 0) return null;
        int?[] numbers = new int?[entries.Count];
        HashSet<int> resolved = new();
        for (int index = 0; index < entries.Count; index++)
        {
            GalleryEntry entry = entries[index];
            if (media.NumberOf(entry.Post?.PostId, entry.Attachment?.Position) is int slot
                && ranks.TryGetValue(slot, out int rank) && resolved.Add(slot))
                numbers[index] = rank;
        }
        // Outside the navigation window, availability comes from the index. Inside it, resolution can also detect stale index entries.
        HashSet<int> lost = new();
        foreach (MediaLibraryEntry entry in window)
            if (media.NumberOf(entry.PostId, entry.Position) is int slot && ranks.ContainsKey(slot) && !resolved.Contains(slot))
                lost.Add(slot);
        // A saved attachment can share a tile with another occurrence of the same bytes.
        HashSet<int> grouped = new();
        foreach (MediaLibraryEntry entry in groupedOccurrences)
            if (entry.Presence == MediaLibraryPresence.Present
                && media.NumberOf(entry.PostId, entry.Position) is int slot && !ranks.ContainsKey(slot))
                grouped.Add(slot);
        return new ThreadGalleryPositions(numbers, media.Count, media.Count - ranks.Count + lost.Count - grouped.Count);
    }
}
