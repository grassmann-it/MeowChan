using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using MeowChan.Desktop.Localization;

namespace MeowChan.Desktop;

internal sealed partial record ArchiveThreadRow
{
    // Value-typed state only: the record's generated equality must not start comparing arrays by reference.
    // Tag names cannot contain line breaks, so '\n' separates the sorted names in the key.
    private string _sortedTagKey = string.Empty;
    private bool _tagsAvailable = true;

    /// <summary>Own thread tags as one display line; empty when the thread has none or tags are unavailable.</summary>
    public string TagsText { get; private set; } = string.Empty;
    public bool HasTags => TagsText.Length > 0;

    /// <summary>Own thread tags for the table column: sorted names; an unobtrusive dash while tags cannot be read.</summary>
    public string TagColumnText => !_tagsAvailable ? "—" : _sortedTagKey.Replace("\n", ", ");
    /// <summary>Full list for the cell tooltip; null (no tooltip) for a thread without tags.</summary>
    public string? TagColumnTip => !_tagsAvailable ? Texts.T("tags.filter.unavailable")
        : _sortedTagKey.Length > 0 ? TagColumnText : null;

    /// <summary>
    /// Sets the thread's own tags. <paramref name="available"/> is false while the tag store cannot be read; the
    /// row then shows no tags at all rather than the last known ones.
    /// </summary>
    internal void SetTags(IReadOnlyList<string>? names, bool available = true)
    {
        if (!available) names = null;
        string text = names is { Count: > 0 } ? Texts.T("tags.rowLabel", string.Join(", ", names)) : string.Empty;
        if (text != TagsText)
        {
            TagsText = text;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TagsText)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasTags)));
        }
        string key = names == null ? string.Empty : string.Join('\n', names.Order(TagNameComparer));
        if (available == _tagsAvailable && key == _sortedTagKey) return;
        _tagsAvailable = available;
        _sortedTagKey = key;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TagColumnText)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TagColumnTip)));
    }

    private static StringComparer TagNameComparer => StringComparer.Create(Texts.Culture, ignoreCase: true);

    /// <summary>
    /// Table order for the tag column: by the sorted tag names, element by element; threads without tags (or with
    /// unreadable tags) tie with each other and rank after every tagged thread. Equal keys compare equal, so the
    /// grid's stable sort keeps their list order. A descending sort reverses this whole order.
    /// </summary>
    internal sealed class TagColumnComparer : IComparer
    {
        public static readonly TagColumnComparer Instance = new();

        public int Compare(object? x, object? y)
        {
            string[] left = Names(x), right = Names(y);
            if (left.Length == 0 || right.Length == 0) return (left.Length == 0 ? 1 : 0) - (right.Length == 0 ? 1 : 0);
            StringComparer names = TagNameComparer;
            for (int index = 0; index < Math.Min(left.Length, right.Length); index++)
                if (names.Compare(left[index], right[index]) is int order and not 0) return order;
            return left.Length.CompareTo(right.Length);
        }

        private static string[] Names(object? row) => row is ArchiveThreadRow { _sortedTagKey.Length: > 0 } tagged
            ? tagged._sortedTagKey.Split('\n') : Array.Empty<string>();
    }
}
