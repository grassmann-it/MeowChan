using System;
using System.Collections.Generic;
using MeowChan.Desktop.Controls;

namespace MeowChan.Desktop;

/// <summary>What the media page does for the entries of a filter chip's menu; the chip's own value is passed when the entry runs.</summary>
internal sealed record MediaFilterChipActions(Action<MediaFilterTerm> ShowOnly, Action<MediaFilterField> Edit, Action<string> CopyTagName, Action CopyFilters,
    Action QueueAll, Func<string?> QueueBlocked, Action ResetAll, Func<bool> IsSuppressed);

/// <summary>
/// Which entries the menu of a chip of the media filter row offers, by what the chip is. Pure: the page supplies what each entry does
/// (<see cref="MediaFilterChipActions"/>), this decides which entries exist, so each kind of chip can be tested without a page:
/// <list type="bullet">
/// <item>a tag chip: switch, Show only this (when another tag is in the filter), Edit…, Copy tag name, Copy filters as text, queue, Remove filter, Reset all;</item>
/// <item>a type, source or board chip: the same without Copy tag name; Show only this when another value of the field is in the set;</item>
/// <item>every other chip (sound, rating, favourites, missing, without tags, name, thread, the "any tag" mode chip): no switch and no
/// Show only this; Edit… for the fields the picker shows, then the common entries.</item>
/// </list>
/// </summary>
internal static class MediaFilterChipMenu
{
    /// <param name="term">The chip's term; null for the tag mode chip ("any of the tags"), which is no term.</param>
    /// <param name="terms">The filter's terms now (decides whether "Show only this" has anything to drop).</param>
    /// <param name="tagName">The own tag's display name for a tag chip (what "Copy tag name" copies and the switch names).</param>
    internal static FilterChipMenuOptions Options(MediaFilterTerm? term, IReadOnlyList<MediaFilterTerm> terms, string? tagName, MediaFilterChipActions actions)
    {
        ArgumentNullException.ThrowIfNull(terms);
        ArgumentNullException.ThrowIfNull(actions);
        // Own tags and the provider's source tags (MP4) both have a name to copy.
        bool tag = term?.Field is MediaFilterField.Tag or MediaFilterField.SourceTag && tagName != null;
        return new FilterChipMenuOptions
        {
            TagName = tag ? tagName : null,
            ShowOnly = term != null && MediaFilterTermSet.CanShowOnly(terms, term) ? () => actions.ShowOnly(term) : null,
            Edit = term != null && IsPickerField(term.Field) ? () => actions.Edit(term.Field) : null,
            CopyName = tag ? () => actions.CopyTagName(tagName!) : null,
            CopyFilters = actions.CopyFilters,
            QueueAll = actions.QueueAll,
            QueueBlocked = actions.QueueBlocked,
            ResetAll = actions.ResetAll,
            IsSuppressed = actions.IsSuppressed
        };
    }

    /// <summary>Whether "Edit…" has something to open: the picker lists the tags, flags and filter fields up to "without media tags", not the name or the thread.</summary>
    internal static bool IsPickerField(MediaFilterField field) => FilterTermPicker.GroupOrder(field) <= FilterTermPicker.GroupOrder(MediaFilterField.Untagged);
}
