using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Threading;

namespace MeowChan.Desktop;

/// <summary>
/// SP1: a settings row that only applies under a condition (the tagline "Always the same", the slideshow seconds, the statistics options that need
/// their switch). Such a row is not greyed out: it is shown, indented under its parent, only while <see cref="WhenProperty"/> holds. Two
/// things keep the page usable with the keyboard and a screen reader: when the row is hidden while the focus is inside it, the focus goes back to
/// the setting before it (never to nothing), and when the row appears because the person changed its parent, its label is announced
/// (<see cref="RowAppeared"/>, spoken by the page's polite line). The settings search still finds a hidden row: a jump to it
/// <see cref="Reveal"/>s the row with its parent, <see cref="SettingsConditionalReveal"/> takes that back when the visit ends.
/// </summary>
public static class SettingsConditional
{
    /// <summary>The style class that marks a conditional row (the indent is its style).</summary>
    public const string RowClass = "settings-conditional";

    /// <summary>Whether the condition of the row holds (default true: a row without a condition is always shown).</summary>
    public static readonly AttachedProperty<bool> WhenProperty = AvaloniaProperty.RegisterAttached<Control, Control, bool>("When", true);

    /// <summary>Shown although the condition does not hold: the settings search jumped here.</summary>
    public static readonly AttachedProperty<bool> RevealedProperty = AvaloniaProperty.RegisterAttached<Control, Control, bool>("Revealed", false);

    /// <summary>Raised when a row appeared because of its condition (not by a search reveal); the editor announces its label.</summary>
    public static event Action<Control>? RowAppeared;

    static SettingsConditional()
    {
        WhenProperty.Changed.AddClassHandler<Control>((row, e) => Apply(row, e.GetNewValue<bool>(), row.GetValue(RevealedProperty), announce: true));
        RevealedProperty.Changed.AddClassHandler<Control>((row, e) => Apply(row, row.GetValue(WhenProperty), e.GetNewValue<bool>(), announce: false));
    }

    public static bool GetWhen(Control row) => row.GetValue(WhenProperty);
    public static void SetWhen(Control row, bool value) => row.SetValue(WhenProperty, value);
    public static bool GetRevealed(Control row) => row.GetValue(RevealedProperty);
    public static void SetRevealed(Control row, bool value) => row.SetValue(RevealedProperty, value);

    /// <summary>The one rule: shown while the condition holds or the search revealed the row.</summary>
    public static bool IsShown(bool when, bool revealed) => when || revealed;

    /// <summary>Whether the control is a conditional row (carries the row class).</summary>
    public static bool IsConditionalRow(Control control) => control.Classes.Contains(RowClass);

    private static void Apply(Control row, bool when, bool revealed, bool announce)
    {
        bool shown = IsShown(when, revealed);
        if (row.IsVisible == shown) return;
        if (!shown) LeaveFocus(row);
        row.IsVisible = shown;
        if (shown && announce && when) RowAppeared?.Invoke(row);
    }

    // The focus must not vanish with the row: it moves to the nearest setting before it, else to the nearest one after it.
    private static void LeaveFocus(Control row)
    {
        if (!row.IsKeyboardFocusWithin || TopLevel.GetTopLevel(row) is null) return;
        Control? target = NeighbourInput(row, backwards: true) ?? NeighbourInput(row, backwards: false);
        if (target == null) return;
        Dispatcher.UIThread.Post(() => { if (target.IsEffectivelyVisible && target.IsEffectivelyEnabled) target.Focus(NavigationMethod.Tab); }, DispatcherPriority.Loaded);
    }

    private static Control? NeighbourInput(Control row, bool backwards)
    {
        if (row.GetLogicalParent() is not Panel parent) return null;
        IEnumerable<Control> siblings = parent.Children.AsEnumerable();
        int index = parent.Children.IndexOf(row);
        siblings = backwards ? siblings.Take(index).Reverse() : siblings.Skip(index + 1);
        foreach (Control sibling in siblings)
            foreach (Control candidate in (backwards ? sibling.GetSelfAndLogicalDescendants().OfType<Control>().Reverse() : sibling.GetSelfAndLogicalDescendants().OfType<Control>()))
                if (IsInput(candidate) && candidate.IsEffectivelyVisible && candidate.IsEffectivelyEnabled) return candidate;
        return null;
    }

    private static bool IsInput(Control control) => control.Focusable && control.IsTabStop && control is TextBox or ComboBox or Avalonia.Controls.Primitives.ToggleButton or NumericUpDown or Button;

    /// <summary>The accessible label a row is announced with when it appears: the name of its first input, else its first text.</summary>
    public static string? LabelOf(Control row)
    {
        Control[] parts = row.GetSelfAndLogicalDescendants().OfType<Control>().ToArray();
        string? name = parts.Where(IsInput).Select(AutomationProperties.GetName).FirstOrDefault(text => !string.IsNullOrWhiteSpace(text));
        return name ?? parts.OfType<TextBlock>().Select(block => block.Text).FirstOrDefault(text => !string.IsNullOrWhiteSpace(text));
    }

    /// <summary>
    /// The conditional rows on the way from <paramref name="element"/> up to the page that are hidden now (their own condition does not hold).
    /// A search target inside a hidden row shows exactly these rows, no others.
    /// </summary>
    public static IReadOnlyList<Control> HiddenRowsAround(Control element) =>
        element.GetSelfAndLogicalAncestors().OfType<Control>().Where(control => IsConditionalRow(control) && !GetWhen(control)).ToArray();
}

/// <summary>
/// The conditional rows the settings search showed for a jump: they stay for the visit (while the person works in them) and are taken back when it ends
/// or the next jump goes elsewhere. A row whose value the person edited stays until the page is closed (their value is not hidden under them).
/// </summary>
internal sealed class SettingsConditionalReveal
{
    private readonly List<Control> _revealed = new();
    private readonly HashSet<Control> _edited = new();

    internal int Count => _revealed.Count;

    /// <summary>Shows the hidden conditional rows around the target; the rows are shown with their parent because they sit right under it.</summary>
    internal void Reveal(Control element)
    {
        Release();
        foreach (Control row in SettingsConditional.HiddenRowsAround(element))
        {
            SettingsConditional.SetRevealed(row, true);
            _revealed.Add(row);
        }
    }

    /// <summary>The person changed a value inside <paramref name="source"/>: the row it belongs to stays.</summary>
    internal void NoteEdited(Control source)
    {
        foreach (Control row in source.GetSelfAndLogicalAncestors().OfType<Control>())
            if (_revealed.Contains(row)) _edited.Add(row);
    }

    internal void Release()
    {
        foreach (Control row in _revealed)
            if (!_edited.Contains(row)) SettingsConditional.SetRevealed(row, false);
        _revealed.RemoveAll(row => !_edited.Contains(row));
    }
}
