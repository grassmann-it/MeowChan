using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using MeowChan.Desktop.Controls;
using Ursa.Controls;

namespace MeowChan;

// The context menu of the filter chips (applied filters and draft chips): right-click, Menu key, Shift+F10 on a chip. The same chip menu
// as the media page (FilterChipMenu, opened by ContextMenuHost); a tag chip gets it from WorkspaceChips.CreateTermChip, any other chip here.
public partial class ArchivePage
{
    /// <summary>
    /// What this page adds to a chip's menu: copying the chip's text, an own tag's name, and (for an applied filter) "Reset all", which is
    /// the reset of the "Reset all" link behind the chips. An applied chip changes the search, which waits while one runs.
    /// </summary>
    private FilterChipMenuOptions ChipMenuOptions(string label, string? tagName, bool applied) => new()
    {
        TagName = tagName,
        CopyText = () => _ = CopyChipTextAsync(this, label),
        CopyName = tagName == null ? null : () => _ = CopyChipTextAsync(this, tagName),
        ResetAll = applied ? () => OnResetArchiveFiltersClick(_appliedResetAll, new RoutedEventArgs()) : null,
        IsAvailable = applied ? () => !_busy && CanRead : null,
        IsSuppressed = () => _ended
    };

    /// <summary>
    /// Gives a chip that has no menu yet its menu. <paramref name="tagName"/> marks an own-tag chip; <paramref name="toggle"/> is the
    /// draft chip's switch between include and exclude (an applied chip has none). An applied chip removes its filter by a new search.
    /// </summary>
    private ContextMenuHost AttachFilterChipMenu(ClosableTag chip, Action remove, string? tagName = null, bool excluded = false, Action? toggle = null,
        bool applied = false)
    {
        string label = FilterTagLabel(chip);
        FilterChipMenuOptions options = ChipMenuOptions(label, tagName, applied);
        return WorkspaceChips.AttachMenu(chip, () => options.IsSuppressed!() ? null
            : FilterChipMenu.Compose(label, tagName, excluded, toggle, remove, options, options.IsAvailable?.Invoke() ?? true));
    }

    private async System.Threading.Tasks.Task CopyChipTextAsync(Control owner, string text)
    {
        await MenuClipboard.CopyAsync(owner, text, message => { if (!_ended) ShowTagFilterError(message); });
    }
}
