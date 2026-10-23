using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using Ursa.Controls;

namespace MeowChan.DesktopSmoke;

internal static class ActionToolBarFocusChecks
{
    internal static async Task RunAsync(MainWindow owner)
    {
        int executions = 0;
        var command = new DelegateCommand(() => executions++);
        var view = new Button { Content = "List", Width = 80 };
        var gallery = new Button { Content = "Gallery", Width = 100, Command = command };
        var always = new Button { Content = "Always", Width = 80 };
        ToolBar.SetOverflowMode(view, OverflowMode.Never);
        ToolBar.SetOverflowMode(gallery, OverflowMode.AsNeeded);
        ToolBar.SetOverflowMode(always, OverflowMode.Always);
        var toolbar = new ActionToolBar { Width = 120, Items = { view, gallery, always } };
        var outside = new Button { Content = "Another focus target" };
        var fixture = new Window
        {
            Width = 440, Height = 200,
            Content = new StackPanel { Children = { toolbar, outside } }
        };
        fixture.Show(owner);
        try
        {
            await Program.WaitUntilAsync(() => toolbar.More != null && toolbar.OverflowHost?.Children.Contains(gallery) == true);
            toolbar.More!.IsChecked = true;
            await Program.WaitUntilAsync(() => gallery.IsEffectivelyVisible);
            Require(gallery.Focus(NavigationMethod.Tab), "Could not focus the original overflow action.");
            Require(ReferenceEquals(fixture.FocusManager?.GetFocusedElement(), gallery), "Overflow action did not own actual focus.");

            toolbar.Width = 360;
            fixture.UpdateLayout();
            await Program.WaitUntilAsync(() => toolbar.OverflowHost!.Children.Contains(always)
                && !toolbar.OverflowHost.Children.Contains(gallery)
                && ReferenceEquals(fixture.FocusManager?.GetFocusedElement(), gallery));
            Require(toolbar.More.IsVisible && toolbar.More.IsChecked != true,
                "Always overflow vanished or retained its popup after the focused action moved directly.");
            Require(ReferenceEquals(toolbar.Items[1], gallery) && ReferenceEquals(gallery.Command, command),
                "Focus repair replaced the original action or command.");
            gallery.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
            gallery.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyUpEvent, Key = Key.Enter });
            Require(executions == 1, "Enter after overflow-to-direct must execute the same command exactly once.");

            toolbar.Width = 120;
            fixture.UpdateLayout();
            await Program.WaitUntilAsync(() => toolbar.OverflowHost!.Children.Contains(gallery)
                && ReferenceEquals(fixture.FocusManager?.GetFocusedElement(), toolbar.More));
            Require(ReferenceEquals(gallery.Command, command), "Shrink replaced the command.");
            toolbar.More.IsChecked = true;
            await Program.WaitUntilAsync(() => ReferenceEquals(fixture.FocusManager?.GetFocusedElement(), gallery));
            toolbar.OverflowHost!.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape });
            await Program.WaitUntilAsync(() => toolbar.More.IsChecked != true
                && ReferenceEquals(fixture.FocusManager?.GetFocusedElement(), toolbar.More));
            Require(executions == 1, "Shrink/open/Escape executed an action.");

            // A user focus move after synchronous reparenting wins over the queued repair.
            toolbar.More.IsChecked = true;
            await Program.WaitUntilAsync(() => ReferenceEquals(fixture.FocusManager?.GetFocusedElement(), gallery));
            toolbar.Width = 360;
            fixture.UpdateLayout();
            Require(outside.Focus(NavigationMethod.Tab), "Could not place the competing explicit focus.");
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            Require(ReferenceEquals(fixture.FocusManager?.GetFocusedElement(), outside),
                "Deferred overflow repair stole focus from another control.");
            Require(ReferenceEquals(toolbar.Items[1], gallery) && ReferenceEquals(gallery.Command, command)
                && toolbar.OverflowHost!.Children.Contains(always), "Return path lost identity or remaining Always overflow.");
        }
        finally { fixture.Close(); }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
