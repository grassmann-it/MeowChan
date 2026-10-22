using System;
using Avalonia.Controls;
using Avalonia.Input;

namespace MeowChan.Desktop;

/// <summary>Settings drafts must retain rejected text until explicit correction or discard.</summary>
public sealed class SettingsNumberInput : NumericUpDown
{
    protected override Type StyleKeyOverride => typeof(NumericUpDown);

    protected override void OnLostFocus(FocusChangedEventArgs e)
    {
        string? draft = Text;
        bool rejected = !decimal.TryParse(draft, ParsingNumberStyle, NumberFormat, out decimal number)
            || number < Minimum || number > Maximum;
        // Keep native focus, automation and valid-number commit/spin behavior. Avalonia's
        // forced blur commit formats the old Value even when parsing/range validation fails.
        base.OnLostFocus(e);
        if (rejected && Text != draft)
            SetCurrentValue(TextProperty, draft);
    }
}
