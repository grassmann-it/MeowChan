using System;
using MeowChan.Gallery;

namespace MeowChan.Desktop;

/// <summary>
/// The saved order of the media page: the criterion and its direction, kept as the names of <see cref="MediaLibrarySort"/> and
/// <see cref="MediaLibrarySortDirection"/> in the settings file (<c>MediaSort</c>, <c>MediaSortDirection</c>). Pure: no controls, no IO.
/// The random order is never saved (its seed belongs to one visit), so it reads back as the default order; an unknown or hand-edited
/// value does the same, and a direction only counts for a criterion that has one.
/// </summary>
internal static class MediaSortPreference
{
    internal const MediaLibrarySort DefaultSort = MediaLibrarySort.ByThread;
    internal const MediaLibrarySortDirection DefaultDirection = MediaLibrarySortDirection.Default;

    /// <summary>The criterion a saved name stands for; null, unknown names, numbers and the random order give the default.</summary>
    internal static MediaLibrarySort ParseSort(string? value)
    {
        string? name = value?.Trim();
        if (string.IsNullOrEmpty(name) || char.IsDigit(name[0]) || !Enum.TryParse(name, ignoreCase: true, out MediaLibrarySort sort)
            || !Enum.IsDefined(sort)) return DefaultSort;
        return sort == MediaLibrarySort.Random ? DefaultSort : sort;
    }

    internal static MediaLibrarySortDirection ParseDirection(string? value) =>
        string.Equals(value?.Trim(), nameof(MediaLibrarySortDirection.Reversed), StringComparison.OrdinalIgnoreCase)
            ? MediaLibrarySortDirection.Reversed : DefaultDirection;

    /// <summary>The settings spelling of a criterion; the random order is saved as the default order.</summary>
    internal static string Format(MediaLibrarySort sort) => (sort == MediaLibrarySort.Random || !Enum.IsDefined(sort) ? DefaultSort : sort).ToString();

    /// <summary>The settings spelling of a direction for a criterion: criteria without a direction (random) and the default order give Default.</summary>
    internal static string Format(MediaLibrarySort sort, MediaLibrarySortDirection direction) =>
        (MediaLibraryView.HasDirection(sort) && direction == MediaLibrarySortDirection.Reversed
            ? MediaLibrarySortDirection.Reversed : DefaultDirection).ToString();

    /// <summary>What the page starts with: the criterion and the direction (Default for a criterion that has none).</summary>
    internal static (MediaLibrarySort Sort, MediaLibrarySortDirection Direction) Restore(string? sort, string? direction)
    {
        MediaLibrarySort restored = ParseSort(sort);
        return (restored, MediaLibraryView.HasDirection(restored) ? ParseDirection(direction) : DefaultDirection);
    }
}
