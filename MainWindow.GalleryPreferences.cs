using System;
using System.Threading.Tasks;
using Avalonia.Controls.Notifications;
using Avalonia.Threading;
using MeowChan.Desktop.Localization;

namespace MeowChan;

public partial class MainWindow
{
    private DispatcherTimer? _galleryPreferenceTimer;
    private bool? _pendingGalleryContext;
    private int? _pendingGalleryVolume;
    private Task _galleryPreferenceSave = Task.CompletedTask;

    private double ReadingFontSize() => _config.ReadingFontSize switch { "Small" => 13, "Large" => 18, _ => 15 };

    private void ApplyGalleryPreferences()
    {
        _gallery?.Configure(_config.GalleryContextOpen, _config.ShowShortcutHelp,
            _config.VideoAutoplay, _config.VideoStartMuted, _config.VideoVolume);
        if (_archivePage != null) _archivePage.ReadingFontSizePreference = ReadingFontSize();
    }

    private void QueueGalleryPreference(bool? context, int? volume)
    {
        if (context.HasValue) _pendingGalleryContext = context;
        if (volume.HasValue) _pendingGalleryVolume = Math.Clamp(volume.Value, 0, 100);
        _galleryPreferenceTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background,
            async (_, _) => await FlushGalleryPreferencesAsync());
        _galleryPreferenceTimer.Stop();
        _galleryPreferenceTimer.Start();
    }

    private async Task FlushGalleryPreferencesAsync()
    {
        _galleryPreferenceTimer?.Stop();
        bool? context = _pendingGalleryContext;
        int? volume = _pendingGalleryVolume;
        _pendingGalleryContext = null; _pendingGalleryVolume = null;
        Task previous = _galleryPreferenceSave;
        if (!context.HasValue && !volume.HasValue) { await previous; return; }
        async Task SaveAsync()
        {
            try
            {
                await previous;
                await _session.SaveViewPreferencesAsync(current =>
                {
                    if (context.HasValue) current.GalleryContextOpen = context.Value;
                    if (volume.HasValue) current.VideoVolume = volume.Value;
                });
            }
            catch (Exception error)
            {
                if (!_isClosing) ShowNotification(Texts.T("notification.error"), Texts.T("preferences.failed", error.Message), NotificationType.Error, context: "gallery", operation: "preferences.gallery");
                ApplyGalleryPreferences();
            }
        }
        _galleryPreferenceSave = SaveAsync();
        await _galleryPreferenceSave;
    }
}
