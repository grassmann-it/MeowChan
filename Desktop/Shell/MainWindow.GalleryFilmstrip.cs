using System;
using System.Threading.Tasks;
using Avalonia.Controls.Notifications;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;

namespace MeowChan;

public partial class MainWindow
{
    // The L key and the ⋯ row of the gallery change the profile setting "show the filmstrip". Until the write is confirmed the viewer keeps
    // the choice (a re-applied preference must not flip the strip back), the same way the volume and the context panel are kept.
    private bool? _desiredGalleryFilmstrip;

    private void ConfigureGalleryFilmstrip() =>
        _gallery?.ConfigureFilmstrip(_desiredGalleryFilmstrip ?? _config.GalleryFilmstrip, _config.GalleryFilmstripHeight, _config.GalleryFilmstripPreload);

    private void OnGalleryFilmstripPreferenceChanged(GalleryViewer owner, bool show)
    {
        if (!ReferenceEquals(_gallery, owner) || _isClosing) return;
        _desiredGalleryFilmstrip = show;
        _ = SaveGalleryFilmstripAsync(show);
    }

    private async Task SaveGalleryFilmstripAsync(bool show)
    {
        try
        {
            await _session.SaveViewPreferencesAsync(current => current.GalleryFilmstrip = show);
            if (_desiredGalleryFilmstrip == show && _config.GalleryFilmstrip == show) _desiredGalleryFilmstrip = null;
        }
        catch (Exception error)
        {
            // The choice stays for this gallery; only the saving failed.
            if (!_isClosing) ShowNotification(Texts.T("notification.error"), Texts.T("preferences.failed", error.Message), NotificationType.Error, context: "gallery", operation: "preferences.gallery");
        }
    }
}
