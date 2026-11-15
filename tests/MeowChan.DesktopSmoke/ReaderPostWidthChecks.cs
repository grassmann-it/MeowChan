using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Interfaces;

namespace MeowChan.DesktopSmoke;

// Reader post width: "fit to content" (default) makes a card as wide as its header, text and media need, left-aligned and
// capped at the reading width; "full width" keeps every card across the list. The check reads the real reader in the real
// window at 1440 and 1920, in both themes and both settings, switches live, and measures scrolling in both modes.
internal static class ReaderPostWidthChecks
{
    private static readonly ResourceId Fixture = new("4chan", "g", "9997");
    private static readonly ResourceId Scrolling = new("4chan", "g", "9996");
    private const string Sentence = "Saved posts keep the width their content needs, up to the reading width.";

    internal static async Task SeedAsync(ArchiveCatalog catalog, string media)
    {
        CapturedPost Post(string id, int ordinal, string html, bool withMedia = false, string? subject = null) => new(id, ordinal,
            PostFieldPresence.All, PostedAt: new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero).AddMinutes(ordinal * 7),
            Subject: subject, Author: "Anonymous", Html: html,
            Attachments: withMedia ? new[] { new CapturedAttachment(0, "shared", OriginalFileName: "reader.png") } : Array.Empty<CapturedAttachment>());
        string Text(int sentences) => "<p>" + string.Join(" ", Enumerable.Repeat(Sentence, sentences)) + "</p>";
        CapturedPost[] posts =
        [
            Post("9000", 0, Text(6), withMedia: true, subject: "Fit to content"),
            Post("9001", 1, "<p>Sexy</p>"),
            Post("9002", 2, "<a href='#p9000'>&gt;&gt;9000</a><br>lol"),
            Post("9003", 3, "<p>One sentence of ordinary length, about as long as a typical imageboard reply.</p>"),
            Post("9004", 4, Text(14)),
            Post("9005", 5, "<p>Nice picture</p>", withMedia: true),
            Post("9006", 6, Text(10), withMedia: true),
            Post("9007", 7, "<p>&gt;greentext line<br>and a short answer</p>")
        ];
        posts = await StrictMediaFixture.BindAsync(media, Fixture, posts);
        await catalog.CaptureAsync(new CapturedThreadSnapshot(Fixture, "https://boards.4chan.org/g/thread/9997", "Post width fixture",
            ThreadSourceState.Archived, DateTimeOffset.UtcNow, media, posts));
        // A longer thread for the scroll measurement: short, medium, long and media posts in turn (beyond one viewport).
        CapturedPost[] many = Enumerable.Range(0, 120).Select(ordinal => (ordinal % 5) switch
        {
            0 => Post((8000 + ordinal).ToString(), ordinal, "<p>Short " + ordinal + "</p>"),
            1 => Post((8000 + ordinal).ToString(), ordinal, "<p>A medium reply, one line of ordinary length " + ordinal + ".</p>"),
            2 => Post((8000 + ordinal).ToString(), ordinal, Text(1 + ordinal * 7 % 12)),
            3 => Post((8000 + ordinal).ToString(), ordinal, "<p>Nice " + ordinal + "</p>", withMedia: true),
            _ => Post((8000 + ordinal).ToString(), ordinal, Text(1 + ordinal * 5 % 9), withMedia: true)
        }).ToArray();
        many = await StrictMediaFixture.BindAsync(media, Scrolling, many);
        await catalog.CaptureAsync(new CapturedThreadSnapshot(Scrolling, "https://boards.4chan.org/g/thread/9996", "Post width scroll",
            ThreadSourceState.Archived, DateTimeOffset.UtcNow, media, many));
    }

    internal static async Task RunAsync(MainWindow window)
    {
        double width = window.Width, height = window.Height;
        PixelPoint position = window.Position;
        ThemeVariant? theme = Application.Current!.RequestedThemeVariant;
        Exception? failureBeforeCleanup = null;
        try { await RunCoreAsync(window); }
        catch (Exception thrown) { failureBeforeCleanup = thrown; throw; }
        finally
        {
            await SmokeCleanup.RunAsync(failureBeforeCleanup, async () =>
            {
                Application.Current!.RequestedThemeVariant = theme;
                window.ArchivePage.ReaderPostWidthPreference = window.SessionHost.Config.ReaderPostWidth;
                window.Width = width; window.Height = height;
                window.Position = position;
                await Program.WaitUntilAsync(() => PresentationChecks.MatchesRequestedSize(window, width, height));
                await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Background);
            }, exception => Console.Error.WriteLine("SMOKE cleanup failed in ReaderPostWidthChecks: " + exception));
        }
    }

    private static async Task RunCoreAsync(MainWindow window)
    {
        window.Width = 1440; window.Height = 920;
        await Program.WaitUntilAsync(() => PresentationChecks.MatchesRequestedSize(window, 1440, 920));
        SmokeNavigation.GoTo(window, MainWindow.WorkspacePage.Archive);
        ArchivePage page = window.ArchivePage;
        await Program.WaitUntilAsync(() => !page.IsBusy);
        page.FindControl<TextBox>("ArchiveSearchText")!.Text = "Post width fixture";
        await page.ApplySearchAsync();
        ListBox threads = page.FindControl<ListBox>("ArchiveThreads")!;
        threads.SelectedIndex = 0;
        ContentControl host = window.FindControl<ContentControl>("ReaderHost")!;
        await Program.WaitUntilAsync(() => !page.IsBusy && page.CurrentThread?.Summary.Id == Fixture);
        Click(page.FindControl<Button>("ArchiveReadPosts")!);
        await Program.WaitUntilAsync(() => host.IsVisible && !page.IsBusy && page.CurrentThread?.Summary.Id == Fixture);
        await page.MediaLoadTask;
        ListBox list = page.FindControl<ListBox>("ArchivePosts")!;
        Require(list.Classes.Contains("reader-fit-width") && page.ReaderPostWidthPreference == Config.ReaderPostWidthFit,
            "The reader did not start with post width 'fit to content'.");
        Require(window.SessionHost.Config.ReaderPostWidth == Config.ReaderPostWidthFit, "The default setting is not 'fit to content'.");

        foreach (double windowWidth in new[] { 1440d, 1920d })
        {
            window.Width = windowWidth; window.Height = 920;
            await Program.WaitUntilAsync(() => PresentationChecks.MatchesRequestedSize(window, windowWidth, 920));
            foreach (ThemeVariant variant in new[] { ThemeVariant.Dark, ThemeVariant.Light })
            {
                Application.Current!.RequestedThemeVariant = variant;
                foreach (string mode in new[] { Config.ReaderPostWidthFit, Config.ReaderPostWidthFull, Config.ReaderPostWidthColumn })
                {
                    page.ReaderPostWidthPreference = mode;
                    await Flush(window);
                    string key = $"{mode.ToLowerInvariant()}-{windowWidth:0}-{variant.Key.ToString()!.ToLowerInvariant()}";
                    await ShowAsync(window, list, "9000");
                    list.GetVisualDescendants().OfType<ScrollViewer>().First().Offset = default;
                    await Flush(window);
                    CheckCards(list, mode, windowWidth, new[] { "9000", "9001", "9002", "9003" }, key);
                    await WaitForThumbnailsAsync(list);
                    Program.CaptureWindow(window, $"smoke-reader-postwidth-{key}-short.png");
                    await ShowAsync(window, list, "9006");
                    await ShowAsync(window, list, "9004");
                    await ShowAsync(window, list, "9005");
                    CheckCards(list, mode, windowWidth, new[] { "9004", "9005", "9006" }, key);
                    ScrollTo(list, "9004");
                    await Flush(window);
                    await WaitForThumbnailsAsync(list);
                    Program.CaptureWindow(window, $"smoke-reader-postwidth-{key}-long-media.png");
                }
            }
        }

        // The preference switches the open reader at once, in both directions, without a reload.
        window.Width = 1440; window.Height = 920;
        await Program.WaitUntilAsync(() => PresentationChecks.MatchesRequestedSize(window, 1440, 920));
        await ShowAsync(window, list, "9001");
        page.ReaderPostWidthPreference = Config.ReaderPostWidthFit;
        await Flush(window);
        double fitWidth = Realized(list, "9001")!.Bounds.Width;
        page.ReaderPostWidthPreference = Config.ReaderPostWidthFull;
        await Flush(window);
        double fullWidth = Realized(list, "9001")!.Bounds.Width;
        page.ReaderPostWidthPreference = Config.ReaderPostWidthFit;
        await Flush(window);
        Require(fitWidth < fullWidth - 300 && Math.Abs(Realized(list, "9001")!.Bounds.Width - fitWidth) < 0.5,
            $"Live switching did not follow the preference: fit {fitWidth}, full {fullWidth}.");
        Console.WriteLine($"SMOKE reader post width: short reply {fitWidth:0} dip fit / {fullWidth:0} dip full, cards capped at {ReaderNavigationLayout.CardMaxWidth}, live switch, left alignment, 1440/1920, dark/light passed.");

        await CheckReadingLineAsync(window, page, list);
        await CheckScrollCostAsync(window, page, host);
    }

    // The reading mark is a row of its own between two cards (9003fa35): in both modes it runs across the list width (the cards'
    // 8-dip gutters on both sides), 8 dip above the marked card, however narrow that card is, and never inside it.
    private static async Task CheckReadingLineAsync(MainWindow window, ArchivePage page, ListBox list)
    {
        Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
        await page.ResumeReadingPositionAsync(new LocalReadingPosition("4chan", "g", "9997", "9000", 0, null));
        await Program.WaitUntilAsync(() => !page.IsBusy && page.UnreadFirstNewPostId == "9001", () => "no reading mark: " + page.UnreadFirstNewPostId);
        string label = MeowChan.Desktop.Localization.Texts.Count("reader.unreadLine", 7);
        foreach (string mode in new[] { Config.ReaderPostWidthFit, Config.ReaderPostWidthFull })
        {
            page.ReaderPostWidthPreference = mode;
            await ShowAsync(window, list, "9001");
            list.GetVisualDescendants().OfType<ScrollViewer>().First().Offset = default;
            await Flush(window);
            string geometry = "";
            // The line host places its line from the settled layout, one pass after the card moved.
            await Program.WaitUntilAsync(() =>
            {
                window.UpdateLayout();
                if (Realized(list, "9001") is not { } item) { geometry = "9001 not realized"; return false; }
                if (item.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault(text => text.IsEffectivelyVisible && text.Text == label) is not { } labelText)
                { geometry = "no visible label " + label; return false; }
                Grid divider = labelText.GetVisualAncestors().OfType<Grid>().First();
                Control viewport = divider.FindAncestorOfType<ScrollContentPresenter>()!;
                Point start = divider.TranslatePoint(default, viewport)!.Value;
                Point card = item.TranslatePoint(default, viewport)!.Value;
                geometry = $"x={start.X}, width={divider.Bounds.Width}, list={viewport.Bounds.Width}, line bottom={start.Y + divider.Bounds.Height}, card top={card.Y}, card width={item.Bounds.Width}";
                return Math.Abs(start.X - 8) < 1 && Math.Abs(divider.Bounds.Width - (viewport.Bounds.Width - 16)) < 1
                    && divider.Bounds.Width >= labelText.Bounds.Width + 30
                    && Math.Abs(card.Y - (start.Y + divider.Bounds.Height) - 8) < 1;
            }, () => $"The reading line did not run across the list 8 dip above its card in mode {mode}: {geometry}.");
            if (mode == Config.ReaderPostWidthFit)
                Require(Realized(list, "9001")!.Bounds.Width < list.Bounds.Width - 100, "The fit-width card is not narrower than the line: " + geometry);
            await WaitForThumbnailsAsync(list);
            Program.CaptureWindow(window, $"smoke-reader-postwidth-{mode.ToLowerInvariant()}-1440-dark-reading-line.png");
        }
        page.ReaderPostWidthPreference = Config.ReaderPostWidthFit;
    }

    private static void CheckCards(ListBox list, string mode, double windowWidth, string[] ids, string key)
    {
        double listWidth = list.Bounds.Width;
        string context = $" [{key}; list {listWidth:0}; " + string.Join(", ", ids.Select(id => id + "=" + Describe(list, id))) + "]";
        double? left = null;
        foreach (string id in ids)
        {
            ListBoxItem item = Realized(list, id) ?? throw new InvalidOperationException("Post " + id + " was not realized." + context);
            double x = item.TranslatePoint(default, list)!.Value.X, w = item.Bounds.Width;
            left ??= x;
            Require(Math.Abs(x - left.Value) < 0.5, "Cards were not left-aligned." + context);
            ReaderBodyTextBlock body = item.GetVisualDescendants().OfType<ReaderBodyTextBlock>().First();
            Point bodyTopLeft = body.TranslatePoint(default, item)!.Value;
            Require(bodyTopLeft.X + body.Bounds.Width <= w + 0.5, "The post text overflowed its card." + context);
            if (mode == Config.ReaderPostWidthFull)
                Require(w >= listWidth - 40 && x + w <= listWidth + 0.5, $"Full width did not use the whole list: card {w}." + context);
            else
                Require(w <= ReaderNavigationLayout.CardMaxWidth + 0.5, $"A card was wider than the reading width: {w}." + context);
        }
        if (mode == Config.ReaderPostWidthColumn)
        {
            // Every card is as wide as the reading width allows, and the column is centred in the list.
            double expected = Math.Min(ReaderNavigationLayout.CardMaxWidth, listWidth - 16);
            foreach (string id in ids)
            {
                ListBoxItem item = Realized(list, id)!;
                double x = item.TranslatePoint(default, list)!.Value.X;
                Require(Math.Abs(item.Bounds.Width - expected) < 20 && x >= 0 && Math.Abs(x - (listWidth - item.Bounds.Width) / 2) < 14,
                    $"The centred column was not full reading width and centred: card {item.Bounds.Width}, x {x}." + context);
            }
            return;
        }
        if (mode != Config.ReaderPostWidthFit) return;
        double Width(string id) => Realized(list, id)?.Bounds.Width ?? double.NaN;
        if (ids.Contains("9001"))
            Require(Width("9001") < Math.Min(520, listWidth * 0.6) && Width("9002") < Width("9003") && Width("9003") < Width("9001") + 600,
                "Short replies did not shrink to their content." + context);
        if (ids.Contains("9004"))
        {
            Require(Width("9004") >= 600 && Width("9004") <= ReaderNavigationLayout.CardMaxWidth + 0.5, "A long text did not wrap at its reading width." + context);
            Require(Width("9006") > Width("9004") + 150 && Width("9005") > 300, "Media cards did not widen for the picture column." + context);
            if (listWidth > ReaderNavigationLayout.CardMaxWidth + 80)
                Require(Width("9006") >= 900, "The media card did not use its room up to the cap." + context);
        }
    }

    private static string Describe(ListBox list, string id) => Realized(list, id) is { } item
        ? $"{item.TranslatePoint(default, list)!.Value.X:0}+{item.Bounds.Width:0}" : "n/a";

    private static ListBoxItem? Realized(ListBox list, string id) => list.GetVisualDescendants().OfType<ListBoxItem>()
        .FirstOrDefault(item => (item.DataContext as ArchivePostRow)?.Id == id);

    private static async Task ShowAsync(Window window, ListBox list, string id)
    {
        ArchivePostRow row = list.ItemsSource!.Cast<ArchivePostRow>().Single(candidate => candidate.Id == id);
        list.ScrollIntoView(row);
        await Program.WaitUntilAsync(() => Realized(list, id) != null);
        await Flush(window);
    }

    private static void ScrollTo(ListBox list, string id)
    {
        ListBoxItem item = Realized(list, id)!;
        ScrollViewer scroll = list.GetVisualDescendants().OfType<ScrollViewer>().First();
        scroll.Offset = new Vector(0, Math.Max(0, scroll.Offset.Y + item.TranslatePoint(default, scroll)!.Value.Y - 8));
    }

    // Scroll the same thread through its first page in small steps in each mode: the number of post layouts that were
    // measured and the time per step must not grow with the fit-to-content mode.
    private static async Task CheckScrollCostAsync(MainWindow window, ArchivePage page, ContentControl host)
    {
        window.Width = 1440; window.Height = 920;
        await Program.WaitUntilAsync(() => PresentationChecks.MatchesRequestedSize(window, 1440, 920));
        await page.LoadThreadAsync(Scrolling);
        await Program.WaitUntilAsync(() => !page.IsBusy && page.CurrentThread?.Summary.Id == Scrolling && host.IsVisible);
        await page.MediaLoadTask;
        ListBox list = page.FindControl<ListBox>("ArchivePosts")!;
        ScrollViewer scroll = list.GetVisualDescendants().OfType<ScrollViewer>().First();
        var results = new Dictionary<string, (long Measures, double MeanMs, double P95Ms)>();
        foreach (string mode in new[] { Config.ReaderPostWidthFull, Config.ReaderPostWidthFit, Config.ReaderPostWidthFull, Config.ReaderPostWidthFit })
        {
            page.ReaderPostWidthPreference = mode;
            scroll.Offset = default;
            await Flush(window);
            long before = ReaderPostLayout.MeasurePasses;
            List<double> frames = new();
            for (int step = 0; step < 90; step++)
            {
                scroll.Offset = new Vector(0, scroll.Offset.Y + 180);
                Stopwatch watch = Stopwatch.StartNew();
                window.UpdateLayout();
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
                frames.Add(watch.Elapsed.TotalMilliseconds);
            }
            frames.Sort();
            results[mode] = (ReaderPostLayout.MeasurePasses - before, frames.Average(), frames[(int)(frames.Count * 0.95)]);
            Console.WriteLine($"SMOKE reader post width scroll: mode={mode} steps=90 postLayoutMeasures={results[mode].Measures} meanMs={results[mode].MeanMs:0.0} p95Ms={results[mode].P95Ms:0.0} extent={scroll.Extent.Height:0}");
        }
        var full = results[Config.ReaderPostWidthFull];
        var fit = results[Config.ReaderPostWidthFit];
        // Fit cards wrap their text narrower, so the page is taller and a step realizes about as many posts; the count of
        // post layouts must stay in the same range, with no second pass per container.
        Require(fit.Measures <= full.Measures * 1.5 + 20,
            $"Fit-to-content measured many more post layouts while scrolling: fit {fit.Measures}, full {full.Measures}.");
        Require(fit.MeanMs <= full.MeanMs * 2 + 15, $"Fit-to-content scrolled markedly slower: fit {fit.MeanMs:0.0} ms, full {full.MeanMs:0.0} ms per step.");
        page.ReaderPostWidthPreference = Config.ReaderPostWidthFit;
    }

    // Screenshots show the pictures, not "Loading image...": wait (bounded) for the visible slots.
    private static async Task WaitForThumbnailsAsync(ListBox list)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(8);
        while (DateTime.UtcNow < deadline && list.GetVisualDescendants().OfType<ArchiveAttachmentView>()
            .Any(view => view.IsEffectivelyVisible && view.Thumbnail.Source == null))
            await Task.Delay(100);
    }

    private static async Task Flush(Window window)
    {
        await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Loaded);
        await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Background);
        window.UpdateLayout();
    }
    private static void Click(Control control) => control.RaiseEvent(new RoutedEventArgs(control is MenuItem ? MenuItem.ClickEvent : Button.ClickEvent));
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
