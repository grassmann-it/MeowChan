using System;
using System.Collections.Generic;
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

    internal ContextMenu? MediaMenu => _mediaMenuPresenter.Menu;
    internal MediaMenuSession? MediaMenuSession => _mediaMenuPresenter.Session;
    /// <summary>The post address of a downloaded provider medium, or null when it has none; supplied by the shell.</summary>
    internal Func<GalleryEntry, Uri?>? ProviderPostUrl { get; set; }
    internal event Action<GalleryEntry>? ProviderPostOpenRequested;
    internal event Action<GalleryEntry>? ProviderPostCopyRequested;
    /// <summary>The summary of actions the menu could not offer, shown by the shell when the menu's summary item is chosen.</summary>
    internal event Action<string>? MenuExplanationRequested;

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
            RatingWired = RatingEditRequested != null,
            RatingKnown = _ratingButton.IsEnabled,
            TagsWired = TagsRequested != null,
            TagsBlocked = _tagNames == null ? _tagsUnavailableReason ?? MediaMenuReasons.TagsUnknown
                : CanRequestFavorite ? null : MediaMenuReasons.Unavailable,
            ExportWired = ExportOriginalRequested != null,
            FolderWired = ShowInFolderRequested != null,
            DetailsWired = DetailsRequested != null,
            Posts = entry.Post != null ? MediaMenuPostScope.Single : MediaMenuPostScope.None,
            ShowInThreadWired = ReadPostRequested != null,
            ProviderMedia = provider,
            ProviderPostKnown = provider && ProviderPostUrl?.Invoke(entry) != null,
            ProviderWired = ProviderPostOpenRequested != null
        };
    }

    private void ShowMediaMenu()
    {
        HoldSlideshowAdvance();
        RevealOverlayControls();
        if (_entry == null) return;
        GalleryEntry entry = _entry;
        long navigation = _navigationId;
        MediaMenuModel model = MediaMenuComposition.Compose(MediaMenuFactsFor(entry));
        MediaMenuSession session = new(model.Surface, model.Target, () => !_terminalRetirement && navigation == _navigationId
            && _entry is { } current && MediaMenuTarget.Of(current) == model.Target);
        Dictionary<MediaMenuAction, Func<Task>> handlers = new()
        {
            [MediaMenuAction.Favorite] = Sync(() => FavoriteToggleRequested?.Invoke(entry)),
            [MediaMenuAction.Rating] = Sync(() => RatingEditRequested?.Invoke(entry)),
            [MediaMenuAction.Tags] = Sync(() => { PauseSlideshow(); TagsRequested?.Invoke(entry); }),
            [MediaMenuAction.Export] = Sync(() => ExportOriginalRequested?.Invoke(entry)),
            [MediaMenuAction.ShowInFolder] = Sync(() => ShowInFolderRequested?.Invoke(entry)),
            [MediaMenuAction.Details] = Sync(() => DetailsRequested?.Invoke(entry)),
            [MediaMenuAction.ShowInThread] = Sync(() => { PauseSlideshow(); ReadPostRequested?.Invoke(entry); }),
            [MediaMenuAction.OpenProviderPost] = Sync(() => ProviderPostOpenRequested?.Invoke(entry)),
            [MediaMenuAction.CopyProviderPost] = Sync(() => ProviderPostCopyRequested?.Invoke(entry))
        };
        ContextMenu menu = MediaMenuBuilder.Build(session, model, handlers, action => action(), null,
            text => MenuExplanationRequested?.Invoke(text));
        Control owner = _nativeOverlayActive ? _nativeVideoOverlay : _viewport;
        menu.Opened += (_, _) => RefreshPlaybackControls();
        menu.Closed += (_, _) => RefreshPlaybackControls();
        _mediaMenuPresenter.Show(menu, session, owner, attachToOrigin: true, fallbackFocus: FocusGallerySurface);
    }

    private static Func<Task> Sync(Action action) => () => { action(); return Task.CompletedTask; };
}
