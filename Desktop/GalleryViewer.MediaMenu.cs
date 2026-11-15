using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using MeowChan.Desktop.MediaActions;
using MeowChan.Gallery;

namespace MeowChan.Desktop;

// The gallery's media menu: the same composed common actions as the archive and the media page (including the media
// tags), opened on the shown medium for the viewport, the native video overlay and the keyboard's Menu key / Shift+F10.
// The target is frozen when the menu opens; navigating to another medium invalidates the menu.
internal sealed partial class GalleryViewer
{
    private readonly MediaMenuPresenter _mediaMenuPresenter = new();

    /// <summary>The medium shown now; the menu is opened for exactly this one.</summary>
    internal GalleryEntry? CurrentEntry => _entry;
    internal ContextMenu? MediaMenu => _mediaMenuPresenter.Menu;
    internal MediaMenuSession? MediaMenuSession => _mediaMenuPresenter.Session;
    /// <summary>The post address of a downloaded provider medium, or null when it has none; supplied by the shell.</summary>
    internal Func<GalleryEntry, Uri?>? ProviderPostUrl { get; set; }
    internal event Action<GalleryEntry>? ProviderPostOpenRequested;
    internal event Action<GalleryEntry>? ProviderPostCopyRequested;
    /// <summary>The summary of actions the menu could not offer, shown by the shell when the menu's summary item is chosen.</summary>
    internal event Action<string>? MenuExplanationRequested;
    /// <summary>The post bookmark entry of the menu (and its key): the shell opens the add / edit dialog for the post the medium belongs to.</summary>
    internal event Action<GalleryEntry>? PostBookmarkRequested;

    private void OnMediaContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (e.Handled || _entry == null || MediaMenuPresenter.IsNativeTextSource(e.Source)) return;
        ShowMediaMenu();
        e.Handled = true;
    }

    // Menu key and Shift+F10 carry no pointer position and come from the focused control, which may be the viewer itself.
    private void OnKeyboardContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (e.Handled || _entry == null || e.TryGetPosition(this, out _) || MediaMenuPresenter.IsNativeTextSource(e.Source)) return;
        ShowMediaMenu();
        e.Handled = true;
    }

    internal void InvalidateMediaMenu() => _mediaMenuPresenter.Invalidate();

    internal MediaMenuFacts MediaMenuFactsFor(GalleryEntry entry)
    {
        bool provider = BooruMediaIdentity.IsBooruThread(entry.Key.Thread);
        return new MediaMenuFacts(_nativeOverlayActive ? MediaMenuSurface.NativeVideoOverlay : MediaMenuSurface.Gallery,
            MediaMenuTarget.Of(entry))
        {
            Present = true,
            Favorite = _favoriteSelected ? MediaMenuFavorite.On : MediaMenuFavorite.Off,
            FavoriteWired = FavoriteToggleRequested != null && !_terminalRetirement,
            RatingWired = RatingSetRequested != null,
            RatingKnown = _ratingAvailable,
            Rating = _ratingValue,
            TagsWired = TagsRequested != null,
            TagsBlocked = _tagNames == null ? _tagsUnavailableReason ?? MediaMenuReasons.TagsUnknown
                : CanRequestFavorite ? null : MediaMenuReasons.Unavailable,
            ExportWired = ExportOriginalRequested != null,
            QueueWired = QueueRequested != null,
            QueueBlocked = TagQueueActions.BlockedReason(true, provider),
            FolderWired = ShowInFolderRequested != null,
            CopyPathWired = CopyPathRequested != null,
            DetailsWired = DetailsRequested != null,
            Posts = entry.Post != null ? MediaMenuPostScope.Single : MediaMenuPostScope.None,
            ShowInThreadWired = ReadPostRequested != null,
            ProviderMedia = provider,
            ProviderPostKnown = provider && ProviderPostUrl?.Invoke(entry) != null,
            ProviderWired = ProviderPostOpenRequested != null
        };
    }

    /// <summary>
    /// Opens the media menu. Without <paramref name="tile"/> it is the shown medium's; with one (a right-click on a filmstrip tile) it is that tile's: the
    /// same composed actions on its entry, without the three that read or change the shown medium's own state (favorite, rating, tags), and without a
    /// playback group. It stays valid until the next navigation, like the shown medium's.
    /// </summary>
    private void ShowMediaMenu(GalleryEntry? tile = null)
    {
        HoldSlideshowAdvance();
        RevealOverlayControls();
        if ((tile ?? _entry) is not { } entry) return;
        bool shown = tile == null;
        long navigation = _navigationId;
        MediaMenuModel model = MediaMenuComposition.Compose(MediaMenuFactsFor(entry));
        if (!shown) model = model with { Items = model.Items.Where(item => item.Action is not (MediaMenuAction.Favorite or MediaMenuAction.Rating or MediaMenuAction.Tags)).ToArray() };
        MediaMenuSession session = new(model.Surface, model.Target, () => !_terminalRetirement && navigation == _navigationId
            && (!shown || _entry is { } current && MediaMenuTarget.Of(current) == model.Target));
        Dictionary<MediaMenuAction, Func<Task>> handlers = new()
        {
            [MediaMenuAction.Favorite] = Sync(() => FavoriteToggleRequested?.Invoke(entry)),
            [MediaMenuAction.Tags] = Sync(() => { PauseSlideshow(); TagsRequested?.Invoke(entry); }),
            [MediaMenuAction.Queue] = Sync(() => QueueRequested?.Invoke(entry)),
            [MediaMenuAction.Export] = Sync(() => ExportOriginalRequested?.Invoke(entry)),
            [MediaMenuAction.ShowInFolder] = Sync(() => ShowInFolderRequested?.Invoke(entry)),
            [MediaMenuAction.CopyPath] = Sync(() => CopyPathRequested?.Invoke(entry)),
            [MediaMenuAction.Details] = Sync(() => DetailsRequested?.Invoke(entry)),
            [MediaMenuAction.ShowInThread] = Sync(() => { PauseSlideshow(); ReadPostRequested?.Invoke(entry); }),
            [MediaMenuAction.OpenProviderPost] = Sync(() => ProviderPostOpenRequested?.Invoke(entry)),
            [MediaMenuAction.CopyProviderPost] = Sync(() => ProviderPostCopyRequested?.Invoke(entry))
        };
        Func<Func<Task>, Task> run = action => action();
        // The same groups as the reader's menu: the playback group of a shown video on top, the post bookmark entry below the common section.
        GalleryVideoPlayer? video = shown && _entry?.Kind == SavedMediaKind.Video ? _video : null;
        IReadOnlyList<Control>? playback = video == null ? null : MediaMenuBuilder.PlaybackGroup(session,
            ReaderPlaybackMenu.GalleryExtras.Playback(true, CanUseVideoTransport, video.IsPlaybackRequested, video.Muted, video.LoopEnabled,
                video.HasAudio, video.CanToggleLoopNow),
            action => ExecuteVideoMenuAction(video, action), run);
        List<Control>? context = null;
        if (ReaderPlaybackMenu.GalleryExtras.OffersPostBookmark(entry.Post != null))
            context = new() { MediaMenuBuilder.PostBookmarkItem(session, Sync(() => RequestPostBookmark(entry)),
                () => !_terminalRetirement && PostBookmarkRequested != null, run) };
        // The "Rating" submenu rates the frozen entry (the medium the menu opened on), never the marked media of the tag panel.
        ContextMenu menu = MediaMenuBuilder.Build(session, model, handlers, run, context,
            text => MenuExplanationRequested?.Invoke(text), playback, value => Sync(() => RatingSetRequested?.Invoke(entry, value))());
        Control owner = !shown ? _filmstrip : _nativeOverlayActive ? _nativeVideoOverlay : _viewport;
        menu.Opened += (_, _) => RefreshPlaybackControls();
        menu.Closed += (_, _) => RefreshPlaybackControls();
        _mediaMenuPresenter.Show(menu, session, owner, attachToOrigin: true, fallbackFocus: FocusGallerySurface);
    }

    private void RequestPostBookmark(GalleryEntry entry)
    {
        if (entry.Post == null) return;
        PauseSlideshow();
        PostBookmarkRequested?.Invoke(entry);
    }

    // The playback group acts on the video that was shown when the menu opened, through the gallery's own commands (the same as its keys).
    private Task ExecuteVideoMenuAction(GalleryVideoPlayer video, ReaderVideoAction action)
    {
        if (!ReferenceEquals(video, _video) || !CanUseVideoTransport) return Task.CompletedTask;
        switch (action)
        {
            case ReaderVideoAction.PlayPause: return video.TogglePlaybackAsync();
            case ReaderVideoAction.Mute: return video.ToggleMuteAsync();
            case ReaderVideoAction.Loop: ToggleVideoLoopByKey(); break;
        }
        return Task.CompletedTask;
    }

    private static Func<Task> Sync(Action action) => () => { action(); return Task.CompletedTask; };
}
