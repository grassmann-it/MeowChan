using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.MediaActions;
using MeowChan.Favorites;
using MeowChan.Gallery;
using MeowChan.Tags;

namespace MeowChan;

// Own media tags from the gallery. The target (bound version or loose local file) is resolved once from the
// exact entry by the shared resolver and frozen in the session; gallery navigation while the panel or the dialog is
// open cannot redirect a change: the panel gets a new frozen target per shown medium.
// "Tags" opens the quick tag panel in the gallery (GalleryViewer.TagPanel.cs); the full dialog stays behind "Advanced…".
public partial class MainWindow
{
    private bool _galleryTagDialogOpen;
    private GalleryTagPanelOwner? _galleryTagPanelOwner;
    // Tags used in this window's session, for the "recently used" suggestions of the panel (kept while the panel closes and reopens).
    private TagRecentList? _quickTagRecent;
    private TagRecentList QuickTagRecent => _quickTagRecent ??= QuickTagSettings.NewRecentList(_config);
    // The one stamp set of this profile (PT5): the gallery panel and every tag popover share it, and each change is saved at once.
    // The general/ungrouped stamp stays in Config.TagStamp. A named queue walk supplies its own live stamp from tag-queue.json.
    private QuickTagStamp? _quickTagStamp;
    internal QuickTagStamp QuickTagStampSet
    {
        get
        {
            if (_quickTagStamp != null) return _quickTagStamp;
            _quickTagStamp = TaggingSettings.StampOptions(_config).CreateStamp(_config.TagStamp);
            _quickTagStamp.Changed += OnQuickTagStampChanged;
            return _quickTagStamp;
        }
    }

    // The stamp size is a setting (Config.TagStampSize, PT7): saving the settings shortens or lengthens the live stamp at once.
    private void ApplyTaggingPreferences()
    {
        if (_quickTagStamp != null) _quickTagStamp.Capacity = _config.TagStampSize;
    }

    private async void OnQuickTagStampChanged()
    {
        if (_quickTagStamp == null || _isClosing) return;
        string[] names = _quickTagStamp.ToNames().ToArray();
        _config.TagStamp = names;
        try { await _session.SaveViewPreferencesAsync(config => config.TagStamp = (string[])names.Clone()); }
        catch (Exception error) { Serilog.Log.Warning(error, "Could not save the tag stamp."); }
    }
    internal MediaTagDialog? GalleryMediaTagWindow { get; private set; }

    // K in the tagging mode: the walk's start choice is a direct view preference (Config.TaggingCardsDefault); the live copy changes at once.
    private void SaveTaggingCardsActive(bool active)
    {
        if (_isClosing) return;
        _config.TaggingCardsDefault = active;
        _ = SaveTaggingCardsActiveAsync(active);
    }

    private async Task SaveTaggingCardsActiveAsync(bool active)
    {
        try { await _session.SaveViewPreferencesAsync(config => config.TaggingCardsDefault = active); }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            if (!_isClosing) ShowNotification(Texts.T("notification.error"), Texts.T("preferences.failed", error.Message),
                Avalonia.Controls.Notifications.NotificationType.Error, context: "gallery", operation: "preferences.tagging-cards");
        }
    }

    private void OpenGalleryTagPanel()
    {
        if (_isClosing || _gallery == null) return;
        _gallery.ShowTagPanel(_galleryTagPanelOwner ??= new GalleryTagPanelOwner(this));
    }

    // What the gallery tag panel needs from the window: the frozen targets, the list, limits and the full dialog.
    private sealed class GalleryTagPanelOwner(MainWindow window) : IGalleryTagPanelOwner, IGalleryDecisionOwner, ITaggingCardsOwner
    {
        // Favorite and rating of a medium (PT6): the same stores, queues and error paths as the toolbar star and the rating keys. A pending
        // favorite write counts as its target value. Unreadable stores answer null (the row says so); the session reads again after every
        // write, so a failure the toolbar path already reported leaves no step.
        public GalleryDecisionState? Read(GalleryEntry entry)
        {
            if (window._isClosing) return null;
            FavoriteAssetKey key = FavoriteAssetKey.From(entry.Key.Thread, entry.Asset);
            int? rating = window._session.GetMediaRating(key);
            bool favorite = window._favoriteChanges.Target(key, window._session.IsFavorite(entry));
            return new GalleryDecisionState(favorite, rating);
        }

        public Task SetFavoriteAsync(GalleryEntry entry, bool selected, CancellationToken token) => window.SetGalleryFavoriteAsync(entry, selected);

        public Task SetRatingAsync(GalleryEntry entry, int? value, CancellationToken token) => window.SetGalleryRatingAsync(entry, value);

        public IQuickTagTarget? OpenTarget(GalleryEntry entry, out string? unavailableReason)
        {
            MediaTagSubject? subject = MediaTagSubjects.Of(entry, out GalleryMediaTagUnavailable reason);
            if (subject == null)
            {
                unavailableReason = MediaMenuReasons.Tags(reason, BooruMediaIdentity.IsBooruThread(entry.Key.Thread));
                return null;
            }
            PrivateTagLoadSnapshot loaded = window._session.GetPublishedPrivateTagSnapshot();
            if (loaded.State != PrivateTagLoadState.Ready || loaded.Snapshot == null)
            {
                unavailableReason = Texts.T("tags.unavailable");
                return null;
            }
            unavailableReason = null;
            return QuickTagTarget.For(new MediaTagSession(subject, window._session.GetPublishedPrivateTagSnapshot,
                window._session.DefineAndSetMediaSubjectTagAsync, window._session.SetMediaSubjectTagAsync));
        }

        public MediaTagSubject? SubjectOf(GalleryEntry entry, out string? unavailableReason)
        {
            MediaTagSubject? subject = MediaTagSubjects.Of(entry, out GalleryMediaTagUnavailable reason);
            unavailableReason = subject == null ? MediaMenuReasons.Tags(reason, BooruMediaIdentity.IsBooruThread(entry.Key.Thread)) : null;
            return subject;
        }

        // The marked media as one selection: one admitted write for all of them, bound to the revision the panel shows (the same
        // services as "Tag the selection" on the media page).
        public IQuickTagTarget? OpenSelectionTarget(IReadOnlyList<MediaTagSubject> subjects, out string? unavailableReason)
        {
            PrivateTagLoadSnapshot loaded = window._session.GetPublishedPrivateTagSnapshot();
            if (loaded.State != PrivateTagLoadState.Ready || loaded.Snapshot == null)
            {
                unavailableReason = Texts.T("tags.unavailable");
                return null;
            }
            unavailableReason = null;
            return QuickTagTarget.For(MediaSelectionTagSession.ForSubjects(subjects, window._session.GetPublishedPrivateTagSnapshot,
                (name, many, revision, token) => window._session.DefineAndSetMediaSubjectTagsAsync(name, many, revision, token),
                (tag, many, assigned, revision, token) => window._session.SetMediaSubjectTagsAsync(tag, many, assigned, revision, token)));
        }

        public IReadOnlyList<GalleryEntry> Entries => window._galleryEntries;

        public GalleryTagPanelPosition? Position => window._galleryIndex >= 0 && window._galleryIndex < window._galleryEntries.Count
            ? new GalleryTagPanelPosition(window._galleryIndex + 1, window._galleryEntries.Count, window._galleryThreadIndex < window._galleryThreads.Count)
            : null;

        public TagRecentList Recent => window.QuickTagRecent;
        // Legacy source seam retained for free-gallery callers: public QuickTagStamp? Stamp => window.QuickTagStampSet;
        public QuickTagStamp? Stamp => window.ActiveGalleryStamp;

        // New panels use the saved profile limits (TP5, MarkedMediaLimit included); an open panel retains its undo history and current operation.
        public QuickTagOptions Options => QuickTagSettings.SurfaceOptions(window._config);
        public TagContextOptions ContextOptions => QuickTagSettings.ContextOptions(window._config);

        public BooruCatalogSnapshot? ReadSourceTags() => window._session.ReadBooruCatalog();

        public GalleryThreadTagModel OpenThreadTags(ResourceId thread) => new(thread, window._session.GetPublishedPrivateTagSnapshot,
            (name, token) => window._session.DefinePrivateTagAsync(name, token),
            (tag, owner, assigned, token) => window._session.SetThreadTagAsync(tag, owner, assigned, token));

        public void OpenAdvanced(GalleryEntry entry) =>
            _ = window.RunGalleryActionAsync(() => window.EditGalleryMediaTagsAsync(entry), entry.Key.ToString());

        // The card mode of the tagging mode (PT4): the cards and the start choice come from the settings; K saves the choice.
        public TaggingCardSettings Cards => TaggingCardSettings.FromConfig(window._config);
        public void SaveCardsActive(bool active) => window.SaveTaggingCardsActive(active);
    }

    private void RefreshGalleryMediaTags()
    {
        if (_gallery == null || _galleryIndex < 0 || _galleryIndex >= _galleryEntries.Count) return;
        GalleryEntry current = _galleryEntries[_galleryIndex];
        _gallery.RetargetTagPanel(current); // an open panel follows the shown medium
        MediaTagSubject? target = MediaTagSubjects.Of(current, out GalleryMediaTagUnavailable reason);
        if (target == null)
        {
            _gallery.SetMediaTags(null, MediaMenuReasons.Tags(reason, BooruMediaIdentity.IsBooruThread(current.Key.Thread)));
            return;
        }
        PrivateTagLoadSnapshot loaded = _session.GetPublishedPrivateTagSnapshot();
        if (loaded.State != PrivateTagLoadState.Ready || loaded.Snapshot == null)
        {
            _gallery.SetMediaTags(null, Texts.T("tags.unavailable"));
            return;
        }
        _gallery.SetMediaTags(loaded.Snapshot.TagsOf(target).Select(tag => tag.Name.Display).ToArray());
    }

    private async Task EditGalleryMediaTagsAsync(GalleryEntry entry, Control? returnTo = null)
    {
        if (_isClosing || _galleryTagDialogOpen) return;
        MediaTagSubject? target = MediaTagSubjects.Of(entry, out _);
        if (target == null) { RefreshGalleryMediaTags(); return; }
        Control? origin = returnTo ?? FocusManager?.GetFocusedElement() as Control;
        _galleryTagDialogOpen = true;
        try
        {
            MediaTagSession session = new(target, _session.GetPublishedPrivateTagSnapshot,
                _session.DefineAndSetMediaSubjectTagAsync, _session.SetMediaSubjectTagAsync);
            MediaTagDialog dialog = new(session, entry.Asset.OriginalFileName ?? System.IO.Path.GetFileName(entry.Asset.Path), _session.ReadBooruCatalog);
            GalleryMediaTagWindow = dialog;
            await dialog.ShowDialog(this);
        }
        finally
        {
            GalleryMediaTagWindow = null;
            _galleryTagDialogOpen = false;
            if (!_isClosing)
            {
                // Media tags protect threads from cleanup and appear on the archive page.
                RefreshGalleryMediaTags();
                _gallery?.ReloadTagPanel(); // the panel shows what the dialog changed
                // A19: a tags action the view keeps under More returns focus to More, not to the closed menu's hidden row.
                Control? back = _gallery?.FocusReturnTarget(origin, "media.tags.edit") ?? origin;
                if (back?.IsEffectivelyVisible == true) back.Focus();
            }
        }
    }
}
