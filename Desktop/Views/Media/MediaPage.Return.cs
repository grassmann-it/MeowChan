using System;
using System.Collections.Generic;
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
    private long _booruHandoffInteractionRevision;

    // Unlike the present/focus guard, this spans an expected activation/build. Refreshing the
    // index may replace snapshots and tasks; only a user edit/selection or ending the page revokes it.
    internal Func<bool> CaptureBooruHandoffInteraction()
    {
        long interaction = _booruHandoffInteractionRevision;
        long selection = _selectionStamp;
        return () => !_lifetime.IsCancellationRequested
            && interaction == _booruHandoffInteractionRevision && selection == _selectionStamp;
    }

    private void NoteBooruHandoffInteraction()
    {
        if (!_localizing) ++_booruHandoffInteractionRevision;
    }

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
    /// The way back for a gallery opened on this page (its grid, the tag-queue walk of its header): the selected tile, else the tile at the top
    /// of the view, with the selection and the view position. Null when the page is hidden or shows no tile; closing then focuses the grid only.
    /// </summary>
    internal MediaLibraryReturnPoint? CaptureGalleryReturnPoint()
    {
        if (_lifetime.IsCancellationRequested || !IsEffectivelyVisible) return null;
        MediaLibraryEntry? opened = SelectedRow?.Entry;
        if (opened == null && FirstTileInView() is var top && top >= 0 && top < _tiles.Count) opened = _tiles[top].Entry;
        return opened == null ? null : CaptureReturnPoint(opened);
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
    internal void EndPendingReturn()
    {
        _returnRun = null;
        NoteBooruHandoffInteraction();
    }

    /// <summary>Focuses exactly one already-present tile, without falling back to an ID substring or a neighbouring tile.</summary>
    internal async Task<bool> FocusExactEntryAsync(Func<MediaLibraryEntry, bool> match, string missingText)
    {
        ArgumentNullException.ThrowIfNull(match);
        object run = new();
        _returnRun = run;
        return await FocusExactEntryAsync(match, missingText, run).ConfigureAwait(true) == ExactEntryFocusResult.Focused;
    }

    // Captures both requested and applied presentation generations: PresentCount alone cannot detect a
    // pending replacement, and PresentTask alone cannot distinguish two synchronous presentations.
    // No profile identifier is needed: the page lifetime, snapshot/index and build task pin this page's data.
    private Func<bool> CaptureNavigationContext(object run, bool awaitingPresent)
    {
        var snapshot = _snapshot;
        var presentIndex = _presentIndex;
        Task present = PresentTask, build = BuildTask;
        int generation = PresentCount + (awaitingPresent && _presentPending ? 1 : 0);
        int requested = PresentRunCount;
        long selection = _selectionStamp;
        long interaction = _booruHandoffInteractionRevision;
        return () => !_lifetime.IsCancellationRequested && ReferenceEquals(_returnRun, run)
            && interaction == _booruHandoffInteractionRevision
            && ReferenceEquals(snapshot, _snapshot) && ReferenceEquals(presentIndex, _presentIndex)
            && ReferenceEquals(build, BuildTask) && _building == null
            && ReferenceEquals(present, PresentTask) && PresentCount == generation && PresentRunCount == requested
            && !_presentPending && selection == _selectionStamp;
    }

    internal enum ExactEntryFocusResult { Focused, Missing, Cancelled, Failed }

    private async Task<ExactEntryFocusResult> FocusExactEntryAsync(Func<MediaLibraryEntry, bool> match, string missingText, object run,
        Func<bool>? stillRequested = null)
    {
        Func<bool> context = CaptureNavigationContext(run, awaitingPresent: false);
        if (!context() || !IsEffectivelyVisible || stillRequested?.Invoke() == false) return ExactEntryFocusResult.Cancelled;
        int[] matches = _shown.Select((entry, index) => (entry, index)).Where(item => match(item.entry)).Select(item => item.index).ToArray();
        if (matches.Length != 1)
        {
            // Absence from a filtered/partial presentation and duplicate identity are not proof
            // that an original disappeared. Only an authoritative index can establish Missing.
            if (matches.Length == 0 && _snapshot is { Complete: true } snapshot && !snapshot.Entries.Any(match))
            {
                ShowSelectionNote(missingText);
                return ExactEntryFocusResult.Missing;
            }
            return ExactEntryFocusResult.Failed;
        }
        int index = matches[0];
        var shown = _shown;
        var tiles = _tiles;
        object? rows = _grid.ItemsSource;
        MediaLibraryEntry exact = shown[index];
        if (index >= tiles.Count || index >= _grid.ItemCount || !ReferenceEquals(exact, tiles[index].Entry))
            return ExactEntryFocusResult.Failed;
        // Full occurrence/row identity, not just byte grouping: another post may have identical bytes.
        bool Current() => context() && IsEffectivelyVisible && (stillRequested?.Invoke() ?? true)
            && ReferenceEquals(shown, _shown) && ReferenceEquals(tiles, _tiles) && ReferenceEquals(rows, _grid.ItemsSource)
            && index < _shown.Count && index < _tiles.Count && index < _grid.ItemCount
            && ReferenceEquals(exact, _shown[index]) && ReferenceEquals(exact, _tiles[index].Entry) && match(_shown[index]);
        if (!Current()) return ExactEntryFocusResult.Cancelled;
        for (int attempt = 0; attempt < 2 && _grid.ContainerFromIndex(index) is not Control; attempt++)
        {
            _grid.ScrollIntoView(index);
            if (!Current()) return ExactEntryFocusResult.Cancelled; // Scrolling can reenter layout/presentation.
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Loaded);
            if (!Current()) return ExactEntryFocusResult.Cancelled;
        }
        if (!Current()) return ExactEntryFocusResult.Cancelled;
        if (_grid.ContainerFromIndex(index) is not Control tile) return ExactEntryFocusResult.Failed;
        // A recycled container must still carry this exact row and occurrence, never the neighbour now at index.
        if (tile.DataContext is not MediaLibraryTileRow row || row.Index != index
            || !ReferenceEquals(row, _grid.Items[index]) || !ReferenceEquals(row.Tile, _tiles[index])
            || !ReferenceEquals(row.Entry, exact) || !tile.IsEffectivelyVisible) return ExactEntryFocusResult.Failed;
        bool focused = tile.Focus();
        if (!Current()) return ExactEntryFocusResult.Cancelled;
        return focused ? ExactEntryFocusResult.Focused : ExactEntryFocusResult.Failed;
    }

    // Any own input on the page ends a pending second restore: the user has taken over.
    private void InstallReturnInterruption()
    {
        void Interrupt(object? sender, RoutedEventArgs e)
        {
            _returnRun = null;
            NoteBooruHandoffInteraction();
        }
        // The filter row owns the synchronous TextChanging hook. A deferred TextChanged from an
        // expected programmatic reset must not cancel the navigation after _localizing has ended.
        _sort.SelectionChanged += (_, _) =>
        {
            if (!_restoringSort) NoteBooruHandoffInteraction();
        };
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
        SyncPanelToRows(); // the panel shows the tile the gallery was opened on
        if (match.Anchor >= 0) TilePanel()?.RestoreAnchor(match.Anchor, match.AnchorOffset);
        ShowSelectionNote(match.Target < 0 ? Texts.T("media.return.missing", point.Occurrence.Name) : null);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Loaded);
        if (!ReferenceEquals(_returnRun, run) || !IsEffectivelyVisible) return match.Target >= 0;
        // Focus the opened tile; when it is gone, the first tile still in view (it is focused, not selected, and the note says why).
        int focus = match.Target >= 0 ? match.Target : match.Selected.Count > 0 ? match.Selected[0] : FirstTileInView();
        if (focus < 0)
        {
            // No tile at all (the rebuild left the page empty): the control that held the focus ("Back to media", the gallery) is hidden now,
            // so the empty state's first way out, else the search field, takes it; never nothing.
            EmptyReturnFocusTarget()?.Focus();
            return match.Target >= 0;
        }
        if (_grid.ContainerFromIndex(focus) == null && match.Target >= 0) _grid.ScrollIntoView(focus); // it moved out of the restored view
        (_grid.ContainerFromIndex(focus) as Control)?.Focus();
        return match.Target >= 0;
    }

    /// <summary>Where the focus goes after a return to a page without tiles: the first usable empty-state action, else the search field, else Refresh.</summary>
    internal Control? EmptyReturnFocusTarget() =>
        PickReturnFocus(_emptyActions.Children.Append(_nameFilter).Append(_refresh), control => control.IsEffectivelyVisible);

    internal static Control? PickReturnFocus(IEnumerable<Control> ordered, Func<Control, bool> shown) =>
        ordered.FirstOrDefault(control => control.Focusable && control.IsEffectivelyEnabled && shown(control));

    private int FirstTileInView() => TilePanel()?.CaptureAnchor() is { } anchor ? anchor.Index : _tiles.Count > 0 ? 0 : -1;

    private MediaTilePanel? TilePanel() => _grid.ItemsPanelRoot as MediaTilePanel ?? _grid.GetVisualDescendants().OfType<MediaTilePanel>().FirstOrDefault();
}
