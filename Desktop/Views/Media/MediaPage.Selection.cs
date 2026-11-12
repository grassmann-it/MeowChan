using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using MeowChan.Desktop;
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
    private readonly TextBlock _selectionText = new() { Name = "MediaSelectionStatus", TextWrapping = Avalonia.Media.TextWrapping.Wrap, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
    private readonly Button _selTag = new() { Name = "MediaSelectionTag" };
    private readonly Button _selGallery = new() { Name = "MediaSelectionGallery" };
    private readonly Button _selExport = new() { Name = "MediaSelectionExport" };
    private readonly Button _selClear = new() { Name = "MediaSelectionClear" };
    private readonly TextBlock _selectionNote = new() { Name = "MediaSelectionNote", TextWrapping = Avalonia.Media.TextWrapping.Wrap, IsVisible = false, Classes = { "muted" } };
    private MediaSelectionTagDialog? _selectionTag;
    private bool _restoring;
    private bool _selectionBusy;

    /// <summary>Own-tag access for the "tag together" dialog; null disables that action.</summary>
    internal MediaSelectionTagServices? TagServices { get; set; }
    /// <summary>Shows the export dialog for the frozen originals, the number of tiles that had none to offer and the label of what is exported.</summary>
    internal Func<IReadOnlyList<FavoriteExportAsset>, int, string, Task>? ExportOriginals { get; set; }
    internal Window? SelectionTagWindow => _selectionTag;

    /// <summary>The selected tiles in page order. Selected tiles are always among the shown ones.</summary>
    internal IReadOnlyList<MediaLibraryTile> SelectedTiles => MediaLibrarySelection.Pick(_tiles, _grid.Selection.SelectedIndexes);

    private Control BuildSelectionBar()
    {
        AutomationProperties.SetLiveSetting(_selectionText, AutomationLiveSetting.Polite);
        AutomationProperties.SetLiveSetting(_selectionNote, AutomationLiveSetting.Polite);
        WrapPanel bar = new() { ItemSpacing = 6, LineSpacing = 4 };
        foreach (Control control in new Control[] { _selectionText, _selTag, _selGallery, _selExport, _selClear })
        { control.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center; bar.Children.Add(control); }
        _selTag.Click += async (_, _) => await TagSelectionAsync();
        _selGallery.Click += async (_, _) => await OpenSelectionGalleryAsync();
        _selExport.Click += async (_, _) => await ExportSelectionAsync();
        _selClear.Click += (_, _) => ClearSelection();
        return new StackPanel { Spacing = 2, Children = { bar, _selectionNote } };
    }

    private void LocalizeSelection()
    {
        _selTag.Content = Texts.T("media.selection.tag");
        _selGallery.Content = Texts.T("media.selection.gallery");
        _selExport.Content = Texts.T("media.selection.export");
        _selClear.Content = Texts.T("media.selection.clear");
        LocalizeExportAll();
        UpdateSelectionBar();
    }

    internal void ClearSelection() => _grid.Selection.Clear();

    // Escape clears and Ctrl/Cmd+A selects every shown tile; clicks and Shift/Ctrl ranges are the ListBox's own.
    private bool OnSelectionKey(KeyEventArgs e)
    {
        if (e.Key == Key.Escape && e.KeyModifiers == KeyModifiers.None && _exportAllAsk.IsVisible)
        {
            DismissExportAllAsk();
            e.Handled = true;
            return true;
        }
        if (e.Key == Key.Escape && e.KeyModifiers == KeyModifiers.None && _grid.Selection.Count > 0)
        {
            ClearSelection();
            e.Handled = true;
            return true;
        }
        if (e.Key == Key.A && e.KeyModifiers is KeyModifiers.Control or KeyModifiers.Meta && _tiles.Count > 0)
        {
            _grid.Selection.SelectAll();
            e.Handled = true;
            return true;
        }
        return false;
    }

    private void UpdateSelectionBar()
    {
        IReadOnlyList<MediaLibraryTile> selected = SelectedTiles;
        int count = selected.Count, notSaved = selected.Count(tile => tile.Entry.Presence != MediaLibraryPresence.Present);
        bool building = _building != null;
        string? reason = count == 0 ? Texts.T("media.selection.reason.none") : building ? Texts.T("media.selection.reason.building") : null;
        _selectionText.Text = count == 0 ? Texts.T("media.selection.none")
            : (notSaved > 0 ? Texts.T("media.selection.countNotSaved", count, notSaved) : Texts.T("media.selection.count", count))
              + (building ? " · " + Texts.T("media.selection.reason.building") : string.Empty);
        bool ready = reason == null && !_selectionBusy && !_lifetime.IsCancellationRequested;
        SetAction(_selTag, ready && TagServices != null, reason, TagServices != null, "media.selection.tag.hint");
        SetAction(_selGallery, ready && OpenGallery != null, reason, OpenGallery != null, "media.selection.gallery.hint");
        SetAction(_selExport, ready && ExportOriginals != null, reason, ExportOriginals != null, "media.selection.export.hint");
        _selClear.IsEnabled = count > 0;
        UpdateExportAll();
        if (count == 0 && !_selectionBusy) ShowSelectionNote(null);
    }

    // A disabled action says why in its tooltip and accessible description.
    private static void SetAction(Button button, bool enabled, string? reason, bool available, string hintKey)
    {
        button.IsEnabled = enabled;
        string hint = enabled ? Texts.T(hintKey) : reason ?? (available ? string.Empty : Texts.T("media.selection.reason.unavailable"));
        ToolTip.SetTip(button, hint.Length == 0 ? null : hint);
        AutomationProperties.SetHelpText(button, hint);
    }

    private void ShowSelectionNote(string? text)
    {
        _selectionNote.Text = text;
        _selectionNote.IsVisible = !string.IsNullOrEmpty(text);
    }

    /// <summary>Opens the gallery with exactly the selected tiles in page order, one file per tile (the file the tile shows).</summary>
    internal async Task OpenSelectionGalleryAsync()
    {
        if (!_selGallery.IsEnabled || OpenGallery == null) return;
        (IReadOnlyList<MediaLibraryEntry> entries, int notSaved) = MediaLibrarySelection.GalleryEntries(SelectedTiles);
        ShowSelectionNote(notSaved == 0 ? null : entries.Count == 0 ? Texts.T("media.selection.note.noneSaved")
            : Texts.T("media.selection.note.galleryPartial", entries.Count, notSaved));
        if (entries.Count == 0) return;
        await OpenGallery(entries, 0);
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
        if (preparing is { } count) ShowSelectionNote(Texts.T("media.exportAll.preparing", count.ToString("N0", Texts.Culture)));
        try
        {
            // Reading the manifests is IO and a large plan is a long loop: neither belongs on the UI thread. The plan holds no controls.
            MediaLibraryExportPlan plan = await Task.Run(() => MediaLibraryExport.BuildAsync(tiles, _openManifest, _lifetime.Token), _lifetime.Token);
            ShowSelectionNote(plan.Unavailable == 0 ? null : Texts.T("viewer.favorites.unavailable", plan.Unavailable));
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

    /// <summary>Adds or removes an own tag on the occurrences of the selection; the dialog lists them and tags all by default.</summary>
    internal async Task TagSelectionAsync()
    {
        if (!_selTag.IsEnabled || TagServices is not { } services) return;
        if ((_owner ?? TopLevel.GetTopLevel(this) as Window) is not { } owner) return;
        IReadOnlyList<MediaLibraryTile> tiles = SelectedTiles;
        MediaSelectionTagSession session = new(MediaLibraryTagging.Candidates(tiles), services.Load, services.DefineAndSetMany, services.SetMany);
        MediaSelectionTagDialog dialog = new(session, tiles.Count);
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

/// <summary>What the page needs from the host to write own media tags.</summary>
internal sealed record MediaSelectionTagServices(Func<PrivateTagLoadSnapshot> Load,
    Func<string, IReadOnlyList<MediaTagSubject>, long?, CancellationToken, Task<PrivateTagCommit>> DefineAndSetMany,
    Func<TagId, IReadOnlyList<MediaTagSubject>, bool, long?, CancellationToken, Task<PrivateTagCommit>> SetMany);
