using System;
using System.Threading.Tasks;
using Avalonia.Controls.Notifications;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;

namespace MeowChan;

public partial class MainWindow
{
    private void OnGalleryBackgroundChanged(GalleryViewer owner, string mode)
    {
        if (!ReferenceEquals(_gallery, owner) || _isClosing) return;
        string previous = _config.GalleryBackground;
        _config.GalleryBackground = mode;
        _settingsEditor?.RefreshConfirmedGalleryBackground(previous, mode);
        OpenActions.Start(() => SaveGalleryBackgroundAsync(mode), "preferences.gallery-background");
    }

    private async Task SaveGalleryBackgroundAsync(string mode)
    {
        try { await _session.SaveViewPreferencesAsync(config => config.GalleryBackground = mode); }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            if (!_isClosing) ShowNotification(Texts.T("notification.error"), Texts.T("preferences.failed", error.Message),
                NotificationType.Error, context: "gallery", operation: "preferences.gallery-background");
        }
    }
}
