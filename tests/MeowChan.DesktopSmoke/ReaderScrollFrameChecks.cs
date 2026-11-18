using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Layout;
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
                // Deep in the page: a card clicked while partly below the view, and one partly above it.
                foreach ((string start, string target, bool above) in new[] { ("1044", "1045", false), ("1034", "1033", true), ("1046", "1045", true) })
                    await PointerSelectAsync(window, page, posts, scale, start, target, above);
            }
            page.ApplyReaderMediaPreferences(original with { Sidebar = false, FocusedEnlarge = false });
            await Flush(window);
            await PointerSelectAsync(window, page, posts, 100, "1044", "1045", false);
            await HeightChangeHoldsAsync(window, page, posts);
            await TabFocusAsync(window, page, posts);
        }
        finally
        {
            page.ApplyReaderMediaPreferences(original);
            await Flush(window);
        }
        Console.WriteLine("SMOKE reader scroll frames: keyboard steps at 200% and 300% centred the target from the first rendered frame on, settled centred, at most two offsets; "
            + "deep pointer selections showed one card position from the first frame on; a realized card's height change held the cards in view; Tab focus outside the view scrolls, programmatic focus does not.");
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

    // A pointer selection deep in the page (review 2026-11-15): the clicked card keeps its place, and with the focused media enlarged the
    // view takes in the whole medium (and the post header with it when both fit) in one move. Every rendered frame after the press shows
    // the card at the same place. Before ReaderPostList the virtualizer re-estimated the cards around the grown card from the average height
    // and the list flailed (tops 636 -> -1279, -1050, 1840 for post 1045).
    private static async Task PointerSelectAsync(MainWindow window, ArchivePage page, ListBox posts, int scale, string start, string target, bool above)
    {
        string label = $"pointer {start}->{target} {(above ? "partly above" : "partly below")} at {scale}%";
        await page.FocusPostAsync(start);
        await Flush(window);
        ScrollViewer scroll = posts.GetVisualDescendants().OfType<ScrollViewer>().First();
        ScrollContentPresenter viewport = scroll.GetVisualDescendants().OfType<ScrollContentPresenter>().First();
        ArchivePostRow row = posts.ItemsSource!.Cast<ArchivePostRow>().Single(candidate => candidate.Id == target);
        double want = above ? -150 : viewport.Bounds.Height - 150;
        for (int attempt = 0; attempt < 4; attempt++)
        {
            if (posts.ContainerFromItem(row) is not Control placed || placed.TranslatePoint(default, viewport) is not Point at)
            {
                posts.ScrollIntoView(row);
                await Flush(window);
                continue;
            }
            if (Math.Abs(at.Y - want) < 1) break;
            scroll.Offset = new Vector(scroll.Offset.X, Math.Max(0, scroll.Offset.Y + at.Y - want));
            await Flush(window);
        }
        await Task.Delay(1700); // past the re-fit window of the selection before: nothing else may move the list
        await Flush(window);
        Control card = posts.ContainerFromItem(row) ?? throw new InvalidOperationException(label + ": the target is not realized.");
        double before = card.TranslatePoint(default, viewport)!.Value.Y;
        Require(Math.Abs(before - want) <= 1, $"{label}: the card could not be placed (top {before}).");
        // A press on the card itself (not on its text, a button or a link), where it is visible.
        Point press = default;
        InputElement? hit = null;
        for (double y = 4; y < card.Bounds.Height && hit == null; y += 12)
            for (double x = card.Bounds.Width - 30; x > 10 && hit == null; x -= 20)
            {
                if (card.TranslatePoint(new Point(x, y), viewport) is not { } inView || inView.Y < 5 || inView.Y > viewport.Bounds.Height - 5) continue;
                Point inWindow = card.TranslatePoint(new Point(x, y), window)!.Value;
                if (window.InputHitTest(inWindow) is InputElement candidate && candidate.GetSelfAndVisualAncestors().Contains(card)
                    && candidate.GetSelfAndVisualAncestors().TakeWhile(node => node != card)
                        .All(node => node is not (Button or TextBox or Avalonia.Controls.Primitives.ToggleButton or SelectableTextBlock)))
                    (press, hit) = (inWindow, candidate);
            }
        Require(hit != null, $"{label}: no plain spot of the card to press.");
        TopLevel top = TopLevel.GetTopLevel(window)!;
        List<(double Top, string Selected)> frames = new();
        bool running = true;
        void Sample(TimeSpan _)
        {
            if (!running) return;
            Dispatcher.UIThread.Post(() =>
            {
                if (running && posts.SelectedItem is { } selected && posts.ContainerFromItem(selected)?.TranslatePoint(default, viewport) is Point at)
                    frames.Add((at.Y, (selected as ArchivePostRow)?.Id ?? "-"));
            }, DispatcherPriority.Render);
            top.RequestAnimationFrame(Sample);
        }
        top.RequestAnimationFrame(Sample);
        await Program.WaitUntilAsync(() => frames.Count >= 3);
        using (Pointer pointer = new(Pointer.GetNextFreeId(), PointerType.Mouse, true))
        {
            hit!.RaiseEvent(new PointerPressedEventArgs(hit, pointer, window, press, 0,
                new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed), KeyModifiers.None));
            hit.RaiseEvent(new PointerReleasedEventArgs(hit, pointer, window, press, 1,
                new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.LeftButtonReleased), KeyModifiers.None, MouseButton.Left));
        }
        Require((posts.SelectedItem as ArchivePostRow)?.Id == target, $"{label}: the press did not select the card.");
        int mark = frames.Count;
        await Program.WaitUntilAsync(() => frames.Count >= mark + 8);
        await Task.Delay(1000); // the decoded picture and the Background hold of the selection come within this
        running = false;
        double[] tops = frames.Skip(mark).Where(frame => frame.Selected == target).Select(frame => Math.Round(frame.Top)).Distinct().ToArray();
        Require(tops.Length == 1, $"{label}: the clicked card moved between rendered frames (tops {before:F0} -> {string.Join(", ", tops)}).");
        Control settled = posts.ContainerFromItem(row)!;
        double cardTop = settled.TranslatePoint(default, viewport)!.Value.Y;
        if (scale <= 100)
            Require(Math.Abs(cardTop - before) <= 1, $"{label}: without the enlargement a press scrolled the list (top {before:F0} -> {cardTop:F0}).");
        else
        {
            Rect? frame = settled.GetVisualDescendants().OfType<ArchiveAttachmentView>().FirstOrDefault(view => view.IsEffectivelyVisible)?.FrameBoundsIn(viewport);
            Require(frame is not { } medium || (medium.Top >= -1 && medium.Bottom <= viewport.Bounds.Height + 1),
                $"{label}: the enlarged medium of the clicked card is not whole in view ({frame}).");
            Require(!above || cardTop >= -1, $"{label}: the header of the clicked card stayed above the view (top {cardTop:F0}).");
        }
    }

    // Deep in the page a realized card that grows and shrinks again (no offset write) leaves the cards in view where they are: the panel
    // measures the card itself (ReaderPostList). A plain virtualizing panel re-estimates them from the average realized height instead.
    private static async Task HeightChangeHoldsAsync(MainWindow window, ArchivePage page, ListBox posts)
    {
        await page.FocusPostAsync("1044");
        await Flush(window);
        await Task.Delay(300);
        await Flush(window);
        ScrollViewer scroll = posts.GetVisualDescendants().OfType<ScrollViewer>().First();
        ScrollContentPresenter viewport = scroll.GetVisualDescendants().OfType<ScrollContentPresenter>().First();
        double Top(Control card) => card.TranslatePoint(default, viewport)!.Value.Y;
        ListBoxItem[] shown = posts.GetVisualDescendants().OfType<ListBoxItem>().Where(card => card.IsVisible && card.Bounds.Height > 0)
            .Where(card => Top(card) + card.Bounds.Height > 0 && Top(card) < viewport.Bounds.Height).OrderBy(Top).ToArray();
        Require(shown.Length >= 2, "Fewer than two cards in view deep in the page.");
        ListBoxItem first = shown[0], grows = shown[1];
        double offset = scroll.Offset.Y, firstTop = Top(first), growsTop = Top(grows), height = grows.Bounds.Height;
        foreach (bool grown in new[] { true, false })
        {
            if (grown) grows.MinHeight = height + 240;
            else grows.ClearValue(Layoutable.MinHeightProperty);
            await Flush(window);
            Require(Math.Abs(grows.Bounds.Height - (grown ? height + 240 : height)) <= 1, "The fixture card did not change its height.");
            Require(Math.Abs(scroll.Offset.Y - offset) <= 1 && Math.Abs(Top(first) - firstTop) <= 1 && Math.Abs(Top(grows) - growsTop) <= 1,
                $"A realized card that {(grown ? "grew" : "shrank")} moved the cards in view (offset {offset:F0} -> {scroll.Offset.Y:F0}, "
                + $"first card {firstTop:F0} -> {Top(first):F0}, changed card {growsTop:F0} -> {Top(grows):F0}).");
        }
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
