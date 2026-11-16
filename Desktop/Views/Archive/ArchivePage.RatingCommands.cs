using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
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
            // Executable whenever the reader is: RateAndTagByKeyAsync (which opens through OpenRateAndTagForFocusAsync, as the toolbar button does) finds
            // the medium (the focused one, else the selected post's or the Media tab's selection, waiting for a running media scan) and says why when
            // there is none. Before, a key without a focused medium was consumed in silence (the router consumes a matched key whose action cannot run).
            // A text selection in a post does not keep R: the selection has no use for a bare letter (Ctrl+C stays the selection's).
            _ratingCommands.Register(new("media.rating.edit", pane, () => pane.IsEffectivelyVisible,
                () => !_ended && _available,
                () => OpenActions.Start(RateAndTagByKeyAsync, "media.rating.edit"), Priority: 100, AllowInTextSelection: true));
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
        // Run before a ListBox/ListBoxItem can consume a letter for type-ahead. F has a
        // dedicated reader route, while R is a command owned by this same pane; keeping
        // the rating router on the bubble path made R disappear when keyboard focus was
        // on a post, media tile or another selecting control. CommandRouter still owns
        // the native-input and popup guards, so text fields and open overlays keep their
        // keys. A list item's Enter/Space activation remains local even if a saved
        // rating binding uses one of those gestures.
        pane.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (!ReaderSelectionOwnsActivationKey(e)) _ratingCommands?.Route(e);
        }, RoutingStrategies.Tunnel);
    }

    /// <summary>True when a selecting row owns Enter/Space activation instead of a page shortcut.</summary>
    private static bool ReaderSelectionOwnsActivationKey(KeyEventArgs e) =>
        e.KeyModifiers == KeyModifiers.None && e.Key is Key.Enter or Key.Space && e.Source is Visual source
        && source.GetSelfAndVisualAncestors().Any(node => node is ListBoxItem or DataGridRow);

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
