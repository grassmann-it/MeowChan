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
}
