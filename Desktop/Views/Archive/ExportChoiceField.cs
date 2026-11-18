using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace MeowChan;

/// <summary>
/// One labelled choice in the header of the shared "Export…" dialog: the format or the scope.
/// Several options show a drop-down; a single option is plain text, so a fixed choice never looks
/// like an input and the header grows no controls the user cannot use (progressive disclosure).
/// </summary>
internal sealed class ExportChoiceField : DockPanel
{
    internal sealed record Option(object Value, string Label)
    {
        public override string ToString() => Label;
    }

    private readonly TextBlock _label = new() { VerticalAlignment = VerticalAlignment.Center };
    private bool _updating;

    internal ExportChoiceField(string name)
    {
        Name = name + "Field";
        HorizontalSpacing = 8;
        _label.Name = name + "Label";
        Box = new ComboBox { Name = name, MinWidth = 160, MaxWidth = 320, VerticalAlignment = VerticalAlignment.Center };
        // The label docks left; the value fills the rest, so a long fixed scope wraps instead of clipping.
        Fixed = new TextBlock { Name = name + "Fixed", VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeight.SemiBold,
            TextWrapping = TextWrapping.Wrap };
        SetDock(_label, Dock.Left);
        _label.Classes.Add("workspace-meta");
        AutomationProperties.SetLabeledBy(Box, _label);
        AutomationProperties.SetLabeledBy(Fixed, _label);
        Children.Add(_label);
        Children.Add(Box);
        Children.Add(Fixed);
        Box.SelectionChanged += (_, _) =>
        {
            if (!_updating && Box.SelectedItem is Option option) Chosen?.Invoke(option.Value);
        };
    }

    internal ComboBox Box { get; }
    internal TextBlock Fixed { get; }
    internal TextBlock Label => _label;
    internal object? Value => (Box.SelectedItem as Option)?.Value;
    internal bool IsChoice => Box.IsVisible;

    /// <summary>Raised for a user choice only; refreshing the options never raises it.</summary>
    internal event Action<object>? Chosen;

    internal void Set(string label, IReadOnlyList<Option> options, object? selected, bool enabled)
    {
        _updating = true;
        try
        {
            _label.Text = label;
            // Replace the items only when they changed, so a refresh never closes an open drop-down.
            if (Box.ItemsSource is not IReadOnlyList<Option> current || !current.SequenceEqual(options))
                Box.ItemsSource = options.ToArray();
            IReadOnlyList<Option> shown = (IReadOnlyList<Option>)Box.ItemsSource!;
            int index = -1;
            for (int i = 0; i < shown.Count; i++) if (Equals(shown[i].Value, selected)) { index = i; break; }
            Box.SelectedIndex = index >= 0 ? index : shown.Count > 0 ? 0 : -1;
            bool choice = shown.Count > 1;
            Box.IsVisible = choice;
            Box.IsEnabled = enabled;
            Fixed.IsVisible = shown.Count == 1;
            Fixed.Text = shown.Count == 1 ? shown[0].Label : string.Empty;
            IsVisible = shown.Count > 0;
            AutomationProperties.SetName(Box, label);
        }
        finally { _updating = false; }
    }
}
