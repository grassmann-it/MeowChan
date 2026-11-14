using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Desktop;
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
            await LayoutsAsync(window, "dark");
            await SetThemeAsync(window, "Light");
            await HeaderAndPageCapturesAsync(window, "light");
            await LayoutsAsync(window, "light");
            await FooterNavigationChecks.PersistLanguageAsync(window, "de");
            await HeaderAndPageCapturesAsync(window, "light-de");
            await LayoutsAsync(window, "light-de");
            await FooterNavigationChecks.PersistLanguageAsync(window, "en");
            await SetThemeAsync(window, "Dark");
            await ClaimAsync(window);
            stop.Cancel();
            await traffic;
            await ResetAsync(window);
            await StatisticsReadabilityChecks.RunAsync(window);
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
            Interlocked.Add(ref first.NetUp, (long)((4_000 + wave * 36_000) * 0.2));
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
        await Settle(window);
        RequireRateCells(window, IoStatsDisplay.ResolveHeaderLayout("Auto", window.Bounds.Width), "initial Auto");
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
    // The disk block (heading and values; its graph follows separately) is shown or hidden by the setting.
    private static bool DiskRowShown(MainWindow window) => window.FindControl<Control>("TransferDiskText")!.IsVisible;
    private static string TextOf(MainWindow window, string name) => window.FindControl<TextBlock>(name)!.Text ?? "";

    internal static bool HeaderWordIs(MainWindow window, string slotName, string key)
    {
        TextBlock? word = window.FindControl<TextBlock>(slotName);
        return word is not null && word.Text == Texts.T(key) && word.Classes.Contains("muted")
            && word.FontSize == 11 && word.FontWeight == FontWeight.Normal
            && AutomationProperties.GetAccessibilityView(word) == AccessibilityView.Raw;
    }

    private static async Task RequireHeaderAsync(MainWindow window, bool disk, bool graph, string scenario)
    {
        // The graphs need room: in the narrow Barebones window every layout falls back to the labelled values.
        graph = graph && window.Bounds.Width >= IoStatsDisplay.HeaderGraphsMinimumWidth;
        await Program.WaitUntilAsync(() => DiskRowShown(window) == disk
            && AutomationProperties.GetName(Speed(window)) is { Length: > 0 } name && name.Contains("disk", StringComparison.OrdinalIgnoreCase) == disk,
            () => $"{scenario}: row {DiskRowShown(window)}, name '{AutomationProperties.GetName(Speed(window))}'");
        string text = AutomationProperties.GetName(Speed(window))!;
        Require(ToolTip.GetTip(Speed(window)) as string == text, scenario + ": the tooltip differs from the accessible name.");
        Require(DiskRowShown(window) == disk, scenario + ": the disk value is " + (disk ? "missing" : "shown") + ".");
        // The header matrix has no Network/Disk titles or glyphs: solid words are Down/Read, dashed words Up/Write.
        string netDown = TextOf(window, "TransferDownMark"), netUp = TextOf(window, "TransferUpMark");
        string diskWrite = TextOf(window, "TransferWriteMark"), diskRead = TextOf(window, "TransferReadMark");
        Require(HeaderWordIs(window, "TransferDownMark", "stats.header.down") && HeaderWordIs(window, "TransferUpMark", "stats.header.up")
            && TextOf(window, "TransferSpeedLabel").EndsWith("/s", StringComparison.Ordinal)
            && TextOf(window, "TransferUploadLabel").EndsWith("/s", StringComparison.Ordinal),
            scenario + ": the network word/rate matrix is incomplete: '" + netDown + "' '" + netUp + "'.");
        Require(text.Contains("upload", StringComparison.OrdinalIgnoreCase), scenario + ": the sentence does not name the upload: '" + text + "'.");
        if (disk)
        {
            Require(HeaderWordIs(window, "TransferWriteMark", "stats.header.write") && HeaderWordIs(window, "TransferReadMark", "stats.header.read")
                && TextOf(window, "TransferDiskLabel").EndsWith("/s", StringComparison.Ordinal) && TextOf(window, "TransferDiskReadLabel").EndsWith("/s", StringComparison.Ordinal),
                scenario + ": the disk word/rate matrix is incomplete: '" + diskWrite + "' '" + diskRead + "'.");
            Require(HeaderDiskGraph(window).IsVisible == graph, scenario + ": the disk graph is " + (graph ? "missing" : "drawn") + ".");
            Require(text.Contains("read", StringComparison.OrdinalIgnoreCase) && text.Contains("written", StringComparison.OrdinalIgnoreCase),
                scenario + ": the sentence does not name disk read and written: '" + text + "'.");
        }
        // Barebones fits the window to its minimum, below the widths where the graph (1000) and the button (760) appear.
        if (window.Bounds.Width >= IoStatsDisplay.HeaderGraphsMinimumWidth)
        {
            Require(HeaderGraph(window).IsEffectivelyVisible && HeaderGraph(window).SampleCount >= 8 && HeaderGraph(window).Bounds.Width > 0,
                scenario + ": the header graph shows no history.");
            // Exactly two graph surfaces, each with both directions on its own scale: network down+up, disk read+write.
            Require(HeaderGraph(window).Source == IoGraphSource.NetworkIo && HeaderDiskGraph(window).Source == IoGraphSource.DiskIo,
                scenario + ": a header row draws the wrong series.");
            if (disk && graph)
                Require(HeaderDiskGraph(window).IsEffectivelyVisible && HeaderDiskGraph(window).SampleCount >= 8 && HeaderDiskGraph(window).Bounds.Width > 0,
                    scenario + ": the disk row shows no history.");
        }
        if (window.Bounds.Width >= 760)
            Require(Speed(window) is { IsEffectivelyEnabled: true, IsEffectivelyVisible: true } && Speed(window).IsTabStop,
                scenario + ": the graph button cannot be reached.");
        Console.WriteLine($"SMOKE statistics header: {scenario}; disk value {(disk ? "shown" : "omitted")}, graph {(graph ? "drawn" : "none")}; name '{text}'.");
    }

    private static async Task HeaderAsync(MainWindow window)
    {
        // Automatic follows the work view and updates the moment it changes.
        foreach (string mode in new[] { "Advanced", "Simple", "Barebones", "Advanced" })
        {
            await PresentationChecks.SwitchModeAsync(window, mode);
            if (mode != "Barebones") await EnsureWideAsync(window);
            await RequireHeaderAsync(window, disk: true, graph: true, "Auto/" + mode);   // both graphs in every work view, where there is room
            // The tagline shows where the title bar has room and is gone where it has not (never a stub).
            Control tagline = window.FindControl<Control>("WorkspaceSubtitle")!;
            if (mode == "Barebones") Require(!tagline.IsVisible, "The tagline is still shown in the narrow Barebones title bar.");
            if (mode == "Advanced") Require(tagline.IsEffectivelyVisible && tagline.Bounds.Width > 100, $"The tagline is missing in a wide title bar: visible {tagline.IsVisible}, bounds {tagline.Bounds}, column {window.FindControl<Grid>("WorkspaceHeader")!.ColumnDefinitions[1].ActualWidth}, window {window.Bounds.Width}.");
            Program.CaptureWindow(window, $"smoke-statistics-header-dark-{mode.ToLowerInvariant()}.png");
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
        Require(settings.StatsHeaderGraphChoices.Select(choice => choice.Value).SequenceEqual(["Auto", "Network", "Disk", "NetworkAndDisk", "SideBySide", "Text"]),
            "The header graph setting does not offer Auto, Network, Disk, NetworkAndDisk, SideBySide and Text.");
        Require(settings.SelectedStatsHeaderGraph?.Value == "Auto" && settings.StatsHeaderGraphLabel == Texts.T("stats.headerGraph.label")
            && settings.StatsHeaderGraphHint == "Chooses what the header shows. Automatic: network and disk, each with its own graph, in every view. Side by side needs a wide window; narrow windows drop the graphs and keep the values.",
            "The header graph setting lost its default, label or hint.");
        Require(AutomationProperties.GetName(input) == settings.StatsHeaderGraphLabel && AutomationProperties.GetHelpText(input) == settings.StatsHeaderGraphHint,
            "The header graph setting has no accessible name or help text.");
        Program.CaptureWindow(window, "smoke-statistics-settings-block-dark.png");
        // Advanced: Network hides the disk, NetworkAndDisk shows it again; Simple with NetworkAndDisk keeps it.
        await SaveChoiceAsync(window, settings, "Network");
        await GoHeaderAsync(window, "Advanced", disk: false, graph: false, "Network/Advanced");
        await SaveChoiceAsync(window, settings, "NetworkAndDisk");
        await GoHeaderAsync(window, "Simple", disk: true, graph: true, "NetworkAndDisk/Simple");
        await GoHeaderAsync(window, "Barebones", disk: true, graph: true, "NetworkAndDisk/Barebones");
        await SaveChoiceAsync(window, settings, "Auto");
        await GoHeaderAsync(window, "Advanced", disk: true, graph: true, "Auto/Advanced");
        await EnsureWideAsync(window);
        await SaveChoiceAsync(window, settings, "Network");
        Require(Config.LoadConfig().StatsHeaderGraph == "Network" && window.SessionHost.Config.StatsHeaderGraph == "Network",
            "The header graph setting was not persisted.");
        await SaveChoiceAsync(window, settings, "Auto");
        await GoToAsync(window, "navigation.downloads", MainWindow.WorkspacePage.Downloads);
    }

    // Every layout of the setting on the real header: structure by the layout rules, then a capture per theme variant. At the wide
    // window all six values; then the narrower widths where side by side and text blocks must fall back.
    private static async Task LayoutsAsync(MainWindow window, string variant)
    {
        window.FindControl<MenuItem>("SettingsMenuItem")!.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        await Program.WaitUntilAsync(() => window.SettingsPageVisible);
        SettingsEditor settings = window.SettingsEditor;
        await ShowBehaviorAsync(settings);
        await EnsureWideAsync(window);
        Require(window.Bounds.Width >= IoStatsDisplay.HeaderBlocksMinimumWidth, "The smoke window is too narrow for two header blocks: " + window.Bounds.Width);
        double? controlsFromRight = null;
        foreach (string value in new[] { "Network", "Disk", "NetworkAndDisk", "SideBySide", "Text", "Auto" })
            await CheckLayoutAsync(window, settings, value, _wideWidth, variant, "", v => controlsFromRight = RequireSame(controlsFromRight, v, value));
        // Narrower windows: side by side first loses its full-height graphs (rows), then the graphs (text rows); text blocks lose theirs.
        foreach ((string value, double width, string tag) in new[] { ("Auto", 1380.0, "-w1380"), ("Auto", 1280.0, "-w1280"), ("Auto", 1100.0, "-w1100"), ("Auto", 900.0, "-w900"), ("SideBySide", 1280.0, "-w1280"), ("Text", 1000.0, "-w1000"), ("NetworkAndDisk", 1000.0, "-w1000"), ("SideBySide", 1150.0, "-w1150"), ("SideBySide", 900.0, "-w900"), ("Text", 900.0, "-w900"), ("NetworkAndDisk", 900.0, "-w900") })
        {
            window.Width = width;
            await Program.WaitUntilAsync(() => Math.Abs(window.Bounds.Width - width) <= 1, () => $"width {window.Bounds.Width}, wanted {width}");
            await CheckLayoutAsync(window, settings, value, width, variant, tag, v => controlsFromRight = RequireSame(controlsFromRight, v, value + tag));
        }
        await EnsureWideAsync(window);
        await SaveChoiceAsync(window, settings, "Auto");
        await GoToAsync(window, "navigation.downloads", MainWindow.WorkspacePage.Downloads);
    }

    // The distance of the window controls from the window's right edge: the same in every layout (they never move).
    private static double RequireSame(double? known, double now, string scenario)
    {
        Require(known is null || Math.Abs(known.Value - now) <= 1, $"{scenario}: the window controls moved ({known:0.#} -> {now:0.#} from the right edge).");
        return now;
    }

    private static async Task CheckLayoutAsync(MainWindow window, SettingsEditor settings, string value, double width, string variant, string tag, Action<double> controlsMoved)
    {
        Control Cell(string name) => window.FindControl<Control>(name)!;
        await SaveChoiceAsync(window, settings, value);
        IoStatsDisplay.HeaderLayout layout = IoStatsDisplay.ResolveHeaderLayout(value, window.Bounds.Width);
        await Program.WaitUntilAsync(() => Cell("TransferSpeedText").IsVisible == layout.NetValue && Cell("TransferDiskText").IsVisible == layout.DiskValue
            && Cell("TransferSpeedGraph").IsVisible == layout.NetGraph && Cell("TransferDiskGraph").IsVisible == layout.DiskGraph,
            () => $"{value}{tag}: net text {Cell("TransferSpeedText").IsVisible}, disk text {Cell("TransferDiskText").IsVisible}, net graph {Cell("TransferSpeedGraph").IsVisible}, disk graph {Cell("TransferDiskGraph").IsVisible}");
        await Settle(window);
        Button speed = Speed(window);
        Require(speed.IsEffectivelyVisible && Cell("TransferLayoutGrid").Bounds.Width > 0, value + tag + ": the header button has no size.");
        // Whatever the layout: nothing reaches into the window controls, the whole button stays inside the title bar and the controls stay put.
        Control controls = Cell("PauseButton");
        Point origin = speed.TranslatePoint(new Point(0, 0), window) ?? default;
        double controlsX = controls.TranslatePoint(new Point(0, 0), window)!.Value.X;
        Require(origin.X + speed.Bounds.Width <= controlsX + 1, $"{value}{tag}: the header button overlaps the window controls ({origin.X + speed.Bounds.Width} vs {controlsX}).");
        controlsMoved(window.Bounds.Width - controlsX);
        Control header = Cell("WorkspaceHeader");
        Require(speed.Bounds.Height <= header.Bounds.Height + 0.5 && origin.Y >= -0.5, $"{value}{tag}: the header button is taller than the title bar ({speed.Bounds.Height} vs {header.Bounds.Height}).");
        Rect logo = new(Cell("WorkspaceLogo").TranslatePoint(default, window)!.Value, Cell("WorkspaceLogo").Bounds.Size);
        Rect version = new(Cell("WorkspaceVersion").TranslatePoint(default, window)!.Value, Cell("WorkspaceVersion").Bounds.Size);
        Require(origin.X >= Math.Max(logo.Right, version.Right) + 11, value + tag + ": the header metrics overlap the brand.");
        if (layout.TwoLineLabels)
        {
            if (layout.NetValue && layout.DiskValue)
                Require(Grid.GetColumn(Cell("TransferDiskText")) > Grid.GetColumn(Cell("TransferSpeedText")),
                    value + tag + ": the directional blocks are not side by side.");
            foreach (string graph in new[] { "TransferSpeedGraph", "TransferDiskGraph" })
                if (Cell(graph).IsVisible)
                    Require(Cell(graph).Bounds.Height >= header.Bounds.Height * 0.6,
                        value + tag + ": the block graph is not full height: " + graph);
        }
        else if (layout.NetValue && layout.DiskValue)
            Require(Grid.GetRow(Cell("TransferDiskText")) == 1 && Grid.GetRow(Cell("TransferSpeedText")) == 0,
                value + tag + ": the compact rows are not stacked.");
        RequireRateCells(window, layout, value + tag);
        Program.CaptureWindow(window, $"smoke-statistics-layout-{value.ToLowerInvariant()}{tag}-{variant}.png");
        Console.WriteLine($"SMOKE statistics layout: {value}{tag}/{variant}; net {layout.NetValue}/{layout.NetGraph}, disk {layout.DiskValue}/{layout.DiskGraph}, {layout.Arrangement}; header {speed.Bounds.Width:0}x{speed.Bounds.Height:0}.");
    }

    private static void RequireRateCells(MainWindow window, IoStatsDisplay.HeaderLayout layout, string scenario)
    {
        Rect Bounds(string name)
        {
            Control control = window.FindControl<Control>(name)!;
            Point origin = control.TranslatePoint(default, window)!.Value;
            return new Rect(origin, control.Bounds.Size);
        }
        bool Near(double actual, double expected) => Math.Abs(actual - expected) < 0.6;
        Require(window.FindControl<Control>("TransferSpeedTitle") is null && window.FindControl<Control>("TransferDiskTitle") is null,
            scenario + ": a removed Network/Disk heading remains in the header.");
        foreach ((bool shown, bool graph, string graphName, string first, string second, string firstWord, string secondWord,
            string firstKey, string secondKey) in new[]
        {
            (layout.NetValue, layout.NetGraph, "TransferSpeedGraph", "TransferSpeedLabel", "TransferUploadLabel",
                "TransferDownMark", "TransferUpMark", "stats.header.down", "stats.header.up"),
            (layout.DiskValue, layout.DiskGraph, "TransferDiskGraph", "TransferDiskReadLabel", "TransferDiskLabel",
                "TransferReadMark", "TransferWriteMark", "stats.header.read", "stats.header.write")
        })
        {
            if (!shown) continue;
            Require(HeaderWordIs(window, firstWord, firstKey) && HeaderWordIs(window, secondWord, secondKey),
                scenario + ": the direction labels are not localized regular caption words.");
            Rect a = Bounds(first), b = Bounds(second), wordA = Bounds(firstWord), wordB = Bounds(secondWord);
            foreach (string rate in new[] { first, second })
            {
                TextBlock value = window.FindControl<TextBlock>(rate)!;
                Require(value.FontSize == 11 && value.FontWeight == FontWeight.Normal
                    && value.TextAlignment == TextAlignment.Right && value.Bounds.Width >= 72,
                    scenario + ": the rate lost its caption size, regular weight or minimum shared width: " + rate);
            }
            if (layout.TwoLineLabels)
                Require(Near(b.Top - a.Bottom, 2) && Near(a.Right, b.Right) && Near(wordA.Left, wordB.Left),
                    $"{scenario}: stacked rates are not a two-line aligned matrix with 2 DIP spacing ({a}, {b}).");
            else
                Require(Near(a.Top, b.Top) && Near(wordB.Left - a.Right, 12),
                    scenario + ": the row matrix has no common baseline or its pair gap is not 12 DIP.");
            Require(Near(a.Left - wordA.Right, 6) && Near(b.Left - wordB.Right, 6),
                scenario + ": word/rate column gaps are not 6 DIP.");
            if (graph)
            {
                Rect plot = Bounds(graphName);
                Require(Near(wordA.Left - plot.Right, 8), scenario + ": graph/word gap is not 8 DIP.");
                Require(Near(plot.Width, layout.TwoLineLabels ? 120 : 128)
                    && Near(plot.Height, layout.TwoLineLabels ? 30 : 14),
                    scenario + ": graph dimensions do not match the word matrix.");
            }
        }
        if (layout.NetValue && layout.DiskValue)
        {
            if (layout.TwoLineLabels)
            {
                Require(Near(Bounds("TransferSpeedLabel").Top, Bounds("TransferDiskReadLabel").Top)
                    && Near(Bounds("TransferUploadLabel").Top, Bounds("TransferDiskLabel").Top),
                    scenario + ": the solid and dashed directions do not share baselines across blocks.");
                string nextBlock = layout.DiskGraph ? "TransferDiskGraph" : "TransferReadMark";
                Require(Near(Bounds(nextBlock).Left - Bounds("TransferSpeedLabel").Right, 24),
                    scenario + ": block gap is not 24 DIP.");
            }
            else
            {
                foreach ((string net, string disk) in new[] { ("TransferDownMark", "TransferReadMark"),
                    ("TransferSpeedLabel", "TransferDiskReadLabel"), ("TransferUpMark", "TransferWriteMark"),
                    ("TransferUploadLabel", "TransferDiskLabel") })
                    Require(Near(Bounds(net).Left, Bounds(disk).Left) && Near(Bounds(net).Right, Bounds(disk).Right),
                        scenario + ": compact rows do not share the four matrix columns: " + net + "/" + disk);
            }
        }
    }

    private static async Task ShowBehaviorAsync(SettingsEditor settings)
    {
        settings.OpenSectionNavigation();
        Require(settings.CanGoToSection("settings.behavior"), "The Behavior section is not available.");
        settings.GoToSection("settings.behavior");
        await Program.WaitUntilAsync(() => settings.FindControl<Control>("SettingsBehaviorPage")!.IsVisible);
    }

    private static async Task GoHeaderAsync(MainWindow window, string mode, bool disk, bool graph, string scenario)
    {
        await PresentationChecks.SwitchModeAsync(window, mode);
        if (mode != "Barebones") await EnsureWideAsync(window);
        await RequireHeaderAsync(window, disk, graph, scenario);
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
        bool disk = IoStatsDisplay.ResolveHeaderLayout(value, window.Bounds.Width).DiskValue;
        await Program.WaitUntilAsync(() => DiskRowShown(window) == disk, () => $"{value}: disk row {DiskRowShown(window)}");
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

    // Statistics is a segment of the Status page: the page shortcut opens Status, the segment is then chosen like a deep link does.
    private static async Task GoToStatisticsAsync(MainWindow window)
    {
        await GoToAsync(window, "navigation.status", MainWindow.WorkspacePage.Status);
        Require(window.ShowStatus(StatusSegment.Statistics), "The Status page does not switch to its statistics segment.");
        await Program.WaitUntilAsync(() => window.StatisticsSegmentShown, () => "segment " + window.StatusSegmentSelected);
        await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Background);
    }

    private static StatisticsPage Page(MainWindow window) => (StatisticsPage)window.StatisticsPageHost.Content!;

    private static async Task OpenPageAsync(MainWindow window, bool byGraphClick)
    {
        if (byGraphClick) Speed(window).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        else await GoToStatisticsAsync(window);
        await Program.WaitUntilAsync(() => window.StatisticsSegmentShown
            && window.StatisticsPageHost.Content is StatisticsPage
            && Page(window).Rows.Any(row => row.Key == Fixture) && Page(window).Graph.SampleCount >= 8,
            () => "page " + window.SelectedPage);
    }

    private static async Task PageAsync(MainWindow window, string variant)
    {
        // The shortcut opens the page and the footer button is selected.
        await OpenPageAsync(window, byGraphClick: false);
        Button footer = window.FindControl<Button>("StatusNavigationButton")!;
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
        // Reset sits on the bar in Advanced and under More in Barebones and Simple (work views).
        await CheckResetWorkViewFlowAsync(window, page);
        Require(page.ResetIsReachable && page.ResetButton.IsEnabled, "Reset is not available.");
        Require(!page.IsResetConfirmationOpen, "The reset question is open before Reset was pressed.");
        await Settle(window);
        Program.CaptureWindow(window, $"smoke-statistics-page-{variant}.png");
        // The question is never the default: it opens on Cancel and Cancel keeps everything.
        long before = Hub(window).Snapshot().Total.Session.NetDown;
        page.ResetButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Program.WaitUntilAsync(() => page.IsResetConfirmationOpen && page.CancelButton.IsFocused,
            () => $"open {page.IsResetConfirmationOpen}, cancel focused {page.CancelButton.IsFocused}");
        Require(page.CancelButton.IsFocused, "The reset question does not focus Cancel.");
        await Settle(window);
        Program.CaptureWindow(window, $"smoke-statistics-page-{variant}-confirm.png");
        page.CancelButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Program.WaitUntilAsync(() => !page.IsResetConfirmationOpen && page.ResetButton.IsEnabled);
        Require(Hub(window).Snapshot().Total.Session.NetDown >= before, "Cancel reset the counters.");
        Console.WriteLine("SMOKE statistics page: " + variant + "; rows " + page.Rows.Count + "; the reset question opens on Cancel.");
    }

    private static async Task CheckResetWorkViewFlowAsync(MainWindow window, StatisticsPage page)
    {
        ActionToolBar toolbar = page.FindControl<ActionToolBar>("StatisticsActions")!;
        var more = toolbar.More ?? throw new InvalidOperationException("Statistics More action has no template.");
        foreach ((string mode, bool direct) in new[] { ("Advanced", true), ("Simple", false), ("Barebones", false) })
        {
            await PresentationChecks.SwitchModeAsync(window, mode);
            if (mode != "Barebones") await EnsureWideAsync(window);
            if (!direct) more.IsChecked = false;
            await WaitForResetSurfaceAsync(window, page, toolbar, direct, mode);
            Require(page.ResetIsReachable && page.ResetButton.IsEnabled,
                mode + ": Reset is not reachable before opening its work-view surface.");

            if (!direct)
            {
                more.IsChecked = true;
                await Program.WaitUntilAsync(() => more.IsChecked == true
                    && ResetVisibleInOverflow(toolbar, page.ResetButton),
                    () => DescribeResetSurface(page, toolbar, mode + " after More"));
                Require(page.ResetIsReachable, mode + ": Reset became unreachable after opening More.");
            }

            SmokeActionClick.Execute(page.ResetButton);
            await Program.WaitUntilAsync(() => page.IsResetConfirmationOpen && page.CancelButton.IsFocused,
                () => mode + ": confirmation open " + page.IsResetConfirmationOpen + ", cancel focused " + page.CancelButton.IsFocused);
            Require(page.IsResetConfirmationOpen && page.CancelButton.IsFocused,
                mode + ": Reset did not open its confirmation from the visible action.");
            SmokeActionClick.Execute(page.CancelButton);
            await Program.WaitUntilAsync(() => !page.IsResetConfirmationOpen && page.ResetButton.IsEnabled,
                () => mode + ": confirmation open " + page.IsResetConfirmationOpen + ", reset enabled " + page.ResetButton.IsEnabled);
        }

        await PresentationChecks.SwitchModeAsync(window, "Advanced");
        await EnsureWideAsync(window);
        await WaitForResetSurfaceAsync(window, page, toolbar, direct: true, "Advanced restore");
    }

    private static bool ResetInDirectBar(ActionToolBar toolbar, Button reset) =>
        toolbar.DirectHost?.Children.Contains(reset) == true && ReferenceEquals(reset.Parent, toolbar.DirectHost);

    private static bool ResetInOverflow(ActionToolBar toolbar, Button reset) =>
        toolbar.OverflowHost?.Children.Contains(reset) == true && ReferenceEquals(reset.Parent, toolbar.OverflowHost);

    private static bool ResetVisibleInOverflow(ActionToolBar toolbar, Button reset) =>
        ResetInOverflow(toolbar, reset) && reset.IsEffectivelyVisible && reset.Bounds.Width > 0 && reset.Bounds.Height > 0
            && TopLevel.GetTopLevel(reset) is { IsVisible: true };

    private static async Task WaitForResetSurfaceAsync(MainWindow window, StatisticsPage page, ActionToolBar toolbar,
        bool direct, string mode)
    {
        await Program.WaitUntilAsync(() =>
        {
            window.UpdateLayout();
            bool visibleDirect = ResetInDirectBar(toolbar, page.ResetButton) && page.ResetButton.IsEffectivelyVisible
                && page.ResetButton.Bounds.Width > 0 && page.ResetButton.Bounds.Height > 0
                && TopLevel.GetTopLevel(page.ResetButton) is { IsVisible: true };
            bool moreVisible = toolbar.More?.IsEffectivelyVisible == true;
            return direct ? visibleDirect && !ResetInOverflow(toolbar, page.ResetButton)
                : !visibleDirect && ResetInOverflow(toolbar, page.ResetButton) && moreVisible && toolbar.More?.IsChecked != true;
        }, () => DescribeResetSurface(page, toolbar, mode));
    }

    private static string DescribeResetSurface(StatisticsPage page, ActionToolBar toolbar, string mode)
    {
        bool inDirectBar = ResetInDirectBar(toolbar, page.ResetButton);
        bool inOverflow = ResetInOverflow(toolbar, page.ResetButton);
        return mode + ": direct=" + inDirectBar + "; visible=" + page.ResetButton.IsEffectivelyVisible
            + "; bounds=" + page.ResetButton.Bounds + "; topLevel=" + (TopLevel.GetTopLevel(page.ResetButton)?.IsVisible ?? false)
            + "; inOverflow=" + inOverflow + "; moreVisible=" + toolbar.More?.IsEffectivelyVisible
            + "; moreChecked=" + toolbar.More?.IsChecked + "; reachable=" + page.ResetIsReachable;
    }

    private static async Task HeaderAndPageCapturesAsync(MainWindow window, string variant)
    {
        await GoToAsync(window, "navigation.downloads", MainWindow.WorkspacePage.Downloads);
        await Settle(window);
        Program.CaptureWindow(window, $"smoke-statistics-header-{variant}.png");
        if (variant == "light")
        {
            // The compact work views carry the labelled values too.
            foreach (string mode in new[] { "Simple", "Barebones" })
            {
                await PresentationChecks.SwitchModeAsync(window, mode);
                if (mode != "Barebones") await EnsureWideAsync(window);
                await Settle(window);
                Program.CaptureWindow(window, $"smoke-statistics-header-{variant}-{mode.ToLowerInvariant()}.png");
            }
            await PresentationChecks.SwitchModeAsync(window, "Advanced");
            await EnsureWideAsync(window);
            await GoToAsync(window, "navigation.downloads", MainWindow.WorkspacePage.Downloads);
        }
        await OpenPageAsync(window, byGraphClick: true);
        await Settle(window);
        Require(Page(window).Rows.Any(row => row.Key == Fixture) && Page(window).ResetIsReachable, "The page is incomplete in " + variant + ".");
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
        await GoToStatisticsAsync(window);
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
