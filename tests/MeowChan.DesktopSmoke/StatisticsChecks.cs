using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.Stats;
using MeowChan.Http;

namespace MeowChan.DesktopSmoke;

/// <summary>
/// The header graph and the statistics page with synthetic counters only: no request leaves the process, the numbers are
/// written into the meter the way the transport writes them. Covers the graph in every work view, the "Header graph" setting
/// through the real Settings page, the page, its reset question, the sampling claim and light/dark/German captures.
/// </summary>
internal static class StatisticsChecks
{
    internal const int SampleIntervalMs = 500;
    private const string Fixture = "stats-fixture";
    private const string SecondFixture = "stats-fixture-b";

    internal static async Task RunAsync(MainWindow window)
    {
        using CancellationTokenSource stop = new();
        Task traffic = Task.Run(() => GenerateTrafficAsync(stop.Token));
        double widthBefore = window.Width, heightBefore = window.Height;
        try
        {
            await PrepareAsync(window);
            await Program.WaitUntilAsync(() => Hub(window).Snapshot().Series.Count >= 8,
                () => "samples " + Hub(window).Snapshot().Series.Count + ", claim " + window.IoStatsClaim);
            await HeaderAsync(window);
            await SettingAsync(window);
            await PageAsync(window, "dark");
            await SetThemeAsync(window, "Light");
            await HeaderAndPageCapturesAsync(window, "light");
            await FooterNavigationChecks.PersistLanguageAsync(window, "de");
            await HeaderAndPageCapturesAsync(window, "light-de");
            await FooterNavigationChecks.PersistLanguageAsync(window, "en");
            await SetThemeAsync(window, "Dark");
            await ClaimAsync(window);
            stop.Cancel();
            await traffic;
            await ResetAsync(window);
        }
        finally
        {
            stop.Cancel();
            try { await traffic; } catch (OperationCanceledException) { }
            window.Width = widthBefore; window.Height = heightBefore;
        }
    }

    private static IoStatsHub Hub(MainWindow window) => window.SessionHost.IoStats;

    // A smooth wave on two sources: network, disk write and read, requests, now and then an error.
    private static async Task GenerateTrafficAsync(CancellationToken stop)
    {
        SourceCounters first = TransferMeter.For(Fixture), second = TransferMeter.For(SecondFixture);
        for (long tick = 0; !stop.IsCancellationRequested; tick++)
        {
            double wave = 0.5 + 0.5 * Math.Sin(tick / 7.0);
            Interlocked.Add(ref first.NetDown, (long)((150_000 + wave * 1_650_000) * 0.2));
            Interlocked.Add(ref second.NetDown, (long)((30_000 + (1 - wave) * 300_000) * 0.2));
            Interlocked.Add(ref first.DiskWritten, (long)(wave * 700_000 * 0.2));
            if (tick % 9 == 0) Interlocked.Add(ref first.DiskRead, 220_000);
            Interlocked.Increment(ref first.Requests);
            if (tick % 3 == 0) Interlocked.Increment(ref second.Requests);
            if (tick % 41 == 7) Interlocked.Increment(ref second.Errors);
            try { await Task.Delay(200, stop); } catch (OperationCanceledException) { return; }
        }
    }

    private static async Task PrepareAsync(MainWindow window)
    {
        Avalonia.Platform.Screen screen = window.Screens.ScreenFromWindow(window) ?? throw new InvalidOperationException("The statistics smoke has no screen.");
        double frame = Math.Max(0, (window.FrameSize?.Width ?? window.ClientSize.Width) - window.ClientSize.Width);
        double width = _wideWidth = Math.Min(1400, Math.Floor(screen.WorkingArea.Width / window.RenderScaling - frame));
        await FooterNavigationChecks.PersistLanguageAsync(window, "en");
        await SetThemeAsync(window, "Dark");
        await PresentationChecks.SwitchModeAsync(window, "Advanced");
        window.Width = width;
        window.Height = Math.Max(window.MinHeight, 820);
        await Program.WaitUntilAsync(() => Math.Abs(window.Bounds.Width - width) <= 1 && window.FindControl<Control>("TransferSpeedGraph")!.IsVisible);
        await GoToAsync(window, "navigation.downloads", MainWindow.WorkspacePage.Downloads);
        Require(width >= 1000, "The smoke screen is too narrow for the header graph: " + width);
        Require(window.IoStatsClaim == IoStatsVisibility.Window, "A visible header graph holds no window claim: " + window.IoStatsClaim);
    }

    private static double _wideWidth;

    // Leaving Barebones keeps its fitted size; the graph needs the wide window again.
    private static async Task EnsureWideAsync(MainWindow window)
    {
        window.Width = _wideWidth;
        window.Height = Math.Max(window.MinHeight, 820);
        await Program.WaitUntilAsync(() => Math.Abs(window.Bounds.Width - _wideWidth) <= 1 && HeaderGraph(window).IsVisible,
            () => $"width {window.Bounds.Width}, wanted {_wideWidth}");
        await Settle(window);
    }

    private static Button Speed(MainWindow window) => window.FindControl<Button>("TransferSpeed")!;
    private static IoGraph HeaderGraph(MainWindow window) => window.FindControl<IoGraph>("TransferSpeedGraph")!;
    private static IoGraph HeaderDiskGraph(MainWindow window) => window.FindControl<IoGraph>("TransferDiskGraph")!;
    // The disk row (graph, icon and value) is hidden as one; its own visibility is what the work view and the setting decide.
    private static bool DiskRowShown(MainWindow window) => window.FindControl<Control>("TransferDiskRow")!.IsVisible;

    private static async Task RequireHeaderAsync(MainWindow window, bool disk, string scenario)
    {
        await Program.WaitUntilAsync(() => DiskRowShown(window) == disk
            && AutomationProperties.GetName(Speed(window)) is { Length: > 0 } name && name.Contains("disk", StringComparison.OrdinalIgnoreCase) == disk,
            () => $"{scenario}: row {DiskRowShown(window)}, name '{AutomationProperties.GetName(Speed(window))}'");
        string text = AutomationProperties.GetName(Speed(window))!;
        Require(Texts.T("stats.graph.name", "", "", 0).Length > 0 && ToolTip.GetTip(Speed(window)) as string == text,
            scenario + ": the tooltip differs from the accessible name.");
        Require(DiskRowShown(window) == disk && window.FindControl<Control>("TransferDiskIcon")!.IsVisible && window.FindControl<Control>("TransferDiskLabel")!.IsVisible,
            scenario + ": the disk value is " + (disk ? "missing" : "shown") + ".");
        // Barebones fits the window to its minimum, below the widths where the graph (1000) and the button (760) appear.
        if (window.Bounds.Width >= 1000)
        {
            Require(HeaderGraph(window).IsEffectivelyVisible && HeaderGraph(window).SampleCount >= 8 && HeaderGraph(window).Bounds.Width > 0,
                scenario + ": the header graph shows no history.");
            // The disk row is its own graph with its own scale, drawing the write series only.
            Require(HeaderGraph(window).Source == IoGraphSource.NetDown && HeaderDiskGraph(window).Source == IoGraphSource.DiskWrite,
                scenario + ": a header row draws the wrong series.");
            if (disk)
                Require(HeaderDiskGraph(window).IsEffectivelyVisible && HeaderDiskGraph(window).SampleCount >= 8 && HeaderDiskGraph(window).Bounds.Width > 0,
                    scenario + ": the disk row shows no history.");
        }
        if (window.Bounds.Width >= 760)
            Require(Speed(window) is { IsEffectivelyEnabled: true, IsEffectivelyVisible: true } && Speed(window).IsTabStop,
                scenario + ": the graph button cannot be reached.");
        Console.WriteLine($"SMOKE statistics header: {scenario}; disk row {(disk ? "drawn" : "omitted")}; name '{text}'.");
    }

    private static async Task HeaderAsync(MainWindow window)
    {
        // Automatic follows the work view and updates the moment it changes.
        foreach ((string mode, bool disk) in new[] { ("Advanced", true), ("Simple", false), ("Barebones", false), ("Advanced", true) })
        {
            await PresentationChecks.SwitchModeAsync(window, mode);
            if (mode != "Barebones") await EnsureWideAsync(window);
            await RequireHeaderAsync(window, disk, "Auto/" + mode);
            if (mode != "Barebones") Program.CaptureWindow(window, $"smoke-statistics-header-dark-{mode.ToLowerInvariant()}.png");
        }
        Program.CaptureWindow(window, "smoke-statistics-header-dark-advanced.png");
    }

    // The real Settings page: the block, its texts, the three values and their effect on the header.
    private static async Task SettingAsync(MainWindow window)
    {
        window.FindControl<MenuItem>("SettingsMenuItem")!.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        await Program.WaitUntilAsync(() => window.SettingsPageVisible);
        SettingsEditor settings = window.SettingsEditor;
        await ShowBehaviorAsync(settings);
        await Program.WaitUntilAsync(() => settings.FindControl<Control>("SettingsBehaviorPage")!.IsVisible);
        ComboBox input = settings.FindControl<ComboBox>("StatsHeaderGraphInput")!;
        await Program.WaitUntilAsync(() => input.IsEffectivelyVisible && input.Bounds.Width > 0);
        Require(settings.StatsHeaderGraphChoices.Select(choice => choice.Value).SequenceEqual(["Auto", "Network", "NetworkAndDisk"]),
            "The header graph setting does not offer Auto, Network and NetworkAndDisk.");
        Require(settings.SelectedStatsHeaderGraph?.Value == "Auto" && settings.StatsHeaderGraphLabel == Texts.T("stats.headerGraph.label")
            && settings.StatsHeaderGraphHint == "Chooses what the graph at the top shows. Automatic: network and disk in the Advanced view, network only otherwise.",
            "The header graph setting lost its default, label or hint.");
        Require(AutomationProperties.GetName(input) == settings.StatsHeaderGraphLabel && AutomationProperties.GetHelpText(input) == settings.StatsHeaderGraphHint,
            "The header graph setting has no accessible name or help text.");
        Program.CaptureWindow(window, "smoke-statistics-settings-block-dark.png");
        // Advanced: Network hides the disk, NetworkAndDisk shows it again; Simple with NetworkAndDisk keeps it.
        await SaveChoiceAsync(window, settings, "Network");
        await GoHeaderAsync(window, "Advanced", disk: false, "Network/Advanced");
        await SaveChoiceAsync(window, settings, "NetworkAndDisk");
        await GoHeaderAsync(window, "Simple", disk: true, "NetworkAndDisk/Simple");
        await GoHeaderAsync(window, "Barebones", disk: true, "NetworkAndDisk/Barebones");
        await SaveChoiceAsync(window, settings, "Auto");
        await GoHeaderAsync(window, "Advanced", disk: true, "Auto/Advanced");
        await EnsureWideAsync(window);
        await SaveChoiceAsync(window, settings, "Network");
        Require(Config.LoadConfig().StatsHeaderGraph == "Network" && window.SessionHost.Config.StatsHeaderGraph == "Network",
            "The header graph setting was not persisted.");
        await SaveChoiceAsync(window, settings, "Auto");
        await GoToAsync(window, "navigation.downloads", MainWindow.WorkspacePage.Downloads);
    }

    private static async Task ShowBehaviorAsync(SettingsEditor settings)
    {
        settings.OpenSectionNavigation();
        Require(settings.CanGoToSection("settings.behavior"), "The Behavior section is not available.");
        settings.GoToSection("settings.behavior");
        await Program.WaitUntilAsync(() => settings.FindControl<Control>("SettingsBehaviorPage")!.IsVisible);
    }

    private static async Task GoHeaderAsync(MainWindow window, string mode, bool disk, string scenario)
    {
        await PresentationChecks.SwitchModeAsync(window, mode);
        if (mode != "Barebones") await EnsureWideAsync(window);
        await RequireHeaderAsync(window, disk, scenario);
        // The work view switch leaves Settings through the footer; open it again for the next choice.
        window.FindControl<MenuItem>("SettingsMenuItem")!.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        await Program.WaitUntilAsync(() => window.SettingsPageVisible);
        await ShowBehaviorAsync(window.SettingsEditor);
    }

    private static async Task SaveChoiceAsync(MainWindow window, SettingsEditor settings, string value)
    {
        settings.FindControl<ComboBox>("StatsHeaderGraphInput")!.SelectedItem = settings.StatsHeaderGraphChoices.Single(choice => choice.Value == value);
        settings.FindControl<Button>("SaveSettingsButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Program.WaitUntilAsync(() => !settings.IsBusy && Config.LoadConfig().StatsHeaderGraph == value
            && window.SessionHost.Config.StatsHeaderGraph == value && window.FindControl<Button>("AddUrlButton")!.IsEnabled,
            () => $"wanted {value}, file {Config.LoadConfig().StatsHeaderGraph}, session {window.SessionHost.Config.StatsHeaderGraph}, busy {settings.IsBusy}, error '{settings.ErrorMessage}'");
        // The header shows the new value without leaving Settings.
        string mode = window.SessionHost.Config.WorkspaceMode;
        bool disk = value == "NetworkAndDisk" || (value == "Auto" && mode == "Advanced");
        await Program.WaitUntilAsync(() => DiskRowShown(window) == disk, () => $"{value}/{mode}: disk row {DiskRowShown(window)}");
        await ShowBehaviorAsync(settings);
    }

    private static async Task GoToAsync(MainWindow window, string command, MainWindow.WorkspacePage page)
    {
        KeyGesture gesture = CommandBindings.Gesture(command) ?? throw new InvalidOperationException("No shortcut for " + command);
        KeyEventArgs args = new() { RoutedEvent = InputElement.KeyDownEvent, Key = gesture.Key, KeyModifiers = gesture.KeyModifiers };
        window.RaiseEvent(args);
        Require(args.Handled, command + " is not handled by the shell.");
        await Program.WaitUntilAsync(() => window.SelectedPage == page, () => "page " + window.SelectedPage);
        await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Background);
    }

    private static StatisticsPage Page(MainWindow window) => (StatisticsPage)window.FindControl<ContentControl>("StatisticsPageHost")!.Content!;

    private static async Task OpenPageAsync(MainWindow window, bool byGraphClick)
    {
        if (byGraphClick) Speed(window).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        else await GoToAsync(window, "navigation.statistics", MainWindow.WorkspacePage.Statistics);
        await Program.WaitUntilAsync(() => window.SelectedPage == MainWindow.WorkspacePage.Statistics
            && window.FindControl<ContentControl>("StatisticsPageHost")!.Content is StatisticsPage
            && Page(window).Rows.Any(row => row.Key == Fixture) && Page(window).Graph.SampleCount >= 8,
            () => "page " + window.SelectedPage);
    }

    private static async Task PageAsync(MainWindow window, string variant)
    {
        // The shortcut opens the page and the footer button is selected.
        await OpenPageAsync(window, byGraphClick: false);
        Button footer = window.FindControl<Button>("StatisticsNavigationButton")!;
        Require(footer.IsVisible && footer.Classes.Contains("selected"), "The footer does not mark the open Statistics page.");
        await GoToAsync(window, "navigation.downloads", MainWindow.WorkspacePage.Downloads);
        Require(!footer.Classes.Contains("selected"), "The footer still marks Statistics after leaving it.");
        // A click on the header graph opens it too.
        await OpenPageAsync(window, byGraphClick: true);
        StatisticsPage page = Page(window);
        Require(window.IoStatsClaim == IoStatsVisibility.Window, "The open page holds no window claim.");
        Require(page.Rows.Count >= 3 && page.Rows.Last().IsTotal && page.Rows.Any(row => row.Key == SecondFixture),
            "The table lacks the sources or the total row: " + string.Join(", ", page.Rows.Select(row => row.Key)));
        StatisticsRow first = page.Rows.Single(row => row.Key == Fixture);
        Require(first.Requests != "0" && first.Session.Contains('B') && first.Today.Length > 0 && first.Retained.Length > 0,
            "The fixture row shows no counted traffic: " + first.Session + " / " + first.Requests);
        Require(page.Graph.Source == IoGraphSource.NetDown && page.WriteGraph.Source == IoGraphSource.DiskWrite && page.ReadGraph.Source == IoGraphSource.DiskRead,
            "The page graphs do not draw network, disk write and disk read.");
        Require(page.Graph.Peak > IoGraphGeometry.DefaultMinimumPeak && page.WriteGraph.Peak > IoGraphGeometry.DefaultMinimumPeak
            && page.Graph.Peak != page.WriteGraph.Peak && page.WriteGraph.SampleCount >= 8 && page.ReadGraph.SampleCount >= 8,
            $"The page graphs never scaled up or share one scale: net {page.Graph.Peak}, write {page.WriteGraph.Peak}, read {page.ReadGraph.Peak}.");
        // The page shows all rows whatever the header setting is, each with its own name.
        foreach (IoGraph graph in new[] { page.Graph, page.WriteGraph, page.ReadGraph })
            Require(AutomationProperties.GetName(graph) is { Length: > 0 }, "A page graph has no accessible name.");
        Require(AutomationProperties.GetName(page.ReadGraph)!.StartsWith(Texts.T("stats.metric.diskRead"), StringComparison.Ordinal),
            "The read graph is not named as the read series.");
        Require(page.ResetButton.IsEffectivelyVisible && page.ResetButton.IsEnabled, "Reset is not available.");
        Require(!page.IsResetConfirmationOpen, "The reset question is open before Reset was pressed.");
        await Settle(window);
        Program.CaptureWindow(window, $"smoke-statistics-page-{variant}.png");
        // The question is never the default: it opens on Cancel and Cancel keeps everything.
        long before = Hub(window).Snapshot().Total.Session.NetDown;
        page.ResetButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Program.WaitUntilAsync(() => page.IsResetConfirmationOpen);
        Require(page.CancelButton.IsFocused, "The reset question does not focus Cancel.");
        await Settle(window);
        Program.CaptureWindow(window, $"smoke-statistics-page-{variant}-confirm.png");
        page.CancelButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Program.WaitUntilAsync(() => !page.IsResetConfirmationOpen && page.ResetButton.IsEnabled);
        Require(Hub(window).Snapshot().Total.Session.NetDown >= before, "Cancel reset the counters.");
        Console.WriteLine("SMOKE statistics page: " + variant + "; rows " + page.Rows.Count + "; the reset question opens on Cancel.");
    }

    private static async Task HeaderAndPageCapturesAsync(MainWindow window, string variant)
    {
        await GoToAsync(window, "navigation.downloads", MainWindow.WorkspacePage.Downloads);
        await Settle(window);
        Program.CaptureWindow(window, $"smoke-statistics-header-{variant}.png");
        await OpenPageAsync(window, byGraphClick: true);
        await Settle(window);
        Require(Page(window).Rows.Any(row => row.Key == Fixture) && Page(window).ResetButton.IsEffectivelyVisible, "The page is incomplete in " + variant + ".");
        Program.CaptureWindow(window, $"smoke-statistics-page-{variant}.png");
        await GoToAsync(window, "navigation.downloads", MainWindow.WorkspacePage.Downloads);
        Console.WriteLine("SMOKE statistics captures: " + variant + " (" + Texts.Language + ").");
    }

    // The window claims samples only while the graph or the page is visible; nothing runs for nobody.
    private static async Task ClaimAsync(MainWindow window)
    {
        await GoToAsync(window, "navigation.downloads", MainWindow.WorkspacePage.Downloads);
        Require(window.IoStatsClaim == IoStatsVisibility.Window && Hub(window).SamplingMode == IoStatsVisibility.Window,
            "The visible graph does not hold the sampling claim.");
        // Only Barebones may be narrower than the 760 pixels at which the header shows the graph button.
        await PresentationChecks.SwitchModeAsync(window, "Barebones");
        await GoToAsync(window, "navigation.downloads", MainWindow.WorkspacePage.Downloads);
        window.Width = Math.Max(window.MinWidth, 660);
        await Program.WaitUntilAsync(() => !Speed(window).IsVisible && window.IoStatsClaim == IoStatsVisibility.None && Hub(window).SamplingMode == IoStatsVisibility.None,
            () => $"claim {window.IoStatsClaim}, mode {Hub(window).SamplingMode}, graph {Speed(window).IsVisible}");
        await GoToAsync(window, "navigation.statistics", MainWindow.WorkspacePage.Statistics);
        await Program.WaitUntilAsync(() => window.IoStatsClaim == IoStatsVisibility.Window && Hub(window).SamplingMode == IoStatsVisibility.Window,
            () => $"claim {window.IoStatsClaim}, mode {Hub(window).SamplingMode}");
        await Settle(window);
        Program.CaptureWindow(window, "smoke-statistics-page-narrow.png");
        await GoToAsync(window, "navigation.downloads", MainWindow.WorkspacePage.Downloads);
        await Program.WaitUntilAsync(() => window.IoStatsClaim == IoStatsVisibility.None && Hub(window).SamplingMode == IoStatsVisibility.None,
            () => $"claim {window.IoStatsClaim}, mode {Hub(window).SamplingMode}");
        await PresentationChecks.SwitchModeAsync(window, "Advanced");
        await EnsureWideAsync(window);
        await Program.WaitUntilAsync(() => Speed(window).IsVisible && window.IoStatsClaim == IoStatsVisibility.Window);
        Console.WriteLine("SMOKE statistics claim: held with the graph or the page, released when neither is visible.");
    }

    private static async Task ResetAsync(MainWindow window)
    {
        await Task.Delay(1200); // let the last samples of the stopped generator land
        await OpenPageAsync(window, byGraphClick: false);
        StatisticsPage page = Page(window);
        page.ResetButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Program.WaitUntilAsync(() => page.IsResetConfirmationOpen);
        page.ConfirmButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Program.WaitUntilAsync(() => !page.IsResetConfirmationOpen && Hub(window).Snapshot().Sources.Count == 0 && page.Rows.All(row => row.IsTotal || false),
            () => "sources " + Hub(window).Snapshot().Sources.Count + ", rows " + page.Rows.Count);
        IoStatsSnapshot snapshot = Hub(window).Snapshot();
        Require(snapshot.Total.Session.NetDown == 0 && snapshot.Total.Today.NetDown == 0 && snapshot.Series.Count == 0,
            "Reset left counters or history behind.");
        await Settle(window);
        Program.CaptureWindow(window, "smoke-statistics-page-after-reset.png");
        await GoToAsync(window, "navigation.downloads", MainWindow.WorkspacePage.Downloads);
        Console.WriteLine("SMOKE statistics reset: the confirmed reset cleared the counters and the history.");
    }

    private static async Task SetThemeAsync(MainWindow window, string theme)
    {
        if (Config.LoadConfig().Theme != theme)
            window.FindControl<Button>("ThemeToggleButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Avalonia.Styling.ThemeVariant variant = theme == "Light" ? Avalonia.Styling.ThemeVariant.Light : Avalonia.Styling.ThemeVariant.Dark;
        await Program.WaitUntilAsync(() => Config.LoadConfig().Theme == theme && Application.Current?.RequestedThemeVariant == variant
            && window.FindControl<Button>("AddUrlButton")!.IsEnabled);
    }

    private static async Task Settle(MainWindow window)
    {
        await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Background);
        await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Background);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
