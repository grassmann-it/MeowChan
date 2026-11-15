using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using MeowChan.Archive;
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
    /// Opens the "Rate and tag" popover (the shell owns the tag session and the favourite and rating writes) for one file: the exact entry, the
    /// tile or attachment it opened on, the context of the suggestions, the page guard and how it was asked for (stars focused, why it cannot be tagged). Completes when the popover closed. Null opens the tag dialog of
    /// <see cref="EditArchiveMediaTagsAsync"/> instead.
    /// </summary>
    internal Func<GalleryEntry, Control, TagContext, Func<bool>, RateAndTagRequest, Task>? TagPopoverAsync { get; set; }
    /// <summary>
    /// The favourite and rating row of a "Rate and tag" popover over these files (one decision session the popover disposes); the shell supplies it. Null
    /// leaves the selection popover without the row.
    /// </summary>
    internal Func<IReadOnlyList<GalleryEntry>, bool, QuickTagPopoverDecision?>? DecisionFor { get; set; }
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
        _mediaSidebar?.RefreshFavorite();
    }

    private void InitializeArchiveMediaCommands()
    {
        AddHandler(ContextRequestedEvent, OnArchiveMediaContextRequested, RoutingStrategies.Tunnel);
        AddHandler(KeyDownEvent, OnArchiveMediaContextKey, RoutingStrategies.Tunnel);
        AddHandler(ArchiveMediaTileView.RateAndTagRequestedEvent, OnTileRateAndTagRequested);
    }

    // The rating caption of a tile (dead-affordance audit 15.11.) opens "Rate and tag" on that tile.
    private void OnTileRateAndTagRequested(object? sender, RoutedEventArgs e)
    {
        if (_ended || !_available || _thread == null || e.Source is not ArchiveMediaTileView { DataContext: ArchiveAssetRow row } tile) return;
        e.Handled = true;
        ArchiveMediaSubject subject = ResolveArchiveMediaSubject(row, null, null);
        ArchiveAssetRow[]? selection = SeveralSelectedWith(row, null);
        Action<string> report = CaptureActionErrorReporter();
        OpenActions.Start(OpenAsync, "archive.media.rateAndTag");
        async Task OpenAsync()
        {
            try { await OpenRateAndTagAsync(subject, tile, selection, focusRating: true); }
            catch (Exception error) { report(error.Message); }
        }
    }

    // The Menu key and Shift+F10 open the same media menu as a right-click, for the focused row or attachment.
    private void OnArchiveMediaContextKey(object? sender, KeyEventArgs e)
    {
        _sidebarPointerContextPending = false; // a key ends a pointer gesture whose release never came
        if (_ended || !_available || IsReaderDialogOpen || e.Source is not Control source || !MediaMenuPresenter.IsContextKey(e)) return;
        Control[] path = source.GetSelfAndVisualAncestors().OfType<Control>().ToArray();
        if (!path.Any(control => control is ArchiveAttachmentView || control.Name is "ArchiveMediaGrid" or "ArchiveMediaTiles")
            && SidebarMediaContext(path) == null && SidebarStripContext(path) == null) return;
        ContextRequestedEventArgs request = new() { RoutedEvent = ContextRequestedEvent };
        source.RaiseEvent(request);
        e.Handled = request.Handled;
    }

    // The sidebar's right click, anywhere in it (picture, video row, header line, the card's edge; a thumbnail of the strip keeps its own request, which
    // selects it first): the press itself opens the menu (nothing the player does with the pointer can swallow it), and the ContextRequested that Avalonia
    // sends while it processes the release of that click is consumed once. The flag lives from the press until that release is through: Avalonia 12 sends the
    // request on the release (a click lasts far longer than a Background tick), so a flag that was dropped right after the press let the release re-open the menu
    // in place of the first one.
    private bool _sidebarPointerContextPending;

    private void OnSidebarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _sidebarPointerContextPending = false; // each press starts its own gesture
        if (e.Handled || _ended || !_available || !_mediaSidebarShown || _mediaSidebar is not { } sidebar || e.Source is not Control source
            || !e.GetCurrentPoint(sidebar).Properties.IsRightButtonPressed || MediaMenuPresenter.IsNativeTextSource(source)) return;
        Control[] ancestors = source.GetSelfAndVisualAncestors().OfType<Control>().ToArray();
        if (ancestors.Any(control => ReferenceEquals(control, sidebar.Strip))) return;
        ContextRequestedEventArgs request = new(e) { RoutedEvent = ContextRequestedEvent };
        source.RaiseEvent(request);
        if (!request.Handled) return;
        e.Handled = true;
        _sidebarPointerContextPending = true;
    }

    // The release of that right click: the request Avalonia sends with it comes through the handlers after this one, so the flag ends on the next tick.
    private void OnSidebarPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_sidebarPointerContextPending || e.InitialPressMouseButton != MouseButton.Right) return;
        Avalonia.Threading.Dispatcher.UIThread.Post(() => _sidebarPointerContextPending = false, Avalonia.Threading.DispatcherPriority.Background);
    }

    private void OnArchiveMediaContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (e.Handled || _ended || !_available || e.Source is not Control source) return;
        // Text inputs and selectable text keep their own native menu.
        if (MediaMenuPresenter.IsNativeTextSource(source)) return;
        Control[] ancestors = source.GetSelfAndVisualAncestors().OfType<Control>().ToArray();
        if (_sidebarPointerContextPending && _mediaSidebar != null && ancestors.Any(control => ReferenceEquals(control, _mediaSidebar)))
        {
            _sidebarPointerContextPending = false; // the menu opened with the press
            e.Handled = true;
            return;
        }
        ArchiveAttachmentView? attachmentView = ancestors.OfType<ArchiveAttachmentView>().FirstOrDefault();
        ArchiveAttachmentRow? attachment = attachmentView?.DataContext as ArchiveAttachmentRow;
        ArchiveAssetRow? asset = ancestors.Select(control => control.DataContext).OfType<ArchiveAssetRow>().FirstOrDefault();
        Control? origin = attachmentView;
        if (asset != null) origin = ancestors.First(control => control.DataContext is ArchiveAssetRow);
        bool keyboard = !e.TryGetPosition(source, out _);
        if (asset == null && attachment == null && keyboard)
        {
            Control? list = ancestors.FirstOrDefault(control => control.Name is "ArchiveMediaGrid" or "ArchiveMediaTiles");
            // No pointer: the menu is for the focused file of the list (the Menu key or Shift+F10), which may be one of several selected.
            asset = list == null ? null : SelectedMediaRow();
            origin = list;
        }
        string? postId = ancestors.Select(control => control.DataContext).OfType<ArchivePostRow>().FirstOrDefault()?.Id;
        // The medium of the media sidebar: the same menu as the attachment it shows in its post (right-click, Menu key, Shift+F10).
        ReaderInlineVideoSlot? playback = attachmentView?.InlineVideoForKeys();
        if (attachment == null && asset == null && SidebarMediaContext(ancestors, keyboard) is { } shown)
        {
            attachment = shown.Attachment; postId = shown.PostId; origin = shown.Origin; playback = _sidebarVideo;
        }
        // A thumbnail of the sidebar's strip: the menu of that file, not of the one shown (right-click, Menu key, Shift+F10).
        else if (attachment == null && asset == null && SidebarStripContext(ancestors) is { } thumbnail)
        {
            attachment = thumbnail.Attachment; postId = thumbnail.PostId; origin = thumbnail.Origin; playback = thumbnail.Shown ? _sidebarVideo : null;
        }
        if (attachment != null) asset = _selectedThread == null || postId == null ? null : FindMatchingAsset(_selectedThread, postId, attachment.Attachment, _assets);
        if (asset == null && attachment == null) return;
        if (_selectedThread == null || _thread == null || origin == null) return;
        e.Handled = true;
        (ContextMenu menu, MediaMenuSession session) = BuildArchiveMediaMenu(asset, attachment, origin, postId, ArchiveMenuSurface(ancestors, attachment),
            SeveralSelectedWith(asset, attachment), playback);
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
        // Ctrl+Shift+T: the same combined popover as "Rate and tag…", with the tag field focused instead of the stars.
        if (CommandBindings.Matches(e, "media.tags.edit"))
        {
            e.Handled = true;
            ArchiveMediaSubject subject = ResolveArchiveMediaSubject(asset, attachment, postId);
            Action<string> reportTagError = CaptureActionErrorReporter();
            _ = RunTagShortcutAsync();
            return true;
            async Task RunTagShortcutAsync()
            {
                try { await OpenRateAndTagAsync(subject, origin, SeveralSelectedWith(asset, attachment), focusRating: false, attachment?.Label); }
                catch (Exception error) { reportTagError(error.Message); }
            }
        }
        MediaMenuSurface surface = attachment != null ? MediaMenuSurface.ArchiveInline
            : owner.Name == "ArchiveMediaTiles" ? MediaMenuSurface.ArchiveTiles : MediaMenuSurface.ArchiveTable;
        return CommandBindings.RouteMenuAction(BuildArchiveMediaMenu(asset, attachment, origin, postId, surface, SeveralSelectedWith(asset, attachment)).Menu, e);
    }

    // The sidebar stands for the medium it shows: its attachment, post and menu origin. A pointer right click anywhere in the sidebar counts (picture, video row,
    // header line and its buttons, the edge around the card); a thumbnail of the strip stands for its own file (SidebarStripContext). With the keyboard (the
    // Menu key, Shift+F10) any focus inside the sidebar stands for it, its strip included.
    private (ArchiveAttachmentRow Attachment, string PostId, Control Origin)? SidebarMediaContext(Control[] ancestors, bool keyboard = true)
    {
        if (_mediaSidebar == null || !_mediaSidebarShown || !ancestors.Any(control => ReferenceEquals(control, _mediaSidebar))
            || !keyboard && ancestors.Any(control => ReferenceEquals(control, _mediaSidebar.Strip))) return null;
        return ArchivePosts.SelectedItem is ArchivePostRow post && CurrentSidebarAttachment() is { } current
            ? (current, post.Id, _mediaSidebar.FrameArea) : null;
    }

    // A thumbnail of the sidebar's strip stands for its own file of the focused post: its attachment, post and the thumbnail as menu origin. From the keyboard the
    // strip itself is the source, and the menu is the focused (the selected, i.e. shown) thumbnail's. Shown tells whether that file is the one the sidebar shows.
    private (ArchiveAttachmentRow Attachment, string PostId, Control Origin, bool Shown)? SidebarStripContext(Control[] ancestors)
    {
        if (_mediaSidebar == null || !_mediaSidebarShown || !ancestors.Any(control => ReferenceEquals(control, _mediaSidebar.Strip))
            || ArchivePosts.SelectedItem is not ArchivePostRow { ShowsMedia: true } post) return null;
        ListBoxItem? item = ancestors.OfType<ListBoxItem>().FirstOrDefault(candidate => candidate.Tag is int)
            ?? _mediaSidebar.Strip.SelectedItem as ListBoxItem;
        if (item?.Tag is not int index || index < 0 || index >= post.Attachments.Count) return null;
        return (post.Attachments[index], post.Id, item, index == ReaderMediaSidebarRules.ClampIndex(_sidebarIndex, post.Attachments.Count));
    }

    // The playback group of a video that plays in the reader: Play / Pause, Mute / Unmute, Repeat on / off, with the bindings' shortcuts.
    private static List<Control>? PlaybackMenuItems(ReaderInlineVideoSlot? slot, MediaMenuSession session, Func<Func<Task>, Task> run) =>
        ReaderPlaybackMenu.MenuItems(slot, session, run);

    /// <summary>What a media menu, R and the "Rate and tag" button freeze about the file they open on.</summary>
    private sealed record ArchiveMediaSubject(ArchivedThreadView Thread, ResourceId ThreadId, GalleryEntry[] GalleryEntries, GalleryEntry[] Entries,
        GalleryEntry? Target, GalleryEntry? GalleryTarget, GalleryEntry? TagEntry, bool AmbiguousTag, string[] PostIds, GalleryMediaTagUnavailable TagReason)
    {
        /// <summary>Null when the file can be tagged; otherwise the reason (a shared file is never tagged under one of its posts).</summary>
        internal string? TagsBlocked => TagEntry != null ? null : MediaMenuReasons.Tags(TagReason, ambiguous: AmbiguousTag);
    }

    private ArchiveMediaSubject ResolveArchiveMediaSubject(ArchiveAssetRow? asset, ArchiveAttachmentRow? attachment, string? postId)
    {
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
        if (entry == null && asset != null)
            entry = new GalleryEntry(new GalleryAssetKey(threadId, asset.Manifest.Directory, asset.Asset.AssetId), asset.Manifest, asset.Asset,
                null, null, !asset.Asset.IsCurrent, true) { ThreadUrl = thread.Summary.Url };
        GalleryEntry? target = entry;
        // Keep the chosen post/position and its provenance when the visible sequence shares the same bytes.
        if (attachment != null && target != null)
            galleryEntries = galleryEntries.Select(item => item.Key == target.Key ? target : item).ToArray();
        GalleryEntry? galleryTarget = target == null ? null : galleryEntries.FirstOrDefault(item => item.EntryKey == target.EntryKey);
        MediaTagSubjects.Of(target, out GalleryMediaTagUnavailable tagReason);
        return new ArchiveMediaSubject(thread, threadId, galleryEntries, entries, target, galleryTarget, tagEntry, ambiguousTagTarget, postIds, tagReason);
    }

    /// <summary>
    /// "Rate and tag…" (menu entry, R, the toolbar button, the tile's rating caption and Ctrl+Shift+T): the quick tag popover on <paramref name="origin"/>
    /// with the favourite and stars row on top, then the tag field. Several selected files (the file it opened on is one of them) are rated and tagged
    /// together. A file that cannot be tagged gets the stars and the reason as text. <paramref name="focusRating"/>: the stars take the focus (R and the
    /// menu entry); otherwise the field does (the tag shortcut).
    /// </summary>
    private async Task OpenRateAndTagAsync(ArchiveMediaSubject subject, Control origin, ArchiveAssetRow[]? selection, bool focusRating, string? label = null)
    {
        var thread = subject.Thread;
        ResourceId threadId = subject.ThreadId;
        bool PopoverCurrent() => !_ended && _available && ReferenceEquals(_thread, thread) && _selectedThread == threadId;
        if (!PopoverCurrent()) return;
        if (selection is { Length: > 1 })
        {
            await TagMediaSelectionAsync(thread, selection, subject.Entries, origin, focusRating);
            return;
        }
        GalleryEntry? target = subject.Target;
        if (target == null)
        {
            QuickTagPopover.OpenBlocked(origin, label ?? string.Empty, subject.TagsBlocked ?? MediaMenuReasons.Unavailable,
                new QuickTagPopoverOptions { IsCurrent = PopoverCurrent });
            return;
        }
        if (TagPopoverAsync != null)
            await TagPopoverAsync(subject.TagEntry ?? target, origin, QuickTagPopoverContext.ContextOf(subject.TagEntry ?? target, subject.GalleryEntries, thread.Summary.Title),
                PopoverCurrent, new RateAndTagRequest(focusRating, subject.TagsBlocked));
        else if (subject.TagEntry != null && EditArchiveMediaTagsAsync != null) await EditArchiveMediaTagsAsync(subject.TagEntry);
        else QuickTagPopover.OpenBlocked(origin, target.Asset.OriginalFileName ?? string.Empty, subject.TagsBlocked ?? MediaMenuReasons.Unavailable,
            new QuickTagPopoverOptions { IsCurrent = PopoverCurrent });
    }

    private (ArchiveAssetRow? Asset, ArchiveAttachmentRow? Attachment, string? PostId, Control Origin)? FocusedMediaForRateAndTag()
    {
        if (_ended || !_available || _thread == null || _selectedThread == null) return null;
        Control pane = ArchiveReaderPane;
        if (TopLevel.GetTopLevel(pane)?.FocusManager?.GetFocusedElement() is not Control focus) return null;
        Control[] parents = focus.GetSelfAndVisualAncestors().OfType<Control>().ToArray();
        if (!parents.Contains(pane)) return null;
        ArchiveAttachmentView? attachmentView = parents.OfType<ArchiveAttachmentView>().FirstOrDefault();
        ArchiveAttachmentRow? attachment = attachmentView?.DataContext as ArchiveAttachmentRow;
        ArchiveAssetRow? asset = parents.Select(control => control.DataContext).OfType<ArchiveAssetRow>().FirstOrDefault();
        string? postId = parents.Select(control => control.DataContext).OfType<ArchivePostRow>().FirstOrDefault()?.Id;
        Control? origin = attachmentView;
        if (asset != null) origin = parents.First(control => control.DataContext is ArchiveAssetRow);
        if (attachment != null) asset = postId == null ? null : FindMatchingAsset(_selectedThread, postId, attachment.Attachment, _assets);
        if (asset == null && attachment == null && parents.FirstOrDefault(control => control.Name is "ArchiveMediaGrid" or "ArchiveMediaTiles") is { } list)
        {
            asset = SelectedMediaRow();
            origin = list;
        }
        else if (asset == null && attachment == null && SidebarMediaContext(parents) is { } shown)
        {
            attachment = shown.Attachment; postId = shown.PostId; origin = shown.Origin;
            asset = FindMatchingAsset(_selectedThread, postId, attachment.Attachment, _assets);
        }
        if (asset == null || origin == null) return null;
        return (asset, attachment, postId, origin);
    }

    /// <summary>
    /// R, and the toolbar's "Rate and tag" button: the popover for the focused file of the reader or the media list (its origin is the focused control; the
    /// toolbar's own button, or a control the toolbar reduced under "More", gives way to <see cref="RatingMenuAnchor"/>).
    /// </summary>
    internal async Task OpenRateAndTagForFocusAsync(bool focusRating, Control? buttonAnchor = null)
    {
        if (FocusedMediaForRateAndTag() is not { } focused)
        {
            // The toolbar button took the focus: the file is the list's selection.
            if (buttonAnchor == null || _thread == null || _selectedThread == null || SelectedMediaRow() is not { } row) return;
            ArchiveMediaSubject chosen = ResolveArchiveMediaSubject(row, null, null);
            Action<string> report = CaptureActionErrorReporter();
            try { await OpenRateAndTagAsync(chosen, RatingMenuAnchor(buttonAnchor), SeveralSelectedWith(row, null), focusRating); }
            catch (Exception error) { report(error.Message); }
            return;
        }
        ArchiveMediaSubject subject = ResolveArchiveMediaSubject(focused.Asset, focused.Attachment, focused.PostId);
        Control origin = focused.Origin;
        if (buttonAnchor != null && ReferenceEquals(origin, buttonAnchor)) origin = RatingMenuAnchor(buttonAnchor);
        Action<string> reportError = CaptureActionErrorReporter();
        try { await OpenRateAndTagAsync(subject, origin, SeveralSelectedWith(focused.Asset, focused.Attachment), focusRating); }
        catch (Exception error) { reportError(error.Message); }
    }

    private (ContextMenu Menu, MediaMenuSession Session) BuildArchiveMediaMenu(ArchiveAssetRow? asset, ArchiveAttachmentRow? attachment,
        Control origin, string? postId, MediaMenuSurface surface, ArchiveAssetRow[]? selection = null, ReaderInlineVideoSlot? playback = null)
    {
        Action<string> reportError = CaptureActionErrorReporter();
        // Freeze the current manifest references and post association together. Neither
        // a later page refresh nor a different lead may retarget an open media menu.
        ArchiveMediaSubject subject = ResolveArchiveMediaSubject(asset, attachment, postId);
        var thread = subject.Thread;
        ResourceId threadId = subject.ThreadId;
        GalleryEntry[] galleryEntries = subject.GalleryEntries;
        GalleryEntry[] entries = subject.Entries;
        GalleryEntry? tagEntry = subject.TagEntry;
        bool ambiguousTagTarget = subject.AmbiguousTag;
        string[] postIds = subject.PostIds;
        bool severalPosts = postIds.Length > 1;
        GalleryEntry? target = subject.Target;
        GalleryEntry? galleryTarget = subject.GalleryTarget;
        GalleryMediaTagUnavailable tagReason = subject.TagReason;
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
            RatingWired = SetMediaRatingAsync != null && target != null && CanChangeMediaRating?.Invoke(FavoriteAssetKey.From(threadId, target.Asset)) != false,
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
            // "Rate and tag…": the popover on the tile or attachment the menu opened on (frozen with the menu), the stars focused.
            [MediaMenuAction.RateAndTag] = () => OpenRateAndTagAsync(subject, origin, selection, focusRating: true, attachment?.Label),
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
                TryShowMediaInFolder(target?.Asset);
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
            // The selection entries take the icons of the commands that do the same for one file (the menu's icon column stays filled).
            static string Icon(string commandId) => CommandCatalog.Get(commandId).Icon;
            context.Add(MediaMenuBuilder.ContextItem(session, "media.selection.queue", () => AddMediaSelectionToQueueAsync(thread, selection, entries),
                () => AddToTagQueue != null && !_mediaSelectionBusy, Run, unavailable, Icon("media.queue.add")));
            context.Add(MediaMenuBuilder.ContextItem(session, "media.selection.gallery", () => OpenMediaSelectionGalleryAsync(thread, selection, entries, origin),
                () => OpenMediaGallery != null, Run, unavailable, Icon("gallery.open")));
            context.Add(MediaMenuBuilder.ContextItem(session, "media.selection.export", () => ExportMediaSelectionAsync(thread, selection, origin),
                () => true, Run, icon: Icon("media.export-original")));
            context.Add(MediaMenuBuilder.ContextItem(session, "media.selection.clear", () => { ClearMediaSelection(); return Task.CompletedTask; },
                () => true, Run, icon: "x"));
        }
        ContextMenu menu = MediaMenuBuilder.Build(session, model, handlers, Run, context, text => Message(text), PlaybackMenuItems(playback, session, Run));
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
