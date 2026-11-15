using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Notifications;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using MeowChan.Desktop;
using MeowChan.Desktop.Booru;
using MeowChan.Desktop.Feedback;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.MediaActions;
using MeowChan.Favorites;
using MeowChan.Archive;
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
        _gallery.ManageRatingsRequested += () => _ = RunGalleryActionAsync(ManageMediaRatingsAsync);
        _gallery.FavoriteToggleRequested += entry => _ = RunGalleryActionAsync(() => ToggleGalleryFavoriteAsync(entry), entry.Key.ToString());
        _gallery.FavoriteSetRequested += (entry, selected) => _ = RunGalleryActionAsync(() => SetGalleryFavoriteAsync(entry, selected), entry.Key.ToString());
        _gallery.ExportOriginalRequested += entry => _ = RunGalleryActionAsync(() => ArchivePage.OpenOriginalExportAsync(
            new[] { FavoriteExportAsset.From(entry.Key.Thread, entry.Manifest, entry.Asset) }, _gallery), entry.Key.ToString());
        _gallery.ShowInFolderRequested += entry =>
        {
            string? directory = Path.GetDirectoryName(entry.Asset.Path);
            if (directory != null) OpenDirectory(directory);
        };
        _gallery.CopyPathRequested += entry => _ = RunGalleryActionAsync(() => CopyMediaPathAsync(entry), entry.Key.ToString());
        _gallery.OpenExternalRequested += entry => _ = RunGalleryActionAsync(() => OpenMediaFileExternallyAsync(entry), entry.Key.ToString());
        _gallery.DetailsRequested += entry => _ = RunGalleryActionAsync(() => ShowGalleryDetailsAsync(entry), entry.Key.ToString());
        _gallery.TagsRequested += entry => _ = RunGalleryActionAsync(() => { OpenGalleryTagPanel(); return Task.CompletedTask; }, entry.Key.ToString());
        _gallery.QueueRequested += entry => _ = RunGalleryActionAsync(() => AddGalleryEntryToTagQueueAsync(entry), entry.Key.ToString());
        // The gallery menu's provider post actions and its summary of unavailable actions, like the media page's.
        _gallery.ProviderPostUrl = GalleryProviderPostUrl;
        _gallery.ProviderPostOpenRequested += entry => _ = RunGalleryActionAsync(() => OpenGalleryProviderPostAsync(entry), entry.Key.ToString());
        _gallery.ProviderPostCopyRequested += entry => _ = RunGalleryActionAsync(() => CopyGalleryProviderPostAsync(entry), entry.Key.ToString());
        _gallery.MenuExplanationRequested += text => ShowNotification(Texts.T("archive.command.unavailable"), text,
            NotificationType.Information, operation: "gallery.menu");
        // The post bookmark entry of the gallery menu: the same add / edit dialog as the reader's, for the post the medium belongs to.
        _gallery.PostBookmarkRequested += entry =>
        {
            Control? origin = _gallery;
            if (entry.Post is { } post) _ = RunGalleryActionAsync(() => EditPostBookmarkAsync(new MeowChan.Persistence.PostBookmarkKey(entry.Key.Thread, post.PostId), origin),
                entry.Key.ToString());
        };
        _gallery.SaveFrameRequested += entry => _ = RunGalleryActionAsync(SaveGalleryFrameAsync, entry.Key.ToString());
        // L repeats the shown video: a short status says what the key did ("Repeat on / off", or that a running slideshow decides).
        _gallery.VideoLoopKeyed += (result, reason) => ShowNotification(
            result switch
            {
                VideoLoopKeyResult.On => Texts.T("gallery.video.loopOn"),
                VideoLoopKeyResult.Off => Texts.T("gallery.video.loopOff"),
                _ => reason ?? Texts.T("gallery.video.loopOff")
            }, string.Empty, NotificationType.Information, operation: "video.loop");
    }

    // Test seam: the folder the person would pick for saved frames (null: the picker was cancelled).
    internal Func<Task<string?>>? GalleryFramePickerForTests { get; set; }

    /// <summary>
    /// The folder a saved video frame goes to: the dedicated frames folder chosen earlier (when it still exists and is not
    /// the archive export folder), else the one the person picks now, which is then remembered. Null when nothing was
    /// chosen; <paramref name="cancelled"/> tells a refusal from a picker that failed.
    /// </summary>
    internal async Task<(string? Directory, bool Cancelled)> ResolveGalleryFrameDirectoryAsync()
    {
        string remembered = _config.GalleryFrameDirectory;
        if (GalleryFrameFile.IsDedicatedFolder(remembered, _config.LastExportDirectory)
            && await Task.Run(() => Directory.Exists(remembered))) return (remembered, false);
        string? chosen;
        if (GalleryFramePickerForTests is { } seam) chosen = await seam();
        else
        {
            LocalFolderPickResult result = await LocalFolderPicker.PickAsync(StorageProvider, Texts.T("gallery.frame.pickFolder"));
            if (result.Status == LocalFolderPickStatus.Cancelled) return (null, true);
            chosen = result.Status == LocalFolderPickStatus.Selected ? result.Path : null;
        }
        if (string.IsNullOrWhiteSpace(chosen)) return (null, GalleryFramePickerForTests != null);
        // A folder picked here is used now; it is remembered only when it is not the export folder.
        if (GalleryFrameFile.IsDedicatedFolder(chosen, _config.LastExportDirectory))
        {
            try { await _session.SaveViewPreferencesAsync(current => current.GalleryFrameDirectory = chosen); }
            catch (Exception error) when (error is not OutOfMemoryException) { /* The frame is still saved; the folder is asked again next time. */ }
        }
        return (chosen, false);
    }

    private async Task SaveGalleryFrameAsync()
    {
        if (_gallery is not { } gallery) return;
        (string? directory, bool cancelled) = await ResolveGalleryFrameDirectoryAsync();
        if (_isClosing || cancelled) return;
        string? path = directory == null ? null : await gallery.SaveVideoFrameAsync(directory);
        if (_isClosing) return;
        if (path != null) ShowNotification(Texts.T("gallery.frame.saved"), path, NotificationType.Information, operation: "video.snapshot");
        else ShowNotification(Texts.T("notification.error"), Texts.T("gallery.frame.failed"), NotificationType.Warning, operation: "video.snapshot");
    }

    // Test seam: no program is ever started; the path that would have been handed to the desktop is passed instead.
    internal Func<string, Task>? OpenFileForTests { get; set; }

    private Task CopyMediaPathAsync(GalleryEntry entry) => CopyFilePathAsync(entry.Asset.Path);

    // The saved file's path as plain text: the way to chat or an editor until drag-out exists. Local only.
    private async Task CopyFilePathAsync(string path)
    {
        try
        {
            if (CopyTextForTests is { } seam) await seam(path);
            else if (Clipboard is { } clipboard) await clipboard.SetTextAsync(path);
            else throw new InvalidOperationException("The clipboard is unavailable.");
            ShowNotification(Texts.T("media.copyPath.done"), path, NotificationType.Information, operation: "media.copy-path");
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        { ShowNotification(Texts.T("notification.error"), Texts.T("media.copyPath.failed"), NotificationType.Warning, operation: "media.copy-path"); }
    }

    // A file the gallery cannot show is handed to the system's default program, except types that could run as a
    // program; those can only be shown in their folder.
    private async Task OpenMediaFileExternallyAsync(GalleryEntry entry)
    {
        string path = entry.Asset.Path;
        if (!MediaFileActions.CanOpenWithDefaultProgram(path))
        {
            ShowNotification(Texts.T("notification.error"), Texts.T("gallery.fileOnly.blocked"), NotificationType.Warning, operation: "media.open-external");
            return;
        }
        if (!File.Exists(path))
        {
            ShowNotification(Texts.T("notification.error"), Texts.T("gallery.fileOnly.missing"), NotificationType.Warning, operation: "media.open-external");
            return;
        }
        try { await (OpenFileForTests?.Invoke(path) ?? ExternalLauncher.OpenAsync(path)); }
        catch (Exception error) when (error is not OutOfMemoryException)
        { ShowNotification(Texts.T("notification.error"), Texts.T("location.openFailed"), NotificationType.Warning, operation: "media.open-external"); }
    }

    private Uri? GalleryProviderPostUrl(GalleryEntry entry)
    {
        if (!BooruMediaIdentity.TryGetPostId(entry.Key.Thread, out string postId)) return null;
        BooruPostRecord? record = null;
        try { record = _session.ReadBooruCatalog()?.TryGetPost(BooruMediaIdentity.Source, postId); }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or ObjectDisposedException) { }
        return BooruMediaIdentity.PostUrl(postId, record?.Post.PostUrl);
    }

    // The exact entry the menu was opened on; a failure is said in words and changes nothing.
    private async Task OpenGalleryProviderPostAsync(GalleryEntry entry)
    {
        if (GalleryProviderPostUrl(entry) is not { } url) return;
        try { await (OpenProviderPageForTests?.Invoke(url) ?? ExternalLauncher.OpenAsync(url.AbsoluteUri)); }
        catch (Exception error) when (error is not OutOfMemoryException)
        { ShowNotification(Texts.T("notification.error"), Texts.T("booru.media.openFailed"), NotificationType.Warning, operation: "gallery.provider-post"); }
    }

    private async Task CopyGalleryProviderPostAsync(GalleryEntry entry)
    {
        if (GalleryProviderPostUrl(entry) is not { } url) return;
        try
        {
            if (CopyProviderPageForTests != null) await CopyProviderPageForTests(url);
            else if (Clipboard is { } clipboard) await clipboard.SetTextAsync(url.AbsoluteUri);
            else throw new InvalidOperationException("The clipboard is unavailable.");
            ShowNotification(Texts.T("booru.media.copied"), url.AbsoluteUri, NotificationType.Information, operation: "gallery.provider-post");
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        { ShowNotification(Texts.T("notification.error"), Texts.T("booru.media.copyFailed"), NotificationType.Warning, operation: "gallery.provider-post"); }
    }

    private async Task RunGalleryActionAsync(Func<Task> command, string? context = null)
    {
        try { await command(); }
        catch (OperationCanceledException) when (_isClosing) { }
        catch (Exception error) when (error is not OutOfMemoryException)
        { if (!_isClosing) ShowNotification(Texts.T("notification.error"), Texts.T("gallery.actionFailed", error.Message), NotificationType.Error, context: context, operation: "gallery.action"); }
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
        // Modals plan MD2: a read-only modal details dialog on the prompt step. The title stands in the title bar only,
        // and a lone Close button is left out: × at the top right and Escape close it.
        Window dialog = DialogWindow.Create(CommandCatalog.Label("thread.details"), DialogSize.S, modal: true);
        dialog.Name = "GalleryDetailsDialog";
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
        text.Margin = new Thickness(16, 0, 16, 16);
        DialogWindow.Frame(dialog, DialogFrame.Build(header: null, text, footer: null), modal: true);
        DialogEscape.Attach(dialog);
        await dialog.ShowDialog(this);
        if (origin?.IsEffectivelyVisible == true) origin.Focus();
    }
}

public partial class MainWindow
{
    internal BooruMediaDetailsDialog? BooruDetailsWindow { get; private set; }
    // Test seams: no browser is ever started and the clipboard is only touched when the shell has one.
    internal Func<Uri, Task>? OpenProviderPageForTests { get; set; }
    internal Func<Uri, Task>? CopyProviderPageForTests { get; set; }
    internal Func<string, Task>? CopyTextForTests { get; set; }

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
                if (details.Tags.Count > 0) lines.Add(Texts.Count("booru.media.gallery.tags", details.Tags.Count));
            }
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException) { }
        return lines;
    }

    // The same details for a downloaded provider medium from the media page and from the gallery. Local data only.
    private async Task ShowBooruMediaDetailsAsync(GalleryEntry entry, string postId, Control? origin)
    {
        // The dialog may remain open across a plugin/profile presentation change. Capture the
        // exact service object that was current at open time; the live gate below refuses to
        // hand the old dialog's query to a later composition.
        BooruWorkbenchServices? openedServices = CanOpenBooru ? _session.BooruServices : null;
        BooruMediaDetails? details = null;
        // Off the UI thread: a cold catalog cache parses the whole file once.
        try { if (await Task.Run(_session.ReadBooruCatalog) is { } catalog) details = BooruMediaProjection.Find(catalog, postId, localFilePresent: true); }
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
            },
            async text =>
            {
                if (CopyTextForTests is { } seam) { await seam(text); return; }
                if (Clipboard == null) throw new InvalidOperationException("Clipboard is unavailable.");
                await Clipboard.SetTextAsync(text);
            },
            openedServices is null ? null : query => UseAsBooruSearchForDialog(query, openedServices),
            openedServices is null ? null : () => IsBooruSearchAvailable(openedServices));
        BooruDetailsWindow = dialog;
        try { await dialog.ShowDialog(this); }
        finally
        {
            BooruDetailsWindow = null;
            if (!_isClosing && origin?.IsEffectivelyVisible == true) origin.Focus();
        }
    }

    /// <summary>
    /// Live presentation/lifetime gate for a source-tag query opened from local media. The
    /// service reference is deliberately part of the gate: <see cref="UseAsBooruSearch"/>
    /// would otherwise resolve the current page and could feed a stale dialog into a new
    /// profile/plugin composition.
    /// </summary>
    private bool IsBooruSearchAvailable(BooruWorkbenchServices openedServices) =>
        !_isClosing && CanOpenBooru && ReferenceEquals(_session.BooruServices, openedServices);

    private bool UseAsBooruSearchForDialog(string query, BooruWorkbenchServices openedServices)
    {
        if (!IsBooruSearchAvailable(openedServices) || !NavigateWorkspace(WorkspacePage.Booru)) return false;
        if (!IsBooruSearchAvailable(openedServices) || _booruPage is not { } page
            || !ReferenceEquals(page.Services, openedServices)) return false;

        // This is the same unsent-draft handoff as the main Booru entry, but it is bound to the
        // captured page identity. No search or other provider request is started here.
        page.ShowQuery(query);
        Dispatcher.UIThread.Post(() =>
        {
            if (IsBooruSearchAvailable(openedServices) && _selectedPage == WorkspacePage.Booru
                && ReferenceEquals(_booruPage, page))
                page.View.FocusSearchInput();
        });
        return true;
    }
}
