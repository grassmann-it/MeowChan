using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.Notifications;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;
using MeowChan.Favorites;
using MeowChan.Gallery;

namespace MeowChan;
public partial class MainWindow
{
    private readonly HashSet<FavoriteAssetKey> _ratingChanges = new();
    private readonly Dictionary<FavoriteAssetKey, string> _ratingErrors = new();
    private bool _ratingManagerOpen;
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
    // The colour label of a medium (keys 6 to 9): written like a rating, then the gallery's info line and the media page's tiles follow.
    private async Task SetMediaLabelAsync(GalleryEntry entry, string? color)
    {
        if (_isClosing) return;
        try
        {
            await _session.SetMediaLabelAsync(FavoriteAssetKey.From(entry.Key.Thread, entry.Asset), color);
            if (_isClosing) return;
            RefreshGalleryRating();
            _mediaPage?.RefreshUserData();
        }
        catch (Exception error) when (error is not OperationCanceledException and not OutOfMemoryException)
        {
            if (!_isClosing) ShowNotification(Texts.T("notification.error"), MediaRatingText.FormatError(MediaRatingText.Catalog("media.rating.error.save"), error.Message),
                NotificationType.Error, operation: "media.label");
        }
    }
    private Task SetGalleryLabelAsync(GalleryEntry entry, string? color) => SetMediaLabelAsync(entry, color);
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
        RefreshGalleryFilmstripFlags(); // the filmstrip's flags follow a favourite or rating the gallery just changed
        if (_gallery == null || _galleryIndex < 0 || _galleryIndex >= _galleryEntries.Count) return;
        GalleryEntry entry = _galleryEntries[_galleryIndex];
        FavoriteAssetKey key = FavoriteAssetKey.From(entry.Key.Thread, entry.Asset);
        // A write under way is "pending", not "unavailable": the Post row's stars stay enabled (the keyboard focus must not be lost) and ignore clicks.
        bool pending = _ratingChanges.Contains(key);
        try { _gallery.SetRating(_session.GetMediaRating(key), _ratingErrors.GetValueOrDefault(key), available: !pending, pending: pending); }
        catch (Exception error) { _gallery.SetRating(null, error.Message, available: false); }
        try { _gallery.SetLabel(_session.GetMediaLabel(key)); } catch (Exception error) when (error is System.IO.InvalidDataException or ObjectDisposedException) { _gallery.SetLabel(null); }
    }
    // The media page's rating menu (the stars, "Remove rating" and R): the dialog used to show a failed write, so it is a notification now.
    private async Task RateMediaPageEntryAsync(GalleryEntry entry, int? value)
    {
        if (_isClosing) return;
        try { await SetMediaRatingAsync(FavoriteAssetKey.From(entry.Key.Thread, entry.Asset), value); }
        catch (Exception error) when (error is not OperationCanceledException and not OutOfMemoryException)
        {
            if (!_isClosing) ShowNotification(Texts.T("notification.error"), MediaRatingText.FormatError(MediaRatingText.Catalog("media.rating.error.save"), error.Message),
                NotificationType.Error, operation: "media.rating");
        }
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
