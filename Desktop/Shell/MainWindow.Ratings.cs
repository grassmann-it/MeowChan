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
        // Review 15.11.: opened over the gallery, the dialog pauses a playing video and a running slideshow; only those resume after it.
        GalleryViewer? held = _gallery is { } shown && !_gallerySuspended ? shown : null;
        try
        {
            int? value = _session.GetMediaRating(key);
            MediaRatingDialog dialog = new(key, value, rating => SetMediaRatingAsync(key, rating));
            held?.BeginDialogPlaybackHold();
            await dialog.ShowDialog(this);
        }
        catch (Exception) { RefreshGalleryRating(); throw; }
        finally
        {
            _ratingDialogOpen = false;
            if (held != null && !_isClosing) await held.EndDialogPlaybackHoldAsync();
            if (_gallery != null && !_gallerySuspended)
            {
                // A19: a rating action the view keeps under More returns focus to More, not to the closed menu's hidden row.
                Control? back = _gallery.FocusReturnTarget(origin, "media.rating.edit");
                if (back?.IsEffectivelyVisible == true) back.Focus(); else _gallery.Focus();
            }
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
            ConfirmGalleryRatingSaved(key); // GL6: "Rating saved" in the gallery's info line, only while the rated medium is still shown
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
    private void ConfirmGalleryRatingSaved(FavoriteAssetKey key)
    {
        if (_gallery == null || _galleryIndex < 0 || _galleryIndex >= _galleryEntries.Count) return;
        GalleryEntry entry = _galleryEntries[_galleryIndex];
        if (FavoriteAssetKey.From(entry.Key.Thread, entry.Asset) == key) _gallery.ConfirmRatingSaved();
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
            // A19: opened from the gallery's More (or by its shortcut), focus returns to More while the view keeps it there.
            Control? back = _gallery is { } open && !_gallerySuspended ? open.FocusReturnTarget(origin, "media.rating.manage") : origin;
            if (back?.IsEffectivelyVisible == true) back.Focus();
        }
    }
}
