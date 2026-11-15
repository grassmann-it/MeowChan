using System;
using System.Linq;
using System.Threading.Tasks;
using MeowChan.Desktop.Commands;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using MeowChan.Gallery;
using MeowChan.Desktop.Localization;

namespace MeowChan.Desktop;

internal sealed partial class GalleryViewer
{
    private GalleryPlaybackPreferences _playbackPreferences = new();
    private GallerySlideshowController _slideshow = null!;
    private readonly DispatcherTimer _interactionTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private bool _updatingPlaybackControls;
    private bool _interactionAvailable = true;
    private bool _slideshowSurface = true;
    private readonly GallerySlideshowWindowGate _windowGate = new();
    private bool _viewerInteractionStopped;
    private bool _navigationPending;
    private bool _mediaFailed;
    private int _imageReadyRequest = -1, _videoReadyRequest = -1;
    private bool _lastRunning;
    private bool _slideshowStarted;
    private bool _selectionVideoPaused;
    private SlideshowStatus _shownSlideshowStatus;
    private readonly TextBlock _slideshowStatus = new() { Name = "GallerySlideshowStatus", TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(8, 0, 8, 0), IsVisible = false };
    private long _slideshowGeneration;
    private Action? _videoEndedHandler, _videoSeekHandler, _videoFailedHandler;
    private Action<string>? _videoNoticeHandler;
    private Action<bool>? _videoPlaybackHandler;
    private readonly ComboBox _playbackMode = new() { Name = "GalleryPlaybackModeSelector", MinWidth = 120 };
    // How long each image stays in the slideshow (1–300 s, typed or stepped ±1, PageUp/PageDown ±10); shown only in slideshow
    // mode, written through the same preferences as the settings page.
    private readonly AccessibleNumberInput _slideshowSeconds = SlideshowNumber("GallerySlideshowSecondsInput",
        GalleryPlaybackPreferences.ImageSecondsMinimum, GalleryPlaybackPreferences.ImageSecondsMaximum);
    // "Repeat video" for the shown video outside a slideshow; the transport's loop button writes the same preference.
    private readonly ToggleButton _videoRepeatToggle = new() { Name = "GalleryVideoRepeatToggle" };
    // What a running slideshow does with a video, and the number its rule needs (seconds or passes).
    private readonly ComboBox _videoRule = new() { Name = "GallerySlideshowVideoRuleSelector", MinWidth = 120, IsVisible = false };
    private readonly AccessibleNumberInput _videoSeconds = SlideshowNumber("GallerySlideshowVideoSecondsInput",
        Config.GallerySlideshowVideoSecondsMinimum, Config.GallerySlideshowVideoSecondsMaximum);
    private readonly AccessibleNumberInput _videoRepeats = SlideshowNumber("GallerySlideshowVideoRepeatsInput",
        Config.GallerySlideshowVideoRepeatsMinimum, Config.GallerySlideshowVideoRepeatsMaximum);
    private readonly TextBlock _videoRuleCaption = new() { Classes = { "workspace-meta" }, Margin = OverflowCaptionMargin, IsVisible = false };
    private readonly TextBlock _videoValueCaption = new() { Classes = { "workspace-meta" }, Margin = OverflowCaptionMargin, IsVisible = false };
    private readonly TextBlock _reducedMotionNote = new() { Name = "GallerySlideshowReducedMotionNote", Classes = { "workspace-help" },
        Margin = OverflowNoteMargin, TextWrapping = TextWrapping.Wrap, MaxWidth = 300, IsVisible = false };
    // "Pause when MeowChan loses focus" (off by default): a row of the slideshow settings, shown with the other slideshow choices.
    private readonly ToggleButton _pauseOnFocusLossToggle = new() { Name = "GallerySlideshowPauseOnFocusLossToggle" };
    private readonly ToggleButton _slideshowToggle = new() { Name = "GallerySlideshowToggle" };
    private readonly ToggleButton _overlayToggle = new() { Name = "GalleryOverlayToggle" };
    private readonly ToggleButton _autoHideToggle = new() { Name = "GalleryAutoHideToggle" };
    private readonly ToggleButton _postOverlayToggle = new() { Name = "GalleryPostOverlayToggle" };
    private readonly TextBlock _overlayFallbackNotice = new() { Name = "GalleryOverlayFallbackNotice",
        IsVisible = false, TextWrapping = TextWrapping.Wrap };

    internal event Action<GalleryPlaybackPreferences>? PlaybackPreferencesChanged;

    internal void ConfigurePlayback(GalleryPlaybackPreferences prefs)
    {
        ArgumentNullException.ThrowIfNull(prefs);
        prefs = prefs.Normalize();
        if (prefs == _playbackPreferences) return;
        bool modeChanged = _playbackPreferences.Mode != prefs.Mode;
        bool durationChanged = _playbackPreferences.ImageSeconds != prefs.ImageSeconds;
        if (modeChanged) { PauseSlideshow(pauseVideo: false); _slideshowStarted = false; } // Switching automation mode is not a native Pause command.
        bool enablingEdgeControls = prefs.OverlayControls && prefs.AutoHideControls
            && (!_playbackPreferences.OverlayControls || !_playbackPreferences.AutoHideControls);
        _playbackPreferences = prefs;
        if (enablingEdgeControls) _overlayHidden = true;
        if (modeChanged) _slideshow.SetMode(prefs.Mode);
        if (durationChanged) _slideshow.SetImageDuration(TimeSpan.FromSeconds(prefs.ImageSeconds));
        ApplySlideshowVideoRule();
        ApplyEffectiveVideoLoop();
        SetMediaFocus(prefs.MediaFocus, notify: false);
        ApplyOverlayPlacement();
        RefreshPlaybackControls();
        // Applying a saved mode is not a Start request, including after focus returns.
    }

    /// <param name="available">The window is the active one and usable: the viewer's own input, overlay and presentation follow it.</param>
    /// <param name="slideshowSurface">The gallery can be worked with at all (main window enabled, gallery not put away for the reader), whatever the focus;
    /// without it the slideshow stops waiting even when "Pause when MeowChan loses focus" is off. Defaults to <paramref name="available"/>.</param>
    internal void SetInteractionAvailable(bool available, bool? slideshowSurface = null)
    {
        bool surface = slideshowSurface ?? available;
        if (_interactionAvailable == available && _slideshowSurface == surface) return;
        bool availableChanged = _interactionAvailable != available;
        _interactionAvailable = available;
        _slideshowSurface = surface;
        ObserveSlideshowWindow();
        if (availableChanged) UpdateGifPresentation();
        if (!SlideshowWindowAllows) HoldSlideshowAdvance();
        if (!available && availableChanged)
        {
            _pressedPointers.Clear();
            RevealOverlayControls();
        }
        RefreshPlaybackControls();
    }

    // Config controls selected-video playback, never progression. A running slideshow is a
    // separate explicit request to play its selected videos, even when normal autoplay is off.
    private bool EffectiveVideoAutoplay => GalleryPlaybackPolicy.ShouldPlaySelection(_videoAutoplay, _slideshow.IsRunning, _selectionVideoPaused);

    private void InitializePlaybackControls(Panel toolbar, StackPanel focusTools)
    {
        _slideshow = new GallerySlideshowController(RequestSlideshowNext, requestReplay: RequestSlideshowReplay);
        _slideshow.StateChanged += ApplyEffectiveVideoLoop; // Running or not decides who repeats a video.
        _slideshow.SetMode(_playbackPreferences.Mode);
        _slideshow.SetImageDuration(TimeSpan.FromSeconds(_playbackPreferences.ImageSeconds));
        ApplySlideshowVideoRule();
        // Playback choices are rows of the "⋯" menu: "Repeat video", then a caption above each slideshow choice, then the
        // slideshow control (it moves to the toolbar while a slideshow is chosen, see PlaceSlideshowToggle).
        _slideshowHome = new StackPanel { Name = "GalleryPlaybackTools", Spacing = 2 };
        AddOverflowItem(_videoRepeatToggle, "video.loop", check: true, keepOpen: true,
            label: () => GalleryPlaybackCatalog.Get("viewer.playback.repeat"), host: _slideshowHome);
        _videoRepeatToggle.Click += (_, _) => ChangePlaybackPreferences(_playbackPreferences with { VideoRepeat = !_playbackPreferences.VideoRepeat });
        // One inset for everything in the menu: a choice box starts where a row's lead mark starts and ends where its shortcut ends,
        // a caption is a field label of the box below it (Space.4 between them), and the box leaves Space.4 before the next row.
        foreach (Control choice in new Control[] { _playbackMode, _slideshowSeconds, _videoRule, _videoSeconds, _videoRepeats })
        { choice.HorizontalAlignment = HorizontalAlignment.Stretch; choice.Margin = OverflowChoiceMargin; }
        _slideshowHome.Children.Add(_modeCaption);
        _slideshowHome.Children.Add(_playbackMode);
        _slideshowHome.Children.Add(_secondsCaption);
        _slideshowHome.Children.Add(_slideshowSeconds);
        _slideshowHome.Children.Add(_videoRuleCaption);
        _slideshowHome.Children.Add(_videoRule);
        _slideshowHome.Children.Add(_videoValueCaption);
        _slideshowHome.Children.Add(_videoSeconds);
        _slideshowHome.Children.Add(_videoRepeats);
        _slideshowHome.Children.Add(_reducedMotionNote);
        AddOverflowItem(_pauseOnFocusLossToggle, "gallery.slideshow-pause-on-focus-loss", check: true, keepOpen: true,
            label: () => GalleryPlaybackCatalog.Get("viewer.playback.pauseOnFocusLoss"), host: _slideshowHome);
        _pauseOnFocusLossToggle.Click += (_, _) => ChangePlaybackPreferences(_playbackPreferences with { PauseOnFocusLoss = !_playbackPreferences.PauseOnFocusLoss });
        void Number(AccessibleNumberInput input, Func<int, GalleryPlaybackPreferences> change) => input.ValueChanged += (_, e) =>
        {
            if (!_updatingPlaybackControls && e.NewValue is decimal value && value >= (decimal)input.Minimum && value <= (decimal)input.Maximum)
                ChangePlaybackPreferences(change(decimal.ToInt32(decimal.Round(value))));
        };
        Number(_slideshowSeconds, seconds => _playbackPreferences with { ImageSeconds = seconds });
        Number(_videoSeconds, seconds => _playbackPreferences with { VideoSeconds = seconds });
        Number(_videoRepeats, repeats => _playbackPreferences with { VideoRepeats = repeats });
        foreach (ComboBox choice in new[] { _playbackMode, _videoRule })
        {
            choice.DropDownOpened += (_, _) => { HoldSlideshowAdvance(); RefreshPlaybackControls(); };
            choice.DropDownClosed += (_, _) => RefreshPlaybackControls();
        }
        _playbackMode.SelectionChanged += (_, _) =>
        {
            if (!_updatingPlaybackControls && _playbackMode.SelectedIndex is >= 0 and <= 1)
                ChangePlaybackPreferences(_playbackPreferences with { Mode = (GalleryPlaybackMode)_playbackMode.SelectedIndex });
        };
        _videoRule.SelectionChanged += (_, _) =>
        {
            if (!_updatingPlaybackControls && _videoRule.SelectedIndex is >= 0 and <= 2)
                ChangePlaybackPreferences(_playbackPreferences with { VideoRule = (SlideshowVideoRule)_videoRule.SelectedIndex });
        };
        foreach (var (button, id) in PlaybackActions())
        {
            CommandBindings.BindExisting(button, id);
            button.Click += async (_, _) => await ExecutePlaybackActionAsync(id);
        }
        InitializeSlideshowMenu(); // G2: the menu button and the flyout of the split control (GalleryViewer.SlideshowMenu.cs)
        _interactionTimer.Tick += (_, _) => OnViewerInteractionTick();
        RefreshPlaybackControls();
    }

    private (ToggleButton Button, string Id)[] PlaybackActions() => new[]
    {
        (_slideshowToggle, "gallery.slideshow-toggle"), (_overlayToggle, "gallery.overlay-toggle"),
        (_autoHideToggle, "gallery.overlay-autohide-toggle"), (_postOverlayToggle, "gallery.post-overlay-toggle")
    };

    internal bool CanExecutePlaybackAction(string id) => !_viewerInteractionStopped && id switch
    {
        "gallery.slideshow-toggle" => _slideshow.IsRunning || CanRunSlideshow && !_navigationPending,
        "gallery.overlay-toggle" or "gallery.overlay-autohide-toggle" or "gallery.post-overlay-toggle" => true,
        _ => false
    };

    internal async Task ExecutePlaybackActionAsync(string id)
    {
        if (!CanExecutePlaybackAction(id)) { RefreshPlaybackControls(); return; }
        switch (id)
        {
            case "gallery.slideshow-toggle":
                if (_slideshow.IsRunning) PauseSlideshow(pauseVideo: true);
                else
                {
                    if (_playbackPreferences.Mode != GalleryPlaybackMode.Slideshow)
                        ChangePlaybackPreferences(_playbackPreferences with { Mode = GalleryPlaybackMode.Slideshow });
                    StartSlideshowNavigation();
                    if (_slideshow.IsRunning && _gif != null) await _gif.PlayAsync();
                    if (_slideshow.IsRunning && _entry?.Kind == SavedMediaKind.Video && _video != null && _videoReadyRequest == _request)
                        await _video.PlayForSlideshowAsync();
                }
                break;
            case "gallery.overlay-toggle":
            case "gallery.overlay-autohide-toggle": // Compatibility alias, not a second visible setting.
                ChangePlaybackPreferences(_playbackPreferences.WithControlsOverlay(!_playbackPreferences.OverlayControls)); break;
            case "gallery.post-overlay-toggle": ChangePlaybackPreferences(_playbackPreferences with { OverlayPostContext = !_playbackPreferences.OverlayPostContext }); break;
        }
        RefreshPlaybackControls();
    }

    private SlideshowStatus CurrentSlideshowStatus => new SlideshowStatusInput(
        _playbackPreferences.Mode == GalleryPlaybackMode.Slideshow, _slideshow.IsRunning, _slideshowStarted, _slideshow.IsTerminal,
        _entry != null && _entry.Kind != SavedMediaKind.Other, _mediaFailed,
        _navigationPending || _entry != null && (_entry.Kind == SavedMediaKind.Video ? _videoReadyRequest != _request : _imageReadyRequest != _request),
        SlideshowBlockedByPanel, _helpOpen || IsFavoriteConfirmOpen, HasGalleryPopup || HasGalleryDialog,
        SlideshowWindowAllows && !_videoSuspended, false, Tagging: IsTagMode, TagHold: _taggingHold).Resolve();

    private string SlideshowStatusText(SlideshowStatus state)
    {
        return state switch
        {
            SlideshowStatus.Ready => GalleryPlaybackCatalog.Get("viewer.playback.status.ready"),
            SlideshowStatus.Running => GalleryPlaybackCatalog.Get("viewer.playback.status.running"),
            SlideshowStatus.Loading => GalleryPlaybackCatalog.Get("viewer.playback.status.loading"),
            SlideshowStatus.Context => GalleryPlaybackCatalog.Get("viewer.playback.status.context"),
            SlideshowStatus.Help => GalleryPlaybackCatalog.Get("viewer.playback.status.help"),
            SlideshowStatus.Menu => GalleryPlaybackCatalog.Get("viewer.playback.status.menu"),
            SlideshowStatus.Inactive => GalleryPlaybackCatalog.Get("viewer.playback.status.inactive"),
            SlideshowStatus.Ended => GalleryPlaybackCatalog.Get("viewer.playback.status.ended"),
            SlideshowStatus.NoMedia => GalleryPlaybackCatalog.Get("viewer.playback.status.noMedia"),
            SlideshowStatus.Failed => GalleryPlaybackCatalog.Get("viewer.playback.status.failed"),
            SlideshowStatus.SeekPaused => GalleryPlaybackCatalog.Get("viewer.playback.status.seekPaused"),
            SlideshowStatus.Paused => GalleryPlaybackCatalog.Get("viewer.playback.status.paused"),
            SlideshowStatus.TagHold => GalleryPlaybackCatalog.Get("viewer.playback.status.tagHold"),
            _ => string.Empty
        };
    }

    private void RefreshSlideshowStatus()
    {
        _shownSlideshowStatus = CurrentSlideshowStatus;
        // GL0: what the slideshow does is a hint (the layout's Hints, off with hidden tools); why it waits and a failure always show.
        bool shown = _playbackPreferences.Mode == GalleryPlaybackMode.Slideshow
            && (!GalleryChromePlan.IsHint(_shownSlideshowStatus) || _modeTools == null || BuildChromePlan().Hints);
        string text = ApplyStatusDwell(_shownSlideshowStatus, shown ? SlideshowStatusText(_shownSlideshowStatus) : string.Empty); // GL5c: "ready" fades out after the dwell time
        if (_slideshowStatus.Text != text) _slideshowStatus.Text = text;
        _slideshowStatus.IsVisible = text.Length > 0;
    }

    private void ChangePlaybackPreferences(GalleryPlaybackPreferences prefs)
    {
        prefs = prefs.Normalize();
        if (_updatingPlaybackControls || prefs == _playbackPreferences) return;
        ConfigurePlayback(prefs);
        RevealOverlayControls(); // Explicit user choice, not incidental pointer movement or media change.
        PlaybackPreferencesChanged?.Invoke(_playbackPreferences);
    }

    private void RefreshPlaybackControls()
    {
        if (_slideshow == null) return;
        _updatingPlaybackControls = true;
        try
        {
            RefreshPlaybackModeChoices(new[] { GalleryPlaybackCatalog.Get("viewer.playback.manual"), GalleryPlaybackCatalog.Get("viewer.playback.slideshow") });
            AutomationProperties.SetName(_playbackMode, GalleryPlaybackCatalog.Get("viewer.playback.mode"));
            ToolTip.SetTip(_playbackMode, GalleryPlaybackCatalog.Get("viewer.playback.modeHelp"));
            RefreshSlideshowNumbers();
            SetToggleLabel(_slideshowToggle, _slideshow.IsRunning ? GalleryPlaybackCatalog.Get("viewer.playback.pause") : GalleryPlaybackCatalog.Get("viewer.playback.start"));
            PresentSlideshowIcon();
            _slideshowToggle.IsChecked = _slideshow.IsRunning;
            _slideshowToggle.IsEnabled = _slideshow.IsRunning || CanRunSlideshow && !_navigationPending;
            SetToggleLabel(_overlayToggle, GalleryPlaybackCatalog.Get("viewer.playback.controlsOverlay"));
            SetToggleLabel(_autoHideToggle, GalleryPlaybackCatalog.Get("viewer.playback.autoHide"));
            ToolTip.SetTip(_autoHideToggle, GalleryPlaybackCatalog.Get("viewer.playback.autoHideHelp"));
            SetToggleLabel(_postOverlayToggle, GalleryPlaybackCatalog.Get("viewer.playback.postOverlay"));
            _overlayToggle.IsChecked = _playbackPreferences.OverlayControls;
            _autoHideToggle.IsChecked = _playbackPreferences.AutoHideControls;
            _autoHideToggle.IsEnabled = _playbackPreferences.OverlayControls && !_nativeOverlayFallback;
            _postOverlayToggle.IsChecked = _playbackPreferences.OverlayPostContext;
            _overlayFallbackNotice.Text = GalleryPlaybackCatalog.Get("viewer.playback.overlayUnavailable");
            foreach (var (button, id) in PlaybackActions())
            {
                button.IsEnabled = CanExecutePlaybackAction(id);
                string? reason = id == "gallery.slideshow-toggle" && !button.IsEnabled ? SlideshowStatusText(CurrentSlideshowStatus)
                    : id == "gallery.overlay-autohide-toggle" && !button.IsEnabled
                        ? GalleryPlaybackCatalog.Get("viewer.playback.enableOverlay") : null;
                // A menu row's content is its Grid (the row layout), never a text: the visible label is the row's label block.
                string label = LabelOf(button) ?? CommandCatalog.Label(id);
                string tip = CommandBindings.Tooltip(id, reason, label);
                ToolTip.SetTip(button, tip); AutomationProperties.SetHelpText(button, tip);
            }
            PresentSlideshowMenu();
            PlaceSlideshowToggle();
            RefreshOverflowItems();
            RefreshSlideshowStatus();
            ApplyChrome();
            _lastRunning = _slideshow.IsRunning;
        }
        finally { _updatingPlaybackControls = false; }
        UpdateViewerInteractionTimer();
    }

    // Compare resolved text rather than a language code: Texts.Changed also fires
    // for a same-code pack reload. Keep the existing ItemsSource on no-op ticks.
    // The semantic mode, not a transient ComboBox clear, owns the restored index.
    internal void RefreshPlaybackModeChoices(string[] labels)
    {
        bool wasUpdating = _updatingPlaybackControls;
        _updatingPlaybackControls = true;
        try
        {
            if (_playbackMode.ItemsSource is not string[] current || !current.SequenceEqual(labels, StringComparer.Ordinal))
                _playbackMode.ItemsSource = labels;
            if (_playbackMode.SelectedIndex != (int)_playbackPreferences.Mode)
                _playbackMode.SelectedIndex = (int)_playbackPreferences.Mode;
        }
        finally { _updatingPlaybackControls = wasUpdating; }
    }

    // GL3: the slideshow control shows a play icon while stopped and a pause icon while running, like the other tools (catalog icons from
    // the command's state icons). On the bar it is icon + text (icon only in the compact toolbar); in the "⋯" menu the row's lead icon is
    // swapped, the row keeps its text. The built content is kept while neither state, label nor density changed.
    private void PresentSlideshowIcon()
    {
        if (_slideshow == null) return;
        string key = CommandCatalog.IconForState("gallery.slideshow-toggle", _slideshow.IsRunning ? "running" : "stopped");
        if (_slideshowToggle.Tag is OverflowItem item)
        {
            if (item.Root.Children.Count == 0 || item.Root.Children[0].Tag as string == key) return;
            Border lead = (Border)CommandBindings.Icon("gallery.slideshow-toggle", 16, _slideshow.IsRunning ? "running" : "stopped");
            lead.HorizontalAlignment = HorizontalAlignment.Center;
            lead.Bind(Border.BackgroundProperty, lead.GetObservable(Avalonia.Controls.Documents.TextElement.ForegroundProperty));
            lead.Tag = key;
            item.Root.Children[0] = lead;
            return;
        }
        string label = AutomationProperties.GetName(_slideshowToggle) ?? string.Empty;
        string signature = key + "|" + _compactToolbar + "|" + label;
        if (_slideshowToggle.Content is Control { Tag: string shown } && shown == signature) return;
        Border icon = (Border)CommandBindings.Icon("gallery.slideshow-toggle", _compactToolbar ? 16 : 14, _slideshow.IsRunning ? "running" : "stopped");
        icon.Bind(Border.BackgroundProperty, icon.GetObservable(Avalonia.Controls.Documents.TextElement.ForegroundProperty));
        _slideshowToggle.Classes.Set("command-icon", true);
        if (_compactToolbar) { icon.Tag = signature; _slideshowToggle.Content = icon; return; }
        StackPanel content = new() { Orientation = Orientation.Horizontal, Spacing = 4, Tag = signature };
        content.Children.Add(icon);
        content.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
        _slideshowToggle.Content = content;
    }

    private static void SetToggleLabel(ToggleButton button, string label)
    {
        SetButtonLabel(button, label);
        AutomationProperties.SetName(button, label);
        ToolTip.SetTip(button, label);
    }

    private static AccessibleNumberInput SlideshowNumber(string name, int minimum, int maximum)
    {
        AccessibleNumberInput input = new()
        {
            Name = name, Minimum = minimum, Maximum = maximum, Increment = 1, FormatString = "0",
            ParsingNumberStyle = System.Globalization.NumberStyles.Integer, ClipValueToMinMax = true, IsVisible = false, MinWidth = 120
        };
        // ±1 with the arrows and the spinner (the control's own), ±10 with PageUp/PageDown.
        input.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.KeyModifiers != Avalonia.Input.KeyModifiers.None || e.Key is not (Avalonia.Input.Key.PageUp or Avalonia.Input.Key.PageDown)) return;
            decimal current = input.Value ?? (decimal)input.Minimum;
            input.Value = Math.Clamp(current + (e.Key == Avalonia.Input.Key.PageUp ? 10 : -10), (decimal)input.Minimum, (decimal)input.Maximum);
            e.Handled = true;
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        return input;
    }

    /// <summary>The rule a running slideshow applies to videos now: the chosen one, or "play once" while the
    /// operating system or the Animations setting asks for reduced motion (no automatic repetition then).</summary>
    internal SlideshowVideoRule EffectiveSlideshowVideoRule => Motion.MotionPolicy.IsOff ? SlideshowVideoRule.PlayOnce : _playbackPreferences.VideoRule;

    private void ApplySlideshowVideoRule() => _slideshow?.SetVideoRule(EffectiveSlideshowVideoRule,
        TimeSpan.FromSeconds(_playbackPreferences.VideoSeconds), _playbackPreferences.VideoRepeats);

    // The player repeats a video by itself only outside a running slideshow; inside, the slideshow's video rule decides
    // (two loops must never compete). The loop button then shows the saved choice as unavailable and says why.
    private void ApplyEffectiveVideoLoop()
    {
        if (_video == null || _slideshow == null) return;
        bool slideshowOwns = _slideshow.IsRunning;
        _video.SetRepeatOwnedBySlideshow(slideshowOwns, GalleryPlaybackCatalog.Get("viewer.playback.repeatInSlideshow"));
        _video.SetLoopEnabled(_playbackPreferences.VideoRepeat && !slideshowOwns);
    }

    private void SetNumber(AccessibleNumberInput input, int value, bool visible, string name, string? help = null)
    {
        if (input.Value != value) input.Value = value;
        input.IsVisible = visible;
        AutomationProperties.SetName(input, name);
        string tip = help == null ? name : name + " · " + help;
        AutomationProperties.SetHelpText(input, tip);
        ToolTip.SetTip(input, tip);
    }

    private void RefreshSlideshowNumbers()
    {
        bool slideshow = _playbackPreferences.Mode == GalleryPlaybackMode.Slideshow;
        SetNumber(_slideshowSeconds, _playbackPreferences.ImageSeconds, slideshow, Texts.T("gallery.slideshow.secondsLabel"),
            GalleryPlaybackCatalog.Get("viewer.playback.numberHelp"));
        string[] rules =
        {
            GalleryPlaybackCatalog.Get("viewer.playback.videoRule.once"), GalleryPlaybackCatalog.Get("viewer.playback.videoRule.atLeast"),
            GalleryPlaybackCatalog.Get("viewer.playback.videoRule.repeat")
        };
        bool wasUpdating = _updatingPlaybackControls;
        _updatingPlaybackControls = true;
        try
        {
            if (_videoRule.ItemsSource is not string[] current || !current.SequenceEqual(rules, StringComparer.Ordinal)) _videoRule.ItemsSource = rules;
            if (_videoRule.SelectedIndex != (int)_playbackPreferences.VideoRule) _videoRule.SelectedIndex = (int)_playbackPreferences.VideoRule;
        }
        finally { _updatingPlaybackControls = wasUpdating; }
        _videoRule.IsVisible = _videoRuleCaption.IsVisible = slideshow;
        string ruleName = GalleryPlaybackCatalog.Get("viewer.playback.videoRule");
        _videoRuleCaption.Text = ruleName;
        AutomationProperties.SetName(_videoRule, ruleName);
        string ruleHelp = GalleryPlaybackCatalog.Get("viewer.playback.videoRuleHelp");
        AutomationProperties.SetHelpText(_videoRule, ruleHelp);
        ToolTip.SetTip(_videoRule, ruleName + " · " + ruleHelp);
        bool seconds = slideshow && _playbackPreferences.VideoRule == SlideshowVideoRule.LoopAtLeastSeconds;
        bool repeats = slideshow && _playbackPreferences.VideoRule == SlideshowVideoRule.RepeatTimes;
        string secondsName = GalleryPlaybackCatalog.Get("viewer.playback.videoSeconds");
        string repeatsName = GalleryPlaybackCatalog.Get("viewer.playback.videoRepeats");
        SetNumber(_videoSeconds, _playbackPreferences.VideoSeconds, seconds, secondsName, GalleryPlaybackCatalog.Get("viewer.playback.videoSecondsHelp"));
        SetNumber(_videoRepeats, _playbackPreferences.VideoRepeats, repeats, repeatsName, GalleryPlaybackCatalog.Get("viewer.playback.numberHelp"));
        _videoValueCaption.IsVisible = seconds || repeats;
        _videoValueCaption.Text = seconds ? secondsName : repeats ? repeatsName : string.Empty;
        _reducedMotionNote.Text = GalleryPlaybackCatalog.Get("viewer.playback.reducedMotion");
        _reducedMotionNote.IsVisible = slideshow && Motion.MotionPolicy.IsOff && _playbackPreferences.VideoRule != SlideshowVideoRule.PlayOnce;
        _pauseOnFocusLossToggle.IsChecked = _playbackPreferences.PauseOnFocusLoss;
        _pauseOnFocusLossToggle.IsVisible = slideshow;
        string focusName = GalleryPlaybackCatalog.Get("viewer.playback.pauseOnFocusLoss");
        string focusTip = focusName + " · " + GalleryPlaybackCatalog.Get("viewer.playback.pauseOnFocusLossHelp");
        AutomationProperties.SetName(_pauseOnFocusLossToggle, focusName);
        AutomationProperties.SetHelpText(_pauseOnFocusLossToggle, focusTip);
        ToolTip.SetTip(_pauseOnFocusLossToggle, focusTip);
        _videoRepeatToggle.IsChecked = _playbackPreferences.VideoRepeat;
        string repeatTip = CommandBindings.Tooltip("video.loop", GalleryPlaybackCatalog.Get("viewer.playback.repeatHelp"),
            GalleryPlaybackCatalog.Get("viewer.playback.repeat"));
        ToolTip.SetTip(_videoRepeatToggle, repeatTip);
        AutomationProperties.SetName(_videoRepeatToggle, GalleryPlaybackCatalog.Get("viewer.playback.repeat"));
        AutomationProperties.SetHelpText(_videoRepeatToggle, repeatTip);
    }


    // The transport's loop button: the same "Repeat video" preference as the menu row; the playback mode stays as it is.
    private void OnVideoLoopChanged(bool enabled) => ChangePlaybackPreferences(_playbackPreferences with { VideoRepeat = enabled });

    // The slideshow's video rule asks for another pass of the same video: replay it from the start (silently, as the
    // slideshow's own playback). Selection and generation stay; a newer selection has already rebound these signals.
    private void RequestSlideshowReplay()
    {
        if (_video == null || _entry?.Kind != SavedMediaKind.Video || _navigationPending || _videoReadyRequest != _request) return;
        _ = _video.PlayForSlideshowAsync();
    }

    private void BeginSlideshowMedia(GalleryEntry entry)
    {
        UnbindVideoPlaybackSignals();
        _navigationPending = false; _mediaFailed = false; _selectionVideoPaused = false;
        _imageReadyRequest = _videoReadyRequest = -1;
        ApplySlideshowVideoRule(); // The reduced-motion answer may have changed since the last medium.
        _slideshow.BeginMedia(++_slideshowGeneration, entry.Kind == SavedMediaKind.Video, _next.IsEnabled);
        // Readiness may arrive before the next interaction tick while a panel is open.
        // Start held so no hidden/loading interval is charged to the next image.
        _slideshow.SetAdvanceHeld(_slideshowGeneration, true);
        if (entry.Kind == SavedMediaKind.Other) StopSlideshow();
        RefreshPlaybackControls();
    }

    private void MarkVisibleImageReady()
    {
        if (_imageReadyRequest == _request || _info == null || _image.Source == null || !ShownInWindow(_image)
            || _entry?.Kind == SavedMediaKind.Video || _navigationPending || _sessionCancellation.IsCancellationRequested) return;
        _imageReadyRequest = _request;
        _slideshow.ImageReady(_slideshowGeneration);
        RefreshPlaybackControls();
    }

    private void UpdateSlideshowAvailability(bool hasNext)
    {
        if (_slideshow == null || _navigationPending || _entry == null) return;
        // Availability changes do not re-arm an ended medium or restart its image clock.
        _slideshow.UpdateHasNext(_slideshowGeneration, hasNext);
    }

    private void BindVideoPlaybackSignals(int request)
    {
        if (_video == null) return;
        UnbindVideoPlaybackSignals();
        long generation = _slideshowGeneration;
        bool Current() => request == _request && generation == _slideshowGeneration;
        _videoEndedHandler = () =>
        {
            if (!Current() || _entry?.Kind != SavedMediaKind.Video || _navigationPending) return;
            ObserveSlideshowWindow();
            _slideshow.SetAdvanceHeld(generation, IsSlideshowAdvanceBlocked);
            _slideshow.VideoEnded(generation);
            RefreshPlaybackControls();
        };
        _videoPlaybackHandler = playing =>
        {
            if (!Current() || _playbackPreferences.Mode != GalleryPlaybackMode.Slideshow) return;
            if (playing)
            {
                _selectionVideoPaused = false;
                if (CanRunSlideshow && !_navigationPending && !_slideshow.IsRunning) StartSlideshowNavigation();
            }
            else PauseSlideshow(pauseVideo: false);
            RefreshPlaybackControls();
        };
        _videoSeekHandler = () =>
        {
            if (!Current()) return;
            _slideshow.SeekStarted(generation);
            HoldSlideshowAdvance();
            RefreshPlaybackControls();
        };
        _videoFailedHandler = () =>
        {
            if (!Current()) return;
            _mediaFailed = true;
            StopSlideshow();
        };
        _video.PlaybackEnded += _videoEndedHandler;
        _video.UserPlaybackRequested += _videoPlaybackHandler;
        _video.SeekInteractionStarted += _videoSeekHandler;
        _video.PlaybackFailed += _videoFailedHandler;
        _videoNoticeHandler = message =>
        {
            if (Current()) ReportDiagnostic("video", message, mediaUnavailable: true);
        };
        _video.DiagnosticNotice += _videoNoticeHandler;
    }

    private void UnbindVideoPlaybackSignals()
    {
        if (_video == null) return;
        if (_videoEndedHandler != null) _video.PlaybackEnded -= _videoEndedHandler;
        if (_videoPlaybackHandler != null) _video.UserPlaybackRequested -= _videoPlaybackHandler;
        if (_videoSeekHandler != null) _video.SeekInteractionStarted -= _videoSeekHandler;
        if (_videoFailedHandler != null) _video.PlaybackFailed -= _videoFailedHandler;
        if (_videoNoticeHandler != null) _video.DiagnosticNotice -= _videoNoticeHandler;
        _videoEndedHandler = _videoSeekHandler = _videoFailedHandler = null; _videoNoticeHandler = null; _videoPlaybackHandler = null;
    }

    private void StartSlideshowNavigation()
    {
        _selectionVideoPaused = false;
        _slideshowStarted = true;
        _slideshow.SetAdvanceHeld(_slideshowGeneration, IsSlideshowAdvanceBlocked);
        _slideshow.Start();
        if (_slideshow.IsRunning || _entry == null || _navigationPending) return;
        // A completed/consumed medium may be replayed only by this explicit user
        // request. Re-arm with a new identity, not a duplicate old Ended callback.
        _slideshow.BeginMedia(++_slideshowGeneration, _entry.Kind == SavedMediaKind.Video, _next.IsEnabled);
        if (_imageReadyRequest == _request) _slideshow.ImageReady(_slideshowGeneration);
        if (_entry.Kind == SavedMediaKind.Video && _videoReadyRequest == _request) _slideshow.VideoReady(_slideshowGeneration);
        if (_entry.Kind == SavedMediaKind.Video) BindVideoPlaybackSignals(_request);
        _slideshow.SetAdvanceHeld(_slideshowGeneration, IsSlideshowAdvanceBlocked);
        _slideshow.Start();
    }

    private void RequestSlideshowNext()
    {
        if (!CanRunSlideshow || _navigationPending || !_next.IsEnabled) { StopSlideshow(); return; }
        Move(1);
    }

    // UI interruption API shared with the overlay partial. These methods never issue a native
    // transport command. IsRunning retains explicit progression intent during temporary holds.
    private bool IsSlideshowAdvanceBlocked => !CanRunSlideshow || _navigationPending || _dragging
        || _pressedPointers.Count != 0 || _lastPointer?.Captured != null
        || _video?.IsSeekInteracting == true || _keyboardInteraction && _infoOverlay.IsKeyboardFocusWithin;

    private void HoldSlideshowAdvance()
    {
        _slideshow?.SetAdvanceHeld(_slideshowGeneration, true);
        UpdateViewerInteractionTimer();
    }

    private void UpdateSlideshowInteraction(bool blocked)
    {
        if (_slideshow == null) return;
        _slideshow.SetAdvanceHeld(_slideshowGeneration, blocked || IsSlideshowAdvanceBlocked);
        _slideshow.Tick();
    }

    private void PauseSlideshow(bool pauseVideo = false)
    {
        if (_slideshow == null) return;
        bool wasRunning = _slideshow.IsRunning;
        _slideshow.Pause();
        if (wasRunning && pauseVideo && _entry?.Kind == SavedMediaKind.Video)
        {
            // The explicit pause may precede ShowAsync admission while the previous input
            // drains. Carry it into this selection's initial policy, not the next selection.
            _selectionVideoPaused = true;
            if (_video != null) _ = _video.PauseAsync();
        }
        if (wasRunning) RefreshPlaybackControls();
    }

    private void StopSlideshow()
    {
        _slideshow?.Stop();
        RefreshPlaybackControls();
    }

    private void UpdateViewerInteractionTimer()
    {
        // A manual, docked gallery needs no periodic whole-window popup/focus scan.
        // Optional idle hiding and a running/pending slideshow own the single timer.
        bool waitingForImage = _playbackPreferences.Mode == GalleryPlaybackMode.Slideshow
            && !_mediaFailed && _entry != null && _entry.Kind != SavedMediaKind.Video
            && _entry.Kind != SavedMediaKind.Other && _imageReadyRequest != _request;
        bool hiding = OverlayControlsEffective && _playbackPreferences.AutoHideControls
            && !_overlayHidden && _interactionAvailable && IsGalleryWindowActive;
        bool needed = !_viewerInteractionStopped && _scaleOwner != null && _slideshow != null && (hiding || _slideshow.IsRunning || waitingForImage || _hoverContextOpen || _filmstripPeek);
        if (needed && !_interactionTimer.IsEnabled) _interactionTimer.Start();
        else if (!needed && _interactionTimer.IsEnabled) _interactionTimer.Stop();
    }

    private void StopViewerInteraction()
    {
        _viewerInteractionStopped = true;
        _gif?.SetActive(false);
        _interactionTimer.Stop();
        PauseSlideshow();
        StopSlideshow();
        _pressedPointers.Clear();
        _lastPointer = null;
        _lastIndicatorMotion = null;
        _lastPointerScreen = null; _controlsHot = _postHot = _pointerInControls = _pointerInPost = false;
        if (_hoverContextOpen) SetContextOpen(false, notify: false, hover: true);
        _revealPointerId = null; _swallowRevealTap = false;
        RevealOverlayControls();
    }
}
