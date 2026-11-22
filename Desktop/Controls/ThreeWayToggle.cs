using System;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.VisualTree;
using MeowChan.Desktop.Commands;

namespace MeowChan.Desktop.Controls;

/// <summary>
/// The three-state sibling of <see cref="TwoWayToggle"/> ("List | Grid | Gallery"): three touching toggle buttons, exactly one is on, a click
/// on the one that is on changes nothing, the arrow keys move between them and the button that is on is the only stop of the tab key.
/// <see cref="SelectedIndex"/> is 0, 1 or 2 and <see cref="SelectionChanged"/> follows every change of it, whoever made it.
/// </summary>
internal sealed class ThreeWayToggle : StackPanel
{
    private readonly ToggleButton[] _buttons =
    {
        new() { Classes = { "segment", "segment-first" } },
        new() { Classes = { "segment" } },
        new() { Classes = { "segment", "segment-last" } }
    };
    private int _selected;

    internal ThreeWayToggle()
    {
        Orientation = Orientation.Horizontal;
        Spacing = 0;
        AutomationProperties.SetControlTypeOverride(this, AutomationControlType.Group);
        for (int index = 0; index < _buttons.Length; index++)
        {
            ToggleButton button = _buttons[index];
            int target = index;
            Children.Add(button);
            button.AttachedToVisualTree += (_, _) => ShowIcon(button);
            button.Click += (_, _) => Select(target);
        }
        AddHandler(KeyDownEvent, OnKeyDown);
        Apply();
    }

    /// <summary>Raised after <see cref="SelectedIndex"/> changed.</summary>
    internal event EventHandler? SelectionChanged;

    /// <summary>0, 1 or 2; other values are brought to that range.</summary>
    internal int SelectedIndex { get => _selected; set => Select(value); }

    internal ToggleButton First => _buttons[0];
    internal ToggleButton Second => _buttons[1];
    internal ToggleButton Third => _buttons[2];

    /// <summary>Sets the catalog icon, the accessible name and the hint (tooltip and help text) of the button at <paramref name="index"/>.</summary>
    internal void SetIconItem(int index, string icon, string text, string? hint)
    {
        ToggleButton button = _buttons[index];
        button.Tag = icon;
        button.Content = null;
        if (button.IsAttachedToVisualTree()) ShowIcon(button);
        AutomationProperties.SetName(button, text);
        ToolTip.SetTip(button, hint);
        AutomationProperties.SetHelpText(button, hint ?? string.Empty);
    }

    private static void ShowIcon(ToggleButton button)
    {
        if (button.Tag is string icon && button.Content is not Control) button.Content = CommandBindings.NamedIcon(icon);
    }

    private void Select(int index)
    {
        index = Math.Clamp(index, 0, _buttons.Length - 1);
        bool changed = index != _selected;
        _selected = index;
        Apply();
        if (changed) SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Apply()
    {
        for (int index = 0; index < _buttons.Length; index++)
        {
            _buttons[index].IsChecked = _selected == index;
            KeyboardNavigation.SetIsTabStop(_buttons[index], _selected == index);
        }
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyModifiers != KeyModifiers.None || e.Key is not (Key.Left or Key.Right)) return;
        int target = Math.Clamp(_selected + (e.Key == Key.Left ? -1 : 1), 0, _buttons.Length - 1);
        Select(target);
        _buttons[target].Focus(NavigationMethod.Directional);
        e.Handled = true;
    }
}
