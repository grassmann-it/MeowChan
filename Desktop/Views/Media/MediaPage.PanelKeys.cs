using System;
using System.Threading;
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
            OpenActions.Start(panel.EditTagsAsync, "media.panel.tags");
            return true;
        }
        foreach (string id in MediaShortcutCommands)
        {
            if (!CommandBindings.Matches(e, id)) continue;
            e.Handled = true;
            if (id == "media.rating.edit") OpenActions.Start(panel.EditTagsAsync, "media.panel.tags");
            else OpenActions.Start(() => RunMediaCommandOnPanelAsync(id, row), "media.panel.key");
            return true;
        }
        return false;
    }

    // Tiles coalesce equal bytes. A private tag belongs to the visible occurrence and revision.
    private static bool SamePanelTagTarget(MediaLibraryEntry? left, MediaLibraryEntry? right) =>
        ReferenceEquals(left, right) || left is not null && right is not null && left.Thread == right.Thread
        && left.ThreadRevision == right.ThreadRevision && left.PostId == right.PostId && left.Position == right.Position
        && left.AttachmentRevisionKey == right.AttachmentRevisionKey
        && left.Directory == right.Directory && left.AssetId == right.AssetId && left.MediaId == right.MediaId && left.Variant == right.Variant
        && left.Sha256 == right.Sha256 && left.Length == right.Length && left.LocalSlot == right.LocalSlot && left.Presence == right.Presence
        && left.Historical == right.Historical && left.Unassigned == right.Unassigned && left.BooruPostId == right.BooruPostId
        && left.ImportItemId == right.ImportItemId && left.FolderItemId == right.FolderItemId;

    private Task OpenPanelTagsAsync(MediaLibraryTileRow row, Avalonia.Controls.Control anchor, CancellationToken token)
    {
        if (_lifetime.IsCancellationRequested || _selectionBusy) return Task.CompletedTask;
        if (_building != null) { ShowSelectionNote(Texts.T("media.selection.reason.building")); return Task.CompletedTask; }
        if (row.Entry.Presence != MediaLibraryPresence.Present) { ShowSelectionNote(MediaMenuReasons.NotSaved); return Task.CompletedTask; }
        bool Current() => !token.IsCancellationRequested && !_lifetime.IsCancellationRequested && _building == null
            && _panelShown && !_panelObscured && IsEffectivelyVisible && SamePanelTagTarget(row.Entry, _panelRow?.Entry);
        return OpenRateAndTagAsync(row.Entry, row, anchor, Current, QuickTagInitialFocus.TagInput, sidebarToken: token);
    }

    private Task RunMediaCommandOnPanelAsync(string id, MediaLibraryTileRow row)
    {
        if (id == "media.next-untagged") { SelectNextUntagged(); return Task.CompletedTask; }
        if (_lifetime.IsCancellationRequested || _building != null || _selectionBusy || ExecuteMediaMenuActionAsync == null || row.Entry.Presence != MediaLibraryPresence.Present)
            return Task.CompletedTask;
        // A rating key needs the saved ratings to be known, like the grid's.
        if (id.StartsWith("media.rating.", StringComparison.Ordinal) && row.State?.RatingKnown != true) return Task.CompletedTask;
        return RunMediaCommandOnTileAsync(id, row);
    }
}
