using Avalonia.Controls;
using Avalonia.Interactivity;

namespace MeowChan.DesktopSmoke;

internal static class SmokeActionClick
{
    // Synthetic routed Click does not run Button.OnClick's ICommand dispatch.
    internal static void Execute(Control control)
    {
        if (control is Button { Command: { } command } button)
        {
            if (button.IsEnabled && command.CanExecute(button.CommandParameter)) command.Execute(button.CommandParameter);
        }
        else if (control is MenuItem { Command: { } itemCommand } item)
        {
            if (item.IsEnabled && itemCommand.CanExecute(item.CommandParameter)) itemCommand.Execute(item.CommandParameter);
        }
        else control.RaiseEvent(new RoutedEventArgs(control switch
        {
            MenuItem => MenuItem.ClickEvent,
            SplitButton => SplitButton.ClickEvent, // the main part of a split button (AP2 saved searches)
            _ => Button.ClickEvent
        }));
    }
}
