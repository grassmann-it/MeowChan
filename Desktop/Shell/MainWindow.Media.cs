using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Notifications;
using Avalonia.VisualTree;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.MediaActions;
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
            ReadBooruCatalog = _session.ReadBooruCatalog,
            CacheLimitBytes = () => (long)_session.Config.MediaIndexCacheLimitMiB * 1024 * 1024,
            TagServices = new(_session.GetPublishedPrivateTagSnapshot,
                (name, subjects, expectedRevision, token) => _session.DefineAndSetMediaSubjectTagsAsync(name, subjects, expectedRevision, token),
                (tag, subjects, assigned, expectedRevision, token) => _session.SetMediaSubjectTagsAsync(tag, subjects, assigned, expectedRevision, token)),
            ExportOriginals = ExportMediaLibraryOriginalsAsync,
            GoToDownloadsRequested = GoToDownloads,
            GoToArchiveRequested = () => NavigateWorkspace(WorkspacePage.Archive)
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
        // Direct tile/selection opens have no menu guard of their own. Keep the host's
        // navigation revision and any caller's stricter frozen-menu guard throughout admission.
        Func<bool> hostCurrent = CaptureMediaPageMenuGuard();
        if (_mediaPage is not { } page || !hostCurrent() || current?.Invoke() == false
            || start < 0 || start >= window.Count) return;
        MediaLibraryEntry chosen = window[start];
        IReadOnlyList<MediaLibraryEntry> shown = page.ShownEntries;
        MediaLibrarySort sort = page.Sort;
        MediaLibraryFilter filter = page.CurrentFilter();
        bool presentationCurrent = true;
        void OnPresentationChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
        {
            // Present() replaces the rows even for an unchanged local rebuild/user-data refresh.
            // Freeze the logical entries, not the containers; a real change stays stale on return.
            if (e.Property == ItemsControl.ItemsSourceProperty)
                presentationCurrent &= page.Sort == sort && page.FilterError == null
                    && filter == page.CurrentFilter() && shown.SequenceEqual(page.ShownEntries);
        }
        bool Current() => hostCurrent() && presentationCurrent && current?.Invoke() != false;
        page.Grid.PropertyChanged += OnPresentationChanged;
        try
        {
            IReadOnlyList<GalleryEntry> entries = await MediaLibraryGallery.ResolveAsync(window, _session.OpenArchiveReaderAsync,
                Storage.MediaAssetReader.OpenAsync, readBooruCatalog: _session.ReadBooruCatalog);
            GalleryEntry? first = entries.FirstOrDefault(entry => MediaMenuIdentity.Matches(chosen, entry));
            if (!Current()) return;
            if (first == null)
            {
                ShowNotification(Texts.T("notification.error"), Texts.T("media.galleryFailed"), NotificationType.Warning, operation: "media.gallery");
                return;
            }
            // The grid is the origin: closing the gallery returns focus to the selected tile, not to the page shell.
            // A still pending second restore of an earlier "Back to media" must not move selection and focus under the gallery.
            page.EndPendingReturn();
            if (!Current()) return;
            await OpenMediaGalleryAsync(entries, first.EntryKey, page.Grid, stillRequested: Current);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
        {
            if (Current()) ShowNotification(Texts.T("notification.error"), Texts.T("media.galleryFailed"), NotificationType.Error, operation: "media.gallery");
        }
        finally { page.Grid.PropertyChanged -= OnPresentationChanged; }
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
                Storage.MediaAssetReader.OpenAsync, Current, CancellationToken.None, _session.ReadBooruCatalog);
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
            RefreshMediaPageAfterMenuAction(origin, hostCurrent, presentationCurrent);
        }
    }

    // Own media tags from the media page use the gallery's exact target rules (bound or loose local). A
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
                _session.OpenArchiveReaderAsync, Storage.MediaAssetReader.OpenAsync, Current, CancellationToken.None, _session.ReadBooruCatalog);
            if (!Current()) return;
            MediaTagSubject? target = MediaTagSubjects.Of(match, out GalleryMediaTagUnavailable reason);
            if (match == null || target == null)
            {
                ShowNotification(Texts.T("notification.error"), match == null ? Texts.T("media.galleryFailed")
                    : MediaMenuReasons.Tags(reason, BooruMediaIdentity.IsBooruThread(match.Key.Thread)),
                    NotificationType.Warning, operation: "media.tags");
                return;
            }
            MediaTagSession session = new(target, _session.GetPublishedPrivateTagSnapshot,
                _session.DefineAndSetMediaSubjectTagAsync, _session.SetMediaSubjectTagAsync);
            MediaTagDialog dialog = new(session, match.Asset.OriginalFileName ?? Path.GetFileName(match.Asset.Path), _session.ReadBooruCatalog);
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
            RefreshMediaPageAfterMenuAction(origin, hostCurrent, presentationCurrent);
        }
    }

    private void RefreshMediaPageAfterMenuAction(Control? origin, Func<bool> hostCurrent, Func<bool> presentationCurrent)
    {
        if (!hostCurrent() || _mediaPage is not { } page) return;
        // A changed store calls Present(), invalidating the menu that admitted this action.
        // Validate before our refresh, then guard the deferred focus against the NEW presentation.
        bool restoreFocus = presentationCurrent();
        object? rows = page.Grid.ItemsSource;
        page.RefreshUserData();
        if (restoreFocus)
        {
            // A refreshed/recycled container is no longer the old keyboard origin.
            page.RestoreMediaMenuActionFocus(ReferenceEquals(rows, page.Grid.ItemsSource) ? origin : null,
                () => hostCurrent() && !OwnedWindows.Any(window => window.IsVisible));
        }
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
        && indexed.Unassigned == resolved.Unassigned
        // The loose (local tag) presentation must still hold; a changed namespace is a different presentation.
        && indexed.LocalSlot == (resolved.LocalTagEligible ? resolved.Asset.RequestedFileName : null);

    internal static async Task<GalleryEntry?> ResolveAsync(MediaLibraryEntry indexed,
        Func<CancellationToken, Task<ArchiveReader>> openReader,
        Func<string, CancellationToken, Task<MediaAssetSnapshot>> openManifest,
        Func<bool> isCurrent, CancellationToken token, Func<BooruCatalogSnapshot?>? readBooruCatalog = null)
    {
        if (!isCurrent() || token.IsCancellationRequested || indexed.Presence != MediaLibraryPresence.Present
            || indexed.Directory == null) return null;
        GalleryEntry? resolved;
        if (indexed.BooruPostId != null)
        {
            // A downloaded provider post is no archived thread: its entry comes from the catalog's own link and the manifest.
            BooruCatalogSnapshot? catalog = null;
            try { catalog = readBooruCatalog?.Invoke(); }
            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or ObjectDisposedException) { }
            resolved = await BooruMediaGallery.ResolveAsync(indexed, catalog, openManifest, token);
            if (resolved == null || !Matches(indexed, resolved) || !isCurrent() || token.IsCancellationRequested) return null;
            try
            {
                await using Stream verified = await resolved.Manifest.OpenVerifiedReadAsync(resolved.Asset.AssetId, token);
                return isCurrent() && !token.IsCancellationRequested ? resolved : null;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException) { return null; }
        }
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
