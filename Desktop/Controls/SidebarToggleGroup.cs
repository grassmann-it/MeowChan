using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using MeowChan.Desktop.Localization;

namespace MeowChan.Desktop.Controls;

/// <summary>Adjacent, independent sidebar switches with the same joints as the List/Grid control.</summary>
internal sealed class SidebarToggleGroup : StackPanel
{
    // Room for the labelled group beside the reader actions in the post column.
    internal const double ReaderLabelWidth = 768;

    private readonly Dictionary<ToggleButton, (string Name, string Tip)> _descriptions = new();

    public SidebarToggleGroup()
    {
        Orientation = Orientation.Horizontal;
        VerticalAlignment = VerticalAlignment.Center;
        AutomationProperties.SetControlTypeOverride(this, AutomationControlType.Group);
        KeyboardNavigation.SetTabNavigation(this, KeyboardNavigationMode.Once);
        Children.CollectionChanged += OnSegmentsChanged;
    }

    internal bool ShowLabels
    {
        get => !Classes.Contains("compact-sidebar");
        set => Classes.Set("compact-sidebar", !value);
    }

    internal void Describe(ToggleButton segment, string name, string tip)
    {
        var description = (name, tip);
        if (_descriptions.TryGetValue(segment, out var previous) && previous == description
            && ToolTip.GetTip(segment) is string tipWithState && tipWithState.StartsWith(tip + "\n", StringComparison.Ordinal)) return;
        _descriptions[segment] = description;
        UpdateDescription(segment, description);
    }

    private static void UpdateDescription(ToggleButton segment, (string Name, string Tip) description)
    {
        string state = Texts.T(segment.IsChecked == true ? "window.fullscreen.on" : "window.fullscreen.off");
        string tip = description.Tip + "\n" + state;
        AutomationProperties.SetName(segment, description.Name + ": " + state);
        AutomationProperties.SetHelpText(segment, tip);
        ToolTip.SetTip(segment, tip);
    }

    private void OnSegmentsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems != null)
            foreach (ToggleButton segment in e.OldItems)
            {
                segment.PropertyChanged -= OnSegmentPropertyChanged;
                _descriptions.Remove(segment);
            }
        if (e.NewItems != null)
            foreach (ToggleButton segment in e.NewItems)
            {
                segment.Classes.Add("segment");
                segment.Classes.Add("toolbar-toggle");
                segment.PropertyChanged += OnSegmentPropertyChanged;
            }
        UpdateJoints();
    }

    private void OnSegmentPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == IsVisibleProperty) UpdateJoints();
        if (e.Property == ToggleButton.IsCheckedProperty && sender is ToggleButton segment
            && _descriptions.TryGetValue(segment, out var description)) UpdateDescription(segment, description);
    }

    private void UpdateJoints()
    {
        bool joined = Children.Count == 2 && Children[0].IsVisible && Children[1].IsVisible;
        for (int i = 0; i < Children.Count; i++)
        {
            Classes classes = Children[i].Classes;
            classes.Set("segment-first", joined && i == 0);
            classes.Set("segment-last", joined && i == 1);
            classes.Set("segment-single", !joined);
        }
    }

    internal static bool IsSegmentArrow(KeyEventArgs e) => e.KeyModifiers == KeyModifiers.None
        && e.Key is Key.Left or Key.Right && e.Source is ToggleButton { Parent: SidebarToggleGroup };

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled || !IsSegmentArrow(e)) return;
        int index = e.Key == Key.Left ? 0 : Children.Count - 1;
        if (Children[index] is { IsEffectivelyVisible: true, IsEffectivelyEnabled: true } target)
            target.Focus(NavigationMethod.Tab);
        e.Handled = true;
    }
}
