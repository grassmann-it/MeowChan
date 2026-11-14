using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using MeowChan.Desktop.Commands;

namespace MeowChan.DesktopSmoke;

// One way for every smoke to open a main page since F1 (14.11.2026): the header menu (the three-dot menu) no longer lists the pages. They are
// reached through the footer buttons the profile chose, the footer's More menu (every page the footer or a work view leaves out) and the
// Ctrl/Cmd+digit shortcuts, whose digits follow the visible footer. This helper takes the first route that exists:
//   1. the page's footer button, when it is visible;
//   2. the page's entry of the More menu, when that entry is visible (GoToAsync opens the menu first, the synchronous GoTo raises the entry's click);
//   3. the page's keyboard command (the digit or the user's binding), as the last resort.
// Booru is hidden in the footer by default; a smoke that needs it calls EnableBooruAsync (the SetFooterPageVisibleAsync API, which also switches
// the "Show Booru actions" master on) or opens it through More.
internal static class SmokeNavigation
{
    private static readonly Dictionary<MainWindow.WorkspacePage, string> CommandIds = new()
    {
        [MainWindow.WorkspacePage.Downloads] = "navigation.downloads", [MainWindow.WorkspacePage.Archive] = "navigation.archive",
        [MainWindow.WorkspacePage.Media] = "navigation.media", [MainWindow.WorkspacePage.Tagging] = "navigation.tagging",
        [MainWindow.WorkspacePage.Booru] = "navigation.booru", [MainWindow.WorkspacePage.Rules] = "navigation.rules",
        [MainWindow.WorkspacePage.Sources] = "navigation.sources", [MainWindow.WorkspacePage.Status] = "navigation.status",
        [MainWindow.WorkspacePage.Settings] = "navigation.settings"
    };

    /// <summary>The XAML stem of a page's controls: "Archive" for ArchiveNavigationButton and ArchivePageHost.</summary>
    internal static string Stem(MainWindow.WorkspacePage page) => page.ToString();

    /// <summary>The footer button of a page (it exists for every page, visible or not).</summary>
    internal static Button FooterButton(MainWindow window, MainWindow.WorkspacePage page) =>
        window.FindControl<Button>(Stem(page) + "NavigationButton") ?? throw new InvalidOperationException("No footer button for " + page + ".");

    /// <summary>The More menu entry of a page; null for Downloads and Settings, which have none.</summary>
    internal static MenuItem? MoreEntry(MainWindow window, MainWindow.WorkspacePage page)
    {
        if (page is MainWindow.WorkspacePage.Downloads or MainWindow.WorkspacePage.Settings) return null;
        string tag = "nav." + Stem(page).ToLowerInvariant();
        return window.FindControl<Button>("ToolsNavigationButton")?.Flyout is MenuFlyout menu
            ? menu.Items.OfType<MenuItem>().FirstOrDefault(item => item.Tag as string == tag) : null;
    }

    /// <summary>Whether the page's current route (footer button or More entry) is enabled; the former wait on the ⋯ entry's IsEnabled.</summary>
    internal static bool IsEnabled(MainWindow window, MainWindow.WorkspacePage page) => Route(window, page) switch
    {
        "footer" => FooterButton(window, page).IsEnabled,
        "more" => MoreEntry(window, page)!.IsEnabled,
        _ => false
    };

    /// <summary>The route GoTo takes right now: "footer", "more", "command" or null when none exists.</summary>
    internal static string? Route(MainWindow window, MainWindow.WorkspacePage page)
    {
        if (FooterButton(window, page).IsVisible) return "footer";
        if (window.FindControl<Button>("ToolsNavigationButton") is { IsVisible: true } && MoreEntry(window, page) is { IsVisible: true }) return "more";
        return CommandIds.TryGetValue(page, out string? id) && CommandBindings.Gesture(id) != null ? "command" : null;
    }

    /// <summary>
    /// Runs the shell's footer refresh (UpdatePageNavigation). Booru's availability (plugin composed or revoked at runtime, "Show Booru actions")
    /// changes without that refresh running, so its footer button and More entry can be stale until the next resize or view change; a smoke that has
    /// just changed it refreshes before it looks. Reflection on a private method, as the other smokes do for the shell's private entry points.
    /// </summary>
    internal static void RefreshNavigation(MainWindow window) =>
        (typeof(MainWindow).GetMethod("UpdatePageNavigation", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("MainWindow.UpdatePageNavigation was renamed; adapt SmokeNavigation.RefreshNavigation."))
        .Invoke(window, null);

    /// <summary>
    /// Opens a page the way a person does and returns at once (the caller waits for what it needs, as it did after the former menu click).
    /// Footer button first, then the More entry, then the page's keyboard command.
    /// </summary>
    internal static void GoTo(MainWindow window, MainWindow.WorkspacePage page)
    {
        if (page == MainWindow.WorkspacePage.Booru) RefreshNavigation(window);
        switch (Route(window, page))
        {
            case "footer":
                SmokeActionClick.Execute(FooterButton(window, page));
                break;
            case "more":
                MenuItem entry = MoreEntry(window, page)!;
                entry.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                break;
            case "command":
                KeyGesture gesture = CommandBindings.Gesture(CommandIds[page])!;
                window.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = gesture.Key, KeyModifiers = gesture.KeyModifiers });
                break;
            default:
                throw new InvalidOperationException("The " + page + " page has no footer button, More entry or shortcut right now.");
        }
    }

    /// <summary>
    /// <see cref="GoTo"/>, then waits until the page is the selected one and its host is visible. With the More route the menu is opened first
    /// and closed again by the entry, as in the product.
    /// </summary>
    internal static async Task GoToAsync(MainWindow window, MainWindow.WorkspacePage page)
    {
        if (page == MainWindow.WorkspacePage.Booru) RefreshNavigation(window);
        if (Route(window, page) == "more" && window.FindControl<Button>("ToolsNavigationButton") is { Flyout: MenuFlyout menu } tools)
        {
            menu.ShowAt(tools);
            await Program.WaitUntilAsync(() => menu.IsOpen && MoreEntry(window, page) is { IsEffectivelyVisible: true, Bounds.Width: > 0 });
        }
        GoTo(window, page);
        await WaitForPageAsync(window, page);
        if (window.FindControl<Button>("ToolsNavigationButton")?.Flyout is MenuFlyout open && open.IsOpen)
        {
            open.Hide();
            await Program.WaitUntilAsync(() => !open.IsOpen);
        }
    }

    internal static Task WaitForPageAsync(MainWindow window, MainWindow.WorkspacePage page) => Program.WaitUntilAsync(() =>
        window.SelectedPage == page && (HostName(page) is not { } host || window.FindControl<Control>(host) is not { } control || control.IsVisible),
        () => "selected " + window.SelectedPage + ", wanted " + page + ", route " + Route(window, page));

    private static string? HostName(MainWindow.WorkspacePage page) => page switch
    {
        MainWindow.WorkspacePage.Downloads => "WorkspaceBody",
        _ => Stem(page) + "PageHost"
    };

    /// <summary>
    /// Switches Booru on for a smoke that needs the page: its footer button (hidden by default since F1) and the "Show Booru actions" master, through
    /// the same API a later "You use Booru - show it?" hint calls.
    /// </summary>
    internal static async Task EnableBooruAsync(MainWindow window)
    {
        await window.SetFooterPageVisibleAsync("booru", true);
        await Program.WaitUntilAsync(() => window.FooterLayout.IsVisible("navigation.booru"));
    }
}
