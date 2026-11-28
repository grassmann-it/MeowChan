using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.MediaActions;
using MeowChan.Favorites;
using MeowChan.Gallery;

namespace MeowChan;

// Multi-select in the Media tab of a thread (list and tiles): the list's own Ctrl/Cmd+click, Shift+click and Shift+arrows, plus
// Ctrl/Cmd+A and Esc. The selection feeds the same actions as the media page: "Tag together" (MediaSelectionTagDialog), the selection as
// a gallery and "Export originals". The file menu and the shortcuts keep acting on the focused file, and the gallery opens at it.
public partial class ArchivePage
{
    private ArchiveAssetRow? _mediaLead;
    private bool _mediaSelectionBusy;
    private MediaSelectionTagDialog? _mediaSelectionTagDialog;
    private QuickTagStamp? _archiveStamp;
    private ArchiveStampRun? _archiveStampRun;
    private ThreadMediaTarget? _threadMediaTarget;

    private sealed record ThreadMediaTarget(ArchivedThreadView Thread, IReadOnlyList<ArchiveAssetRow> ShownRows,
        ArchiveAssetRow[] Rows, GalleryEntry[] Entries, ArchiveMediaSelection.MediaCount Count);

    private sealed record ArchiveStampRun(ResourceId Thread, QuickTagSession Session, MediaSelectionTagSession Target,
        ArchiveMediaSelection.MediaCount Count, string Names);

    /// <summary>Own-tag access for "Tag together" in the Media tab; set when the private tags are attached, null disables the action.</summary>
    internal MediaSelectionTagServices? MediaTagServices { get; set; }
    internal Window? MediaSelectionTagWindow => _mediaSelectionTagDialog;

    /// <summary>Adds gallery entries to the tagging queue and returns the sentence for the status line; null disables the queue actions.</summary>
    internal Func<IReadOnlyList<GalleryEntry>, Task<string>>? AddToTagQueue { get; set; }

    private void AttachMediaSelectionTags(DesktopSessionHost host)
    {
        AddToTagQueue = entries => TagQueueActions.AddAsync(host, entries);
        MediaTagServices = new(host.GetPublishedPrivateTagSnapshot,
            (name, subjects, expectedRevision, token) => host.DefineAndSetMediaSubjectTagsAsync(name, subjects, expectedRevision, token),
            (tag, subjects, assigned, expectedRevision, token) => host.SetMediaSubjectTagsAsync(tag, subjects, assigned, expectedRevision, token));
    }

    private void InitializeMediaSelection()
    {
        foreach (Control list in new Control[] { ArchiveMediaTiles, ArchiveMediaGrid })
        {
            // The focused file is the one single-file actions use while several are selected; a click or the keyboard moves it.
            list.AddHandler(InputElement.GotFocusEvent, (_, e) => NoteMediaLead(e.Source), RoutingStrategies.Bubble, handledEventsToo: true);
            list.AddHandler(InputElement.PointerPressedEvent, (_, e) => NoteMediaLead(e.Source), RoutingStrategies.Tunnel, handledEventsToo: true);
            list.AddHandler(InputElement.KeyDownEvent, OnMediaSelectionKey, RoutingStrategies.Tunnel);
        }
        ArchiveMediaGrid.CurrentCellChanged += (_, _) =>
        {
            if (!_mediaTiles) NoteMediaLead(ArchiveMediaGrid.CurrentRowItem as ArchiveAssetRow);
        };
        foreach (Control list in new Control[] { ArchiveThreads, ArchiveThreadTable })
            list.AddHandler(InputElement.KeyDownEvent, OnThreadMediaCommandKey, RoutingStrategies.Tunnel);
        ArchiveContentTabs.SelectionChanged += (_, e) =>
        {
            if (ReferenceEquals(e.Source, ArchiveContentTabs)) UpdateMediaSelectionBar();
        };
        CommandBindings.ObservePresentation(ArchiveMediaSelectionStamp, UpdateMediaSelectionBar);
        AutomationProperties.SetLiveSetting(ArchiveMediaSelectionStatus, AutomationLiveSetting.Polite);
    }

    private void NoteMediaLead(object? source)
    {
        if (source is not Visual visual) return;
        ArchiveAssetRow? row = visual.GetSelfAndVisualAncestors().OfType<Control>().Select(control => control.DataContext)
            .OfType<ArchiveAssetRow>().FirstOrDefault();
        NoteMediaLead(row);
    }

    private void NoteMediaLead(ArchiveAssetRow? row)
    {
        if (_rebindingMedia || row == null || ReferenceEquals(row, _mediaLead)) return;
        _mediaLead = row;
        if (SelectedMediaCount > 1) FollowSavedMediaSelection();
    }

    private Control VisibleMediaList => _mediaTiles ? ArchiveMediaTiles : ArchiveMediaGrid;

    private bool FocusSelectedMedia() => SelectedMediaFocusTarget()?.Focus() == true || VisibleMediaList.Focus();

    private Control? SelectedMediaFocusTarget()
    {
        Control list = VisibleMediaList;
        if (!list.IsEffectivelyVisible) return null;
        if (SelectedMediaRow() is { } lead)
        {
            if (_mediaTiles)
            {
                ArchiveMediaTiles.ScrollIntoView(lead);
                if (ArchiveMediaTiles.ContainerFromItem(lead) is Control tile) return tile;
            }
            else
            {
                ArchiveMediaGrid.ScrollIntoView(lead, ArchiveMediaGrid.CurrentColumn);
                // The grid owns keyboard focus and keeps its current cell independently of SelectedItem.
                if (ReferenceEquals(ArchiveMediaGrid.CurrentRowItem, lead) && ArchiveMediaGrid.IsKeyboardFocusWithin
                    && TopLevel.GetTopLevel(list)?.FocusManager?.GetFocusedElement() is Control focused) return focused;
                DataGridRow? row = ArchiveMediaGrid.GetVisualDescendants().OfType<DataGridRow>()
                    .FirstOrDefault(item => ReferenceEquals(item.DataContext, lead));
                DataGridCell? cell = row?.GetVisualDescendants().OfType<DataGridCell>().FirstOrDefault(item => item.IsEffectivelyVisible);
                if (cell != null)
                {
                    cell.Focusable = true;
                    cell.IsTabStop = false;
                    return cell;
                }
            }
        }
        return list;
    }

    private int SelectedMediaCount => (_mediaTiles ? ArchiveMediaTiles.SelectedItems : ArchiveMediaGrid.SelectedItems)?.Count ?? 0;

    /// <summary>The selected files in the order the tab shows them. Only files that are still shown are ever in it.</summary>
    internal IReadOnlyList<ArchiveAssetRow> SelectedMediaRows()
    {
        System.Collections.IList? items = _mediaTiles ? ArchiveMediaTiles.SelectedItems : ArchiveMediaGrid.SelectedItems;
        return items == null || items.Count == 0 ? Array.Empty<ArchiveAssetRow>()
            : ArchiveMediaSelection.InShownOrder(_shownMedia, items.OfType<ArchiveAssetRow>());
    }

    // The file single-file actions use: with one selected file that file, with several the focused one.
    private ArchiveAssetRow? SelectedMediaRow()
    {
        object? primary = _mediaTiles ? ArchiveMediaTiles.SelectedItem : ArchiveMediaGrid.SelectedItem;
        if (SelectedMediaCount <= 1) return primary as ArchiveAssetRow;
        return ArchiveMediaSelection.Lead(SelectedMediaRows(), _mediaLead, primary as ArchiveAssetRow);
    }

    /// <summary>Selects exactly these files in the shown list (those not shown are ignored); the hidden list is left alone.</summary>
    private void SetMediaSelection(IReadOnlyCollection<ArchiveAssetRow> rows)
    {
        HashSet<ArchiveAssetRow> wanted = new(rows, ReferenceEqualityComparer.Instance);
        bool previous = _rebindingMedia;
        _rebindingMedia = true;
        try
        {
            if (_mediaTiles)
            {
                ListBox tiles = ArchiveMediaTiles;
                tiles.Selection.Clear();
                // Runs of neighbouring files are one range, so selecting a large part of the tab stays cheap.
                for (int index = 0; index < _shownMedia.Count; index++)
                {
                    if (!wanted.Contains(_shownMedia[index])) continue;
                    int end = index;
                    while (end + 1 < _shownMedia.Count && wanted.Contains(_shownMedia[end + 1])) end++;
                    tiles.Selection.SelectRange(index, end);
                    index = end;
                }
            }
            else
            {
                DataGrid grid = ArchiveMediaGrid;
                grid.SelectedItems.Clear();
                foreach (ArchiveAssetRow row in _shownMedia) if (wanted.Contains(row)) grid.SelectedItems.Add(row);
            }
        }
        finally { _rebindingMedia = previous; }
    }

    internal void ClearMediaSelection()
    {
        if (SelectedMediaCount == 0) return;
        SetMediaSelection(Array.Empty<ArchiveAssetRow>());
        ++_mediaPositionRequest;
        UpdateActions();
        FollowSavedMediaSelection();
    }

    internal void SelectAllMedia()
    {
        if (_shownMedia.Count == 0) return;
        if (_mediaTiles) ArchiveMediaTiles.Selection.SelectAll();
        else ArchiveMediaGrid.SelectAll();
    }

    // Ctrl/Cmd+A selects every shown file and Esc clears a selection of several; clicks and Shift/Ctrl ranges are the lists' own.
    private void OnMediaSelectionKey(object? sender, KeyEventArgs e)
    {
        if (e.Handled || _ended || !_available || IsReaderDialogOpen || sender is not Control list
            || !MainWindow.IsListCommandSource(e.Source, list) || !ReferenceEquals(list, VisibleMediaList)) return;
        if (ArchiveMediaTab.IsSelected && !ShortcutService.IsCapturing)
        {
            if (RunThreadMediaShortcut(e, _selectedThread, list, "reader")) return;
            if (SelectedMediaCount > 0 && ShortcutService.Current.Matches(e, QuickTagCommands.StampApply, "reader"))
            {
                e.Handled = true;
                StartSelectionStamp();
                return;
            }
        }
        if (e.KeyModifiers == KeyModifiers.Meta && e.Key is Key.Left or Key.Right or Key.Up or Key.Down)
            e.Handled = true;
        else if (e.Key == Key.Escape && e.KeyModifiers == KeyModifiers.None && ArchiveMediaSelection.ShowsStatus(SelectedMediaCount))
        {
            ClearMediaSelection();
            e.Handled = true;
        }
        else if (e.Key == Key.A && e.KeyModifiers is KeyModifiers.Control or KeyModifiers.Meta && _shownMedia.Count > 0)
        {
            SelectAllMedia();
            e.Handled = true;
        }
    }

    private void LocalizeMediaSelection()
    {
        ArchiveThreadMediaMenuButton.Content = Texts.T("tags.quick.archive.menu");
        ArchiveMediaSelectionStamp.Content = CommandCatalog.Label(QuickTagCommands.StampApply);
        ArchiveMediaStampUndo.Content = Texts.T("tags.quick.undo");
        ArchiveMediaSelectionTag.Content = Texts.T("media.selection.tag");
        ArchiveMediaSelectionQueue.Content = Texts.T("media.selection.queue");
        ArchiveMediaSelectionGallery.Content = Texts.T("media.selection.gallery");
        ArchiveMediaSelectionExport.Content = Texts.T("media.selection.export");
        ArchiveMediaSelectionClear.Content = Texts.T("media.selection.clear");
        string help = Texts.T("archive.media.selection.help");
        foreach (Control list in new Control[] { ArchiveMediaTiles, ArchiveMediaGrid }) AutomationProperties.SetHelpText(list, help);
        AutomationProperties.SetName(ArchiveMediaSelectionBar, Texts.T("archive.media.selection.name"));
        UpdateMediaSelectionBar();
    }

    private void UpdateMediaSelectionBar()
    {
        ObserveArchiveStamp();
        if (_archiveStampRun is { } run && (_ended || run.Thread != _selectedThread)) ClearArchiveStampRun();
        if (_threadMediaTarget is { } target && !ReferenceEquals(target.Thread, _thread)) _threadMediaTarget = null;
        int count = _ended ? 0 : SelectedMediaCount;
        bool show = ArchiveMediaSelection.ShowsStatus(count);
        bool showActions = ArchiveMediaTab.IsSelected && count > 0;
        ArchiveMediaSelectionActions.IsVisible = showActions;
        ArchiveMediaSelectionBar.IsVisible = show;
        ArchiveMediaStampResult.IsVisible = _archiveStampRun is { } result && result.Session.Status.Kind != QuickTagStatusKind.None;
        ArchiveMediaSelectionStrip.IsVisible = showActions || ArchiveMediaStampResult.IsVisible;
        UpdateArchiveStampResult();
        if (showActions)
            SetArchiveStampAction(ArchiveMediaSelectionStamp, QuickTagCommands.StampApply, SelectionStampBlocked());
        if (!show) return;
        ArchiveMediaSelectionStatus.Text = ArchiveMediaSelection.Status(count);
        bool ready = _available && !_busy && !_ended && !IsReaderDialogOpen && !_mediaSelectionBusy && _thread != null;
        SetMediaSelectionAction(ArchiveMediaSelectionTag, ready && MediaTagServices != null, MediaTagServices != null, "media.selection.tag.hint");
        SetMediaSelectionAction(ArchiveMediaSelectionQueue, ready && AddToTagQueue != null, AddToTagQueue != null, "media.selection.queue.hint");
        SetMediaSelectionAction(ArchiveMediaSelectionGallery, ready && OpenMediaGallery != null, OpenMediaGallery != null, "media.selection.gallery.hint");
        SetMediaSelectionAction(ArchiveMediaSelectionExport, ready, true, "media.selection.export.hint");
        ArchiveMediaSelectionClear.IsEnabled = !_ended;
    }

    private void ObserveArchiveStamp()
    {
        if (_ended || _archiveStamp != null || TagPopoverOptions?.Invoke(TagContext.Empty).Stamp is not { } stamp) return;
        _archiveStamp = stamp;
        stamp.Changed += UpdateMediaSelectionBar;
    }

    private static void SetArchiveStampAction(Button button, string command, string? reason)
    {
        button.IsEnabled = reason == null;
        string tip = CommandBindings.Tooltip(command, reason, keyContext: "reader");
        ToolTip.SetTip(button, tip);
        AutomationProperties.SetHelpText(button, tip);
    }

    private string? ThreadMediaBlocked(ResourceId? id)
    {
        if (_ended || !_available || _busy || _mediaSelectionBusy || IsReaderDialogOpen || IsFilenameSearchOpen)
            return Texts.T("archive.command.busy");
        if (id == null || _thread?.Summary.Id != id || _selectedThread != id)
            return Texts.T("tags.quick.archive.threadNotLoaded");
        if (_mediaLoading || _mediaProjectionPending) return Texts.T("archive.loadingMedia");
        if (_shownMedia.Count == 0) return Texts.T("tags.quick.archive.noSaved");
        return MediaTagServices?.Load().State == PrivateTagLoadState.Ready ? null : Texts.T("media.selection.reason.unavailable");
    }

    private int ArchiveStampTagCount() => _archiveStamp?.Resolve(MediaTagServices?.Load().Snapshot).Count ?? 0;

    private string? StampBlocked() =>
        ArchiveStampTagCount() == 0 ? Texts.T("tags.quick.archive.emptyStamp") : null;

    private string? SelectionStampBlocked() => ThreadMediaBlocked(_selectedThread) ?? StampBlocked();

    private ThreadMediaTarget FreezeThreadMedia(ArchivedThreadView thread)
    {
        if (_threadMediaTarget is { } frozen && ReferenceEquals(frozen.Thread, thread)
            && ReferenceEquals(frozen.ShownRows, _shownMedia)) return frozen;
        ArchiveAssetRow[] rows = _shownMedia.ToArray();
        GalleryEntry[] entries = MediaSelectionEntries(thread);
        return _threadMediaTarget = new(thread, _shownMedia, rows, entries, ArchiveMediaSelection.CountThreadMedia(thread, rows, entries));
    }

    private GalleryEntry[] MediaSelectionEntries(ArchivedThreadView thread) =>
        GallerySelection.Build(thread, _assets.Select(row => row.Manifest).DistinctBy(manifest => manifest.Directory),
            true, includeOtherMedia: true).Entries.ToArray();

    private void OnMediaSelectionStampClick(object? sender, RoutedEventArgs e) => StartSelectionStamp();

    private void StartSelectionStamp()
    {
        if (SelectionStampBlocked() != null || FreezeMediaSelection() is not { } frozen) return;
        OpenActions.Start(() => ApplyArchiveStampAsync(frozen.Thread, frozen.Rows, frozen.Entries, new(frozen.Rows.Length)), QuickTagCommands.StampApply);
    }

    private void UpdateArchiveStampResult()
    {
        if (_archiveStampRun is not { } run) return;
        QuickTagStatus status = run.Session.Status;
        string summary = Texts.T("tags.quick.archive.stampResult", run.Names, run.Count.Text);
        ArchiveMediaStampStatus.Text = status.Kind switch
        {
            QuickTagStatusKind.StampApplied => summary,
            QuickTagStatusKind.StampComplete => summary + " · " + status.Text,
            QuickTagStatusKind.Error when run.Session.UndoSteps.Count > 0 =>
                Texts.T("tags.quick.archive.stampResult", status.Name, run.Count.Text) + " · " + status.Text,
            _ => status.Text
        };
        ArchiveMediaStampUndo.IsVisible = run.Session.UndoSteps.Count > 0;
        ArchiveMediaStampUndo.IsEnabled = _available && !_busy && !_mediaSelectionBusy && !IsReaderDialogOpen && run.Session.CanUndo;
    }

    private async Task ApplyArchiveStampAsync(ArchivedThreadView thread, ArchiveAssetRow[] rows, GalleryEntry[] entries,
        ArchiveMediaSelection.MediaCount count)
    {
        if (SelectionStampBlocked() != null || thread.Summary.Id != _selectedThread
            || MediaTagServices is not { } services || SelectionTagContext(thread, rows, entries) is not { } context) return;
        QuickTagPopoverOptions options = TagPopoverOptions?.Invoke(context) ?? QuickTagPopoverOptions.Defaults(context);
        MediaSelectionTagSession target = NewMediaSelectionTagSession(thread, rows, entries, services);
        ClearArchiveStampRun();
        // The run owns the session; ClearArchiveStampRun disposes it.
        QuickTagSession? created = options.NewSession(QuickTagTarget.For(target));
        QuickTagSession session;
        try
        {
            string names = string.Join(", ", created.Stamp.Resolve(created.Snapshot).Select(tag => tag.Name.Display));
            _archiveStampRun = new(thread.Summary.Id, created, target, count, names);
            session = created;
            created = null;
        }
        finally
        {
            created?.Dispose();
        }
        if (options.Recent is { } recent) session.Applied += change => recent.Touch(change.Tag);
        await RunArchiveStampAsync(() => session.ApplyStampJoinedAsync(_lifetime.Token));
    }

    private void OnMediaStampUndoClick(object? sender, RoutedEventArgs e)
    {
        if (!ArchiveMediaStampUndo.IsEnabled || _archiveStampRun is not { } run || run.Thread != _selectedThread) return;
        OpenActions.Start(() => RunArchiveStampAsync(async () =>
        {
            run.Target.Reload();
            await run.Session.UndoAsync(_lifetime.Token);
        }), "archive.media-stamp.undo");
    }

    private async Task RunArchiveStampAsync(Func<Task> action)
    {
        _mediaSelectionBusy = true;
        UpdateActions();
        try { await action(); }
        finally
        {
            _mediaSelectionBusy = false;
            if (!_ended) UpdateActions();
        }
    }

    private void ClearArchiveStampRun()
    {
        _archiveStampRun?.Session.Dispose();
        _archiveStampRun = null;
    }

    // A disabled action says why in its tooltip and accessible description.
    private static void SetMediaSelectionAction(Button button, bool enabled, bool wired, string hintKey)
    {
        button.IsEnabled = enabled;
        string hint = enabled ? Texts.T(hintKey) : wired ? string.Empty : Texts.T("media.selection.reason.unavailable");
        ToolTip.SetTip(button, hint.Length == 0 ? null : hint);
        AutomationProperties.SetHelpText(button, hint);
    }

    /// <summary>The current selection frozen with everything the actions need, or null when nothing can be acted on right now.</summary>
    private (ArchivedThreadView Thread, ArchiveAssetRow[] Rows, GalleryEntry[] Entries)? FreezeMediaSelection()
    {
        if (_ended || !_available || _busy || _thread == null || _selectedThread == null) return null;
        ArchiveAssetRow[] rows = SelectedMediaRows().ToArray();
        if (rows.Length == 0) return null;
        ArchivedThreadView thread = _thread;
        return (thread, rows, MediaSelectionEntries(thread));
    }

    private async void OnMediaSelectionTagClick(object? sender, RoutedEventArgs e) =>
        await RunMediaSelectionButtonAsync(ArchiveMediaSelectionTag, (thread, rows, entries, origin) => TagMediaSelectionAsync(thread, rows, entries, origin));

    private async void OnMediaSelectionQueueClick(object? sender, RoutedEventArgs e) =>
        await RunMediaSelectionButtonAsync(ArchiveMediaSelectionQueue, (thread, rows, entries, origin) => AddMediaSelectionToQueueAsync(thread, rows, entries));

    private async void OnMediaSelectionGalleryClick(object? sender, RoutedEventArgs e) =>
        await RunMediaSelectionButtonAsync(ArchiveMediaSelectionGallery, (thread, rows, entries, origin) => OpenMediaSelectionGalleryAsync(thread, rows, entries, origin));

    private async void OnMediaSelectionExportClick(object? sender, RoutedEventArgs e) =>
        await RunMediaSelectionButtonAsync(ArchiveMediaSelectionExport, (thread, rows, _, origin) => ExportMediaSelectionAsync(thread, rows, origin));

    private void OnMediaSelectionClearClick(object? sender, RoutedEventArgs e)
    {
        ClearMediaSelection();
        // The button is gone with the selection: focus goes back to the list the files are in.
        VisibleMediaList.Focus();
    }

    private async Task RunMediaSelectionButtonAsync(Button button, Func<ArchivedThreadView, ArchiveAssetRow[], GalleryEntry[], Control, Task> action)
    {
        if (!button.IsEnabled || FreezeMediaSelection() is not { } frozen) return;
        FocusSelectedMedia();
        Action<string> reportError = CaptureActionErrorReporter();
        try { await action(frozen.Thread, frozen.Rows, frozen.Entries, button); }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception error) { reportError(error.Message); }
    }

    private QuickTagPopover? _mediaSelectionTagPopover;
    internal QuickTagPopover? MediaSelectionTagPopover => _mediaSelectionTagPopover;

    /// <summary>
    /// Tags the selected files in the quick tag popover on the button that asked (tri-state chips over every occurrence, one write per
    /// change). "Advanced…" opens the dialog that lists the occurrences (<see cref="TagMediaSelectionDialogAsync"/>).
    /// </summary>
    private async Task TagMediaSelectionAsync(ArchivedThreadView thread, ArchiveAssetRow[] rows, GalleryEntry[] entries, Control origin,
        QuickTagInitialFocus initialFocus = QuickTagInitialFocus.TagInput, ArchiveMediaSelection.MediaCount? threadCount = null)
    {
        if (_mediaSelectionBusy || MediaTagServices is not { } services
            || SelectionTagContext(thread, rows, entries) is not { } selectionContext) return;
        MediaSelectionTagSession session = NewMediaSelectionTagSession(thread, rows, entries, services);
        ResourceId threadId = thread.Summary.Id;
        // The shell's saved limits, ranking and shared stamp (unbound: the defaults); the session takes the undo depth and the stamp.
        QuickTagPopoverOptions options = TagPopoverOptions?.Invoke(selectionContext) ?? QuickTagPopoverOptions.Defaults(selectionContext);
        using QuickTagSession quick = options.NewSession(QuickTagTarget.For(session));
        _mediaSelectionBusy = true;
        UpdateActions();
        QuickTagPopoverCloseReason reason = QuickTagPopoverCloseReason.Stale;
        QuickTagPopover? shown = null;
        bool released = false;
        // The busy flag ends with the popover (R right after Esc opens it again), not with this method's continuation.
        void Release()
        {
            if (released) return;
            released = true;
            if (shown == null || ReferenceEquals(_mediaSelectionTagPopover, shown)) _mediaSelectionTagPopover = null;
            _mediaSelectionBusy = false;
            if (!_ended) UpdateActions();
        }
        try
        {
            // Favourite and rating of every selected file ride on top (the shell's decision session over the same entries).
            HashSet<GalleryAssetKey> selectedKeys = rows.Select(row => new GalleryAssetKey(threadId, row.Manifest.Directory, row.Asset.AssetId)).ToHashSet();
            GalleryEntry[] decided = entries.Where(entry => selectedKeys.Contains(entry.Key)).DistinctBy(entry => entry.Key).ToArray();
            QuickTagPopover popover = QuickTagPopover.Open(origin, quick, threadCount?.Text ?? Texts.T("media.selection.count", rows.Length), options with
            {
                ReadSourceTags = _sourceTagSuggestions.Read,
                IsCurrent = () => !_ended && _available && ReferenceEquals(_thread, thread) && _selectedThread == threadId,
                Name = "ArchiveSelectionTagPopover",
                TooltipKeyContext = "reader",
                FocusFallback = () => threadCount != null && origin.Focus() || FocusAfterRateAndTag(),
                Decision = DecisionFor?.Invoke(decided, initialFocus)
            });
            shown = popover;
            _mediaSelectionTagPopover = popover;
            popover.WhenClosed(Release);
            reason = await popover.Result;
        }
        finally { Release(); }
        if (reason == QuickTagPopoverCloseReason.Advanced && !_ended) await TagMediaSelectionDialogAsync(thread, rows, entries, origin);
    }

    private static TagContext? SelectionTagContext(ArchivedThreadView thread, ArchiveAssetRow[] rows, GalleryEntry[] entries)
    {
        if (rows.Length == 0) return null;
        GalleryAssetKey leadKey = ArchiveMediaSelection.KeyOf(thread.Summary, rows[0]);
        GalleryEntry lead = entries.FirstOrDefault(entry => entry.Key == leadKey)
            ?? ArchiveMediaSelection.GalleryEntries(thread.Summary, rows, entries)[0];
        return QuickTagPopoverContext.ContextOf(thread, lead, entries) with { Sha256 = null };
    }

    private static MediaSelectionTagSession NewMediaSelectionTagSession(ArchivedThreadView thread, ArchiveAssetRow[] rows,
        GalleryEntry[] entries, MediaSelectionTagServices services) =>
        new(MediaLibraryTagging.Candidates(ArchiveMediaSelection.Tiles(thread.Summary, rows, entries)), services.Load, services.DefineAndSetMany, services.SetMany);

    /// <summary>Adds or removes one own tag on every occurrence of the selected files; the dialog lists them and tags all by default.</summary>
    private async Task TagMediaSelectionDialogAsync(ArchivedThreadView thread, ArchiveAssetRow[] rows, GalleryEntry[] entries, Control origin)
    {
        if (_mediaSelectionBusy || MediaTagServices is not { } services) return;
        if ((TopLevel.GetTopLevel(origin) ?? TopLevel.GetTopLevel(this)) is not Window owner) return;
        IReadOnlyList<MediaLibraryTile> tiles = ArchiveMediaSelection.Tiles(thread.Summary, rows, entries);
        MediaSelectionTagSession session = new(MediaLibraryTagging.Candidates(tiles), services.Load, services.DefineAndSetMany, services.SetMany);
        MediaSelectionTagDialog dialog = new(session, tiles.Count, _sourceTagSuggestions.Read);
        _mediaSelectionTagDialog = dialog;
        _mediaSelectionBusy = true;
        UpdateActions();
        try { await dialog.ShowDialog(owner); }
        finally
        {
            _mediaSelectionTagDialog = null;
            _mediaSelectionBusy = false;
            if (!_ended) UpdateActions();
        }
    }

    /// <summary>Q with several files selected: the same action and gates as the selection bar's "Add to tagging queue" button.</summary>
    private void QueueSelectionByKey()
    {
        if (AddToTagQueue == null) { Message(MediaMenuReasons.Unavailable); return; }
        if (_mediaSelectionBusy || FreezeMediaSelection() is not { } frozen) return;
        Action<string> reportError = CaptureActionErrorReporter();
        OpenActions.Start(QueueAsync, "archive.media-selection.queue");
        async Task QueueAsync()
        {
            try { await AddMediaSelectionToQueueAsync(frozen.Thread, frozen.Rows, frozen.Entries); }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
            catch (Exception error) { reportError(error.Message); }
        }
    }

    /// <summary>Adds the selected files (one per file, in the tab's order) to the tagging queue and says what happened.</summary>
    private async Task AddMediaSelectionToQueueAsync(ArchivedThreadView thread, ArchiveAssetRow[] rows, GalleryEntry[] entries)
    {
        if (AddToTagQueue is not { } add) return;
        IReadOnlyList<GalleryEntry> selected = ArchiveMediaSelection.GalleryEntries(thread.Summary, rows, entries);
        string message = await add(selected);
        if (!_ended) Message(message);
    }

    /// <summary>Opens the gallery with exactly the selected files in the tab's order, starting at the first.</summary>
    private async Task OpenMediaSelectionGalleryAsync(ArchivedThreadView thread, ArchiveAssetRow[] rows, GalleryEntry[] entries, Control origin)
    {
        if (OpenMediaGallery is not { } open) return;
        IReadOnlyList<GalleryEntry> selected = ArchiveMediaSelection.GalleryEntries(thread.Summary, rows, entries);
        if (selected.Count == 0) return;
        await open(selected, selected[0].EntryKey, origin);
    }

    /// <summary>Exports the originals of the selected files: one per file, never one per post that shows it.</summary>
    private Task ExportMediaSelectionAsync(ArchivedThreadView thread, ArchiveAssetRow[] rows, Control origin) =>
        OpenOriginalExportAsync(rows.Select(row => FavoriteExportAsset.From(thread.Summary.Id, row.Manifest, row.Asset)).ToArray(), origin,
            0, Texts.T("media.selection.exportScope"));

    private void EndMediaSelectionLifetime()
    {
        if (_archiveStamp != null) _archiveStamp.Changed -= UpdateMediaSelectionBar;
        _archiveStamp = null;
        ClearArchiveStampRun();
        _threadMediaTarget = null;
        _mediaSelectionTagDialog?.CloseForLifetime();
        _mediaSelectionTagPopover?.Close(QuickTagPopoverCloseReason.Stale);
        _mediaSelectionBusy = false;
    }
}
