using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.WorkViews;
using MeowChan.Gallery;

namespace MeowChan.Desktop;

// WV4 (plan AP5 §1.6): the gallery follows WorkViewPolicy.Gallery.
//   * A movable tool is one control that the view keeps either on the toolbar (Direct) or as a row of the "More" (⋯) flyout
//     (Overflow). The viewer moves the very same instance, so command, enabled state, tooltip and shortcut exist once. The
//     shortcuts never depended on where a button sits: the key handler routes them by command (OnKeyDown).
//   * Free elements that a view reduces go through WorkViewGate (the "where am I" scope line).
//   * The standalone viewer starts in Barebones, which is the layout the gallery always had; the window pushes its view
//     (MainWindow.WorkViews) when it opens a gallery and on every change.
//   * A dialog that a reduced action opened returns focus to "More" (A19), see FocusReturnTarget.
internal sealed partial class GalleryViewer
{
    /// <summary>Room the top row (the way back with the favourite star beside it) takes: 12 margin + the button + an 8 gap. The post
    /// panel and the shortcut help start below it, so they never cover the way back.</summary>
    internal const double TopRowClearance = 56;

    private sealed class ToolSlot(string id, Control control, Panel menuHost, Control? anchor, Control? caption, Action<Button>? addRow,
        bool iconOnly)
    {
        internal string Id { get; } = id;
        internal Control Control { get; } = control;
        internal Panel MenuHost { get; } = menuHost;
        /// <summary>The control the flyout row sits before (null: at the end); stable because no view moves it.</summary>
        internal Control? Anchor { get; } = anchor;
        /// <summary>A caption above the control in the flyout, hidden while the control is on the bar.</summary>
        internal Control? Caption { get; } = caption;
        internal Action<Button>? AddRow { get; } = addRow;
        internal bool IconOnly { get; } = iconOnly;
        internal int Order => WorkViewPolicy.Get(Id).OverflowOrder;
        internal bool OnBar { get; set; }
    }

    // Marks a button that sits on the toolbar although it was built as a flyout row (SetButtonLabel keeps its icon content).
    private static readonly object ToolbarTool = new();

    private readonly List<ToolSlot> _toolSlots = new();
    private readonly Dictionary<string, Control> _registeredWorkViewControls = new(StringComparer.Ordinal);
    private StackPanel _workViewTools = null!;
    private WorkView _workView = WorkView.Barebones;

    /// <summary>The view the viewer was last laid out for (tests and smokes compare it with the window's view).</summary>
    internal WorkView CurrentWorkView => _workView;

    /// <summary>Whether the media focus (tools hidden) is on; the tool group is hidden with the other secondary tools then.</summary>
    internal bool IsMediaFocus => _mediaFocus;

    /// <summary>The group of the toolbar that holds the tools a view brings onto the bar.</summary>
    internal Panel WorkViewToolGroup => _workViewTools;

    /// <summary>Every policy id this viewer registered (the smoke checks that no policy row is orphaned).</summary>
    internal IReadOnlyList<string> RegisteredWorkViewIds => _registeredWorkViewControls.Keys.ToArray();

    /// <summary>The control registered under a policy id (null for a row whose control does not exist, such as the transport before a video).</summary>
    internal Control? WorkViewControl(string id) => _registeredWorkViewControls.GetValueOrDefault(id);

    private void Mark(Control control) => _registeredWorkViewControls[WorkViewElement.GetId(control)!] = control;

    /// <summary>Ids of the movable tools on the bar right now, in policy order (the slideshow toggle counts when it sits on the bar).</summary>
    internal IReadOnlyList<string> ToolbarToolIds => WorkViewPolicy.GalleryMovableToolIds
        .Where(id => id == "gallery.toolbar.slideshow" ? SlideshowToggleOnBar : _toolSlots.Any(slot => slot.Id == id && slot.OnBar)).ToArray();

    /// <summary>Ids of the movable tools under "More" right now, in policy order.</summary>
    internal IReadOnlyList<string> MenuToolIds => WorkViewPolicy.GalleryMovableToolIds.Except(ToolbarToolIds).ToArray();

    private bool SlideshowToggleOnBar => _slideshowToggle.Tag is not OverflowItem && _slideshowToggle.Parent != null;

    /// <summary>Whether the slideshow toggle belongs on the bar: the view says so, or a slideshow is chosen or running (R6).</summary>
    private bool SlideshowOnBarByView => _slideshow != null && WorkViewPolicy.OnSurface(WorkViewPolicy.Resolve("gallery.toolbar.slideshow",
        _workView, nonDefaultState: _playbackPreferences.Mode == GalleryPlaybackMode.Slideshow || _slideshow.IsRunning));

    // Id registration (WorkViewElement): one literal call per control, so the policy lint sees every id.
    private void RegisterWorkViewIds()
    {
        WorkViewElement.SetId(_close, "gallery.back"); Mark(_close);
        WorkViewElement.SetId(_previous, "gallery.nav.previous"); Mark(_previous);
        WorkViewElement.SetId(_next, "gallery.nav.next"); Mark(_next);
        WorkViewElement.SetId(_compactPosition, "gallery.position"); Mark(_compactPosition);
        WorkViewElement.SetId(_favorite, "gallery.favorite"); Mark(_favorite);
        WorkViewElement.SetId(_favoriteIndicator, "gallery.favorite.indicator"); Mark(_favoriteIndicator);
        WorkViewElement.SetId(_toolsToggle, "gallery.toolbar.tools-toggle"); Mark(_toolsToggle);
        WorkViewElement.SetId(_overflowButton, "gallery.toolbar.more"); Mark(_overflowButton);
        WorkViewElement.SetId(_readPosts, "gallery.toolbar.read-posts"); Mark(_readPosts);
        WorkViewElement.SetId(_contextToggle, "gallery.toolbar.post-panel"); Mark(_contextToggle);
        WorkViewElement.SetId(_contextPanel, "gallery.post-panel"); Mark(_contextPanel);
        WorkViewElement.SetId(_sidePanel.PostButton, "gallery.panel.post"); Mark(_sidePanel.PostButton);
        WorkViewElement.SetId(_sidePanel.TagsButton, "gallery.panel.tags"); Mark(_sidePanel.TagsButton);
        WorkViewElement.SetId(_fit, "gallery.toolbar.fit"); Mark(_fit);
        WorkViewElement.SetId(_actual, "gallery.toolbar.actual"); Mark(_actual);
        WorkViewElement.SetId(_ratingButton, "gallery.toolbar.rating"); Mark(_ratingButton);
        WorkViewElement.SetId(_tagsButton, "gallery.toolbar.tags"); Mark(_tagsButton);
        WorkViewElement.SetId(_slideshowToggle, "gallery.toolbar.slideshow"); Mark(_slideshowToggle);
        WorkViewElement.SetId(_slideshowSeconds, "gallery.toolbar.slideshow-seconds"); Mark(_slideshowSeconds);
        WorkViewElement.SetId(_playbackMode, "gallery.toolbar.playback-mode"); Mark(_playbackMode);
        WorkViewElement.SetId(_helpButton, "gallery.toolbar.help"); Mark(_helpButton);
        WorkViewElement.SetId(_zoomIn, "gallery.menu.zoom-in"); Mark(_zoomIn);
        WorkViewElement.SetId(_zoomOut, "gallery.menu.zoom-out"); Mark(_zoomOut);
        WorkViewElement.SetId(_manageRatingsButton, "gallery.menu.manage-ratings"); Mark(_manageRatingsButton);
        WorkViewElement.SetId(_exportOriginal, "gallery.menu.export-original"); Mark(_exportOriginal);
        WorkViewElement.SetId(_saveFrame, "gallery.menu.save-frame"); Mark(_saveFrame);
        WorkViewElement.SetId(_videoRepeatToggle, "gallery.menu.video-repeat"); Mark(_videoRepeatToggle);
        WorkViewElement.SetId(_videoRule, "gallery.menu.video-rule"); Mark(_videoRule);
        WorkViewElement.SetId(_overlayToggle, "gallery.menu.overlay-toggle"); Mark(_overlayToggle);
        WorkViewElement.SetId(_postOverlayToggle, "gallery.menu.post-overlay-toggle"); Mark(_postOverlayToggle);
        WorkViewElement.SetId(_fullscreenButton, "gallery.menu.fullscreen"); Mark(_fullscreenButton);
        WorkViewElement.SetId(_quality, "gallery.info.quality"); Mark(_quality);
        WorkViewElement.SetId(_provenance, "gallery.info.provenance"); Mark(_provenance);
        WorkViewElement.SetId(_status, "gallery.status.media-unavailable"); Mark(_status);
        WorkViewElement.SetId(_fileOnlyPlate, "gallery.status.file-only"); Mark(_fileOnlyPlate);
        WorkViewElement.SetId(_favoriteError, "gallery.status.favorite-error"); Mark(_favoriteError);
        WorkViewElement.SetId(_ratingStatus, "gallery.status.rating"); Mark(_ratingStatus);
        WorkViewElement.SetId(_slideshowStatus, "gallery.status.slideshow"); Mark(_slideshowStatus);
        WorkViewElement.SetId(_overlayFallbackNotice, "gallery.status.overlay-fallback"); Mark(_overlayFallbackNotice);
        WorkViewElement.SetId(_favoriteConfirmPanel, "gallery.favorite.confirm"); Mark(_favoriteConfirmPanel);
        // The scope line is registered first in the constructor (its gate runs before this method); the transport when the player exists.
        Mark(_scope);
    }

    // The movable tools. The anchors are rows no view moves, so a row returns to its place whatever order the views moved them in.
    private void InitializeWorkViewTools()
    {
        _workViewTools.IsVisible = false;
        _toolSlots.Add(new("gallery.toolbar.rating", _ratingButton, _overflowPanel, _manageRatingsButton, null,
            button => AddOverflowItem(button, "media.rating.edit", label: () => CommandCatalog.Label("media.rating.edit")), iconOnly: false));
        _toolSlots.Add(new("gallery.toolbar.tags", _tagsButton, _overflowPanel, _exportOriginal, null,
            button => AddOverflowItem(button, "media.tags.edit", label: TagsLabel), iconOnly: false));
        _toolSlots.Add(new("gallery.toolbar.slideshow-seconds", _slideshowSeconds, _slideshowHome, _videoRuleCaption, _secondsCaption, null, iconOnly: false));
        _toolSlots.Add(new("gallery.toolbar.playback-mode", _playbackMode, _slideshowHome, _secondsCaption, _modeCaption, null, iconOnly: false));
        _toolSlots.Add(new("gallery.toolbar.help", _helpButton, _overflowPanel, null, null,
            button => AddOverflowItem(button, "viewer.shortcut-help", label: () => CommandCatalog.Label("viewer.shortcut-help")), iconOnly: true));
        _toolSlots.Sort((a, b) => a.Order.CompareTo(b.Order));
    }

    private ToolSlot? SlotOf(Control control) => _toolSlots.FirstOrDefault(slot => ReferenceEquals(slot.Control, control));

    private bool ToolInMenu(string id) => _toolSlots.Any(slot => slot.Id == id && !slot.OnBar);

    /// <summary>
    /// Lays the viewer out for <paramref name="view"/> (plan §1.6): movable tools go to the bar or under "More", the "where am I"
    /// line follows the gate, and the slideshow toggle follows the view unless a slideshow is chosen or running.
    /// </summary>
    internal void ApplyWorkView(WorkView view)
    {
        if (_terminalRetirement) return;
        bool focusInMenu = _overflowFlyout.IsOpen;
        _workView = view;
        foreach (ToolSlot slot in _toolSlots)
        {
            bool onBar = WorkViewPolicy.OnSurface(WorkViewPolicy.Resolve(slot.Id, view));
            if (onBar) PlaceToolOnBar(slot);
            else PlaceToolInMenu(slot);
        }
        _workViewTools.IsVisible = _workViewTools.Children.Count > 0;
        PlaceSlideshowToggle();
        PresentMovableTools();
        UpdateMediaFocusVisibility();
        RefreshOverflowItems();
        UpdateFilmstripPlacement();
        if (focusInMenu) _overflowFlyout.Hide(); // The rows changed under an open menu: close it, focus goes back to "More".
    }

    private void PlaceToolOnBar(ToolSlot slot)
    {
        if (slot.OnBar) return;
        Control control = slot.Control;
        bool hadFocus = control.IsKeyboardFocusWithin;
        (control.Parent as Panel)?.Children.Remove(control);
        if (control is Button button)
        {
            if (button.Tag is OverflowItem) _overflowButtons.Remove(button);
            button.Tag = ToolbarTool;
            StyleAsOverflowRow(button, false);
        }
        else
        {
            control.Margin = default;
            control.HorizontalAlignment = HorizontalAlignment.Left;
            control.VerticalAlignment = VerticalAlignment.Center;
        }
        slot.OnBar = true;
        int index = _workViewTools.Children.Count(child => SlotOf(child) is { } other && other.Order < slot.Order);
        _workViewTools.Children.Insert(index, control);
        if (slot.Caption != null) slot.Caption.IsVisible = false;
        if (hadFocus && control.IsEffectivelyVisible) control.Focus(NavigationMethod.Tab);
    }

    private void PlaceToolInMenu(ToolSlot slot)
    {
        if (!slot.OnBar && slot.Control.Parent != null) return;
        Control control = slot.Control;
        bool hadFocus = control.IsKeyboardFocusWithin;
        (control.Parent as Panel)?.Children.Remove(control);
        slot.OnBar = false;
        if (control is Button button)
        {
            button.Tag = null;
            button.Classes.Set("command-icon", false);
            slot.AddRow!(button); // appends to the flyout (and to _overflowButtons)
            slot.MenuHost.Children.Remove(button);
        }
        else
        {
            control.Margin = new Thickness(8, 0);
            control.HorizontalAlignment = HorizontalAlignment.Stretch;
        }
        int anchor = slot.Anchor == null ? -1 : slot.MenuHost.Children.IndexOf(slot.Anchor);
        if (anchor >= 0) slot.MenuHost.Children.Insert(anchor, control); else slot.MenuHost.Children.Add(control);
        if (slot.Caption != null) slot.Caption.IsVisible = true;
        if (control is Button) UpdateTagsButton(); // the row's label follows the live tag count again
        if (hadFocus && _overflowButton.IsEffectivelyVisible) _overflowButton.Focus(NavigationMethod.Tab);
    }

    // Icon with label (or the icon alone on a narrow viewer) for the buttons now on the bar; the name, tooltip and shortcut stay.
    private void PresentMovableTools()
    {
        foreach (ToolSlot slot in _toolSlots.Where(slot => slot.OnBar && slot.Control is Button))
        {
            Button button = (Button)slot.Control;
            string id = WorkViewPolicy.Get(slot.Id).CommandId!;
            bool iconOnly = _compactToolbar || slot.IconOnly;
            CommandBindings.Apply(button, id, iconOnly: iconOnly, iconWithLabel: !iconOnly);
            button.Tag = ToolbarTool;
        }
        UpdateTagsButton();
        UpdateHelpButtonTooltip(); // the help button's tooltip names the favourite and post keys as well
    }

    /// <summary>
    /// A19: where focus goes when the dialog of <paramref name="commandId"/> closes. An action the view keeps under "More" returns
    /// to "More" of the toolbar, never to the closed menu's hidden row; otherwise the origin stays (it may be null or hidden:
    /// the caller then focuses the gallery surface).
    /// </summary>
    internal Control? FocusReturnTarget(Control? origin, string commandId)
    {
        bool reduced = WorkViewPolicy.GalleryCommandReduced(commandId, _workView);
        if (reduced && _overflowButton is { IsEffectivelyVisible: true, IsEffectivelyEnabled: true }) return _overflowButton;
        return origin;
    }
}
