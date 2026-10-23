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
        else control.RaiseEvent(new RoutedEventArgs(control is MenuItem ? MenuItem.ClickEvent : Button.ClickEvent));
    }
}
