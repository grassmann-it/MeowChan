using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using MeowChan.Gallery;

namespace MeowChan.Desktop.Controls;

/// <summary>What the row shows: the texts are made by the page, which owns the language and the counts.</summary>
/// <param name="Name">The accessible name of the row ("Where “cat” was found").</param>
/// <param name="Parts">The parts in row order: "All" first.</param>
/// <param name="Note">A line under the parts (the words only match across fields), or null.</param>
/// <param name="Elsewhere">The line of a narrowed scope that found nothing, or null; it comes with the button.</param>
/// <param name="Everywhere">The text of that button.</param>
internal sealed record MediaTextBreakdownView(string Name, IReadOnlyList<MediaTextBreakdownView.Part> Parts, string? Note, string? Elsewhere, string? Everywhere)
{
    /// <summary>One toggle: the field (<see cref="MediaTextFields.All"/> for "All"), its sentence and whether it is on.</summary>
    internal sealed record Part(MediaTextFields Field, string Text, bool Checked);
}

/// <summary>
/// The "where was it found" row under the Media search field: a wrapping row of toggle buttons ("All 57", "12 in file names", …), the note
/// that the words only match across fields, and, when a narrowed scope finds nothing, the line with "Search everywhere". The row is one
/// stop of the tab key (the key goes to the part that was focused last, "All" at first); Left, Right, Home and End move between the
/// parts, Space and Enter toggle the one with the focus, and Escape asks the owner to return to the search field. The page decides what
/// a toggle means; this control shows what it is given and keeps the keyboard focus on its part while the row changes.
/// </summary>
internal sealed class MediaTextBreakdownRow : StackPanel
{
    private readonly WrapPanel _parts = new() { Name = "MediaTextBreakdownParts", ItemSpacing = 8, LineSpacing = 8 };
    private readonly TextBlock _note = new() { Name = "MediaTextBreakdownNote", TextWrapping = Avalonia.Media.TextWrapping.Wrap, IsVisible = false, Classes = { "workspace-meta" } };
    private readonly TextBlock _elsewhere = new() { Name = "MediaTextBreakdownElsewhere", TextWrapping = Avalonia.Media.TextWrapping.Wrap,
        VerticalAlignment = VerticalAlignment.Center, Classes = { "workspace-meta" } };
    private readonly Button _everywhere = new() { Name = "MediaTextBreakdownEverywhere", Classes = { "link" }, VerticalAlignment = VerticalAlignment.Center };
    private readonly StackPanel _elsewhereRow = new() { Orientation = Orientation.Horizontal, Spacing = 8, IsVisible = false };
    private readonly Dictionary<MediaTextFields, ToggleButton> _buttons = new();
    private MediaTextFields _current = MediaTextFields.All;

    internal MediaTextBreakdownRow()
    {
        Spacing = 4;
        AutomationProperties.SetControlTypeOverride(this, AutomationControlType.Group);
        _elsewhereRow.Children.Add(_elsewhere);
        _elsewhereRow.Children.Add(_everywhere);
        Children.Add(_parts);
        Children.Add(_note);
        Children.Add(_elsewhereRow);
        _everywhere.Click += (_, _) => EverywhereInvoked?.Invoke(this, EventArgs.Empty);
        AddHandler(KeyDownEvent, OnKeyDown);
    }

    /// <summary>A part was toggled (a click, or Space or Enter on it); the page changes the scope and calls <see cref="Show"/> again.</summary>
    internal event Action<MediaTextFields>? PartInvoked;

    /// <summary>"Search everywhere" was pressed.</summary>
    internal event EventHandler? EverywhereInvoked;

    /// <summary>Escape in the row: the owner puts the focus back into the search field.</summary>
    internal event EventHandler? EscapeRequested;

    /// <summary>The part buttons in row order (tests and smokes).</summary>
    internal IReadOnlyList<ToggleButton> PartButtons => _parts.Children.OfType<ToggleButton>().ToArray();
    internal Button EverywhereButton => _everywhere;
    internal bool HasNote => _note.IsVisible;
    internal string? NoteText => _note.IsVisible ? _note.Text : null;
    internal string? ElsewhereText => _elsewhereRow.IsVisible ? _elsewhere.Text : null;

    /// <summary>The field of a part button.</summary>
    internal static MediaTextFields FieldOf(ToggleButton button) => button.Tag is MediaTextFields field ? field : MediaTextFields.All;

    /// <summary>The part that is the row's tab stop.</summary>
    internal MediaTextFields TabStopField => _current;

    /// <summary>Shows the view. Buttons are kept per field, so a change of counts or choice does not take the focus from the part that has it.</summary>
    internal void Show(MediaTextBreakdownView view)
    {
        ArgumentNullException.ThrowIfNull(view);
        AutomationProperties.SetName(this, view.Name);
        HashSet<MediaTextFields> wanted = view.Parts.Select(part => part.Field).ToHashSet();
        bool lostFocus = false;
        foreach ((MediaTextFields field, ToggleButton button) in _buttons.ToArray())
        {
            if (wanted.Contains(field)) continue;
            lostFocus |= button.IsFocused;
            _parts.Children.Remove(button);
            _buttons.Remove(field);
        }
        for (int index = 0; index < view.Parts.Count; index++)
        {
            MediaTextBreakdownView.Part part = view.Parts[index];
            if (!_buttons.TryGetValue(part.Field, out ToggleButton? button))
            {
                button = new ToggleButton { Name = "MediaTextBreakdown" + (part.Field == MediaTextFields.All ? "All" : MediaTextBreakdown.Key(part.Field)),
                    Tag = part.Field, Classes = { "breakdown-chip" } };
                ToggleButton created = button;
                button.Click += (_, _) => PartInvoked?.Invoke(FieldOf(created));
                button.GotFocus += (_, _) => MarkCurrent(FieldOf(created));
                _buttons[part.Field] = button;
            }
            button.Content = part.Text;
            AutomationProperties.SetName(button, part.Text);
            // The page's choice is the truth: the button toggled itself on the click, this puts it right again.
            button.IsChecked = part.Checked;
            if (index >= _parts.Children.Count || !ReferenceEquals(_parts.Children[index], button))
            {
                _parts.Children.Remove(button);
                _parts.Children.Insert(Math.Min(index, _parts.Children.Count), button);
            }
        }
        _note.Text = view.Note;
        _note.IsVisible = !string.IsNullOrEmpty(view.Note);
        _elsewhere.Text = view.Elsewhere;
        _everywhere.Content = view.Everywhere;
        if (!string.IsNullOrEmpty(view.Everywhere)) AutomationProperties.SetName(_everywhere, view.Everywhere);
        _elsewhereRow.IsVisible = !string.IsNullOrEmpty(view.Elsewhere);
        if (!wanted.Contains(_current)) _current = MediaTextFields.All;
        ApplyTabStops();
        // The part under the keyboard went away (a field without a hit that was switched off): "All" is the nearest place.
        if (lostFocus && _buttons.TryGetValue(MediaTextFields.All, out ToggleButton? all)) all.Focus(NavigationMethod.Directional);
    }

    /// <summary>The part that got the keyboard focus becomes the row's tab stop (called by the buttons; tests call it for the focus they cannot place).</summary>
    internal void MarkCurrent(MediaTextFields field)
    {
        if (!_buttons.ContainsKey(field)) return;
        _current = field;
        ApplyTabStops();
    }

    // The part that was focused last is the only tab stop; the others are reached with the arrow keys.
    private void ApplyTabStops()
    {
        foreach ((MediaTextFields field, ToggleButton button) in _buttons)
            KeyboardNavigation.SetIsTabStop(button, field == _current);
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled || e.KeyModifiers != KeyModifiers.None) return;
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            EscapeRequested?.Invoke(this, EventArgs.Empty);
            return;
        }
        if (e.Key is not (Key.Left or Key.Right or Key.Home or Key.End)) return;
        ToggleButton[] buttons = PartButtons.ToArray();
        int at = Array.FindIndex(buttons, button => button.IsFocused);
        if (at < 0) return; // the "Search everywhere" button has its own keys
        bool rightToLeft = FlowDirection == Avalonia.Media.FlowDirection.RightToLeft;
        int target = e.Key switch
        {
            Key.Home => 0,
            Key.End => buttons.Length - 1,
            Key.Left => rightToLeft ? Math.Min(at + 1, buttons.Length - 1) : Math.Max(at - 1, 0),
            _ => rightToLeft ? Math.Max(at - 1, 0) : Math.Min(at + 1, buttons.Length - 1)
        };
        buttons[target].Focus(NavigationMethod.Directional);
        e.Handled = true;
    }
}
