using System;
using System.Threading.Tasks;
using Avalonia.Controls.Notifications;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;

namespace MeowChan;

// The gallery's mouse wheel setting (Config.GalleryWheelMode) changed in the viewer's "⋯" menu: a direct view preference like the tag
// cards default. The live configuration changes at once, the settings page (when it exists) shows the new value, the write is queued.
public partial class MainWindow
{
    private void OnGalleryWheelModeChanged(GalleryViewer owner, string mode)
    {
        if (!ReferenceEquals(_gallery, owner) || _isClosing) return;
        mode = Config.NormalizeGalleryWheelMode(mode);
        _config.GalleryWheelMode = mode;
        _settingsEditor?.RefreshConfirmedGalleryWheelMode(mode);
        _ = SaveGalleryWheelModeAsync(mode);
    }

    private async Task SaveGalleryWheelModeAsync(string mode)
    {
        try { await _session.SaveViewPreferencesAsync(config => config.GalleryWheelMode = mode); }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            if (!_isClosing) ShowNotification(Texts.T("notification.error"), Texts.T("preferences.failed", error.Message),
                NotificationType.Error, context: "gallery", operation: "preferences.gallery-wheel");
        }
    }
}
