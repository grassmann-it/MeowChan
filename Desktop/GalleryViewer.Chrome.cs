using System;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.WorkViews;
using MeowChan.Gallery;

namespace MeowChan.Desktop;

/// <summary>Where the video bar (the transport) lives: under the picture in the player, in the docked dock, or in the overlay footer.</summary>
internal enum GalleryTransportHostKind { Player, Dock, Overlay }

/// <summary>
/// GL2 (PLAN-GALERIE-LAYOUT-BLOECKE §6): the video bar moves into the dock, so the video bar, the tools and the info are one surface
/// without a gap. Pure rules, so they are tested without a window.
/// </summary>
internal static class GalleryTransportPlacement
{
    /// <summary>Overlay controls: the footer hosts it. Docked: the dock does, except in the Windows transparency fallback, which keeps the bar under the picture.</summary>
    internal static GalleryTransportHostKind Resolve(bool overlayControls, bool transparencyFallback) =>
        overlayControls ? GalleryTransportHostKind.Overlay : transparencyFallback ? GalleryTransportHostKind.Player : GalleryTransportHostKind.Dock;

    /// <summary>The dock's host row shows while the plan has a video bar, the dock hosts it and the video is on the screen.</summary>
    internal static bool DockRowVisible(bool planTransport, GalleryTransportHostKind host, bool videoShown) =>
        planTransport && host == GalleryTransportHostKind.Dock && videoShown;

    /// <summary>
    /// The dock's margin: directly under the picture (the video bar is the dock's first row, nothing separates them); the fallback keeps
    /// the 8 dip its full-width strip always had above the tools. The bottom is 8 only while no info line follows. With the filmstrip at
    /// the bottom (GL1) the band is the dock's first row and carries that top margin; the tools surface then sits right under the band.
    /// </summary>
    internal static Thickness DockMargin(bool transparencyFallback, bool infoFollows, bool filmstripAbove = false) =>
        new(8, transparencyFallback && !filmstripAbove ? 8 : 0, 8, infoFollows ? 0 : 8);
}

// G1 (PLAN-TAGGING-GALERIE-UX-1.1 §3, §6): the viewer applies GalleryChromePlan in one place, ApplyChrome. Every change that can move a
// block (the work view, the mode, the width, the side panel, the slideshow, the tools toggle, a medium) ends here; no other code sets the
// visibility of a block the plan decides. Errors, warnings, the consent to remove a favourite and the way back are not blocks of the plan
// and keep their own rules (they are never hidden by a mode, a width or a layout choice).
//
// The mode switch "View | Tag" (Tom's E3 addition): tagging is a mode of the gallery, in every gallery, independent of the slideshow.
// Tag without a walk (any gallery) is the docked tag panel with favourite, rating and tags for the shown medium (StartFreeTagMode); on a
// walk of the tagging queue the window starts the queue's tagging mode (StartTaggingMode) with its decision strip. The viewer raises
// TaggingModeRequested and the window picks the flavour, so the existing ways in (the Tagging page, "Walk through tagging queue") stay.
internal sealed partial class GalleryViewer
{
    private StackPanel _modeTools = null!;
    private StackPanel _navigationTools = null!, _zoomTools = null!, _mediaTools = null!, _viewTools = null!;
    private Grid _controlsGrid = null!;
    private readonly ToggleButton _modeView = new() { Name = "GalleryModeView", Classes = { "media-control" }, MinHeight = 32 };
    private readonly ToggleButton _modeTag = new() { Name = "GalleryModeTag", Classes = { "media-control" }, MinHeight = 32 };
    // The live region of the mode: in the tree at no size, only its text is for a screen reader (the media page's focus-mode pattern).
    private readonly TextBlock _modeAnnouncement = new()
    {
        Name = "GalleryModeAnnouncement", Width = 0, Height = 0, ClipToBounds = true, IsHitTestVisible = false, Focusable = false,
        HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top
    };
    // GL2: the dock hosts the video bar above the tools (one surface, 4 dip between the rows) in the docked placement; the overlay footer
    // has its own host (GalleryViewer.Overlay.cs). Empty and collapsed while no video bar lives here.
    private readonly StackPanel _dockTransportHost = new() { Name = "GalleryDockTransportHost", Classes = { "in-dock" }, IsVisible = false };
    private GalleryChromePlan? _chromePlan;
    // GL0 hook: the person's layout choice per mode (GL4 loads and saves them per profile, GL3's menu changes them).
    private GalleryLayoutChoice _viewChoice = GalleryLayoutChoice.Default, _tagChoice = GalleryLayoutChoice.Default;
    private bool _taggingAvailable;
    // Tag mode without a walk: the tag panel works on the shown medium; nothing is added to the tagging queue.
    private bool _freeTagMode;
    private bool _taggingHold;

    /// <summary>The person chose View (false) or Tag (true) with the switch or its key; the window starts or ends the tagging mode.</summary>
    internal event Action<bool>? TaggingModeRequested;

    /// <summary>The plan the chrome was last laid out for (tests and smokes).</summary>
    internal GalleryChromePlan ChromePlan => _chromePlan ?? BuildChromePlan();
    internal GalleryChromeMode ChromeMode => ChromePlan.Mode;
    /// <summary>
    /// The input of what is on the screen now (tests, smokes and GL3's menu rows): the layout wish plus the real <c>ControlsHidden</c> state
    /// (the overlay footer is auto-hidden). The plan the chrome is applied from, <see cref="ChromePlan"/>, resolves the wish without it.
    /// </summary>
    internal GalleryChromeInput ChromeInput => BuildChromeInput() with { ControlsHidden = ControlsHiddenNow };
    /// <summary>The plan for what is on the screen now: <see cref="ChromePlan"/> seen through <see cref="ControlsHiddenNow"/>.</summary>
    internal GalleryChromePlan ShownChromePlan => GalleryChromePlan.Resolve(ChromeInput);
    /// <summary>The overlay footer is auto-hidden (only the overlay placement can hide the controls; the docked dock never does).</summary>
    internal bool ControlsHiddenNow => OverlayControlsEffective && _overlayHidden;
    /// <summary>The docked dock hosts the video bar (the Windows transparency fallback keeps it under the picture, as before).</summary>
    private GalleryTransportHostKind TransportHostKind => GalleryTransportPlacement.Resolve(OverlayControlsEffective, _nativeOverlayFallback);
    private bool DockHostsTransport => TransportHostKind == GalleryTransportHostKind.Dock;
    internal Panel DockTransportHost => _dockTransportHost;
    internal ToggleButton ModeViewButton => _modeView;
    internal ToggleButton ModeTagButton => _modeTag;
    internal Panel ModeSwitch => _modeTools;
    /// <summary>What the screen reader got last for a mode change (null: never changed).</summary>
    internal string? ModeAnnouncement => _modeAnnouncement.Text;
    /// <summary>Whether the window can walk the shown list as the tagging queue (Tag then brings the decision strip as well).</summary>
    internal bool IsTaggingAvailable => _taggingAvailable;
    /// <summary>The gallery is in its Tag mode: a walk of the tagging queue (<see cref="IsTaggingMode"/>) or the tag panel alone.</summary>
    internal bool IsTagMode => _tagging != null || _freeTagMode;

    /// <summary>
    /// Tag in a gallery that is no walk of the tagging queue: the tag panel opens docked on the shown medium (its favourite and rating row
    /// is the favourite surface, E1) and the chrome takes the Tag layout without a decision strip. No-op without a medium.
    /// </summary>
    internal void StartFreeTagMode(IGalleryTagPanelOwner owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        if (_terminalRetirement || IsTagMode || _entry == null) return;
        _freeTagMode = true; // first: the panel then opens as part of the work and does not pause a running slideshow
        ShowTagPanel(owner);
        RefreshPanelCounter();
        RefreshPlaybackControls();
        OnGalleryModeChanged();
    }

    /// <summary>View again after <see cref="StartFreeTagMode"/>: the tag panel closes and the View layout comes back.</summary>
    internal void EndFreeTagMode()
    {
        if (!_freeTagMode) return;
        _freeTagMode = false;
        CloseTagPanel();
        RefreshPanelCounter();
        RefreshPlaybackControls();
        OnGalleryModeChanged();
    }

    // M1: the tag panel went without the switch (its ×, Ctrl+Shift+T, the side panel's Post segment or ×, the slot closing): a free Tag
    // mode ends with it, so the switch shows View again and Tag opens the panel anew. A walk keeps its mode (its decision row stays).
    private void EndFreeTagModeWithPanel()
    {
        if (!_freeTagMode || _tagging != null) return;
        _freeTagMode = false;
        if (_terminalRetirement) return; // the viewer stops: nothing left to present
        RefreshPlaybackControls();
        OnGalleryModeChanged();
    }

    /// <summary>The window tells whether the shown list is a walk of the tagging queue (open or parked); the switch's Tag half follows.</summary>
    internal void SetTaggingAvailable(bool available)
    {
        if (_taggingAvailable == available) return;
        _taggingAvailable = available;
        PresentModeSwitch();
        ApplyChrome();
    }

    /// <summary>The person's layout choice of <paramref name="mode"/> (null members: the view's default), as saved per profile and mode (GL4).</summary>
    internal GalleryLayoutChoice LayoutChoice(GalleryChromeMode mode) => mode == GalleryChromeMode.Tag ? _tagChoice : _viewChoice;

    /// <summary>The mode the chrome is laid out for now.</summary>
    private GalleryChromeMode ShownChromeMode => GalleryChromePlan.ResolveMode(IsTagMode);

    /// <summary>
    /// The choice the plan reads: the saved choice of the mode, and for the View set the old filmstrip setting where the set has none
    /// (<see cref="ConfigureFilmstrip"/>: off is off). The window loads the migrated choice (GalleryLayoutPreferences.FromConfig), so
    /// the two agree; this keeps a viewer that only got the old setting working.
    /// </summary>
    private GalleryLayoutChoice EffectiveLayoutChoice(GalleryChromeMode mode)
    {
        GalleryLayoutChoice choice = LayoutChoice(mode);
        return mode == GalleryChromeMode.View && choice.Filmstrip == null && !_filmstripSetting ? choice with { Filmstrip = false } : choice;
    }

    /// <summary>Raised when the person changed the layout of a mode (the filmstrip key, the layout menu, the reset): the window saves it per profile and mode. Not raised by <see cref="SetLayoutChoice"/>.</summary>
    internal event Action<GalleryChromeMode, GalleryLayoutChoice>? LayoutChoiceChanged;

    /// <summary>Sets the layout choice of one mode without reporting it (the window on load); the chrome follows at once when it is the shown mode.</summary>
    internal void SetLayoutChoice(GalleryChromeMode mode, GalleryLayoutChoice choice)
    {
        ArgumentNullException.ThrowIfNull(choice);
        // "The view decides" is authoritative once a choice is loaded: the old setting must not revive an "off" after a reset.
        if (mode == GalleryChromeMode.View && choice.Filmstrip == null) _filmstripSetting = true;
        if (choice == LayoutChoice(mode)) return;
        if (mode == GalleryChromeMode.Tag) _tagChoice = choice; else _viewChoice = choice;
        ApplyChrome();
        UpdateFilmstripPlacement();
        RefreshSlideshowStatus();
    }

    /// <summary>The person changes the layout of one mode (GL3's menu rows, the filmstrip key): applies it and reports it for saving. An unchanged choice reports nothing.</summary>
    internal void ChangeLayoutChoice(GalleryChromeMode mode, GalleryLayoutChoice choice)
    {
        ArgumentNullException.ThrowIfNull(choice);
        if (_terminalRetirement || choice == LayoutChoice(mode)) return;
        SetLayoutChoice(mode, choice);
        LayoutChoiceChanged?.Invoke(mode, choice);
    }

    /// <summary>"Reset layout": the shown mode (or the given one) back to the work view's defaults; the other mode keeps its choice. GL3's menu row calls this and offers the undo with the returned previous choice.</summary>
    internal GalleryLayoutChoice ResetLayout(GalleryChromeMode? mode = null)
    {
        GalleryChromeMode target = mode ?? ShownChromeMode;
        GalleryLayoutChoice previous = LayoutChoice(target);
        ChangeLayoutChoice(target, GalleryLayoutChoice.Default);
        return previous;
    }

    // The switch is the first group of the toolbar: it names the mode the rest of the chrome follows.
    private void InitializeModeSwitch(StackPanel group)
    {
        _modeTools = group;
        _modeTools.Spacing = 8; // toolbar UX review 15.11.: the bar gap (Workspace.Space.BarGap), as on every toolbar
        _modeTools.Children.Add(_modeView);
        _modeTools.Children.Add(_modeTag);
        _modeView.Click += (_, _) => RequestGalleryMode(tag: false);
        _modeTag.Click += (_, _) => RequestGalleryMode(tag: true);
        AutomationProperties.SetLiveSetting(_modeAnnouncement, AutomationLiveSetting.Polite);
        PresentModeSwitch();
    }

    private void PresentModeSwitch()
    {
        if (_modeTools == null) return;
        bool tag = IsTagMode;
        string view = Texts.T("gallery.mode.view"), tagLabel = Texts.T("gallery.mode.tag");
        // GL5d (S2 stage 2): words from CompactToolbarWidth on (the plan's ModeIcons says the same), icons (eye | tag) below it; the words stay the name.
        bool icons = _compactToolbar;
        _modeView.Content = icons ? CommandBindings.NamedIcon("eye", 16) : view;
        _modeTag.Content = icons ? CommandBindings.NamedIcon("tag", 16) : tagLabel;
        foreach (ToggleButton button in new[] { _modeView, _modeTag }) { button.MinWidth = icons ? 32 : 0; if (icons) button.Padding = new Thickness(8, 0); else button.ClearValue(TemplatedControl.PaddingProperty); }
        _modeView.IsChecked = !tag;
        _modeTag.IsChecked = tag;
        foreach ((ToggleButton button, string label) in new[] { (_modeView, view), (_modeTag, tagLabel) })
        {
            string tip = CommandBindings.Tooltip("gallery.mode-toggle", null, label);
            AutomationProperties.SetName(button, label);
            AutomationProperties.SetHelpText(button, tip);
            AutomationProperties.SetAcceleratorKey(button, CommandCatalog.ShortcutDisplay("gallery.mode-toggle") ?? string.Empty);
            ToolTip.SetTip(button, tip);
        }
        AutomationProperties.SetName(_modeTools, Texts.T("gallery.mode.name"));
    }

    /// <summary>The switch or its key asks for View (false) or Tag (true); the window starts the walk or the tag panel alone.</summary>
    private void RequestGalleryMode(bool tag)
    {
        if (_terminalRetirement) return;
        bool hadFocus = _modeTools.IsKeyboardFocusWithin;
        if (tag != IsTagMode) TaggingModeRequested?.Invoke(tag);
        PresentModeSwitch(); // a ToggleButton flips itself on click; the shown state is the mode's
        // The mode change keeps the focus on the switch (the half that is now on), never on a block that just left the bar.
        if (hadFocus && _modeTools.IsEffectivelyVisible) (IsTagMode ? _modeTag : _modeView).Focus(NavigationMethod.Tab);
    }

    // StartTaggingMode and EndTaggingMode: the switch, the chrome and the screen reader follow the mode.
    private void OnGalleryModeChanged()
    {
        PresentModeSwitch();
        ApplyChrome();
        _modeAnnouncement.Text = Texts.T(IsTagMode ? "gallery.mode.announce.tag" : "gallery.mode.announce.view");
    }

    private GalleryChromePlan BuildChromePlan() => GalleryChromePlan.Resolve(BuildChromeInput());

    private GalleryChromeInput BuildChromeInput()
    {
        GalleryChromeMode mode = GalleryChromePlan.ResolveMode(IsTagMode);
        GalleryLayoutChoice choice = EffectiveLayoutChoice(mode);
        // ControlsHidden stays false in the plan the chrome is applied from: the overlay footer hides as a whole (GalleryOverlayActivity) and
        // gives every block back unchanged, while the filmstrip's peek, the hints and the slideshow toggle ask this plan for the person's
        // layout wish. ChromeInput / ShownChromePlan carry the real state (GL2).
        return new GalleryChromeInput(mode, _workView, choice, _mediaFocus, OverlayControlsEffective, ControlsHidden: false,
            _entry?.Kind == SavedMediaKind.Video, _contextOpen, _taggingSurface != null, Bounds.Width, Bounds.Height)
        {
            SlideshowActive = _slideshow != null && (_playbackPreferences.Mode == GalleryPlaybackMode.Slideshow || _slideshow.IsRunning),
            SlideshowRunning = _slideshow?.IsRunning == true,
            PanelDecisions = DecisionRowVisible,
            TagAvailable = _taggingAvailable,
            Walk = IsTaggingMode
        };
    }

    /// <summary>
    /// The one place that lays the chrome out for the current plan: movable tools on the bar or under "More" (only when that changed), then
    /// the visibility of every block. Cheap when nothing moved: only <c>IsVisible</c> is set, no control is rebuilt.
    /// </summary>
    private void ApplyChrome(bool force = false)
    {
        if (_terminalRetirement || _workViewTools == null || _modeTools == null) return; // (a viewer under construction or retired)
        GalleryChromePlan plan = BuildChromePlan();
        GalleryChromePlan? last = _chromePlan;
        _chromePlan = plan;
        bool focusLost = false;
        if (_toolSlots.Count > 0 && (force || last == null || !last.SamePlacement(plan))) PlaceChromeTools(plan);
        void Show(Control control, bool visible)
        {
            if (!visible && control.IsVisible && control.IsKeyboardFocusWithin) focusLost = true;
            control.IsVisible = visible;
        }
        Show(_modeTools, plan.ModeSwitch);
        Show(_navigationTools, plan.Nav);
        Show(_zoomTools, plan.Zoom);
        Show(_actual, plan.ActualSize); // S2 stage 4: 1:1 leaves the bar below the tiny width ...
        _actualRow.IsVisible = plan.Zoom && !plan.ActualSize; // ... and is a row of "More" then (the key 1 stays)
        Show(_contextToggle, plan.PanelToggle);
        Show(_favorite, plan.Favorite);
        Show(_mediaTools, plan.PanelToggle || plan.Favorite);
        Show(_viewTools, plan.More);
        // GL3: the Layout button on the bar, or its rows as a section of "More" (placement in the plan, UX spec S4 GL3+a).
        Show(_layoutButton, plan.LayoutOnBar);
        Show(_layoutSection, plan.More && !plan.LayoutOnBar);
        Show(_workViewTools, _workViewTools.Children.Count > 0);
        Show(_toolsToggle, plan.ToolsToggle);
        Show(_taggingStrip, plan.DecisionRow);
        LayOutControls(plan);
        // The legacy action remains routable, but must never reappear as duplicate chrome.
        _autoHideToggle.IsVisible = false;
        // The position sits between the arrows, always; the caption no longer repeats it.
        _compactPosition.IsVisible = true;
        _position.IsVisible = false;
        _quality.IsVisible = plan.InfoQuality;
        // "Where am I" (thread or result list): the work view gates it (Barebones leaves it to the details), the mode and the layout are its state.
        WorkViewGate.Apply(_scope, _workView, stateVisible: plan.InfoScope);
        // An integrity warning or a passive import: a safety line, only the hidden tools take it with the whole info line.
        _provenance.IsVisible = !_mediaFocus && !string.IsNullOrEmpty(_provenance.Text);
        // QW5 (in the plan): with the tag panel's favorite and rating row on the screen, that row is the one place that shows the rating.
        _ratingStatus.IsVisible = plan.InfoRating;
        // Diagnostic notices have no visibility or auto-hide authority. The capability
        // fallback is neutral layout information, not a dismissible technical banner.
        UpdateInfoLine(); // GL5c: the info block shows while a segment of its line or a safety line does (and sets the dock margin when that changes)
        ApplyDockMargin();
        // The video bar is a plan block (Transport); its docked host is visible only while the dock hosts it and the video is shown (the
        // transport left the player, so the player's own visibility no longer hides it).
        _dockTransportHost.IsVisible = GalleryTransportPlacement.DockRowVisible(plan.Transport, TransportHostKind, _video?.IsVisible == true);
        RefreshLayoutMenu(); // GL3: an open layout menu shows what the plan now says (a width, mode or state change under it)
        if (focusLost) RescueChromeFocus();
    }

    // GL2: the dock sits directly under the picture (the video bar is the first row of the tools surface); only the Windows fallback, whose
    // bar stays under the picture as a full-width strip, keeps the 8 dip it always had. GL1+GL2: a bottom filmstrip shown in the dock is
    // the dock's first row and takes that margin itself (GalleryFilmstripPlan.BottomBandTop), so the tools surface follows it without a
    // gap. Called from ApplyChrome and, when the band comes or goes in the dock, from UpdateFilmstripPlacement.
    private void ApplyDockMargin() =>
        _chrome.Margin = GalleryTransportPlacement.DockMargin(_nativeOverlayFallback, _infoOverlay.IsVisible, _filmstripHeadsDock);

    // GL2: the video bar's row above the tools; it shares the dock's surface, so the rows are 4 dip apart and there is no second frame.
    private Control WithDockTransport(Control tools)
    {
        StackPanel dock = new() { Spacing = 4 };
        dock.Children.Add(_dockTransportHost);
        dock.Children.Add(tools);
        return dock;
    }

    // Movable tools follow the plan (the view, the mode and the width); the slideshow toggle has its own home (R6).
    private void PlaceChromeTools(GalleryChromePlan plan)
    {
        foreach (ToolSlot slot in _toolSlots)
        {
            if (plan.OnBar(slot.Id)) PlaceToolOnBar(slot);
            else PlaceToolInMenu(slot);
        }
        PlaceSlideshowToggle();
        PresentMovableTools();
        RefreshOverflowItems();
    }

    private void LayOutControls(GalleryChromePlan plan)
    {
        if (_controlsGrid == null) return;
        Control toolbar = _controlsGrid.Children[0];
        Grid.SetColumnSpan(toolbar, 2);
        Grid.SetRow(_taggingStrip, 1);
        Grid.SetColumn(_taggingStrip, 0);
        Grid.SetColumnSpan(_taggingStrip, 2);
        _taggingStrip.BorderThickness = new Thickness(0, 1, 0, 0);
        _taggingStrip.Padding = new Thickness(0, 8, 0, 0);
        _taggingStrip.Margin = new Thickness(0, 8, 0, 0);
        _taggingStrip.VerticalAlignment = VerticalAlignment.Stretch;
    }

    // A block that held the keyboard focus left the screen with a mode change: the focus goes to the switch (the mode's half), else to
    // "More", else to the picture, never to nothing.
    private void RescueChromeFocus()
    {
        NavigationMethod method = _keyboardInteraction ? NavigationMethod.Tab : NavigationMethod.Pointer;
        if (_modeTools.IsEffectivelyVisible) (IsTagMode ? _modeTag : _modeView).Focus(method);
        else if (_overflowButton.IsEffectivelyVisible) _overflowButton.Focus(method);
        else FocusGallerySurface();
    }

    // ---- the slideshow in the tagging mode (Tom's E3 addition) -------------------------------------------------------------

    /// <summary>The side panel stops the slideshow: the post always does; the tag panel only outside the tagging mode.</summary>
    private bool SlideshowBlockedByPanel => TaggingSlideshowHold.PanelBlocks(_contextOpen, _tagPanelOpen, IsTagMode);

    /// <summary>The tag panel is being worked in: the focus is in it, the field has text or its suggestion list is open.</summary>
    private bool TagPanelBusy => _tagSurface is { } surface && TaggingSlideshowHold.Holds(IsTagMode, _tagPanelOpen,
        _contextPanel.IsKeyboardFocusWithin, !string.IsNullOrEmpty(surface.Input.Text), surface.Input.IsDropDownOpen);

    /// <summary>Whether the tag panel holds a running slideshow now (tests and smokes).</summary>
    internal bool IsSlideshowHeldByTagging => _taggingHold;

    // Called by the interaction tick before the slideshow's hold is set: when the panel's hold ends the shown medium starts a fresh period.
    private bool UpdateTaggingHold()
    {
        bool busy = TagPanelBusy;
        if (_taggingHold && !busy && _slideshow.IsRunning) _slideshow.RenewAfterHold(_slideshowGeneration);
        _taggingHold = busy;
        return busy;
    }
}
