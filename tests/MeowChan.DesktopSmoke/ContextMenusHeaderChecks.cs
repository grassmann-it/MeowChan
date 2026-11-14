using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using MeowChan.Desktop;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Stats;

namespace MeowChan.DesktopSmoke;

/// <summary>
/// The context menu of the header's network and disk display on the real window: it opens from the Menu key and from a context
/// request, offers the seven arrangements (the stored one checked) and the statistics and settings entries, a choice changes the
/// header at once and survives in the profile file, the settings row shows the same value, "Settings: Statistics…" lands on that
/// row, Escape closes the menu and the focus returns to the display. No request leaves the process.
/// </summary>
internal static class ContextMenusHeaderChecks
{
    private static readonly string[] Values = ["Auto", "Network", "Disk", "NetworkAndDisk", "SideBySide", "Text", "Off"];

    internal static async Task RunAsync(MainWindow window)
    {
        await FooterNavigationChecks.PersistLanguageAsync(window, "en");
        await PresentationChecks.SwitchModeAsync(window, "Advanced");
        Avalonia.Platform.Screen screen = window.Screens.ScreenFromWindow(window) ?? throw new InvalidOperationException("The header menu smoke has no screen.");
        double frame = Math.Max(0, (window.FrameSize?.Width ?? window.ClientSize.Width) - window.ClientSize.Width);
        double width = Math.Min(1400, Math.Floor(screen.WorkingArea.Width / window.RenderScaling - frame));
        window.Width = width;
        window.Height = Math.Max(window.MinHeight, 820);
        await Program.WaitUntilAsync(() => Math.Abs(window.Bounds.Width - width) <= 1 && window.FindControl<Control>("TransferSpeedGraph")!.IsVisible,
            () => $"width {window.Bounds.Width}, wanted {width}");
        Require(width >= IoStatsDisplay.HeaderGraphsMinimumWidth, "The smoke screen is too narrow for the header display: " + width);
        Button speed = window.FindControl<Button>("TransferSpeed")!;
        ContextMenuHost host = window.HeaderIoMenuHost ?? throw new InvalidOperationException("The header display has no context menu host.");
        Require(window.SessionHost.Config.StatsHeaderGraph == "Auto", "The smoke profile does not start with the Auto arrangement.");

        // The Menu key opens it under the display: six choices (the stored one checked), a separator, statistics and settings.
        speed.Focus();
        await Program.WaitUntilAsync(() => speed.IsFocused);
        speed.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Apps, Source = speed });
        await WaitOpenAsync(host);
        RequireMenu(host, "Auto");
        Program.CaptureWindow(window, "smoke-context-menu-header-auto.png");

        // A choice applies at once, is written to the profile and the menu closes; the focus returns to the display.
        Choose(host, "Disk");
        await Program.WaitUntilAsync(() => Config.LoadConfig().StatsHeaderGraph == "Disk" && window.SessionHost.Config.StatsHeaderGraph == "Disk",
            () => $"file {Config.LoadConfig().StatsHeaderGraph}, session {window.SessionHost.Config.StatsHeaderGraph}");
        await Program.WaitUntilAsync(() => !window.FindControl<Control>("TransferSpeedText")!.IsVisible && window.FindControl<Control>("TransferDiskText")!.IsVisible,
            () => "the header did not switch to the disk display");
        await Program.WaitUntilAsync(() => host.OpenMenu == null && speed.IsFocused, () => $"menu open {host.OpenMenu != null}, focus on the display {speed.IsFocused}");

        // A context request (the right-click without a pointer position) shows the new state, and Escape closes it.
        speed.RaiseEvent(new ContextRequestedEventArgs { RoutedEvent = Control.ContextRequestedEvent });
        await WaitOpenAsync(host);
        RequireMenu(host, "Disk");
        Program.CaptureWindow(window, "smoke-context-menu-header-disk.png");
        window.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape });
        await Program.WaitUntilAsync(() => host.OpenMenu == null, () => "Escape did not close the header menu");

        // Settings: the row shows the same value, and the entry opens the page at that row.
        speed.RaiseEvent(new ContextRequestedEventArgs { RoutedEvent = Control.ContextRequestedEvent });
        await WaitOpenAsync(host);
        Invoke(host, "navigation.settings");
        await Program.WaitUntilAsync(() => window.SettingsPageVisible, () => "the settings page did not open");
        SettingsEditor settings = window.SettingsEditor;
        ComboBox input = settings.FindControl<ComboBox>("StatsHeaderGraphInput")!;
        await Program.WaitUntilAsync(() => input.IsEffectivelyVisible && input.Bounds.Width > 0 && input.IsFocused,
            () => $"the header graph row is not shown and focused: visible {input.IsEffectivelyVisible}, width {input.Bounds.Width}, focus {input.IsFocused}");
        Require(settings.SelectedStatsHeaderGraph?.Value == "Disk", "The settings row does not show the arrangement chosen in the menu: " + settings.SelectedStatsHeaderGraph?.Value);
        Program.CaptureWindow(window, "smoke-context-menu-header-settings.png");

        // Back to Auto through the menu, then the statistics page.
        speed.Focus();
        speed.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Apps, Source = speed });
        await WaitOpenAsync(host);
        Choose(host, "Auto");
        await Program.WaitUntilAsync(() => Config.LoadConfig().StatsHeaderGraph == "Auto" && settings.SelectedStatsHeaderGraph?.Value == "Auto",
            () => $"file {Config.LoadConfig().StatsHeaderGraph}, settings row {settings.SelectedStatsHeaderGraph?.Value}");
        await Program.WaitUntilAsync(() => host.OpenMenu == null, () => "the menu stayed open");
        speed.RaiseEvent(new ContextRequestedEventArgs { RoutedEvent = Control.ContextRequestedEvent });
        await WaitOpenAsync(host);
        Invoke(host, "navigation.statistics");
        await Program.WaitUntilAsync(() => window.SelectedPage == MainWindow.WorkspacePage.Statistics, () => "page " + window.SelectedPage);

        await CheckOffAsync(window);
        Console.WriteLine("SMOKE context menus header: menu key and context request open it, seven arrangements (the last one, Off, hides the display and stops its measuring; the statistics page's checkbox brings it back) with the stored one checked, a choice applies at once and persists, settings and statistics entries work, Escape closes, focus returns.");
    }

    /// <summary>"Off" hides the display and its sampling claim; the statistics page still claims while open and its checkbox shows the display again.</summary>
    private static async Task CheckOffAsync(MainWindow window)
    {
        Button speed = window.FindControl<Button>("TransferSpeed")!;
        ContextMenuHost host = window.HeaderIoMenuHost!;
        // From the settings page (no statistics page open), so only the header could claim samples.
        speed.RaiseEvent(new ContextRequestedEventArgs { RoutedEvent = Control.ContextRequestedEvent });
        await WaitOpenAsync(host);
        Invoke(host, "navigation.settings");
        await Program.WaitUntilAsync(() => window.SettingsPageVisible, () => "the settings page did not open");
        speed.RaiseEvent(new ContextRequestedEventArgs { RoutedEvent = Control.ContextRequestedEvent });
        await WaitOpenAsync(host);
        Choose(host, "Off");
        await Program.WaitUntilAsync(() => Config.LoadConfig().StatsHeaderGraph == "Off" && !speed.IsVisible && window.IoStatsClaim == IoStatsVisibility.None,
            () => $"file {Config.LoadConfig().StatsHeaderGraph}, display visible {speed.IsVisible}, claim {window.IoStatsClaim}");
        window.ShowStatistics();
        await Program.WaitUntilAsync(() => window.IoStatsClaim == IoStatsVisibility.Window, () => "the open statistics page does not claim samples: " + window.IoStatsClaim);
        Program.CaptureWindow(window, "smoke-context-menu-header-off.png");
        CheckBox box = ((StatisticsPage)window.FindControl<ContentControl>("StatisticsPageHost")!.Content!).HeaderMonitorBox;
        Require(box.IsChecked == false, "The statistics page's header checkbox is not cleared while the display is off.");
        box.IsChecked = true;
        box.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        await Program.WaitUntilAsync(() => Config.LoadConfig().StatsHeaderGraph == "Auto" && speed.IsVisible,
            () => $"file {Config.LoadConfig().StatsHeaderGraph}, display visible {speed.IsVisible}");
    }

    private static async Task WaitOpenAsync(ContextMenuHost host) =>
        await Program.WaitUntilAsync(() => host.OpenMenu is { IsOpen: true }, () => "the header menu did not open");

    private static MenuItem[] Items(ContextMenuHost host) => host.OpenMenu!.Items.OfType<MenuItem>().ToArray();

    private static void RequireMenu(ContextMenuHost host, string selected)
    {
        MenuItem[] items = Items(host);
        Require(host.OpenMenu!.Items.Count == 10 && host.OpenMenu.Items[7] is Separator, "The header menu is not seven choices, a separator and two entries: " + host.OpenMenu.Items.Count + " items.");
        Require(Values.Select(HeaderIoMenu.EntryId).SequenceEqual(items.Take(7).Select(item => (string)item.Tag!)), "The header menu does not offer the seven arrangements in the settings order.");
        Require(items.Take(7).All(item => item.ToggleType == MenuItemToggleType.Radio), "A header menu choice is not a radio entry.");
        MenuItem[] isChecked = items.Take(7).Where(item => item.IsChecked).ToArray();
        Require(isChecked.Length == 1 && (string)isChecked[0].Tag! == HeaderIoMenu.EntryId(selected), $"The header menu does not check {selected}.");
        Require(items[7].Tag as string == "navigation.statistics" && items[8].Tag as string == "navigation.settings", "The header menu lacks the statistics and settings entries.");
    }

    private static void Choose(ContextMenuHost host, string value) => Invoke(host, HeaderIoMenu.EntryId(value));

    private static void Invoke(ContextMenuHost host, string tag)
    {
        MenuItem item = Items(host).Single(candidate => candidate.Tag as string == tag);
        System.Windows.Input.ICommand command = item.Command!;
        Require(item.IsEnabled && command.CanExecute(null), "The header menu entry " + tag + " is disabled.");
        command.Execute(null);
        host.Close();
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
