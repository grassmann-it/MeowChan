using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using MeowChan.Desktop;
using MeowChan.Favorites;
using MeowChan.Gallery;

namespace MeowChan;
public partial class ArchivePage
{
    internal Func<FavoriteAssetKey, int?>? GetMediaRating { get; set; }
    internal Func<GalleryEntry, Task>? EditMediaRatingAsync { get; set; }
    internal bool RestoreMediaRatingFocus(FavoriteAssetKey key, Control? origin)
    {
        if (_selectedThread != key.Thread || !IsEffectivelyVisible) return false;
        if (!VisibleAssets().Any(row => FavoriteAssetKey.From(key.Thread, row.Asset) == key))
        { Control<ComboBox>("ArchiveRatingFilter").Focus(); return true; }
        if (origin?.IsEffectivelyVisible == true)
        {
            ArchiveAssetRow? row = origin.GetSelfAndVisualAncestors().OfType<Control>().Select(control => control.DataContext).OfType<ArchiveAssetRow>().FirstOrDefault();
            if (row == null || FavoriteAssetKey.From(key.Thread, row.Asset) == key) { origin.Focus(); return true; }
        }
        (_mediaTiles ? (Control)Control<ListBox>("ArchiveMediaTiles") : Control<DataGrid>("ArchiveMediaGrid")).Focus();
        return true;
    }
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
        ComboBox filter = Control<ComboBox>("ArchiveRatingFilter");
        filter.IsEnabled = _mediaRatingError == null && GetMediaRating != null;
        Control<TextBlock>("ArchiveRatingStatus").Text = _mediaRatingError;
        Control<TextBlock>("ArchiveRatingStatus").IsVisible = _mediaRatingError != null;
    }
    internal void RefreshMediaRatings()
    {
        if (_ended) return;
        ArchiveAssetRow? selected = (Control<DataGrid>("ArchiveMediaGrid").SelectedItem ?? Control<ListBox>("ArchiveMediaTiles").SelectedItem) as ArchiveAssetRow;
        RenderMedia();
        if (selected != null && !VisibleAssets().Contains(selected))
        {
            Control<DataGrid>("ArchiveMediaGrid").SelectedItem = null;
            Control<ListBox>("ArchiveMediaTiles").SelectedItem = null;
            Message(MediaRatingText.Get("The media no longer matches the rating filter.", "Das Medium passt nicht mehr zum Bewertungsfilter."));
            Control<ComboBox>("ArchiveRatingFilter").Focus();
        }
        foreach (ArchiveMediaTileView tile in this.GetVisualDescendants().OfType<ArchiveMediaTileView>()) tile.RefreshRating();
    }
    private void OnMediaRatingFilterChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox { SelectedIndex: >= 0 } combo || combo.SelectedIndex == _mediaRatingFilter) return;
        _mediaRatingFilter = combo.SelectedIndex;
        if (this.FindControl<DataGrid>("ArchiveMediaGrid") != null) RenderMedia();
    }
    private async void OnEditMediaRatingClick(object? sender, RoutedEventArgs e)
    {
        if (_selectedThread == null || SelectedMediaRow() is not ArchiveAssetRow row || EditMediaRatingAsync == null || _mediaRatingError != null) return;
        GalleryEntry entry = new(new(_selectedThread, row.Manifest.Directory, row.Asset.AssetId), row.Manifest, row.Asset, null, null, !row.Asset.IsCurrent, true);
        try { await EditMediaRatingAsync(entry); }
        catch (Exception error) { if (!_ended) Message(error.Message, true); }
    }
    private void UpdateMediaRatingAction()
    {
        if (this.FindControl<Button>("ArchiveEditRatingButton") is Button edit)
            edit.IsEnabled = !_ended && _available && !_busy && _mediaRatingError == null && _selectedThread != null
                && SelectedMediaRow() is ArchiveAssetRow row && EditMediaRatingAsync != null
                && CanChangeMediaRating?.Invoke(FavoriteAssetKey.From(_selectedThread, row.Asset)) == true;
    }
    private void LocalizeMediaRatings()
    {
        MeowChan.Desktop.Commands.CommandBindings.Apply(Control<Button>("ArchiveEditRatingButton"), "media.rating.edit");
        ComboBox filter = Control<ComboBox>("ArchiveRatingFilter");
        filter.ItemsSource = new[] { MediaRatingText.Get("All ratings", "Alle Bewertungen"), MediaRatingText.Value(null) }
            .Concat(Enumerable.Range(1, 5).Select(value => MediaRatingText.Get($"At least {value}", $"Mindestens {value}"))).ToArray();
        filter.SelectedIndex = _mediaRatingFilter;
        Avalonia.Automation.AutomationProperties.SetName(filter, MediaRatingText.Get("Rating filter", "Bewertungsfilter"));
        Control<DataGrid>("ArchiveMediaGrid").Columns[3].Header = MediaRatingText.Label;
    }
}
