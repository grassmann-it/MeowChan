using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Gallery;

namespace MeowChan.Desktop;

// Snapshot of actual UI activity. Opacity alone must never decide input ownership.
internal readonly record struct GalleryOverlayActivity(bool Overlay, bool AutoHide, bool Available,
    bool ActiveWindow, bool Focused, bool Captured, bool Popup, bool Reading, bool Notice = false, bool Hovered = false)
{
    internal bool CanHide(TimeSpan idle) => Overlay && AutoHide && Available && ActiveWindow
        // Notice is retained as a source-compatible observation, never an interaction hold.
        && !Focused && !Captured && !Popup && !Reading && !Hovered && idle >= TimeSpan.FromMilliseconds(500);
}

internal readonly record struct GalleryOverlayPlacement(bool Controls, bool PostContext)
{
    internal static GalleryOverlayPlacement Resolve(GalleryPlaybackPreferences prefs, bool nativeVideo,
        bool nativeOverlayActive, bool fallback) => fallback || nativeVideo && !nativeOverlayActive
        ? default : new(prefs.OverlayControls, prefs.OverlayPostContext);
}

internal sealed partial class GalleryViewer
{
    private readonly StackPanel _overlayFooter = new() { Name = "GalleryOverlayFooter", VerticalAlignment = VerticalAlignment.Bottom,
        HorizontalAlignment = HorizontalAlignment.Stretch, IsVisible = false };
    private readonly StackPanel _overlayTransportHost = new() { Name = "GalleryOverlayTransportHost", Margin = new Thickness(8, 4, 8, 4) };
    private readonly HashSet<IPointer> _pressedPointers = new();
    private IPointer? _lastPointer;
    private long _lastOverlayActivity = Stopwatch.GetTimestamp();
    private bool _overlayHidden;
    private long? _lastIndicatorMotion;
    private DispatcherTimer? _indicatorExpiryTimer;
    private bool _keyboardInteraction;
    private PixelPoint? _lastPointerScreen;
    private bool _controlsHot, _postHot, _pointerInControls, _pointerInPost;
    private bool _pointerOnControlsIndicator, _pointerOnPostIndicator;
    private bool _hoverContextOpen, _suppressPostHoverUntilExit;
    private long _lastPostActivity = Stopwatch.GetTimestamp();
    private bool _swallowRevealTap;
    private long? _revealPointerId;
    private bool _applyingOverlayPlacement;
    private GalleryOverlayPlacement EffectiveOverlayPlacement => GalleryOverlayPlacement.Resolve(_playbackPreferences,
        _entry?.Kind == SavedMediaKind.Video && OperatingSystem.IsWindows(), _nativeOverlayActive, _nativeOverlayFallback);
    private bool OverlayControlsEffective => EffectiveOverlayPlacement.Controls;
    private bool PostOverlayEffective => EffectiveOverlayPlacement.PostContext;
    private Panel OverlayHost => _nativeOverlayActive ? _nativeVideoOverlay : _visualRoot;

    private bool HasGalleryPopup => Commands.CommandRouter.HasOpenPopup((Control?)TopLevel.GetTopLevel(this) ?? this)
        || Commands.CommandRouter.HasOpenPopup((Control?)TopLevel.GetTopLevel(_nativeVideoOverlay) ?? _nativeVideoOverlay);

    private bool HasGalleryDialog => TopLevel.GetTopLevel(this) is Window host
        && host.OwnedWindows.Any(window => window.IsVisible && !OwnsOverlayWindow(window));

    private bool IsGalleryWindowActive
    {
        get
        {
            if (TopLevel.GetTopLevel(this) is not Window host || !host.IsEnabled || !IsEffectivelyVisible || !IsEffectivelyEnabled) return false;
            return host.IsActive || _nativeOverlayActive && TopLevel.GetTopLevel(_nativeVideoOverlay) is Window overlay
                && overlay.IsActive && overlay.IsEnabled && overlay.IsVisible;
        }
    }

    private bool CanRunSlideshow => !_viewerInteractionStopped && _interactionAvailable && IsGalleryWindowActive && !HasGalleryDialog && !HasGalleryPopup
        && !_videoSuspended && !_contextOpen && !_helpOpen && !_mediaFailed && !_sessionCancellation.IsCancellationRequested
        && _entry != null && _entry.Kind != SavedMediaKind.Other;

    private readonly Button _controlsEdgeIndicator = CreateEdgeIndicator(post: false);
    private readonly Button _postEdgeIndicator = CreateEdgeIndicator(post: true);

    // Overlaid in the media cell, never in an Auto footer row. A small visible
    // chevron retains a full 44px keyboard/touch target; focus uses the glyph only.
    internal static Button CreateEdgeIndicator(bool post)
    {
        var indicator = new Button
        {
            Name = post ? "GalleryPostEdgeIndicator" : "GalleryControlsEdgeIndicator", Content = post ? "‹" : "⌃",
            Width = 44, Height = 44, MinWidth = 44, MinHeight = 44,
            HorizontalAlignment = post ? HorizontalAlignment.Right : HorizontalAlignment.Center,
            VerticalAlignment = post ? VerticalAlignment.Center : VerticalAlignment.Bottom,
            HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4), Padding = new Thickness(0),
            Background = Brushes.Transparent, BorderBrush = Brushes.Transparent, BorderThickness = new Thickness(0),
            FontSize = 24, Focusable = true, FocusAdorner = null, IsVisible = false, ZIndex = 15,
            // Own the complete template: theme hover/pressed presenters must never
            // introduce a filled rectangle or white border around these hints.
            Template = new FuncControlTemplate<Button>((button, _) => new TextBlock
            {
                Text = button.Content?.ToString(), FontSize = 24,
                Foreground = new SolidColorBrush(Color.FromArgb(100, 150, 150, 150)),
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false, Opacity = 0
            })
        };
        SetEdgeIndicatorLabel(indicator, EdgeIndicatorLabel(post));
        return indicator;
    }

    private static string EdgeIndicatorLabel(bool post) => post
        ? GalleryViewerText.Get("Show post context", "Beitragskontext anzeigen")
        : GalleryViewerText.Get("Show gallery controls", "Galerie-Steuerung anzeigen");

    private bool HasSelectedPostText => _contextOpen && GalleryEdgeHover.HasSelection(_contextPanel);
    private bool HasSelectedControlsText => GalleryEdgeHover.HasSelection(_chrome) || GalleryEdgeHover.HasSelection(_infoOverlay)
        || _video?.TransportControl is Control transport && GalleryEdgeHover.HasSelection(transport);

    private void InitializeEdgeIndicators()
    {
        foreach (Button indicator in new[] { _controlsEdgeIndicator, _postEdgeIndicator })
        {
            indicator.GotFocus += (_, _) => UpdateEdgeIndicatorArtwork();
            indicator.LostFocus += (_, _) => UpdateEdgeIndicatorArtwork();
        }
        _controlsEdgeIndicator.Click += (_, e) =>
        {
            RevealOverlayControls();
            FocusRevealedPanel(_overlayFooter);
            e.Handled = true;
        };
        _postEdgeIndicator.Click += (_, e) =>
        {
            // Transient access, like the edge: do not persist a hover/open preference.
            OpenEdgePostContext();
            _lastPostActivity = Stopwatch.GetTimestamp();
            FocusRevealedPanel(_contextPanel);
            UpdateViewerInteractionTimer();
            e.Handled = true;
        };
    }

    private void OpenEdgePostContext()
    {
        // SetContextOpen also stops advancement. Stop it here without changing the
        // native playing/paused intent, even with the legacy PauseSlideshow default.
        PauseSlideshow(pauseVideo: false);
        SetContextOpen(true, notify: false, hover: true);
    }

    private void FocusRevealedPanel(Control panel)
    {
        var method = _keyboardInteraction ? NavigationMethod.Tab : NavigationMethod.Pointer;
        Control? target = panel.GetVisualDescendants().OfType<Control>()
            .FirstOrDefault(control => control.Focusable && control.IsEffectivelyVisible && control.IsEffectivelyEnabled);
        if (target != null) target.Focus(method);
        else
        {
            bool keyboard = _keyboardInteraction;
            FocusGallerySurface();
            _keyboardInteraction = keyboard;
        }
    }

    private void UpdateEdgeIndicators()
    {
        // Buttons have no native resources to Dispose. On stop/detach release their
        // parent and any tooltip popup; do not reparent them into a closing window.
        if (_viewerInteractionStopped)
        {
            _pointerOnControlsIndicator = _pointerOnPostIndicator = false;
            _indicatorExpiryTimer?.Stop();
            foreach (Button indicator in new[] { _controlsEdgeIndicator, _postEdgeIndicator })
            {
                ToolTip.SetIsOpen(indicator, false);
                indicator.IsVisible = false;
                if (indicator.Parent is Panel parent) parent.Children.Remove(indicator);
            }
            return;
        }
        // Same instances follow the controls into/out of the owned native overlay.
        // Unsupported native overlays expose docked controls, not unusable handles.
        foreach (Button indicator in new[] { _controlsEdgeIndicator, _postEdgeIndicator })
        {
            MoveOverlayControl(indicator, OverlayHost);
            Grid.SetRow(indicator, 0); Grid.SetRowSpan(indicator, 1);
        }
        // Keep a hovered handle hit-testable through reveal until the pointer leaves.
        // Otherwise its pending click/tap could land on the media or transport below.
        bool controls = OverlayControlsEffective && (_overlayHidden || _pointerOnControlsIndicator);
        bool post = PostOverlayEffective && (!_contextOpen || _pointerOnPostIndicator) && _entry != null;
        UpdateEdgeIndicatorArtwork();
        bool controlsHadFocus = _controlsEdgeIndicator.IsKeyboardFocusWithin;
        bool postHadFocus = _postEdgeIndicator.IsKeyboardFocusWithin;
        _controlsEdgeIndicator.IsVisible = controls;
        _postEdgeIndicator.IsVisible = post;
        double transportInset = OverlayControlsEffective && _overlayFooter.IsVisible ? _overlayFooter.Bounds.Height + 8
            : !_nativeOverlayActive && _entry?.Kind == SavedMediaKind.Video ? _video?.TransportBottomInset ?? 0 : 0;
        _postEdgeIndicator.Margin = new Thickness(4, 4, 4, transportInset + 4);
        SetEdgeIndicatorLabel(_controlsEdgeIndicator, EdgeIndicatorLabel(post: false));
        SetEdgeIndicatorLabel(_postEdgeIndicator, EdgeIndicatorLabel(post: true));
        // A Tab reveal or placement change must not leave focus on a hidden handle.
        if (controlsHadFocus && !controls) FocusRevealedPanel(OverlayControlsEffective ? _overlayFooter : _chrome);
        if (postHadFocus && !post)
        {
            if (_contextOpen) FocusRevealedPanel(_contextPanel);
            else FocusGallerySurface();
        }
    }

    private void UpdateEdgeIndicatorArtwork()
    {
        TimeSpan? age = _lastIndicatorMotion is long motion ? Stopwatch.GetElapsedTime(motion) : null;
        foreach (Button indicator in new[] { _controlsEdgeIndicator, _postEdgeIndicator })
        {
            indicator.ApplyTemplate();
            if (indicator.GetVisualChildren().OfType<TextBlock>().FirstOrDefault() is TextBlock chevron)
                chevron.Opacity = GalleryEdgeMotion.IsVisible(age, indicator.IsKeyboardFocusWithin) ? 1 : 0;
        }
    }

    private static void SetEdgeIndicatorLabel(Button indicator, string label)
    {
        AutomationProperties.SetName(indicator, label);
        AutomationProperties.SetHelpText(indicator, label);
        ToolTip.SetTip(indicator, label);
    }

    private void InitializeOverlayInteraction()
    {
        // Install once, on both managed roots. No OS hook and no duplicate control handlers.
        foreach (Control surface in new Control[] { this, _nativeVideoOverlay })
        {
            surface.AddHandler(PointerMovedEvent, OnOverlayPointerMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
            surface.PointerEntered += OnOverlayPointerExited;
            surface.PointerExited += OnOverlayPointerExited;
            surface.AddHandler(GotFocusEvent, (_, e) =>
            {
                if (e.NavigationMethod == NavigationMethod.Pointer) _keyboardInteraction = false;
                else if (e.NavigationMethod is NavigationMethod.Tab or NavigationMethod.Directional) _keyboardInteraction = true;
            }, RoutingStrategies.Bubble, handledEventsToo: true);
            surface.AddHandler(PointerPressedEvent, OnOverlayPointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
            surface.AddHandler(PointerReleasedEvent, OnOverlayPointerReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
            surface.AddHandler(PointerCaptureLostEvent, OnOverlayPointerCaptureLost, RoutingStrategies.Bubble, handledEventsToo: true);
            surface.AddHandler(PointerWheelChangedEvent, OnOverlayPointerWheel, RoutingStrategies.Tunnel, handledEventsToo: true);
            surface.AddHandler(TappedEvent, OnOverlayTap, RoutingStrategies.Tunnel, handledEventsToo: true);
            surface.AddHandler(DoubleTappedEvent, OnOverlayTap, RoutingStrategies.Tunnel, handledEventsToo: true);
        }
        InitializeEdgeIndicators();
        _overlayFooter.ZIndex = 10;
        _contextPanel.ZIndex = 20;
        _helpPanel.ZIndex = 30;
        _nativeVideoOverlay.Focusable = true;
        KeyboardNavigation.SetTabNavigation(_nativeVideoOverlay, KeyboardNavigationMode.Cycle);
        ActualThemeVariantChanged += (_, _) => ApplyOverlayBackgrounds();
        _overlayFooter.Children.Add(_overlayTransportHost);
        _overlayFooter.SizeChanged += (_, _) =>
        {
            UpdateEdgeIndicators();
            if (_contextOpen) LayoutContextPlacement();
        };
    }

    private void ApplyOverlayPlacement()
    {
        if (_terminalRetirement || _applyingOverlayPlacement || _slideshow == null || _chrome.Child == null) return;
        _applyingOverlayPlacement = true;
        try
        {
            bool overlay = OverlayControlsEffective;
            if (!overlay || !_playbackPreferences.AutoHideControls) _overlayHidden = false;
            if (!PostOverlayEffective && _hoverContextOpen) SetContextOpen(false, notify: false, hover: true);
            // Move the same controls, preserving their executors, values and subscriptions.
            // Empty Auto rows collapse, so this actually reclaims the media area.
            if (overlay)
            {
                MoveOverlayControl(_overlayFooter, OverlayHost);
                Grid.SetRow(_overlayFooter, 0); Grid.SetRowSpan(_overlayFooter, 1);
                MoveOverlayControl(_chrome, _overlayFooter);
                MoveOverlayControl(_infoOverlay, _overlayFooter);
                _video?.SetExternalTransportHost(_overlayTransportHost);
            }
            else
            {
                // Restore transport before detaching/hiding its overlay host, including a
                // Windows transparency failure. Never leave the usable controls on a hidden window.
                _video?.SetExternalTransportHost(null);
                MoveOverlayControl(_chrome, _visualRoot);
                MoveOverlayControl(_infoOverlay, _visualRoot);
                Grid.SetRow(_chrome, _nativeOverlayFallback ? 2 : 1); Grid.SetRowSpan(_chrome, 1);
                Grid.SetRow(_infoOverlay, _nativeOverlayFallback ? 3 : 2); Grid.SetRowSpan(_infoOverlay, 1);
                // Keep the hidden footer parented. Removing it here would detach the
                // source ancestor while its chrome/transport transfers are still queued.
            }
            _overlayFooter.IsVisible = overlay && !_overlayHidden;
            _overlayFooter.IsHitTestVisible = !_overlayHidden;
            _overlayTransportHost.IsVisible = overlay && _entry?.Kind == SavedMediaKind.Video && _video?.IsVisible == true;
            _overlayFallbackNotice.IsVisible = _nativeOverlayFallback
                && (_playbackPreferences.OverlayControls || _playbackPreferences.OverlayPostContext);
            ApplyOverlayBackgrounds();
            LayoutContext();
            UpdateMediaFocusVisibility();
            RefreshPlaybackControls();
        }
        finally { _applyingOverlayPlacement = false; }
    }

    private void MoveOverlayControl(Control control, Panel target)
    {
        if (_terminalRetirement) return;
        Control? focused = control.IsKeyboardFocusWithin
            ? TopLevel.GetTopLevel(control)?.FocusManager?.GetFocusedElement() as Control : null;
        Action? restoreFocus = focused == null ? null : () =>
        {
            // Reparenting never activates another native window to recover focus.
            if (!_terminalRetirement && focused.IsAttachedToVisualTree()
                && TopLevel.GetTopLevel(control) is Window { IsActive: true }
                && focused.IsEffectivelyVisible && focused.IsEffectivelyEnabled)
                focused.Focus(_keyboardInteraction ? NavigationMethod.Tab : NavigationMethod.Pointer);
        };
        // Submit even when target is the actual parent: this may retarget a pending move.
        GalleryControlTransfer.Move(control, target, () => !_terminalRetirement
            && (!(ReferenceEquals(control, _controlsEdgeIndicator) || ReferenceEquals(control, _postEdgeIndicator))
                || !_viewerInteractionStopped), restoreFocus);
    }

    private void LayoutContextPlacement()
    {
        if (_terminalRetirement) return;
        bool overlay = PostOverlayEffective;
        bool compact = Bounds.Width < 850;
        MoveOverlayControl(_contextPanel, overlay ? OverlayHost : _body);
        _contextPanel.IsVisible = _contextOpen;
        if (overlay)
        {
            _contextPanel.Width = Math.Min(360, Math.Max(0, Bounds.Width - 24));
            _contextPanel.HorizontalAlignment = HorizontalAlignment.Right;
            _contextPanel.VerticalAlignment = VerticalAlignment.Stretch;
            double transportInset = !_nativeOverlayActive && _entry?.Kind == SavedMediaKind.Video
                ? _video?.TransportBottomInset ?? 0 : 0;
            _contextPanel.Margin = new Thickness(12, 12, 12, OverlayControlsEffective && _overlayFooter.IsVisible
                ? _overlayFooter.Bounds.Height + 16 : transportInset + 12);
            Grid.SetColumn(_contextPanel, 0); Grid.SetColumnSpan(_contextPanel, 1);
            Grid.SetRow(_contextPanel, 0); Grid.SetRowSpan(_contextPanel, 1);
        }
        else
        {
            _contextPanel.Width = compact ? double.NaN : 320;
            _contextPanel.HorizontalAlignment = HorizontalAlignment.Stretch;
            _contextPanel.VerticalAlignment = VerticalAlignment.Stretch;
            // Native chrome covers only the video viewport in a separate window.
            // The adjacent docked context is not underneath it. Feeding that
            // footer's measured height into this root also creates a cross-root
            // layout dependency (main layout positions the native overlay).
            _contextPanel.Margin = new Thickness(0, 0, 0, !_nativeOverlayActive && OverlayControlsEffective
                && _overlayFooter.IsVisible ? _overlayFooter.Bounds.Height + 8 : 0);
            Grid.SetColumn(_contextPanel, compact ? 0 : 1); Grid.SetColumnSpan(_contextPanel, compact ? 2 : 1);
            Grid.SetRow(_contextPanel, 0); Grid.SetRowSpan(_contextPanel, 1);
        }
        bool hideViewport = !overlay && _contextOpen && compact;
        if (hideViewport && _viewport.IsKeyboardFocusWithin) _toolsToggle.Focus(NavigationMethod.Tab);
        _viewport.IsVisible = !hideViewport;
        UpdateEdgeIndicators();
    }

    private void ApplyOverlayBackgrounds()
    {
        bool light = ActualThemeVariant == ThemeVariant.Light;
        var background = new SolidColorBrush(light ? Color.FromArgb(238, 248, 249, 252) : Color.FromArgb(232, 28, 31, 38));
        SetOverlayBackground(_chrome, OverlayControlsEffective, background);
        SetOverlayBackground(_infoOverlay, OverlayControlsEffective, background);
        SetOverlayBackground(_contextPanel, PostOverlayEffective, background);
        if (_video?.TransportControl is Border transport) SetOverlayBackground(transport, OverlayControlsEffective, background);
        // Deliberately never set parent Opacity: text and controls remain fully opaque.
    }

    private static void SetOverlayBackground(Border border, bool overlay, IBrush background)
    {
        if (overlay) border.Background = background;
        else border.ClearValue(Border.BackgroundProperty);
    }

    internal void FocusGallerySurface()
    {
        Control target = _nativeOverlayActive ? _nativeVideoOverlay : this;
        if (TopLevel.GetTopLevel(target) is Window { IsActive: true, IsEnabled: true })
            target.Focus(NavigationMethod.Tab);
    }

    private void RevealOverlayControls()
    {
        _lastOverlayActivity = Stopwatch.GetTimestamp();
        if (_overlayHidden)
        {
            _overlayHidden = false;
            _overlayFooter.IsVisible = OverlayControlsEffective;
            _overlayFooter.IsHitTestVisible = true;
        }
        UpdateEdgeIndicators();
        if (_contextOpen) LayoutContextPlacement();
        UpdateViewerInteractionTimer();
    }

    private bool RevealForKeyboard(KeyEventArgs e)
    {
        _keyboardInteraction = true;
        // This is an observation, never an input gate. The first command and every
        // repeat must reach the existing gallery/native-control routing immediately.
        // Escape likewise stays entirely with the shell/help/context hierarchy.
        if (GalleryEdgeKeyboard.ShouldReveal(e)) RevealOverlayControls();
        return false;
    }

    private void RememberPointer(object? sender, PointerEventArgs e)
    {
        // Window teardown can dispatch PointerExited after Stop and while the
        // presentation source is being disposed. Never reacquire pointer state
        // or touch coordinate transforms for a retired/detached viewer.
        if (_terminalRetirement || _viewerInteractionStopped) return;
        _lastPointer = e.Pointer;
        if (sender is Control surface && surface.IsAttachedToVisualTree())
            _lastPointerScreen = surface.PointToScreen(e.GetPosition(surface));
        RefreshHoverGeometry();
    }

    private void RefreshHoverGeometry()
    {
        if (_terminalRetirement || _viewerInteractionStopped) return;
        bool wasControls = _controlsHot || _pointerInControls || _pointerOnControlsIndicator;
        bool wasPost = _postHot || _pointerInPost || _pointerOnPostIndicator;
        _controlsHot = _postHot = _pointerInControls = _pointerInPost = false;
        _pointerOnControlsIndicator = _pointerOnPostIndicator = false;
        if (_lastPointerScreen is PixelPoint pixel)
        {
            Control surface = _nativeOverlayActive ? _nativeVideoOverlay : _body;
            // A closed TopLevel may remain in the parent chain after its
            // presentation source is gone. Coordinate APIs require attachment,
            // not merely a discoverable window ancestor.
            bool Inside(Control panel) => panel.IsEffectivelyVisible && panel.IsAttachedToVisualTree()
                && new Rect(panel.Bounds.Size).Contains(panel.PointToClient(pixel));
            // Count the actual transport even in docked/native-fallback placement.
            // Its volume/loop controls can sit in the right strip above the bottom band.
            _pointerInControls = Inside(_overlayFooter) || Inside(_chrome) || Inside(_infoOverlay)
                || _video?.TransportControl is Control transport && Inside(transport);
            _pointerInPost = Inside(_contextPanel);
            _pointerOnControlsIndicator = Inside(_controlsEdgeIndicator);
            _pointerOnPostIndicator = Inside(_postEdgeIndicator);
            if (surface.IsAttachedToVisualTree())
            {
                var hit = GalleryEdgeHit.At(surface.PointToClient(pixel), surface.Bounds.Size)
                    .WithoutPanelOverlap(_pointerInControls, _pointerInPost,
                        _pointerOnControlsIndicator || _pointerOnPostIndicator);
                _controlsHot = hit.Controls; _postHot = hit.Post;
            }
        }
        long now = Stopwatch.GetTimestamp();
        if (_controlsHot || _pointerInControls || _pointerOnControlsIndicator || wasControls) _lastOverlayActivity = now;
        if (_postHot || _pointerInPost || _pointerOnPostIndicator || wasPost) _lastPostActivity = now;
        if (!_postHot && !_pointerInPost && !_pointerOnPostIndicator) _suppressPostHoverUntilExit = false;
    }

    private void RevealHoveredPanels()
    {
        if (_terminalRetirement || _viewerInteractionStopped) return;
        // Ordinary motion should neither reveal chrome nor scan every popup in the shell.
        bool controls = _controlsHot || _pointerOnControlsIndicator && _overlayHidden && !_pointerInPost;
        bool post = _postHot || _pointerOnPostIndicator && !_contextOpen && !_pointerInControls;
        if (!controls && !post) { UpdateViewerInteractionTimer(); return; }
        if (!IsGalleryWindowActive || !_interactionAvailable || HasGalleryDialog || HasGalleryPopup) return;
        // A pan/seek drag or focused keyboard task is not a request to open another
        // panel. Pointer focus alone deliberately does not pin the controls.
        bool keyboardPanelFocus = _keyboardInteraction && (_overlayFooter.IsKeyboardFocusWithin
            || _chrome.IsKeyboardFocusWithin || _infoOverlay.IsKeyboardFocusWithin
            || _contextPanel.IsKeyboardFocusWithin || _video?.TransportControl.IsKeyboardFocusWithin == true);
        if (!GalleryEdgeHover.CanReveal(_dragging || _pressedPointers.Count != 0 || _lastPointer?.Captured != null,
            HasSelectedPostText || HasSelectedControlsText, keyboardPanelFocus, _helpOpen)) return;
        if (controls && OverlayControlsEffective) RevealOverlayControls();
        if (post && PostOverlayEffective && !_contextOpen && !_suppressPostHoverUntilExit && _entry != null)
            OpenEdgePostContext();
        UpdateViewerInteractionTimer();
    }

    private void OnOverlayPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_terminalRetirement || _viewerInteractionStopped) return;
        PixelPoint? previous = _lastPointerScreen;
        RememberPointer(sender, e);
        if (e.Pointer.Type == PointerType.Mouse && _lastPointerScreen is PixelPoint current
            && previous != current)
        {
            _lastIndicatorMotion = Stopwatch.GetTimestamp();
            _indicatorExpiryTimer ??= new DispatcherTimer { Interval = GalleryEdgeMotion.Lifetime };
            _indicatorExpiryTimer.Tick -= OnIndicatorExpiry;
            _indicatorExpiryTimer.Tick += OnIndicatorExpiry;
            _indicatorExpiryTimer.Stop();
            _indicatorExpiryTimer.Start();
        }
        RevealHoveredPanels();
        UpdateEdgeIndicators();
    }

    private void OnIndicatorExpiry(object? sender, EventArgs e)
    {
        if (_terminalRetirement || _viewerInteractionStopped) return;
        _indicatorExpiryTimer?.Stop();
        // A one-shot tick may arrive fractionally early. Clear deterministically
        // rather than retaining artwork with no remaining expiry callback.
        _lastIndicatorMotion = null;
        UpdateEdgeIndicatorArtwork();
    }

    private void OnOverlayPointerExited(object? sender, PointerEventArgs e)
    {
        if (_terminalRetirement || _viewerInteractionStopped) return;
        // Exit can also mean that a newly shown child replaced the hit-test target.
        // Keep actual screen geometry instead of treating every routed exit as leaving
        // the gallery (which made a bottom-edge reveal immediately disappear again).
        RememberPointer(sender, e);
        RevealHoveredPanels();
        UpdateEdgeIndicators();
        UpdateViewerInteractionTimer();
    }

    private void OnOverlayPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_terminalRetirement || _viewerInteractionStopped) return;
        _keyboardInteraction = false;
        bool hidden = _overlayHidden, postWasOpen = _contextOpen;
        RememberPointer(sender, e);
        RevealHoveredPanels();
        _pressedPointers.Add(e.Pointer);
        if (!e.Handled && ReferenceEquals(e.Source, _nativeVideoOverlay)) _nativeVideoOverlay.Focus(NavigationMethod.Pointer);
        if (e.Handled || !(hidden && !_overlayHidden || !postWasOpen && _hoverContextOpen)) return;
        // A touch/click in a reveal strip opens its panel, not an underlying media action.
        _revealPointerId = e.Pointer.Id; _swallowRevealTap = true; e.Handled = true;
    }
    private void OnOverlayPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_terminalRetirement || _viewerInteractionStopped) return;
        _pressedPointers.Remove(e.Pointer);
        if (_revealPointerId != e.Pointer.Id) return;
        e.Handled = true;
        Dispatcher.UIThread.Post(() => { _revealPointerId = null; _swallowRevealTap = false; }, DispatcherPriority.Background);
    }
    private void OnOverlayPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        if (_terminalRetirement || _viewerInteractionStopped) return;
        _pressedPointers.Remove(e.Pointer);
        _lastOverlayActivity = _lastPostActivity = Stopwatch.GetTimestamp();
        UpdateViewerInteractionTimer();
    }
    private void OnOverlayPointerWheel(object? sender, PointerWheelEventArgs e)
    {
        if (_terminalRetirement || _viewerInteractionStopped) return;
        bool hidden = _overlayHidden, postWasOpen = _contextOpen;
        RememberPointer(sender, e);
        RevealHoveredPanels();
        if (hidden && !_overlayHidden || !postWasOpen && _hoverContextOpen) e.Handled = true;
    }
    private void OnOverlayTap(object? sender, TappedEventArgs e)
    {
        if (_terminalRetirement || _viewerInteractionStopped) return;
        if (_swallowRevealTap) e.Handled = true;
    }

    private void OnViewerInteractionTick()
    {
        if (_terminalRetirement || _viewerInteractionStopped) return;
        MarkVisibleImageReady();
        bool active = IsGalleryWindowActive;
        if (!active) _pressedPointers.Clear();
        bool popup = HasGalleryPopup || HasGalleryDialog;
        bool captured = _dragging || _pressedPointers.Count != 0 || _lastPointer?.Captured != null;
        bool reading = _helpOpen || _contextOpen || _keyboardInteraction && _infoOverlay.IsKeyboardFocusWithin;
        RefreshHoverGeometry();
        RevealHoveredPanels();
        bool controlHasFocus = _chrome.IsKeyboardFocusWithin || _infoOverlay.IsKeyboardFocusWithin
            || _video?.TransportControl.IsKeyboardFocusWithin == true;
        bool focused = _keyboardInteraction && controlHasFocus;
        bool selectedPostText = HasSelectedPostText;
        if (_hoverContextOpen && !_postHot && !_pointerInPost && !_pointerOnPostIndicator && !captured && !popup
            && !(_keyboardInteraction && _contextPanel.IsKeyboardFocusWithin) && !selectedPostText
            && Stopwatch.GetElapsedTime(_lastPostActivity) >= TimeSpan.FromMilliseconds(500))
            SetContextOpen(false, notify: false, hover: true);
        UpdateSlideshowInteraction(!_interactionAvailable || !active || popup || reading || captured
            || _sessionCancellation.IsCancellationRequested);
        if (_lastRunning != _slideshow.IsRunning || _shownSlideshowStatus != CurrentSlideshowStatus) RefreshPlaybackControls();
        _slideshowToggle.IsEnabled = _slideshow.IsRunning || CanRunSlideshow && !_navigationPending;
        var activity = new GalleryOverlayActivity(OverlayControlsEffective, _playbackPreferences.AutoHideControls,
            _interactionAvailable, active, focused, captured, popup, _helpOpen || selectedPostText || HasSelectedControlsText,
            Hovered: _controlsHot || _pointerInControls || _pointerOnControlsIndicator);
        if (activity.CanHide(Stopwatch.GetElapsedTime(_lastOverlayActivity)))
        {
            if (controlHasFocus) { FocusGallerySurface(); _keyboardInteraction = false; }
            _overlayHidden = true;
            _overlayFooter.IsVisible = false;
            _overlayFooter.IsHitTestVisible = false;
            UpdateEdgeIndicators();
            if (_contextOpen) LayoutContextPlacement();
        }
        // Diagnostics never reveal or pin controls. Real capture/selection/dialog
        // holds above remain independent of media readiness and failure reporting.
        UpdateViewerInteractionTimer();
    }
}
