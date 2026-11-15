using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Desktop;

namespace MeowChan.DesktopSmoke;

// Reader scroll frames (review 2026-11-15): a keyboard step between posts must not show an intermediate frame, whatever the focused
// media's enlargement. With the enlargement on, a jump centres the card vertically in the viewport (ReaderMediaFit.JumpOffset), so the
// selected card's centre is sampled in every rendered frame (after the layout of its tick): the card that was centred stays centred, and
// so does the target of the step, from the first frame after the key on (before the fix an upward step showed the grown target above the
// view for a frame, the presenter's scroll anchoring having held the old card). A late size change of the card (the decoded picture)
// is centred again by the hold, in at most one more offset.
// Also: Tab into a realized card outside the view brings the focused control in, and a programmatic focus does not scroll.
internal static class ReaderScrollFrameChecks
{
    internal static async Task RunAsync(MainWindow window, ArchivePage page, ListBox posts)
    {
        ReaderMediaPreferences original = page.ReaderMedia;
        try
        {
            foreach (int scale in new[] { 200, 300 })
            {
                page.ApplyReaderMediaPreferences(original with { Sidebar = false, FocusedEnlarge = true, FocusedScalePercent = scale });
                await Flush(window);
                Require(posts.GetValue(ReaderPostLayout.FocusScalePercentProperty) == scale, "The focused media enlargement did not apply at " + scale + "%.");
                // Up onto a media post (the target grows), Up onto a text post (the old card shrinks), Down onto a media post.
                foreach ((string start, string target, Key key) in new[] { ("1004", "1003", Key.Up), ("1006", "1005", Key.Up), ("1002", "1003", Key.Down) })
                    await StepAsync(window, page, posts, scale, start, target, key);
            }
            await TabFocusAsync(window, page, posts);
        }
        finally
        {
            page.ApplyReaderMediaPreferences(original);
            await Flush(window);
        }
        Console.WriteLine("SMOKE reader scroll frames: keyboard steps at 200% and 300% centred the target from the first rendered frame on, settled centred, at most two offsets; Tab focus outside the view scrolls, programmatic focus does not.");
    }

    private static async Task StepAsync(MainWindow window, ArchivePage page, ListBox posts, int scale, string start, string target, Key key)
    {
        await page.FocusPostAsync(start);
        await Flush(window);
        await Task.Delay(1700); // past the re-fit window of the selection before: nothing else may move the list
        await Flush(window);
        ScrollViewer scroll = posts.GetVisualDescendants().OfType<ScrollViewer>().First();
        ScrollContentPresenter viewport = scroll.GetVisualDescendants().OfType<ScrollContentPresenter>().First();
        TopLevel top = TopLevel.GetTopLevel(window)!;
        List<(double Top, double Height, double Offset, string Selected)> frames = new();
        bool running = true;
        // The centre of the card minus the centre of the viewport (zero when centred), or its top minus the aligned top for a card that
        // does not fit; NaN while it is not realized.
        double Error((double Top, double Height, double Offset, string Selected) frame) =>
            ReaderMediaFit.FitsCentred(frame.Height, viewport.Bounds.Height) ? frame.Top + frame.Height / 2 - viewport.Bounds.Height / 2
                : frame.Top - ReaderMediaFit.AlignedTop;
        (double, double, double, string) Measure()
        {
            Control? card = posts.SelectedItem is { } selected ? posts.ContainerFromItem(selected) : null;
            return (card?.TranslatePoint(default, viewport)?.Y ?? double.NaN, card?.Bounds.Height ?? double.NaN, scroll.Offset.Y,
                (posts.SelectedItem as ArchivePostRow)?.Id ?? "-");
        }
        void Sample(TimeSpan _)
        {
            if (!running) return;
            // After the layout of this tick: what that frame renders.
            Dispatcher.UIThread.Post(() => { if (running) frames.Add(Measure()); }, DispatcherPriority.Render);
            top.RequestAnimationFrame(Sample);
        }
        top.RequestAnimationFrame(Sample);
        await Program.WaitUntilAsync(() => frames.Count >= 3);
        string label = $"{key} {start}->{target} at {scale}%";
        double start0 = Error(frames[^1]);
        Require(Math.Abs(start0) <= 1, $"{label}: the starting card is not centred (off by {start0}).");
        int before = frames.Count;
        Control focused = window.FocusManager!.GetFocusedElement() as Control ?? posts;
        if (!focused.GetSelfAndVisualAncestors().Contains(posts)) focused = posts;
        focused.Focus();
        focused.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key });
        await page.ReaderNavigationTask;
        await Program.WaitUntilAsync(() => frames.Count >= before + 8);
        await Task.Delay(400); // a late size change of the card (the decoded picture) is held within its window
        running = false;
        List<(double Top, double Height, double Offset, string Selected)> after = frames.Skip(before).ToList();
        Require((posts.SelectedItem as ArchivePostRow)?.Id == target, $"{label}: the step did not select the target.");
        Require(Math.Abs(Error(after[0])) <= 1,
            $"{label}: the first rendered frame after the key did not show the target centred (off by {Error(after[0])}).");
        Require(Math.Abs(Error(after[^1])) <= 1,
            $"{label}: the settled card is not centred (off by {Error(after[^1])}; tops {string.Join(", ", after.Select(frame => Math.Round(frame.Top)).Distinct())}).");
        // At most the jump's own move and the one re-centring of a late size change: never an intermediate offset of the anchoring.
        Require(after.Select(frame => Math.Round(frame.Offset)).Distinct().Count() <= 2,
            $"{label}: the list showed more than two offsets after the key ({string.Join(", ", after.Select(frame => Math.Round(frame.Offset)).Distinct())}).");
    }

    private static async Task TabFocusAsync(MainWindow window, ArchivePage page, ListBox posts)
    {
        foreach (string start in new[] { "1003", "1006", "1000" })
        {
            await page.FocusPostAsync(start);
            await Flush(window);
            await Task.Delay(300);
            ScrollViewer scroll = posts.GetVisualDescendants().OfType<ScrollViewer>().First();
            ScrollContentPresenter viewport = scroll.GetVisualDescendants().OfType<ScrollContentPresenter>().First();
            Button? below = posts.GetVisualDescendants().OfType<ListBoxItem>()
                .SelectMany(item => item.GetVisualDescendants().OfType<Button>())
                .FirstOrDefault(button => button.Focusable && button.IsEffectivelyVisible && button.Bounds.Height > 0
                    && button.TranslatePoint(default, viewport) is Point at && at.Y >= viewport.Bounds.Height + 10);
            if (below == null) continue;
            double offset = scroll.Offset.Y;
            below.Focus(NavigationMethod.Unspecified);
            await Flush(window);
            Require(Math.Abs(scroll.Offset.Y - offset) <= 1, "A programmatic focus scrolled the reader list.");
            await page.FocusPostAsync(start);
            await Flush(window);
            await Task.Delay(300);
            Require(below.TranslatePoint(default, viewport) is Point still && still.Y >= viewport.Bounds.Height, "The Tab fixture control did not stay outside the view.");
            below.Focus(NavigationMethod.Tab);
            await Flush(window);
            await Task.Delay(300);
            Point now = below.TranslatePoint(default, viewport)!.Value;
            Require(now.Y >= 0 && now.Y + below.Bounds.Height <= viewport.Bounds.Height,
                $"Tab focus left a control of a realized card outside the view (top {now.Y}, viewport {viewport.Bounds.Height}).");
            return;
        }
        throw new InvalidOperationException("No focusable control of a realized card below the view to Tab to.");
    }

    private static async Task Flush(Window window)
    {
        await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Loaded);
        await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Background);
        window.UpdateLayout();
    }

    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
