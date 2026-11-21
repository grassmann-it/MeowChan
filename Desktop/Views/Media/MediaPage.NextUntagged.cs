using System;
using Avalonia.Controls;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;
using MeowChan.Gallery;

namespace MeowChan;

public sealed partial class MediaPage
{
    /// <summary>
    /// "Next untagged medium" (media.next-untagged, no default key): selects the next shown tile after the current one that
    /// lacks media tags (<see cref="MediaLibraryUserData.LacksMediaTags"/>, the rule of the "untagged" filter), wrapping to the start.
    /// It only moves the selection; nothing is changed, filtered or tagged. Unknown tags are never read as "untagged".
    /// </summary>
    internal void SelectNextUntagged()
    {
        if (_lifetime.IsCancellationRequested || _building != null || _selectionBusy) { ShowSelectionNote(Texts.T("media.selection.reason.building")); return; }
        if (_userData.TagSnapshot == null) { ShowSelectionNote(Texts.T("media.nextUntagged.unknown")); return; }
        int count = _grid.ItemCount;
        int start = _grid.Selection.AnchorIndex is var at && at >= 0 && at < count ? at : _grid.SelectedIndex;
        for (int step = 1; step <= count; step++)
        {
            int index = ((start < 0 ? -1 : start) + step) % count;
            if (_grid.Items[index] is not MediaLibraryTileRow row || row.Entry.Presence != MediaLibraryPresence.Present
                || !_userData.LacksMediaTags(row.Entry)) continue;
            ShowSelectionNote(null);
            _grid.SelectedItem = row;
            _grid.ScrollIntoView(index);
            _grid.Focus();
            return;
        }
        ShowSelectionNote(Texts.T("media.nextUntagged.none"));
    }
}
