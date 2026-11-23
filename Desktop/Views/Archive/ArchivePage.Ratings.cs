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

using MeowChan.Desktop.Controls;

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
        RefreshMediaSidebarDecisions();
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
    // The button is "Rate and tag" (Tom's decision of 15.11.: no rating menu): the popover with the stars on top and the tag field under them, for the
    // selected file of the list. A button the toolbar keeps under "More" is no anchor while that menu is closed: the popover then opens on "More" (A19),
    // and on the media list when "More" is not shown either.
    private void OnEditMediaRatingClick(object? sender, RoutedEventArgs e)
    {
        if (_selectedThread == null || SelectedMediaRow() == null) return;
        OpenActions.Start(() => OpenRateAndTagForFocusAsync(initialFocus: QuickTagInitialFocus.TagInput, ArchiveEditRatingButton), "media.rating.edit");
    }
    /// <summary>The control the "Rate and tag" popover opens on for <paramref name="button"/>: the button, "More" when the toolbar reduced it, the media list otherwise.</summary>
    internal Control RatingMenuAnchor(Control button)
    {
        ActionToolBar bar = ReaderMediaActions;
        if (!bar.ReducedItems.Contains(button) && button.IsEffectivelyVisible) return button;
        if (bar.More is { IsEffectivelyVisible: true } more) return more;
        return _mediaTiles ? ArchiveMediaTiles : ArchiveMediaGrid;
    }
    private void UpdateMediaRatingAction()
    {
        if (this.FindControl<Button>("ArchiveEditRatingButton") is Button edit)
            edit.IsEnabled = !_ended && _available && !_busy && _selectedThread != null && SelectedMediaRow() is ArchiveAssetRow row
                && (_mediaRatingError == null && SetMediaRatingAsync != null && CanChangeMediaRating?.Invoke(FavoriteAssetKey.From(_selectedThread, row.Asset)) == true
                    || TagPopoverAsync != null || EditArchiveMediaTagsAsync != null);
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
