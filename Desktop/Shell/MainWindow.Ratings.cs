using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia.Controls;
using MeowChan.Desktop;
using MeowChan.Favorites;
using MeowChan.Gallery;

namespace MeowChan;
public partial class MainWindow
{
    private readonly HashSet<FavoriteAssetKey> _ratingChanges = new();
    private readonly Dictionary<FavoriteAssetKey, string> _ratingErrors = new();
    private bool _ratingDialogOpen;
    private bool _ratingManagerOpen;
    private async Task EditMediaRatingAsync(GalleryEntry entry)
    {
        if (_isClosing || _ratingDialogOpen) return;
        FavoriteAssetKey key = FavoriteAssetKey.From(entry.Key.Thread, entry.Asset);
        if (_ratingChanges.Contains(key)) return;
        Control? origin = FocusManager?.GetFocusedElement() as Control;
        _ratingDialogOpen = true;
        try
        {
            int? value = _session.GetMediaRating(key);
            MediaRatingDialog dialog = new(key, value, rating => SetMediaRatingAsync(key, rating));
            await dialog.ShowDialog(this);
        }
        catch (Exception) { RefreshGalleryRating(); throw; }
        finally
        {
            _ratingDialogOpen = false;
            if (_gallery != null && !_gallerySuspended) { if (origin?.IsEffectivelyVisible == true) origin.Focus(); else _gallery.Focus(); }
            else if (_archivePage?.RestoreMediaRatingFocus(key, origin) != true && origin?.IsEffectivelyVisible == true) origin.Focus();
        }
    }
    private async Task SetMediaRatingAsync(FavoriteAssetKey key, int? value)
    {
        if (!_ratingChanges.Add(key)) return;
        _ratingErrors.Remove(key); RefreshGalleryRating();
        try
        {
            await _session.SetMediaRatingAsync(key, value);
            if (_isClosing) return;
            _archivePage?.RefreshMediaRatings();
            RefreshGalleryRating();
        }
        catch (Exception error) { _ratingErrors[key] = error.Message; throw; }
        finally { _ratingChanges.Remove(key); if (!_isClosing) RefreshGalleryRating(); }
    }
    private async Task SetGalleryRatingAsync(GalleryEntry entry, int? value)
    {
        FavoriteAssetKey key = FavoriteAssetKey.From(entry.Key.Thread, entry.Asset);
        try { await SetMediaRatingAsync(key, value); }
        catch (Exception)
        {
            if (_gallery != null && _galleryIndex >= 0 && _galleryIndex < _galleryEntries.Count
                && FavoriteAssetKey.From(_galleryEntries[_galleryIndex].Key.Thread, _galleryEntries[_galleryIndex].Asset) == key)
                RefreshGalleryRating();
        }
    }
    private void RefreshGalleryRating()
    {
        if (_gallery == null || _galleryIndex < 0 || _galleryIndex >= _galleryEntries.Count) return;
        GalleryEntry entry = _galleryEntries[_galleryIndex];
        FavoriteAssetKey key = FavoriteAssetKey.From(entry.Key.Thread, entry.Asset);
        try { _gallery.SetRating(_session.GetMediaRating(key), _ratingErrors.GetValueOrDefault(key), !_ratingChanges.Contains(key)); }
        catch (Exception error) { _gallery.SetRating(null, error.Message, available: false); }
    }
    private async Task ManageMediaRatingsAsync()
    {
        if (_isClosing || _ratingManagerOpen) return;
        _ratingManagerOpen = true;
        Control? origin = FocusManager?.GetFocusedElement() as Control;
        try { await new MediaRatingsManagerDialog(_session).ShowDialog(this); }
        finally
        {
            _ratingManagerOpen = false;
            _archivePage?.RefreshMediaRatings(); RefreshGalleryRating();
            if (origin?.IsEffectivelyVisible == true) origin.Focus();
        }
    }
}
