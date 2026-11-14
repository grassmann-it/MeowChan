using System;
using MeowChan.Desktop.Localization;

namespace MeowChan.Desktop.Controls;

/// <summary>
/// What a page adds to the menu of its filter chips beyond the chip itself: the entries a page can offer are present, the others are
/// null and stay out of the menu. A page supplies this once per chip; the menu is composed from it at every opening.
/// </summary>
internal sealed record FilterChipMenuOptions
{
    /// <summary>The own tag's name for a tag chip (enables <see cref="CopyName"/>), else null.</summary>
    internal string? TagName { get; init; }
    /// <summary>"Show only this": drops the other terms of the chip's field.</summary>
    internal Action? ShowOnly { get; init; }
    /// <summary>"Edit…": opens the filter picker on the chip's field.</summary>
    internal Action? Edit { get; init; }
    /// <summary>Copies the text of this chip (the archive's "Copy filter text").</summary>
    internal Action? CopyText { get; init; }
    /// <summary>Copies the page's whole filter as a query (the media page's "Copy filters as text").</summary>
    internal Action? CopyFilters { get; init; }
    /// <summary>Copies the own tag's name.</summary>
    internal Action? CopyName { get; init; }
    /// <summary>"Add all shown to the tagging queue…" (the media page).</summary>
    internal Action? QueueAll { get; init; }
    /// <summary>Asks, at every opening, why <see cref="QueueAll"/> cannot run now (nothing shown, no queue); null result: it can.</summary>
    internal Func<string?>? QueueBlocked { get; init; }
    /// <summary>"Reset all": every filter of the page.</summary>
    internal Action? ResetAll { get; init; }
    /// <summary>False while the page cannot change its filters (a search is running): the entries that change them say why.</summary>
    internal Func<bool>? IsAvailable { get; init; }
    /// <summary>True when the chip has no menu now (the page is gone).</summary>
    internal Func<bool>? IsSuppressed { get; init; }

    /// <summary>The menu of a chip that only switches, copies its text and removes.</summary>
    internal static FilterChipMenuOptions Plain { get; } = new();
}

/// <summary>
/// The context menu of a filter chip, one composition for the media page and the archive (the applied filters and the draft chips): for a
/// chip that can, the switch between include and exclude and "Show only this" and "Edit…", the copy entries (the chip's text, the whole
/// filter as text, an own tag's name), "Add all shown to the tagging queue…", then removing the filter and "Reset all" last. Pure
/// composition; the page supplies what each entry does, and <see cref="ContextMenuHost"/> opens it on a right-click, the Menu key and
/// Shift+F10 and gives the focus back. At most nine entries, in four sections.
/// </summary>
internal static class FilterChipMenu
{
    internal const string Toggle = "tags.filter-toggle";
    internal const string ShowOnly = "tags.filter-only";
    internal const string Edit = "media.filter.edit";
    internal const string CopyText = "archive.copy-filter";
    internal const string CopyFilters = "media.filter.copy-query";
    internal const string CopyName = "tags.copy-name";
    internal const string QueueAll = "media.queue.all";
    internal const string Remove = "archive.clear-filter";
    internal const string ResetAll = "media.filter.reset-all";

    /// <param name="chipText">The text the chip shows (the menu's accessible name).</param>
    /// <param name="valueName">What the switch names ("Switch to exclude: Videos"); the tag's name for a tag chip.</param>
    /// <param name="excluded">Whether the chip excludes its value (only meaningful with <paramref name="toggle"/>).</param>
    /// <param name="toggle">Switches between include and exclude; null where the chip cannot (a plain chip, or an applied archive filter: that one is changed by removing it).</param>
    internal static ContextMenuBuilder Compose(string chipText, string? valueName, bool excluded, Action? toggle, Action remove, FilterChipMenuOptions options,
        bool available = true)
    {
        ArgumentNullException.ThrowIfNull(remove);
        ArgumentNullException.ThrowIfNull(options);
        string? reason = available ? null : Texts.T("menu.unavailable.target");
        ContextMenuBuilder menu = new ContextMenuBuilder().Named(chipText);
        if (toggle != null)
            menu.Command(Toggle, toggle, available, reason, Texts.T(excluded ? "tags.filter.toInclude" : "tags.filter.toExclude", valueName ?? chipText));
        if (options.ShowOnly != null) menu.Command(ShowOnly, options.ShowOnly, available, reason);
        if (options.Edit != null) menu.Command(Edit, options.Edit);
        menu.Section();
        if (options.CopyText != null) menu.Command(CopyText, options.CopyText);
        if (options.TagName != null && options.CopyName != null) menu.Command(CopyName, options.CopyName);
        if (options.CopyFilters != null) menu.Command(CopyFilters, options.CopyFilters);
        menu.Section();
        if (options.QueueAll != null)
        {
            string? blocked = options.QueueBlocked?.Invoke();
            menu.Command(QueueAll, options.QueueAll, blocked == null, blocked);
        }
        menu.Section().Command(Remove, remove, available, reason);
        if (options.ResetAll != null) menu.Command(ResetAll, options.ResetAll, available, reason);
        return menu;
    }

    /// <summary>The menu of a chip of the archive search: the switch for a draft tag, the chip's text, the tag's name, and removing the filter.</summary>
    internal static ContextMenuBuilder Compose(string chipText, string? tagName, bool excluded, Action? toggle, Action copyText, Action? copyName,
        Action remove, bool available = true) =>
        Compose(chipText, tagName, excluded, toggle, remove, new FilterChipMenuOptions { TagName = tagName, CopyText = copyText, CopyName = copyName }, available);

    /// <summary>
    /// The menu of the chip row itself (a right-click beside the chips, or the Menu key on "+ Filter" and "Reset all"): the whole filter
    /// as text, then "Reset all".
    /// </summary>
    internal static ContextMenuBuilder ComposeRow(string name, Action copyFilters, Action resetAll) =>
        new ContextMenuBuilder().Named(name).Command(CopyFilters, copyFilters).Section().Command(ResetAll, resetAll);
}

/// <summary>
/// The context menu of a tag in the "Add tag…" popover: add it to the filter as an inclusion or as an exclusion (whatever the popover's
/// "Add as" choice says) and copy its name. Pure composition.
/// </summary>
internal static class TagPickerMenu
{
    internal const string Include = "tags.filter-include";
    internal const string Exclude = "tags.filter-exclude";
    internal const string CopyName = "tags.copy-name";

    internal static ContextMenuBuilder Compose(string tagName, Action include, Action exclude, Action copyName)
    {
        ArgumentNullException.ThrowIfNull(include);
        ArgumentNullException.ThrowIfNull(exclude);
        ArgumentNullException.ThrowIfNull(copyName);
        return new ContextMenuBuilder().Named(tagName).Command(Include, include).Command(Exclude, exclude).Section().Command(CopyName, copyName);
    }
}
