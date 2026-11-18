using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Desktop;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using Ursa.Controls;

namespace MeowChan.DesktopSmoke;

// The stat footer of the Sources list card, with the real control in a real window: no wrapping, the same segment places for every
// value length (0 / 1 / 10 / 1000 / 12345), labels dropped before counts, the narrowest card keeps Threads, and the accessible names and
// tooltips carry the exact numbers. Plus the real Sources page: its card shows the footer instead of the old text line.
internal static class StatFooterChecks
{
    private static readonly long[][] ValueSets =
    [
        [0, 0, 0, 0], [1, 1, 1, 1], [10, 10, 10, 10], [73, 0, 0, 6], [1000, 1000, 1000, 1000], [12_345, 123_456, 9999, 1_234_567]
    ];

    private sealed record Placement(StatFooterMode Mode, int Visible, double Height, string Segments);

    internal static async Task RunAsync(MainWindow owner)
    {
        ThemeVariant? originalTheme = Application.Current!.RequestedThemeVariant;
        try
        {
            foreach (ThemeVariant theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
            {
                Application.Current.RequestedThemeVariant = theme;
                foreach (string language in new[] { "en", "de", "fr", "ru" })
                {
                    Texts.SetLanguage(language);
                    await CheckRowAsync(owner, language, theme == ThemeVariant.Light ? "light" : "dark");
                }
            }
            Texts.SetLanguage("en");
            Application.Current.RequestedThemeVariant = originalTheme;
            await CheckSourcesPageAsync(owner);
        }
        finally { Application.Current!.RequestedThemeVariant = originalTheme; Texts.SetLanguage("en"); }
        Console.WriteLine("SMOKE stat footer: no wrap, same segment places for 0/1/10/1000/12345, labels before counts, Threads kept, exact tooltips and names in en/de/fr/ru and both themes; real Sources card shows the footer.");
    }

    private static IReadOnlyList<StatFooterItem> Items(long[] values) =>
    [
        new("threads", "message-square", Texts.T("sources.stat.threads"), values[0], 0),
        new("boards", "grid", Texts.T("sources.stat.boards"), values[1], 3),
        new("queued", "list", Texts.T("sources.stat.queued"), values[2], 2),
        new("active", "download", Texts.T("sources.stat.active"), values[3], 1)
    ];

    private static async Task CheckRowAsync(MainWindow owner, string language, string theme)
    {
        // 280 is the Sources list column minus the card inset (256); the others show the steps down to a single segment.
        double[] widths = [360, 280, 256, 232, 200, 160, 120, 90, 40];
        StatFooterMode? previousMode = null;
        int previousVisible = int.MaxValue;
        foreach (double width in widths)
        {
            StatFooterRow row = new();
            Border host = new() { Width = width, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top, Child = row, Margin = new Thickness(16) };
            Window fixture = new() { Width = 480, Height = 180, Content = host, Title = "stat-footer-" + language + "-" + theme + "-" + width };
            fixture.Show(owner);
            try
            {
                row.Update(Items(ValueSets[0]));
                await Program.WaitUntilAsync(() => row.Bounds.Width > 0 && row.Segments.Count == 4 && fixture.IsEffectivelyVisible);
                Placement baseline = await Measure(fixture, row);
                foreach (long[] values in ValueSets)
                {
                    row.Update(Items(values));
                    Placement now = await Measure(fixture, row);
                    Require(now == baseline, $"{language}/{theme}/{width}: the row moved when the numbers changed to {string.Join(",", values)}: {baseline} vs {now}.");
                    foreach (StatSegmentView segment in row.Segments.Where(segment => segment.IsVisible))
                    {
                        TextBlock value = segment.GetVisualDescendants().OfType<TextBlock>().First(text => text.Classes.Contains("stat-value") && text.Opacity > 0);
                        Require(value.TextLayout.TextLines.Count == 1 && value.Text == segment.ValueText,
                            $"{language}/{theme}/{width}: the value of {segment.Id} wrapped or was cut: '{value.Text}'.");
                        TextBlock label = segment.GetVisualDescendants().OfType<TextBlock>().First(text => text.Classes.Contains("stat-label"));
                        Require(!segment.LabelShown || label.TextLayout.TextLines.Count == 1,
                            $"{language}/{theme}/{width}: the label of {segment.Id} wrapped.");
                        Require(segment.Bounds.Right <= row.Bounds.Width + 0.01, $"{language}/{theme}/{width}: {segment.Id} sticks out of the row: {baseline}; row {row.Bounds}; panel {row.Child?.Bounds}; segment {segment.Bounds}.");
                    }
                }
                // Mode and visible count are monotone in the width: labels go first, then the least important counts.
                Require(previousMode == null || !(previousMode == StatFooterMode.IconsOnly && baseline.Mode == StatFooterMode.Full),
                    $"{language}/{theme}/{width}: labels came back at a narrower width.");
                Require(baseline.Visible <= previousVisible, $"{language}/{theme}/{width}: more segments at a narrower width.");
                Require(baseline.Visible >= 1 && row.Segments.Single(segment => segment.Id == "threads").IsVisible,
                    $"{language}/{theme}/{width}: Threads, the most important count, is not shown.");
                if (width >= 360) Require(baseline.Mode == StatFooterMode.Full && baseline.Visible == 4, $"{language}/{theme}: the wide card lost its labels or a count.");
                if (width <= 90) Require(baseline.Mode == StatFooterMode.IconsOnly && baseline.Visible < 4, $"{language}/{theme}: the narrow card kept all four counts.");
                previousMode = baseline.Mode; previousVisible = baseline.Visible;

                row.Update(Items([73, 0, 0, 6]));
                await Measure(fixture, row);
                StatSegmentView threads = row.Segments.Single(segment => segment.Id == "threads");
                Require(AutomationProperties.GetName(threads) == Texts.T("stats.footer.tip", Texts.T("sources.stat.threads"), "73")
                    && AutomationProperties.GetHelpText(threads) == AutomationProperties.GetName(threads),
                    $"{language}: the accessible name of Threads is '{AutomationProperties.GetName(threads)}'.");
                Require(ToolTip.GetTip(threads) as string == AutomationProperties.GetName(threads) && threads.Background != null && threads.IsHitTestVisible,
                    $"{language}: the tooltip of Threads is missing or the slot cannot be hovered.");
                row.Update(Items([12_345, 0, 0, 1_234_567]));
                await Measure(fixture, row);
                StatSegmentView active = row.Segments.Single(segment => segment.Id == "active");
                Require(threads.ValueText == "12" + Texts.Culture.NumberFormat.NumberDecimalSeparator + "3k",
                    $"{language}: 12345 shows as '{threads.ValueText}'.");
                Require((AutomationProperties.GetName(active) ?? string.Empty).EndsWith((1_234_567L).ToString("N0", Texts.Culture), StringComparison.Ordinal),
                    $"{language}: the exact number is missing from '{AutomationProperties.GetName(active)}'.");
                if (language == "en" && (width == 280 || width == 360 || width == 90))
                    Program.CaptureWindow(fixture, $"smoke-stat-footer-{theme}-{width}.png");
            }
            finally { fixture.Close(); }
        }
    }

    private static async Task<Placement> Measure(Window fixture, StatFooterRow row)
    {
        await Dispatcher.UIThread.InvokeAsync(() => { fixture.UpdateLayout(); }, DispatcherPriority.Render);
        fixture.UpdateLayout();
        string segments = string.Join(";", row.Segments.Select(segment => segment.IsVisible
            ? $"{segment.Id}@{segment.Bounds.X:0.#}+{segment.Bounds.Width:0.#}" : segment.Id + ":hidden"));
        return new Placement(row.Mode, row.VisibleCount, Math.Round(row.Bounds.Height, 1), segments);
    }

    private static async Task CheckSourcesPageAsync(MainWindow window)
    {
        SmokeNavigation.GoTo(window, MainWindow.WorkspacePage.Sources);
        ContentControl host = window.FindControl<ContentControl>("SourcesPageHost")!;
        object? previous = host.Content;
        SourcesPage page = new(_ => false, _ => { }, _ => { }, () => { }, () => { });
        host.Content = page;
        try
        {
            DesktopThreadEntry thread = SnapshotFixtures.Thread(993, "321", "Footer fixture", queued: 3, active: 12);
            DesktopSessionView view = new([thread], [SnapshotFixtures.Board(994)], [], DesktopSessionState.Ready, null);
            ProviderDescriptor[] providers =
            [
                new("4chan", "4chan", new Uri("https://boards.4chan.org/"), ProviderCapabilities.ThreadWatch | ProviderCapabilities.BoardCatalog,
                    Array.AsReadOnly(new[] { "boards.4chan.org" }), null, true),
                new("idle-source", "Idle fixture", new Uri("https://idle.invalid/"), ProviderCapabilities.ThreadWatch,
                    Array.AsReadOnly(new[] { "idle.invalid" }), null, false)
            ];
            page.Refresh(providers, view, true);
            await Program.WaitUntilAsync(() => page.FindControl<SelectionList>("SourceCards")!.Items.Count >= 2);
            SelectionListItem[] cards = page.FindControl<SelectionList>("SourceCards")!.Items.OfType<SelectionListItem>().ToArray();
            StackPanel Labels(string name) => (StackPanel)cards.Single(card => card.Content is StackPanel labels
                && labels.Children.OfType<TextBlock>().First().Text == name).Content!;
            StackPanel watched = Labels("4chan"), idle = Labels("Idle fixture");
            StatFooterRow footer = watched.Children.OfType<StatFooterRow>().Single();
            Require(footer.IsVisible && footer.Segments.Select(segment => segment.Id).SequenceEqual(["threads", "boards", "queued", "active"]),
                "The watched source's card does not show the four footer segments.");
            Require(footer.Segments.Select(segment => segment.ValueText).SequenceEqual(["1", "1", "3", "12"]),
                "The footer values are not the card's counts: " + string.Join(",", footer.Segments.Select(segment => segment.ValueText)));
            Require(!watched.Children.OfType<TextBlock>().Last().IsVisible, "The old text line is still shown beside the footer.");
            StatFooterRow idleFooter = idle.Children.OfType<StatFooterRow>().Single();
            Require(!idleFooter.IsVisible && idle.Children.OfType<TextBlock>().Last().IsVisible
                && idle.Children.OfType<TextBlock>().Last().Text == Texts.T("sources.noObservation"),
                "A source without watches lost its sentence or shows a footer.");
            await Program.WaitUntilAsync(() => footer.Bounds.Width > 0);
            Program.CaptureWindow(window, "smoke-stat-footer-sources-page.png");
        }
        finally { host.Content = previous; }
    }

    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
