using System;
using System.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.MediaActions;

namespace MeowChan.Desktop;

// GL3 (PLAN-GALERIE-LAYOUT-BLOECKE §3, Tom's E2): the "Layout" button beside "More" and the same rows on a right click of free dock area.
//   * The button opens a flyout (LayoutMenuPanel) with the rows of GalleryLayoutMenu.Rows: filmstrip, its position, tools, info line, hints
//     and "Reset layout". A switch or a choice keeps the flyout open and shows its new state at once; "Reset layout" closes it and offers the
//     undo (LayoutResetPerformed; the window shows the toast). Escape closes the menu and the focus returns to the button.
//   * A right click on free area of the dock (the tools surface, the info line, the filmstrip band; not on a button, slider or thumbnail, and
//     not on the picture, which keeps the media menu) opens the same rows as a ContextMenuBuilder menu; so do the Menu key and Shift+F10 with
//     the focus in the dock.
//   * Both edit the layout of the CURRENT mode (the menu's title says which). The choice is applied by ChangeLayoutChoice, which raises
//     LayoutChoiceChanged for the window to save per profile and mode (GL4); ApplyChrome stays the one place that applies visibility.
// Texts: gallery.layout.* (GalleryLayoutCatalog); the command gallery.layout-menu opens the menu from the keyboard (unbound by default).
internal sealed partial class GalleryViewer
{
    private readonly Button _layoutButton = new() { Name = "GalleryLayoutButton", Classes = { "media-control" }, MinHeight = 32 };
    private readonly Flyout _layoutFlyout = new() { Placement = PlacementMode.TopEdgeAlignedRight };
    private readonly TextBlock _layoutButtonText = new() { VerticalAlignment = VerticalAlignment.Center };
    private LayoutMenuPanel _layoutPanel = null!;
    // The same rows as a section of "More" while the button is not on the bar (Simple and Barebones, and below CompactWidth in every view).
    private readonly StackPanel _layoutSection = new() { Name = "GalleryLayoutSection", Spacing = 2, IsVisible = false };
    private LayoutMenuPanel _layoutSectionPanel = null!;
    private ContextMenuHost? _layoutContextHost;
    private bool _layoutMenuHadFocus;

    /// <summary>The "Layout" button beside "More" (tests and smokes).</summary>
    internal Button LayoutButton => _layoutButton;
    internal Flyout LayoutFlyout => _layoutFlyout;
    internal bool IsLayoutMenuOpen => _layoutFlyout.IsOpen;
    /// <summary>The rows of the flyout (tests and smokes).</summary>
    internal LayoutMenuPanel LayoutMenuRows => _layoutPanel;
    /// <summary>The section of "More" that holds the same rows while the button is not on the bar (tests and smokes).</summary>
    internal Panel LayoutSection => _layoutSection;
    internal LayoutMenuPanel LayoutSectionRows => _layoutSectionPanel;
    /// <summary>The right-click (context) form of the layout menu, while it is open.</summary>
    internal ContextMenu? LayoutContextMenu => _layoutContextHost?.OpenMenu;

    /// <summary>"Reset layout" ran for <c>mode</c>; the second argument is the choice it replaced, so the window can offer the undo with <see cref="ChangeLayoutChoice"/>.</summary>
    internal event Action<GalleryChromeMode, GalleryLayoutChoice>? LayoutResetPerformed;

    /// <summary>The menu for the mode on the screen now: what is shown, the person's choice, the work view's default and the state's reasons.</summary>
    internal LayoutMenuModel LayoutMenuNow()
    {
        GalleryChromeMode mode = ShownChromeMode;
        return GalleryLayoutMenu.Rows(BuildChromePlan(), EffectiveLayoutChoice(mode), GalleryChromePlan.ViewDefault(mode, _workView), BuildChromeInput());
    }

    /// <summary>Opens or closes the layout flyout without a pointer or key (smokes and tests); false when the button is not on the screen.</summary>
    internal bool SetLayoutMenuOpen(bool open)
    {
        if (!open) { if (_layoutFlyout.IsOpen) _layoutFlyout.Hide(); return true; }
        if (_layoutFlyout.IsOpen) return true;
        if (_terminalRetirement || !_layoutButton.IsEffectivelyVisible) return false;
        CloseOtherMenus();
        _layoutFlyout.ShowAt(_layoutButton);
        return true;
    }

    /// <summary>Opens the right-click form of the menu without a pointer (smokes and tests, the keyboard commands).</summary>
    internal bool ShowLayoutContextMenu(bool atPointer = false)
    {
        if (_terminalRetirement || _layoutContextHost == null || !_layoutButton.IsEffectivelyVisible && !_overflowButton.IsEffectivelyVisible) return false;
        CloseOtherMenus();
        HoldSlideshowAdvance();
        RevealOverlayControls();
        _layoutContextHost.Show(atPointer);
        return _layoutContextHost.OpenMenu != null;
    }

    private void InitializeLayoutMenu()
    {
        _layoutButton.Content = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center,
            Children = { CommandBindings.NamedIcon("sliders", 16), _layoutButtonText, ChevronDown() }
        };
        _layoutPanel = new LayoutMenuPanel("GalleryLayoutMenu", LayoutMenuNow, ApplyLayoutRow);
        _layoutPanel.CloseRequested += () => _layoutFlyout.Hide();
        _layoutFlyout.FlyoutPresenterClasses.Add("gallery-overflow");
        _layoutFlyout.Content = new ScrollViewer
        {
            Content = _layoutPanel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        // The section of "More" for the placements without the button (UX spec S4 GL3+a): a caption line, the same rows, the same apply.
        _layoutSectionPanel = new LayoutMenuPanel("GalleryLayoutSectionRows", LayoutMenuNow, ApplyLayoutRow);
        _layoutSectionPanel.CloseRequested += () => _overflowFlyout.Hide();
        _layoutSection.Children.Add(new Border
        {
            Height = 1, Margin = new Thickness(8, 4), IsHitTestVisible = false,
            [!Border.BackgroundProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("Workspace.Border")
        });
        _layoutSection.Children.Add(_layoutSectionPanel);
        _overflowPanel.Children.Add(_layoutSection);
        _overflowFlyout.Opening += (_, _) => { if (_layoutSection.IsVisible) _layoutSectionPanel.Refresh(); };
        _layoutButton.Flyout = _layoutFlyout; // a click, Enter or Space opens it; an open flyout is a popup (HasGalleryPopup)
        _layoutPanel.AddHandler(KeyDownEvent, (_, e) => OnMenuKeyDown(e, _layoutPanel, _layoutFlyout), RoutingStrategies.Tunnel);
        _layoutPanel.GotFocus += (_, _) => _layoutMenuHadFocus = true;
        _layoutFlyout.Opening += (_, _) =>
        {
            _layoutMenuHadFocus = false;
            CloseOtherMenus();
            HoldSlideshowAdvance();
            if (_layoutFlyout.Content is ScrollViewer scroller) scroller.MaxHeight = Math.Max(160, Bounds.Height - 72);
            _layoutPanel.Refresh(); // the rows show what is on the screen at this moment
        };
        _layoutFlyout.Opened += (_, _) =>
        {
            RevealOverlayControls();
            RefreshPlaybackControls();
            // The keyboard continues in the menu: the first usable row.
            Dispatcher.UIThread.Post(() => MenuFocusables(_layoutPanel).FirstOrDefault()?.Focus(NavigationMethod.Tab), DispatcherPriority.Loaded);
        };
        _layoutFlyout.Closed += (_, _) =>
        {
            RefreshPlaybackControls();
            RevealOverlayControls();
            if (!_layoutMenuHadFocus || _terminalRetirement || !_layoutButton.IsEffectivelyVisible) return;
            _layoutMenuHadFocus = false;
            // A keyboard user returns to the button the menu came from, never to a block that may have left the screen; a menu opened with the pointer hands the keys to the picture.
            if (_keyboardInteraction) _layoutButton.Focus(NavigationMethod.Tab);
            else ReclaimGallerySurface();
        };
        // The same rows on a right click of free dock area; the host also opens them for the Menu key and Shift+F10 on the button.
        _layoutContextHost = new ContextMenuHost(_layoutButton,
            () => LayoutMenuBuilder.ToContextMenu(LayoutMenuNow(), ApplyLayoutRow, Texts.T("gallery.layout.button")), FocusGallerySurface,
            () => _layoutButton.IsEffectivelyVisible ? _layoutButton : _overflowButton); // under "More" the menu opens on that button
        foreach (Control area in new Control[] { _dockStack, _overlayFooter, _filmstrip })
            area.AddHandler(ContextRequestedEvent, OnDockContextRequested, RoutingStrategies.Bubble);
        PresentLayoutButton();
    }

    // Name, tooltip and help of the button, from the current language, the shortcut and the density: with the labelled bar it shows its name,
    // below CompactToolbarWidth only the icon and the arrow (the name stays the accessible name).
    private void PresentLayoutButton()
    {
        if (_layoutButton.Content == null) return;
        string name = Texts.T("gallery.layout.button"), help = Texts.T("gallery.layout.help");
        _layoutButtonText.Text = name;
        _layoutButtonText.IsVisible = !_compactToolbar;
        string tip = CommandBindings.Tooltip(GalleryLayoutMenu.OpenCommandId, null, name) + "\n" + help;
        AutomationProperties.SetName(_layoutButton, name);
        AutomationProperties.SetHelpText(_layoutButton, help);
        AutomationProperties.SetAcceleratorKey(_layoutButton, CommandCatalog.ShortcutDisplay(GalleryLayoutMenu.OpenCommandId) ?? string.Empty);
        ToolTip.SetTip(_layoutButton, tip);
        _layoutButton.Padding = _compactToolbar ? new Thickness(8, 0) : new Thickness(12, 0);
        RefreshLayoutMenu();
    }

    /// <summary>Keeps an open layout menu in step with the screen (the width, the work view or the mode changed under it).</summary>
    private void RefreshLayoutMenu()
    {
        if (_layoutPanel != null && _layoutFlyout.IsOpen) _layoutPanel.Refresh();
        if (_layoutSectionPanel != null && _layoutSection.IsVisible && _overflowFlyout.IsOpen) _layoutSectionPanel.Refresh();
    }

    // One menu at a time: the layout menu, the "More" menu and the slideshow's flyout close each other.
    private void CloseOtherMenus()
    {
        if (_overflowFlyout.IsOpen) _overflowFlyout.Hide();
        CloseSlideshowMenu();
        _layoutContextHost?.Close();
        if (_layoutFlyout.IsOpen) _layoutFlyout.Hide();
    }

    private void CloseLayoutMenu()
    {
        if (_layoutFlyout.IsOpen) _layoutFlyout.Hide();
        _layoutContextHost?.Close();
    }

    // A row was chosen (flyout or context menu): the new choice of the shown mode, applied and reported for saving (GL4).
    private void ApplyLayoutRow(LayoutMenuRow row)
    {
        if (_terminalRetirement || !row.Enabled) return;
        GalleryChromeMode mode = ShownChromeMode;
        if (row.Id == GalleryLayoutMenu.ResetId)
        {
            GalleryLayoutChoice previous = ResetLayout(mode);
            if (previous != GalleryLayoutChoice.Default) LayoutResetPerformed?.Invoke(mode, previous);
            return;
        }
        GalleryLayoutChoice? next = GalleryLayoutMenu.Apply(row.Id, BuildChromePlan(), EffectiveLayoutChoice(mode),
            GalleryChromePlan.ViewDefault(mode, _workView), BuildChromeInput());
        if (next == null) return;
        bool filmstrip = row.Id is GalleryLayoutMenu.FilmstripId or GalleryLayoutMenu.FilmstripTopId or GalleryLayoutMenu.FilmstripBottomId;
        if (filmstrip)
        {
            _filmstripPeek = false;
            // A strip that leaves while it held the focus: the focus goes to the picture (the filmstrip key does the same).
            if (row.Id == GalleryLayoutMenu.FilmstripId && BuildChromePlan().Filmstrip && _filmstrip.IsKeyboardFocusWithin) FocusGallerySurface();
        }
        ChangeLayoutChoice(mode, next);
        if (filmstrip) { if (OverlayControlsEffective) RevealOverlayControls(); UpdateFilmstripPlacement(); }
        RefreshLayoutMenu();
    }

    // ---- right click, Menu key and Shift+F10 on free dock area ----

    private void OnDockContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (e.Handled || _terminalRetirement || sender is not Control area) return;
        bool pointer = e.TryGetPosition(this, out _);
        if (pointer)
        {
            if (!IsFreeDockArea(e.Source as Visual, area)) return; // a button, slider or thumbnail keeps its own behaviour
        }
        else if (e.Source is not Visual || MediaMenuPresenter.IsNativeTextSource(e.Source)) return; // the keyboard: only from a control of the dock
        // The key already opened the menu (RouteAdmittedKey): Avalonia's positionless request that may follow it is consumed, not opened again.
        else if (LayoutContextMenu != null) { e.Handled = true; return; }
        if (ShowLayoutContextMenu(pointer)) e.Handled = true;
    }

    /// <summary>
    /// Whether a pointer at <paramref name="source"/> is on free area of the dock part <paramref name="area"/>: nothing between the hit
    /// element and the part is a control that acts on its own (a button, slider, box, number field, text, scroll bar, any focusable
    /// control) or a thumbnail of the filmstrip.
    /// </summary>
    internal bool IsFreeDockArea(Visual? source, Control area)
    {
        for (Visual? node = source; node != null; node = node.GetVisualParent())
        {
            if (node is Control control)
            {
                if (control is Button or Slider or ComboBox or NumericUpDown or TextBox or ScrollBar or Thumb or ListBoxItem
                    || control.Focusable && control.IsTabStop && !ReferenceEquals(control, area)) return false;
                if (_filmstrip.LiveTileIndices.Any(index => ReferenceEquals(_filmstrip.TileControl(index), control))) return false;
            }
            if (ReferenceEquals(node, area)) return true;
        }
        return false;
    }

    // ---- the keyboard commands ----

    // The unbound layout commands (gallery.layout-menu opens the menu; the others change one block or the filmstrip's position).
    private bool HandleLayoutKey(KeyEventArgs e)
    {
        if (CommandBindings.Matches(e, GalleryLayoutMenu.OpenCommandId))
        {
            e.Handled = true;
            RevealControlsForCommand(); // the button lives in the footer: shown first, never a dead key while auto-hidden
            // Without the button on the bar the rows are the section of "More": the command opens that menu.
            if (_layoutFlyout.IsOpen) _layoutFlyout.Hide();
            else if (!_layoutButton.IsEffectivelyVisible && _layoutSection.IsVisible) SetOverflowOpen(!_overflowFlyout.IsOpen);
            else if (!SetLayoutMenuOpen(true)) ShowLayoutContextMenu(); // the button is not on the screen: the same rows as its context form
            return true;
        }
        foreach (string id in GalleryLayoutMenu.RowIds)
        {
            // The filmstrip's own switch is the filmstrip key (HandleFilmstripKey); its row has no command of its own.
            if (id == GalleryLayoutMenu.FilmstripId || !CommandBindings.Matches(e, id)) continue;
            e.Handled = true;
            if (LayoutMenuNow().Find(id) is { Enabled: true } row) ApplyLayoutRow(row);
            return true;
        }
        return false;
    }
}
