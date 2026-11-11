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
            await OpenMediaGalleryAsync(entries, first.EntryKey, _mediaPage);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
        {
            if (!_isClosing) ShowNotification(Texts.T("notification.error"), Texts.T("media.galleryFailed"), NotificationType.Error, operation: "media.gallery");
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
