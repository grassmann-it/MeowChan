using System;
using System.IO;
using Avalonia.Threading;
using MeowChan.Desktop;
using MeowChan.Desktop.Controls;
using MeowChan.Favorites;
using MeowChan.Gallery;
using MeowChan.Tags;

namespace MeowChan;

public partial class MainWindow
{
    // GL4: the filmstrip choice (the L key, the ⋯ row) is part of the layout choice of the shown mode, saved per profile and mode. The View
    // set's filmstrip is also the old setting GalleryFilmstrip (GalleryLayoutPreferences.ApplyTo writes both). Until a write is confirmed the
    // viewer keeps the choice (a re-applied preference must not flip the strip back), the same way the volume and the context panel are kept.
    // The write goes with the other gallery preferences (QueueGalleryPreference / FlushGalleryPreferencesAsync), so closing the gallery or
    // the window right after L still saves it (N3).
    private GalleryLayoutPreferences? _desiredGalleryLayout;
    // The changed choice of each mode not yet handed to the host (null = unchanged); only these are written onto the latest saved state.
    private GalleryLayoutChoice? _pendingGalleryLayoutView, _pendingGalleryLayoutTag;

    private GalleryLayoutPreferences GalleryLayoutNow() => _desiredGalleryLayout ?? GalleryLayoutPreferences.FromConfig(_config);

    private bool GalleryLayoutPending => _pendingGalleryLayoutView != null || _pendingGalleryLayoutTag != null;

    private void ConfigureGalleryFilmstrip()
    {
        if (_gallery == null) return;
        GalleryLayoutPreferences layout = GalleryLayoutNow();
        _gallery.ConfigureFilmstrip(layout.View.Filmstrip != false, _config.GalleryFilmstripHeight, _config.GalleryFilmstripPreload, _config.GalleryFilmstripTileGap);
        _gallery.ConfigureFilmstripFlags(FilmstripFlagSettings.FromConfig(_config));
        _gallery.SetFilmstripFlagSource(ReadFilmstripFlagFacts);
        // Both modes are loaded here: the viewer shows the choice of the mode it is in and switches without another load.
        _gallery.SetLayoutChoice(GalleryChromeMode.View, layout.View);
        _gallery.SetLayoutChoice(GalleryChromeMode.Tag, layout.Tag);
    }

    // ---- filmstrip flags (decision 123): favourite, rating and "no tags yet" on the filmstrip tiles ----
    // The facts come from what the window already holds in memory (the favourite list, the rating list and the tag index the media page reads too);
    // a tile never reads a file. They follow the same session notification the media page follows (StateChanged), which is subscribed once for the
    // window and released when it closes; a notification that changed none of the three sources draws nothing.
    private MediaLibraryUserData? _filmstripTagIndex;
    private FilmstripFlagSources _filmstripFlagSources;

    private void InitializeGalleryFilmstripFlags()
    {
        _session.StateChanged += OnGalleryFilmstripSessionStateChanged;
        Closed += (_, _) => _session.StateChanged -= OnGalleryFilmstripSessionStateChanged;
    }

    // Raised on any thread, for every command of the session: nothing is queued while no gallery is open.
    private void OnGalleryFilmstripSessionStateChanged()
    {
        if (_gallery == null) return;
        Dispatcher.UIThread.Post(() => RefreshGalleryFilmstripFlags(onlyIfSourcesChanged: true));
    }

    /// <summary>The favourite, rating and tag facts of one medium for the flags asked for; what cannot be read is simply not a flag.</summary>
    private FilmstripFlagFacts ReadFilmstripFlagFacts(GalleryEntry entry, FilmstripFlagKinds kinds)
    {
        if (_isClosing) return default;
        bool favorite = false;
        int? rating = null;
        bool? untagged = null;
        try
        {
            FavoriteAssetKey key = FavoriteAssetKey.From(entry.Key.Thread, entry.Asset);
            // A pending favourite write counts as its target, as it does for the toolbar star.
            if (kinds.HasFlag(FilmstripFlagKinds.Favorite)) favorite = _favoriteChanges.Target(key, _session.IsFavorite(entry));
            if (kinds.HasFlag(FilmstripFlagKinds.Rating)) rating = _session.GetMediaRating(key);
        }
        catch (Exception error) when (error is InvalidDataException or ObjectDisposedException or ArgumentException) { }
        if (kinds.HasFlag(FilmstripFlagKinds.Untagged))
        {
            try { untagged = FilmstripUntagged(entry); }
            catch (Exception error) when (error is InvalidDataException or ObjectDisposedException or ArgumentException) { }
        }
        return new FilmstripFlagFacts(favorite, rating, untagged);
    }

    // The media page's rule (no own media tag; thread tags do not count) over one index per published tag snapshot, built when the first tile asks.
    private bool? FilmstripUntagged(GalleryEntry entry)
    {
        PrivateTagLoadSnapshot tags = _session.GetPublishedPrivateTagSnapshot();
        if (tags.State != PrivateTagLoadState.Ready || tags.Snapshot == null) return null;
        if (_filmstripTagIndex == null || !ReferenceEquals(_filmstripTagIndex.TagSnapshot, tags.Snapshot))
            _filmstripTagIndex = new MediaLibraryUserData(tags.Snapshot, null, null);
        return _filmstripTagIndex.LacksMediaTags(MediaTagSubjects.Of(entry));
    }

    private FilmstripFlagSources CurrentFilmstripFlagSources()
    {
        object? tags = null, favorites = null, ratings = null;
        try { PrivateTagLoadSnapshot loaded = _session.GetPublishedPrivateTagSnapshot(); tags = loaded.State == PrivateTagLoadState.Ready ? loaded.Snapshot : null; }
        catch (ObjectDisposedException) { }
        try { favorites = _session.GetFavorites(); } catch (Exception error) when (error is InvalidDataException or ObjectDisposedException) { }
        try { ratings = _session.GetMediaRatings(); } catch (Exception error) when (error is InvalidDataException or ObjectDisposedException) { }
        return new FilmstripFlagSources(tags, favorites, ratings);
    }

    /// <summary>
    /// The tiles of the open filmstrip read their flags again. After an action of the gallery itself (a favourite, a rating, tags) it always does; for a
    /// session notification (<paramref name="onlyIfSourcesChanged"/>) only when the favourite list, the rating list or the tag snapshot is another one.
    /// </summary>
    private void RefreshGalleryFilmstripFlags(bool onlyIfSourcesChanged = false)
    {
        if (_gallery == null || _isClosing) return;
        FilmstripFlagSources now = CurrentFilmstripFlagSources();
        bool same = now.Same(_filmstripFlagSources);
        _filmstripFlagSources = now;
        if (onlyIfSourcesChanged && same) return;
        _gallery.RefreshFilmstripFlags();
    }

    private void OnGalleryLayoutChoiceChanged(GalleryViewer owner, GalleryChromeMode mode, GalleryLayoutChoice choice)
    {
        if (!ReferenceEquals(_gallery, owner) || _isClosing) return;
        _desiredGalleryLayout = GalleryLayoutNow().With(mode, choice);
        // The gallery owns the live layout choice. Keep the already-created Settings editor in step with
        // the View set immediately; waiting for the preference write leaves the old GalleryFilmstrip draft visible.
        if (mode == GalleryChromeMode.View) _settingsEditor?.RefreshConfirmedGalleryFilmstrip(choice.Filmstrip != false);
        if (mode == GalleryChromeMode.Tag) _pendingGalleryLayoutTag = choice; else _pendingGalleryLayoutView = choice;
        QueueGalleryPreference(null, null);
    }

    /// <summary>
    /// Only the changed modes are written, onto the latest saved state: the other mode (and a change made meanwhile) is not touched.
    /// </summary>
    internal static void ApplyGalleryLayoutChanges(Config current, GalleryLayoutChoice? view, GalleryLayoutChoice? tag)
    {
        if (view == null && tag == null) return;
        GalleryLayoutPreferences layout = GalleryLayoutPreferences.FromConfig(current);
        if (view != null) layout = layout.With(GalleryChromeMode.View, view);
        if (tag != null) layout = layout.With(GalleryChromeMode.Tag, tag);
        layout.ApplyTo(current);
    }

    /// <summary>
    /// After the settings were applied the saved layout is the truth again (it holds the flushed choice, a reset or the filmstrip box):
    /// the kept choice is dropped, unless the gallery changed the layout meanwhile and that write is still to come.
    /// </summary>
    private void ForgetDesiredGalleryLayout()
    {
        if (!GalleryLayoutPending) _desiredGalleryLayout = null;
    }
}
