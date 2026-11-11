using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Notifications;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using MeowChan.Desktop;
using MeowChan.Desktop.Feedback;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;
using MeowChan.Favorites;
using MeowChan.Gallery;

namespace MeowChan;

public partial class MainWindow
{
    private readonly GalleryFavoriteCommandQueue<FavoriteAssetKey> _favoriteChanges = new();

    private void WireGalleryActions()
    {
        if (_gallery == null) return;
        _gallery.RatingSetRequested += (entry, value) => _ = SetGalleryRatingAsync(entry, value);
        _gallery.RatingEditRequested += entry => _ = RunGalleryActionAsync(() => EditMediaRatingAsync(entry), entry.Key.ToString());
        _gallery.FavoriteToggleRequested += entry => _ = RunGalleryActionAsync(() => ToggleGalleryFavoriteAsync(entry), entry.Key.ToString());
        _gallery.FavoriteSetRequested += (entry, selected) => _ = RunGalleryActionAsync(() => SetGalleryFavoriteAsync(entry, selected), entry.Key.ToString());
        _gallery.ExportOriginalRequested += entry => _ = RunGalleryActionAsync(() => ArchivePage.OpenOriginalExportAsync(
            new[] { FavoriteExportAsset.From(entry.Key.Thread, entry.Manifest, entry.Asset) }, _gallery), entry.Key.ToString());
        _gallery.ShowInFolderRequested += entry =>
        {
            string? directory = Path.GetDirectoryName(entry.Asset.Path);
            if (directory != null) OpenDirectory(directory);
        };
        _gallery.DetailsRequested += entry => _ = RunGalleryActionAsync(() => ShowGalleryDetailsAsync(entry), entry.Key.ToString());
        _gallery.TagsRequested += entry => _ = RunGalleryActionAsync(() => EditGalleryMediaTagsAsync(entry), entry.Key.ToString());
    }

    private async Task RunGalleryActionAsync(Func<Task> command, string? context = null)
    {
        try { await command(); }
        catch (OperationCanceledException) when (_isClosing) { }
        catch (Exception error) when (error is not OutOfMemoryException)
        { if (!_isClosing) ShowNotification(Texts.T("notification.error"), error.Message, NotificationType.Error, context: context, operation: "gallery.action"); }
    }

    private Task ToggleGalleryFavoriteAsync(GalleryEntry entry)
    {
        FavoriteAssetKey key = FavoriteAssetKey.From(entry.Key.Thread, entry.Asset);
        return SetGalleryFavoriteAsync(entry, !_favoriteChanges.Target(key, _session.IsFavorite(entry)));
    }

    private async Task SetGalleryFavoriteAsync(GalleryEntry entry, bool selected)
    {
        if (_isClosing) return; // New UI intents stop; already accepted sequences still drain.
        FavoriteAssetKey key = FavoriteAssetKey.From(entry.Key.Thread, entry.Asset);
        if (_favoriteChanges.IsPending(key))
        {
            // The original operation owns persistence, diagnostics and the session
            // admission lease. Repeats only update its desired value and await it.
            try { await _favoriteChanges.SetAsync(key, selected,
                _ => throw new InvalidOperationException("Pending favorite lost its owner."), () => { }); }
            catch (Exception error) when (error is not OutOfMemoryException) { }
            return;
        }
        long revision = _galleryAssociationsRevision;
        _archivePage?.ClearFavoriteError(key);
        // Hold one existing bounded session slot through ALL latest-intent writes.
        // BeginShutdown can cancel the engine immediately without losing an already
        // accepted M -> U queued behind the first persisted write.
        using var sequence = _session.AdmitFavoriteSequence(entry);
        try
        {
            await _favoriteChanges.SetAsync(key, selected,
                async desired => { await sequence.SetAsync(desired); }, RefreshGalleryFavorite);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // Operation-owned: exactly once, even after navigation/gallery close.
            string context = entry.Key.Thread.Source + "/" + entry.Key.Thread.Board + "/"
                + entry.Key.Thread.Thread + "/" + entry.Asset.AssetId + "@" + revision;
            _feedback.Record(FeedbackCategory.Error, "gallery.favorite", context,
                error.Message, faultKey: "gallery.favorite");
        }
    }

    private void RefreshGalleryFavorite()
    {
        RefreshGalleryRating();
        RefreshGalleryMediaTags();
        _archivePage?.RefreshArchiveFavoriteControls();
        if (_gallery != null && _galleryIndex >= 0 && _galleryIndex < _galleryEntries.Count)
        {
            GalleryEntry current = _galleryEntries[_galleryIndex];
            _gallery.SetFavorite(_session.IsFavorite(current),
                _favoriteChanges.IsPending(FavoriteAssetKey.From(current.Key.Thread, current.Asset)));
            // Persistence failures are recorded by their operation owner, not by the
            // currently displayed entry (which may have changed while saving).
        }
    }

    private async Task ShowGalleryDetailsAsync(GalleryEntry entry)
    {
        var origin = FocusManager?.GetFocusedElement() as Control;
        Window dialog = new()
        {
            Title = CommandCatalog.Label("thread.details"), Width = 600, Height = 360, MinWidth = 340,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        SelectableTextBlock text = new()
        {
            TextWrapping = TextWrapping.Wrap,
            Text = entry.Key.Thread.Source + " /" + entry.Key.Thread.Board + "/ " + entry.Key.Thread.Thread + Environment.NewLine
                + (entry.Asset.OriginalFileName ?? entry.Asset.Path) + Environment.NewLine
                + MediaProvenanceText.LocalSize(entry.Asset.Length) + Environment.NewLine
                + "SHA-256: " + entry.Asset.Sha256 + Environment.NewLine
                + MediaProvenanceText.Details(entry.AttachmentResolution?.Binding) + Environment.NewLine
                + (entry.Post?.PostId ?? Texts.T("gallery.noPost"))
        };
        Button close = CommandBindings.CreateButton("window.close", () => dialog.Close());
        close.HorizontalAlignment = HorizontalAlignment.Right;
        Grid layout = new() { RowDefinitions = new RowDefinitions("*,Auto"), RowSpacing = 16, Margin = new Thickness(20) };
        layout.Children.Add(new ScrollViewer { Content = text });
        layout.Children.Add(close); Grid.SetRow(close, 1); dialog.Content = layout;
        dialog.KeyDown += (_, key) => { if (key.Key == Key.Escape) { key.Handled = true; dialog.Close(); } };
        await dialog.ShowDialog(this);
        if (origin?.IsEffectivelyVisible == true) origin.Focus();
    }
}
