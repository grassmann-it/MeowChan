using System;
using System.Collections.Generic;
using MeowChan.Archive;
using MeowChan.Gallery;
using MeowChan.Tags;

namespace MeowChan.Desktop;

/// <summary>
/// What the quick tag popover (TP3) knows about the medium it opened on: the <see cref="TagContext"/> of a media page tile or an
/// archive/reader file. The suggestions themselves come from the gallery panel's adapter (<see cref="GalleryTagPanelSuggestions"/>),
/// so the panel and the popover rank the same way; <see cref="QuickTagSettings.PopoverOptions"/> builds it with the saved limits.
/// </summary>
internal static class QuickTagPopoverContext
{
    /// <summary>
    /// The context of a media page tile: its thread (and title), file name and bytes, and the tag subjects of the shown tiles
    /// around it. <paramref name="selection"/> drops the bytes (a selection has no single file).
    /// </summary>
    internal static TagContext ContextOf(MediaLibraryEntry entry, IReadOnlyList<MediaLibraryEntry>? shown, int index, bool selection = false)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return new TagContext
        {
            Thread = entry.IsCatalogMedium ? null : entry.Thread,
            Sha256 = selection ? null : entry.Sha256,
            FileName = entry.Name,
            TitleTexts = new[] { entry.ThreadTitle },
            Neighbours = shown == null ? TagNeighbourhood.None : Neighbourhood(shown, index, item => MediaTagSubjects.Of(item))
        };
    }

    /// <summary>The context of an archive or reader file: its thread, title, file name and bytes, and the visible files around it.</summary>
    internal static TagContext ContextOf(GalleryEntry entry, IReadOnlyList<GalleryEntry>? shown, string? threadTitle)
    {
        ArgumentNullException.ThrowIfNull(entry);
        int index = shown == null ? -1 : IndexOf(shown, entry);
        return new TagContext
        {
            Thread = BooruMediaIdentity.IsBooruThread(entry.Key.Thread) ? null : entry.Key.Thread,
            Sha256 = entry.Asset.Sha256,
            FileName = entry.Asset.OriginalFileName ?? System.IO.Path.GetFileName(entry.Asset.Path),
            TitleTexts = new[] { threadTitle, entry.Post?.Subject },
            Neighbours = shown == null ? TagNeighbourhood.None : Neighbourhood(shown, index, item => MediaTagSubjects.Of(item))
        };
    }

    /// <summary>
    /// The tag subjects of the list around <paramref name="index"/> (at most <paramref name="radius"/> each way), frozen into an
    /// array, so the ranking may read it on any thread while the list changes.
    /// </summary>
    internal static TagNeighbourhood Neighbourhood<T>(IReadOnlyList<T> list, int index, Func<T, MediaTagSubject?> subjectOf,
        int radius = TagContextOptions.DefaultNeighbourScan)
    {
        ArgumentNullException.ThrowIfNull(list);
        ArgumentNullException.ThrowIfNull(subjectOf);
        if (index < 0 || index >= list.Count) return TagNeighbourhood.None;
        int from = Math.Max(0, index - radius), to = Math.Min(list.Count, index + radius + 1);
        MediaTagSubject?[] subjects = new MediaTagSubject?[to - from];
        for (int at = from; at < to; at++) subjects[at - from] = at == index ? null : subjectOf(list[at]);
        return new TagNeighbourhood(subjects.Length, index - from, position => subjects[position]);
    }

    private static int IndexOf(IReadOnlyList<GalleryEntry> list, GalleryEntry entry)
    {
        for (int index = 0; index < list.Count; index++)
            if (ReferenceEquals(list[index], entry) || list[index].EntryKey == entry.EntryKey) return index;
        return -1;
    }
}
