using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Notifications;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using MeowChan.Desktop;
using MeowChan.Desktop.Feedback;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Controls;
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
        if (BooruMediaIdentity.TryGetPostId(entry.Key.Thread, out string booruPost))
        {
            await ShowBooruMediaDetailsAsync(entry, booruPost, origin);
            return;
        }
        Window dialog = new()
        {
            Name = "GalleryDetailsDialog", Title = CommandCatalog.Label("thread.details"), Width = 600, Height = 360,
            MinWidth = 340, MinHeight = 240, WindowStartupLocation = WindowStartupLocation.CenterOwner
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
        text.Margin = new Thickness(16, 4, 16, 16);
        Button close = CommandBindings.CreateButton("window.close", () => dialog.Close());
        close.HorizontalAlignment = HorizontalAlignment.Right;
        TextBlock heading = new() { Text = dialog.Title, TextWrapping = TextWrapping.Wrap };
        heading.Classes.Add("section-title");
        dialog.Content = DialogFrame.Build(heading, text, close);
        dialog.KeyDown += (_, key) => { if (key.Key == Key.Escape) { key.Handled = true; dialog.Close(); } };
        await dialog.ShowDialog(this);
        if (origin?.IsEffectivelyVisible == true) origin.Focus();
    }
}

public partial class MainWindow
{
    internal BooruMediaDetailsDialog? BooruDetailsWindow { get; private set; }
    // Test seams: no browser is ever started and the clipboard is only touched when the shell has one.
    internal Func<Uri, Task>? OpenProviderPageForTests { get; set; }

    // Context lines of the gallery panel for a downloaded provider medium: source, post and the provider's own rating.
    private IReadOnlyList<string>? BooruGalleryContext(GalleryEntry entry)
    {
        if (!BooruMediaIdentity.TryGetPostId(entry.Key.Thread, out string postId)) return null;
        List<string> lines = new() { Texts.T("booru.media.tile.post", postId) };
        try
        {
            if (_session.ReadBooruCatalog() is { } catalog && BooruMediaProjection.Find(catalog, postId) is { } details)
            {
                lines.Add(Texts.T("booru.rating.label") + ": " + (details.RatingKnown ? BooruMessages.Rating(details.Rating) : Texts.T("booru.field.absent")));
                if (details.Tags.Count > 0) lines.Add(Texts.T("booru.media.gallery.tags", details.Tags.Count));
            }
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException) { }
        return lines;
    }

    // The same details for a downloaded provider medium from the media page and from the gallery. Local data only.
    private async Task ShowBooruMediaDetailsAsync(GalleryEntry entry, string postId, Control? origin)
    {
        BooruMediaDetails? details = null;
        try { if (_session.ReadBooruCatalog() is { } catalog) details = BooruMediaProjection.Find(catalog, postId, localFilePresent: true); }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException) { }
        string[] file =
        {
            entry.Asset.OriginalFileName ?? entry.Asset.RequestedFileName,
            MediaProvenanceText.LocalSize(entry.Asset.Length),
            "SHA-256: " + entry.Asset.Sha256
        };
        BooruMediaDetailsDialog dialog = new(postId, file, details,
            url => OpenProviderPageForTests?.Invoke(url) ?? ExternalLauncher.OpenAsync(url.AbsoluteUri),
            async url =>
            {
                if (Clipboard == null) throw new InvalidOperationException("Clipboard is unavailable.");
                await Clipboard.SetTextAsync(url.AbsoluteUri);
            });
        BooruDetailsWindow = dialog;
        try { await dialog.ShowDialog(this); }
        finally
        {
            BooruDetailsWindow = null;
            if (!_isClosing && origin?.IsEffectivelyVisible == true) origin.Focus();
        }
    }
}
