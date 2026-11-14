using System;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;

namespace MeowChan.Desktop.Controls;

/// <summary>
/// A switch between two states drawn as two touching toggle buttons ("all | any"): exactly one is on, a click on the one that is on changes
/// nothing, and the arrow keys move between them. It is one stop of the tab key (the key goes to the button that is on), so a row of
/// fields is not lengthened by it. <see cref="SelectedIndex"/> is 0 or 1 and <see cref="SelectionChanged"/> follows every change of it,
/// whoever made it (a click, a key, or code such as a chip's remove mark).
/// </summary>
internal sealed class TwoWayToggle : StackPanel
{
    private readonly ToggleButton _first = new() { Classes = { "segment", "segment-first" } };
    private readonly ToggleButton _second = new() { Classes = { "segment", "segment-last" } };
    private int _selected;

    internal TwoWayToggle()
    {
        Orientation = Orientation.Horizontal;
        Spacing = 0;
        Children.Add(_first);
        Children.Add(_second);
        AutomationProperties.SetControlTypeOverride(this, AutomationControlType.Group);
        _first.Click += (_, _) => Select(0);
        _second.Click += (_, _) => Select(1);
        AddHandler(KeyDownEvent, OnKeyDown);
        Apply();
    }

    /// <summary>Raised after <see cref="SelectedIndex"/> changed.</summary>
    internal event EventHandler? SelectionChanged;

    /// <summary>0 for the first state, 1 for the second; other values are brought to that range.</summary>
    internal int SelectedIndex { get => _selected; set => Select(value); }

    internal ToggleButton First => _first;
    internal ToggleButton Second => _second;

    /// <summary>Sets what the two buttons say and explain (the tooltip is also their help text).</summary>
    internal void SetItems(string first, string second, string? firstHint = null, string? secondHint = null)
    {
        Describe(_first, first, firstHint);
        Describe(_second, second, secondHint);
    }

    private static void Describe(ToggleButton button, string text, string? hint)
    {
        button.Content = text;
        AutomationProperties.SetName(button, text);
        ToolTip.SetTip(button, hint);
        AutomationProperties.SetHelpText(button, hint ?? string.Empty);
    }

    private void Select(int index)
    {
        index = Math.Clamp(index, 0, 1);
        bool changed = index != _selected;
        _selected = index;
        Apply();
        if (changed) SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    // The button that is on is the only tab stop; the other one is reached with the arrow keys.
    private void Apply()
    {
        _first.IsChecked = _selected == 0;
        _second.IsChecked = _selected == 1;
        KeyboardNavigation.SetIsTabStop(_first, _selected == 0);
        KeyboardNavigation.SetIsTabStop(_second, _selected == 1);
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyModifiers != KeyModifiers.None || e.Key is not (Key.Left or Key.Right)) return;
        int target = e.Key == Key.Left ? 0 : 1;
        Select(target);
        (target == 0 ? _first : _second).Focus(NavigationMethod.Directional);
        e.Handled = true;
    }
}
