using System;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.VisualTree;
using MeowChan.Desktop.Commands;

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
        _first.AttachedToVisualTree += (_, _) => ShowIcon(_first);
        _second.AttachedToVisualTree += (_, _) => ShowIcon(_second);
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

    /// <summary>
    /// As <see cref="SetItems"/> with a catalog icon (<c>list</c>, <c>grid</c>) instead of a word on each button: the name and the hint are
    /// the buttons' accessible name, tooltip and help text, so nothing depends on the picture. The icons are created once the buttons are
    /// shown (they need the platform's asset loader).
    /// </summary>
    internal void SetIconItems(string firstIcon, string first, string? firstHint, string secondIcon, string second, string? secondHint)
    {
        Describe(_first, first, firstHint, firstIcon);
        Describe(_second, second, secondHint, secondIcon);
    }

    internal void SetLabeledIconItems(string firstIcon, string first, string firstHint, string secondIcon, string second, string secondHint)
    {
        DescribeLabeledIcon(_first, firstIcon, first, firstHint);
        DescribeLabeledIcon(_second, secondIcon, second, secondHint);
    }

    internal void SetLabelsVisible(bool visible)
    {
        if (_first.Content is StackPanel first) first.Children[1].IsVisible = visible;
        if (_second.Content is StackPanel second) second.Children[1].IsVisible = visible;
    }

    private static void ShowIcon(ToggleButton button)
    {
        if (button.Tag is not string icon) return;
        if (button.Content is StackPanel { Children: [ContentControl slot, _] })
            slot.Content ??= CommandBindings.NamedIcon(icon);
        else if (button.Content is not Control) button.Content = CommandBindings.NamedIcon(icon);
    }

    private static void DescribeLabeledIcon(ToggleButton button, string icon, string text, string hint)
    {
        if (button.Content is not StackPanel content)
        {
            content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                [!SpacingProperty] = new DynamicResourceExtension("Workspace.Space.8"),
                Children =
                {
                    new ContentControl { VerticalAlignment = VerticalAlignment.Center, Focusable = false },
                    new TextBlock { VerticalAlignment = VerticalAlignment.Center }
                }
            };
            AutomationProperties.SetAccessibilityView(content, AccessibilityView.Raw);
            button.Content = content;
        }
        button.Tag = icon;
        ((TextBlock)content.Children[1]).Text = text;
        if (button.IsAttachedToVisualTree()) ShowIcon(button);
        DescribeAccessibility(button, text, hint);
    }

    private static void Describe(ToggleButton button, string text, string? hint, string? icon = null)
    {
        button.Tag = icon;
        button.Content = icon == null ? text : null;
        if (icon != null && button.IsAttachedToVisualTree()) ShowIcon(button);
        DescribeAccessibility(button, text, hint);
    }

    private static void DescribeAccessibility(ToggleButton button, string text, string? hint)
    {
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
