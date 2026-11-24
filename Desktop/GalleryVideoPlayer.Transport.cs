using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LibVLCSharp.Shared;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Controls;
using Texts = MeowChan.Desktop.Localization.Texts;

namespace MeowChan.Desktop;

// The rest of the video transport row (plan V1): A–B loop with its marks on the timeline, next frame, the volume that opens
// on hover or keyboard focus, full screen and the player menu (speed, audio track, save frame, repeat). Every control is an
// icon or a short symbol label with a tooltip, accessible name and its shortcut from the command catalog.
internal sealed partial class GalleryVideoPlayer
{
    /// <summary>The volume opened by pointing at the speaker closes this long after the pointer left it (documented fixed delay).</summary>
    internal const int VolumePopupCloseDelayMs = 400;
    /// <summary>One mouse-wheel notch over the speaker changes the volume by this much (documented fixed step).</summary>
    internal const int VolumeWheelStep = 5;
    // Next frame: the backend reports no time change for a paused step, so the time is read back after it.
    private const int FrameReadbackDelayMs = 60;
    private const string AbLoopLabel = "A·B";

    private readonly StackPanel _secondary = new() { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
    private readonly ToggleButton _abLoopButton = new() { Name = "GalleryVideoAbLoop", Classes = { "media-control" }, MinWidth = 40, Height = 32,
        Padding = new Thickness(8, 0), HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center };
    private readonly Button _frameStep = new() { Name = "GalleryVideoFrameStep", Classes = { "media-control" }, Width = 32, Height = 32, Padding = new Thickness(0),
        HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center };
    private readonly Button _fullscreen = new() { Name = "GalleryVideoFullscreen", Classes = { "media-control" }, Width = 32, Height = 32, Padding = new Thickness(0),
        HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center };
    private readonly Button _playerMenuButton = new() { Name = "GalleryVideoMenu", Classes = { "media-control" }, Width = 32, Height = 32, Padding = new Thickness(0),
        HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center };
    private readonly MenuFlyout _playerMenu = new() { Placement = PlacementMode.TopEdgeAlignedRight };
    private readonly ContextMenu _speedMenu = new();
    private readonly List<(MenuItem Item, float Rate)> _speedMenuRates = new(), _playerMenuRates = new();
    private readonly MenuItem _playerMenuSpeed = new() { Name = "GalleryVideoMenuSpeed" };
    private readonly MenuItem _playerMenuAudio = new() { Name = "GalleryVideoMenuAudio" };
    private MenuItem _playerMenuSaveFrame = null!;
    private MenuItem _playerMenuRepeat = null!;
    private MenuItem _playerMenuRotate = null!;
    private string? _playerMenuLanguage;

    // The speaker and, above it, the volume as a vertical slider. The slider is drawn on a zero-size canvas so opening it
    // never changes the layout of the row, and it stays in this visual tree (pointer and focus remain the gallery's).
    private readonly Grid _volumeHost = new() { Name = "GalleryVideoVolumeHost", Background = Brushes.Transparent, VerticalAlignment = VerticalAlignment.Center };
    private readonly Canvas _volumeCanvas = new();
    private readonly Border _volumePopup = new()
    {
        Name = "GalleryVideoVolumePopup", IsVisible = false, Width = 32, BorderThickness = new Thickness(1),
        [!Border.PaddingProperty] = new DynamicResourceExtension("Workspace.Inset.VolumePopup"),
        [!Border.BackgroundProperty] = new DynamicResourceExtension("Workspace.Panel"),
        [!Border.BorderBrushProperty] = new DynamicResourceExtension("Workspace.ControlBorder"),
        [!Border.CornerRadiusProperty] = new DynamicResourceExtension("Workspace.Radius.Control")
    };
    private readonly DispatcherTimer _volumeCloseTimer = new() { Interval = TimeSpan.FromMilliseconds(VolumePopupCloseDelayMs) };
    private bool _volumeAsPopup = true;
    private bool _volumeKeyboardFocus;
    private bool _volumeDragging;

    // The A–B marks: two 2 dip strokes and the looped stretch between them, over the timeline track (no input).
    private readonly Canvas _seekMarks = new() { Name = "GalleryVideoLoopMarks", IsHitTestVisible = false };
    private readonly Rectangle _markStart = LoopMark(), _markEnd = LoopMark();
    private readonly Rectangle _markRange = new()
    {
        Height = 3, Opacity = 0.35, IsVisible = false, IsHitTestVisible = false,
        [!Shape.FillProperty] = new DynamicResourceExtension("Workspace.Accent")
    };
    private Track? _seekTrack;

    private readonly VideoAbLoop _abLoop = new();
    private string? _abLoopNotice;
    private readonly DispatcherTimer _loopJumpTimer = new();
    private long _loopJumpGeneration;

    private IReadOnlyList<VideoAudioTrackChoice> _audioTrackChoices = Array.Empty<VideoAudioTrackChoice>();
    private int _audioTrackCurrent = -1;
    private readonly Func<int, int?>? _selectAudioTrackOverride;

    // The selector returns the transport's readback of the active track, or no observation on failure.
    internal GalleryVideoPlayer(Func<int, int?> selectAudioTrack) : this() => _selectAudioTrackOverride = selectAudioTrack;

    /// <summary>The full-screen button of the transport was pressed (the viewer owns the window).</summary>
    internal event Action? FullscreenRequested;
    /// <summary>"Save frame as image" was chosen in the player menu (the viewer owns the folder).</summary>
    internal event Action? SaveFrameRequested;

    internal Control PlayerMenuControl => _playerMenuButton;
    internal MenuFlyout PlayerMenu => _playerMenu;
    internal ContextMenu SpeedMenu => _speedMenu;
    internal VideoAbLoopState AbLoopState => _abLoop.State;
    internal (long Start, long End) AbLoopRange => (_abLoop.Start, _abLoop.End);
    internal IReadOnlyList<VideoAudioTrackChoice> AudioTrackChoices => _audioTrackChoices;
    internal bool IsVolumePopupOpen => _volumePopup.IsVisible;

    private static Rectangle LoopMark() => new()
    {
        Width = 2, Height = 12, IsVisible = false, IsHitTestVisible = false,
        [!Shape.FillProperty] = new DynamicResourceExtension("Workspace.Accent")
    };

    private void InitializeTransportExtras()
    {
        _seekMarks.Children.Add(_markRange); _seekMarks.Children.Add(_markStart); _seekMarks.Children.Add(_markEnd);
        _seekMarks.SizeChanged += (_, _) => LayoutLoopMarks();
        _seek.TemplateApplied += (_, e) =>
        {
            _seekTrack = e.NameScope.Find<Track>("PART_Track");
            Dispatcher.UIThread.Post(LayoutLoopMarks, DispatcherPriority.Loaded);
        };
        _seek.PropertyChanged += (_, args) =>
        {
            if (args.Property == IsEnabledProperty) UpdateTransportExtrasEnabled();
        };

        _abLoopButton.Content = AbLoopLabel;
        AutomationProperties.SetLiveSetting(_abLoopButton, AutomationLiveSetting.Polite); // "A–B loop 0:04–0:09" is announced.
        ToolTip.SetShowOnDisabled(_abLoopButton, true);
        _abLoopButton.Click += (_, _) => ToggleAbLoop();
        _frameStep.Click += async (_, _) => await StepFrameAsync();
        ToolTip.SetShowOnDisabled(_frameStep, true);
        _fullscreen.Click += (_, _) => FullscreenRequested?.Invoke();
        _loopJumpTimer.Tick += (_, _) => { _loopJumpTimer.Stop(); if (_loopJumpGeneration == _generation) JumpWhenLoopEndReached(); };

        // Volume: the speaker toggles mute; pointing at it or focusing it by keyboard shows the slider above it.
        _volumeHost.Children.Add(_mute);
        _volumeHost.Children.Add(_volumeCanvas);
        _volumeCanvas.Children.Add(_volumePopup);
        Canvas.SetLeft(_volumePopup, 0);
        Canvas.SetBottom(_volumePopup, 32 + 4);
        _volumeHost.PropertyChanged += (_, args) =>
        {
            if (args.Property == IsPointerOverProperty) UpdateVolumePopup();
        };
        // A native pointer can move within the retained host while the transport
        // changes visual roots. PropertyChanged is not guaranteed for that move on
        // every platform, so use the real pointer route as the second admission
        // signal; no routed or synthetic event is manufactured here.
        _volumeHost.PointerEntered += (_, _) => UpdateVolumePopup();
        _volumeHost.PointerMoved += (_, _) => UpdateVolumePopup();
        _volumeHost.AddHandler(GotFocusEvent, (_, e) =>
        {
            _volumeKeyboardFocus = e.NavigationMethod is NavigationMethod.Tab or NavigationMethod.Directional;
            UpdateVolumePopup();
        }, RoutingStrategies.Bubble, handledEventsToo: true);
        _volumeHost.AddHandler(LostFocusEvent, (_, _) => Dispatcher.UIThread.Post(() =>
        {
            if (!_volumeHost.IsKeyboardFocusWithin) _volumeKeyboardFocus = false;
            UpdateVolumePopup();
        }), RoutingStrategies.Bubble, handledEventsToo: true);
        _volumeHost.AddHandler(PointerWheelChangedEvent, (_, e) =>
        {
            if (!CanAdjustVolume || e.Delta.Y == 0) return;
            AdjustVolumeBy(Math.Sign(e.Delta.Y) * VolumeWheelStep);
            e.Handled = true;
        }, RoutingStrategies.Bubble);
        _volume.AddHandler(PointerPressedEvent, (_, _) => _volumeDragging = true, RoutingStrategies.Tunnel, handledEventsToo: true);
        _volume.AddHandler(PointerReleasedEvent, (_, _) => { _volumeDragging = false; UpdateVolumePopup(); }, RoutingStrategies.Bubble, handledEventsToo: true);
        _volume.PointerCaptureLost += (_, _) => { _volumeDragging = false; UpdateVolumePopup(); };
        _volumeCloseTimer.Tick += (_, _) =>
        {
            _volumeCloseTimer.Stop();
            if (!KeepVolumeOpen) SetVolumePopupVisible(false);
        };
        ApplyVolumePlacement();

        BuildSpeedMenus();
        BuildPlayerMenu();
        UpdateTransportExtrasEnabled();
    }

    // ---- volume ----------------------------------------------------------------------------------------------------

    // Below this width the row splits in two (timeline above, controls below); an inline volume slider needs 80 dip more.
    private double CompactTransportWidth => _volumeAsPopup ? 560 : 660;

    // The popup is intentionally drawn above the compact transport and therefore can sit
    // outside the host Grid's arranged bounds. Keep it alive from the actual popup/slider
    // hover as well as the speaker host; on native macOS input the overflow child can stop
    // contributing to the host's IsPointerOver state during the move into the slider.
    private bool KeepVolumeOpen => _volumeHost.IsPointerOver || _volumePopup.IsPointerOver
        || _volume.IsPointerOver || _volumeKeyboardFocus || _volumeDragging;

    // A native Windows video draws above everything in its area: with docked controls (no overlay window) the volume stays a
    // horizontal slider in the row, as before. The software surface (Linux, macOS) and the native overlay window take the popup.
    private void ApplyVolumePlacement()
    {
        bool popup = UsesSoftwareVideo || _externalTransportHost != null;
        if (popup == _volumeAsPopup && _volume.Parent != null)
        {
            // Placement can be requested again after the transport crossed roots.
            // The pointer may still be over the speaker even though no new
            // IsPointerOver notification was raised; do not leave the popup hidden.
            UpdateVolumePopup();
            return;
        }
        _volumeAsPopup = popup;
        (_volume.Parent as Panel)?.Children.Remove(_volume);
        if (ReferenceEquals(_volumePopup.Child, _volume)) _volumePopup.Child = null;
        _volume.Orientation = popup ? Orientation.Vertical : Orientation.Horizontal;
        _volume.Width = popup ? 32 : 80;
        _volume.Height = popup ? 80 : 32;
        _volume.HorizontalAlignment = HorizontalAlignment.Center;
        // The vertical track is 4 dip wide: an even width sits exactly on the middle of the 12 dip thumb and the box (3 left half a dip off).
        _volume.Resources["SliderTrackWidth"] = popup ? 4d : 3d;
        if (popup) _volumePopup.Child = _volume;
        else
        {
            SetVolumePopupVisible(false);
            _secondary.Children.Insert(_secondary.Children.IndexOf(_volumeHost) + 1, _volume);
        }
        if (popup) UpdateVolumePopup();
    }

    private void UpdateVolumePopup()
    {
        if (!_volumeAsPopup) return;
        if (!TransientHover.WindowUsable(_volumeHost)) { _volumeCloseTimer.Stop(); SetVolumePopupVisible(false); return; }
        if (KeepVolumeOpen) { _volumeCloseTimer.Stop(); SetVolumePopupVisible(true); }
        else if (_volumePopup.IsVisible && !_volumeCloseTimer.IsEnabled) _volumeCloseTimer.Start();
    }

    private void SetVolumePopupVisible(bool visible)
    {
        if (_volumePopup.IsVisible == visible) return;
        // A hidden slider cannot keep the focus. One the pointer focused goes back to the owner of the keys (never to the speaker, whose
        // Space and Enter would toggle mute); one reached by keyboard hands it to the speaker beside it, so the Tab order is kept.
        if (!visible && _volume.IsFocused)
        {
            if (!_volumeKeyboardFocus && ShortcutFocusRequested != null) ShortcutFocusRequested();
            else _mute.Focus(NavigationMethod.Pointer);
        }
        _volumePopup.IsVisible = visible;
    }

    /// <summary>Opens or closes the volume above the speaker without a pointer (smokes and tests).</summary>
    internal void SetVolumePopupOpen(bool open)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (!_volumeAsPopup) return;
        _volumeCloseTimer.Stop();
        SetVolumePopupVisible(open);
    }

    // ---- A–B loop --------------------------------------------------------------------------------------------------

    /// <summary>Sets A, then B (the loop starts), then turns the loop off (key A, the A·B button).</summary>
    internal void ToggleAbLoop()
    {
        Dispatcher.UIThread.VerifyAccess();
        if (IsDisposed || !_abLoopButton.IsEnabled) { RefreshAbLoopPresentation(); return; }
        VideoAbLoopPress result = _abLoop.Press(_timeline.DisplayPosition, _length);
        _abLoopNotice = result == VideoAbLoopPress.TooShort ? Texts.T("viewer.video.abLoop.tooShort") : null;
        if (result != VideoAbLoopPress.Active) _loopJumpTimer.Stop();
        RefreshAbLoopPresentation();
        LayoutLoopMarks();
    }

    private void ClearAbLoop()
    {
        _loopJumpTimer.Stop();
        if (!_abLoop.IsSet && _abLoopNotice == null) return;
        _abLoop.Clear(); _abLoopNotice = null;
        RefreshAbLoopPresentation();
        LayoutLoopMarks();
    }

    // Called with every observed time of the playing medium. Reaching B returns to A at once; B close ahead schedules the
    // return at the moment playback reaches it, so the loop does not overshoot by a whole time report.
    private void CheckAbLoop(long position)
    {
        if (!_abLoop.IsActive || !_isPlaying || _timeline.HasGesture || _timeline.HasPendingSeek) return;
        if (_abLoop.ShouldReturn(position)) { _loopJumpTimer.Stop(); JumpToLoopStart(); return; }
        if (_abLoop.JumpDelay(position, _rate) is not long delay) return;
        _loopJumpTimer.Stop();
        _loopJumpGeneration = _generation;
        _loopJumpTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(1, delay));
        _loopJumpTimer.Start();
    }

    private void JumpWhenLoopEndReached()
    {
        if (!_abLoop.IsActive || !_isPlaying || _intent.Current.Mode != VideoPlaybackMode.Playing
            || _timeline.HasGesture || _timeline.HasPendingSeek) return;
        JumpToLoopStart();
    }

    // The same latest-seek path as the timeline and the seek keys: never a second native seek route.
    private bool JumpToLoopStart()
    {
        if (!_abLoop.IsActive || !CanSeekNow) return false;
        double value = Math.Clamp(_abLoop.Start * 1000d / _length, _seek.Minimum, _seek.Maximum);
        if (Math.Abs(value - _seek.Value) < 1e-9) return false;
        _seek.Value = value;
        return true;
    }

    private void LayoutLoopMarks()
    {
        bool shown = _abLoop.IsSet && _length > 0 && _seekTrack != null && _seekTrack.Bounds.Width > 0;
        _markStart.IsVisible = shown;
        _markEnd.IsVisible = shown && _abLoop.IsActive;
        _markRange.IsVisible = shown && _abLoop.IsActive;
        if (!shown) return;
        Track track = _seekTrack!;
        double thumb = track.Thumb?.Bounds.Width ?? 12;
        double usable = Math.Max(0, track.Bounds.Width - thumb);
        Point? origin = track.TranslatePoint(new Point(thumb / 2, track.Bounds.Height / 2), _seekMarks);
        if (origin is not { } start) { _markStart.IsVisible = _markEnd.IsVisible = _markRange.IsVisible = false; return; }
        double X(long ms) => start.X + Math.Clamp(ms / (double)_length, 0, 1) * usable;
        double a = X(_abLoop.Start), b = X(_abLoop.End);
        Canvas.SetLeft(_markStart, a - 1); Canvas.SetTop(_markStart, start.Y - 6);
        Canvas.SetLeft(_markEnd, b - 1); Canvas.SetTop(_markEnd, start.Y - 6);
        _markRange.Width = Math.Max(0, b - a);
        Canvas.SetLeft(_markRange, a); Canvas.SetTop(_markRange, start.Y - 1.5);
    }

    private void RefreshAbLoopPresentation()
    {
        _abLoopButton.IsChecked = _abLoop.IsSet;
        string state = _abLoop.State switch
        {
            VideoAbLoopState.StartSet => Texts.T("viewer.video.abLoop.aSet", FormatVideoTime(_abLoop.Start)),
            VideoAbLoopState.Active => Texts.T("viewer.video.abLoop.active", FormatVideoTime(_abLoop.Start), FormatVideoTime(_abLoop.End)),
            _ => Texts.T("viewer.video.abLoop.off")
        };
        string next = Texts.T(_abLoop.State switch
        {
            VideoAbLoopState.StartSet => "viewer.video.abLoop.nextB",
            VideoAbLoopState.Active => "viewer.video.abLoop.nextOff",
            _ => "viewer.video.abLoop.nextA"
        });
        string? reason = _repeatOwnedBySlideshow ? _repeatOwnedReason : null;
        string name = _abLoopNotice ?? state;
        string tip = name + " · " + CommandBindings.Tooltip("video.ab-loop", reason, next);
        AutomationProperties.SetName(_abLoopButton, name);
        AutomationProperties.SetHelpText(_abLoopButton, tip);
        ToolTip.SetTip(_abLoopButton, tip);
    }

    // ---- next frame --------------------------------------------------------------------------------------------------

    /// <summary>Pauses (when playing) and shows the next frame (key "."). The backend has no step back.</summary>
    internal async Task StepFrameAsync()
    {
        Dispatcher.UIThread.VerifyAccess();
        if (IsDisposed || !_frameStep.IsEnabled) return;
        long generation = _generation;
        _loopJumpTimer.Stop();
        if (VideoFrameStepPolicy.PausesFirst(_intent.Current.Mode)) await SetPlaybackModeAsync(VideoPlaybackMode.Paused);
        if (!IsCurrent(generation) || _intent.Current.Mode != VideoPlaybackMode.Paused) return;
        await CommandAsync(playback => playback.Player.NextFrame());
        await Task.Delay(FrameReadbackDelayMs);
        if (!IsCurrent(generation)) return;
        long revision = _timeline.Revision;
        await CommandAsync(playback =>
        {
            long time = Math.Max(0, playback.Player.Time);
            Post(generation, () => { if (!_ended && _timeline.Observe(generation, revision, time)) RenderTime(); });
        });
    }

    // ---- audio tracks ------------------------------------------------------------------------------------------------

    // Read on our native worker after track and Playing events (rare), never per time tick.
    private void RequestAudioTrackRefresh(long generation, MediaPlayer expectedPlayer) => _ = CommandAsync(playback =>
    {
        if (!ReferenceEquals(playback.Player, expectedPlayer)) return;
        IReadOnlyList<VideoAudioTrackChoice> tracks;
        int current;
        try
        {
            tracks = VideoAudioTracks.Real(playback.Player.AudioTrackDescription.Select(track => (track.Id, (string?)track.Name)));
            current = playback.Player.AudioTrack;
        }
        catch (Exception) { return; } // A track list is a convenience; it never fails the playback.
        Post(generation, () => ObserveAudioTracks(tracks, current));
    });

    internal void ObserveAudioTracks(IReadOnlyList<VideoAudioTrackChoice> tracks, int current)
    {
        _audioTrackChoices = tracks;
        _audioTrackCurrent = current;
    }

    private void SelectAudioTrack(int id)
    {
        if (IsDisposed || !_playPause.IsEnabled) return;
        long generation = _generation;
        if (_selectAudioTrackOverride is { } select)
        {
            ApplySelection(select(id));
            return;
        }
        _ = CommandAsync(playback =>
        {
            int current;
            try
            {
                if (!playback.Player.SetAudioTrack(id)) return;
                current = playback.Player.AudioTrack;
            }
            catch (Exception) { return; }
            ApplySelection(current);
        });

        void ApplySelection(int? current)
        {
            if (current is int selected) Post(generation, () => _audioTrackCurrent = selected);
        }
    }

    /// <summary>Switches to the next audio track of a medium that has more than one (command "Next audio track").</summary>
    internal void CycleAudioTrack()
    {
        Dispatcher.UIThread.VerifyAccess();
        if (VideoAudioTracks.Next(_audioTrackChoices, _audioTrackCurrent) is int next) SelectAudioTrack(next);
    }

    private string AudioTrackLabel(VideoAudioTrackChoice track, int index) =>
        string.IsNullOrWhiteSpace(track.Name) ? Texts.T("viewer.video.audioTrack.item", index + 1) : track.Name;

    // ---- speed and player menu ---------------------------------------------------------------------------------------

    private void BuildSpeedMenus()
    {
        foreach (float rate in VideoRates.Choices)
        {
            _speedMenuRates.Add((RateItem(rate, "GalleryVideoSpeedChoices"), rate));
            _playerMenuRates.Add((RateItem(rate, "GalleryVideoMenuSpeedChoices"), rate));
        }
        foreach (var (item, _) in _speedMenuRates) _speedMenu.Items.Add(item);
        foreach (var (item, _) in _playerMenuRates) _playerMenuSpeed.Items.Add(item);
        _speedMenu.Opening += (_, _) => RefreshRateItems(_speedMenuRates);
        _speed.ContextMenu = _speedMenu;
    }

    private MenuItem RateItem(float rate, string group)
    {
        MenuItem item = new() { Header = RateLabel(rate), ToggleType = MenuItemToggleType.Radio, GroupName = group };
        item.Click += (_, _) => ChooseRate(rate);
        return item;
    }

    private void RefreshRateItems(List<(MenuItem Item, float Rate)> items)
    {
        foreach (var (item, rate) in items)
        {
            item.Header = RateLabel(rate);
            item.IsChecked = VideoRates.Same(rate, _rate);
            item.IsEnabled = _speed.IsEnabled;
        }
    }

    /// <summary>Opens the speed choices at the speed button (command "Choose speed…").</summary>
    internal void OpenSpeedMenu()
    {
        Dispatcher.UIThread.VerifyAccess();
        if (IsDisposed || !_speed.IsEnabled || !_speed.IsEffectivelyVisible) return;
        // ContextMenu.Open does not raise Opening: compose the checkmarks, labels and enabled state first.
        RefreshRateItems(_speedMenuRates);
        _speedMenu.Open(_speed);
    }

    private bool CanSaveFrame => !IsDisposed && !_lifetime.HasCleanupPending && _playPause.IsEnabled && SaveFrameRequested != null;

    private void BuildPlayerMenu()
    {
        _playerMenuButton.Content = WorkspaceIcons.Create(WorkspaceIcons.More);
        _playerMenuSpeed.Icon = CommandBindings.Icon("video.speed-choose");
        _playerMenuAudio.Icon = CommandBindings.Icon("video.audio-track");
        _playerMenuSaveFrame = CommandBindings.CreateMenuItem("video.snapshot", () => SaveFrameRequested?.Invoke(), () => CanSaveFrame);
        _playerMenuRotate = CommandBindings.CreateMenuItem("gallery.rotate", () => RotateRequested?.Invoke(), () => CanRotate);
        _playerMenuRepeat = CommandBindings.CreateMenuItem("video.loop", ToggleLoop, () => _loop.IsEnabled,
            () => _repeatOwnedBySlideshow ? _repeatOwnedReason : null);
        _playerMenuRepeat.ToggleType = MenuItemToggleType.CheckBox;
        _playerMenu.Items.Add(_playerMenuSpeed);
        _playerMenu.Items.Add(_playerMenuAudio);
        _playerMenu.Items.Add(_playerMenuSaveFrame);
        _playerMenu.Items.Add(_playerMenuRotate);
        _playerMenu.Items.Add(_playerMenuRepeat);
        _playerMenu.Opening += (_, _) => RefreshPlayerMenu();
        _playerMenuButton.Flyout = _playerMenu;
    }

    private void RefreshPlayerMenu()
    {
        _playerMenuSpeed.IsEnabled = _speed.IsEnabled;
        // The audio track entry exists only for a medium with more than one track.
        _playerMenuAudio.Items.Clear();
        foreach (VideoAudioTrackChoice track in _audioTrackChoices)
        {
            MenuItem item = new()
            {
                Tag = track, ToggleType = MenuItemToggleType.Radio, GroupName = "GalleryVideoAudioTracks",
                IsChecked = track.Id == _audioTrackCurrent
            };
            item.Click += (_, _) => SelectAudioTrack(track.Id);
            _playerMenuAudio.Items.Add(item);
        }
        _playerMenuAudio.IsVisible = _audioTrackChoices.Count > 1;
        _playerMenuAudio.IsEnabled = _playPause.IsEnabled;
        RefreshPlayerMenuLabels();
        _playerMenuRepeat.IsChecked = _intent.LoopEnabled;
    }

    private void RefreshPlayerMenuLabels()
    {
        _playerMenuLanguage = Texts.Language;
        _playerMenuSpeed.Header = Texts.T("viewer.video.speedMenu");
        _playerMenuAudio.Header = Texts.T("viewer.video.audioTrack");
        RefreshRateItems(_playerMenuRates);
        for (int index = 0; index < _playerMenuAudio.Items.Count; index++)
        {
            var item = (MenuItem)_playerMenuAudio.Items[index]!;
            item.Header = AudioTrackLabel((VideoAudioTrackChoice)item.Tag!, index);
        }
        CommandBindings.Refresh(_playerMenuSaveFrame);
        CommandBindings.Refresh(_playerMenuRotate);
        CommandBindings.Refresh(_playerMenuRepeat);
    }

    // ---- presentation, enabled state, medium change ------------------------------------------------------------------

    private void UpdateTransportExtrasEnabled()
    {
        bool seekable = CanSeekNow;
        _abLoopButton.IsEnabled = seekable && !_repeatOwnedBySlideshow;
        _frameStep.IsEnabled = VideoFrameStepPolicy.CanStep(seekable, _ended, _intent.Current.Mode);
        _playerMenuButton.IsEnabled = _playPause.IsEnabled;
    }

    private void RefreshTransportExtras()
    {
        if (_playerMenu.IsOpen && _playerMenuLanguage != Texts.Language) RefreshPlayerMenuLabels();
        UpdateTransportExtrasEnabled();
        RefreshAbLoopPresentation();
        _frameStep.Content = TransportIcon("video.frame-step", 18);
        string frameTip = CommandBindings.Tooltip("video.frame-step", keyContext: ShortcutContext) + " · " + Texts.T("viewer.video.frameStepHelp");
        AutomationProperties.SetName(_frameStep, CommandCatalog.Label("video.frame-step"));
        AutomationProperties.SetHelpText(_frameStep, frameTip);
        ToolTip.SetTip(_frameStep, frameTip);
        bool fullscreen = _ownerWindow?.WindowState == WindowState.FullScreen;
        _fullscreen.Content = TransportIcon("window.fullscreen", 16, fullscreen ? "fullscreen" : "windowed");
        string fullscreenTip = CommandBindings.Tooltip("window.fullscreen");
        AutomationProperties.SetName(_fullscreen, CommandCatalog.Label("window.fullscreen"));
        AutomationProperties.SetHelpText(_fullscreen, fullscreenTip);
        ToolTip.SetTip(_fullscreen, fullscreenTip);
        string menu = Texts.T("viewer.video.menu");
        AutomationProperties.SetName(_playerMenuButton, menu);
        ToolTip.SetTip(_playerMenuButton, menu);
        if (_volumeAsPopup && _minimalBar == null && ToolTip.GetTip(_mute) is string muteTip)
        {
            muteTip += " · " + Texts.T("viewer.video.volumeHelp");
            AutomationProperties.SetHelpText(_mute, muteTip);
            ToolTip.SetTip(_mute, muteTip);
        }
    }

    private void ResetTransportExtras()
    {
        if (_playerMenu.IsOpen) _playerMenu.Hide();
        _abLoop.Clear(); _abLoopNotice = null;
        _loopJumpTimer.Stop();
        _audioTrackChoices = Array.Empty<VideoAudioTrackChoice>(); _audioTrackCurrent = -1;
        LayoutLoopMarks();
    }

    private void AttachOwnerWindowExtras()
    {
        if (_ownerWindow != null) _ownerWindow.PropertyChanged += OnOwnerWindowPropertyChanged;
    }

    private void DetachOwnerWindowExtras()
    {
        if (_ownerWindow != null) _ownerWindow.PropertyChanged -= OnOwnerWindowPropertyChanged;
        _volumeCloseTimer.Stop();
        _loopJumpTimer.Stop();
    }

    private void OnOwnerWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == Window.WindowStateProperty) RefreshTransportPresentation();
    }
}
