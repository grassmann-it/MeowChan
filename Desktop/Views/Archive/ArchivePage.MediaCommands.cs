using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.MediaActions;
using MeowChan.Favorites;
using MeowChan.Gallery;
using MeowChan.Tags;

namespace MeowChan;

public partial class ArchivePage
{
    internal ContextMenu? ArchiveMediaMenu => _mediaMenuPresenter.Menu;
    internal MediaMenuSession? ArchiveMediaMenuSession => _mediaMenuPresenter.Session;
    internal Func<GalleryEntry, bool>? IsArchiveFavorite { get; set; }
    internal Func<GalleryEntry, Task>? ToggleArchiveFavoriteAsync { get; set; }
    internal Func<GalleryEntry, Task>? EditArchiveMediaTagsAsync { get; set; }
    /// <summary>
    /// Opens the quick tag popover (the shell owns the tag session) for one file: the exact entry, the tile or attachment it opened
    /// on, the context of the suggestions and the page guard. Completes when the popover closed. Null opens the tag dialog of
    /// <see cref="EditArchiveMediaTagsAsync"/> instead.
    /// </summary>
    internal Func<GalleryEntry, Control, TagContext, Func<bool>, Task>? TagPopoverAsync { get; set; }
    /// <summary>Limits and suggestions of the selection's tag popover (the shell binds the saved settings); unset uses the defaults.</summary>
    internal Func<TagContext, QuickTagPopoverOptions>? TagPopoverOptions { get; set; }
    /// <summary>Copies a saved file's path to the clipboard and says so; supplied by the shell.</summary>
    internal Func<string, Task>? CopyFilePathAsync { get; set; }
    private readonly Dictionary<FavoriteAssetKey, string> _favoriteErrors = new();
    internal void ClearFavoriteError(FavoriteAssetKey key)
    {
        if (_favoriteErrors.Remove(key)) RefreshArchiveFavoriteControls();
    }

    private void ConfigureArchiveMediaFavorites()
    {
        if (_thread == null) return;
        ResourceId threadId = _thread.Summary.Id;
        string url = _thread.Summary.Url;
        GalleryEntry Entry(ArchiveAssetRow row) => new(new GalleryAssetKey(threadId, row.Manifest.Directory, row.Asset.AssetId),
            row.Manifest, row.Asset, null, null, !row.Asset.IsCurrent, true) { ThreadUrl = url };
        foreach (ArchiveAssetRow row in _assets)
        {
            GalleryEntry entry = Entry(row);
            row.FavoriteAvailable = () => _available && !_ended && ToggleArchiveFavoriteAsync != null;
            row.IsFavorite = () => IsArchiveFavorite?.Invoke(entry) == true;
            row.ToggleFavoriteAsync = () => ToggleArchiveMediaFavoriteAsync(entry);
            row.FavoriteError = () => _favoriteErrors.GetValueOrDefault(FavoriteAssetKey.From(entry.Key.Thread, entry.Asset));
        }
        if (ArchivePosts.ItemsSource is System.Collections.IEnumerable posts)
            foreach (ArchivePostRow post in posts.OfType<ArchivePostRow>())
                foreach (ArchiveAttachmentRow row in post.Attachments)
                {
                    ArchiveAssetRow? match = FindMatchingAsset(threadId, post.Id, row.Attachment, _assets);
                    GalleryEntry? entry = match == null ? null : Entry(match);
                    row.FavoriteAvailable = () => _available && !_ended && entry != null && ToggleArchiveFavoriteAsync != null;
                    row.IsFavorite = () => entry != null && IsArchiveFavorite?.Invoke(entry) == true;
                    row.ToggleFavoriteAsync = () => entry == null ? Task.CompletedTask : ToggleArchiveMediaFavoriteAsync(entry);
                    row.FavoriteError = () => entry == null ? null : _favoriteErrors.GetValueOrDefault(FavoriteAssetKey.From(entry.Key.Thread, entry.Asset));
                }
        RefreshArchiveFavoriteControls();
    }

    private async Task ToggleArchiveMediaFavoriteAsync(GalleryEntry entry)
    {
        if (ToggleArchiveFavoriteAsync == null) return;
        FavoriteAssetKey key = FavoriteAssetKey.From(entry.Key.Thread, entry.Asset);
        _favoriteErrors.Remove(key);
        RefreshArchiveFavoriteControls();
        try { await ToggleArchiveFavoriteAsync(entry); }
        catch (Exception error)
        {
            _favoriteErrors[key] = error.Message;
            RefreshArchiveFavoriteControls();
            throw;
        }
        if (_archiveFavoritesOnly) RenderMedia();
        else RefreshArchiveFavoriteControls();
    }

    internal void RefreshArchiveFavoriteControls()
    {
        Control reader = ArchiveReaderPane;
        foreach (ArchiveAttachmentView view in reader.GetVisualDescendants().OfType<ArchiveAttachmentView>()) view.RefreshFavorite();
        foreach (ArchiveMediaTileView view in reader.GetVisualDescendants().OfType<ArchiveMediaTileView>()) view.RefreshFavorite();
    }

    private void InitializeArchiveMediaCommands()
    {
        AddHandler(ContextRequestedEvent, OnArchiveMediaContextRequested, RoutingStrategies.Tunnel);
        AddHandler(KeyDownEvent, OnArchiveMediaContextKey, RoutingStrategies.Tunnel);
    }

    // The Menu key and Shift+F10 open the same media menu as a right-click, for the focused row or attachment.
    private void OnArchiveMediaContextKey(object? sender, KeyEventArgs e)
    {
        if (_ended || !_available || IsReaderDialogOpen || e.Source is not Control source || !MediaMenuPresenter.IsContextKey(e)) return;
        if (!source.GetSelfAndVisualAncestors().OfType<Control>().Any(control => control is ArchiveAttachmentView
            || control.Name is "ArchiveMediaGrid" or "ArchiveMediaTiles")) return;
        ContextRequestedEventArgs request = new() { RoutedEvent = ContextRequestedEvent };
        source.RaiseEvent(request);
        e.Handled = request.Handled;
    }

    private void OnArchiveMediaContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (e.Handled || _ended || !_available || e.Source is not Control source) return;
        // Text inputs and selectable text keep their own native menu.
        if (MediaMenuPresenter.IsNativeTextSource(source)) return;
        Control[] ancestors = source.GetSelfAndVisualAncestors().OfType<Control>().ToArray();
        ArchiveAttachmentView? attachmentView = ancestors.OfType<ArchiveAttachmentView>().FirstOrDefault();
        ArchiveAttachmentRow? attachment = attachmentView?.DataContext as ArchiveAttachmentRow;
        ArchiveAssetRow? asset = ancestors.Select(control => control.DataContext).OfType<ArchiveAssetRow>().FirstOrDefault();
        Control? origin = attachmentView;
        if (asset != null) origin = ancestors.First(control => control.DataContext is ArchiveAssetRow);
        if (asset == null && attachment == null && !e.TryGetPosition(this, out _))
        {
            Control? list = ancestors.FirstOrDefault(control => control.Name is "ArchiveMediaGrid" or "ArchiveMediaTiles");
            // No pointer: the menu is for the focused file of the list (the Menu key or Shift+F10), which may be one of several selected.
            asset = list == null ? null : SelectedMediaRow();
            origin = list;
        }
        string? postId = ancestors.Select(control => control.DataContext).OfType<ArchivePostRow>().FirstOrDefault()?.Id;
        if (attachment != null) asset = _selectedThread == null || postId == null ? null : FindMatchingAsset(_selectedThread, postId, attachment.Attachment, _assets);
        if (asset == null && attachment == null) return;
        if (_selectedThread == null || _thread == null || origin == null) return;
        e.Handled = true;
        (ContextMenu menu, MediaMenuSession session) = BuildArchiveMediaMenu(asset, attachment, origin, postId, ArchiveMenuSurface(ancestors, attachment),
            SeveralSelectedWith(asset, attachment));
        _mediaMenuPresenter.Show(menu, session, origin);
    }

    private readonly MediaMenuPresenter _mediaMenuPresenter = new();

    // The selection the menu also offers its selection actions for: only when the file it opens on is one of several selected files.
    private ArchiveAssetRow[]? SeveralSelectedWith(ArchiveAssetRow? asset, ArchiveAttachmentRow? attachment)
    {
        if (attachment != null || asset == null) return null;
        IReadOnlyList<ArchiveAssetRow> selected = SelectedMediaRows();
        return ArchiveMediaSelection.IsSeveral(selected, asset) ? selected.ToArray() : null;
    }

    private static MediaMenuSurface ArchiveMenuSurface(Control[] ancestors, ArchiveAttachmentRow? attachment) =>
        attachment != null ? MediaMenuSurface.ArchiveInline
        : ancestors.Any(control => control.Name == "ArchiveMediaTiles") ? MediaMenuSurface.ArchiveTiles
        : MediaMenuSurface.ArchiveTable;

    private bool TryRouteArchiveMediaShortcut(Control owner, KeyEventArgs e)
    {
        if (e.Handled || CommandRouter.IsNativeInput(e.Source, e.Key) || IsReaderDialogOpen) return false;
        string[] ids = { "favorites.toggle", "media.tags.edit", "media.export-original", "media.show-in-folder", "media.copy-path", "media.details", "gallery.show-in-thread" };
        if (!ids.Any(id => CommandBindings.Matches(e, id))) return false;
        if (_ended || !_available || _busy || _thread == null || _selectedThread == null) { e.Handled = true; return true; }
        var ancestors = (e.Source as Avalonia.Visual)?.GetSelfAndVisualAncestors().OfType<Control>().ToArray() ?? Array.Empty<Control>();
        var attachmentView = ancestors.OfType<ArchiveAttachmentView>().FirstOrDefault();
        var attachment = attachmentView?.DataContext as ArchiveAttachmentRow;
        var asset = ancestors.Select(c => c.DataContext).OfType<ArchiveAssetRow>().FirstOrDefault();
        if (asset == null && attachment == null && owner.Name is "ArchiveMediaGrid" or "ArchiveMediaTiles") asset = SelectedMediaRow();
        string? postId = ancestors.Select(c => c.DataContext).OfType<ArchivePostRow>().FirstOrDefault()?.Id;
        if (attachment != null) asset = postId == null ? null : FindMatchingAsset(_selectedThread, postId, attachment.Attachment, _assets);
        if (asset == null && attachment == null) return false;
        var origin = (Control?)attachmentView ?? owner;
        MediaMenuSurface surface = attachment != null ? MediaMenuSurface.ArchiveInline
            : owner.Name == "ArchiveMediaTiles" ? MediaMenuSurface.ArchiveTiles : MediaMenuSurface.ArchiveTable;
        return CommandBindings.RouteMenuAction(BuildArchiveMediaMenu(asset, attachment, origin, postId, surface, SeveralSelectedWith(asset, attachment)).Menu, e);
    }

    private (ContextMenu Menu, MediaMenuSession Session) BuildArchiveMediaMenu(ArchiveAssetRow? asset, ArchiveAttachmentRow? attachment,
        Control origin, string? postId, MediaMenuSurface surface, ArchiveAssetRow[]? selection = null)
    {
        Action<string> reportError = CaptureActionErrorReporter();
        // Freeze the current manifest references and post association together. Neither
        // a later page refresh nor a different lead may retarget an open media menu.
        var thread = _thread!;
        ResourceId threadId = thread.Summary.Id;
        ArchiveAssetRow[] assets = _assets.ToArray();
        ArchiveAssetRow[] visible = VisibleAssets().ToArray();
        GalleryEntry[] galleryEntries = BuildVisibleGalleryEntries(thread, assets, visible);
        GalleryEntry[] entries = GallerySelection.Build(thread, assets.Select(row => row.Manifest).DistinctBy(manifest => manifest.Directory),
            true, includeOtherMedia: true).Entries.ToArray();
        GalleryEntry[] candidates = asset == null ? Array.Empty<GalleryEntry>() : entries.Where(item => item.Key == new GalleryAssetKey(threadId, asset.Manifest.Directory, asset.Asset.AssetId)
            && (postId == null || item.Post?.PostId == postId)
            && (attachment == null || item.Attachment?.Position == attachment.Attachment.Position)).ToArray();
        GalleryEntry? entry = candidates.FirstOrDefault();
        // A saved file shared by several post bindings has several tag targets and several posts: never pick one silently.
        MediaTagSubject[] tagSubjects = candidates.Select(item => MediaTagSubjects.Of(item)).OfType<MediaTagSubject>().Distinct().ToArray();
        bool ambiguousTagTarget = tagSubjects.Length > 1;
        GalleryEntry? tagEntry = tagSubjects.Length == 1 ? candidates.First(item => Equals(MediaTagSubjects.Of(item), tagSubjects[0])) : null;
        string[] postIds = candidates.Select(item => item.Post?.PostId).OfType<string>().Distinct(StringComparer.Ordinal).ToArray();
        bool severalPosts = postIds.Length > 1;
        if (entry == null && asset != null)
            entry = new GalleryEntry(new GalleryAssetKey(threadId, asset.Manifest.Directory, asset.Asset.AssetId), asset.Manifest, asset.Asset,
                null, null, !asset.Asset.IsCurrent, true) { ThreadUrl = thread.Summary.Url };
        GalleryEntry? target = entry;
        // Keep the chosen post/position and its provenance when the visible sequence shares the same bytes.
        if (attachment != null && target != null)
            galleryEntries = galleryEntries.Select(item => item.Key == target.Key ? target : item).ToArray();
        GalleryEntry? galleryTarget = target == null ? null : galleryEntries.FirstOrDefault(item => item.EntryKey == target.EntryKey);
        MediaTagSubjects.Of(target, out GalleryMediaTagUnavailable tagReason);
        bool taggable = tagEntry != null;
        bool? favorite = target == null || IsArchiveFavorite == null ? null : IsArchiveFavorite(target);
        string? tagsBlocked = taggable ? null : MediaMenuReasons.Tags(tagReason, ambiguous: ambiguousTagTarget);
        MediaMenuFacts facts = new(surface, target == null ? null : severalPosts ? MediaMenuTarget.OfFile(target) : MediaMenuTarget.Of(target))
        {
            Present = target != null,
            GalleryWired = OpenMediaGallery != null,
            InGallerySequence = galleryTarget != null,
            Favorite = favorite == null ? MediaMenuFavorite.Unknown : favorite == true ? MediaMenuFavorite.On : MediaMenuFavorite.Off,
            FavoriteWired = ToggleArchiveFavoriteAsync != null,
            RatingWired = EditMediaRatingAsync != null,
            RatingBlocked = _mediaRatingError,
            TagsWired = EditArchiveMediaTagsAsync != null || TagPopoverAsync != null,
            TagsBlocked = tagsBlocked,
            TagsBlockedExplained = true,
            QueueWired = AddToTagQueue != null,
            QueueBlocked = TagQueueActions.BlockedReason(target != null, BooruMediaIdentity.IsBooruThread(threadId)),
            FolderWired = _openFolder != null,
            CopyPathWired = CopyFilePathAsync != null,
            Posts = severalPosts ? MediaMenuPostScope.Several : postIds.Length == 1 ? MediaMenuPostScope.Single : MediaMenuPostScope.None
        };
        MediaMenuModel model = MediaMenuComposition.Compose(facts);
        // The guard is the page state the menu was opened on. A different thread, a reloaded thread, a closed page or an
        // open filename search retires it; a mere filter change keeps the frozen visible sequence it was built from.
        MediaMenuSession session = new(surface, model.Target, () => !_ended && _available && !IsFilenameSearchOpen
            && ReferenceEquals(_thread, thread) && _selectedThread == threadId);
        // The popover outlives the menu session (a tag write invalidates the menu on purpose); it lives while this thread is on screen.
        bool PopoverCurrent() => !_ended && _available && ReferenceEquals(_thread, thread) && _selectedThread == threadId;
        Task Run(Func<Task> action) => RunArchiveMenuActionAsync(action, session, reportError);
        Dictionary<MediaMenuAction, Func<Task>> handlers = new()
        {
            [MediaMenuAction.Open] = async () =>
            {
                if (galleryTarget != null && OpenMediaGallery != null)
                    await OpenMediaGallery(galleryEntries, galleryTarget.EntryKey, origin);
            },
            [MediaMenuAction.Favorite] = async () => { if (target != null) await ToggleArchiveMediaFavoriteAsync(target); },
            [MediaMenuAction.Rating] = async () => { if (target != null && EditMediaRatingAsync != null) await EditMediaRatingAsync(target); },
            [MediaMenuAction.Tags] = async () =>
            {
                // A file that cannot be tagged gets the reason as text in the popover; a taggable one the tag popover on its tile.
                if (tagEntry == null)
                {
                    QuickTagPopover.OpenBlocked(origin, target?.Asset.OriginalFileName ?? attachment?.Label ?? string.Empty,
                        tagsBlocked ?? MediaMenuReasons.Unavailable, new QuickTagPopoverOptions { IsCurrent = PopoverCurrent });
                }
                else if (TagPopoverAsync != null)
                    await TagPopoverAsync(tagEntry, origin, QuickTagPopoverContext.ContextOf(tagEntry, galleryEntries, thread.Summary.Title), PopoverCurrent);
                else if (EditArchiveMediaTagsAsync != null) await EditArchiveMediaTagsAsync(tagEntry);
            },
            [MediaMenuAction.Queue] = async () =>
            {
                if (target != null && AddToTagQueue is { } add) { string message = await add(new[] { target }); if (!_ended) Message(message); }
            },
            [MediaMenuAction.Export] = async () =>
            {
                if (target != null) await OpenOriginalExportAsync(new[] { FavoriteExportAsset.From(threadId, target.Manifest, target.Asset) }, origin);
            },
            [MediaMenuAction.ShowInFolder] = () =>
            {
                if (target != null && Path.GetDirectoryName(target.Asset.Path) is string directory) _openFolder?.Invoke(directory);
                return Task.CompletedTask;
            },
            [MediaMenuAction.CopyPath] = async () =>
            {
                if (target != null && CopyFilePathAsync != null) await CopyFilePathAsync(target.Asset.Path);
            },
            [MediaMenuAction.Details] = () =>
            {
                string details = target == null ? attachment?.Label + Environment.NewLine + MediaMenuReasons.NotSaved
                    : (target.Asset.OriginalFileName ?? target.Asset.Path) + Environment.NewLine + target.Asset.Path + Environment.NewLine
                        + MediaProvenanceText.LocalSize(target.Asset.Length) + Environment.NewLine + "SHA-256: " + target.Asset.Sha256
                        + Environment.NewLine + (severalPosts ? string.Empty : MediaProvenanceText.Details(target.AttachmentResolution?.Binding) + Environment.NewLine)
                        + threadId.Source + " /" + threadId.Board + "/ " + threadId.Thread
                        + Environment.NewLine + (severalPosts ? Texts.T("media.menu.details.posts", string.Join(", ", postIds))
                            : target.Post?.PostId ?? Texts.T("gallery.noPost"));
                return ShowArchiveMetadataAsync(CommandCatalog.Label("media.details"), details);
            },
            [MediaMenuAction.ShowInThread] = async () =>
            {
                if (target?.Post == null || severalPosts) return;
                if (_thread?.Summary.Id != threadId) await LoadThreadAsync(threadId);
                ArchiveContentTabs.SelectedIndex = 0;
                await FocusPostAsync(target.Post.PostId);
            }
        };
        List<Control> context = new();
        if (postId != null && EditPostBookmark != null)
            context.Add(CreatePostBookmarkMenuItem(new MeowChan.Persistence.PostBookmarkKey(threadId, postId), origin));
        if (selection is { Length: > 1 })
        {
            // The common section above acts on the file the menu opened on (and "open in gallery" starts at it); everything below
            // acts on the whole selection, frozen now, exactly like the media page.
            context.Add(MediaMenuBuilder.Note(Texts.T("media.menu.note.selection", selection.Length)));
            string unavailable = Texts.T("media.selection.reason.unavailable");
            context.Add(MediaMenuBuilder.ContextItem(session, "media.selection.tag", () => TagMediaSelectionAsync(thread, selection, entries, origin),
                () => MediaTagServices != null && !_mediaSelectionBusy, Run, unavailable));
            context.Add(MediaMenuBuilder.ContextItem(session, "media.selection.queue", () => AddMediaSelectionToQueueAsync(thread, selection, entries),
                () => AddToTagQueue != null && !_mediaSelectionBusy, Run, unavailable));
            context.Add(MediaMenuBuilder.ContextItem(session, "media.selection.gallery", () => OpenMediaSelectionGalleryAsync(thread, selection, entries, origin),
                () => OpenMediaGallery != null, Run, unavailable));
            context.Add(MediaMenuBuilder.ContextItem(session, "media.selection.export", () => ExportMediaSelectionAsync(thread, selection, origin),
                () => true, Run));
            context.Add(MediaMenuBuilder.ContextItem(session, "media.selection.clear", () => { ClearMediaSelection(); return Task.CompletedTask; },
                () => true, Run));
        }
        ContextMenu menu = MediaMenuBuilder.Build(session, model, handlers, Run, context, text => Message(text));
        return (menu, session);
    }

    // The archive's own error reporting: a late failure is shown on the thread it belongs to, never after the page ended.
    private async Task RunArchiveMenuActionAsync(Func<Task> action, MediaMenuSession session, Action<string> reportError)
    {
        try { if (!IsFilenameSearchOpen && session.IsCurrent) await action(); }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception error) { reportError(error.Message); }
    }
}
