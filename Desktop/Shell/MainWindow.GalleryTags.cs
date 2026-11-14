using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.MediaActions;
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
    internal MediaTagDialog? GalleryMediaTagWindow { get; private set; }

    private void OpenGalleryTagPanel()
    {
        if (_isClosing || _gallery == null) return;
        _gallery.ShowTagPanel(_galleryTagPanelOwner ??= new GalleryTagPanelOwner(this));
    }

    // What the gallery tag panel needs from the window: the frozen targets, the list, limits and the full dialog.
    private sealed class GalleryTagPanelOwner(MainWindow window) : IGalleryTagPanelOwner
    {
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

        // New panels use the saved profile limits (TP5, MarkedMediaLimit included); an open panel retains its undo history and current operation.
        public QuickTagOptions Options => QuickTagSettings.SurfaceOptions(window._config);
        public TagContextOptions ContextOptions => QuickTagSettings.ContextOptions(window._config);

        public BooruCatalogSnapshot? ReadSourceTags() => window._session.ReadBooruCatalog();

        public GalleryThreadTagModel OpenThreadTags(ResourceId thread) => new(thread, window._session.GetPublishedPrivateTagSnapshot,
            (name, token) => window._session.DefinePrivateTagAsync(name, token),
            (tag, owner, assigned, token) => window._session.SetThreadTagAsync(tag, owner, assigned, token));

        public void OpenAdvanced(GalleryEntry entry) =>
            _ = window.RunGalleryActionAsync(() => window.EditGalleryMediaTagsAsync(entry), entry.Key.ToString());
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

    private async Task EditGalleryMediaTagsAsync(GalleryEntry entry)
    {
        if (_isClosing || _galleryTagDialogOpen) return;
        MediaTagSubject? target = MediaTagSubjects.Of(entry, out _);
        if (target == null) { RefreshGalleryMediaTags(); return; }
        Control? origin = FocusManager?.GetFocusedElement() as Control;
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
