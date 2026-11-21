using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Archive;
using MeowChan.Desktop;

namespace MeowChan.DesktopSmoke;

// F07/F08: everything underlined in a post body is clickable (real hit-testing and real pointer events at the drawn text), and
// every inline >>No. reference of a post has its own click target and hover preview, in one line and over several lines.
internal static partial class ReaderIdeaChecks
{
    internal static async Task CheckTextLinksAsync(MainWindow owner)
    {
        const string eporner = "https://www.eporner.com/video-XX5RIhNkRtV/latina-julia-assad-takes-big-white-dick-buttplug/";
        string[] variants =
        [
            eporner,
            "https://example.org/a-b-c/Long-Path-With-CAPS-and-dashes-0123456789/",
            "https://example.org/watch?v=AbC-123_x&t=42s",
            "https://example.org/page#Section-2",
            "https://example.org/end-of-line"
        ];
        await using Fixture fixture = await OpenAsync(owner, "400", _ => Enumerable.Concat(
        [
            new CapturedPost("400", 0, PostFieldPresence.All, Subject: "Opening", Html: "<p>The opening post.</p>"),
            new CapturedPost("401", 1, PostFieldPresence.All, Html: "<p>one first</p>"),
            new CapturedPost("402", 2, PostFieldPresence.All, Html: "<p>two second</p>"),
            new CapturedPost("403", 3, PostFieldPresence.All, Html: "<p>three third</p>"),
            new CapturedPost("404", 4, PostFieldPresence.All, Html: "<p>four fourth</p>"),
            new CapturedPost("410", 10, PostFieldPresence.All, Html: Refs("<br>", 401, 402) + "<br>fuck off"),
            new CapturedPost("411", 11, PostFieldPresence.All, Html: Refs("<br>", 400, 401, 402, 403, 404) + "<br>five lines"),
            new CapturedPost("412", 12, PostFieldPresence.All, Html: Refs(" ", 401, 402, 403, 404) + " in one line"),
            new CapturedPost("413", 13, PostFieldPresence.All, Html: Refs(" ", 401, 402) + "<br>" + Refs("<br>", 403, 404)),
            new CapturedPost("414", 14, PostFieldPresence.All, Html: Refs("<br>", 401, 402) + "<br><br>" + eporner)
        ], variants.Select((url, index) => new CapturedPost((420 + index).ToString(), 20 + index, PostFieldPresence.All,
            Html: "<p>text before<br><br>" + url + (index == 4 ? "" : "<br>text after") + "</p>"))).ToArray());
        ArchivePage page = fixture.Page;
        List<Uri> launched = [];
        page.ExternalLinkLauncher = uri => { launched.Add(uri); return Task.FromResult(true); };
        ListBox list = fixture.List;
        async Task<ReaderBodyTextBlock> Realize(string id)
        {
            for (int attempt = 0; attempt < 60; attempt++)
            {
                // The reader may rebuild its rows after a jump, so the row is looked up again on every attempt.
                ArchivePostRow row = list.ItemsSource!.Cast<ArchivePostRow>().Single(post => post.Id == id);
                list.ScrollIntoView(row);
                await Dispatcher.UIThread.InvokeAsync(fixture.Window.UpdateLayout, DispatcherPriority.Background);
                await Task.Delay(50);
                ReaderBodyTextBlock? found = list.GetVisualDescendants().OfType<ListBoxItem>().Where(item => ReferenceEquals(item.DataContext, row))
                    .SelectMany(item => item.GetVisualDescendants().OfType<ReaderBodyTextBlock>()).FirstOrDefault(text => text.Bounds.Height > 0);
                if (found != null) return found;
            }
            throw new InvalidOperationException("Post " + id + " was never realized.");
        }
        Visual root = fixture.Window;
        List<string> failures = [];
        void Fail(string message) { failures.Add(message); Console.WriteLine("TEXTLINKS FAIL " + message); }
        async Task Flush() { await Dispatcher.UIThread.InvokeAsync(fixture.Window.UpdateLayout, DispatcherPriority.Background); await Task.Delay(20); }
        // The drawn text range as points in the control: the middle of each line rectangle of the range.
        static IEnumerable<Point> Points(ReaderBodyTextBlock body, ReaderBodySegment segment) =>
            body.TextLayout.HitTestTextRange(segment.Start, segment.Length).Where(rect => rect.Width > 2)
                .Select(rect => new Point(rect.X + rect.Width / 2 + body.Padding.Left, rect.Y + rect.Height / 2 + body.Padding.Top));
        PointerEventArgs Move(RoutedEvent<PointerEventArgs> routed, ReaderBodyTextBlock body, Point at, Pointer pointer) => new(routed, body, pointer, root,
            body.TranslatePoint(at, root) ?? default, 0, new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.Other), KeyModifiers.None);
        void Click(ReaderBodyTextBlock body, Point at)
        {
            Point rootPoint = body.TranslatePoint(at, root) ?? default;
            using Pointer pointer = new(Pointer.GetNextFreeId(), PointerType.Mouse, true);
            body.RaiseEvent(new PointerPressedEventArgs(body, pointer, root, rootPoint, 0,
                new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed), KeyModifiers.None));
            body.RaiseEvent(new PointerReleasedEventArgs(body, pointer, root, rootPoint, 1,
                new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.LeftButtonReleased), KeyModifiers.None, MouseButton.Left));
            pointer.Capture(null);
        }

        // F07: every underlined address opens its confirmation with exactly the address shown.
        for (int index = 0; index < variants.Length; index++)
        {
            string id = (420 + index).ToString();
            ReaderBodyTextBlock body = await Realize(id);
            ReaderBodySegment link = body.Document!.Segments.Single(segment => segment.Link != null);
            Require(body.PlainText.Substring(link.Start, link.Length) == variants[index], "The address was not recognised whole: " + variants[index]);
            Point[] points = Points(body, link).ToArray();
            Require(points.Length > 0, "No drawn rectangle for " + variants[index]);
            foreach (Point at in points)
            {
                if (!ReferenceEquals(body.SegmentAt(at), link))
                {
                    Fail($"Hit-testing the drawn text of {variants[index]} at {at} found {body.SegmentAt(at)?.Kind.ToString() ?? "nothing"} (lines {string.Join(";", body.TextLayout.TextLines.Select(line => line.FirstTextSourceIndex + "+" + line.Length))}).");
                    continue;
                }
                Click(body, at);
                await Flush();
                if (!(page.IsExternalLinkDialogOpen && page.PendingExternalLink?.OriginalString == variants[index]))
                    Fail($"Clicking {variants[index]} at {at} opened no confirmation (pending {page.PendingExternalLink}).");
                page.CloseExternalLinkDialog();
                await Flush();
            }
        }

        // F08: each reference of each post has a click target and a hover preview of its own post. The reader may rebuild the rows after a
        // jump, so the references are read again from the current body for every step.
        foreach (string id in new[] { "410", "411", "412", "413", "414" })
        {
            ReaderBodyTextBlock body = await Realize(id);
            string[] labels = body.Document!.Segments.Where(segment => segment.Quote != null).Select(segment => segment.Quote!.Quote.Label).ToArray();
            int expected = list.ItemsSource!.Cast<ArchivePostRow>().Single(post => post.Id == id).Quotes.Count;
            Require(labels.Length == expected, $"Post {id}: {labels.Length} reference segments for {expected} quotes.");
            using Pointer pointer = new(Pointer.GetNextFreeId(), PointerType.Mouse, true);
            body.RaiseEvent(Move(InputElement.PointerExitedEvent, body, new Point(-5, -5), pointer));
            foreach (string label in labels)
            {
                body = await Realize(id);
                ReaderBodySegment reference = body.Document!.Segments.First(segment => segment.Quote?.Quote.Label == label);
                Point[] points = Points(body, reference).ToArray();
                if (points.Length == 0) { Fail($"Post {id}: no drawn rectangle for {label}."); continue; }
                foreach (Point at in points)
                    if (body.QuoteAt(at)?.Quote?.Quote.Label != label)
                        Fail($"Post {id}: hit-test of {label} at {at} found {body.QuoteAt(at)?.Quote?.Quote.Label ?? "nothing"} (lines {string.Join(";", body.TextLayout.TextLines.Select(line => line.FirstTextSourceIndex + "+" + line.Length))}).");
                Point inside = points[0];
                // Hover moves directly from the previous reference (no leave in between), as the pointer does between lines.
                string? hovered = null;
                string? clicked = null;
                EventHandler<ReaderQuoteHoverEventArgs> recordHover = (_, e) => hovered = e.Quote?.Quote.Label;
                EventHandler<ReaderQuoteInvokedEventArgs> recordClick = (_, e) => clicked = e.Quote.Quote.Label;
                // The page's own handlers sit on the same element and mark the events handled, so these listen for handled events too.
                body.AddHandler(ReaderBodyTextBlock.QuoteHoverEvent, recordHover, RoutingStrategies.Bubble, handledEventsToo: true);
                body.AddHandler(ReaderBodyTextBlock.QuoteInvokedEvent, recordClick, RoutingStrategies.Bubble, handledEventsToo: true);
                body.RaiseEvent(Move(InputElement.PointerMovedEvent, body, inside, pointer));
                body.RemoveHandler(ReaderBodyTextBlock.QuoteHoverEvent, recordHover);
                // The body reports exactly this reference, and the page has scheduled (or opened) the preview of its post. (The delayed popup
                // itself is not awaited: it needs a focused window, which the private display does not always give.)
                if (hovered != label) Fail($"Post {id}: hovering {label} raised {hovered ?? "nothing"}.");
                else if (!page.IsQuotePreviewPending && !page.IsQuotePreviewOpen) Fail($"Post {id}: hovering {label} scheduled no preview.");
                Click(body, inside);
                body.RemoveHandler(ReaderBodyTextBlock.QuoteInvokedEvent, recordClick);
                if (clicked != label) Fail($"Post {id}: clicking {label} invoked {clicked ?? "nothing"}.");
                page.CloseQuotePreview();
                await Flush();
            }
        }
        Require(failures.Count == 0, "Text link failures: " + failures.Count + "\n" + string.Join("\n", failures));
        Console.WriteLine("SMOKE reader text links: every address variant and every reference of one- and multi-line posts hit-tested, clicked and previewed.");
    }

    private static string Refs(string separator, params int[] ids) =>
        string.Join(separator, ids.Select(id => "<a href='#p" + id + "' class='quotelink'>&gt;&gt;" + id + "</a>"));
}
