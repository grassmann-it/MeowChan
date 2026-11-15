using MeowChan.Desktop;

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
        // Both modes are loaded here: the viewer shows the choice of the mode it is in and switches without another load.
        _gallery.SetLayoutChoice(GalleryChromeMode.View, layout.View);
        _gallery.SetLayoutChoice(GalleryChromeMode.Tag, layout.Tag);
    }

    private void OnGalleryLayoutChoiceChanged(GalleryViewer owner, GalleryChromeMode mode, GalleryLayoutChoice choice)
    {
        if (!ReferenceEquals(_gallery, owner) || _isClosing) return;
        _desiredGalleryLayout = GalleryLayoutNow().With(mode, choice);
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
