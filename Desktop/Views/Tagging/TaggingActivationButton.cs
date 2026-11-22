using System;
using Avalonia.Controls;
using Avalonia.Input;

namespace MeowChan.Desktop;

// Avalonia's Button handles Space even with modifiers. Only a plain press on these
// triggers should activate them, including when macOS delivers a system chord to the app.
internal sealed class TaggingActivationButton : Button
{
    protected override Type StyleKeyOverride => typeof(Button);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key != Key.Space || e.KeyModifiers == KeyModifiers.None) base.OnKeyDown(e);
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        if (e.Key != Key.Space || IsPressed) base.OnKeyUp(e);
    }
}
