using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;
using MeowChan.Gallery;

namespace MeowChan.Desktop;

// The gallery toolbar keeps the frequent controls in view (close, previous/next with the position, fit, 1:1, the post
// panel, favorite, the GIF transport) and moves the rare ones into one "⋯" menu: zoom in/out, rating, tags, export,
// playback mode and slideshow, the two overlay choices (with a visible check), full screen and the shortcut help.
// The menu hosts the very same controls, so commands, enabled states, tooltips and shortcuts exist once; the shortcuts
// are routed by the viewer's key handler and work without opening the menu.
internal sealed partial class GalleryViewer
{
    /// <summary>Below this viewer width the labelled primary controls (close, fit, post, favorite, tools) show only their icon.</summary>
    internal const double CompactToolbarWidth = 1100;
    /// <summary>Below this viewer width the post panel overlays the picture instead of taking a column from it.</summary>
    internal const double CompactPostWidth = 900;
    /// <summary>The widest strip of the picture the overlaid post panel leaves free; the panel gives way before the picture does.</summary>
    internal const double MinimumPictureWidth = 300;

    // A menu entry: the row content and the parts that change with language, shortcut and state.
    private sealed class OverflowItem(string id, Grid root, TextBlock label, TextBlock gesture, Control? check, bool keepOpen)
    {
        internal string Id { get; } = id;
        internal Grid Root { get; } = root;
        internal TextBlock Label { get; } = label;
        internal TextBlock Gesture { get; } = gesture;
        internal Control? Check { get; } = check;
        internal bool KeepOpen { get; } = keepOpen;
        internal Func<string>? LabelSource { get; init; }
    }

    private readonly StackPanel _overflowPanel = new() { Name = "GalleryOverflowMenu", Spacing = 2, MinWidth = 280 };
    private readonly Button _overflowButton = new() { Name = "GalleryMore", Classes = { "command-icon" } };
    private readonly Flyout _overflowFlyout = new() { Placement = PlacementMode.TopEdgeAlignedRight };
    private readonly List<Button> _overflowButtons = new();
    private readonly TextBlock _modeCaption = new() { Classes = { "workspace-meta" }, Margin = new Thickness(12, 8, 12, 0) };
    private readonly TextBlock _secondsCaption = new() { Classes = { "workspace-meta" }, Margin = new Thickness(12, 8, 12, 0), IsVisible = false };
    private StackPanel _focusTools = null!;
    private StackPanel _slideshowHome = null!;
    private bool _compactToolbar;
    private bool _contextOverPicture;
    private bool _overflowHadFocus;

    internal Button OverflowButton => _overflowButton;
    internal Flyout OverflowFlyout => _overflowFlyout;
    internal bool IsOverflowOpen => _overflowFlyout.IsOpen;
    internal bool IsCompactToolbar => _compactToolbar;
    internal bool IsContextOverPicture => _contextOverPicture;
    internal IReadOnlyList<Button> OverflowItems => _overflowButtons;
    internal Control OverflowMenuContent => _overflowPanel;

    /// <summary>Opens or closes the "⋯" menu without a pointer or key (smokes and tests).</summary>
    internal void SetOverflowOpen(bool open)
    {
        if (open == _overflowFlyout.IsOpen) return;
        if (open) _overflowFlyout.ShowAt(_overflowButton);
        else _overflowFlyout.Hide();
    }

    private void InitializeOverflowMenu()
    {
        _overflowButton.Content = OverflowGlyph();
        _overflowFlyout.FlyoutPresenterClasses.Add("gallery-overflow");
        _overflowFlyout.Content = new ScrollViewer
        {
            Content = _overflowPanel,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        _overflowButton.Flyout = _overflowFlyout;
        KeyboardNavigation.SetTabNavigation(_overflowPanel, KeyboardNavigationMode.Cycle);
        _overflowPanel.AddHandler(KeyDownEvent, OnOverflowKeyDown, RoutingStrategies.Tunnel);
        _overflowPanel.AddHandler(Button.ClickEvent, OnOverflowItemClick);
        _overflowFlyout.Opening += (_, _) =>
        {
            _overflowHadFocus = false;
            HoldSlideshowAdvance();
            if (_overflowFlyout.Content is ScrollViewer scroller)
                scroller.MaxHeight = Math.Max(160, Bounds.Height - 72);
            RefreshOverflowItems();
        };
        _overflowFlyout.Opened += (_, _) =>
        {
            RevealOverlayControls();
            RefreshPlaybackControls();
            // Move the focus into the menu so the keyboard continues there (the first usable entry).
            Dispatcher.UIThread.Post(() => OverflowFocusables().FirstOrDefault()?.Focus(NavigationMethod.Tab), DispatcherPriority.Loaded);
        };
        _overflowFlyout.Closed += (_, _) =>
        {
            RefreshPlaybackControls();
            RevealOverlayControls();
            if (!_overflowHadFocus || _terminalRetirement || !_overflowButton.IsEffectivelyVisible) return;
            _overflowHadFocus = false;
            _overflowButton.Focus(_keyboardInteraction ? NavigationMethod.Tab : NavigationMethod.Pointer);
        };
        _overflowPanel.GotFocus += (_, _) => _overflowHadFocus = true;
        ApplyOverflowButtonText();
    }

    private Control OverflowGlyph()
    {
        Border icon = (Border)CommandBindings.Icon("plugins.actions", 16);
        icon.Bind(Border.BackgroundProperty, icon.GetObservable(TextElement.ForegroundProperty));
        return icon;
    }

    private void ApplyOverflowButtonText(string? name = null)
    {
        name ??= Texts.T("gallery.overflow.label");
        string help = Texts.T("gallery.overflow.help");
        AutomationProperties.SetName(_overflowButton, name);
        AutomationProperties.SetHelpText(_overflowButton, help);
        ToolTip.SetTip(_overflowButton, name + " · " + help);
    }

    /// <summary>Gives an existing control the look of a menu row (lead mark, label, shortcut) and adds it to the "⋯" menu.</summary>
    private T AddOverflowItem<T>(T button, string id, bool check = false, bool keepOpen = false, Func<string>? label = null,
        Panel? host = null) where T : Button
    {
        Control lead;
        Control? mark = null;
        if (check)
        {
            // A drawn check (P6: not the font's tick), shown while the toggle is on; it follows the row's foreground.
            mark = MeowChan.Desktop.Controls.WorkspaceIcons.Create(MeowChan.Desktop.Controls.WorkspaceIcons.Check);
            mark.Opacity = 0;
            lead = mark;
        }
        else
        {
            Border icon = (Border)CommandBindings.Icon(id, 16);
            icon.HorizontalAlignment = HorizontalAlignment.Center;
            icon.Bind(Border.BackgroundProperty, icon.GetObservable(TextElement.ForegroundProperty));
            lead = icon;
        }
        TextBlock text = new() { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        TextBlock gesture = new() { VerticalAlignment = VerticalAlignment.Center, Opacity = 0.7, IsHitTestVisible = false };
        gesture.Classes.Add("workspace-meta");
        Grid root = new() { ColumnDefinitions = new ColumnDefinitions("20,*,Auto"), ColumnSpacing = 8 };
        root.Children.Add(lead);
        root.Children.Add(text); Grid.SetColumn(text, 1);
        root.Children.Add(gesture); Grid.SetColumn(gesture, 2);
        OverflowItem item = new(id, root, text, gesture, mark, keepOpen) { LabelSource = label };
        button.Tag = item;
        StyleAsOverflowRow(button, true);
        button.Content = root;
        text.Text = label?.Invoke() ?? button.Content as string ?? AutomationProperties.GetName(button) ?? string.Empty;
        gesture.Text = CommandCatalog.ShortcutDisplay(id) ?? string.Empty;
        _overflowButtons.Add(button);
        (host ?? _overflowPanel).Children.Add(button);
        return button;
    }

    private static void StyleAsOverflowRow(Button button, bool row)
    {
        button.Classes.Set("gallery-overflow-item", row);
        button.HorizontalAlignment = row ? HorizontalAlignment.Stretch : HorizontalAlignment.Left;
        button.HorizontalContentAlignment = row ? HorizontalAlignment.Stretch : HorizontalAlignment.Center;
    }

    private void AddOverflowSeparator() => _overflowPanel.Children.Add(new Border
    {
        Height = 1, Margin = new Thickness(8, 4), IsHitTestVisible = false,
        [!Border.BackgroundProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("Workspace.Border")
    });

    /// <summary>Re-reads labels, shortcut texts and check marks of the menu rows; cheap, and safe before the viewer was shown.</summary>
    private void RefreshOverflowItems()
    {
        foreach (Button button in _overflowButtons)
        {
            if (button.Tag is not OverflowItem item) continue;
            if (item.LabelSource != null) item.Label.Text = item.LabelSource();
            item.Gesture.Text = CommandCatalog.ShortcutDisplay(item.Id) ?? string.Empty;
            if (item.Check != null) item.Check.Opacity = (button as ToggleButton)?.IsChecked == true ? 1 : 0;
            // A command presenter (shortcut or language change) may have replaced the row with plain text.
            if (!ReferenceEquals(button.Content, item.Root)) button.Content = item.Root;
        }
        _modeCaption.Text = GalleryPlaybackCatalog.Get("viewer.playback.mode");
        _secondsCaption.Text = Texts.T("gallery.slideshow.secondsLabel");
        // A caption belongs to its control's row: it goes with the control when a work view puts it on the toolbar.
        _modeCaption.IsVisible = ToolInMenu("gallery.toolbar.playback-mode");
        _secondsCaption.IsVisible = _slideshowSeconds.IsVisible && ToolInMenu("gallery.toolbar.slideshow-seconds");
        ApplyOverflowButtonText();
    }

    /// <summary>The visible text of a toolbar button or menu row (smokes and tests).</summary>
    internal static string? LabelOf(Button button) => button.Tag is OverflowItem item ? item.Label.Text : button.Content as string;

    // A label shown in a menu row replaces the row's text; anywhere else it is the button's content.
    private static void SetButtonLabel(Button button, string label)
    {
        if (button.Tag is OverflowItem item) { item.Label.Text = label; if (!ReferenceEquals(button.Content, item.Root)) button.Content = item.Root; }
        else if (button.Tag is not null) { } // A tool a work view put on the toolbar keeps its icon; the label is its name and tooltip.
        else button.Content = label;
    }

    // The slideshow control stays on the toolbar while a slideshow is chosen or running, so "pause" is always one click
    // away; otherwise it is one of the menu rows.
    private void PlaceSlideshowToggle()
    {
        if (_slideshowToggle == null || _focusTools == null || _slideshowHome == null || _slideshow == null) return;
        // Chosen or running (R6) or put on the bar by the work view (Simple, Advanced); Barebones keeps it under More until then.
        bool inline = SlideshowOnBarByView;
        bool inMenu = _slideshowToggle.Tag is OverflowItem;
        if (inline != inMenu && _slideshowToggle.Parent != null) return;
        bool hadFocus = _slideshowToggle.IsKeyboardFocusWithin || _slideshowMenuButton.IsKeyboardFocusWithin;
        if (!inline) CloseSlideshowMenu(); // G2: the menu button leaves the bar with the toggle, so its flyout (the borrowed rows) goes back first
        string name = AutomationProperties.GetName(_slideshowToggle) ?? string.Empty;
        (_slideshowToggle.Parent as Panel)?.Children.Remove(_slideshowToggle);
        (_slideshowSplit.Parent as Panel)?.Children.Remove(_slideshowSplit);
        if (inline)
        {
            _overflowButtons.Remove(_slideshowToggle);
            _slideshowToggle.Tag = null;
            StyleAsOverflowRow(_slideshowToggle, false);
            _slideshowToggle.Content = name;
            // G2: one split control (the toggle and its menu button), after the tools toggle as the toggle always sat in the group that
            // survives collapsed tools.
            _slideshowSplit.Children.Insert(0, _slideshowToggle);
            _focusTools.Children.Insert(Math.Min(1, _focusTools.Children.Count), _slideshowSplit);
        }
        else
        {
            AddOverflowItem(_slideshowToggle, "gallery.slideshow-toggle", host: _slideshowHome);
            ((OverflowItem)_slideshowToggle.Tag!).Label.Text = name;
        }
        PresentSlideshowIcon(); // GL3: play/pause icon for the new place (bar: icon + text, menu: the row's lead icon)
        if (hadFocus && _slideshowToggle.IsEffectivelyVisible) _slideshowToggle.Focus(NavigationMethod.Tab);
        else if (hadFocus && _overflowButton.IsEffectivelyVisible) _overflowButton.Focus(NavigationMethod.Tab);
    }

    private IEnumerable<Control> OverflowFocusables() => MenuFocusables(_overflowPanel);

    private static IEnumerable<Control> MenuFocusables(Panel root) => root.GetVisualDescendants().OfType<Control>()
        .Where(control => (control is Button || control is ComboBox || control is NumericUpDown) && control.Focusable && control.IsEffectivelyVisible
            && control.IsEffectivelyEnabled && !control.GetVisualAncestors().Any(parent => parent is ComboBox or NumericUpDown));

    // Arrow keys move through the rows (also past the choice boxes, which would otherwise keep Up/Down for their own
    // selection and trap the keyboard); Enter, Space or F4 still open a choice box. Escape closes the menu.
    private void OnOverflowKeyDown(object? sender, KeyEventArgs e) => OnMenuKeyDown(e, _overflowPanel, _overflowFlyout);

    // The same keys serve the slideshow flyout (G2), which borrows rows of this menu.
    private static void OnMenuKeyDown(KeyEventArgs e, Panel root, FlyoutBase flyout)
    {
        if (e.Handled || e.Source is Visual open && open.GetSelfAndVisualAncestors().OfType<ComboBox>().Any(combo => combo.IsDropDownOpen)) return;
        if (e.Key == Key.Escape && e.KeyModifiers == KeyModifiers.None) { flyout.Hide(); e.Handled = true; return; }
        // A number field keeps its own keys: Up/Down step the value, Home/End move the caret.
        if (e.Source is Visual number && number.GetSelfAndVisualAncestors().OfType<NumericUpDown>().Any()) return;
        if (e.KeyModifiers != KeyModifiers.None || e.Key is not (Key.Down or Key.Up or Key.Home or Key.End)) return;
        Control[] items = MenuFocusables(root).ToArray();
        if (items.Length == 0) return;
        int current = Array.FindIndex(items, item => item.IsKeyboardFocusWithin);
        int next = e.Key switch
        {
            Key.Home => 0,
            Key.End => items.Length - 1,
            Key.Down => current < 0 ? 0 : (current + 1) % items.Length,
            _ => current < 0 ? items.Length - 1 : (current - 1 + items.Length) % items.Length
        };
        items[next].Focus(NavigationMethod.Tab);
        e.Handled = true;
    }

    private void OnOverflowItemClick(object? sender, RoutedEventArgs e)
    {
        // Choosing an action closes the menu; zoom stays open because it is used in steps.
        if (e.Source is Button { Tag: OverflowItem { KeepOpen: true } }) return;
        if (e.Source is Button { Tag: string } && _layoutSection.IsVisible) return; // a layout row: a switch stays open, "Reset layout" closes it itself
        if (e.Source is Button) _overflowFlyout.Hide();
    }

    // ---- density and post panel placement ----

    private void UpdateToolbarDensity()
    {
        bool compact = Bounds.Width > 0 && Bounds.Width < CompactToolbarWidth;
        if (compact == _compactToolbar) return;
        _compactToolbar = compact;
        PresentToolbarControls(); // Icon or labelled presentation of the primary controls; no other text changes.
        PresentLayoutButton();
        PresentSlideshowIcon();
    }

    private bool ContextOverPicture => PostOverlayEffective || Bounds.Width < CompactPostWidth;

    // The side panel's wished width (V2), fitted to the viewer: the overlay option only has to fit, the narrow placement keeps a strip
    // of the picture free as far as the panel's minimum width allows.
    private double ContextOverlayWidth => MeowChan.Desktop.Controls.GallerySidePanel.EffectiveWidth(_panelWidth, Bounds.Width,
        overlayOption: PostOverlayEffective, narrow: true, MinimumPictureWidth);
}
