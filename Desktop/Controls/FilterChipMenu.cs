using System;
using MeowChan.Desktop.Localization;

namespace MeowChan.Desktop.Controls;

/// <summary>
/// The context menu of a filter chip (the applied filters and the draft chips of the archive search): for a tag chip the switch between
/// include and exclude, the text of the chip, the tag's name, and removing the filter. Pure composition; the page supplies what each
/// entry does, and <see cref="ContextMenuHost"/> opens it on a right-click, the Menu key and Shift+F10.
/// </summary>
internal static class FilterChipMenu
{
    internal const string Toggle = "tags.filter-toggle";
    internal const string CopyText = "archive.copy-filter";
    internal const string CopyName = "tags.copy-name";
    internal const string Remove = "archive.clear-filter";

    /// <param name="chipText">The text the chip shows (what "Copy filter text" copies and the menu's accessible name).</param>
    /// <param name="tagName">The own tag's name for a tag chip, else null.</param>
    /// <param name="excluded">Whether the tag chip excludes the tag (only meaningful with <paramref name="toggle"/>).</param>
    /// <param name="toggle">Switches a draft tag between include and exclude; null where the chip cannot (an applied filter is changed by removing it).</param>
    /// <param name="available">False while the page cannot change its filters (a search is running).</param>
    internal static ContextMenuBuilder Compose(string chipText, string? tagName, bool excluded, Action? toggle, Action copyText, Action? copyName,
        Action remove, bool available = true)
    {
        ArgumentNullException.ThrowIfNull(copyText);
        ArgumentNullException.ThrowIfNull(remove);
        string? reason = available ? null : Texts.T("menu.unavailable.target");
        ContextMenuBuilder menu = new ContextMenuBuilder().Named(chipText);
        if (tagName != null && toggle != null)
            menu.Command(Toggle, toggle, available, reason, Texts.T(excluded ? "tags.filter.toInclude" : "tags.filter.toExclude", tagName)).Section();
        menu.Command(CopyText, copyText);
        if (tagName != null && copyName != null) menu.Command(CopyName, copyName);
        return menu.Section().Command(Remove, remove, available, reason);
    }
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
