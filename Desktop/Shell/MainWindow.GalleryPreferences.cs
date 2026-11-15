using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Linq;
using MeowChan.Desktop;
using MeowChan.Desktop.Controls;
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
        // A saved zero must discard recent tags immediately, even when the tag panel is closed.
        if (_quickTagRecent is { } recent) QuickTagSettings.ApplyRecentLimit(recent, _config);
        ApplyTaggingPreferences();
        ConfigureGalleryScrubPreview();
        _gallery?.Configure(_desiredGalleryContext ?? _config.GalleryContextOpen, _config.ShowShortcutHelp,
            _config.VideoAutoplay, _config.VideoStartMuted, _desiredGalleryVolume ?? _config.VideoVolume);
        _presentedGalleryPlayback = _desiredGalleryPlayback ?? GalleryPlaybackPreferences.FromConfig(_config);
        _gallery?.ConfigurePlayback(_presentedGalleryPlayback);
        _gallery?.ConfigureFit(ReaderSettingsPreferences.Gallery(_config));
        _gallery?.ConfigureZoomLimits(_config.GalleryMaxZoomPercent);
        if (_gallery != null) _gallery.ControlsIdleHideMs = _config.ControlsIdleHideMs;
        if (_gallery != null) _gallery.VideoSeekStepSeconds = _config.GalleryVideoSeekStepSeconds;
        if (_gallery != null) _gallery.StatusDwellMs = _config.GalleryStatusDwellMs;
        if (_gallery != null) _gallery.PanelWidth = GalleryPanelWidthPreference();
        ConfigureGalleryFilmstrip();
        if (_archivePage != null)
        {
            _archivePage.ReadingFontSizePreference = ReadingFontSize();
            _archivePage.ReaderPostWidthPreference = _config.ReaderPostWidth;
            ReaderSettingsPreferences.Apply(_archivePage, _config);
            _archivePage.ReaderNavigationIdleDelayMs = _config.ControlsIdleHideMs;
            _archivePage.ReaderNavigationMode = _config.ReaderNavigationMode;
            _archivePage.ReaderNavigationShowPosition = _config.ReaderNavigationShowPosition;
        }
    }

    // V2: the width of the gallery side panel is a view preference of the layout profile (ColumnWidths["gallery.panelWidth"]),
    // written with the window's column widths (MainWindow.ViewPreferences.cs) and clamped to the panel's limits when read.
    private double GalleryPanelWidthPreference() =>
        _config.ColumnWidths.TryGetValue(GallerySidePanel.WidthPreferenceKey, out double width)
            ? GallerySidePanel.ClampWidth(width) : Config.GalleryPanelWidthDefault;

    private void QueueGalleryPanelWidth(double width)
    {
        string mode = _config.WorkspaceMode;
        width = GallerySidePanel.ClampWidth(width);
        if (!_pendingColumnWidths.TryGetValue(mode, out Dictionary<string, double>? pending))
            _pendingColumnWidths[mode] = pending = new(StringComparer.Ordinal);
        pending[GallerySidePanel.WidthPreferenceKey] = width;
        _config.GetLayoutProfile(mode).ColumnWidths[GallerySidePanel.WidthPreferenceKey] = width;
        QueueWindowPreferences(); // in fullscreen the draft waits for the next window save or the final flush on close
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
        // The second value is the part that is not about the focus: with "Pause when MeowChan loses focus" off, a slideshow runs on while
        // another window has the focus, while minimised or in the tray, but a disabled main window or the reader still stop it.
        _gallery.SetInteractionAvailable(activeSurface && IsEnabled && !_gallerySuspended, IsEnabled && !_gallerySuspended);
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
        GalleryLayoutChoice? layoutView = _pendingGalleryLayoutView, layoutTag = _pendingGalleryLayoutTag;
        _pendingGalleryContext = null; _pendingGalleryVolume = null;
        _pendingGalleryPlayback = null; _pendingGalleryPlaybackBaseline = null;
        _pendingGalleryLayoutView = null; _pendingGalleryLayoutTag = null;
        if (!context.HasValue && !volume.HasValue && playback == null && layoutView == null && layoutTag == null)
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
                ApplyGalleryLayoutChanges(current, layoutView, layoutTag);
            });
        }
        catch (Exception error)
        {
            _pendingGalleryContext ??= context;
            _pendingGalleryVolume ??= volume;
            _pendingGalleryPlayback ??= playback;
            _pendingGalleryPlaybackBaseline ??= baseline;
            _pendingGalleryLayoutView ??= layoutView;
            _pendingGalleryLayoutTag ??= layoutTag;
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
            // The gallery's volume reaches the Media page's panel (its next player) now, not only after the settings were saved.
            if (!_isClosing) ConfigureMediaPanel();
            if (_pendingGalleryContext == null && _desiredGalleryContext == _config.GalleryContextOpen)
                _desiredGalleryContext = null;
            if (_pendingGalleryVolume == null && _desiredGalleryVolume == _config.VideoVolume)
                _desiredGalleryVolume = null;
            if (ReferenceEquals(write, _galleryPreferenceWrite) && _pendingGalleryPlayback == null)
                _desiredGalleryPlayback = null;
            if (ReferenceEquals(write, _galleryPreferenceWrite) && !GalleryLayoutPending)
                _desiredGalleryLayout = null;
        }
        catch (Exception error)
        {
            if (!_isClosing) ShowNotification(Texts.T("notification.error"), Texts.T("preferences.failed", error.Message), NotificationType.Error, context: "gallery", operation: "preferences.gallery");
            // The host retains the failed draft for explicit retry. Keep current live
            // volume/mode/layout intent too: presentation of a persistence failure must
            // never reconfigure a playing/paused medium from the last durable snapshot.
        }
    }
}
