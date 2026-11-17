using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using MeowChan.Favorites;
using MeowChan.Gallery;
using MeowChan.Tags;

namespace MeowChan;

// Multi-select on the grid (the ListBox's own Ctrl/Cmd+click, Shift+click and Ctrl/Cmd+A) with three actions: tag together,
// the selection as a gallery, export the originals. Actions always work on the tiles frozen at the moment of the click,
// and only on tiles that are still shown: when filters, order or the index change, selected tiles that are still shown stay
// selected (identified by their bytes) and the others drop out, so nothing hidden is ever acted on.
public sealed partial class MediaPage
{
    // Selection commands lead; the way out and the reserved live counter form the trailing state group.
    // FitSelectionLine keeps both groups on one 32 dip line, moving commands under More when needed.
    private readonly ReservedTextBlock _selectionText = new() { Name = "MediaSelectionStatus", Classes = { "tnum" }, TextWrapping = Avalonia.Media.TextWrapping.NoWrap, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
    private readonly TextBlock _selectionReason = new() { Name = "MediaSelectionReason", Classes = { "muted" }, IsVisible = false, TextWrapping = Avalonia.Media.TextWrapping.NoWrap,
        TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
    // The reason stays after the actions in the fill area, before the trailing state group.
    private readonly StackPanel _selectionActions = new() { Name = "MediaSelectionActions", Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8 };
    private readonly StackPanel _selectionState = new() { Name = "MediaSelectionState", Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8, Margin = new Thickness(8, 0, 0, 0) };
    private readonly DockPanel _selectionRow = new() { Name = "MediaSelectionRow", LastChildFill = true };
    private readonly Button _selTag = new() { Name = "MediaSelectionTag" };
    private readonly Button _selGallery = new() { Name = "MediaSelectionGallery" };
    private readonly Button _selExport = new() { Name = "MediaSelectionExport" };
    private readonly Button _selClear = new() { Name = "MediaSelectionClear" };
    private readonly TextBlock _selectionNote = new() { Name = "MediaSelectionNote", TextWrapping = Avalonia.Media.TextWrapping.Wrap, IsVisible = false, Classes = { "muted" } };
    private MediaSelectionTagDialog? _selectionTag;
    private bool _restoring;
    private bool _selectionBusy;
    private bool _selectionShown;

    /// <summary>Own-tag access for the "tag together" dialog; null disables that action.</summary>
    internal MediaSelectionTagServices? TagServices { get; set; }
    /// <summary>Shows the export dialog for the frozen originals, the number of tiles that had none to offer and the label of what is exported.</summary>
    internal Func<IReadOnlyList<FavoriteExportAsset>, int, string, Task>? ExportOriginals { get; set; }
    internal Window? SelectionTagWindow => _selectionTag;
    private QuickTagPopover? _selectionTagPopover;
    /// <summary>The quick tag popover of the selection while it is open ("Advanced…" in it opens <see cref="SelectionTagWindow"/>).</summary>
    internal QuickTagPopover? SelectionTagPopover => _selectionTagPopover;
    /// <summary>Hashes the unhashed folder source files among the given entries (a user action) and returns their entries with digest.</summary>
    internal Func<IReadOnlyList<MediaLibraryEntry>, CancellationToken, Task<IReadOnlyDictionary<MediaLibraryEntry, MediaLibraryEntry>>>? HashFolderFilesAsync { get; set; }

    /// <summary>The selected tiles in page order. Selected tiles are always among the shown ones.</summary>
    internal IReadOnlyList<MediaLibraryTile> SelectedTiles => MediaLibrarySelection.Pick(_tiles, _grid.Selection.SelectedIndexes);

    private Control BuildSelectionBar()
    {
        AutomationProperties.SetLiveSetting(_selectionText, AutomationLiveSetting.Polite);
        AutomationProperties.SetLiveSetting(_selectionReason, AutomationLiveSetting.Polite);
        AutomationProperties.SetLiveSetting(_selectionNote, AutomationLiveSetting.Polite);
        // The action row exists only while something is selected: the counter and the actions share one line (the how-to is the tag action's tooltip).
        // It is as high as the summary line it replaces (Workspace.Size.Control, 32), so the grid does not move when the selection starts or ends.
        _selectionRow.IsVisible = false;
        _selectionRow[!Avalonia.Layout.Layoutable.MinHeightProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("Workspace.Size.Control");
        _selectionText.FontWeight = Avalonia.Media.FontWeight.SemiBold; // the anchor of the line
        _selClear.Classes.Add("subtle"); // the way out weighs less than the actions
        _selClear.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center;
        // MP2: "Add to tagging queue" comes before "Tag together": choosing and queueing is the way into the Tagging page.
        foreach (Control control in new Control[] { _selQueue, _selTag, _selGallery, _selExport, _selMore })
        { control.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center; _selectionActions.Children.Add(control); }
        _selectionState.Children.Add(_selClear);
        _selectionState.Children.Add(_selectionText);
        DockPanel.SetDock(_selectionState, Dock.Right);
        _selectionRow.Children.Add(_selectionState);
        DockPanel.SetDock(_selectionActions, Dock.Left);
        _selectionRow.Children.Add(_selectionActions);
        _selectionRow.Children.Add(_selectionReason);
        _selectionRow.SizeChanged += (_, e) => { if (e.WidthChanged) FitSelectionLine(); };
        _selectionActions.SizeChanged += (_, _) => FitSelectionLine();
        _selectionState.SizeChanged += (_, _) => FitSelectionLine();
        _selTag.Click += async (_, _) => await TagSelectionAsync();
        _selGallery.Click += (_, _) => OpenActions.Start(OpenSelectionGalleryAsync, "media.selection.gallery");
        _selExport.Click += async (_, _) => await ExportSelectionAsync();
        _selClear.Click += (_, _) => ClearSelection();
        // Escape that left the media panel unhandled: its level of the page's ladder (after the focus mode, which MediaPage.Focus.cs ends here first).
        AddHandler(KeyDownEvent, (_, e) => { if (!e.Handled) OnPanelEscape(e); }, Avalonia.Interactivity.RoutingStrategies.Bubble);
        return new StackPanel { Spacing = 2, Children = { _selectionRow, _selectionNote, BuildQueueAsk() } };
    }

    private void LocalizeSelection()
    {
        _selTag.Content = Texts.T("media.selection.tag");
        _selGallery.Content = Texts.T("media.selection.gallery");
        _selExport.Content = Texts.T("media.selection.export");
        ApplyClearLabel();
        ResetSelectionFit();
        LocalizeQueue();
        LocalizeExportAll();
        UpdateSelectionBar();
    }

    internal void ClearSelection() => _grid.Selection.Clear();

    // Escape (fixed) ends the focus mode, then dismisses the "export all" question, then clears (the panel's level is OnPanelEscape); media.select-all (Ctrl/Cmd+A by default) selects every shown tile; clicks and Shift/Ctrl ranges are the ListBox's own.
    private bool OnSelectionKey(KeyEventArgs e)
    {
        // PT1: a pending "add to tagging queue" question is dismissed before the levels below.
        if (e.Key == Key.Escape && e.KeyModifiers == KeyModifiers.None && _queueAsk.IsVisible)
        {
            DismissQueueAsk();
            e.Handled = true;
            return true;
        }
        if (e.Key == Key.Escape && e.KeyModifiers == KeyModifiers.None)
        {
            // The levels in order (MediaFocusMode.Escape): the focus mode, a pending "export all" question, the selection (the grid has the focus, not the panel).
            switch (MediaFocusMode.Escape(_focusMode, panelFocused: false, _exportAllAsk.IsVisible, _grid.Selection.Count))
            {
                case MediaEscapeStep.ExitFocus: SetFocusMode(false); break;
                case MediaEscapeStep.DismissExportAll: DismissExportAllAsk(); break;
                case MediaEscapeStep.ClearSelection: ClearSelection(); break;
                default: return false;
            }
            e.Handled = true;
            return true;
        }
        if (CommandBindings.Matches(e, "media.select-all") && _tiles.Count > 0)
        {
            _grid.Selection.SelectAll();
            e.Handled = true;
            return true;
        }
        return false;
    }

    // Escape in the media panel: the focus mode ends first (its own page handler), then the keyboard goes back to the grid, whose handler takes the next
    // levels (the question, the selection) with the following Escape. Anywhere else on the page this handler leaves Escape alone.
    private bool OnPanelEscape(KeyEventArgs e)
    {
        if (e.Key != Key.Escape || e.KeyModifiers != KeyModifiers.None || !_panelShown || _panel?.IsKeyboardFocusWithin != true) return false;
        if (MediaFocusMode.Escape(_focusMode, panelFocused: true, _exportAllAsk.IsVisible, _grid.Selection.Count) != MediaEscapeStep.ReturnToGrid) return false;
        FocusGrid();
        e.Handled = true;
        return true;
    }

    // While rows are being replaced (a presentation runs in the background), Ctrl+A means every tile of the new rows: the grid
    // selects the old ones now, and ApplyPresent carries "everything" over, not only the old tiles that remain. Without a running
    // presentation the grid's own Ctrl+A applies.
    private void OnSelectAllWhilePresenting(object? sender, KeyEventArgs e)
    {
        if (e.Handled || !_presentPending || _tiles.Count == 0 || !CommandBindings.Matches(e, "media.select-all")) return;
        _grid.Selection.SelectAll();
        _selectAllStamp = _selectionStamp;
        e.Handled = true;
    }

    private void UpdateSelectionBar()
    {
        // The status line only needs two numbers: the grid knows the count, and the unsaved ones are one pass over the positions
        // (no tile list is built, which for a selected library of 200,000 tiles would be copied and sorted on every update).
        int count = _grid.Selection.Count, notSaved = count == 0 ? 0 : NotSavedSelected();
        bool building = _building != null;
        string? reason = count == 0 ? Texts.T("media.selection.reason.none") : building ? Texts.T("media.selection.reason.building") : null;
        bool hasSelection = count > 0;
        _selectionRow.IsVisible = hasSelection;
        if (hasSelection != _selectionShown)
        {
            // The actions take the place of the summary line (or give it back): MediaPage.Layout.cs decides which rows show.
            _selectionShown = hasSelection;
            ApplyLayoutPlan();
        }
        _selectionText.ReserveText = Texts.T("media.selection.count", CounterReserveSample(_tiles.Count));
        _selectionText.Text = count == 0 ? string.Empty
            : notSaved > 0 ? Texts.T("media.selection.countNotSaved", count, notSaved) : Texts.T("media.selection.count", count);
        // After the last action, never before the actions: they keep their place whether the index is being built or not. The full sentence is the
        // tooltip of the (possibly cut) reason and the accessible description of the disabled actions (SetAction).
        string? buildingReason = count > 0 && building ? Texts.T("media.selection.reason.building") : null;
        _selectionReason.Text = buildingReason;
        _selectionReason.IsVisible = buildingReason != null;
        ToolTip.SetTip(_selectionReason, buildingReason);
        bool ready = reason == null && !_selectionBusy && !_lifetime.IsCancellationRequested;
        SetAction(_selTag, ready && TagServices != null, reason, TagServices != null, "media.selection.tag.hint", Texts.T("media.selection.howTo"));
        UpdateQueueActions(ready, reason);
        SetAction(_selGallery, ready && OpenGallery != null, reason, OpenGallery != null, "media.selection.gallery.hint");
        SetAction(_selExport, ready && ExportOriginals != null, reason, ExportOriginals != null, "media.selection.export.hint");
        _selClear.IsEnabled = count > 0;
        UpdateExportAll();
        RefreshWorkView();
        if (count == 0 && !_selectionBusy) { _folderNoteShown = false; ShowSelectionNote(null); }
        UpdateFolderVersionNote(count);
    }

    /// <summary>The widest count the counter keeps room for: as many 8s as the shown tile count has digits (a selection cannot be larger), 1 to 4.</summary>
    internal static int CounterReserveSample(int shownTiles)
    {
        int digits = Math.Clamp(Math.Max(0, shownTiles).ToString(System.Globalization.CultureInfo.InvariantCulture).Length, 1, 4);
        return digits switch { 1 => 8, 2 => 88, 3 => 888, _ => 8888 };
    }

    // How far the line gave way to its width (FitSelectionLine): 0 all actions with "Clear selection", 1 "Clear", 2 Export under More, 3 Tag together
    // too, 4 Add to tagging queue too. Never more than the line needs, decided from the full widths, not from what shows now.
    private int _selectionLevel;
    private readonly Dictionary<Control, double> _selectionWidths = new();

    /// <summary>Whether "Clear selection" says "Clear" now because the line has no room for the full label (tests and smokes).</summary>
    internal bool ClearLabelShortened => _selectionLevel >= 1;

    /// <summary>The actions the line's width moved under its More (⋯), in the order they leave (tests and smokes).</summary>
    internal IReadOnlyList<Control> SelectionActionsMovedByWidth => SelectionMovedByWidth(_selectionLevel);

    private List<Control> SelectionMovedByWidth(int level)
    {
        List<Control> moved = new();
        if (level >= 2) moved.Add(_selExport);
        if (level >= 3) moved.Add(_selTag);
        if (level >= 4) moved.Add(_selQueue);
        return moved;
    }

    private void ApplyClearLabel()
    {
        string full = Texts.T("media.selection.clear");
        _selClear.Content = _selectionLevel >= 1 ? Texts.T("media.selection.clear.short") : full;
        AutomationProperties.SetName(_selClear, full);
        ToolTip.SetTip(_selClear, full);
    }

    // After the work view decided (RefreshWorkView): what the line's width moves under More is hidden like what the view moves; More lists both.
    private void ApplySelectionLevel()
    {
        foreach (Control moved in SelectionMovedByWidth(_selectionLevel)) moved.IsVisible = false;
    }

    // A new language: every label has another width; measure the line again from the full layout.
    private void ResetSelectionFit()
    {
        _selectionWidths.Clear();
        if (_selectionLevel == 0) return;
        _selectionLevel = 0;
        ApplyClearLabel();
        RefreshWorkView();
    }

    // The selection line stays one line (a second line at the first selection moved the grid): the counter and the actions must fit the line's width.
    // Where they do not, the line gives way in steps: "Clear selection" says "Clear" (full text as tooltip and accessible name), then Export, Tag
    // together and Add to tagging queue move under the line's More (⋯). Every step is computed from the full widths (remembered while each part showed),
    // whichever step shows now, so it cannot switch back and forth.
    private void FitSelectionLine()
    {
        if (_lifetime.IsCancellationRequested || !_selectionRow.IsVisible || TopLevel.GetTopLevel(_selectionRow) == null) return;
        double available = _selectionRow.Bounds.Width;
        if (available <= 0) return;
        foreach (Control child in _selectionActions.Children.Concat(_selectionState.Children))
            if (child.IsVisible && child.IsMeasureValid && child.DesiredSize.Width > 0 && !(ReferenceEquals(child, _selClear) && _selectionLevel >= 1))
                _selectionWidths[child] = child.DesiredSize.Width;
        if (!_selectionWidths.TryGetValue(_selClear, out double clearFull)) return; // not laid out with its full label yet
        double clearShort = clearFull - Math.Max(0, LabelWidth(Texts.T("media.selection.clear")) - LabelWidth(Texts.T("media.selection.clear.short")));
        bool viewOverflow = _selectionViewOverflow.Count > 0;
        double Width(Control control) => _selectionWidths.TryGetValue(control, out double width) ? width : control.DesiredSize.Width;
        double Needed(int level)
        {
            List<Control> moved = SelectionMovedByWidth(level);
            List<double> parts = new() { Width(_selectionText) };
            foreach (Control action in new Control[] { _selQueue, _selTag, _selGallery, _selExport })
                if (!_selectionViewOverflow.Contains(action) && !moved.Contains(action)) parts.Add(Width(action));
            if (viewOverflow || moved.Count > 0) parts.Add(_selectionWidths.TryGetValue(_selMore, out double more) ? more : 32);
            parts.Add(level >= 1 ? clearShort : clearFull);
            return parts.Sum() + _selectionActions.Spacing * (parts.Count - 1);
        }
        int level = 0;
        while (level < 4 && Needed(level) > available + 0.5) level++;
        if (level == _selectionLevel) return;
        _selectionLevel = level;
        ApplyClearLabel();
        RefreshWorkView();
    }

    // The width of a label in the clear button's font (the button is in a window: text shaping is there).
    private double LabelWidth(string text)
    {
        TextBlock measure = new() { Text = text, FontFamily = _selClear.FontFamily, FontSize = _selClear.FontSize, FontWeight = _selClear.FontWeight, FontStyle = _selClear.FontStyle };
        measure.Measure(Size.Infinity);
        return measure.DesiredSize.Width;
    }

    private int NotSavedSelected()
    {
        int notSaved = 0, shown = _tiles.Count;
        foreach (int index in _grid.Selection.SelectedIndexes)
            if (index >= 0 && index < shown && _tiles[index].Entry.Presence != MediaLibraryPresence.Present) notSaved++;
        return notSaved;
    }

    /// <summary>Selects the given positions (ascending as <see cref="MediaLibrarySelection.Restore"/> gives them) as ranges, not one by one.</summary>
    private void SelectIndexes(IReadOnlyList<int> indexes)
    {
        int at = 0;
        while (at < indexes.Count)
        {
            int start = indexes[at], end = start;
            for (at++; at < indexes.Count && indexes[at] == end + 1; at++) end = indexes[at];
            if (start == end) _grid.Selection.Select(start); else _grid.Selection.SelectRange(start, end);
        }
    }

    // A disabled action says why in its tooltip and accessible description.
    // The enabled tip may carry a second line (how to select), which is never part of a reason.
    private static void SetAction(Button button, bool enabled, string? reason, bool available, string hintKey, string? moreHint = null)
    {
        button.IsEnabled = enabled;
        string hint = enabled ? Texts.T(hintKey) + (moreHint == null ? string.Empty : "\n" + moreHint)
            : reason ?? (available ? string.Empty : Texts.T("media.selection.reason.unavailable"));
        ToolTip.SetTip(button, hint.Length == 0 ? null : hint);
        AutomationProperties.SetHelpText(button, hint);
    }

    private void ShowSelectionNote(string? text)
    {
        _selectionNote.Text = text;
        _selectionNote.IsVisible = !string.IsNullOrEmpty(text);
    }

    /// <summary>Opens the gallery with exactly the selected tiles in page order, one file per tile (the file the tile shows).</summary>
    internal Task OpenSelectionGalleryAsync() => OpenSelectionGalleryAtAsync(null);

    // A focused tile opens the same selection, but starts at that tile, not index zero.
    private async Task OpenSelectionGalleryAtAsync(MediaLibraryEntry? focused)
    {
        if (!_selGallery.IsEnabled || OpenGallery == null) return;
        (IReadOnlyList<MediaLibraryEntry> entries, int notSaved) = MediaLibrarySelection.GalleryEntries(SelectedTiles);
        ShowSelectionNote(notSaved == 0 ? null : entries.Count == 0 ? Texts.T("media.selection.note.noneSaved")
            : Texts.Count("media.selection.note.galleryPartial", entries.Count, entries.Count, notSaved));
        if (entries.Count == 0) return;
        _ = PausePanelVideo();
        int start = focused == null ? 0 : entries.ToList().FindIndex(entry => entry == focused);
        if (start < 0) return; // Never substitute the first file for an unavailable focused medium.
        await OpenGallery(entries, start);
    }

    /// <summary>Exports the originals of the selection: one per tile, never one per duplicate.</summary>
    internal async Task ExportSelectionAsync()
    {
        if (!_selExport.IsEnabled || ExportOriginals == null) return;
        await ExportTilesAsync(SelectedTiles, Texts.T("media.selection.exportScope"));
    }

    // The one export path of the page, for the selection and for "export all": the frozen tiles become the export plan, the plan goes to the dialog.
    private async Task ExportTilesAsync(IReadOnlyList<MediaLibraryTile> tiles, string scope, int? preparing = null)
    {
        if (ExportOriginals is not { } export) return;
        DismissExportAllAsk();
        _selectionBusy = true;
        UpdateSelectionBar();
        if (preparing is { } count) ShowSelectionNote(Texts.Count("media.exportAll.preparing", count, count.ToString("N0", Texts.Culture)));
        try
        {
            // Reading the manifests is IO and a large plan is a long loop: neither belongs on the UI thread. The plan holds no controls.
            MediaLibraryExportPlan plan = await Task.Run(() => MediaLibraryExport.BuildAsync(tiles, _openManifest, _lifetime.Token), _lifetime.Token);
            ShowSelectionNote(plan.Unavailable == 0 ? null : Texts.Count("viewer.favorites.unavailable", plan.Unavailable));
            await export(plan.Assets, plan.Unavailable, scope);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception error) when (error is System.IO.IOException or System.IO.InvalidDataException or UnauthorizedAccessException)
        {
            ShowSelectionNote(Texts.T("media.selection.note.exportFailed"));
        }
        finally
        {
            _selectionBusy = false;
            if (!_lifetime.IsCancellationRequested) UpdateSelectionBar();
        }
    }

    /// <summary>
    /// Tags the selection in the quick tag popover (tri-state chips over every occurrence of the chosen tiles, one write per change).
    /// It opens on <paramref name="anchor"/> (the tile of the menu), by default on the selection bar's button. "Advanced…" opens the
    /// full dialog (<see cref="TagSelectionDialogAsync"/>) to pick occurrences.
    /// </summary>
    internal async Task TagSelectionAsync(Control? anchor = null, QuickTagInitialFocus initialFocus = QuickTagInitialFocus.TagInput)
    {
        if (!_selTag.IsEnabled || TagServices is not { } services) return;
        IReadOnlyList<MediaLibraryTile> tiles = SelectedTiles;
        if (tiles.Count == 0) return;
        static bool Usable(Control? control) => control != null && control.IsAttachedToVisualTree() && control.IsEffectivelyVisible;
        Control host = Usable(anchor) ? anchor! : Usable(_selTag) ? _selTag : _grid;
        MediaSelectionTagSession session = new(MediaLibraryTagging.Candidates(tiles), services.Load, services.DefineAndSetMany, services.SetMany);
        MediaLibraryTileRow? lead = SelectedRow;
        TagContext context = QuickTagPopoverContext.ContextOf(lead?.Entry ?? tiles[0].Entry, _shown, lead?.Index ?? -1, selection: true);
        // The shell's saved limits, ranking and shared stamp (unbound: the defaults); the session takes the undo depth and the stamp.
        QuickTagPopoverOptions options = TagPopoverOptions?.Invoke(context) ?? QuickTagPopoverOptions.Defaults(context);
#pragma warning disable CA2000 // QuickTagPopover owns the session passed to Open until the popover closes.
        QuickTagSession quick = options.NewSession(QuickTagTarget.For(session));
#pragma warning restore CA2000
        _selectionBusy = true;
        UpdateSelectionBar();
        QuickTagPopoverCloseReason reason = QuickTagPopoverCloseReason.Stale;
        Control? origin = null;
        QuickTagPopover? shown = null;
        bool released = false;
        // The busy flag ends with the popover, not with this method's continuation: R or Ctrl+Shift+T right after Esc found the page still busy.
        void Release()
        {
            if (released) return;
            released = true;
            if (shown == null || ReferenceEquals(_selectionTagPopover, shown)) _selectionTagPopover = null;
            _selectionBusy = false;
            if (!_lifetime.IsCancellationRequested) UpdateSelectionBar();
        }
        try
        {
            QuickTagPopover popover = QuickTagPopover.Open(host, quick, Texts.T("media.selection.count", tiles.Count), options with
            {
                ReadSourceTags = ReadBooruCatalog,
                IsCurrent = () => !_lifetime.IsCancellationRequested && IsEffectivelyVisible,
                // The keyboard goes back at once (↑/↓ and R right after Esc); the page places it again below once its tiles were rebuilt.
                RestoreFocus = true,
                FocusFallback = () => FocusGridNow(),
                TooltipKeyContext = "media",
                // The rating (and favourite where the shell offers it) of every selected tile rides on top of the tag field.
                Decision = DecisionForEntries?.Invoke(tiles.Select(tile => tile.Entry).ToArray(), initialFocus)
            });
            origin = popover.Origin;
            shown = popover;
            _selectionTagPopover = popover;
            popover.WhenClosed(Release);
            reason = await popover.Result;
        }
        finally
        {
            Release();
            if (!_lifetime.IsCancellationRequested)
            {
                // The new tags show at once in filters and details; the tiles are rebuilt, so the focus is placed again. A popover opened again in the
                // meantime keeps its anchor: the rebuild waits until it closed.
                if (session.Changed && QuickTagPopover.Current is { } next) next.WhenClosed(() => { if (!_lifetime.IsCancellationRequested) RefreshUserData(); });
                else if (session.Changed) RefreshUserData();
                UpdateSelectionBar();
                if (reason != QuickTagPopoverCloseReason.Advanced && QuickTagPopover.Current == null)
                    RestoreMediaMenuActionFocus(origin, () => !OwnedWindowOpen() && QuickTagPopover.Current == null);
            }
        }
        if (reason == QuickTagPopoverCloseReason.Advanced && !_lifetime.IsCancellationRequested) await TagSelectionDialogAsync();
    }

    private bool OwnedWindowOpen() => (_owner ?? TopLevel.GetTopLevel(this) as Window)?.OwnedWindows.Any(window => window.IsVisible) == true;

    /// <summary>Adds or removes an own tag on the occurrences of the selection; the dialog lists them and tags all by default.</summary>
    internal async Task TagSelectionDialogAsync()
    {
        if (!_selTag.IsEnabled || TagServices is not { } services) return;
        if ((_owner ?? TopLevel.GetTopLevel(this) as Window) is not { } owner) return;
        IReadOnlyList<MediaLibraryTile> tiles = SelectedTiles;
        tiles = await WithHashedFolderFilesAsync(tiles);
        if (_lifetime.IsCancellationRequested) return;
        MediaSelectionTagSession session = new(MediaLibraryTagging.Candidates(tiles), services.Load, services.DefineAndSetMany, services.SetMany);
        MediaSelectionTagDialog dialog = new(session, tiles.Count, ReadBooruCatalog);
        _selectionTag = dialog;
        _selectionBusy = true;
        UpdateSelectionBar();
        try { await dialog.ShowDialog(owner); }
        finally
        {
            _selectionTag = null;
            _selectionBusy = false;
            if (!_lifetime.IsCancellationRequested)
            {
                RefreshUserData(); // The new tags show at once in filters and details.
                UpdateSelectionBar();
            }
        }
    }
}

public sealed partial class MediaPage
{
    // A folder source file can carry a tag only once its bytes are hashed: the selection's unhashed folder files are hashed
    // first (the note says so), the tiles then carry their entries with digest. Nothing else of the selection changes.
    private async Task<IReadOnlyList<MediaLibraryTile>> WithHashedFolderFilesAsync(IReadOnlyList<MediaLibraryTile> tiles)
    {
        if (HashFolderFilesAsync is not { } hash) return tiles;
        MediaLibraryEntry[] unhashed = tiles.SelectMany(tile => tile.Occurrences.Append(tile.Entry))
            .Where(entry => entry.FolderItemId != null && entry.Sha256 == null && entry.Presence == MediaLibraryPresence.Present).Distinct().ToArray();
        if (unhashed.Length == 0) return tiles;
        _selectionBusy = true;
        UpdateSelectionBar();
        ShowSelectionNote(Texts.T("folderSource.selection.hashing", unhashed.Length.ToString("N0", Texts.Culture)));
        try
        {
            IReadOnlyDictionary<MediaLibraryEntry, MediaLibraryEntry> hashed = await hash(unhashed, _lifetime.Token);
            MediaLibraryEntry Map(MediaLibraryEntry entry) => hashed.TryGetValue(entry, out MediaLibraryEntry? updated) ? updated : entry;
            return tiles.Select(tile => tile with { Entry = Map(tile.Entry), Occurrences = tile.Occurrences.Select(Map).ToArray() }).ToArray();
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { return tiles; }
        finally
        {
            _selectionBusy = false;
            ShowSelectionNote(null);
            if (!_lifetime.IsCancellationRequested) UpdateSelectionBar();
        }
    }
}

/// <summary>What the page needs from the host to write own media tags.</summary>
internal sealed record MediaSelectionTagServices(Func<PrivateTagLoadSnapshot> Load,
    Func<string, IReadOnlyList<MediaTagSubject>, long?, CancellationToken, Task<PrivateTagCommit>> DefineAndSetMany,
    Func<TagId, IReadOnlyList<MediaTagSubject>, bool, long?, CancellationToken, Task<PrivateTagCommit>> SetMany);
