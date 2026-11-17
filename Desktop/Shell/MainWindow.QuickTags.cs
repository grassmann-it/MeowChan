using System;
using System.Collections.Generic;
using System.Threading;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.Notifications;
using MeowChan.Desktop;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.MediaActions;
using MeowChan.Favorites;
using MeowChan.Gallery;
using MeowChan.Tags;

namespace MeowChan;

// The right-click tag popover of the media page, the reader attachments and the archive tiles (TP3). The page hands over the exact
// entry, the tile it opened on and the context of the suggestions; the shell resolves the file with the same rules as the tag
// dialog (a changed or unbound file is refused), owns the tag session and opens QuickTagPopover. "Advanced…" opens the former
// dialog for the same file; the popover's session is not reused there, the dialog reads the published tags itself.
public partial class MainWindow
{
    internal QuickTagPopover? MediaPageTagPopover { get; private set; }
    internal QuickTagPopover? ArchiveTagPopover { get; private set; }

    /// <summary>The saved quick tag limits (TP5) and the panel's ranking adapter over <paramref name="context"/>, for every popover host.</summary>
    internal QuickTagPopoverOptions TagPopoverDefaults(TagContext context) => QuickTagSettings.PopoverOptions(_config, context, QuickTagRecent, QuickTagStampSet);

    private QuickTagPopoverOptions PopoverOptions(TagContext context, Func<bool> isCurrent, bool restoreFocus, string name) => TagPopoverDefaults(context) with
    {
        ReadSourceTags = _session.ReadBooruCatalog,
        IsCurrent = isCurrent,
        RestoreFocus = restoreFocus,
        Name = name
    };

    private async Task OpenMediaLibraryTagPopoverAsync(MediaLibraryEntry indexed, Control anchor, TagContext context, Func<bool> presentationCurrent,
        RateAndTagRequest request)
    {
        Func<bool> hostCurrent = CaptureMediaPageMenuGuard();
        bool Current() => hostCurrent() && presentationCurrent();
        if (!Current() || _mediaTagDialogOpen) return;
        _mediaTagDialogOpen = true;
        Control? origin = FocusManager?.GetFocusedElement() as Control;
        QuickTagPopoverCloseReason reason = QuickTagPopoverCloseReason.Stale;
        GalleryEntry? match = null;
        MediaTagSession? session = null;
        QuickTagPopoverDecision? decision = null;
        // The open flag and the popover property end with the popover itself, not with this method's continuation: R pressed right after Esc ran before
        // the continuation and found the flag still set, so the second R did nothing (audit 16.11.2026).
        QuickTagPopover? shown = null;
        bool released = false;
        void Release()
        {
            if (released) return;
            released = true;
            if (shown == null || ReferenceEquals(MediaPageTagPopover, shown)) MediaPageTagPopover = null;
            _mediaTagDialogOpen = false;
        }
        try
        {
            match = await MediaMenuIdentity.ResolveAsync(indexed, _session.OpenArchiveReaderAsync, Storage.MediaAssetReader.OpenAsync, Current,
                System.Threading.CancellationToken.None, _session.ReadBooruCatalog, _session.ReadManualImportCatalog);
            if (!Current()) return;
            if (match == null)
            {
                ShowNotification(Texts.T("notification.error"), Texts.T("media.galleryFailed"), NotificationType.Warning, operation: "media.tags");
                return;
            }
            GalleryMediaTagUnavailable unavailable = GalleryMediaTagUnavailable.InvalidIdentity;
            MediaTagSubject? target = request.TagsBlocked == null ? MediaTagSubjects.Of(match, out unavailable) : null;
            string label = match.Asset.OriginalFileName ?? Path.GetFileName(match.Asset.Path);
            decision = DecisionFor(new[] { match }, request.FocusRating);
            // The popover gives the keyboard back at once (↑/↓ and R work right after Esc); the page places it again after it rebuilt its tiles.
            QuickTagPopoverOptions options = PopoverOptions(context, hostCurrent, restoreFocus: true, "MediaTagPopover") with
            {
                Decision = decision, TooltipKeyContext = "media", FocusFallback = request.FocusFallback
            };
            QuickTagPopover popover;
            if (target == null)
            {
                // A medium that cannot be tagged (a Danbooru medium, no bound version) can still be rated: the stars and the reason as text.
                popover = QuickTagPopover.OpenBlocked(anchor, label, request.TagsBlocked
                    ?? MediaMenuReasons.Tags(unavailable, BooruMediaIdentity.IsBooruThread(match.Key.Thread)), options);
            }
            else
            {
                session = new(target, _session.GetPublishedPrivateTagSnapshot, _session.DefineAndSetMediaSubjectTagAsync, _session.SetMediaSubjectTagAsync);
                popover = QuickTagPopover.Open(anchor, options.NewSession(QuickTagTarget.For(session)), label, options);
            }
            origin = popover.Origin ?? origin;
            shown = popover;
            MediaPageTagPopover = popover;
            popover.WhenClosed(Release);
            reason = await popover.Result;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException
            or Microsoft.Data.Sqlite.SqliteException)
        {
            if (Current()) ShowNotification(Texts.T("notification.error"), Texts.T("media.galleryFailed"), NotificationType.Error,
                operation: "media.tags");
        }
        finally
        {
            Release();
            // The page rebuilds its tiles after a change; it places the focus again (a tile that is gone gives it to the grid). A popover opened again in the
            // meantime (R right after Esc) keeps the focus.
            if (reason != QuickTagPopoverCloseReason.Advanced && !_isClosing)
            {
                bool changed = session?.Changed == true || decision?.HasChanged?.Invoke() == true;
                // The rebuild would detach the new popover's anchor: it waits until that one closed.
                if (changed && QuickTagPopover.Current is { } next) next.WhenClosed(() => { if (!_isClosing) RefreshMediaPageAfterMenuAction(origin, hostCurrent, presentationCurrent); });
                else if (changed) RefreshMediaPageAfterMenuAction(origin, hostCurrent, presentationCurrent);
                else if (QuickTagPopover.Current == null)
                    _mediaPage?.RestoreMediaMenuActionFocus(origin, () => hostCurrent() && QuickTagPopover.Current == null && !OwnedWindows.Any(window => window.IsVisible));
            }
        }
        if (reason == QuickTagPopoverCloseReason.Advanced && !_isClosing && match != null)
            await EditMediaLibraryTagsForMenuAsync(indexed, () => true, match, origin);
    }

    private async Task OpenArchiveMediaTagPopoverAsync(GalleryEntry entry, Control anchor, TagContext context, Func<bool> isCurrent,
        RateAndTagRequest request)
    {
        if (_isClosing || _galleryTagDialogOpen || !isCurrent()) return;
        GalleryMediaTagUnavailable unavailable = GalleryMediaTagUnavailable.InvalidIdentity;
        MediaTagSubject? target = request.TagsBlocked == null ? MediaTagSubjects.Of(entry, out unavailable) : null;
        string label = entry.Asset.OriginalFileName ?? Path.GetFileName(entry.Asset.Path);
        QuickTagPopoverDecision? decision = DecisionFor(new[] { entry }, request.FocusRating);
        if (target == null)
        {
            // Not taggable (a Danbooru medium, a file shared by several posts): the stars stay, the reason is said as text.
            QuickTagPopover.OpenBlocked(anchor, label, request.TagsBlocked
                ?? MediaMenuReasons.Tags(unavailable, BooruMediaIdentity.IsBooruThread(entry.Key.Thread)),
                new QuickTagPopoverOptions { IsCurrent = isCurrent, Decision = decision, TooltipKeyContext = "reader", FocusFallback = request.FocusFallback });
            return;
        }
        _galleryTagDialogOpen = true;
        QuickTagPopoverCloseReason reason = QuickTagPopoverCloseReason.Stale;
        Control? origin = null;
        // As on the media page: the flag ends with the popover (R right after Esc or × opens it again), never with this method's continuation.
        QuickTagPopover? shown = null;
        bool released = false;
        void Release()
        {
            if (released) return;
            released = true;
            if (shown == null || ReferenceEquals(ArchiveTagPopover, shown)) ArchiveTagPopover = null;
            _galleryTagDialogOpen = false;
        }
        try
        {
            MediaTagSession session = new(target, _session.GetPublishedPrivateTagSnapshot, _session.DefineAndSetMediaSubjectTagAsync, _session.SetMediaSubjectTagAsync);
            QuickTagPopoverOptions options = PopoverOptions(context, isCurrent, restoreFocus: true, "ArchiveTagPopover") with
            {
                Decision = decision, TooltipKeyContext = "reader", FocusFallback = request.FocusFallback
            };
            QuickTagPopover popover = QuickTagPopover.Open(anchor, options.NewSession(QuickTagTarget.For(session)), label, options);
            origin = popover.Origin;
            shown = popover;
            ArchiveTagPopover = popover;
            popover.WhenClosed(Release);
            reason = await popover.Result;
        }
        finally { Release(); }
        if (reason == QuickTagPopoverCloseReason.Advanced && !_isClosing) await EditGalleryMediaTagsAsync(entry, origin);
    }

    // The popover of a filmstrip tile's "Rate and tag…" in the gallery: the same popover as the archive's, on the filmstrip, for that tile's medium.
    private Task OpenGalleryTileRateAndTagAsync(GalleryEntry entry, Control anchor)
    {
        if (_isClosing || _gallery == null) return Task.CompletedTask;
        GalleryViewer gallery = _gallery;
        TagContext context = QuickTagPopoverContext.ContextOf(entry, _galleryEntries, null);
        string? blocked = MediaTagSubjects.Of(entry, out GalleryMediaTagUnavailable reason) == null
            ? MediaMenuReasons.Tags(reason, BooruMediaIdentity.IsBooruThread(entry.Key.Thread)) : null;
        // Closed with the focus gone (the filmstrip tile was rebuilt): the gallery's picture takes it, as after its other popups.
        bool FocusGallery()
        {
            if (_isClosing || !ReferenceEquals(_gallery, gallery)) return false;
            gallery.FocusGallerySurface();
            return true;
        }
        return OpenArchiveMediaTagPopoverAsync(entry, anchor, context, () => !_isClosing && ReferenceEquals(_gallery, gallery), new RateAndTagRequest(TagsBlocked: blocked, FocusFallback: FocusGallery));
    }

    // ---- favourite and rating row of the popovers --------------------------------------------------------------------------------

    /// <summary>
    /// The favourite and stars row of a "Rate and tag" popover over these media (review 15.11.): one decision session over the frozen list, the
    /// same stores and favourites call as the toolbar star and the gallery's rows. The session is disposed with the popover. A rating the popover
    /// writes does not rebuild the pages under it (that would drop its anchor); <see cref="QuickTagPopoverDecision.Release"/> refreshes the archive and
    /// gallery after it closed, and the media page does so through <see cref="QuickTagPopoverDecision.HasChanged"/>.
    /// </summary>
    internal QuickTagPopoverDecision? DecisionFor(IReadOnlyList<GalleryEntry> targets, bool focusRating)
    {
        if (_isClosing || targets.Count == 0) return null;
        GalleryEntry[] frozen = targets.ToArray();
        PopoverDecisionOwner owner = new(this);
        GalleryDecisionSession session = new(owner);
        return new QuickTagPopoverDecision
        {
            View = () => session.View(frozen),
            SetRating = value => session.SetRatingAsync(frozen, value),
            SetFavorite = selected => session.SetFavoriteAsync(frozen, selected),
            FocusRating = focusRating,
            HasChanged = () => owner.Changed,
            Release = () =>
            {
                session.Dispose();
                if (owner.Changed && !_isClosing) { _archivePage?.RefreshMediaRatings(); RefreshGalleryRating(); }
            }
        };
    }

    /// <summary>
    /// The row for a media page selection: the rating of every selected medium by its identity, no favourite button (the page has no favourites call
    /// for several media). Media without a hashed identity are skipped; none readable shows the row as unavailable.
    /// </summary>
    internal QuickTagPopoverDecision? DecisionForMediaEntries(IReadOnlyList<MediaLibraryEntry> entries, bool focusRating)
    {
        if (_isClosing) return null;
        FavoriteAssetKey[] keys = entries.Where(entry => entry.Sha256 != null && entry.MediaId != null)
            .Select(entry => new FavoriteAssetKey(entry.Thread, entry.MediaId!, entry.Variant, entry.Sha256!.ToLowerInvariant())).Distinct().ToArray();
        bool changed = false;
        GalleryDecisionView View()
        {
            if (_isClosing || keys.Length == 0) return GalleryDecisionView.Unavailable;
            try
            {
                int?[] values = keys.Select(key => _session.GetMediaRating(key)).ToArray();
                bool mixed = values.Distinct().Count() > 1;
                return new GalleryDecisionView(keys.Length, GalleryDecisionCoverage.None, mixed ? null : values[0], mixed, true);
            }
            catch (Exception error) when (error is InvalidDataException or ObjectDisposedException or ArgumentException) { return GalleryDecisionView.Unavailable; }
        }
        async Task SetRating(int? value)
        {
            foreach (FavoriteAssetKey key in keys)
            {
                if (_isClosing || !_ratingChanges.Add(key)) continue;
                try { await _session.SetMediaRatingAsync(key, value); changed = true; }
                catch (Exception error) when (error is not OperationCanceledException and not OutOfMemoryException)
                {
                    if (!_isClosing) ShowNotification(Texts.T("notification.error"), MediaRatingText.FormatError(MediaRatingText.Catalog("media.rating.error.save"), error.Message),
                        NotificationType.Error, operation: "media.rating");
                    break;
                }
                finally { _ratingChanges.Remove(key); }
            }
        }
        return new QuickTagPopoverDecision { View = View, SetRating = SetRating, FocusRating = focusRating, HasChanged = () => changed };
    }

    // What a popover's decision session reads and writes: the gallery panel's reads, the favourites call of the toolbar star, and the rating write
    // without the page refresh (a failed write is a notification; the session reads again to see what was stored).
    private sealed class PopoverDecisionOwner(MainWindow window) : IGalleryDecisionOwner
    {
        internal bool Changed { get; private set; }

        public GalleryDecisionState? Read(GalleryEntry entry)
        {
            if (window._isClosing) return null;
            FavoriteAssetKey key = FavoriteAssetKey.From(entry.Key.Thread, entry.Asset);
            return new GalleryDecisionState(window._favoriteChanges.Target(key, window._session.IsFavorite(entry)), window._session.GetMediaRating(key));
        }

        public async Task SetFavoriteAsync(GalleryEntry entry, bool selected, CancellationToken token)
        {
            await window.SetGalleryFavoriteAsync(entry, selected);
            Changed = true;
        }

        public async Task SetRatingAsync(GalleryEntry entry, int? value, CancellationToken token)
        {
            FavoriteAssetKey key = FavoriteAssetKey.From(entry.Key.Thread, entry.Asset);
            if (window._isClosing || !window._ratingChanges.Add(key)) return;
            try
            {
                await window._session.SetMediaRatingAsync(key, value, token);
                Changed = true;
            }
            catch (Exception error) when (error is not OperationCanceledException and not OutOfMemoryException)
            {
                if (!window._isClosing) window.ShowNotification(Texts.T("notification.error"),
                    MediaRatingText.FormatError(MediaRatingText.Catalog("media.rating.error.save"), error.Message), NotificationType.Error, operation: "media.rating");
            }
            finally { window._ratingChanges.Remove(key); }
        }
    }
}
