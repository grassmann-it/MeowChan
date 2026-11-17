using System;
using System.Threading.Tasks;
using Avalonia.Input;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.MediaActions;
using MeowChan.Gallery;

namespace MeowChan;

// Page commands with panel focus target the displayed medium, not the grid selection.
// The existing panel tunnel owns this forwarding; video and favourite keys retain
// their separate routes in TryRoutePanelKey. Native activation keys stay local.
public sealed partial class MediaPage
{
    /// <summary>The Media page's own keys while the panel has the focus: true when the key was one (handled, even when the medium cannot take the action).</summary>
    private bool TryRoutePanelPageKey(KeyEventArgs e)
    {
        if (e.Handled || ShortcutService.IsCapturing || _lifetime.IsCancellationRequested || !_panelShown || _panel is not { } panel || _panelRow is not { } row
            || CommandRouter.IsNativeInput(e.Source, e.Key)) return false;
        if (CommandBindings.Matches(e, "gallery.open"))
        {
            e.Handled = true;
            StartOpenPanelMedia();
            return true;
        }
        if (e.Key is Key.Enter or Key.Space or Key.Escape or Key.Tab) return false;
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
        if (CommandBindings.Matches(e, "media.tags.edit"))
        {
            e.Handled = true;
            OpenActions.Start(() => RunMediaCommandOnPanelAsync("media.tags.edit", row, panel.Frame), "media.panel.tags");
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
        if ((id is "media.rating.edit" or "media.tags.edit") && !_lifetime.IsCancellationRequested && !_selectionBusy)
        {
            // R on the panel's medium: as on the grid, a refusal says why (the index is being built, the file is not saved) instead of doing nothing.
            if (_building != null) { ShowSelectionNote(Texts.T("media.selection.reason.building")); return Task.CompletedTask; }
            if (row.Entry.Presence != MediaLibraryPresence.Present) { ShowSelectionNote(MediaMenuReasons.NotSaved); return Task.CompletedTask; }
            return OpenRateAndTagAsync(row.Entry, row, anchor,
                () => !_lifetime.IsCancellationRequested && _building == null && IsEffectivelyVisible,
                initialFocus: QuickTagInitialFocus.TagInput, useSelection: false);
        }
        if (_lifetime.IsCancellationRequested || _building != null || _selectionBusy || ExecuteMediaMenuActionAsync == null || row.Entry.Presence != MediaLibraryPresence.Present)
            return Task.CompletedTask;
        // A rating key needs the saved ratings to be known, like the grid's.
        if (id.StartsWith("media.rating.", StringComparison.Ordinal) && row.State?.RatingKnown != true) return Task.CompletedTask;
        return RunMediaCommandOnTileAsync(id, row);
    }
}
