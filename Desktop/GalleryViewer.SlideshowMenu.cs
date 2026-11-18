using System;
using System.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Threading;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;

namespace MeowChan.Desktop;

// G2 (PLAN-TAGGING-GALERIE-UX-1.1 §3.2, §6): the slideshow is one split control on the bar. The main part (the toggle) starts and pauses;
// the menu button beside it (a Workspace.Size.Control square, joined to it) - or the context request of the toggle (right click, long press, the menu key) - opens a flyout with the
// slideshow's settings: the playback mode, the seconds per image (with its caption), the rule for videos and the reduced-motion note.
//
// One panel, two ways: the settings are the rows of _slideshowHome, which sit under "⋯" as before. While the flyout is open it borrows the
// panel (it becomes the flyout's content); when the flyout closes, the panel goes back to its place in the "⋯" menu. The controls exist
// once, so values, labels, tooltips and shortcuts cannot drift between the two ways.
internal sealed partial class GalleryViewer
{
    private readonly SplitMenuButton _slideshowMenuButton = new()
    {
        // Size, edge and corners come from the shared split-button style (WorkspaceStyles.axaml: Workspace.Size.Control square, joined to the toggle).
        Name = "GallerySlideshowMenu", Classes = { "split-menu" }
    };
    // The toggle and its menu button in one group without a gap (the toggle is inserted by PlaceSlideshowToggle).
    private readonly StackPanel _slideshowSplit = new()
    {
        Name = "GallerySlideshowSplit", Orientation = Orientation.Horizontal, Spacing = 0, VerticalAlignment = VerticalAlignment.Center
    };
    private readonly Flyout _slideshowFlyout = new() { Placement = PlacementMode.Top };
    private readonly ScrollViewer _slideshowFlyoutHost = new()
    {
        Name = "GallerySlideshowFlyoutHost", MinWidth = 280, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
    };
    private PanelLoan _slideshowLoan = null!;
    private bool _slideshowMenuHadFocus;

    /// <summary>The split control as the bar holds it: the toggle and the menu button (tests and smokes).</summary>
    internal Panel SlideshowSplit => _slideshowSplit;
    internal Button SlideshowMenuButton => _slideshowMenuButton;
    internal Flyout SlideshowFlyout => _slideshowFlyout;
    internal bool IsSlideshowMenuOpen => _slideshowFlyout.IsOpen;
    /// <summary>The control the flyout holds while it is open (the panel that sits under "⋯" otherwise).</summary>
    internal Panel SlideshowSettingsPanel => _slideshowHome;

    /// <summary>Opens or closes the slideshow settings flyout without a pointer or key (smokes and tests); false when the menu button is not on the bar.</summary>
    internal bool SetSlideshowMenuOpen(bool open)
    {
        if (!open) { CloseSlideshowMenu(); return true; }
        if (_slideshowFlyout.IsOpen) return true;
        if (!SlideshowToggleOnBar || !_slideshowMenuButton.IsEffectivelyVisible || _overflowFlyout.IsOpen || _terminalRetirement) return false;
        _slideshowFlyout.ShowAt(_slideshowMenuButton);
        return true;
    }

    private void InitializeSlideshowMenu()
    {
        _slideshowLoan = new PanelLoan(_overflowPanel, _slideshowHome, _slideshowFlyoutHost);
        _slideshowMenuButton.Content = ChevronDown();
        _slideshowSplit.Children.Add(_slideshowMenuButton);
        _slideshowFlyout.FlyoutPresenterClasses.Add("gallery-overflow");
        _slideshowFlyout.Content = _slideshowFlyoutHost;
        _slideshowMenuButton.Flyout = _slideshowFlyout; // a click, Enter or Space on the button opens it; an open flyout is a popup (HasGalleryPopup)
        KeyboardNavigation.SetTabNavigation(_slideshowFlyoutHost, KeyboardNavigationMode.Cycle); // Tab stays inside the flyout (the host, not the lent panel: under "⋯" the menu cycles)
        _slideshowFlyoutHost.AddHandler(KeyDownEvent, (_, e) => OnMenuKeyDown(e, _slideshowHome, _slideshowFlyout), Avalonia.Interactivity.RoutingStrategies.Tunnel);
        _slideshowFlyoutHost.GotFocus += (_, _) => _slideshowMenuHadFocus = true;
        // Right click, long press and the menu key on the toggle open the same flyout (the toggle itself keeps its click).
        _slideshowToggle.ContextRequested += (_, e) =>
        {
            if (!SlideshowToggleOnBar || e.Handled) return;
            if (SetSlideshowMenuOpen(true)) e.Handled = true;
        };
        _slideshowFlyout.Opening += (_, _) =>
        {
            _slideshowMenuHadFocus = false;
            HoldSlideshowAdvance();
            _slideshowLoan.Lend(); // the settings panel comes from "⋯" and goes back when the flyout closes
            _slideshowFlyoutHost.MaxHeight = Math.Max(160, Bounds.Height - 72);
            RefreshOverflowItems(); // the captions and the rows follow the language and the state at this moment
        };
        _slideshowFlyout.Opened += (_, _) =>
        {
            RevealOverlayControls();
            RefreshPlaybackControls();
            _slideshowMenuButton.RaiseExpandedChanged(true);
            // The focus moves into the flyout so the keyboard continues there: the seconds when they apply, else the first usable row.
            Dispatcher.UIThread.Post(() =>
            {
                Control[] rows = MenuFocusables(_slideshowHome).ToArray();
                (rows.FirstOrDefault(row => ReferenceEquals(row, _slideshowSeconds)) ?? rows.FirstOrDefault())?.Focus(NavigationMethod.Tab);
            }, DispatcherPriority.Loaded);
        };
        _slideshowFlyout.Closed += (_, _) =>
        {
            ReturnSlideshowHome();
            _slideshowMenuButton.RaiseExpandedChanged(false);
            RefreshPlaybackControls();
            RevealOverlayControls();
            if (!_slideshowMenuHadFocus || _terminalRetirement) return;
            _slideshowMenuHadFocus = false;
            // A keyboard user returns to where the flyout came from: the menu button, else (it left the bar) "⋯". A menu opened with the pointer hands the keys to the picture.
            if (!_keyboardInteraction) { ReclaimGallerySurface(); return; }
            Control target = _slideshowMenuButton.IsEffectivelyVisible ? _slideshowMenuButton : _overflowButton;
            if (target.IsEffectivelyVisible) target.Focus(NavigationMethod.Tab);
        };
        PresentSlideshowMenu();
    }

    private static Control ChevronDown()
    {
        var icon = MeowChan.Desktop.Controls.WorkspaceIcons.Create(MeowChan.Desktop.Controls.WorkspaceIcons.ChevronDown);
        icon.Width = 16; icon.Height = 16; // the 16 dip icon size of the bar's other tools
        return icon;
    }

    private void ReturnSlideshowHome() => _slideshowLoan?.Return();

    /// <summary>Closes the flyout and gives the panel back at once (the bar changed under it, or the viewer ends).</summary>
    private void CloseSlideshowMenu()
    {
        if (_slideshowFlyout == null) return;
        if (_slideshowFlyout.IsOpen) _slideshowFlyout.Hide();
        ReturnSlideshowHome();
    }

    // Names, tooltip and help text of the menu button, from the current language.
    private void PresentSlideshowMenu()
    {
        string name = GalleryPlaybackCatalog.Get("viewer.playback.menu");
        string help = GalleryPlaybackCatalog.Get("viewer.playback.menuHelp");
        AutomationProperties.SetName(_slideshowMenuButton, name);
        AutomationProperties.SetHelpText(_slideshowMenuButton, help);
        ToolTip.SetTip(_slideshowMenuButton, name + " · " + help);
        AutomationProperties.SetName(_slideshowSplit, GalleryPlaybackCatalog.Get("viewer.playback.slideshow"));
    }
}
