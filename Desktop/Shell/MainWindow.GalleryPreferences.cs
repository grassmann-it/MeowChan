using System;
using System.Threading.Tasks;
using System.Linq;
using MeowChan.Desktop;
using Avalonia.Controls.Notifications;
using Avalonia.Threading;
using MeowChan.Desktop.Localization;

namespace MeowChan;

public partial class MainWindow
{
    private DispatcherTimer? _galleryPreferenceTimer;
    private bool? _pendingGalleryContext;
    private int? _pendingGalleryVolume;
    private bool? _desiredGalleryContext;
    private int? _desiredGalleryVolume;
    private GalleryPlaybackPreferences? _pendingGalleryPlayback;
    private GalleryPlaybackPreferences? _pendingGalleryPlaybackBaseline;
    private GalleryPlaybackPreferences? _desiredGalleryPlayback;
    private GalleryPlaybackPreferences? _presentedGalleryPlayback;
    private Task _galleryPreferenceSave = Task.CompletedTask;
    private Task _galleryPreferenceWrite = Task.CompletedTask;

    private double ReadingFontSize() => _config.ReadingFontSize switch { "Small" => 13, "Large" => 18, _ => 15 };

    private void ApplyGalleryPreferences()
    {
        _gallery?.Configure(_desiredGalleryContext ?? _config.GalleryContextOpen, _config.ShowShortcutHelp,
            _config.VideoAutoplay, _config.VideoStartMuted, _desiredGalleryVolume ?? _config.VideoVolume);
        _presentedGalleryPlayback = _desiredGalleryPlayback ?? GalleryPlaybackPreferences.FromConfig(_config);
        _gallery?.ConfigurePlayback(_presentedGalleryPlayback);
        if (_archivePage != null) _archivePage.ReadingFontSizePreference = ReadingFontSize();
    }

    private void QueueGalleryPlaybackPreference(GalleryPlaybackPreferences preferences)
    {
        _pendingGalleryPlaybackBaseline ??= _presentedGalleryPlayback ?? GalleryPlaybackPreferences.FromConfig(_config);
        _pendingGalleryPlayback = _desiredGalleryPlayback = _presentedGalleryPlayback = preferences;
        QueueGalleryPreference(null, null);
    }

    private void OnGalleryActivationChanged(object? sender, EventArgs args) =>
        Dispatcher.UIThread.Post(UpdateGalleryInteractionAvailability, DispatcherPriority.Background);

    private void UpdateGalleryInteractionAvailability()
    {
        if (_isClosing || _gallery == null) return;
        bool activeSurface = IsActive || OwnedWindows.Any(window => window.IsActive && _gallery.OwnsOverlayWindow(window));
        _gallery.SetInteractionAvailable(activeSurface && IsEnabled && !_gallerySuspended);
    }

    private void QueueGalleryPreference(bool? context, int? volume)
    {
        if (context.HasValue) _pendingGalleryContext = _desiredGalleryContext = context;
        if (volume.HasValue) _pendingGalleryVolume = _desiredGalleryVolume = Math.Clamp(volume.Value, 0, 100);
        _galleryPreferenceTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background,
            (_, _) => { _ = FlushGalleryPreferencesAsync(); });
        _galleryPreferenceTimer.Stop();
        _galleryPreferenceTimer.Start();
    }

    private Task FlushGalleryPreferencesAsync()
    {
        _galleryPreferenceTimer?.Stop();
        bool? context = _pendingGalleryContext;
        int? volume = _pendingGalleryVolume;
        GalleryPlaybackPreferences? playback = _pendingGalleryPlayback;
        GalleryPlaybackPreferences? baseline = _pendingGalleryPlaybackBaseline;
        _pendingGalleryContext = null; _pendingGalleryVolume = null;
        _pendingGalleryPlayback = null; _pendingGalleryPlaybackBaseline = null;
        if (!context.HasValue && !volume.HasValue && playback == null)
        {
            // A later explicit Settings/Close flush may retry a retained failed
            // host draft. Do not permanently chain the earlier faulted observer.
            if (_galleryPreferenceWrite.IsFaulted)
            {
                _galleryPreferenceWrite = _session.FlushPreferencesAsync();
                _galleryPreferenceSave = ObserveGalleryPreferenceWriteAsync(_galleryPreferenceWrite);
            }
            return _galleryPreferenceSave;
        }
        Task write;
        try
        {
            write = _session.SaveViewPreferencesAsync(current =>
            {
                if (context.HasValue) current.GalleryContextOpen = context.Value;
                if (volume.HasValue) current.VideoVolume = volume.Value;
                if (playback != null && baseline != null) playback.ApplyChangesTo(current, baseline);
            });
        }
        catch (Exception error)
        {
            _pendingGalleryContext ??= context;
            _pendingGalleryVolume ??= volume;
            _pendingGalleryPlayback ??= playback;
            _pendingGalleryPlaybackBaseline ??= baseline;
            write = Task.FromException(error);
        }
        if (!ReferenceEquals(write, _galleryPreferenceWrite))
        {
            _galleryPreferenceWrite = write;
            _galleryPreferenceSave = ObserveGalleryPreferenceWriteAsync(write);
        }
        return _galleryPreferenceSave;
    }

    private async Task ObserveGalleryPreferenceWriteAsync(Task write)
    {
        try
        {
            await write;
            if (_pendingGalleryContext == null && _desiredGalleryContext == _config.GalleryContextOpen)
                _desiredGalleryContext = null;
            if (_pendingGalleryVolume == null && _desiredGalleryVolume == _config.VideoVolume)
                _desiredGalleryVolume = null;
            if (ReferenceEquals(write, _galleryPreferenceWrite) && _pendingGalleryPlayback == null)
                _desiredGalleryPlayback = null;
        }
        catch (Exception error)
        {
            if (!_isClosing) ShowNotification(Texts.T("notification.error"), Texts.T("preferences.failed", error.Message), NotificationType.Error, context: "gallery", operation: "preferences.gallery");
            // A late failure must not roll back a newer accepted preference edit.
            if (!_isClosing && ReferenceEquals(write, _galleryPreferenceWrite) && _pendingGalleryContext == null
                && _pendingGalleryVolume == null && _pendingGalleryPlayback == null)
            {
                _desiredGalleryContext = null; _desiredGalleryVolume = null; _desiredGalleryPlayback = null;
                ApplyGalleryPreferences();
            }
        }
    }
}
