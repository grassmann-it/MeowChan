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
    /// <summary>The input the current plan was resolved from (tests, smokes and GL3's menu rows).</summary>
    internal GalleryChromeInput ChromeInput => BuildChromeInput();
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

    /// <summary>The window tells whether the shown list is a walk of the tagging queue (open or parked); the switch's Tag half follows.</summary>
    internal void SetTaggingAvailable(bool available)
    {
        if (_taggingAvailable == available) return;
        _taggingAvailable = available;
        PresentModeSwitch();
        ApplyChrome();
    }

    /// <summary>The person's layout choice of <paramref name="mode"/> (null members: the view's default). GL0 hook for GL3 and GL4.</summary>
    internal GalleryLayoutChoice LayoutChoice(GalleryChromeMode mode) => mode == GalleryChromeMode.Tag ? _tagChoice : _viewChoice;

    /// <summary>Sets the layout choice of one mode (GL4 on load, GL3's menu on a change); the chrome follows at once when it is the shown mode.</summary>
    internal void SetLayoutChoice(GalleryChromeMode mode, GalleryLayoutChoice choice)
    {
        ArgumentNullException.ThrowIfNull(choice);
        if (choice == LayoutChoice(mode)) return;
        if (mode == GalleryChromeMode.Tag) _tagChoice = choice; else _viewChoice = choice;
        ApplyChrome();
        UpdateFilmstripPlacement();
        RefreshSlideshowStatus();
    }

    // The switch is the first group of the toolbar: it names the mode the rest of the chrome follows.
    private void InitializeModeSwitch(StackPanel group)
    {
        _modeTools = group;
        _modeTools.Spacing = 4;
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
        _modeView.Content = view;
        _modeTag.Content = tagLabel;
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
        GalleryLayoutChoice choice = LayoutChoice(mode);
        // Until GL4 persists the filmstrip in the choice, the profile setting and the person's own L in a view that leaves it away (R6)
        // are the filmstrip's choice: off, on, or the view's default.
        if (choice.Filmstrip == null) choice = choice with { Filmstrip = !_filmstripSetting ? false : _filmstripForced ? true : null };
        // ControlsHidden stays false here: the overlay footer hides as a whole (GalleryOverlayActivity); GL1/GL2 read it for their hosts.
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
        Show(_contextToggle, plan.PanelToggle);
        Show(_favorite, plan.Favorite);
        Show(_mediaTools, plan.PanelToggle || plan.Favorite);
        Show(_viewTools, plan.More);
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
        _infoOverlay.IsVisible = !_mediaFocus || _slideshowStatus.IsVisible;
        _chrome.Margin = new Thickness(8, 8, 8, _infoOverlay.IsVisible ? 0 : 8);
        if (focusLost) RescueChromeFocus();
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

    // Tag mode from the wide width on: one row (the toolbar, then the decisions and the progress); below it two rows (plan §3.1).
    private void LayOutControls(GalleryChromePlan plan)
    {
        if (_controlsGrid == null) return;
        bool oneRow = plan.DecisionRow && !plan.StripTwoRows;
        Control toolbar = _controlsGrid.Children[0];
        Grid.SetColumnSpan(toolbar, oneRow ? 1 : 2);
        Grid.SetRow(_taggingStrip, oneRow ? 0 : 1);
        Grid.SetColumn(_taggingStrip, oneRow ? 1 : 0);
        Grid.SetColumnSpan(_taggingStrip, oneRow ? 1 : 2);
        _taggingStrip.BorderThickness = new Thickness(oneRow ? 1 : 0, oneRow ? 0 : 1, 0, 0); // a hairline left of or above the strip
        _taggingStrip.Padding = oneRow ? new Thickness(8, 0, 0, 0) : new Thickness(0, 8, 0, 0);
        _taggingStrip.Margin = oneRow ? new Thickness(8, 0, 0, 0) : new Thickness(0, 8, 0, 0);
        _taggingStrip.VerticalAlignment = oneRow ? VerticalAlignment.Center : VerticalAlignment.Stretch;
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
