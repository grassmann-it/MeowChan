using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.Notifications;
using Avalonia.VisualTree;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;
using MeowChan.Gallery;
using MeowChan.Storage;
using MeowChan.Archive;
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
        if (_mediaPage != null)
        {
            if (!ReferenceEquals(MediaPageHost.Content, _mediaPage)) MediaPageHost.Content = _mediaPage;
            return;
        }
        _mediaPage = new MediaPage(_session.OpenArchiveReaderAsync, cachePath: Path.Combine(_session.ProfileDirectory, "cache", MediaLibraryIndexCache.FileName))
        {
            OpenGallery = (window, start) => OpenMediaLibraryGalleryAsync(window, start),
            ShowInThread = ShowMediaLibraryEntryInThreadAsync,
            EditTags = EditMediaLibraryTagsAsync,
            EditTagsForMenuAsync = (entry, current) => EditMediaLibraryTagsForMenuAsync(entry, current),
            ExecuteMediaMenuActionAsync = ExecuteMediaLibraryMenuActionAsync,
            ReadUserData = ReadMediaLibraryUserData,
            TagServices = new(_session.GetPublishedPrivateTagSnapshot, _session.DefinePrivateTagAsync,
                (tag, targets, assigned, expectedRevision, token) => _session.SetMediaTagsAsync(tag, targets, assigned, expectedRevision, token)),
            ExportOriginals = ExportMediaLibraryOriginalsAsync
        };
        _mediaPage.InitializeSharedMediaMenus();
        MediaPageHost.Content = _mediaPage;
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

    private async Task OpenMediaLibraryGalleryAsync(IReadOnlyList<MediaLibraryEntry> window, int start, Func<bool>? current = null)
    {
        if (_isClosing || current?.Invoke() == false || start < 0 || start >= window.Count) return;
        MediaLibraryEntry chosen = window[start];
        try
        {
            IReadOnlyList<GalleryEntry> entries = await MediaLibraryGallery.ResolveAsync(window, _session.OpenArchiveReaderAsync,
                Storage.MediaAssetReader.OpenAsync);
            GalleryEntry? first = entries.FirstOrDefault(entry => MediaMenuIdentity.Matches(chosen, entry));
            if (_isClosing || current?.Invoke() == false) return;
            if (first == null)
            {
                ShowNotification(Texts.T("notification.error"), Texts.T("media.galleryFailed"), NotificationType.Warning, operation: "media.gallery");
                return;
            }
            // The grid is the origin: closing the gallery returns focus to the selected tile, not to the page shell.
            // A still pending second restore of an earlier "Back to media" must not move selection and focus under the gallery.
            _mediaPage?.EndPendingReturn();
            if (current?.Invoke() == false) return;
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

    private Func<bool> CaptureMediaPageMenuGuard()
    {
        MediaPage? page = _mediaPage;
        int navigation = _mediaPageNavigationRevision;
        return () => !_isClosing && _selectedPage == WorkspacePage.Media
            && navigation == _mediaPageNavigationRevision && ReferenceEquals(page, _mediaPage)
            && page?.IsEffectivelyVisible == true && _gallery == null;
    }

    private async Task ExecuteMediaLibraryMenuActionAsync(string id, MediaLibraryEntry indexed, Func<bool> presentationCurrent)
    {
        Func<bool> hostCurrent = CaptureMediaPageMenuGuard();
        bool Current() => hostCurrent() && presentationCurrent();
        if (!Current()) return;
        Control? origin = FocusManager?.GetFocusedElement() as Control;
        try
        {
            GalleryEntry? entry = await MediaMenuIdentity.ResolveAsync(indexed, _session.OpenArchiveReaderAsync,
                Storage.MediaAssetReader.OpenAsync, Current, CancellationToken.None);
            if (!Current()) return;
            if (entry == null)
            {
                ShowNotification(Texts.T("notification.error"), Texts.T("media.galleryFailed"), NotificationType.Warning,
                    operation: "media.menu");
                return;
            }
            switch (id)
            {
                case "media.open":
                    if (_mediaPage != null)
                    {
                        MediaLibraryEntry[] window = _mediaPage.ShownEntries.ToArray();
                        int start = Array.FindIndex(window, candidate => candidate == indexed);
                        if (start >= 0) await OpenMediaLibraryGalleryAsync(window, start, Current);
                    }
                    break;
                case "favorites.toggle": await ToggleGalleryFavoriteAsync(entry); break;
                case "media.rating.edit": await EditMediaRatingAsync(entry); break;
                case "media.export-original":
                    await ArchivePage.OpenOriginalExportAsync(new[] { Favorites.FavoriteExportAsset.From(entry.Key.Thread,
                        entry.Manifest, entry.Asset) }, _mediaPage, scopeLabel: CommandCatalog.Label(id));
                    break;
                case "media.show-in-folder":
                    if (Path.GetDirectoryName(entry.Asset.Path) is string directory) OpenDirectory(directory);
                    break;
                case "media.details": await ShowGalleryDetailsAsync(entry); break;
                case "media.tags.edit": await EditMediaLibraryTagsForMenuAsync(indexed, Current, entry); break;
                default: throw new InvalidOperationException("Unsupported media menu action: " + id);
            }
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException
            or Microsoft.Data.Sqlite.SqliteException)
        {
            if (Current()) ShowNotification(Texts.T("notification.error"), Texts.T("media.galleryFailed"), NotificationType.Error,
                operation: "media.menu");
        }
        finally
        {
            if (hostCurrent())
            {
                _mediaPage?.RefreshUserData();
                if (presentationCurrent()) RestoreMediaPageMenuFocus(origin);
            }
        }
    }

    // Own media tags from the media page use the gallery's exact target rules. A
    // menu callback carries both the frozen index entry and its presentation guard;
    // it cannot fall back to the selected tile after an await.
    private Task EditMediaLibraryTagsAsync(MediaLibraryEntry entry) =>
        EditMediaLibraryTagsForMenuAsync(entry, () => true);

    private async Task EditMediaLibraryTagsForMenuAsync(MediaLibraryEntry indexed, Func<bool> presentationCurrent,
        GalleryEntry? alreadyResolved = null)
    {
        Func<bool> hostCurrent = CaptureMediaPageMenuGuard();
        bool Current() => hostCurrent() && presentationCurrent();
        if (!Current() || _mediaTagDialogOpen) return;
        _mediaTagDialogOpen = true;
        Control? origin = FocusManager?.GetFocusedElement() as Control;
        try
        {
            GalleryEntry? match = alreadyResolved ?? await MediaMenuIdentity.ResolveAsync(indexed,
                _session.OpenArchiveReaderAsync, Storage.MediaAssetReader.OpenAsync, Current, CancellationToken.None);
            if (!Current()) return;
            PrivateMediaTagTarget? target = GalleryMediaTagTarget.Resolve(match, out GalleryMediaTagUnavailable reason);
            if (match == null || target == null)
            {
                ShowNotification(Texts.T("notification.error"), Texts.T(match == null ? "media.galleryFailed"
                    : reason == GalleryMediaTagUnavailable.NoPostVersion ? "tags.media.unbound" : "tags.media.unsupported"),
                    NotificationType.Warning, operation: "media.tags");
                return;
            }
            MediaTagSession session = new(target, _session.GetPublishedPrivateTagSnapshot,
                _session.DefinePrivateTagAsync, _session.SetMediaTagAsync);
            MediaTagDialog dialog = new(session, match.Asset.OriginalFileName ?? Path.GetFileName(match.Asset.Path));
            MediaPageTagWindow = dialog;
            await dialog.ShowDialog(this);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException
            or Microsoft.Data.Sqlite.SqliteException)
        {
            if (Current()) ShowNotification(Texts.T("notification.error"), Texts.T("media.galleryFailed"), NotificationType.Error,
                operation: "media.tags");
        }
        finally
        {
            MediaPageTagWindow = null;
            _mediaTagDialogOpen = false;
            if (hostCurrent())
            {
                _mediaPage?.RefreshUserData();
                if (presentationCurrent()) RestoreMediaPageMenuFocus(origin);
            }
        }
    }

    private void RestoreMediaPageMenuFocus(Control? origin)
    {
        if (_isClosing || _selectedPage != WorkspacePage.Media || _gallery != null
            || OwnedWindows.Any(window => window.IsVisible)) return;
        if (origin is { IsEffectivelyVisible: true } && origin.IsAttachedToVisualTree()) origin.Focus();
        else _mediaPage?.FocusGrid();
    }

    // The same export dialog as the gallery and the favorites: verified originals, no overwriting, per-file results.
    private Task ExportMediaLibraryOriginalsAsync(IReadOnlyList<Favorites.FavoriteExportAsset> assets, int unavailable, string scope)
        => _isClosing ? Task.CompletedTask : ArchivePage.OpenOriginalExportAsync(assets, _mediaPage, unavailable, scope);
}


internal static class MediaMenuIdentity
{
    internal static bool Matches(MediaLibraryEntry indexed, GalleryEntry resolved) =>
        indexed.Thread == resolved.Key.Thread && indexed.Directory == resolved.Manifest.Directory
        && indexed.AssetId == resolved.Asset.AssetId && indexed.PostId == resolved.Post?.PostId
        && indexed.Position == resolved.Attachment?.Position
        && indexed.AttachmentRevisionKey == resolved.AttachmentResolution?.Binding?.Reference.RevisionKey
        && string.Equals(indexed.Sha256, resolved.Asset.Sha256, StringComparison.OrdinalIgnoreCase)
        && indexed.Length == resolved.Asset.Length && indexed.MediaId == resolved.Asset.MediaIdentifier
        && indexed.Variant == resolved.Asset.Variant && indexed.Historical == resolved.Historical
        && indexed.Unassigned == resolved.Unassigned;

    internal static async Task<GalleryEntry?> ResolveAsync(MediaLibraryEntry indexed,
        Func<CancellationToken, Task<ArchiveReader>> openReader,
        Func<string, CancellationToken, Task<MediaAssetSnapshot>> openManifest,
        Func<bool> isCurrent, CancellationToken token)
    {
        if (!isCurrent() || token.IsCancellationRequested || indexed.Presence != MediaLibraryPresence.Present
            || indexed.Directory == null) return null;
        GalleryEntry? resolved;
        await using (ArchiveReader reader = await openReader(token))
        {
            if (!isCurrent()) return null;
            ArchivedThreadView? thread = await reader.ReadThreadAsync(indexed.Thread, token);
            if (!isCurrent() || thread == null || thread.Summary.Revision != indexed.ThreadRevision) return null;
            IReadOnlyList<string> directories = await reader.GetMediaDirectoriesAsync(indexed.Thread, token);
            if (!isCurrent() || !directories.Contains(indexed.Directory, StringComparer.Ordinal)) return null;
            List<MediaAssetSnapshot> manifests = new();
            foreach (string directory in directories.Distinct(StringComparer.Ordinal))
            {
                manifests.Add(await openManifest(directory, token));
                if (!isCurrent()) return null;
            }
            GalleryEntry[] matches = GallerySelection.Build(thread, manifests, allSavedVersions: true, includeOtherMedia: true)
                .Entries.Where(entry => Matches(indexed, entry)).ToArray();
            resolved = matches.Length == 1 ? matches[0] : null;
        }
        if (resolved == null || !isCurrent() || token.IsCancellationRequested) return null;
        try
        {
            await using Stream verified = await resolved.Manifest.OpenVerifiedReadAsync(resolved.Asset.AssetId, token);
            return isCurrent() && !token.IsCancellationRequested ? resolved : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return null; }
    }
}
