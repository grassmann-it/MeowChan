using Avalonia.Controls;
using Avalonia.Interactivity;
using MeowChan.Desktop;
using Tmds.DBus.Protocol;

namespace MeowChan.DesktopSmoke;

/// <summary>Real Avalonia DBus icon registration on the runner's private watcher.</summary>
internal static class TrayHostChecks
{
    internal static async Task RunAsync(MainWindow window)
    {
        // Await registration rather than assuming icon creation means the host has it.
        bool registered = false;
        for (int attempt = 0; attempt < 100 && !registered; attempt++)
        {
            registered = await LinuxTrayHost.IsAvailableAsync();
            if (!registered) await Task.Delay(50);
        }
        if (!registered) throw new InvalidOperationException("The native icon was not registered with the private watcher.");

        await ControlAsync("activate");
        await AssertHiddenAsync(window, "left click on visible window");
        await ControlAsync("activate");
        await AssertRestoredAsync(window, "left click on hidden window");
        window.WindowState = WindowState.Minimized;
        await Program.WaitUntilAsync(() => window.WindowState == WindowState.Minimized);
        await ControlAsync("activate");
        await AssertRestoredAsync(window, "left click on taskbar-minimized window");
        await ControlAsync("open-menu-item");
        await Task.Delay(200);
        await AssertRestoredAsync(window, "Open menu item on visible window");
        await ControlAsync("activate");
        await AssertHiddenAsync(window, "left click before Open menu item");
        await ControlAsync("open-menu-item");
        await AssertRestoredAsync(window, "Open menu item on hidden window");

        await ControlAsync("hide-own-item");
        Click(window, "WindowMinimizeButton");
        await Program.WaitUntilAsync(() => window.WindowState == WindowState.Minimized);
        await Task.Delay(200);
        if (!window.IsVisible || !window.ShowInTaskbar)
            throw new InvalidOperationException("A host with only another process's icon stranded the window.");
        window.WindowState = WindowState.Normal;
        window.Activate();
        await AssertRestoredAsync(window, "unregistered own icon fallback");
        await ControlAsync("restore-own-item");
        await CheckIndependentPreferencesAsync(window);
        Click(window, "WindowMinimizeButton");
        await AssertHiddenAsync(window, "first minimize");
        await ControlAsync("activate");
        await AssertRestoredAsync(window, "DBus tray activation");

        Click(window, "WindowCloseButton");
        await AssertHiddenAsync(window, "first close");
        await ControlAsync("lose-host");
        await AssertRestoredAsync(window, "lost host");
        Click(window, "WindowMinimizeButton");
        await Program.WaitUntilAsync(() => window.WindowState == WindowState.Minimized);
        await Task.Delay(200);
        if (!window.IsVisible || !window.ShowInTaskbar)
            throw new InvalidOperationException("A watcher without a host stranded the window.");
        window.WindowState = WindowState.Normal;
        window.Activate();
        await AssertRestoredAsync(window, "taskbar fallback");

        await ControlAsync("drop-watcher");
        await ControlAsync("restart-watcher");
        registered = false;
        for (int attempt = 0; attempt < 100 && !registered; attempt++)
        {
            registered = await LinuxTrayHost.IsAvailableAsync();
            if (!registered) await Task.Delay(50);
        }
        if (!registered) throw new InvalidOperationException("Avalonia did not register again after watcher restart.");
        Click(window, "WindowMinimizeButton");
        await AssertHiddenAsync(window, "minimize after watcher restart");
        await ControlAsync("drop-watcher");
        await AssertRestoredAsync(window, "lost watcher");
        Console.WriteLine("SMOKE tray host: native DBus registration; actual Activate toggled visible/hidden/minimized windows independent of minimize preference; Open menu item only restored; first minimize/close removed taskbar access; host loss, watcher restart and watcher loss remained reachable.");
    }

    private static async Task CheckIndependentPreferencesAsync(MainWindow window)
    {
        SettingsEditor editor = window.SettingsEditor;
        editor.CloseToTray = false;
        editor.MinimizeToTray = false;
        editor.FindControl<Button>("CancelSettingsButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        if (!editor.CloseToTray || !editor.MinimizeToTray)
            throw new InvalidOperationException("Cancel did not restore the two confirmed tray preferences.");
        editor.CloseToTray = false;
        editor.MinimizeToTray = true;
        editor.FindControl<Button>("SaveSettingsButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Program.WaitUntilAsync(() => editor.IsAvailable && !editor.IsBusy && !Config.LoadConfig().CloseToTray);
        if (!Config.LoadConfig().MinimizeToTray)
            throw new InvalidOperationException("Saving CloseToTray changed MinimizeToTray.");
        editor.CloseToTray = true;
        editor.MinimizeToTray = false;
        editor.FindControl<Button>("SaveSettingsButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Program.WaitUntilAsync(() => editor.IsAvailable && !editor.IsBusy && Config.LoadConfig().CloseToTray);
        if (Config.LoadConfig().MinimizeToTray)
            throw new InvalidOperationException("Saving MinimizeToTray changed CloseToTray.");
        await ControlAsync("activate");
        await AssertHiddenAsync(window, "explicit left click with MinimizeToTray disabled");
        await ControlAsync("activate");
        await AssertRestoredAsync(window, "left-click restore with MinimizeToTray disabled");
        Click(window, "WindowMinimizeButton");
        await Program.WaitUntilAsync(() => window.WindowState == WindowState.Minimized);
        await Task.Delay(100);
        if (!window.IsVisible || !window.ShowInTaskbar)
            throw new InvalidOperationException("Disabled MinimizeToTray still hid a window with an active tray.");
        window.WindowState = WindowState.Normal;
        window.Activate();
        await AssertRestoredAsync(window, "ordinary minimize");
        editor.MinimizeToTray = true;
        editor.FindControl<Button>("SaveSettingsButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Program.WaitUntilAsync(() => editor.IsAvailable && !editor.IsBusy && Config.LoadConfig().MinimizeToTray);
    }

    private static void Click(MainWindow window, string control) =>
        window.FindControl<Button>(control)!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static async Task AssertHiddenAsync(MainWindow window, string step)
    {
        await Program.WaitUntilAsync(() => !window.IsVisible);
        if (window.ShowInTaskbar) throw new InvalidOperationException(step + " left a taskbar entry.");
    }

    private static async Task AssertRestoredAsync(MainWindow window, string step)
    {
        await Program.WaitUntilAsync(() => window.IsVisible && window.WindowState == WindowState.Normal);
        if (!window.ShowInTaskbar) throw new InvalidOperationException(step + " did not restore taskbar access.");
    }

    private static async Task ControlAsync(string command)
    {
        using DBusConnection connection = new(DBusAddress.Session!);
        await connection.ConnectAsync();
        await connection.CallMethodAsync(CreateControl(connection, command)).WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static MessageBuffer CreateControl(DBusConnection connection, string command)
    {
        using var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(destination: "org.meowchan.TraySmoke", path: "/StatusNotifierWatcher",
            @interface: "org.meowchan.TraySmoke", member: "Control", signature: "s");
        writer.WriteString(command);
        return writer.CreateMessage();
    }
}
