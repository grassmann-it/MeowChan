using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;

namespace MeowChan.Desktop;

internal sealed class TaggingResultList : ListBox
{
    protected override Type StyleKeyOverride => typeof(ListBox);

    // The page handles plain Space after checking shortcuts. Avalonia's item
    // handler would also select a row on a modified Space that reaches the app.
    protected override bool ShouldTriggerSelection(Visual selectable, KeyEventArgs eventArgs) =>
        eventArgs.Key != Key.Space && base.ShouldTriggerSelection(selectable, eventArgs);
}
