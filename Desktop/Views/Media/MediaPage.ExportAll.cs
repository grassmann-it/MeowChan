using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using MeowChan.Desktop.Localization;
using MeowChan.Gallery;

namespace MeowChan;

// "Alle Treffer exportieren": the originals of every tile the active filter shows right now, through the same export plan and
// dialog as the selection. Count and size come from the index alone; above a limit the page asks first, inline and without
// blocking. The tiles are frozen when the user clicks: a filter change in between drops a pending question instead of
// exporting tiles that are no longer shown.
public sealed partial class MediaPage
{
    private readonly Button _exportAll = new() { Name = "MediaExportAll" };
    private readonly TextBlock _exportAllText = new() { Name = "MediaExportAllConfirmText", TextWrapping = Avalonia.Media.TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _exportAllYes = new() { Name = "MediaExportAllConfirmYes" };
    private readonly Button _exportAllNo = new() { Name = "MediaExportAllConfirmNo" };
    private readonly WrapPanel _exportAllAsk = new() { Name = "MediaExportAllConfirm", ItemSpacing = 8, LineSpacing = 6, IsVisible = false };
    private IReadOnlyList<MediaLibraryTile>? _exportAllPending;

    private (int Files, long Bytes)? _exportAllConfirmAbove;

    /// <summary>The settings-backed threshold (Settings, Storage and limits); read at every export so a change applies at once.</summary>
    internal Func<(int Files, long Bytes)>? ExportAllConfirmSource { get; set; }

    /// <summary>
    /// Count and bytes above which "export all" asks first: the setting, or the former fixed values when the page has none.
    /// Tests may set it; the page never changes it.
    /// </summary>
    internal (int Files, long Bytes) ExportAllConfirmAbove
    {
        get => _exportAllConfirmAbove ?? ExportAllConfirmSource?.Invoke() ?? (MediaLibraryExportSize.ConfirmAboveFiles, MediaLibraryExportSize.ConfirmAboveBytes);
        set => _exportAllConfirmAbove = value;
    }
    internal bool ExportAllAsking => _exportAllAsk.IsVisible;

    private Control BuildExportAllAsk()
    {
        AutomationProperties.SetLiveSetting(_exportAllText, AutomationLiveSetting.Polite);
        _exportAllYes.Classes.Add("primary");
        foreach (Control control in new Control[] { _exportAllText, _exportAllYes, _exportAllNo }) _exportAllAsk.Children.Add(control);
        _exportAll.Click += async (_, _) => await ExportAllAsync();
        _exportAllYes.Click += async (_, _) => await ConfirmExportAllAsync();
        _exportAllNo.Click += (_, _) => DismissExportAllAsk();
        return _exportAllAsk;
    }

    private void LocalizeExportAll()
    {
        _exportAll.Content = Texts.T("media.exportAll");
        _exportAllYes.Content = Texts.T("media.exportAll.confirm.yes");
        _exportAllNo.Content = Texts.T("media.exportAll.confirm.no");
        if (_exportAllPending != null) _exportAllText.Text = AskText(MediaLibraryExport.Measure(_exportAllPending));
    }

    private void UpdateExportAll()
    {
        // While the sound check still runs, a sound filter re-presents the grid with every report, so "all matches" keeps changing.
        string? reason = _building != null ? Texts.T("media.selection.reason.building")
            : SoundFilterActive && _audioProgress != null ? Texts.T("media.exportAll.reason.probing")
            : _tiles.Count == 0 ? Texts.T("media.exportAll.reason.none") : null;
        SetAction(_exportAll, reason == null && !_selectionBusy && !_lifetime.IsCancellationRequested && ExportOriginals != null,
            reason, ExportOriginals != null, "media.exportAll.hint");
    }

    private static string AskText(MediaLibraryExportSize size) => Texts.Count("media.exportAll.confirm", size.Files, size.Files.ToString("N0", Texts.Culture), FormatBytes(size.Bytes));

    private static string FormatBytes(long bytes) => MeowChan.Desktop.SizeText.Format(bytes);

    private void DismissExportAllAsk()
    {
        _exportAllPending = null;
        _exportAllAsk.IsVisible = false;
    }

    /// <summary>Exports the originals of every shown tile, one per tile. Asks first above <see cref="ExportAllConfirmAbove"/>.</summary>
    internal async Task ExportAllAsync()
    {
        if (!_exportAll.IsEnabled || ExportOriginals == null) return;
        IReadOnlyList<MediaLibraryTile> tiles = _tiles; // Replaced, never changed in place: a frozen view of what is shown now.
        MediaLibraryExportSize size = MediaLibraryExport.Measure(tiles);
        if (size.Exceeds(ExportAllConfirmAbove.Files, ExportAllConfirmAbove.Bytes))
        {
            _exportAllPending = tiles;
            _exportAllText.Text = AskText(size);
            _exportAllAsk.IsVisible = true;
            _exportAllYes.Focus();
            return;
        }
        await ExportTilesAsync(tiles, Texts.T("media.exportAll.scope"), preparing: tiles.Count);
    }

    private async Task ConfirmExportAllAsync()
    {
        IReadOnlyList<MediaLibraryTile>? tiles = _exportAllPending;
        DismissExportAllAsk();
        if (tiles == null || _selectionBusy || !_exportAll.IsEnabled || ExportOriginals == null) return;
        await ExportTilesAsync(tiles, Texts.T("media.exportAll.scope"), preparing: tiles.Count);
    }
}
