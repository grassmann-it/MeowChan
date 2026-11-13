using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Styling;
using Avalonia.Threading;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;

namespace MeowChan.DesktopSmoke;

/// <summary>
/// Evidence only: the Plugins page over the passive UX fixture at 1440 x 920, 900 x 760 and 860 x 760 (one pane), Dark and Light, EN and DE, so its look
/// can be compared with the Sources page (smoke-sources-wide-*). Nothing is compiled, described, started or probed; the checks are
/// limited to what the comparison needs (the selected plugin's heading and its state action are on screen).
/// </summary>
internal static class PluginPageCaptureChecks
{
    internal static async Task RunAsync(MainWindow window)
    {
        PluginUxFixture fixture = await PluginUxFixture.CreateAsync(Path.Combine(Config.GetConfigDirectory(), "plugin-page-captures"));
        PluginsPage page = new(); page.Attach(fixture.Services);
        ContentControl host = window.FindControl<ContentControl>("PluginsPageHost")!;
        object? retained = host.Content;
        string language = Texts.Language, mode = window.SessionHost.Config.WorkspaceMode;
        ThemeVariant? theme = Application.Current!.RequestedThemeVariant;
        double width = window.Width, height = window.Height;
        host.Content = page;
        try
        {
            window.GetLogicalDescendants().OfType<MenuItem>().First(item => Equals(item.Tag, "nav.plugins"))
                .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            await PresentationChecks.SwitchModeAsync(window, "Simple");
            await page.ActivateAsync();
            foreach ((double captureWidth, double captureHeight, string size) in new[] { (1440d, 920d, "1440"), (900d, 760d, "900"), (860d, 760d, "860") })
            foreach (ThemeVariant variant in new[] { ThemeVariant.Dark, ThemeVariant.Light })
            {
                string suffix = variant.Key!.ToString()!.ToLowerInvariant();
                Application.Current.RequestedThemeVariant = variant;
                window.WindowState = WindowState.Normal; window.Width = captureWidth; window.Height = captureHeight;
                await Program.WaitUntilAsync(() => PresentationChecks.MatchesRequestedSize(window, captureWidth, captureHeight)
                    && window.ActualThemeVariant == variant);
                foreach (string captureLanguage in new[] { "de", "en" })
                {
                    Texts.SetLanguage(captureLanguage);
                    await page.RefreshAsync();
                    if (size == "1440" && captureLanguage == "de")
                    {
                        // The page before any choice (the list and the hint in the detail column) needs a page without a selection.
                        PluginsPage fresh = new(); fresh.Attach(fixture.Services);
                        host.Content = fresh;
                        try
                        {
                            await fresh.ActivateAsync();
                            Require(fresh.Selected == null, "A fresh Plugins page selected a plugin by itself.");
                            await Capture(window, fresh, null, "smoke-plugins-page-" + size + "-none-" + captureLanguage + "-" + suffix);
                        }
                        finally { fresh.EndLifetime(); host.Content = page; }
                        await page.RefreshAsync();
                    }
                    await Capture(window, page, PluginUxFixture.SourceKey, "smoke-plugins-page-" + size + "-source-" + captureLanguage + "-" + suffix);
                    if (captureLanguage == "de")
                        await Capture(window, page, PluginUxFixture.FilterKey, "smoke-plugins-page-" + size + "-filter-" + captureLanguage + "-" + suffix);
                    if (size == "860" && captureLanguage == "de")
                    {
                        // Below the 886 dip switch point: one pane; Back returns to the list.
                        Require(page.FindControl<Button>("PluginBack")!.IsEffectivelyVisible && !page.FindControl<Grid>("PluginListPane")!.IsVisible,
                            "At 860 dip the Plugins page was not in its one-pane layout.");
                        page.FindControl<Button>("PluginBack")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        await Capture(window, page, null, "smoke-plugins-page-" + size + "-list-" + captureLanguage + "-" + suffix);
                    }
                    if (size == "1440" && captureLanguage == "de")
                    {
                        // The image-filter category: the list shows only the filter plugin, the details stay with the choice.
                        page.FindControl<ComboBox>("PluginCategory")!.SelectedIndex = 2;
                        await Capture(window, page, PluginUxFixture.FilterKey, "smoke-plugins-page-" + size + "-category-" + captureLanguage + "-" + suffix);
                        page.FindControl<ComboBox>("PluginCategory")!.SelectedIndex = 0;
                    }
                }
            }
            Require(fixture.ForbiddenCalls == 0, "The capture pass compiled, described, started or probed a plugin.");
            Console.WriteLine("PLUGIN_PAGE_CAPTURES PASS: 1440x920, 900x760 and 860x760 (one pane), Dark and Light, EN and DE; evidence only.");
        }
        finally
        {
            page.EndLifetime(); host.Content = retained;
            Texts.SetLanguage(language); Application.Current!.RequestedThemeVariant = theme;
            await PresentationChecks.SwitchModeAsync(window, mode);
            window.Width = width; window.Height = height;
        }
    }

    private static async Task Capture(MainWindow window, PluginsPage page, string? key, string name)
    {
        if (key != null)
        {
            page.Select(key);
            page.FindControl<ScrollViewer>("PluginDetailsPane")!.Offset = default;
        }
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        if (key != null)
            Require(page.Selected?.Key == key && page.FindControl<TextBlock>("PluginHeading")!.IsEffectivelyVisible
                && page.FindControl<Button>("PluginEnabled")!.IsEffectivelyVisible,
                "The selected plugin's heading or state action was not shown for the capture " + name + ".");
        Program.CaptureWindow(window, name + ".png");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
