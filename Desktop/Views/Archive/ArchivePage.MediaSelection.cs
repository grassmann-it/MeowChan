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
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
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
        AutomationProperties.SetLiveSetting(ArchiveMediaSelectionStatus, AutomationLiveSetting.Polite);
    }

    private void NoteMediaLead(object? source)
    {
        if (source is not Visual visual) return;
        ArchiveAssetRow? row = visual.GetSelfAndVisualAncestors().OfType<Control>().Select(control => control.DataContext)
            .OfType<ArchiveAssetRow>().FirstOrDefault();
        if (row != null) _mediaLead = row;
    }

    private Control VisibleMediaList => _mediaTiles ? ArchiveMediaTiles : ArchiveMediaGrid;

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
        if (e.Key == Key.Escape && e.KeyModifiers == KeyModifiers.None && ArchiveMediaSelection.ShowsStatus(SelectedMediaCount))
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

    // From two selected files on, a small line says how many are selected and offers the selection actions and "clear selection".
    private void UpdateMediaSelectionBar()
    {
        int count = _ended ? 0 : SelectedMediaCount;
        bool show = ArchiveMediaSelection.ShowsStatus(count);
        ArchiveMediaSelectionBar.IsVisible = show;
        if (!show) return;
        ArchiveMediaSelectionStatus.Text = ArchiveMediaSelection.Status(count);
        bool ready = _available && !_busy && !_ended && !IsReaderDialogOpen && !_mediaSelectionBusy && _thread != null;
        SetMediaSelectionAction(ArchiveMediaSelectionTag, ready && MediaTagServices != null, MediaTagServices != null, "media.selection.tag.hint");
        SetMediaSelectionAction(ArchiveMediaSelectionQueue, ready && AddToTagQueue != null, AddToTagQueue != null, "media.selection.queue.hint");
        SetMediaSelectionAction(ArchiveMediaSelectionGallery, ready && OpenMediaGallery != null, OpenMediaGallery != null, "media.selection.gallery.hint");
        SetMediaSelectionAction(ArchiveMediaSelectionExport, ready, true, "media.selection.export.hint");
        ArchiveMediaSelectionClear.IsEnabled = !_ended;
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
        GalleryEntry[] entries = GallerySelection.Build(thread, _assets.Select(row => row.Manifest).DistinctBy(manifest => manifest.Directory),
            true, includeOtherMedia: true).Entries.ToArray();
        return (thread, rows, entries);
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
    private async Task TagMediaSelectionAsync(ArchivedThreadView thread, ArchiveAssetRow[] rows, GalleryEntry[] entries, Control origin)
    {
        if (_mediaSelectionBusy || MediaTagServices is not { } services) return;
        IReadOnlyList<MediaLibraryTile> tiles = ArchiveMediaSelection.Tiles(thread.Summary, rows, entries);
        if (tiles.Count == 0 || rows.Length == 0) return;
        MediaSelectionTagSession session = new(MediaLibraryTagging.Candidates(tiles), services.Load, services.DefineAndSetMany, services.SetMany);
        QuickTagSession quick = new(QuickTagTarget.For(session));
        GalleryAssetKey leadKey = new(thread.Summary.Id, rows[0].Manifest.Directory, rows[0].Asset.AssetId);
        if ((entries.FirstOrDefault(entry => entry.Key == leadKey) ?? entries.FirstOrDefault()) is not { } lead) return;
        ResourceId threadId = thread.Summary.Id;
        TagContext selectionContext = QuickTagPopoverContext.ContextOf(lead, entries, thread.Summary.Title) with { Sha256 = null };
        _mediaSelectionBusy = true;
        UpdateActions();
        QuickTagPopoverCloseReason reason = QuickTagPopoverCloseReason.Stale;
        try
        {
            QuickTagPopover popover = QuickTagPopover.Open(origin, quick, Texts.T("media.selection.count", rows.Length), (TagPopoverOptions?.Invoke(selectionContext) ?? QuickTagPopoverOptions.Defaults(selectionContext)) with
            {
                ReadSourceTags = _sourceTagSuggestions.Read,
                IsCurrent = () => !_ended && _available && ReferenceEquals(_thread, thread) && _selectedThread == threadId,
                Name = "ArchiveSelectionTagPopover"
            });
            _mediaSelectionTagPopover = popover;
            reason = await popover.Result;
        }
        finally
        {
            _mediaSelectionTagPopover = null;
            _mediaSelectionBusy = false;
            if (!_ended) UpdateActions();
        }
        if (reason == QuickTagPopoverCloseReason.Advanced && !_ended) await TagMediaSelectionDialogAsync(thread, rows, entries, origin);
    }

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
        _mediaSelectionTagDialog?.CloseForLifetime();
        _mediaSelectionTagPopover?.Close(QuickTagPopoverCloseReason.Stale);
        _mediaSelectionBusy = false;
    }
}
