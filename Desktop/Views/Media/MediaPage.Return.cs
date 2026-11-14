using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;
using MeowChan.Gallery;

namespace MeowChan;

// "Show in thread" and back: the page records which tile was opened, the selection and the top of the view before the
// archive comes to the front, and puts them back on return. The page is only hidden meanwhile, so filters, order and the
// random seed are still its own; what can change is the index (every activation rebuilds it in the background) and own
// tags or favorites. A tile that is no longer shown is said so, never replaced by the tile now at its position.
public sealed partial class MediaPage
{
    private object? _returnRun;

    /// <summary>The page's state for the way back, taken before <see cref="ShowInThread"/> leaves it.</summary>
    internal MediaLibraryReturnPoint CaptureReturnPoint(MediaLibraryEntry occurrence)
    {
        MediaTilePanel? panel = TilePanel();
        MediaLibraryTile? top = null;
        double offset = 0;
        if (panel?.CaptureAnchor() is { } anchor && anchor.Index >= 0 && anchor.Index < _tiles.Count) { top = _tiles[anchor.Index]; offset = anchor.Offset; }
        return MediaLibraryReturn.Capture(occurrence, SelectedTiles, top, offset);
    }

    /// <summary>
    /// Restores the view now and once more after the rebuild the activation started, unless the user has scrolled, clicked
    /// or typed on the page in between. Returns whether the opened tile is shown.
    /// </summary>
    internal async Task<bool> ReturnToAsync(MediaLibraryReturnPoint point)
    {
        ArgumentNullException.ThrowIfNull(point);
        object run = new();
        _returnRun = run;
        bool shown = await RestoreAsync(point, run);
        Task build = BuildTask;
        if ((build.IsCompleted && PresentTask.IsCompleted) || !ReferenceEquals(_returnRun, run)) return shown;
        if (!build.IsCompleted)
        {
            try { await build; } catch (OperationCanceledException) { }
        }
        // A large library presents in the background (see Present): its rows replace the ones just restored, so wait for it too.
        await WaitForPresentAsync(run);
        return ReferenceEquals(_returnRun, run) ? await RestoreAsync(point, run) : shown;
    }

    /// <summary>Ends a pending second restore, e.g. when a gallery opened from this page now owns selection and focus.</summary>
    internal void EndPendingReturn() => _returnRun = null;

    /// <summary>Focuses exactly one already-present tile, without falling back to an ID substring or a neighbouring tile.</summary>
    internal async Task<bool> FocusExactEntryAsync(Func<MediaLibraryEntry, bool> match, string missingText)
    {
        ArgumentNullException.ThrowIfNull(match);
        if (_lifetime.IsCancellationRequested || !IsEffectivelyVisible) return false;
        int[] matches = _shown.Select((entry, index) => (entry, index)).Where(item => match(item.entry)).Select(item => item.index).ToArray();
        if (matches.Length != 1)
        {
            ShowSelectionNote(missingText);
            return false;
        }
        int index = matches[0];
        for (int attempt = 0; attempt < 2 && _grid.ContainerFromIndex(index) is not Control; attempt++)
        {
            _grid.ScrollIntoView(index);
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Loaded);
        }
        if (_grid.ContainerFromIndex(index) is not Control tile)
        {
            ShowSelectionNote(missingText);
            return false;
        }
        tile.Focus();
        return true;
    }

    // Any own input on the page ends a pending second restore: the user has taken over.
    private void InstallReturnInterruption()
    {
        void Interrupt(object? sender, RoutedEventArgs e) => _returnRun = null;
        AddHandler(PointerPressedEvent, Interrupt, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerWheelChangedEvent, Interrupt, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(KeyDownEvent, Interrupt, RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    // Waits until no presentation is running (or the return ended): a presentation swaps the rows, and a swap resets scroll and focus.
    // A presentation started while waiting (an audio update, new own state) is waited for as well.
    private async Task WaitForPresentAsync(object run)
    {
        while (true)
        {
            Task seen = PresentTask;
            if (!seen.IsCompleted) await Task.WhenAny(seen);
            if (ReferenceEquals(seen, PresentTask) || !ReferenceEquals(_returnRun, run) || _lifetime.IsCancellationRequested) return;
        }
    }

    private async Task<bool> RestoreAsync(MediaLibraryReturnPoint point, object run)
    {
        // Let a presentation queued by the activation (refreshed own state) run first.
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Loaded);
        await WaitForPresentAsync(run);
        if (!ReferenceEquals(_returnRun, run) || _lifetime.IsCancellationRequested || !IsEffectivelyVisible) return false;
        MediaLibraryReturnMatch match = MediaLibraryReturn.Locate(point, _tiles);
        _restoring = true;
        try
        {
            _grid.Selection.Clear();
            SelectIndexes(match.Selected);
        }
        finally { _restoring = false; }
        UpdateSelectionBar();
        if (match.Anchor >= 0) TilePanel()?.RestoreAnchor(match.Anchor, match.AnchorOffset);
        ShowSelectionNote(match.Target < 0 ? Texts.T("media.return.missing", point.Occurrence.Name) : null);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Loaded);
        if (!ReferenceEquals(_returnRun, run) || !IsEffectivelyVisible) return match.Target >= 0;
        // Focus the opened tile; when it is gone, the first tile still in view (it is focused, not selected, and the note says why).
        int focus = match.Target >= 0 ? match.Target : match.Selected.Count > 0 ? match.Selected[0] : FirstTileInView();
        if (focus < 0) return match.Target >= 0;
        if (_grid.ContainerFromIndex(focus) == null && match.Target >= 0) _grid.ScrollIntoView(focus); // it moved out of the restored view
        (_grid.ContainerFromIndex(focus) as Control)?.Focus();
        return match.Target >= 0;
    }

    private int FirstTileInView() => TilePanel()?.CaptureAnchor() is { } anchor ? anchor.Index : _tiles.Count > 0 ? 0 : -1;

    private MediaTilePanel? TilePanel() => _grid.ItemsPanelRoot as MediaTilePanel ?? _grid.GetVisualDescendants().OfType<MediaTilePanel>().FirstOrDefault();
}
