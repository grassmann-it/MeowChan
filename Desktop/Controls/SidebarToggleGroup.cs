using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using MeowChan.Desktop.Localization;

namespace MeowChan.Desktop.Controls;

/// <summary>Adjacent sidebar switches that can be on independently.</summary>
internal sealed class SidebarToggleGroup : StackPanel
{
    // Room for the labelled group beside the reader actions in the post column.
    internal const double ReaderLabelWidth = 768;

    private readonly Dictionary<ToggleButton, (string Name, string Tip)> _descriptions = new();

    public SidebarToggleGroup()
    {
        Orientation = Orientation.Horizontal;
        VerticalAlignment = VerticalAlignment.Center;
        Spacing = 4;
        AutomationProperties.SetControlTypeOverride(this, AutomationControlType.Group);
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
                segment.Classes.Add("segment-single");
                segment.Classes.Add("toolbar-toggle");
                segment.PropertyChanged += OnSegmentPropertyChanged;
            }
    }

    private void OnSegmentPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == ToggleButton.IsCheckedProperty && sender is ToggleButton segment
            && _descriptions.TryGetValue(segment, out var description)) UpdateDescription(segment, description);
    }
}
