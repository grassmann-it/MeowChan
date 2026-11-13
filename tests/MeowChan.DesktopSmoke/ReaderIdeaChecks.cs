using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Interfaces;
using MeowChan.Persistence;
using MeowChan.Testing;

namespace MeowChan.DesktopSmoke;

// Reader ideas I13 (imageboard formatting), I1 (quote preview, jump highlight), I3 (unread line),
// I6 (folded long posts) and I2 (inline replies) in a private profile with a detached reader.
internal static class ReaderIdeaChecks
{
    private sealed class Fixture : IAsyncDisposable
    {
        internal required string Profile { get; init; }
        internal required ProfileLease Lease { get; init; }
        internal required ArchiveCatalog Catalog { get; init; }
        internal required ResourceId Id { get; init; }
        internal ArchivePage Page { get; set; } = null!;
        internal Window Window { get; set; } = null!;
        internal ListBox List => Page.FindControl<ListBox>("ArchivePosts")!;

        public async ValueTask DisposeAsync()
        {
            Page?.EndLifetime();
            Window?.Close();
            await Catalog.DisposeAsync();
            Lease.Dispose();
            Directory.Delete(Profile, recursive: true);
        }
    }

    internal static async Task RunAsync(MainWindow owner)
    {
        await CheckFormattingAsync(owner);
        await CheckQuotePreviewAsync(owner);
        await CheckUnreadLineAsync(owner);
        await CheckFoldAsync(owner);
    }

    private static async Task<Fixture> OpenAsync(MainWindow owner, string thread, Func<int, CapturedPost[]> posts, int initialCount = int.MaxValue)
    {
        string profile = Path.Combine(TemporaryWorkspacePaths.TempRoot, "meowchan-reader-ideas-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(profile);
        ProfileLease lease = ProfileLease.Acquire(profile);
        ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(profile, lease);
        ResourceId id = new("4chan", "g", thread);
        Fixture fixture = new() { Profile = profile, Lease = lease, Catalog = catalog, Id = id };
        await CaptureAsync(fixture, posts(initialCount));
        ArchivePage page = new(token => ArchiveReader.OpenAsync(profile, token), (_, _) => Task.CompletedTask, _ => { });
        fixture.Page = page;
        Control pane = page.DetachReader();
        fixture.Window = new Window { Width = 1120, Height = 820, Content = pane, Title = "Private reader ideas" };
        fixture.Window.Show(owner);
        page.IsAvailable = true;
        await page.LoadThreadAsync(id);
        await Program.WaitUntilAsync(() => !page.IsBusy && fixture.List.ItemsSource is ArchivePostRow[]);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        return fixture;
    }

    private static Task CaptureAsync(Fixture fixture, CapturedPost[] posts) => fixture.Catalog.CaptureAsync(new CapturedThreadSnapshot(
        fixture.Id, "https://boards.4chan.org/g/thread/" + fixture.Id.Thread, "Reader ideas " + fixture.Id.Thread,
        ThreadSourceState.Archived, DateTimeOffset.UtcNow, fixture.Profile, posts));

    private static ListBoxItem Container(Fixture fixture, string postId) => fixture.List.GetVisualDescendants().OfType<ListBoxItem>()
        .Single(item => item.DataContext is ArchivePostRow row && row.Id == postId);

    private static ReaderBodyTextBlock Body(Fixture fixture, string postId) => Container(fixture, postId).GetVisualDescendants()
        .OfType<ReaderBodyTextBlock>().Single();

    private static Color Token(Window window, string key) => Application.Current!.TryGetResource(key, window.ActualThemeVariant, out object? value)
        && value is ISolidColorBrush brush ? brush.Color : throw new InvalidOperationException("Missing token " + key);

    // I13: dead references struck through with a note, spoilers collapsed until Enter or click, code on a surface,
    // external links underlined, internal references colour only.
    private static async Task CheckFormattingAsync(MainWindow owner)
    {
        await using Fixture fixture = await OpenAsync(owner, "300", _ =>
        [
            new("300", 0, PostFieldPresence.All, Html: "<p>Opening post</p>"),
            new("301", 1, PostFieldPresence.All, Html: "<a href='#p300' class='quotelink'>&gt;&gt;300</a> is saved, <a href='#p999' class='quotelink'>&gt;&gt;999</a> is not.<br>"
                + "<s>the secret</s> and <pre class='prettyprint'>x = 1;</pre> see <a href='https://example.org/a'>the site</a> or https://example.org/b")
        ]);
        ArchivePostRow row = fixture.List.ItemsSource!.Cast<ArchivePostRow>().Single(post => post.Id == "301");
        fixture.List.ScrollIntoView(row);
        await Program.WaitUntilAsync(() => fixture.List.GetVisualDescendants().OfType<ListBoxItem>().Any(item => ReferenceEquals(item.DataContext, row)));
        await Dispatcher.UIThread.InvokeAsync(fixture.Window.UpdateLayout, DispatcherPriority.Background);
        ReaderBodyTextBlock body = Body(fixture, "301");
        ReaderBodyDocument document = row.BodyDocument;
        Run RunWith(string text) => body.Inlines!.OfType<Run>().Single(run => run.Text != null && run.Text.Contains(text));
        string notCaptured = MeowChan.Desktop.Localization.Texts.T("reader.quoteNotCaptured");
        Require(document.Segments.Any(segment => segment.Kind == ReaderBodySegmentKind.DeadQuote) && body.PlainText.Contains(">>999 " + notCaptured),
            "The reference to an uncaptured post was not marked: " + body.PlainText);
        Run dead = RunWith(">>999");
        Require(dead.TextDecorations == TextDecorations.Strikethrough && RunWith(notCaptured).TextDecorations == null,
            "The dead reference was not struck through (alone, without its note).");
        Run live = RunWith(">>300");
        Require(live.TextDecorations == null && (live.Foreground as ISolidColorBrush)?.Color == Token(fixture.Window, "Workspace.Accent"),
            "An internal reference was not colour only.");
        Run site = RunWith("the site");
        Require(site.TextDecorations == TextDecorations.Underline && RunWith("https://example.org/b").TextDecorations == TextDecorations.Underline,
            "External links were not underlined.");
        Run code = RunWith("x = 1;");
        Require(code.FontFamily.Name == "monospace" && (code.Background as ISolidColorBrush)?.Color == Token(fixture.Window, "Workspace.ReaderCodeSurface"),
            "Code did not use monospace on its surface.");
        Color spoiler = Token(fixture.Window, "Workspace.ReaderSpoiler");
        Run secret = RunWith("the secret");
        Require((secret.Foreground as ISolidColorBrush)?.Color == spoiler && (secret.Background as ISolidColorBrush)?.Color == spoiler && body.HasHiddenSpoilers,
            "The spoiler was not collapsed to its area.");
        Program.CaptureWindow(fixture.Window, "smoke-reader-i13-formatting-hidden.png");
        body.Focus(NavigationMethod.Tab);
        body.RaiseEvent(KeyEvent(InputElement.KeyDownEvent, Avalonia.Input.Key.Enter, body));
        await Dispatcher.UIThread.InvokeAsync(fixture.Window.UpdateLayout, DispatcherPriority.Background);
        Require(row.SpoilersRevealed && !body.HasHiddenSpoilers && (RunWith("the secret").Foreground as ISolidColorBrush)?.Color != spoiler,
            "Enter did not show the spoiler.");
        Program.CaptureWindow(fixture.Window, "smoke-reader-i13-formatting-shown.png");
        Console.WriteLine("SMOKE reader I13: dead reference, spoiler (Enter), code, external link and plain reference styles passed.");
    }


    private static KeyEventArgs KeyEvent(RoutedEvent routed, Avalonia.Input.Key key, Control source) => new() { RoutedEvent = routed, Key = key, Source = source };

    private static void SavePopup(Popup popup, Window window, string name)
    {
        if (popup.Child is not Control child) return;
        double scale = window.RenderScaling;
        using Avalonia.Media.Imaging.RenderTargetBitmap bitmap = new(new PixelSize((int)Math.Ceiling(child.Bounds.Width * scale), (int)Math.Ceiling(child.Bounds.Height * scale)),
            new Vector(96 * scale, 96 * scale));
        bitmap.Render(child);
        bitmap.Save(Path.Combine(AppContext.BaseDirectory, name));
        Console.WriteLine("SMOKE popup render: " + name + " " + child.Bounds);
    }

    // I1: one preview after a 300 ms delay on hover or focus, Space opens it without jumping, Escape closes only the
    // preview, dead references never preview, and a jump flashes the target once (static under reduced motion).
    private static async Task CheckQuotePreviewAsync(MainWindow owner)
    {
        await using Fixture fixture = await OpenAsync(owner, "310", _ =>
        [
            new("310", 0, PostFieldPresence.All, Subject: "Opening", Author: "Anon", Html: "<p>The opening post says something long enough to read.<br>Second line.</p>"),
            new("311", 1, PostFieldPresence.All, Html: "<a href='#p310' class='quotelink'>&gt;&gt;310</a> first reply"),
            new("312", 2, PostFieldPresence.All, Html: "<a href='#p311' class='quotelink'>&gt;&gt;311</a> and <a href='#p999' class='quotelink'>&gt;&gt;999</a> reply two")
        ]);
        ArchivePage page = fixture.Page;
        ListBox list = fixture.List;
        ArchivePostRow[] rows = list.ItemsSource!.Cast<ArchivePostRow>().ToArray();
        ArchivePostRow first = rows.Single(row => row.Id == "311"), second = rows.Single(row => row.Id == "312");
        await Dispatcher.UIThread.InvokeAsync(fixture.Window.UpdateLayout, DispatcherPriority.Background);
        ReaderBodyTextBlock firstBody = Body(fixture, "311"), secondBody = Body(fixture, "312");
        bool readerClosed = false;
        page.CloseReaderRequested = () => readerClosed = true;
        Popup popup = page.FindControl<Popup>("QuotePreviewPopup")!;

        // Hover: nothing at once, the preview after the delay, built from the quoted post only.
        firstBody.RaiseEvent(new ReaderQuoteHoverEventArgs(ReaderBodyTextBlock.QuoteHoverEvent, first.Quotes[0], new Rect(0, 0, 40, 18)));
        Require(!page.IsQuotePreviewOpen, "The quote preview opened without its delay.");
        await Program.WaitUntilAsync(() => page.IsQuotePreviewOpen, () => "no preview after hover");
        TextBlock header = page.FindControl<TextBlock>("QuotePreviewHeader")!, body = page.FindControl<TextBlock>("QuotePreviewBody")!;
        Require(header.Text!.StartsWith(MeowChan.Desktop.Localization.Texts.T("reader.postNumber", "310"), StringComparison.Ordinal) && header.Text.Contains("Anon")
            && page.FindControl<TextBlock>("QuotePreviewSubject")!.Text == "Opening" && body.Text!.StartsWith("The opening post", StringComparison.Ordinal),
            "The preview did not show the quoted post: " + header.Text + " / " + body.Text);
        Require(ReferenceEquals(popup.PlacementTarget, firstBody), "The preview was not placed at the hovered reference.");
        SavePopup(popup, fixture.Window, "smoke-reader-i1-preview.png");
        // One at a time: hovering another reference replaces it.
        secondBody.RaiseEvent(new ReaderQuoteHoverEventArgs(ReaderBodyTextBlock.QuoteHoverEvent, second.Quotes[0], new Rect(0, 0, 40, 18)));
        Require(!page.IsQuotePreviewOpen, "A second preview did not replace the first (it must reopen after the delay).");
        await Program.WaitUntilAsync(() => page.IsQuotePreviewOpen && ReferenceEquals(popup.PlacementTarget, secondBody), () => "no second preview");
        Require(header.Text.StartsWith(MeowChan.Desktop.Localization.Texts.T("reader.postNumber", "311"), StringComparison.Ordinal), "The second preview showed the wrong post: " + header.Text);
        secondBody.RaiseEvent(new ReaderQuoteHoverEventArgs(ReaderBodyTextBlock.QuoteHoverEvent, null, default));
        Require(!page.IsQuotePreviewOpen, "Leaving the reference did not close the preview.");
        // A reference to a post that was never captured has nothing to preview.
        ArchiveQuoteRow dead = second.Quotes.Single(quote => quote.IsDead);
        secondBody.RaiseEvent(new ReaderQuoteHoverEventArgs(ReaderBodyTextBlock.QuoteHoverEvent, dead, new Rect(0, 0, 40, 18)));
        await Task.Delay(500);
        Require(!page.IsQuotePreviewOpen, "A dead reference opened a preview.");

        // Focus: the keyboard quote button previews after the delay; Escape closes only the preview.
        Button key = Container(fixture, "312").GetVisualDescendants().OfType<Button>().First(button => button.Classes.Contains("reader-quote") && button.DataContext is ArchiveQuoteRow row && row.Quote == second.Quotes[0].Quote);
        key.Focus(NavigationMethod.Tab);
        await Program.WaitUntilAsync(() => page.IsQuotePreviewOpen && ReferenceEquals(popup.PlacementTarget, key), () => "no preview on focus");
        key.RaiseEvent(KeyEvent(InputElement.KeyDownEvent, Avalonia.Input.Key.Escape, key));
        Require(!page.IsQuotePreviewOpen && !readerClosed, "Escape did not close just the preview: closed reader=" + readerClosed);
        object? selected = list.SelectedItem;
        key.RaiseEvent(KeyEvent(InputElement.KeyDownEvent, Avalonia.Input.Key.Space, key));
        key.RaiseEvent(KeyEvent(InputElement.KeyUpEvent, Avalonia.Input.Key.Space, key));
        Require(page.IsQuotePreviewOpen && ReferenceEquals(list.SelectedItem, selected), "Space did not open the preview without jumping.");
        key.RaiseEvent(KeyEvent(InputElement.KeyDownEvent, Avalonia.Input.Key.Space, key));
        Require(!page.IsQuotePreviewOpen, "Space did not close an open preview.");
        key.RaiseEvent(KeyEvent(InputElement.KeyDownEvent, Avalonia.Input.Key.Escape, key));
        Require(readerClosed, "Escape with no preview no longer closed the reader.");
        readerClosed = false;

        // Jump: the target flashes once and fades out; reduced motion keeps the wash static until it is removed.
        Border flash = page.FindControl<Border>("ReaderJumpFlash")!;
        await page.NavigateQuoteAsync(first.Quotes[0]);
        await Program.WaitUntilAsync(() => list.SelectedItem is ArchivePostRow { Id: "310" } && flash.IsVisible, () => "no highlight after the jump");
        Require(flash.Width > 100 && flash.Height > 20 && flash.Opacity > 0 && !flash.IsHitTestVisible, "The jump highlight had no extent.");
        Program.CaptureWindow(fixture.Window, "smoke-reader-i1-jump-highlight.png");
        await Program.WaitUntilAsync(() => !flash.IsVisible, () => "the highlight never ended");
        Environment.SetEnvironmentVariable("MEOWCHAN_REDUCED_MOTION", "1");
        try
        {
            await page.NavigateQuoteAsync(second.Quotes[0]);
            await Program.WaitUntilAsync(() => flash.IsVisible, () => "no static highlight under reduced motion");
            await Task.Delay(200);
            Require(flash.IsVisible && Math.Abs(flash.Opacity - 0.35) < 0.001 && flash.Transitions == null, "Reduced motion did not keep the highlight static: " + flash.Opacity);
            await Program.WaitUntilAsync(() => !flash.IsVisible, () => "the static highlight never ended");
        }
        finally { Environment.SetEnvironmentVariable("MEOWCHAN_REDUCED_MOTION", null); }
        Console.WriteLine("SMOKE reader I1: delayed hover/focus preview, one at a time, dead reference, Space/Escape and the jump highlight passed.");
    }


    private static CapturedPost[] Numbered(int firstNumber, int count, int lines = 7) => Enumerable.Range(firstNumber, count).Select((number, index) =>
        new CapturedPost(number.ToString(), index, PostFieldPresence.All,
            Html: "<p>" + string.Join("<br>", Enumerable.Range(1, lines).Select(line => "Post " + number + " line " + line + " of saved text.")) + "</p>")).ToArray();

    private static ScrollViewer ListScroll(Fixture fixture) => fixture.List.GetVisualDescendants().OfType<ScrollViewer>().First();

    // I3: the line "N new posts since last time" after the last read post (from the saved reading position and from a
    // manual "Show new posts"), and the toolbar button that returns to it once the view is past it.
    private static async Task CheckUnreadLineAsync(MainWindow owner)
    {
        await using Fixture fixture = await OpenAsync(owner, "320", _ => Numbered(320, 10));
        ArchivePage page = fixture.Page;
        ListBox list = fixture.List;
        Button back = page.FindControl<Button>("ReaderUnreadReturn")!;
        Require(page.UnreadFirstNewPostId == null && !back.IsVisible, "A thread opened from the list had a reading mark.");
        // Continue reading at post 323: posts 324..329 (six) were not read since.
        await page.ResumeReadingPositionAsync(new LocalReadingPosition("4chan", "g", "320", "323", 0, null));
        await Program.WaitUntilAsync(() => !page.IsBusy && page.UnreadFirstNewPostId == "324", () => "no mark after resume: " + page.UnreadFirstNewPostId);
        ArchivePostRow[] rows = list.ItemsSource!.Cast<ArchivePostRow>().ToArray();
        Require(rows.Single(row => row.UnreadCount > 0).Id == "324" && rows.Single(row => row.Id == "324").UnreadCount == 6, "The line was not on the first unread post.");
        string label = MeowChan.Desktop.Localization.Texts.Count("reader.unreadLine", 6);
        list.ScrollIntoView(rows.Single(row => row.Id == "324"));
        await Program.WaitUntilAsync(() => list.GetVisualDescendants().OfType<ListBoxItem>().Any(item => (item.DataContext as ArchivePostRow)?.Id == "324"));
        await Dispatcher.UIThread.InvokeAsync(fixture.Window.UpdateLayout, DispatcherPriority.Background);
        Require(Container(fixture, "324").GetVisualDescendants().OfType<TextBlock>().Any(text => text.IsEffectivelyVisible && text.Text == label),
            "The reading-mark line did not show its label: " + label);
        Require(!Container(fixture, "325").GetVisualDescendants().OfType<TextBlock>().Any(text => text.Text == label), "A second line appeared.");
        Program.CaptureWindow(fixture.Window, "smoke-reader-i3-unread-line.png");
        // Scrolling past the line shows the way back; the button returns to the line and disappears there.
        ScrollViewer scroll = ListScroll(fixture);
        scroll.Offset = new Vector(0, scroll.Extent.Height);
        await Program.WaitUntilAsync(() => back.IsVisible, () => "no return button after scrolling past the line");
        Require(back.Content as string == MeowChan.Desktop.Localization.Texts.T("reader.unreadReturn"), "The return button had the wrong label.");
        Program.CaptureWindow(fixture.Window, "smoke-reader-i3-return-button.png");
        back.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Program.WaitUntilAsync(() => list.SelectedItem is ArchivePostRow { Id: "324" } && !back.IsVisible, () => "the button did not return to the line");

        // A manual "Show new posts" marks the end of the old view.
        await CaptureAsync(fixture, Numbered(320, 13));
        await page.CheckReaderUpdatesAsync();
        Button show = page.FindControl<Button>("ReaderShowNewPosts")!;
        await Program.WaitUntilAsync(() => show.IsVisible);
        show.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Program.WaitUntilAsync(() => page.CurrentThread?.PostCount == 13 && page.UnreadFirstNewPostId == "330", () => "no mark after showing new posts: " + page.UnreadFirstNewPostId);
        rows = list.ItemsSource!.Cast<ArchivePostRow>().ToArray();
        Require(rows.Single(row => row.UnreadCount > 0) is { Id: "330", UnreadCount: 3 }, "The refresh line did not count the three new posts.");
        // Opening the thread again from the list starts without a mark.
        await page.LoadThreadAsync(fixture.Id);
        await Program.WaitUntilAsync(() => !page.IsBusy && page.CurrentThread?.PostCount == 13);
        Require(page.UnreadFirstNewPostId == null && list.ItemsSource!.Cast<ArchivePostRow>().All(row => row.UnreadCount == 0), "A fresh open kept the old mark.");
        Console.WriteLine("SMOKE reader I3: resume mark, line label, return button, manual refresh mark and fresh open passed.");
    }


    // I6: a body over 24 lines shows 18, a fade and "Show more (N lines)"; the choice survives a rebuild of the page and
    // the thread find opens a folded post with a hit.
    private static async Task CheckFoldAsync(MainWindow owner)
    {
        await using Fixture fixture = await OpenAsync(owner, "330", _ =>
        [
            new("330", 0, PostFieldPresence.All, Html: "<p>Short opening post.</p>"),
            new("331", 1, PostFieldPresence.All, Html: "<p>" + string.Join("<br>", Enumerable.Range(1, 40).Select(line => "Long post line " + line + ".")) + "</p>"),
            new("332", 2, PostFieldPresence.All, Html: "<p>Another short reply.</p>"),
            new("333", 3, PostFieldPresence.All, Html: "<p>" + string.Join("<br>", Enumerable.Range(1, 40).Select(line => line == 35 ? "The needle is on line 35." : "Filler line " + line + ".")) + "</p>")
        ]);
        ArchivePage page = fixture.Page;
        ListBox list = fixture.List;
        ArchivePostRow[] rows = list.ItemsSource!.Cast<ArchivePostRow>().ToArray();
        ArchivePostRow longPost = rows.Single(row => row.Id == "331"), shortPost = rows.Single(row => row.Id == "332"), needlePost = rows.Single(row => row.Id == "333");
        list.ScrollIntoView(longPost);
        await Program.WaitUntilAsync(() => list.GetVisualDescendants().OfType<ListBoxItem>().Any(item => ReferenceEquals(item.DataContext, longPost)));
        await Dispatcher.UIThread.InvokeAsync(fixture.Window.UpdateLayout, DispatcherPriority.Background);
        ReaderBodyTextBlock body = Body(fixture, "331");
        Button FoldButton(string id) => Container(fixture, id).GetVisualDescendants().OfType<Button>().Single(button => button.Classes.Contains("reader-fold"));
        double visible = ReaderBodyTextBlock.FoldVisibleLines * body.LineHeight;
        Require(body.IsFoldable && body.IsFolded && body.LineCount >= 40 && Math.Abs(body.Bounds.Height - visible) < 1.5 && body.OpacityMask != null && body.ClipToBounds,
            $"The long post was not folded to {ReaderBodyTextBlock.FoldVisibleLines} lines: lines={body.LineCount}, height={body.Bounds.Height}, expected {visible}");
        Button more = FoldButton("331");
        await Program.WaitUntilAsync(() => more.IsEffectivelyVisible, () => "the fold button never became visible");
        Require(more.IsEffectivelyVisible && more.IsVisible && Equals(more.Content, MeowChan.Desktop.Localization.Texts.Count("reader.foldMore", body.LineCount - ReaderBodyTextBlock.FoldVisibleLines)),
            $"The fold button did not name the hidden lines: {more.Content} / visible={more.IsEffectivelyVisible} / expected {MeowChan.Desktop.Localization.Texts.Count("reader.foldMore", body.LineCount - ReaderBodyTextBlock.FoldVisibleLines)}");
        Program.CaptureWindow(fixture.Window, "smoke-reader-i6-folded.png");
        Require(!FoldButton("332").IsVisible, "A short post offered a fold button.");
        double foldedHeight = Container(fixture, "331").Bounds.Height;
        more.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Dispatcher.UIThread.InvokeAsync(fixture.Window.UpdateLayout, DispatcherPriority.Background);
        Require(longPost.Expanded && !body.IsFolded && body.OpacityMask == null && body.Bounds.Height >= 40 * body.LineHeight - 1 && Container(fixture, "331").Bounds.Height > foldedHeight + 200
            && Equals(more.Content, MeowChan.Desktop.Localization.Texts.T("reader.foldLess")), "Show more did not unfold the post: " + body.Bounds.Height);
        Program.CaptureWindow(fixture.Window, "smoke-reader-i6-expanded.png");
        // The state is remembered per post: a rebuilt page shows 331 unfolded and 333 folded.
        await page.LoadThreadAsync(fixture.Id);
        await Program.WaitUntilAsync(() => !page.IsBusy && list.ItemsSource is ArchivePostRow[]);
        rows = list.ItemsSource!.Cast<ArchivePostRow>().ToArray();
        Require(rows.Single(row => row.Id == "331").Expanded && !rows.Single(row => row.Id == "333").Expanded, "A rebuilt page forgot which posts were expanded.");
        longPost = rows.Single(row => row.Id == "331"); needlePost = rows.Single(row => row.Id == "333");
        // Fold it again, then let the find open the post that holds its hit.
        longPost.Expanded = false;
        Require(!longPost.Expanded, "The post did not fold again.");
        page.FindControl<Button>("ReaderFindOpen")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Task opened = page.ThreadFindTask;
        page.FindControl<TextBox>("ReaderFindQuery")!.Text = "needle";
        await Program.WaitUntilAsync(() => !ReferenceEquals(opened, page.ThreadFindTask), () => "the find did not start");
        await page.ThreadFindTask;
        await Program.WaitUntilAsync(() => page.FindControl<Button>("ReaderFindNext")!.IsEnabled, () => "no find hit for the needle");
        page.FindControl<Button>("ReaderFindNext")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Program.WaitUntilAsync(() => needlePost.Expanded || list.ItemsSource!.Cast<ArchivePostRow>().Single(row => row.Id == "333").Expanded, () => "the find did not open the folded post");
        await Dispatcher.UIThread.InvokeAsync(fixture.Window.UpdateLayout, DispatcherPriority.Background);
        Require(!Body(fixture, "333").IsFolded, "The post with the hit stayed folded.");
        Console.WriteLine("SMOKE reader I6: fold to 18 lines with fade and count, unfold, remembered state and find expansion passed.");
    }

    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}
