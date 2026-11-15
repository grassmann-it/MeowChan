using MeowChan.Desktop.WorkViews;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using MeowChan.Desktop;
using MeowChan.Desktop.MediaActions;
using MeowChan.Favorites;
using MeowChan.Gallery;

namespace MeowChan;
public partial class ArchivePage
{
    internal Func<FavoriteAssetKey, int?>? GetMediaRating { get; set; }
    private int _mediaRatingFilter;
    private string? _mediaRatingError;
    private void ConfigureMediaRatings()
    {
        if (_selectedThread == null) return;
        _mediaRatingError = null;
        foreach (ArchiveAssetRow row in _assets)
        {
            try { row.Rating = GetMediaRating?.Invoke(FavoriteAssetKey.From(_selectedThread, row.Asset)); }
            catch (Exception error) { _mediaRatingError = error.Message; row.Rating = null; }
            row.RatingUnavailable = _mediaRatingError != null;
        }
        if (_mediaRatingError != null) foreach (ArchiveAssetRow row in _assets) row.RatingUnavailable = true;
        ComboBox filter = ArchiveRatingFilter;
        filter.IsEnabled = _mediaRatingError == null && GetMediaRating != null;
        ArchiveRatingStatus.Text = _mediaRatingError;
        WorkViewGate.Apply(ArchiveRatingStatus, ArchiveWorkView, _mediaRatingError != null); // an error: Required in every view
    }
    internal void RefreshMediaRatings()
    {
        if (_ended) return;
        IReadOnlyList<ArchiveAssetRow> selected = SelectedMediaRows();
        RenderMedia();
        IReadOnlyList<ArchiveAssetRow> shown = VisibleAssets();
        if (selected.Any(row => !shown.Contains(row)))
        {
            // The files the filter no longer shows dropped out of the selection with the re-render; the others stay selected.
            Message(MediaRatingText.LocalizedFilterNoLongerMatches);
            ArchiveRatingFilter.Focus();
        }
        foreach (ArchiveMediaTileView tile in this.GetVisualDescendants().OfType<ArchiveMediaTileView>()) tile.RefreshRating();
    }
    private void OnMediaRatingFilterChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox { SelectedIndex: >= 0 } combo || combo.SelectedIndex == _mediaRatingFilter) return;
        _mediaRatingFilter = combo.SelectedIndex;
        if (this.FindControl<DataGrid>("ArchiveMediaGrid") != null) RenderMedia();
    }
    /// <summary>Opens the saved-ratings manager (also reachable from Diagnostics); supplied by the shell.</summary>
    internal Func<Task>? ManageMediaRatingsAsync
    {
        get => _manageMediaRatings;
        set { _manageMediaRatings = value; ArchiveManageRatingsButton.IsEnabled = !_ended && value != null; }
    }
    private Func<Task>? _manageMediaRatings;
    private async void OnManageMediaRatingsClick(object? sender, RoutedEventArgs e)
    {
        if (_ended || ManageMediaRatingsAsync == null) return;
        try { await ManageMediaRatingsAsync(); }
        catch (Exception error) { if (!_ended) Message(error.Message, true); }
    }
    // The button opens the rating menu (the stars and "Remove rating") instead of a dialog. A button the toolbar keeps under "More" is no anchor
    // while that menu is closed: the menu then opens on "More" (A19), and on the media list when "More" is not shown either.
    private void OnEditMediaRatingClick(object? sender, RoutedEventArgs e)
    {
        if (_selectedThread == null || SelectedMediaRow() is not ArchiveAssetRow row) return;
        GalleryEntry entry = new(new(_selectedThread, row.Manifest.Directory, row.Asset.AssetId), row.Manifest, row.Asset, null, null, !row.Asset.IsCurrent, true);
        OpenRatingMenu(entry, RatingMenuAnchor(ArchiveEditRatingButton));
    }
    /// <summary>The control a rating menu opens on for <paramref name="button"/>: the button, "More" when the toolbar reduced it, the media list otherwise.</summary>
    internal Control RatingMenuAnchor(Control button)
    {
        ActionToolBar bar = ReaderMediaActions;
        if (!bar.ReducedItems.Contains(button) && button.IsEffectivelyVisible) return button;
        if (bar.More is { IsEffectivelyVisible: true } more) return more;
        return _mediaTiles ? ArchiveMediaTiles : ArchiveMediaGrid;
    }
    private int? CurrentMediaRating(FavoriteAssetKey key)
    {
        try { return GetMediaRating?.Invoke(key); }
        catch (Exception) { return null; } // the error is shown by the rating row of the page (_mediaRatingError)
    }
    /// <summary>R in the reader or the media list: the rating menu for <paramref name="entry"/> on <paramref name="origin"/>.</summary>
    internal void OpenRatingMenu(GalleryEntry entry, Control origin)
    {
        if (_ended || !_available || _thread == null || _selectedThread == null || SetMediaRatingAsync == null || _mediaRatingError != null) return;
        var thread = _thread;
        ResourceId threadId = _selectedThread;
        FavoriteAssetKey key = FavoriteAssetKey.From(entry.Key.Thread, entry.Asset);
        bool available = !_busy && CanChangeMediaRating?.Invoke(key) == true;
        Action<string> reportError = CaptureActionErrorReporter();
        MediaMenuSession session = new(MediaMenuSurface.ArchiveTable, MediaMenuTarget.Of(entry), () => !_ended && _available && !IsFilenameSearchOpen
            && ReferenceEquals(_thread, thread) && _selectedThread == threadId);
        Task Run(Func<Task> action) => RunArchiveMenuActionAsync(action, session, reportError);
        ContextMenu menu = MediaMenuBuilder.BuildRatingMenu(session, CurrentMediaRating(key), available, MediaMenuReasons.Unavailable,
            value => SetMediaRatingAsync(key, value), Run);
        _mediaMenuPresenter.Show(menu, session, origin, attachToOrigin: true, fallbackFocus: () => (_mediaTiles ? (Control)ArchiveMediaTiles : ArchiveMediaGrid).Focus());
    }
    private void UpdateMediaRatingAction()
    {
        if (this.FindControl<Button>("ArchiveEditRatingButton") is Button edit)
            edit.IsEnabled = !_ended && _available && !_busy && _mediaRatingError == null && _selectedThread != null
                && SelectedMediaRow() is ArchiveAssetRow row && SetMediaRatingAsync != null
                && CanChangeMediaRating?.Invoke(FavoriteAssetKey.From(_selectedThread, row.Asset)) == true;
    }
    private void LocalizeMediaRatings()
    {
        MeowChan.Desktop.Commands.CommandBindings.Apply(ArchiveEditRatingButton, "media.rating.edit");
        MeowChan.Desktop.Commands.CommandBindings.Apply(ArchiveManageRatingsButton, "media.rating.manage");
        ArchiveManageRatingsButton.IsEnabled = !_ended && ManageMediaRatingsAsync != null;
        ComboBox filter = ArchiveRatingFilter;
        filter.ItemsSource = new[] { MediaRatingText.LocalizedFilterAll, MediaRatingText.LocalizedValue(null) }
            .Concat(Enumerable.Range(1, 5).Select(value => MediaRatingText.LocalizedFilterAtLeast(value))).ToArray();
        filter.SelectedIndex = _mediaRatingFilter;
        Avalonia.Automation.AutomationProperties.SetName(filter, MediaRatingText.LocalizedFilterLabel);
        ArchiveMediaGrid.Columns[3].Header = MediaRatingText.LocalizedLabel;
    }
}
