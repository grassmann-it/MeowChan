using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;

namespace MeowChan.Desktop;

/// <summary>
/// The list of Settings › Appearance › Footer (F1): every main page with its name, a switch "show directly in the footer" (a switch at the end of
/// the row like every yes/no setting of the page, SP1) and the buttons that move it one place up or down, plus "Reset to default". It edits a draft
/// <see cref="FooterLayout"/> only; the settings page saves it with everything else. Every control is a plain focusable control with an automation name,
/// so the list works from the keyboard (Tab, Space on the switch, Enter or Space on a move button); after a move the focus stays on the same kind of
/// button of the page that moved, after a toggle on the switch. The heading and the explanation belong to the settings page (they are found by its search). Downloads is always
/// shown (its switch is on and disabled).
/// </summary>
internal sealed class FooterLayoutEditor : UserControl
{
    private readonly StackPanel _rows = new() { Name = "FooterLayoutRows", Spacing = 4 };
    private readonly TextBlock _status = new() { Name = "FooterLayoutStatus", TextWrapping = TextWrapping.Wrap };
    private readonly Button _reset = new() { Name = "FooterLayoutReset" };
    private FooterLayout _layout = FooterLayout.Default;
    // What the digits are counted in: the work view the draft would have and whether Booru can be shown at all (the draft of the other settings).
    private MeowChan.Desktop.WorkViews.WorkView _view = MeowChan.Desktop.WorkViews.WorkView.Barebones;
    private bool _booruAvailable = true;

    /// <summary>Raised after the person changed the draft (not by <see cref="Load"/>).</summary>
    internal event EventHandler? LayoutChanged;

    internal FooterLayoutEditor()
    {
        Name = "FooterLayoutEditor";
        _status.Classes.Add("workspace-meta");
        AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Polite);
        _reset.Classes.Add("subtle");
        _reset.HorizontalAlignment = HorizontalAlignment.Left;
        _reset.Click += (_, _) => Change(FooterLayout.Default, null, null, announce: false);
        // Audit V15: Alt+Up / Alt+Down on any control of a row move that row, like its up and down buttons.
        _rows.AddHandler(KeyDownEvent, OnRowsKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        Content = new StackPanel { Spacing = 8, Children = { _rows, _reset, _status } };
        Texts.Changed += OnLanguageChanged;
        DetachedFromVisualTree += (_, _) => Texts.Changed -= OnLanguageChanged;
        AttachedToVisualTree += (_, _) => { Texts.Changed -= OnLanguageChanged; Texts.Changed += OnLanguageChanged; Render(null, null); };
        Render(null, null);
    }

    /// <summary>The draft.</summary>
    internal FooterLayout Layout => _layout;
    internal StackPanel Rows => _rows;
    internal Button ResetButton => _reset;
    internal TextBlock StatusText => _status;

    /// <summary>The control of the last rebuild that takes the keyboard focus back (null when nothing had it); for tests and smokes.</summary>
    internal Control? FocusTarget { get; private set; }

    /// <summary>Replaces the draft (the saved layout, or the default after a reset of the page). Raises nothing.</summary>
    internal void Load(FooterLayout layout)
    {
        _layout = layout ?? FooterLayout.Default;
        _status.Text = string.Empty;
        Render(null, null);
    }

    /// <summary>
    /// The digits of the rows follow the buttons the work view effectively shows (<see cref="FooterLayout.NumberingFor"/>): the settings page passes the work
    /// view of its draft and its "Show Booru actions" draft, so the hints change live with them. Rebuilds the rows only when something changed.
    /// </summary>
    internal void SetNumberingContext(MeowChan.Desktop.WorkViews.WorkView view, bool booruAvailable)
    {
        if (view == _view && booruAvailable == _booruAvailable) return;
        _view = view;
        _booruAvailable = booruAvailable;
        Render(null, null);
    }

    /// <summary>The digit order the rows show, for tests and smokes.</summary>
    internal IReadOnlyList<string> ShownNumbering => _layout.NumberingFor(_view, id => id != "navigation.booru" || _booruAvailable);

    /// <summary>The "show in the footer" switch of a page, for tests and smokes.</summary>
    internal ToggleSwitch? SwitchOf(string commandId) => _rows.Children.OfType<Grid>().FirstOrDefault(row => row.Tag as string == commandId)?.Children.OfType<ToggleSwitch>().FirstOrDefault();
    internal Button? MoveButtonOf(string commandId, bool up) => _rows.Children.OfType<Grid>().FirstOrDefault(row => row.Tag as string == commandId)
        ?.Children.OfType<Button>().FirstOrDefault(button => button.Name == (up ? "FooterMoveUp" : "FooterMoveDown"));

    private void OnLanguageChanged(object? sender, EventArgs e) => Dispatcher.UIThread.Post(() => Render(null, null));

    /// <summary>The way Alt+Up (-1) or Alt+Down (+1) moves a row; 0 for any other key, and for Alt with another modifier.</summary>
    internal static int MoveDirection(Key key, KeyModifiers modifiers) =>
        modifiers != KeyModifiers.Alt ? 0 : key == Key.Up ? -1 : key == Key.Down ? 1 : 0;

    private void OnRowsKeyDown(object? sender, KeyEventArgs e)
    {
        int direction = MoveDirection(e.Key, e.KeyModifiers);
        if (e.Handled || direction == 0) return;
        if (TryMoveRowOf(e.Source as Control, direction)) e.Handled = true;
    }

    /// <summary>
    /// Moves the row that <paramref name="source"/> (the focused control) belongs to one place up (<paramref name="direction"/> -1) or down (+1), exactly as the row's
    /// up or down button does, and says so. At the first or last place nothing moves. False when the control belongs to no row.
    /// </summary>
    internal bool TryMoveRowOf(Control? source, int direction)
    {
        if (source == null || direction == 0) return false;
        Grid? row = null;
        foreach (Avalonia.Visual node in source.GetSelfAndVisualAncestors())
            if (node is Grid { Tag: string } grid && ReferenceEquals(grid.Parent, _rows)) { row = grid; break; }
        if (row?.Tag is not string id) return false;
        // The focus stays on the kind of control that had it: the same button, or (the switch, or the row itself) the switch.
        Button? button = source.GetSelfAndVisualAncestors().OfType<Button>().FirstOrDefault(candidate => candidate.Name is "FooterMoveUp" or "FooterMoveDown");
        bool? focusUp = button == null ? null : button.Name == "FooterMoveUp";
        Change(_layout.Move(id, direction), id, focusUp, movedPage: id);
        return true;
    }

    private static double Space(int token) =>
        Application.Current?.TryGetResource("Workspace.Space." + token, null, out object? value) == true && value is double size ? size : token;

    private void Change(FooterLayout next, string? focusPage, bool? focusUp, bool announce = true, string? movedPage = null)
    {
        if (next.Equals(_layout)) return;
        _layout = next;
        if (announce && movedPage != null)
            _status.Text = Texts.T("settings.footer.position", CommandCatalog.Label(movedPage), next.IndexOf(movedPage) + 1, next.Order.Count);
        else _status.Text = string.Empty;
        Render(focusPage, focusUp);
        LayoutChanged?.Invoke(this, EventArgs.Empty);
    }

    // Rebuilds the rows from the draft. focusPage/focusUp name the control that had the focus: with focusUp null the check box of that page,
    // otherwise its move button (the same one takes the focus again, or its sibling when the page reached the edge and the button is disabled now).
    private void Render(string? focusPage, bool? focusUp)
    {
        _rows.Children.Clear();
        IReadOnlyList<string> numbering = ShownNumbering;
        Control? focusTarget = null;
        IReadOnlyList<string> order = _layout.Order;
        for (int index = 0; index < order.Count; index++)
        {
            string id = order[index];
            string label = CommandCatalog.Label(id);
            bool pinned = !MainPageOrder.CanHideFromFooter(id);
            var row = new Grid { Tag = id, ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto,Auto"), ColumnSpacing = Space(8) };
            row.Classes.Add("footer-layout-row");
            var title = new TextBlock { Name = "FooterPageLabel" + MainPageOrder.ControlStem(id), Text = label, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
            title.Classes.Add("workspace-field-label");
            row.Children.Add(title);
            var check = new ToggleSwitch
            {
                Name = "FooterShow" + MainPageOrder.ControlStem(id), OnContent = null, OffContent = null, IsChecked = _layout.IsVisible(id), IsEnabled = !pinned,
                VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right
            };
            string hint = pinned ? Texts.T("settings.footer.always") : Texts.T("settings.footer.show");
            AutomationProperties.SetName(check, label + ": " + Texts.T("settings.footer.show"));
            AutomationProperties.SetHelpText(check, hint);
            ToolTip.SetTip(check, hint);
            string captured = id;
            check.IsCheckedChanged += (_, _) =>
            {
                bool shown = check.IsChecked == true;
                if (shown == _layout.IsVisible(captured)) return;
                // The rows are rebuilt (the digits of the other pages move), so the focus is handed to the new switch of this page.
                Change(_layout.WithVisible(captured, shown), captured, null, announce: false);
            };
            Grid.SetColumn(check, 4);
            row.Children.Add(check);

            int digit = numbering.ToList().IndexOf(id) + 1;
            var shortcut = new TextBlock { Name = "FooterShortcut" + MainPageOrder.ControlStem(id), VerticalAlignment = VerticalAlignment.Center, MinWidth = 168, TextWrapping = TextWrapping.Wrap };
            shortcut.Text = ShortcutText(id, digit);
            shortcut.Classes.Add("workspace-meta");
            AutomationProperties.SetAccessibilityView(shortcut, AccessibilityView.Content);
            AutomationProperties.SetName(shortcut, Texts.T("settings.footer.shortcut", label, shortcut.Text));
            Grid.SetColumn(shortcut, 1);
            row.Children.Add(shortcut);

            Button up = MoveButton("FooterMoveUp", "arrow-up", Texts.T("settings.footer.moveUp", label), index > 0);
            Button down = MoveButton("FooterMoveDown", "arrow-down", Texts.T("settings.footer.moveDown", label), index < order.Count - 1);
            up.Click += (_, _) => Change(_layout.Move(captured, -1), captured, true, movedPage: captured);
            down.Click += (_, _) => Change(_layout.Move(captured, 1), captured, false, movedPage: captured);
            Grid.SetColumn(up, 2);
            Grid.SetColumn(down, 3);
            row.Children.Add(up);
            row.Children.Add(down);
            _rows.Children.Add(row);
            if (id == focusPage)
                focusTarget = focusUp == null ? (check.IsEnabled ? check : null)
                    : focusUp == true ? (up.IsEnabled ? up : down) : (down.IsEnabled ? down : up);
        }
        _reset.Content = Texts.T("settings.footer.reset");
        CommandBindings.EnsureIconBeforeText(_reset, "rotate-ccw");
        AutomationProperties.SetName(_reset, Texts.T("settings.footer.reset"));
        ToolTip.SetTip(_reset, Texts.T("settings.footer.reset.hint"));
        AutomationProperties.SetHelpText(_reset, Texts.T("settings.footer.reset.hint"));
        FocusTarget = focusTarget;
        if (focusTarget != null) FocusLater(focusTarget);
    }

    /// <summary>
    /// The page's keys as the editor shows them: the digit of its draft position, then its fixed letter (revision 15, <see cref="PageKeyScheme"/>),
    /// "Ctrl+3 / Ctrl+Shift+E". The digit moves with the footer, the letter never does; a page beyond the ninth button shows the letter alone.
    /// </summary>
    internal static string ShortcutText(string commandId, int digit)
    {
        ShortcutPlatform platform = EffectiveShortcutBindings.CurrentPlatform;
        List<string> keys = new(2);
        if (digit is >= 1 and <= MainPageOrder.MaximumDigit) keys.Add(ShortcutGesture.Parse("Primary+" + digit).Display(platform));
        if (PageKeyScheme.Gesture(commandId) is { } letter) keys.Add(letter.Display(platform));
        return string.Join(" / ", keys);
    }

    private static Button MoveButton(string name, string icon, string label, bool enabled)
    {
        var button = new Button { Name = name, IsEnabled = enabled };
        IconButtons.Apply(button, icon, label);
        // The row's keys (audit V15): the tip names Alt+Up / Alt+Down; the accessible name stays the plain label.
        ToolTip.SetTip(button, label + " · " + ShortcutGesture.Parse(name == "FooterMoveUp" ? "Alt+Up" : "Alt+Down").Display(EffectiveShortcutBindings.CurrentPlatform));
        return button;
    }

    private static void FocusLater(Control control) => Dispatcher.UIThread.Post(() =>
    {
        if (control.IsEffectivelyVisible && control.IsEffectivelyEnabled) control.Focus(NavigationMethod.Tab);
    }, DispatcherPriority.Loaded);
}
