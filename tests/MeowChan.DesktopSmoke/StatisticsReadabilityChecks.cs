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
                Program.CaptureWindow(host, $"smoke-statistics-readability-640-{code}-{theme}.png");

                Point before = page.Graph.TranslatePoint(default, host)!.Value;
                page.ResetButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Program.WaitUntilAsync(() => page.IsResetConfirmationOpen && page.CancelButton.IsFocused,
                    () => $"open {page.IsResetConfirmationOpen}, cancel focused {page.CancelButton.IsFocused}");
                Require(page.CancelButton.IsFocused, "Flyout did not open on the safe answer.");
                Require(before == page.Graph.TranslatePoint(default, host), "Opening the reset question pushed the graph.");
                Program.CaptureWindow(host, $"smoke-statistics-readability-confirm-{code}-{theme}.png");
                page.FindControl<Border>("ResetConfirmPanel")!.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape });
                Require(!page.IsResetConfirmationOpen && page.ResetButton.IsFocused, "Escape did not dismiss and restore focus.");
                page.ResetButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await SettleAsync();
                page.ConfirmButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Program.WaitUntilAsync(() => !page.IsResetConfirmationOpen && page.ResetButton.IsEnabled);
                await SettleAsync();
                Require(!page.Graph.HasTraffic && page.FindControl<TextBlock>("HistoryEmpty")!.IsVisible, "Reset did not show the zero-traffic state.");
                Require(!page.FindControl<TextBlock>("PeakText")!.IsVisible && !page.FindControl<TextBlock>("ScaleTop")!.IsVisible
                    && !page.FindControl<TextBlock>("ScaleHalf")!.IsVisible, "Reset still advertises a fictitious peak or scale.");
                Require(page.FindControl<Control>("ResetStatusPanel")!.IsVisible, "Reset result was not announced.");
                Require(before == page.Graph.TranslatePoint(default, host), "Reset result pushed the graph.");
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
        IoCounts counts = new(2_000_000, 3_000_000, 4_000_000, 123, 2);
        IoSourceStats source = new("Synthetic source", counts, counts, counts, new IoRates(131072, 65536, 32768, 1));
        return new IoStatsSnapshot
        {
            RetentionDays = 7, Rate = source.Rate, Sources = [source], Total = source,
            Series = new IoSeries(300, 4, [0, 65536, 98304, 131072], [0, 0, 32768, 65536], [0, 0, 0, 32768], [0, 1, 1, 1])
        };
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
