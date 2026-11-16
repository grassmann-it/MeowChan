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
    // The file the rating keys (Shift+1 to 5, Shift+0) set: the same focused medium as R (FocusedMediaForRateAndTag, ReaderMediumAt), the post card's included.
    private GalleryEntry? FocusedRatingEntry()
    {
        if (_thread == null || _selectedThread != _thread.Summary.Id || FocusedMediaForRateAndTag() is not { Asset: { } row }) return null;
        return new GalleryEntry(new(_selectedThread, row.Manifest.Directory, row.Asset.AssetId), row.Manifest, row.Asset,
            null, null, !row.Asset.IsCurrent, true) { ThreadUrl = _thread.Summary.Url };
    }
    private void InitializeRatingCommands()
    {
        // The same pane owns focus and input before and after MainWindow detaches it.
        Control pane = ArchiveReaderPane;
        pane.AttachedToVisualTree += (_, _) =>
        {
            _ratingCommands?.Dispose(); _ratingCommands = new(pane);
            bool Ready() => !_ended && _available && _mediaRatingError == null && FocusedRatingEntry() is { } entry
                && CanChangeMediaRating?.Invoke(FavoriteAssetKey.From(entry.Key.Thread, entry.Asset)) == true;
            // R "Rate and tag" opens the popover (stars focused) on the focused file; the keys below rate it directly (Shift+1 to 5, Shift+0).
            _ratingCommands.Register(new("media.rating.edit", pane, () => pane.IsEffectivelyVisible,
                () => !_ended && _available && FocusedMediaForRateAndTag() != null,
                () => OpenActions.Start(() => OpenRateAndTagForFocusAsync(focusRating: true, ArchiveEditRatingButton), "media.rating.edit"), Priority: 100));
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

    /// <summary>The rating keys from the media sidebar, which sits beside the pane in the large reader (in the archive view its keys bubble to the pane).</summary>
    private bool RouteSidebarRatingKey(KeyEventArgs e) => _ratingCommands?.Route(e) == true;
    private async Task ExecuteRatingCommandAsync(int? value)
    {
        GalleryEntry? entry = FocusedRatingEntry();
        if (entry == null) return;
        Action<string> reportError = CaptureActionErrorReporter();
        try
        {
            if (SetMediaRatingAsync != null) await SetMediaRatingAsync(FavoriteAssetKey.From(entry.Key.Thread, entry.Asset), value);
        }
        catch (Exception error) { reportError(error.Message); }
    }
}
