using System;
using Avalonia.Controls;
using MeowChan.Desktop.Controls;
using Ursa.Controls;

namespace MeowChan;

// The context menu of the filter chips (applied filters and draft chips): right-click, Menu key, Shift+F10 on a chip. Only the chips of this
// page are wired here; the chips of the media page are another surface.
public partial class ArchivePage
{
    /// <summary>
    /// Gives a chip its menu. <paramref name="tagName"/> marks an own-tag chip; <paramref name="toggle"/> is the draft chip's switch
    /// between include and exclude (an applied chip has none). An applied chip removes its filter by a new search, which waits while one runs.
    /// </summary>
    private ContextMenuHost AttachFilterChipMenu(ClosableTag chip, Action remove, string? tagName = null, bool excluded = false, Action? toggle = null,
        bool applied = false) =>
        new(chip, () => _ended ? null : FilterChipMenu.Compose(FilterTagLabel(chip), tagName, excluded, toggle,
            copyText: () => _ = CopyChipTextAsync(chip, FilterTagLabel(chip)),
            copyName: tagName == null ? null : () => _ = CopyChipTextAsync(chip, tagName),
            remove: remove, available: !applied || !_busy && CanRead));

    private async System.Threading.Tasks.Task CopyChipTextAsync(Control owner, string text)
    {
        await MenuClipboard.CopyAsync(owner, text, message => { if (!_ended) ShowTagFilterError(message); });
    }
}
