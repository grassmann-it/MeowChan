using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.VisualTree;
using Ursa.Controls;

namespace MeowChan.SettingsComparison;

// Non-shipped experiment. Only public/protected vendor API; no private template reflection.
public sealed class UrsaRecheckInput : NumericIntUpDown
{
    public string? RawText { get => _textBox?.Text; set { ApplyTemplate(); if (_textBox != null) _textBox.Text = value; } }
    public TextBox? Editor => _textBox;
    protected override bool CommitInput(bool forceTextUpdate = false)
    {
        string? raw = _textBox?.Text;
        bool valid = decimal.TryParse(raw, ParsingNumberStyle, NumberFormat, out decimal number)
            && decimal.Truncate(number) == number && number >= Minimum && number <= Maximum;
        // Ursa's forced blur commit formats the last valid value even if parsing failed.
        // Preserve the draft before Save can validate it, rather than storing a shadow value.
        return base.CommitInput(forceTextUpdate && valid);
    }
    protected override void OnGotFocus(FocusChangedEventArgs e)
    {
        Button? requested = e.Source as Button;
        if (requested == null || !ReferenceEquals(requested.FindAncestorOfType<Ursa.Controls.NumericUpDown>(), this)) requested = null;
        base.OnGotFocus(e);
        if (requested != null && _textBox?.IsFocused == true) requested.Focus(e.NavigationMethod, e.KeyModifiers);
    }
}
