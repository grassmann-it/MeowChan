using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.Notifications;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;
using MeowChan.Gallery;
using MeowChan.Tags;

namespace MeowChan;

// The media page: an archive-wide grid fed by the derived media index. Opening a tile resolves its
// neighborhood back into ordinary gallery entries; "show in thread" uses the archive reader.
public partial class MainWindow
{
    private MediaPage? _mediaPage;
    internal MediaPage? MediaPageForTests => _mediaPage;

    private void InitializeMediaPage()
    {
        _mediaPage = new MediaPage(_session.OpenArchiveReaderAsync, cachePath: Path.Combine(_session.ProfileDirectory, "cache", MediaLibraryIndexCache.FileName))
        {
            OpenGallery = OpenMediaLibraryGalleryAsync,
            ShowInThread = ShowMediaLibraryEntryInThreadAsync,
            EditTags = EditMediaLibraryTagsAsync,
            ReadUserData = ReadMediaLibraryUserData,
            TagServices = new(_session.GetPublishedPrivateTagSnapshot, _session.DefinePrivateTagAsync,
                (tag, targets, assigned, expectedRevision, token) => _session.SetMediaTagsAsync(tag, targets, assigned, expectedRevision, token)),
            ExportOriginals = ExportMediaLibraryOriginalsAsync
        };
        this.FindControl<ContentControl>("MediaPageHost")!.Content = _mediaPage;
    }

    // Passive in-memory views only; an unavailable store stays null so a filter needing it is refused.
    private MediaLibraryUserData ReadMediaLibraryUserData()
    {
        PrivateTagLoadSnapshot tags = _session.GetPublishedPrivateTagSnapshot();
        IReadOnlyList<Favorites.FavoriteEntry>? favorites;
        try { favorites = _session.GetFavorites(); }
        catch (InvalidDataException) { favorites = null; }
        IReadOnlyList<Ratings.MediaRatingEntry>? ratings;
        try { ratings = _session.GetMediaRatings(); }
        catch (InvalidDataException) { ratings = null; }
        return new MediaLibraryUserData(tags.State == PrivateTagLoadState.Ready ? tags.Snapshot : null, favorites, ratings);
    }

    private async Task OpenMediaLibraryGalleryAsync(IReadOnlyList<MediaLibraryEntry> window, int start)
    {
        if (_isClosing || start < 0 || start >= window.Count) return;
        MediaLibraryEntry chosen = window[start];
        try
        {
            IReadOnlyList<GalleryEntry> entries = await MediaLibraryGallery.ResolveAsync(window, _session.OpenArchiveReaderAsync,
                Storage.MediaAssetReader.OpenAsync);
            GalleryEntry? first = entries.FirstOrDefault(entry => entry.Key.Thread == chosen.Thread && entry.Key.AssetId == chosen.AssetId
                && entry.Post?.PostId == chosen.PostId && entry.Attachment?.Position == chosen.Position);
            if (_isClosing) return;
            if (first == null)
            {
                ShowNotification(Texts.T("notification.error"), Texts.T("media.galleryFailed"), NotificationType.Warning, operation: "media.gallery");
                return;
            }
            // The grid is the origin: closing the gallery returns focus to the selected tile, not to the page shell.
            await OpenMediaGalleryAsync(entries, first.EntryKey, _mediaPage?.Grid);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
        {
            if (!_isClosing) ShowNotification(Texts.T("notification.error"), Texts.T("media.galleryFailed"), NotificationType.Error, operation: "media.gallery");
        }
    }

    // The gallery is an overlay in this window, so closing it does not reactivate the window: favorite, rating and
    // tag changes made there are picked up here. Unchanged stores are a cheap no-op.
    private void RefreshMediaPageUserData()
    {
        if (!_isClosing) _mediaPage?.RefreshUserData();
    }

    private bool _mediaTagDialogOpen;
    internal MediaTagDialog? MediaPageTagWindow { get; private set; }

    // Own media tags from the media page use the gallery's own target rules: the entry is resolved back to the
    // gallery entry first, so a file that changed since indexing is refused instead of tagged under old bytes.
    private async Task EditMediaLibraryTagsAsync(MediaLibraryEntry entry)
    {
        if (_isClosing || _mediaTagDialogOpen) return;
        _mediaTagDialogOpen = true;
        Control? origin = FocusManager?.GetFocusedElement() as Control;
        try
        {
            IReadOnlyList<GalleryEntry> resolved = await MediaLibraryGallery.ResolveAsync(new[] { entry }, _session.OpenArchiveReaderAsync,
                Storage.MediaAssetReader.OpenAsync);
            GalleryEntry? match = resolved.FirstOrDefault();
            PrivateMediaTagTarget? target = GalleryMediaTagTarget.Resolve(match, out GalleryMediaTagUnavailable reason);
            if (_isClosing) return;
            if (match == null || target == null)
            {
                ShowNotification(Texts.T("notification.error"), Texts.T(match == null ? "media.galleryFailed"
                    : reason == GalleryMediaTagUnavailable.NoPostVersion ? "tags.media.unbound" : "tags.media.unsupported"),
                    NotificationType.Warning, operation: "media.tags");
                return;
            }
            MediaTagSession session = new(target, _session.GetPublishedPrivateTagSnapshot, _session.DefinePrivateTagAsync, _session.SetMediaTagAsync);
            MediaTagDialog dialog = new(session, match.Asset.OriginalFileName ?? Path.GetFileName(match.Asset.Path));
            MediaPageTagWindow = dialog;
            await dialog.ShowDialog(this);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
        {
            if (!_isClosing) ShowNotification(Texts.T("notification.error"), Texts.T("media.galleryFailed"), NotificationType.Error, operation: "media.tags");
        }
        finally
        {
            MediaPageTagWindow = null;
            _mediaTagDialogOpen = false;
            if (!_isClosing)
            {
                _mediaPage?.RefreshUserData();
                if (origin?.IsEffectivelyVisible == true) origin.Focus();
            }
        }
    }

    // The same export dialog as the gallery and the favorites: verified originals, no overwriting, per-file results.
    private Task ExportMediaLibraryOriginalsAsync(IReadOnlyList<Favorites.FavoriteExportAsset> assets, int unavailable)
        => _isClosing ? Task.CompletedTask : ArchivePage.OpenOriginalExportAsync(assets, _mediaPage, unavailable, Texts.T("media.selection.exportScope"));

    private async Task ShowMediaLibraryEntryInThreadAsync(MediaLibraryEntry entry)
    {
        if (_isClosing || entry.PostId == null || !NavigateWorkspace(WorkspacePage.Archive, activateArchive: false)) return;
        await ArchivePage.LoadThreadAsync(entry.Thread);
        await ArchivePage.FocusPostAsync(entry.PostId);
    }
}
