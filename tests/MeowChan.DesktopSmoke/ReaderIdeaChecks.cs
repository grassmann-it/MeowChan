using Avalonia;
using Avalonia.Automation;
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
internal static partial class ReaderIdeaChecks
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
        await CheckTextLinksAsync(owner);
        await CheckFormattingAsync(owner);
        await CheckQuotePreviewAsync(owner);
        await CheckQuoteMarksAsync(owner);
        await CheckUnreadLineAsync(owner);
        await CheckFoldAsync(owner);
        await CheckLinksAsync(owner);
        await CheckDisplayLimitsAsync(owner);
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

    private static async Task HoverQuoteAsync(MediaNativeInput input, ReaderBodyTextBlock body, ArchiveQuoteRow quote)
    {
        ReaderBodySegment segment = body.Document!.Segments.First(part => part.Quote == quote);
        var root = TopLevel.GetTopLevel(body)!;
        Point? target = null;
        Visual? lastHit = null;
        await Program.WaitUntilAsync(() =>
        {
            // A block can wrap or have whitespace at its centre. Choose a character of the actual reference
            // after the renderer has published the current item and a pointer can hit its text.
            for (int i = segment.Start; i < segment.End; i++)
            {
                foreach (Rect area in body.TextLayout.HitTestTextRange(i, 1))
                {
                    Point local = area.Center + new Vector(body.Padding.Left, body.Padding.Top);
                    if (body.QuoteAt(local)?.Quote != quote || body.TranslatePoint(local, root) is not Point client) continue;
                    lastHit = root.InputHitTest(client) as Visual;
                    if (lastHit?.GetSelfAndVisualAncestors().Contains(body) != true) continue;
                    target = client;
                    return true;
                }
            }
            return false;
        }, () => $"No glyph of {quote.Quote.Label} receives pointer input. Hit {lastHit?.GetType().Name} ({(lastHit as Control)?.Name}).");
        bool delivered = false;
        void Moved(object? sender, PointerEventArgs e) => delivered |= body.QuoteAt(e.GetPosition(body))?.Quote == quote;
        body.AddHandler(InputElement.PointerMovedEvent, Moved, RoutingStrategies.Tunnel, handledEventsToo: true);
        try
        {
            await input.MoveAsync(root, target);
            await Program.WaitUntilAsync(() => delivered, () => $"No native move on {quote.Quote.Label} at {target}, pointer over body {body.IsPointerOver}.");
        }
        finally { body.RemoveHandler(InputElement.PointerMovedEvent, Moved); }
    }

    private static async Task LeaveQuoteAsync(MediaNativeInput input, Fixture fixture)
    {
        // Move clear of both the link and its popup. Traversing the corridor between them has a close grace period.
        await input.MoveAsync(fixture.Window, new Point(fixture.Window.ClientSize.Width - 12, 12));
        await Program.WaitUntilAsync(() => !fixture.Page.IsQuotePreviewOpen && !fixture.Page.IsQuotePreviewPending);
    }

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
        // Only an address is a link (it opens its visible text): the label "the site" stays plain even over an anchor.
        Require(site.TextDecorations == null && RunWith("https://example.org/b").TextDecorations == TextDecorations.Underline,
            "External links were not underlined, or a label that is no address was.");
        Run code = RunWith("x = 1;");
        Require(code.FontFamily.Name == "monospace" && (code.Background as ISolidColorBrush)?.Color == Token(fixture.Window, "Workspace.ReaderCodeSurface"),
            "Code did not use monospace on its surface.");
        Color spoiler = Token(fixture.Window, "Workspace.ReaderSpoiler");
        Run secret = RunWith("the secret");
        Require((secret.Foreground as ISolidColorBrush)?.Color == spoiler && (secret.Background as ISolidColorBrush)?.Color == spoiler && body.HasHiddenSpoilers,
            "The spoiler was not collapsed to its area.");
        // Light/Dark review item 8: the hidden area (about 1.1:1 on the active card) carries a 1-dip interactive outline inside the
        // body, one box per line no taller than the line; the reveal removes it.
        IReadOnlyList<Rect> outline = body.SpoilerOutlineAreas;
        Require(outline.Count > 0 && (body.SpoilerOutlineBrush as ISolidColorBrush)?.Color == Token(fixture.Window, "Workspace.ControlBorder")
            && outline.All(area => area.Width > 1 && area.Height > 1 && area.Height <= body.LineHeight && area.Right <= body.Bounds.Width + 1),
            "The hidden spoiler had no interactive outline: " + string.Join("; ", outline));
        Program.CaptureWindow(fixture.Window, "smoke-reader-i13-formatting-hidden.png");
        // A hidden spoiler is hidden for screen readers too: one neutral name instead of the words, the real text after the reveal.
        Avalonia.Automation.Peers.AutomationPeer peer = Avalonia.Automation.Peers.ControlAutomationPeer.CreatePeerForElement(body);
        string hiddenName = peer.GetName();
        Require(!hiddenName.Contains("the secret") && hiddenName.Contains(MeowChan.Desktop.Localization.Texts.T("reader.spoilerHidden"))
            && hiddenName.Contains("is saved"), "The hidden spoiler text was exposed to screen readers: " + hiddenName);
        body.Focus(NavigationMethod.Tab);
        body.RaiseEvent(KeyEvent(InputElement.KeyDownEvent, Avalonia.Input.Key.Enter, body));
        await Dispatcher.UIThread.InvokeAsync(fixture.Window.UpdateLayout, DispatcherPriority.Background);
        Require(row.SpoilersRevealed && !body.HasHiddenSpoilers && (RunWith("the secret").Foreground as ISolidColorBrush)?.Color != spoiler,
            "Enter did not show the spoiler.");
        Require(body.SpoilerOutlineAreas.Count == 0, "The revealed spoiler kept its hidden outline.");
        string shownName = peer.GetName();
        Require(shownName.Contains("the secret") && !shownName.Contains(MeowChan.Desktop.Localization.Texts.T("reader.spoilerHidden")),
            "The revealed spoiler text was not exposed to screen readers: " + shownName);
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

        fixture.Window.Activate();
        using MediaNativeInput native = new(fixture.Window);
        await Program.WaitUntilAsync(() => fixture.Window.IsActive);
        // Hover: nothing at once, the preview after the delay, built from the quoted post only.
        await HoverQuoteAsync(native, firstBody, first.Quotes[0]);
        Require(!page.IsQuotePreviewOpen, "The quote preview opened without its delay.");
        await Program.WaitUntilAsync(() => page.IsQuotePreviewOpen, () => "no preview after hover");
        TextBlock header = page.FindControl<TextBlock>("QuotePreviewHeader")!, body = page.FindControl<TextBlock>("QuotePreviewBody")!;
        Require(header.Text!.StartsWith(MeowChan.Desktop.Localization.Texts.T("reader.postNumber", "310"), StringComparison.Ordinal) && header.Text.Contains("Anon")
            && page.FindControl<TextBlock>("QuotePreviewSubject")!.Text == "Opening" && body.Text!.StartsWith("The opening post", StringComparison.Ordinal),
            "The preview did not show the quoted post: " + header.Text + " / " + body.Text);
        Require(ReferenceEquals(popup.PlacementTarget, firstBody), "The preview was not placed at the hovered reference.");
        SavePopup(popup, fixture.Window, "smoke-reader-i1-preview.png");
        // One at a time: hovering another reference replaces it.
        await HoverQuoteAsync(native, secondBody, second.Quotes[0]);
        Require(!page.IsQuotePreviewOpen, "A second preview did not replace the first (it must reopen after the delay).");
        await Program.WaitUntilAsync(() => page.IsQuotePreviewOpen && ReferenceEquals(popup.PlacementTarget, secondBody), () => "no second preview");
        Require(header.Text.StartsWith(MeowChan.Desktop.Localization.Texts.T("reader.postNumber", "311"), StringComparison.Ordinal), "The second preview showed the wrong post: " + header.Text);
        await LeaveQuoteAsync(native, fixture);
        Require(!page.IsQuotePreviewOpen, "Leaving the reference did not close the preview.");
        // A reference to a post that was never captured has nothing to preview.
        ArchiveQuoteRow dead = second.Quotes.Single(quote => quote.IsDead);
        await HoverQuoteAsync(native, secondBody, dead);
        // A reference without a captured post is refused synchronously: no popup, no pending delay timer, no remembered row.
        Require(!page.IsQuotePreviewOpen && !page.IsQuotePreviewPending && page.QuotePreviewRow == null, "A dead reference opened or scheduled a preview.");

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
            // No timed sample: FlashPostAsync sets the static wash (opacity, no transition) before it shows the flash, so these hold
            // on the first poll that sees the flash, however slowly that poll returns.
            Require(flash.IsVisible && Math.Abs(flash.Opacity - 0.35) < 0.001 && flash.Transitions == null, "Reduced motion did not keep the highlight static: " + flash.Opacity);
            await Program.WaitUntilAsync(() => !flash.IsVisible, () => "the static highlight never ended");
        }
        finally { Environment.SetEnvironmentVariable("MEOWCHAN_REDUCED_MOTION", null); }
        Console.WriteLine("SMOKE reader I1: delayed hover/focus preview, one at a time, dead reference, Space/Escape and the jump highlight passed.");
    }


    // P3: "(OP)" and "(other thread)" behind a reference, and the outline of the quoted post while its reference is hovered.
    // The outline needs the target's container: none on another page, none when it is not realized or scrolled out of view.
    private static async Task CheckQuoteMarksAsync(MainWindow owner)
    {
        await using Fixture fixture = await OpenAsync(owner, "320", _=> Enumerable.Concat(
        [
            new CapturedPost("320", 0, PostFieldPresence.All, Subject: "Opening", Html: "<p>The opening post.</p>"),
            new CapturedPost("321", 1, PostFieldPresence.All, Html: "<a href='#p320' class='quotelink'>&gt;&gt;320</a> to the opening post, and <a href='/g/thread/777#p778' class='quotelink'>&gt;&gt;778</a> elsewhere, and <a href='#p999' class='quotelink'>&gt;&gt;999</a> missing"),
            new CapturedPost("322", 2, PostFieldPresence.All, Html: "<a href='#p321' class='quotelink'>&gt;&gt;321</a> an ordinary reply")
        ], Enumerable.Range(323, 70).Select((number, index) => new CapturedPost(number.ToString(), 3 + index, PostFieldPresence.All,
            Html: "<p>Filler " + number + "<br>second line<br>third line</p>"))).ToArray());
        ArchivePage page = fixture.Page;
        ListBox list = fixture.List;
        ArchivePostRow[] rows = list.ItemsSource!.Cast<ArchivePostRow>().ToArray();
        ArchivePostRow reply = rows.Single(row => row.Id == "321");
        await Dispatcher.UIThread.InvokeAsync(fixture.Window.UpdateLayout, DispatcherPriority.Background);
        string op = MeowChan.Desktop.Localization.Texts.T("reader.quote.opMarker"), cross = MeowChan.Desktop.Localization.Texts.T("reader.quote.crossMarker");
        ReaderBodyTextBlock body = Body(fixture, "321");
        Require(body.PlainText.Contains(">>320 " + op + " to") && body.PlainText.Contains(">>778 " + cross + " elsewhere") && !body.PlainText.Contains(">>999 " + op),
            "The reference markers were not shown: " + body.PlainText);
        Require(!Body(fixture, "322").PlainText.Contains(op) && !Body(fixture, "322").PlainText.Contains(cross), "An ordinary reference got a marker.");
        Run marker = body.Inlines!.OfType<Run>().First(run => run.Text == " " + op);
        Require((marker.Foreground as ISolidColorBrush)?.Color == Token(fixture.Window, "Workspace.Muted"), "The marker was not drawn in the muted colour.");
        Border outline = page.FindControl<Border>("ReaderQuoteHighlight")!;
        Require(!outline.IsVisible && !outline.IsHitTestVisible, "The outline was visible without a hovered reference.");
        Program.CaptureWindow(fixture.Window, "smoke-reader-p3-quote-marks.png");

        fixture.Window.Activate();
        using MediaNativeInput native = new(fixture.Window);
        await Program.WaitUntilAsync(() => fixture.Window.IsActive);
        // Hover: the target (here the realized opening post) is outlined at once, before the preview delay.
        // Enter the card before moving onto its link. The virtualized list can update its extent on
        // first hover, which dismisses transient controls just like a scroll.
        int plain = body.PlainText.IndexOf("opening post", StringComparison.Ordinal);
        Point approach = body.TextLayout.HitTestTextRange(plain, 1).First().Center + new Vector(body.Padding.Left, body.Padding.Top);
        await native.MoveAsync(fixture.Window, body.TranslatePoint(approach, fixture.Window)!.Value);
        await Program.WaitUntilAsync(() => body.IsPointerOver);
        await Dispatcher.UIThread.InvokeAsync(fixture.Window.UpdateLayout, DispatcherPriority.Background);
        ArchiveQuoteRow toOpening = reply.Quotes.Single(quote => quote.TargetsOpeningPost);
        await HoverQuoteAsync(native, body, toOpening);
        Require(outline.IsVisible && page.IsQuoteHighlightVisible && outline.Width > 100 && outline.Height > 20 && outline.BorderThickness.Left == 2,
            $"The target post was not outlined on hover: visible={outline.IsVisible}, size={outline.Width}×{outline.Height}, border={outline.BorderThickness}, pending={page.IsQuotePreviewPending}, open={page.IsQuotePreviewOpen}.");
        // Same on a post that is not the selected one, so the outline is not confused with the selection frame.
        await LeaveQuoteAsync(native, fixture);
        ArchiveQuoteRow toReply = rows.Single(row => row.Id == "322").Quotes.Single();
        await HoverQuoteAsync(native, Body(fixture, "322"), toReply);
        Require(outline.IsVisible, "The reply target was not outlined.");
        Program.CaptureWindow(fixture.Window, "smoke-reader-p3-quote-outline.png");
        await LeaveQuoteAsync(native, fixture);
        await HoverQuoteAsync(native, body, toOpening);
        await Program.WaitUntilAsync(() => page.IsQuotePreviewOpen, () => "no preview after hover");
        Require(outline.IsVisible, "The outline vanished when the preview opened.");
        await LeaveQuoteAsync(native, fixture);
        Require(!outline.IsVisible, "Leaving the reference did not remove the outline.");

        // References to a dead post or another thread have no target in the list: no outline.
        await HoverQuoteAsync(native, body, reply.Quotes.Single(quote => quote.IsCrossThread));
        await HoverQuoteAsync(native, body, reply.Quotes.Single(quote => quote.IsDead));
        Require(!outline.IsVisible, "A dead or foreign reference was outlined.");

        // Scrolling away removes the outline; a target that is no longer realized or in view is not outlined.
        await HoverQuoteAsync(native, body, toOpening);
        Require(outline.IsVisible, "The outline did not come back.");
        ScrollViewer scroll = ListScroll(fixture);
        scroll.Offset = new Vector(0, scroll.Offset.Y + 120);
        await Dispatcher.UIThread.InvokeAsync(fixture.Window.UpdateLayout, DispatcherPriority.Background);
        Require(!outline.IsVisible, "Scrolling did not remove the outline.");
        page.CloseQuotePreview();
        // The end of the list carries the clearance under the arrow cross: the virtualizer needs a few layout passes to
        // realize the last post at the bottom, so repeat the jump until it is there.
        for (int pass = 0; pass < 10; pass++)
        {
            scroll.Offset = new Vector(0, scroll.Extent.Height);
            await Dispatcher.UIThread.InvokeAsync(fixture.Window.UpdateLayout, DispatcherPriority.Background);
            if (fixture.List.GetVisualDescendants().OfType<ListBoxItem>().Any(item => item.DataContext is ArchivePostRow row && row.Id == rows[^1].Id)) break;
        }
        page.RequestQuotePreview(toOpening, Body(fixture, rows[^1].Id), new Rect(0, 0, 40, 18), immediate: false);
        Require(!outline.IsVisible, "A target scrolled out of view was outlined.");
        page.CloseQuotePreview();
        Console.WriteLine("SMOKE reader P3: reference markers and the target outline (on hover, hidden on scroll/leave, none without a visible target) passed.");
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
        // The line is a row of its own: across the full list width (the cards' 8-dip gutters on both sides), above the card with
        // 8 dip of air, and never inside the card.
        TextBlock labelText = Container(fixture, "324").GetVisualDescendants().OfType<TextBlock>().First(text => text.IsEffectivelyVisible && text.Text == label);
        Grid divider = labelText.GetVisualAncestors().OfType<Grid>().First();
        ListBoxItem markedCard = Container(fixture, "324");
        Control viewport = divider.FindAncestorOfType<Avalonia.Controls.Presenters.ScrollContentPresenter>()!;
        Point dividerStart = divider.TranslatePoint(new Point(0, 0), viewport)!.Value;
        Point cardStart = markedCard.TranslatePoint(new Point(0, 0), viewport)!.Value;
        Require(Math.Abs(dividerStart.X - 8) < 1 && Math.Abs(divider.Bounds.Width - (viewport.Bounds.Width - 16)) < 1,
            "The reading line did not run across the list: x=" + dividerStart.X + ", width=" + divider.Bounds.Width + ", list=" + viewport.Bounds.Width);
        Require(Math.Abs(cardStart.Y - (dividerStart.Y + divider.Bounds.Height) - 8) < 1,
            "The reading line was not 8 dip above its card: line bottom=" + (dividerStart.Y + divider.Bounds.Height) + ", card top=" + cardStart.Y);
        Program.CaptureWindow(fixture.Window, "smoke-reader-i3-unread-line.png");
        // Scrolling past the line shows the way back; the button returns to the line and disappears there.
        // Bring a concrete row into view so the virtualizer can measure its actual position.
        // A single jump to the estimated extent can land before the line after remeasurement.
        ArchivePostRow last = rows[^1];
        list.ScrollIntoView(last);
        ScrollViewer scroll = ListScroll(fixture);
        string ScrollState() => $"offset={scroll.Offset.Y}, extent={scroll.Extent.Height}, viewport={scroll.Viewport.Height}";
        await Program.WaitUntilAsync(() => list.ContainerFromItem(last)?.TranslatePoint(default, viewport) is { Y: >= 0 } top
            && top.Y < viewport.Bounds.Height, () => $"last post {last.Id} did not scroll into view; {ScrollState()}");
        await Program.WaitUntilAsync(() => back.IsEffectivelyVisible, () => $"no return button after scrolling to post {last.Id}; {ScrollState()}");
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
        double visible = body.FoldVisibleLines * body.LineHeight;
        Require(body.IsFoldable && body.IsFolded && body.LineCount >= 40 && Math.Abs(body.Bounds.Height - visible) < 1.5 && body.OpacityMask != null && body.ClipToBounds,
            $"The long post was not folded to {body.FoldVisibleLines} lines: lines={body.LineCount}, height={body.Bounds.Height}, expected {visible}");
        Button more = FoldButton("331");
        await Program.WaitUntilAsync(() => more.IsEffectivelyVisible, () => "the fold button never became visible");
        Require(more.IsEffectivelyVisible && more.IsVisible && Equals(more.Content, MeowChan.Desktop.Localization.Texts.Count("reader.foldMore", body.LineCount - body.FoldVisibleLines)),
            $"The fold button did not name the hidden lines: {more.Content} / visible={more.IsEffectivelyVisible} / expected {MeowChan.Desktop.Localization.Texts.Count("reader.foldMore", body.LineCount - body.FoldVisibleLines)}");
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

    // P5 part A: spoiler modes, the fold threshold and the preview/reply limits are clamped properties; the defaults keep
    // today's behaviour and a changed value reaches the realized posts (inherited properties, no rebuild of the page).
    private static async Task CheckDisplayLimitsAsync(MainWindow owner)
    {
        await using Fixture fixture = await OpenAsync(owner, "360", _=>
        [
            new("360", 0, PostFieldPresence.All, Html: "<p>" + string.Join("<br>", Enumerable.Range(1, 12).Select(line => "Opening line " + line)) + "</p>"),
            new("361", 1, PostFieldPresence.All, Html: "<p>before <s>the secret</s> after</p>"),
            new("362", 2, PostFieldPresence.All, Html: "<p>" + string.Join("<br>", Enumerable.Range(1, 30).Select(line => "Long post line " + line + ".")) + "</p>"),
            new("363", 3, PostFieldPresence.All, Html: "<a href='#p360' class='quotelink'>&gt;&gt;360</a> a reply")
        ]);
        ArchivePage page = fixture.Page;
        ListBox list = fixture.List;
        ArchivePostRow[] rows = list.ItemsSource!.Cast<ArchivePostRow>().ToArray();
        async Task Layout() => await Dispatcher.UIThread.InvokeAsync(fixture.Window.UpdateLayout, DispatcherPriority.Background);
        async Task<ReaderBodyTextBlock> Realize(string id)
        {
            ArchivePostRow row = rows.Single(item => item.Id == id);
            list.ScrollIntoView(row);
            await Program.WaitUntilAsync(() => list.GetVisualDescendants().OfType<ListBoxItem>().Any(item => ReferenceEquals(item.DataContext, row)));
            await Layout();
            return Body(fixture, id);
        }

        // Defaults are today's behaviour.
        Require(page.ReaderSpoilerModePreference == ReaderSpoilerMode.Hide && page.ReaderFoldLinesPreference == 24 && page.QuotePreviewMaxLines == 8
            && page.QuotePreviewMaxCharacters == 420 && page.QuotePreviewDelay == TimeSpan.FromMilliseconds(300) && page.InlineReplyPageSize == 5
            && page.ReplyCardMaxLines == 4 && page.ReplyCardMaxCharacters == 260, "A default reader limit differs from the old fixed value.");

        // Spoilers: hidden / while the pointer is over the post / always; a click (reveal) keeps them; an invalid value means hidden.
        ReaderBodyTextBlock secretBody = await Realize("361");
        Color spoiler = Token(fixture.Window, "Workspace.ReaderSpoiler");
        Run Secret() => secretBody.Inlines!.OfType<Run>().Single(run => run.Text != null && run.Text.Contains("the secret"));
        bool Painted() => (Secret().Foreground as ISolidColorBrush)?.Color != spoiler; // true = readable
        string Name() => Avalonia.Automation.Peers.ControlAutomationPeer.CreatePeerForElement(secretBody).GetName();
        string hiddenText = MeowChan.Desktop.Localization.Texts.T("reader.spoilerHidden");
        Require(secretBody.SpoilerMode == ReaderSpoilerMode.Hide && !Painted() && secretBody.HasHiddenSpoilers && !Name().Contains("the secret") && Name().Contains(hiddenText),
            "Hide mode did not hide the spoiler from the paint and the accessible name.");
        Require(secretBody.SpoilerOutlineAreas.Count > 0, "Hide mode drew no outline around the hidden spoiler.");
        secretBody.SetSpoilerHover(true);
        Require(!Painted() && secretBody.HasHiddenSpoilers, "The pointer over the post showed a spoiler in hide mode.");
        secretBody.SetSpoilerHover(false);

        page.ReaderSpoilerModePreference = ReaderSpoilerMode.Always;
        await Layout();
        Require(secretBody.SpoilerMode == ReaderSpoilerMode.Always && Painted() && !secretBody.HasHiddenSpoilers && Name().Contains("the secret") && !Name().Contains(hiddenText),
            "Always mode did not show the spoiler and its accessible text.");
        Require(secretBody.SpoilerOutlineAreas.Count == 0, "Always mode kept the hidden-spoiler outline.");
        Require(!rows.Single(row => row.Id == "361").SpoilersRevealed, "Showing every spoiler changed the post's own reveal state.");
        Program.CaptureWindow(fixture.Window, "smoke-reader-p5a-spoiler-always.png");

        page.ReaderSpoilerModePreference = ReaderSpoilerMode.Hover;
        await Layout();
        Require(secretBody.SpoilerMode == ReaderSpoilerMode.Hover && !Painted() && !Name().Contains("the secret"), "Hover mode showed the spoiler without the pointer.");
        secretBody.SetSpoilerHover(true);
        await Layout();
        Require(Painted() && !secretBody.HasHiddenSpoilers && Name().Contains("the secret") && secretBody.SpoilerOutlineAreas.Count == 0,
            "Hover mode did not show the spoiler (without its hidden outline) while the pointer is over the post.");
        Program.CaptureWindow(fixture.Window, "smoke-reader-p5a-spoiler-hover.png");
        secretBody.SetSpoilerHover(false);
        await Layout();
        Require(!Painted() && secretBody.HasHiddenSpoilers && !Name().Contains("the secret") && secretBody.SpoilerOutlineAreas.Count > 0,
            "Hover mode kept the spoiler (or lost its outline) after the pointer left.");
        secretBody.SetSpoilerHover(true);
        secretBody.SpoilersRevealed = true; // what a click on the spoiler does
        secretBody.SetSpoilerHover(false);
        await Layout();
        Require(rows.Single(row => row.Id == "361").SpoilersRevealed && Painted() && Name().Contains("the secret"), "A click did not keep the spoiler shown after the pointer left.");
        rows.Single(row => row.Id == "361").SpoilersRevealed = false;
        page.ReaderSpoilerModePreference = (ReaderSpoilerMode)9;
        await Layout();
        Require(page.ReaderSpoilerModePreference == ReaderSpoilerMode.Hide && secretBody.SpoilerMode == ReaderSpoilerMode.Hide && !Painted(),
            "An undefined spoiler mode did not fall back to hidden.");

        // Fold threshold: clamped 8..400, three quarters stay visible, the page follows without a rebuild.
        ReaderBodyTextBlock longBody = await Realize("362");
        Require(longBody.FoldThresholdLines == 24 && longBody.FoldVisibleLines == 18 && longBody.IsFolded
            && Math.Abs(longBody.Bounds.Height - 18 * longBody.LineHeight) < 1.5, "The default fold changed: " + longBody.Bounds.Height);
        page.ReaderFoldLinesPreference = 40;
        await Layout();
        Require(page.ReaderFoldLinesPreference == 40 && longBody.FoldThresholdLines == 40 && !longBody.IsFoldable && !longBody.IsFolded && longBody.OpacityMask == null
            && longBody.Bounds.Height >= 30 * longBody.LineHeight - 1, "A threshold above the post length still folded it: " + longBody.Bounds.Height);
        page.ReaderFoldLinesPreference = 10;
        await Layout();
        Require(longBody.FoldThresholdLines == 10 && longBody.FoldVisibleLines == 8 && longBody.IsFoldable && longBody.IsFolded
            && Math.Abs(longBody.Bounds.Height - 8 * longBody.LineHeight) < 1.5, "A threshold of 10 did not show 8 lines: " + longBody.Bounds.Height);
        Button more = Container(fixture, "362").GetVisualDescendants().OfType<Button>().Single(button => button.Classes.Contains("reader-fold"));
        await Program.WaitUntilAsync(() => more.IsEffectivelyVisible && Equals(more.Content, MeowChan.Desktop.Localization.Texts.Count("reader.foldMore", longBody.LineCount - 8)),
            () => "the fold button did not follow the threshold: " + more.Content);
        Program.CaptureWindow(fixture.Window, "smoke-reader-p5a-fold-10.png");
        page.ReaderFoldLinesPreference = 2;
        Require(page.ReaderFoldLinesPreference == 8, "The fold threshold was not clamped up to 8.");
        page.ReaderFoldLinesPreference = 100000;
        Require(page.ReaderFoldLinesPreference == 400, "The fold threshold was not clamped down to 400.");
        page.ReaderFoldLinesPreference = 24;
        await Layout();
        Require(longBody.IsFolded && Math.Abs(longBody.Bounds.Height - 18 * longBody.LineHeight) < 1.5, "Restoring 24 did not restore the old fold.");

        // Preview lines, characters and delay: clamped; lines and delay change what the user sees.
        page.QuotePreviewMaxLines = 1;
        Require(page.QuotePreviewMaxLines == 3 && page.QuotePreviewMaxCharacters == 158, "Preview lines were not clamped up to 3.");
        page.QuotePreviewMaxLines = 1000;
        Require(page.QuotePreviewMaxLines == 40 && page.QuotePreviewMaxCharacters == 2100, "Preview lines were not clamped down to 40.");
        page.QuotePreviewDelayMilliseconds = -7;
        Require(page.QuotePreviewDelayMilliseconds == 0 && page.QuotePreviewDelay == TimeSpan.Zero, "A negative preview delay was not clamped to 0.");
        page.QuotePreviewDelayMilliseconds = 99999;
        Require(page.QuotePreviewDelayMilliseconds == 2000, "The preview delay was not clamped down to 2000 ms.");
        ArchivePostRow reply = rows.Single(row => row.Id == "363");
        ReaderBodyTextBlock replyBody = await Realize("363");
        Popup popup = page.FindControl<Popup>("QuotePreviewPopup")!;
        TextBlock previewBody = page.FindControl<TextBlock>("QuotePreviewBody")!;
        fixture.Window.Activate();
        using MediaNativeInput native = new(fixture.Window);
        await Program.WaitUntilAsync(() => fixture.Window.IsActive);
        page.QuotePreviewMaxLines = 3;
        page.QuotePreviewDelayMilliseconds = 1200;
        await HoverQuoteAsync(native, replyBody, reply.Quotes[0]);
        await Task.Delay(500); // the old fixed delay of 300 ms would have opened it by now
        Require(!page.IsQuotePreviewOpen && page.IsQuotePreviewPending, "A longer preview delay did not hold the preview back.");
        await Program.WaitUntilAsync(() => page.IsQuotePreviewOpen, () => "no preview after the longer delay");
        Require(previewBody.Text!.Split('\n').Length == 3 && previewBody.Text.EndsWith("…", StringComparison.Ordinal) && previewBody.Text.StartsWith("Opening line 1", StringComparison.Ordinal),
            "The preview did not keep exactly 3 lines: " + previewBody.Text);
        await LeaveQuoteAsync(native, fixture);
        page.QuotePreviewDelayMilliseconds = 0;
        page.QuotePreviewMaxLines = 10;
        await HoverQuoteAsync(native, replyBody, reply.Quotes[0]);
        await Program.WaitUntilAsync(() => page.IsQuotePreviewOpen, () => "no preview with a delay of 0");
        Require(previewBody.Text!.Split('\n').Length == 10, "The preview did not keep 10 lines: " + previewBody.Text);
        await LeaveQuoteAsync(native, fixture);
        page.QuotePreviewMaxLines = ReaderDisplayLimits.PreviewLinesDefault;
        page.QuotePreviewDelayMilliseconds = ReaderDisplayLimits.PreviewDelayDefault;

        // Reply cards: clamped step and lines (characters follow the lines).
        page.InlineReplyPageSize = 0;
        Require(page.InlineReplyPageSize == 1, "The reply step was not clamped up to 1.");
        page.InlineReplyPageSize = 5000;
        Require(page.InlineReplyPageSize == 50, "The reply step was not clamped down to 50.");
        page.ReplyCardMaxLines = 0;
        Require(page.ReplyCardMaxLines == 1 && page.ReplyCardMaxCharacters == 65, "Reply card lines were not clamped up to 1.");
        page.ReplyCardMaxLines = 999;
        Require(page.ReplyCardMaxLines == 20 && page.ReplyCardMaxCharacters == 1300, "Reply card lines were not clamped down to 20.");
        page.InlineReplyPageSize = ReaderDisplayLimits.ReplyStepDefault;
        page.ReplyCardMaxLines = ReaderDisplayLimits.ReplyLinesDefault;
        Console.WriteLine("SMOKE reader P5A: spoiler modes (hide/hover/always, click keeps, invalid = hide), fold threshold, preview lines/delay and reply limits passed.");
    }

    // External links: a click or Enter asks first (full address, host in ASCII form highlighted); only http(s) without user
    // information, only its visible text; hover, focus and a hidden spoiler never open; the session choice is not saved.
    // Nothing is launched here: the seam records the address that would be.
    private static async Task CheckLinksAsync(MainWindow owner)
    {
        await using Fixture fixture = await OpenAsync(owner, "350", _ =>
        [
            new("350", 0, PostFieldPresence.All, Html: "<p>Opening post</p>"),
            new("351", 1, PostFieldPresence.All, Html: "<p>one link https://example.org/page?q=1 only</p>"),
            new("352", 2, PostFieldPresence.All, Html: "<p>two links https://first.example/a and https://second.example/b</p>"),
            new("353", 3, PostFieldPresence.All, Html: "<p>not links: http://user@evil.example/x and <a href='https://real.example/'>the site</a> and ftp://files.example/f</p>"),
            new("354", 4, PostFieldPresence.All, Html: "<p>look-alike https://b\u00FCcher.example/p</p>"),
            new("355", 5, PostFieldPresence.All, Html: "<p><s>hidden https://spoiler.example/x</s></p>")
        ]);
        ArchivePage page = fixture.Page;
        List<Uri> launched = [];
        bool launcherStarts = true;
        page.ExternalLinkLauncher = uri => { launched.Add(uri); return Task.FromResult(launcherStarts); };
        async Task<ReaderBodyTextBlock> Realize(string id)
        {
            ArchivePostRow row = fixture.List.ItemsSource!.Cast<ArchivePostRow>().Single(post => post.Id == id);
            fixture.List.ScrollIntoView(row);
            await Program.WaitUntilAsync(() => fixture.List.GetVisualDescendants().OfType<ListBoxItem>().Any(item => ReferenceEquals(item.DataContext, row)));
            await Dispatcher.UIThread.InvokeAsync(fixture.Window.UpdateLayout, DispatcherPriority.Background);
            return Body(fixture, id);
        }
        async Task Enter(ReaderBodyTextBlock body)
        {
            body.Focus(NavigationMethod.Tab);
            body.RaiseEvent(KeyEvent(InputElement.KeyDownEvent, Avalonia.Input.Key.Enter, body));
            await Dispatcher.UIThread.InvokeAsync(fixture.Window.UpdateLayout, DispatcherPriority.Background);
        }
        Button Named(string name) => page.FindControl<Button>(name)!;

        // Focus alone, and an Enter with two links and no selection, open nothing.
        ReaderBodyTextBlock two = await Realize("352");
        two.Focus(NavigationMethod.Tab);
        await Dispatcher.UIThread.InvokeAsync(fixture.Window.UpdateLayout, DispatcherPriority.Background);
        Require(!page.IsExternalLinkDialogOpen && launched.Count == 0, "Focusing a body with links opened a dialog or a link.");
        await Enter(two);
        Require(!page.IsExternalLinkDialogOpen && launched.Count == 0, "Enter without a selection picked one of two links.");
        // The selection chooses: the link inside it opens (after its confirmation).
        int second = two.PlainText.IndexOf("https://second.example/b", StringComparison.Ordinal);
        two.SelectionStart = second; two.SelectionEnd = second + 5;
        await Enter(two);
        Require(page.IsExternalLinkDialogOpen && page.PendingExternalLink == new Uri("https://second.example/b") && launched.Count == 0,
            "A selection inside the second link did not ask about exactly that address.");
        Require(page.FindControl<SelectableTextBlock>("ReaderLinkUrl")!.Inlines!.OfType<Run>().Any(run => run.Text == "second.example" && run.FontWeight == FontWeight.SemiBold),
            "The confirmation did not highlight the host.");
        Require(Named("ReaderLinkCancel").IsFocused, "The link confirmation did not start with the focus on Cancel.");
        // Assistive technology gets the address; the dialog counts as an open reader dialog (shortcuts stand still) and Tab cycles inside it.
        Require(page.IsReaderDialogOpen, "The link confirmation did not count as an open reader dialog.");
        Require(AutomationProperties.GetName(page.FindControl<SelectableTextBlock>("ReaderLinkUrl")!) == "https://second.example/b"
            && AutomationProperties.GetName(page.FindControl<Control>("ReaderLinkDialog")!).Contains("https://second.example/b", StringComparison.Ordinal)
            && AutomationProperties.GetHelpText(Named("ReaderLinkCancel")).Contains("second.example", StringComparison.Ordinal),
            "The confirmation did not announce the address to assistive technology.");
        Control dialogRoot = page.FindControl<Control>("ReaderLinkDialog")!;
        IFocusManager focusManager = fixture.Window.FocusManager ?? throw new InvalidOperationException("The reader window has no focus manager.");
        for (int step = 0; step < 8; step++)
        {
            focusManager.TryMoveFocus(NavigationDirection.Next);
            Require(focusManager.GetFocusedElement() is Visual moved && (ReferenceEquals(moved, dialogRoot) || dialogRoot.IsVisualAncestorOf(moved)),
                "Tab left the link confirmation after " + (step + 1) + " steps.");
        }
        Named("ReaderLinkCancel").Focus();
        // The start focus is on Cancel, so a held or doubled Enter cannot confirm: a second Enter closes the dialog, nothing opens.
        Control focused = Named("ReaderLinkCancel");
        focused.RaiseEvent(KeyEvent(InputElement.KeyDownEvent, Avalonia.Input.Key.Enter, focused));
        await Dispatcher.UIThread.InvokeAsync(fixture.Window.UpdateLayout, DispatcherPriority.Background);
        Require(!page.IsExternalLinkDialogOpen && launched.Count == 0, "A second Enter opened the link or left the dialog open.");
        two.SelectionStart = two.SelectionEnd = 0;

        // Only addresses that are http(s), without user information, and only their visible text, are links.
        ReaderBodyTextBlock plain = await Realize("353");
        ReaderBodyDocument plainDocument = fixture.List.ItemsSource!.Cast<ArchivePostRow>().Single(post => post.Id == "353").BodyDocument;
        Require(plainDocument.Segments.All(segment => segment.Link == null) && plain.Inlines!.OfType<Run>().All(run => run.TextDecorations != TextDecorations.Underline),
            "Userinfo, ftp or a link label that is not an address became a link.");
        await Enter(plain);
        Require(!page.IsExternalLinkDialogOpen && launched.Count == 0, "Enter opened something that is not a link.");

        // A look-alike host is shown in its ASCII form, highlighted.
        ReaderBodyTextBlock lookAlike = await Realize("354");
        await Enter(lookAlike);
        Require(page.IsExternalLinkDialogOpen && page.FindControl<SelectableTextBlock>("ReaderLinkUrl")!.Inlines!.OfType<Run>().Any(run =>
            run.Text == "xn--bcher-kva.example" && run.FontWeight == FontWeight.SemiBold), "The confirmation did not show the host in punycode.");
        page.CloseExternalLinkDialog();

        // A link inside a hidden spoiler: Enter reveals first and opens nothing; the next Enter asks.
        ReaderBodyTextBlock spoiler = await Realize("355");
        Require(spoiler.HasHiddenSpoilers, "The spoiler fixture was not hidden.");
        await Enter(spoiler);
        Require(!spoiler.HasHiddenSpoilers && !page.IsExternalLinkDialogOpen && launched.Count == 0, "Enter opened a link inside a hidden spoiler.");
        await Enter(spoiler);
        Require(page.IsExternalLinkDialogOpen && page.PendingExternalLink == new Uri("https://spoiler.example/x"), "A revealed link did not ask on Enter.");
        // Escape cancels through the reader key handling alone; the overlay dismissal is a separate path.
        spoiler.RaiseEvent(KeyEvent(InputElement.KeyDownEvent, Avalonia.Input.Key.Escape, spoiler));
        Require(!page.IsExternalLinkDialogOpen && launched.Count == 0, "Escape through the reader key handling did not cancel the confirmation.");
        await Enter(spoiler);
        Require(page.IsExternalLinkDialogOpen, "The link did not ask a second time.");
        Require(page.TryDismissOverlay() && !page.IsExternalLinkDialogOpen, "The overlay dismissal did not cancel the confirmation.");
        Require(!page.IsExternalLinkDialogOpen && launched.Count == 0, "Escape did not cancel the confirmation.");

        // Open, and not asking again this session (not persisted: only this page remembers it).
        ReaderBodyTextBlock one = await Realize("351");
        await Enter(one);
        Require(page.IsExternalLinkDialogOpen && page.PendingExternalLink == new Uri("https://example.org/page?q=1") && !page.ExternalLinksTrusted, "The single link did not ask first.");
        page.FindControl<CheckBox>("ReaderLinkRemember")!.IsChecked = true;
        Program.CaptureWindow(fixture.Window, "smoke-reader-links-confirmation.png");
        Named("ReaderLinkOpen").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        Require(!page.IsExternalLinkDialogOpen && launched.SequenceEqual([new Uri("https://example.org/page?q=1")]) && page.ExternalLinksTrusted,
            "Open did not hand exactly the validated address to the launcher.");
        await Enter(one);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        Require(!page.IsExternalLinkDialogOpen && launched.Count == 2, "After the session choice the link did not open without asking.");
        // A launcher that starts nothing (no default browser) is reported in the user's language, not silently and not as raw text.
        launcherStarts = false;
        await Enter(one);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        Require(page.FindControl<TextBlock>("ArchiveErrorText")!.Text == MeowChan.Desktop.Localization.Texts.T("reader.link.failed"), "A launcher that started nothing was not reported.");
        Console.WriteLine("SMOKE reader links: confirmation with highlighted punycode host, selection and sole-link Enter, spoiler, userinfo, session choice and seam passed.");
    }

    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}
