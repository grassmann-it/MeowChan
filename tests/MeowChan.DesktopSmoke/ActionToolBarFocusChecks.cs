using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using System.Text.Json;
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
        var view = new Button { Name = "FocusList", Content = "List", Width = 80 };
        var gallery = new Button { Name = "FocusGallery", Content = "Gallery", Width = 100, Command = command };
        var always = new Button { Name = "FocusAlways", Content = "Always", Width = 80 };
        ToolBar.SetOverflowMode(view, OverflowMode.Never);
        ToolBar.SetOverflowMode(gallery, OverflowMode.AsNeeded);
        ToolBar.SetOverflowMode(always, OverflowMode.Always);
        var toolbar = new ActionToolBar { Width = 120, Items = { view, gallery, always } };
        var outside = new Button { Name = "FocusOutside", Content = "Another focus target" };
        var fixture = new Window
        {
            Width = 440, Height = 200,
            Content = new StackPanel { Children = { toolbar, outside } }
        };
        fixture.Show(owner);
        Exception? checkFailure = null;
        string stage = "native fixture activation";
        try
        {
            // Show() only requests native activation. As in the dynamic-toolbar
            // fixture, finish it before entering a popup's focus scope: a late
            // X11 activation otherwise clears the focus this check just assigned.
            await Program.WaitUntilAsync(() => fixture.IsActive);
            stage = "initial overflow";
            await Program.WaitUntilAsync(() => toolbar.More?.IsEffectivelyVisible == true
                && toolbar.OverflowHost?.Children.Contains(gallery) == true);
            toolbar.More!.IsChecked = true;
            await Program.WaitUntilAsync(() => PopupReady(gallery));
            // Do not race OnPopupOpened's Loaded callback with explicit focus.
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            Require(gallery.Focus(NavigationMethod.Tab), "Could not focus the original overflow action.");
            Require(ReferenceEquals(fixture.FocusManager?.GetFocusedElement(), gallery), "Overflow action did not own actual focus.");

            stage = "overflow to direct";
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

            stage = "direct to overflow";
            toolbar.Width = 120;
            fixture.UpdateLayout();
            await Program.WaitUntilAsync(() => toolbar.OverflowHost!.Children.Contains(gallery)
                && ReferenceEquals(fixture.FocusManager?.GetFocusedElement(), toolbar.More));
            Require(ReferenceEquals(gallery.Command, command), "Shrink replaced the command.");
            stage = "reopen overflow and Escape";
            toolbar.More.IsChecked = true;
            // No explicit Focus() here: reopening must exercise production repair.
            await Program.WaitUntilAsync(() => PopupReady(gallery)
                && ReferenceEquals(fixture.FocusManager?.GetFocusedElement(), gallery));
            toolbar.OverflowHost!.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape });
            await Program.WaitUntilAsync(() => toolbar.More.IsChecked != true
                && ReferenceEquals(fixture.FocusManager?.GetFocusedElement(), toolbar.More));
            Require(executions == 1, "Shrink/open/Escape executed an action.");

            stage = "explicit competing focus wins";
            // A user focus move after synchronous reparenting wins over the queued repair.
            toolbar.More.IsChecked = true;
            // No explicit Focus() here: reopening must exercise production repair.
            await Program.WaitUntilAsync(() => PopupReady(gallery)
                && ReferenceEquals(fixture.FocusManager?.GetFocusedElement(), gallery));
            toolbar.Width = 360;
            fixture.UpdateLayout();
            Require(outside.Focus(NavigationMethod.Tab), "Could not place the competing explicit focus.");
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            Require(ReferenceEquals(fixture.FocusManager?.GetFocusedElement(), outside),
                "Deferred overflow repair stole focus from another control.");
            Require(ReferenceEquals(toolbar.Items[1], gallery) && ReferenceEquals(gallery.Command, command)
                && toolbar.OverflowHost!.Children.Contains(always), "Return path lost identity or remaining Always overflow.");
        }
        catch (Exception exception)
        {
            checkFailure = exception;
            SmokeCleanup.Diagnose(() => Console.Error.WriteLine("SMOKE toolbar focus state: " + JsonSerializer.Serialize(new
            {
                stage, fixture.IsActive, fixture.IsVisible, fixture.Bounds,
                focused = Describe(fixture.FocusManager?.GetFocusedElement() as Control),
                more = Describe(toolbar.More), moreOpen = toolbar.More?.IsChecked,
                gallery = Describe(gallery), outside = Describe(outside), executions,
                overflow = toolbar.OverflowHost?.Children.Select(Describe).ToArray()
            })));
            Console.Error.WriteLine($"SMOKE toolbar focus failure at {stage}: {exception}");
            SmokeCleanup.Diagnose(() => Program.CaptureWindow(fixture, "smoke-toolbar-focus-failure.png"));
            throw;
        }
        finally
        {
            await SmokeCleanup.RunAsync(checkFailure, () =>
            {
                fixture.Close();
                return Task.CompletedTask;
            }, exception => Console.Error.WriteLine("SMOKE toolbar fixture close failed: " + exception));
        }
    }

    private static bool PopupReady(Control action) => action.IsEffectivelyVisible
        && action.Bounds.Width > 0 && action.Bounds.Height > 0
        && TopLevel.GetTopLevel(action) is { IsVisible: true };

    private static object? Describe(Control? control) => control == null ? null : new
    {
        control.Name, type = control.GetType().Name, control.Bounds, control.IsEffectivelyVisible,
        control.IsEffectivelyEnabled, control.Focusable, control.IsKeyboardFocusWithin,
        root = TopLevel.GetTopLevel(control)?.GetType().Name,
        rootVisible = TopLevel.GetTopLevel(control)?.IsVisible
    };

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
