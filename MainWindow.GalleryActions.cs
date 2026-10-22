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
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;
using MeowChan.Favorites;
using MeowChan.Gallery;

namespace MeowChan;

public partial class MainWindow
{
    private readonly HashSet<FavoriteAssetKey> _favoriteChanges = new();
    private readonly Dictionary<FavoriteAssetKey, string> _galleryFavoriteErrors = new();

    private void WireGalleryActions()
    {
        if (_gallery == null) return;
        _gallery.FavoriteToggleRequested += entry => _ = RunGalleryActionAsync(() => ToggleGalleryFavoriteAsync(entry));
        _gallery.ExportOriginalRequested += entry => _ = RunGalleryActionAsync(() => ArchivePage.OpenOriginalExportAsync(
            new[] { FavoriteExportAsset.From(entry.Key.Thread, entry.Manifest, entry.Asset) }, _gallery));
        _gallery.ShowInFolderRequested += entry =>
        {
            string? directory = Path.GetDirectoryName(entry.Asset.Path);
            if (directory != null) OpenDirectory(directory);
        };
        _gallery.DetailsRequested += entry => _ = RunGalleryActionAsync(() => ShowGalleryDetailsAsync(entry));
    }

    private async Task RunGalleryActionAsync(Func<Task> command)
    {
        try { await command(); }
        catch (OperationCanceledException) when (_isClosing) { }
        catch (Exception error) when (error is not OutOfMemoryException)
        { if (!_isClosing) ShowNotification(Texts.T("notification.error"), error.Message, NotificationType.Error); }
    }

    private async Task ToggleGalleryFavoriteAsync(GalleryEntry entry)
    {
        FavoriteAssetKey key = FavoriteAssetKey.From(entry.Key.Thread, entry.Asset);
        if (!_favoriteChanges.Add(key)) return;
        _galleryFavoriteErrors.Remove(key);
        _archivePage?.ClearFavoriteError(key);
        RefreshGalleryFavorite();
        try
        {
            await _session.SetFavoriteAsync(entry, !_session.IsFavorite(entry));
            RefreshGalleryFavorite();
        }
        catch (Exception error)
        {
            _galleryFavoriteErrors[key] = error.Message;
            RefreshGalleryFavorite();
        }
        finally { _favoriteChanges.Remove(key); }
    }

    private void RefreshGalleryFavorite()
    {
        _archivePage?.RefreshArchiveFavoriteControls();
        if (_gallery != null && _galleryIndex >= 0 && _galleryIndex < _galleryEntries.Count)
        {
            _gallery.SetFavorite(_session.IsFavorite(_galleryEntries[_galleryIndex]));
            GalleryEntry current = _galleryEntries[_galleryIndex];
            _gallery.SetFavoriteError(_galleryFavoriteErrors.GetValueOrDefault(FavoriteAssetKey.From(current.Key.Thread, current.Asset)));
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
                + entry.Asset.Length.ToString("N0", Texts.Culture) + " bytes" + Environment.NewLine
                + "SHA-256: " + entry.Asset.Sha256 + Environment.NewLine
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
