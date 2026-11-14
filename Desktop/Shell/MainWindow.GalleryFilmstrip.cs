using System;
using System.Threading.Tasks;
using Avalonia.Controls.Notifications;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;

namespace MeowChan;

public partial class MainWindow
{
    // GL4: the filmstrip choice (the L key, the ⋯ row) is part of the layout choice of the shown mode, saved per profile and mode. The View
    // set's filmstrip is also the old setting GalleryFilmstrip (GalleryLayoutPreferences.ApplyTo writes both). Until a write is confirmed the
    // viewer keeps the choice (a re-applied preference must not flip the strip back), the same way the volume and the context panel are kept.
    private GalleryLayoutPreferences? _desiredGalleryLayout;

    private GalleryLayoutPreferences GalleryLayoutNow() => _desiredGalleryLayout ?? GalleryLayoutPreferences.FromConfig(_config);

    private void ConfigureGalleryFilmstrip()
    {
        if (_gallery == null) return;
        GalleryLayoutPreferences layout = GalleryLayoutNow();
        _gallery.ConfigureFilmstrip(layout.View.Filmstrip != false, _config.GalleryFilmstripHeight, _config.GalleryFilmstripPreload);
        // Both modes are loaded here: the viewer shows the choice of the mode it is in and switches without another load.
        _gallery.SetLayoutChoice(GalleryChromeMode.View, layout.View);
        _gallery.SetLayoutChoice(GalleryChromeMode.Tag, layout.Tag);
    }

    private void OnGalleryLayoutChoiceChanged(GalleryViewer owner, GalleryChromeMode mode, GalleryLayoutChoice choice)
    {
        if (!ReferenceEquals(_gallery, owner) || _isClosing) return;
        _desiredGalleryLayout = GalleryLayoutNow().With(mode, choice);
        _ = SaveGalleryLayoutAsync(mode, choice);
    }

    private async Task SaveGalleryLayoutAsync(GalleryChromeMode mode, GalleryLayoutChoice choice)
    {
        try
        {
            // Only the changed mode is written, onto the latest saved state: the other mode (and a change made meanwhile) is not touched.
            await _session.SaveViewPreferencesAsync(current => GalleryLayoutPreferences.FromConfig(current).With(mode, choice).ApplyTo(current));
            if (_desiredGalleryLayout is { } desired && desired == GalleryLayoutPreferences.FromConfig(_config)) _desiredGalleryLayout = null;
        }
        catch (Exception error)
        {
            // The choice stays for this gallery; only the saving failed.
            if (!_isClosing) ShowNotification(Texts.T("notification.error"), Texts.T("preferences.failed", error.Message), NotificationType.Error, context: "gallery", operation: "preferences.gallery");
        }
    }
}
