using System;
using System.Threading.Tasks;
using Avalonia.Input;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.MediaActions;
using MeowChan.Gallery;

namespace MeowChan;

// Media page audit 16.11.2026: the page's own keys with the keyboard focus in the media panel. The grid handles R, T, Q, I, Ctrl+E, Ctrl+O, Ctrl+Shift+C,
// Ctrl+R and Shift+0 to 5 on its bubble route, and the panel is not on that route: after a click on the panel's star, its "Open in gallery" or its video
// controls the focus is inside the panel, and these keys did nothing there (the panel's video control even leaves them to the page: OwnerClaims). The panel's
// tunnel handler now takes them for the medium the panel shows (the way M, U and F already work there), through the same actions and gates as the grid's keys
// and the tile menu. Enter and Space stay with the focused button; the video keys and the favourite keys have their own routes (TryRoutePanelKey).
public sealed partial class MediaPage
{
    /// <summary>The Media page's own keys while the panel has the focus: true when the key was one (handled, even when the medium cannot take the action).</summary>
    private bool TryRoutePanelPageKey(KeyEventArgs e)
    {
        if (e.Handled || _lifetime.IsCancellationRequested || !_panelShown || _panel is not { } panel || _panelRow is not { } row
            || CommandRouter.IsNativeInput(e.Source, e.Key) || e.Key is Key.Enter or Key.Space or Key.Escape or Key.Tab) return false;
        MediaLibraryEntry entry = row.Entry;
        if (CommandBindings.Matches(e, "gallery.show-in-thread"))
        {
            e.Handled = true;
            if (entry.PostId != null && ShowInThread is { } show) OpenActions.Start(() => show(entry), "media.panel.thread");
            return true;
        }
        if (CommandBindings.Matches(e, "rules.edit-scope"))
        {
            e.Handled = true;
            if (_building == null && HasThreadRules(entry)) EditThreadRules?.Invoke(entry);
            return true;
        }
        if (CommandBindings.Matches(e, "media.queue.add"))
        {
            e.Handled = true;
            if (_building == null && TagQueueActions.BlockedReason(entry.Presence == MediaLibraryPresence.Present, entry.BooruPostId != null) == null)
                OpenActions.Start(() => AddEntryToQueueAsync(entry), "media.panel.queue");
            return true;
        }
        foreach (string id in MediaShortcutCommands)
        {
            if (!CommandBindings.Matches(e, id)) continue;
            e.Handled = true;
            OpenActions.Start(() => RunMediaCommandOnPanelAsync(id, row, panel.Frame), "media.panel.key");
            return true;
        }
        return false;
    }

    // The menu command of the panel's medium (the same rules as RunMediaCommandByShortcutAsync, with the panel's tile instead of the selection).
    private Task RunMediaCommandOnPanelAsync(string id, MediaLibraryTileRow row, Avalonia.Controls.Control anchor)
    {
        if (id == "media.rating.edit" && !_lifetime.IsCancellationRequested && !_selectionBusy)
        {
            // R on the panel's medium: as on the grid, a refusal says why (the index is being built, the file is not saved) instead of doing nothing.
            if (_building != null) { ShowSelectionNote(Texts.T("media.selection.reason.building")); return Task.CompletedTask; }
            if (row.Entry.Presence != MediaLibraryPresence.Present) { ShowSelectionNote(MediaMenuReasons.NotSaved); return Task.CompletedTask; }
            return OpenRateAndTagAsync(row.Entry, row, anchor, () => !_lifetime.IsCancellationRequested && _building == null && IsEffectivelyVisible, focusRating: true);
        }
        if (_lifetime.IsCancellationRequested || _building != null || _selectionBusy || ExecuteMediaMenuActionAsync == null || row.Entry.Presence != MediaLibraryPresence.Present)
            return Task.CompletedTask;
        // A rating key needs the saved ratings to be known, like the grid's.
        if (id.StartsWith("media.rating.", StringComparison.Ordinal) && row.State?.RatingKnown != true) return Task.CompletedTask;
        return RunMediaCommandOnTileAsync(id, row);
    }
}
