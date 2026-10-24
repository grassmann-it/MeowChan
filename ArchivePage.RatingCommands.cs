using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Favorites;
using MeowChan.Gallery;

namespace MeowChan;
public partial class ArchivePage
{
    internal Func<FavoriteAssetKey, int?, Task>? SetMediaRatingAsync { get; set; }
    internal Func<FavoriteAssetKey, bool>? CanChangeMediaRating { get; set; }
    private CommandRouter? _ratingCommands;
    private GalleryEntry? FocusedRatingEntry()
    {
        Control pane = Control<Grid>("ArchiveReaderPane");
        if (_thread == null || _selectedThread != _thread.Summary.Id || TopLevel.GetTopLevel(pane)?.FocusManager?.GetFocusedElement() is not Control focus) return null;
        Control[] parents = focus.GetSelfAndVisualAncestors().OfType<Control>().ToArray();
        if (!parents.Contains(pane)) return null;
        ArchiveAssetRow? row = parents.Select(control => control.DataContext).OfType<ArchiveAssetRow>().FirstOrDefault();
        if (row == null && parents.Select(control => control.DataContext).OfType<ArchiveAttachmentRow>().FirstOrDefault() is { } attachment)
            row = FindMatchingAsset(attachment.Attachment, _assets);
        if (row == null)
            row = parents.FirstOrDefault(control => control.Name is "ArchiveMediaGrid" or "ArchiveMediaTiles") switch
            { DataGrid grid => grid.SelectedItem as ArchiveAssetRow, ListBox list => list.SelectedItem as ArchiveAssetRow, _ => null };
        return row == null ? null : new GalleryEntry(new(_selectedThread, row.Manifest.Directory, row.Asset.AssetId), row.Manifest, row.Asset,
            null, null, !row.Asset.IsCurrent, true) { ThreadUrl = _thread.Summary.Url };
    }
    private void InitializeRatingCommands()
    {
        // The same pane owns focus and input before and after MainWindow detaches it.
        Control pane = Control<Grid>("ArchiveReaderPane");
        pane.AttachedToVisualTree += (_, _) =>
        {
            _ratingCommands?.Dispose(); _ratingCommands = new(pane);
            bool Ready() => !_ended && _available && _mediaRatingError == null && FocusedRatingEntry() is { } entry
                && CanChangeMediaRating?.Invoke(FavoriteAssetKey.From(entry.Key.Thread, entry.Asset)) == true;
            _ratingCommands.Register(new("media.rating.edit", pane, () => pane.IsEffectivelyVisible, Ready,
                () => _ = ExecuteRatingCommandAsync(null, edit: true), Priority: 100));
            _ratingCommands.Register(new("media.rating.clear", pane, () => pane.IsEffectivelyVisible, Ready,
                () => _ = ExecuteRatingCommandAsync(null), Priority: 100));
            for (int value = 1; value <= 5; value++)
            {
                int frozen = value;
                _ratingCommands.Register(new("media.rating.set-" + value, pane, () => pane.IsEffectivelyVisible, Ready,
                    () => _ = ExecuteRatingCommandAsync(frozen), Priority: 100));
            }
        };
        pane.DetachedFromVisualTree += (_, _) => { _ratingCommands?.Dispose(); _ratingCommands = null; };
        pane.KeyDown += (_, e) => _ratingCommands?.Route(e);
    }
    private async Task ExecuteRatingCommandAsync(int? value, bool edit = false)
    {
        GalleryEntry? entry = FocusedRatingEntry();
        if (entry == null) return;
        Action<string> reportError = CaptureActionErrorReporter();
        try
        {
            if (edit && EditMediaRatingAsync != null) await EditMediaRatingAsync(entry);
            else if (!edit && SetMediaRatingAsync != null) await SetMediaRatingAsync(FavoriteAssetKey.From(entry.Key.Thread, entry.Asset), value);
        }
        catch (Exception error) { reportError(error.Message); }
    }
}
