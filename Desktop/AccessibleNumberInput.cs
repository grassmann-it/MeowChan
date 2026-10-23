using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;

namespace MeowChan.Desktop;

/// <summary>Honors explicit focus on the numeric spinner's accessible buttons.</summary>
public class AccessibleNumberInput : NumericUpDown
{
    protected override Type StyleKeyOverride => typeof(NumericUpDown);

    protected override void OnGotFocus(FocusChangedEventArgs e)
    {
        Button? requested = e.Source as Button;
        if (requested?.Name is not ("PART_IncreaseButton" or "PART_DecreaseButton")
            || !ReferenceEquals(requested.FindAncestorOfType<NumericUpDown>(), this))
            requested = null;

        // Keep Avalonia's focus bookkeeping, keyboard entry and SelectAll behavior.
        // On first entry it also redirects an explicitly focused spinner button to
        // PART_TextBox. Restore only that requested button after the redirect;
        // the base control now knows focus is within and will not redirect again.
        base.OnGotFocus(e);
        if (requested != null
            && TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is TextBox text
            && text.Name == "PART_TextBox"
            && ReferenceEquals(text.FindAncestorOfType<NumericUpDown>(), this))
            requested.Focus(e.NavigationMethod, e.KeyModifiers);
    }
}
