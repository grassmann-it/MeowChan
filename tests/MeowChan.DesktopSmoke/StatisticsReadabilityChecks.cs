using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.Stats;

namespace MeowChan.DesktopSmoke;

/// <summary>Deterministic statistics UI fixtures: no transport, settings writes, persistence or sampling timers.</summary>
internal static class StatisticsReadabilityChecks
{
    internal static async Task RunAsync(MainWindow owner)
    {
        string language = Texts.Language;
        using IoStatsHub hub = new(new EmptyCounters(), options: new IoStatsOptions
        {
            Persist = false, SampleIntervalMs = 2500, HistorySamples = 300
        });
        StatisticsPage page = new();
        page.Attach(hub, null);
        Window host = new() { Width = 640, Height = 760, Content = page, Title = "Synthetic statistics readability" };
        try
        {
            host.Show(owner);
            foreach ((string code, ThemeVariant theme) in new[] { ("en", ThemeVariant.Light), ("de", ThemeVariant.Dark) })
            {
                host.RequestedThemeVariant = theme;
                Texts.SetLanguage(code);
                page.Apply(Traffic());
                await SettleAsync();
                Require(page.FindControl<TextBlock>("HistoryStart")!.Text == "−750 s", "The default 300 × 2500 ms axis is not 750 seconds.");
                Require(page.FindControl<TextBlock>("HistoryEnd")!.Text == Texts.T("stats.now"), "The newest tick is not localized.");
                Require(page.FindControl<Control>("CompactSources")!.IsVisible && !page.RowsGrid.IsVisible, "640 dip did not select compact rows.");
                Expander first = page.GetVisualDescendants().OfType<Expander>().First();
                first.IsExpanded = true;
                await SettleAsync();
                Require(first.GetVisualDescendants().OfType<TextBlock>().Any(text => text.Text?.Contains("123") == true), "Expanded requests disappeared.");
                Grid header = (Grid)first.Header!;
                Require(header.Bounds.Width <= host.ClientSize.Width && header.Bounds.Width > 0, "Compact row extends past the viewport.");
                foreach (TextBlock number in header.Children.OfType<TextBlock>().Skip(1))
                    Require(number.Bounds.Width >= number.DesiredSize.Width - 1, "A compact counter is clipped: " + number.Text);
                Require(page.FindControl<TextBlock>("ScaleTop")!.Text == "128 KiB/s" && page.FindControl<TextBlock>("ScaleHalf")!.Text == "64 KiB/s", "Grid guides do not match the graph scale.");
                Control peakSlot = page.FindControl<Control>("PeakSlot")!;
                Require(peakSlot.Bounds.Width >= 103, $"Peak column was not measured in the traffic state: {peakSlot.Bounds}.");
                Program.CaptureWindow(host, $"smoke-statistics-readability-640-{code}-{theme}.png");

                Point? before = page.Graph.TranslatePoint(default, host);
                Rect beforeGraph = page.Graph.Bounds;
                Vector beforeScroll = page.FindControl<ScrollViewer>("StatisticsScroll")!.Offset;
                string beforeLayout = LayoutDetails(page, host);
                page.ResetButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Program.WaitUntilAsync(() => page.IsResetConfirmationOpen && page.CancelButton.IsFocused,
                    () => $"open {page.IsResetConfirmationOpen}, cancel focused {page.CancelButton.IsFocused}");
                Require(page.CancelButton.IsFocused, "Flyout did not open on the safe answer.");
                RequireStableGraphLayout("reset question", page, host, before, beforeGraph, beforeScroll, beforeLayout);
                Program.CaptureWindow(host, $"smoke-statistics-readability-confirm-{code}-{theme}.png");
                page.FindControl<Border>("ResetConfirmPanel")!.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape });
                Require(!page.IsResetConfirmationOpen && page.ResetButton.IsFocused, "Escape did not dismiss and restore focus.");
                page.ResetButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await SettleAsync();
                page.ConfirmButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Program.WaitUntilAsync(() => !page.IsResetConfirmationOpen && page.ResetButton.IsEnabled);
                await SettleAsync();
                Require(!page.Graph.HasTraffic && page.FindControl<TextBlock>("HistoryEmpty")!.IsVisible, "Reset did not show the zero-traffic state.");
                TextBlock peak = page.FindControl<TextBlock>("PeakText")!;
                Require(!peak.IsVisible && string.IsNullOrEmpty(peak.Text) && !page.FindControl<TextBlock>("ScaleTop")!.IsVisible
                    && !page.FindControl<TextBlock>("ScaleHalf")!.IsVisible, "Reset still advertises a fictitious peak or scale.");
                Require(page.FindControl<Control>("ResetStatusPanel")!.IsVisible, "Reset result was not announced.");
                Require(page.FindControl<Control>("PeakSlot")!.Bounds.Width >= 103,
                    $"Zero-traffic peak column collapsed: {page.FindControl<Control>("PeakSlot")!.Bounds}.");
                RequireStableGraphLayout("reset result", page, host, before, beforeGraph, beforeScroll, beforeLayout);
                Program.CaptureWindow(host, $"smoke-statistics-readability-reset-{code}-{theme}.png");
            }
            host.Width = 1100;
            page.Apply(Traffic());
            await SettleAsync();
            Require(page.RowsGrid.IsVisible && !page.FindControl<Control>("CompactSources")!.IsVisible, "Wide layout did not restore all nine columns.");
            Require(page.RowsGrid.Columns.Count == 9 && page.Rows.Count == 2 && page.Rows[^1].IsTotal, "A counter or the total row was lost.");
            Program.CaptureWindow(host, "smoke-statistics-readability-wide.png");
            hub.Configure(hub.Options with { SampleIntervalMs = 500, HistorySamples = 120 });
            page.Apply(hub.Snapshot());
            Require(page.FindControl<TextBlock>("HistoryStart")!.Text == "−60 s", "Axis did not follow live options.");
            Require(hub.ActiveTimerCount == 0, "Page created an unexpected sampling or persistence timer.");
            Console.WriteLine("SMOKE statistics readability: deterministic 750/60 s axes; labeled scale; EN/DE 640 dip expandable counters; wide table; Cancel/Escape/reset without layout shift; zero traffic PASS.");
        }
        finally
        {
            page.EndLifetime();
            host.Close();
            Texts.SetLanguage(language);
        }
    }

    private static IoStatsSnapshot Traffic()
    {
        IoCounts counts = new(NetDown: 2_000_000, NetUp: 0, DiskWritten: 3_000_000, DiskRead: 4_000_000, Requests: 123, Errors: 2);
        IoSourceStats source = new("Synthetic source", counts, counts, counts, new IoRates(NetDown: 131072, NetUp: 0, DiskWrite: 65536, DiskRead: 32768, Requests: 1));
        return new IoStatsSnapshot
        {
            RetentionDays = 7, Rate = source.Rate, Sources = [source], Total = source,
            Series = new IoSeries(capacity: 300, count: 4, netDown: [0, 65536, 98304, 131072], netUp: [0, 0, 0, 0], diskWrite: [0, 0, 32768, 65536], diskRead: [0, 0, 0, 32768], requests: [0, 1, 1, 1])
        };
    }

    private static void RequireStableGraphLayout(string stage, StatisticsPage page, Window host, Point? beforeOrigin,
        Rect beforeGraph, Vector beforeScroll, string beforeLayout)
    {
        Point? afterOrigin = page.Graph.TranslatePoint(default, host);
        Rect afterGraph = page.Graph.Bounds;
        Vector afterScroll = page.FindControl<ScrollViewer>("StatisticsScroll")!.Offset;
        string afterLayout = LayoutDetails(page, host);
        Console.WriteLine($"SMOKE statistics readability bounds: {stage}; before={beforeLayout}; after={afterLayout}");
        Require(beforeOrigin == afterOrigin,
            $"{stage} pushed the graph: beforeOrigin={beforeOrigin}; afterOrigin={afterOrigin}; before={beforeLayout}; after={afterLayout}");
        Require(beforeGraph.Size == afterGraph.Size,
            $"{stage} resized the graph: before={beforeGraph}; after={afterGraph}; before={beforeLayout}; after={afterLayout}");
        Require(beforeScroll == afterScroll,
            $"{stage} changed the statistics scroll offset: before={beforeScroll}; after={afterScroll}; before={beforeLayout}; after={afterLayout}");
    }

    private static string LayoutDetails(StatisticsPage page, Window host)
    {
        TextBlock title = page.FindControl<TextBlock>("HistoryTitle")!;
        TextBlock note = page.FindControl<TextBlock>("HistoryNote")!;
        Control peakSlot = page.FindControl<Control>("PeakSlot")!;
        TextBlock peak = page.FindControl<TextBlock>("PeakText")!;
        ScrollViewer scroll = page.FindControl<ScrollViewer>("StatisticsScroll")!;
        return $"origin={page.Graph.TranslatePoint(default, host)}; graph={page.Graph.Bounds}; scroll={scroll.Offset}; "
            + $"title={title.Bounds}; note={note.Bounds}; peakSlot={peakSlot.Bounds}; peakVisible={peak.IsVisible}; "
            + $"peakText=<{peak.Text}>; traffic={page.Graph.HasTraffic}; trafficPeak={page.Graph.TrafficPeak}; rows={page.Rows.Count}";
    }

    private static async Task SettleAsync()
    {
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        await Task.Delay(100);
    }

    private static void Require(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }

    private sealed class EmptyCounters : IIoCounterSource
    {
        public void Read(List<IoCounterReading> into) { }
    }
}
