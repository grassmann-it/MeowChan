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
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;

namespace MeowChan.Desktop;

/// <summary>
/// The list of Settings › Appearance › Footer (F1): every main page with a check box "show directly in the footer" and the buttons that move it
/// one place up or down, plus "Reset to default". It edits a draft <see cref="FooterLayout"/> only; the settings page saves it with
/// everything else. Every control is a plain focusable control with an automation name, so the list works from the keyboard (Tab, Space on the
/// check box, Enter or Space on a move button); after a move the focus stays on the same kind of button of the page that moved, after a toggle on the check box. The heading and the
/// explanation belong to the settings page (they are found by its search). Downloads is always shown (its check box is on and disabled).
/// </summary>
internal sealed class FooterLayoutEditor : UserControl
{
    private readonly StackPanel _rows = new() { Name = "FooterLayoutRows", Spacing = 4 };
    private readonly TextBlock _status = new() { Name = "FooterLayoutStatus", TextWrapping = TextWrapping.Wrap };
    private readonly Button _reset = new() { Name = "FooterLayoutReset" };
    private FooterLayout _layout = FooterLayout.Default;

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

    /// <summary>The check box of a page, for tests and smokes.</summary>
    internal CheckBox? CheckBoxOf(string commandId) => _rows.Children.OfType<Grid>().FirstOrDefault(row => row.Tag as string == commandId)?.Children.OfType<CheckBox>().FirstOrDefault();
    internal Button? MoveButtonOf(string commandId, bool up) => _rows.Children.OfType<Grid>().FirstOrDefault(row => row.Tag as string == commandId)
        ?.Children.OfType<Button>().FirstOrDefault(button => button.Name == (up ? "FooterMoveUp" : "FooterMoveDown"));

    private void OnLanguageChanged(object? sender, EventArgs e) => Dispatcher.UIThread.Post(() => Render(null, null));

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
        IReadOnlyList<string> numbering = _layout.Numbering;
        Control? focusTarget = null;
        IReadOnlyList<string> order = _layout.Order;
        for (int index = 0; index < order.Count; index++)
        {
            string id = order[index];
            string label = CommandCatalog.Label(id);
            bool pinned = !MainPageOrder.CanHideFromFooter(id);
            var row = new Grid { Tag = id, ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto"), ColumnSpacing = Space(8) };
            var check = new CheckBox
            {
                Name = "FooterShow" + MainPageOrder.ControlStem(id), Content = label, IsChecked = _layout.IsVisible(id), IsEnabled = !pinned,
                VerticalAlignment = VerticalAlignment.Center
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
                // The rows are rebuilt (the digits of the other pages move), so the focus is handed to the new check box of this page.
                Change(_layout.WithVisible(captured, shown), captured, null, announce: false);
            };
            row.Children.Add(check);

            int digit = numbering.ToList().IndexOf(id) + 1;
            var shortcut = new TextBlock { Name = "FooterShortcut" + MainPageOrder.ControlStem(id), VerticalAlignment = VerticalAlignment.Center, MinWidth = 56 };
            if (digit is >= 1 and <= MainPageOrder.MaximumDigit)
                shortcut.Text = ShortcutGesture.Parse("Primary+" + digit).Display(EffectiveShortcutBindings.CurrentPlatform);
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
        AutomationProperties.SetName(_reset, Texts.T("settings.footer.reset"));
        FocusTarget = focusTarget;
        if (focusTarget != null) FocusLater(focusTarget);
    }

    private static Button MoveButton(string name, string icon, string label, bool enabled)
    {
        var button = new Button { Name = name, IsEnabled = enabled };
        IconButtons.Apply(button, icon, label);
        return button;
    }

    private static void FocusLater(Control control) => Dispatcher.UIThread.Post(() =>
    {
        if (control.IsEffectivelyVisible && control.IsEffectivelyEnabled) control.Focus(NavigationMethod.Tab);
    }, DispatcherPriority.Loaded);
}
