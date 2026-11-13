using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Documents;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Interfaces;
using MeowChan.Persistence;
using MeowChan.Testing;

namespace MeowChan.DesktopSmoke;

internal static class ReaderRepliesChecks
{
    internal static async Task RunAsync(MainWindow owner)
    {
        string profile = Path.Combine(TemporaryWorkspacePaths.TempRoot,
            "meowchan-reader-replies-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(profile);
        ResourceId id = new("4chan", "g", "100");
        ArchivePage? page = null;
        Window? window = null;
        try
        {
            using ProfileLease lease = ProfileLease.Acquire(profile);
            await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(profile, lease);
            CapturedPost[] posts = [new("100", 0, PostFieldPresence.All, Html: "<p>" + string.Join(" ", Enumerable.Repeat("Saved local text remains selectable and readable at the configured font size.", 24)) + "</p>"),
                .. Enumerable.Range(101, 25).Select((number, index) => new CapturedPost(number.ToString(), index + 1,
                    PostFieldPresence.All, Html: "<a href='#p100'>&gt;&gt;100</a> reply " + number))];
            await catalog.CaptureAsync(new CapturedThreadSnapshot(id, "https://boards.4chan.org/g/thread/100",
                "Inline replies regression", ThreadSourceState.Archived, DateTimeOffset.UtcNow, profile, posts));
            page = new ArchivePage(token => ArchiveReader.OpenAsync(profile, token), (_, _) => Task.CompletedTask, _ => { });
            window = new Window { Width = 1120, Height = 820, Content = page, Title = "Private inline replies regression" };
            window.Show(owner);
            page.IsAvailable = true;
            await page.LoadThreadAsync(id);
            ListBox list = page.FindControl<ListBox>("ArchivePosts")!;
            ArchivePostRow target = list.ItemsSource!.Cast<ArchivePostRow>().Single(row => row.Id == "100");
            await Program.WaitUntilAsync(() => target.BacklinkCount == 25);
            ArchivePostRow zero = list.ItemsSource!.Cast<ArchivePostRow>().Single(row => row.Id == "125");
            Require(zero.HasConfirmedNoReplies && !zero.CanExpandReplies && !zero.HasReplyToggle,
                "A confirmed zero remained an interactive reply counter.");
            Require(target.Replies.Count == 0 && !target.RepliesLoaded,
                "Collapsed replies loaded the backlink page eagerly.");
            Require(target.BacklinksLabel.Contains("25"), "Inline reply count was not displayed.");
            list.ScrollIntoView(target); list.UpdateLayout();
            await Program.WaitUntilAsync(() => list.GetVisualDescendants().OfType<Button>()
                .Any(button => button.Classes.Contains("reader-reply-toggle") && ReferenceEquals(button.DataContext, target)));
            Button toggle = list.GetVisualDescendants().OfType<Button>()
                .Single(button => button.Classes.Contains("reader-reply-toggle") && ReferenceEquals(button.DataContext, target));
            ListBoxItem inline = list.GetVisualDescendants().OfType<ListBoxItem>().Single(container => ReferenceEquals(container.DataContext, target));
            Require(toggle.IsEffectivelyVisible && !list.GetVisualDescendants().OfType<Button>().Any(button => button.Classes.Contains("reader-reply-card")),
                "The reply counter was missing or the reply cards were built while collapsed.");
            ReaderBodyTextBlock body = list.GetVisualDescendants().OfType<ReaderBodyTextBlock>()
                .Single(block => ReferenceEquals(block.DataContext, target));
            page.ReadingFontSizePreference = 15;
            list.UpdateLayout();
            Console.WriteLine("SMOKE reader text measure at 15: " + MeasureDetails());
            Require(Math.Abs(body.MaxWidth - 660) < 0.1 && body.Bounds.Width <= 660.1,
                "Long text-only body did not use the reading measure: " + MeasureDetails());
            page.ReadingFontSizePreference = 22;
            list.UpdateLayout();
            Console.WriteLine("SMOKE reader text measure at 22: " + MeasureDetails());
            Require(Math.Abs(body.MaxWidth - 968) < 0.1 && Math.Abs(body.FontSize - 22) < 0.1,
                "Text reading measure failed to follow the configured reading font: " + MeasureDetails());
            string MeasureDetails() => $"preference={page.ReadingFontSizePreference}; listFont={list.FontSize}; "
                + $"bodyFont={body.FontSize}; textOnly={body.IsTextOnly}; maxWidth={body.MaxWidth}; "
                + $"bounds={body.Bounds}; desired={body.DesiredSize}; alignment={body.HorizontalAlignment}; "
                + $"theme={window.ActualThemeVariant}; scale={window.RenderScaling}; textLength={body.Text?.Length}";
            body.SelectionStart = 0;
            body.SelectionEnd = 20;
            Require(body.SelectedText == target.Body[..20], "Reading measure broke native text selection.");
            body.SelectionStart = body.SelectionEnd = 0;
            body.IsTextOnly = false;
            Require(double.IsPositiveInfinity(body.MaxWidth), "Attachment/text bodies inherited the text-only limit.");
            body.IsTextOnly = true;
            page.ReadingFontSizePreference = 15;
            foreach (ThemeVariant theme in new[] { ThemeVariant.Dark, ThemeVariant.Light })
            {
                window.RequestedThemeVariant = theme;
                list.SelectedItem = target;
                ListBoxItem item = list.GetVisualDescendants().OfType<ListBoxItem>()
                    .Single(container => ReferenceEquals(container.DataContext, target));
                item.Focus(NavigationMethod.Tab);
                await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Background);
                ContentPresenter presenter = item.GetVisualDescendants().OfType<ContentPresenter>().First();
                Require(presenter.BorderThickness.Left == 3 && presenter.BorderThickness.Top >= 1,
                    "Keyboard focus was not independently outlined beside the active-post edge.");
                Program.CaptureWindow(window, "smoke-reader-text-" + theme.Key + ".png");
            }
            toggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Program.WaitUntilAsync(() => target.Replies.Count == ReaderDisplayLimits.ReplyStepDefault && !target.RepliesLoading);
            Require(target.RepliesExpanded && target.HasMoreReplies && target.Replies[0].Quote.Origin.PostId == "101",
                "First inline reply page was not bounded or ordered.");
            await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Background);
            Button[] cards = inline.GetVisualDescendants().OfType<Button>().Where(button => button.Classes.Contains("reader-reply-card")).ToArray();
            Require(cards.Length == ReaderDisplayLimits.ReplyStepDefault, $"The first page showed {cards.Length} reply cards.");
            Program.CaptureWindow(window, "smoke-reader-i2-replies.png");
            while (target.HasMoreReplies)
            {
                int before = target.Replies.Count;
                inline.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, target.MoreRepliesLabel) && button.IsEffectivelyVisible)
                    .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Program.WaitUntilAsync(() => target.Replies.Count > before && !target.RepliesLoading);
            }
            Require(target.Replies.Count == 25 && target.Replies[^1].Quote.Origin.PostId == "125",
                "Additional inline replies were not appended once.");
            await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Background);
            cards = inline.GetVisualDescendants().OfType<Button>().Where(button => button.Classes.Contains("reader-reply-card")).ToArray();
            Require(cards.Length == 25 && cards.All(button =>
                button.DataContext is ArchiveQuoteRow reply && AutomationProperties.GetName(button) == reply.AccessibleLabel
                && Equals(ToolTip.GetTip(button), reply.AccessibleLabel) && reply.ReplyCardHeader.Contains(reply.Quote.Origin.PostId)
                && reply.ReplyExcerpt.Contains("reply " + reply.Quote.Origin.PostId)
                && button.GetVisualDescendants().OfType<TextBlock>().Any(text => text.Text == reply.ReplyExcerpt)),
                "Reply cards lost their full accessible target identity, header or excerpt.");
            Require(cards.Select(button => button.TranslatePoint(default, inline)!.Value.Y).Distinct().Count() == 25
                && cards.All(button => button.Bounds.Width > 200 && button.Bounds.Width <= inline.Bounds.Width),
                "Reply cards did not stack inside the available reading width.");
            Program.CaptureWindow(window, "smoke-reader-compact-replies.png");
            // The counter closes the cards again without dropping what was loaded, and reopens them without a reload.
            toggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Background);
            Require(!target.RepliesExpanded && target.Replies.Count == 25 && cards.All(button => !button.IsEffectivelyVisible),
                "The reply counter did not collapse the cards.");
            toggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Background);
            Require(target.RepliesExpanded && !target.RepliesLoading && target.Replies.Count == 25 && cards.All(button => button.IsEffectivelyVisible),
                "Reopening the replies reloaded or hid them.");
            Button firstReply = cards.First(button => ReferenceEquals(button.DataContext, target.Replies[0]));
            firstReply.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Program.WaitUntilAsync(() => list.SelectedItem is ArchivePostRow { Id: "101" });
            Require(page.CurrentThread?.Summary.Id == id, "Inline backlink navigation left the local thread.");
            Console.WriteLine("SMOKE reader replies: collapsed count, bounded 5-card inline page, more replies and quote navigation passed.");
            foreach (ThemeVariant theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
            {
                window.RequestedThemeVariant = theme;
                await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Background);
                Require(window.ActualThemeVariant == theme, "The card fixture did not apply " + theme.Key);
                await CheckPostCardsAsync(window, page, id);
            }
        }
        finally
        {
            page?.EndLifetime();
            window?.Close();
            Directory.Delete(profile, recursive: true);
        }
    }

    // Reader post cards (R1-R6): OP on the page and reply cards in their tokens, one-line header with the
    // copyable number and reply count, inline >>No. hit-testing and navigation over displayed plain text,
    // the focus-revealed keyboard quote row, and the bundled reading font with its relative line height.
    private static async Task CheckPostCardsAsync(Window window, ArchivePage page, ResourceId id)
    {
        ListBox list = page.FindControl<ListBox>("ArchivePosts")!;
        // The preceding backlink navigation re-renders the page; read the settled rows.
        await Program.WaitUntilAsync(() => !page.IsBusy && list.ItemsSource is ArchivePostRow[]);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        ArchivePostRow[] rows = list.ItemsSource!.Cast<ArchivePostRow>().ToArray();
        ArchivePostRow opening = rows.Single(row => row.Id == "100"), reply = rows.Single(row => row.Id == "101"); // Realized right below the long OP.
        Require(opening.IsOpeningPost && !reply.IsOpeningPost, "The thread-number post was not the only opening post.");
        await Program.WaitUntilAsync(() => opening.BacklinkCount == 25);
        ListBoxItem Container(ArchivePostRow row) => list.GetVisualDescendants().OfType<ListBoxItem>()
            .Single(item => ReferenceEquals(item.DataContext, row));
        // The preceding async quote click can still be settling its focus/layout passes after selecting 101.
        // Supersede that request through the reader API and await its completion before sampling inactive cards.
        await page.FocusPostAsync("120");
        Require(list.SelectedItem is ArchivePostRow { Id: "120" },
            "The inactive-card fixture did not finish moving selection away from the sampled posts.");
        // Keep keyboard focus out of the virtualized containers too: an inactive but focused post
        // correctly has an accent edge, which is a different state from the plain-card contract.
        Require(page.FindControl<Button>("ReaderTagsOpen")!.Focus(),
            "The inactive-card fixture could not move focus to the reader toolbar.");
        await Program.WaitUntilAsync(() =>
        {
            list.ScrollIntoView(opening); list.UpdateLayout();
            return list.SelectedItem is ArchivePostRow { Id: not ("100" or "101") }
                && list.GetVisualDescendants().OfType<ListBoxItem>().Any(item => ReferenceEquals(item.DataContext, opening))
                && list.GetVisualDescendants().OfType<ListBoxItem>().Any(item => ReferenceEquals(item.DataContext, reply));
        }, () => "selected=" + (list.SelectedItem as ArchivePostRow)?.Id + "; realized=" + string.Join(",", list.GetVisualDescendants()
            .OfType<ListBoxItem>().Select(item => (item.DataContext as ArchivePostRow)?.Id)) + "; rows=" + string.Join(",", rows.Select(row => row.Id))
            + "; source=" + ReferenceEquals(list.ItemsSource, rows) + "/" + (list.ItemsSource as ArchivePostRow[])?.Length);
        await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Background);
        Color Token(string key) => Application.Current!.TryGetResource(key, window.ActualThemeVariant, out object? value)
            && value is ISolidColorBrush brush ? brush.Color : throw new InvalidOperationException("Missing token " + key);
        ContentPresenter Presenter(ListBoxItem item) => item.GetVisualDescendants().OfType<ContentPresenter>()
            .Single(presenter => ReferenceEquals(presenter.TemplatedParent, item));
        Color? Surface(ListBoxItem item) => (Presenter(item).Background as ISolidColorBrush)?.Color;
        ListBoxItem opItem = Container(opening), replyItem = Container(reply);
        Require(!opItem.IsSelected && !replyItem.IsSelected && !opItem.IsKeyboardFocusWithin && !replyItem.IsKeyboardFocusWithin,
            "Inactive-card fixture retained selection or focus: selected=" + (list.SelectedItem as ArchivePostRow)?.Id);
        Program.CaptureWindow(window, "smoke-reader-cards-inactive-" + window.ActualThemeVariant.Key + ".png");
        Require(opItem.Classes.Contains("reader-op") && Surface(opItem) == Colors.Transparent
            && (Presenter(opItem).BorderBrush as ISolidColorBrush)?.Color == Colors.Transparent
            && Presenter(opItem).BorderThickness == new Thickness(1),
            "The inactive opening post did not sit directly on the page: " + CardDetails(opItem));
        Require(!replyItem.Classes.Contains("reader-op") && Surface(replyItem) == Token("Workspace.Card")
            && (Presenter(replyItem).BorderBrush as ISolidColorBrush)?.Color == Token("Workspace.CardEdge")
            && Presenter(replyItem).BorderThickness == new Thickness(1),
            "A reply was not a card with its edge: " + CardDetails(replyItem));
        string CardDetails(ListBoxItem item) => $"post={(item.DataContext as ArchivePostRow)?.Id}; "
            + $"selected={item.IsSelected}; focus={item.IsFocused}; over={item.IsPointerOver}; "
            + $"classes={string.Join(",", item.Classes)}; listSelected={(list.SelectedItem as ArchivePostRow)?.Id}; "
            + $"theme={window.ActualThemeVariant}; surface={Surface(item)}; "
            + $"edge={(Presenter(item).BorderBrush as ISolidColorBrush)?.Color}; thickness={Presenter(item).BorderThickness}";
        Require((list.Background as ISolidColorBrush)?.Color == Token("Workspace.Background"), "The post list was not on the page tone.");
        Require(opItem.GetVisualDescendants().OfType<TextBlock>().Any(text => text.IsEffectivelyVisible && text.Text == opening.BacklinkCountLabel),
            "The reply count was not shown in the opening post header.");

        ReaderBodyTextBlock body = replyItem.GetVisualDescendants().OfType<ReaderBodyTextBlock>().Single();
        // P3 adds a separate, selectable marker run after a quote of the opening post; the stored body stays raw.
        string expectedBody = ">>100 " + MeowChan.Desktop.Localization.Texts.T("reader.quote.opMarker") + " reply 101";
        Require(reply.Body == ">>100 reply 101" && body.Text == null && body.Inlines is { Count: 3 }
            && body.PlainText == expectedBody && body.Document!.Segments.Count(segment => segment.Kind == ReaderBodySegmentKind.QuoteMarker) == 1,
            "The reply body did not retain its quote, OP-marker and text runs: " + body.PlainText);
        Run[] runs = body.Inlines!.OfType<Run>().ToArray();
        Require(runs.Length == 3 && string.Concat(runs.Select(run => run.Text)) == expectedBody
            && runs[0].Text == ">>100" && runs[1].Text == " " + MeowChan.Desktop.Localization.Texts.T("reader.quote.opMarker")
            && (runs[0].Foreground as ISolidColorBrush)?.Color == Token("Workspace.Accent")
            && (runs[1].Foreground as ISolidColorBrush)?.Color == Token("Workspace.Muted"),
            "The native quote/marker runs lost their displayed text or theme tokens: " + body.PlainText);
        Require(body.LineHeight == Math.Round(body.FontSize * 1.45), "The reading line height did not follow the font size.");
        Require(FontManager.Current.TryGetGlyphTypeface(new Typeface(body.FontFamily), out var glyphs)
            && glyphs!.FamilyName == "Noto Sans", "The post body did not resolve the bundled Noto Sans: " + body.FontFamily);
        body.SelectionStart = 0; body.SelectionEnd = expectedBody.Length;
        Require(body.SelectedText == expectedBody, "Selecting a formatted body did not copy its plain text: " + body.SelectedText);
        ReaderBodySegment marker = body.Document!.Segments.Single(segment => segment.Kind == ReaderBodySegmentKind.QuoteMarker);
        body.SelectionStart = marker.Start; body.SelectionEnd = marker.End;
        Require(body.SelectedText == " " + MeowChan.Desktop.Localization.Texts.T("reader.quote.opMarker"),
            "Selecting the OP marker did not preserve the displayed copy text: " + body.SelectedText);
        body.SelectionStart = body.SelectionEnd = 0;
        Rect markerCaret = body.TextLayout.HitTestTextPosition(marker.Start + 2);
        Require(body.QuoteAt(new Point(markerCaret.X + 1, markerCaret.Y + markerCaret.Height / 2)) == null,
            "The display-only OP marker became a quote target.");
        Rect caret = body.TextLayout.HitTestTextPosition(2);
        ReaderBodySegment? link = body.QuoteAt(new Point(caret.X + 1, caret.Y + caret.Height / 2));
        Rect plain = body.TextLayout.HitTestTextPosition(expectedBody.Length - 2);
        Require(link?.Quote == reply.Quotes[0] && body.QuoteAt(new Point(plain.X + 1, plain.Y + plain.Height / 2)) == null,
            "Inline >>100 hit-testing did not map to its quote: " + caret);

        ItemsControl keys = replyItem.GetVisualDescendants().OfType<ItemsControl>().Single(control => control.Classes.Contains("reader-quote-keys"));
        Button key = keys.GetVisualDescendants().OfType<Button>().Single();
        Require(keys.Bounds.Height == 0 && key.IsEffectivelyVisible && AutomationProperties.GetName(key) == reply.Quotes[0].AccessibleLabel,
            "The keyboard quote row was not collapsed while unfocused: " + keys.Bounds);
        key.Focus(NavigationMethod.Tab);
        await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Background);
        Require(keys.Bounds.Height > 0 && key.Bounds.Height >= 24, "Keyboard focus did not reveal the quote row: " + keys.Bounds);
        Program.CaptureWindow(window, "smoke-reader-cards-keyboard-quote-" + window.ActualThemeVariant.Key + ".png");

        string? copied = null;
        page.CopyPostReference = text => { copied = text; return Task.CompletedTask; };
        Button number = replyItem.GetVisualDescendants().OfType<Button>().Single(button => button.Classes.Contains("reader-post-number"));
        Require(Equals(number.Content, reply.NumberLabel) && AutomationProperties.GetName(number) == reply.CopyNumberLabel,
            "The post number was not a labelled link.");
        number.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Program.WaitUntilAsync(() => copied != null);
        Require(copied == ">>101" && page.FindControl<TextBlock>("ArchiveMessage")!.Text
            == MeowChan.Desktop.Localization.Texts.T("reader.postNumberCopied", "101"), "The post number did not copy >>No. with feedback: " + copied);
        page.CopyPostReference = null;

        body.RaiseEvent(new ReaderQuoteInvokedEventArgs(ReaderBodyTextBlock.QuoteInvokedEvent, link!.Quote!));
        await Program.WaitUntilAsync(() => list.SelectedItem is ArchivePostRow { Id: "100" } && !page.IsBusy);
        Require(page.CurrentThread?.Summary.Id == id, "Inline >>100 navigation left the local thread.");
        // Assert the selected contract explicitly, not by accepting its accent surface as an inactive card.
        // FocusPostAsync also supersedes the routed quote handler's remaining layout passes.
        foreach (string postId in new[] { "100", "101" })
        {
            await page.FocusPostAsync(postId);
            ArchivePostRow selected = list.ItemsSource!.Cast<ArchivePostRow>().Single(row => row.Id == postId);
            ListBoxItem selectedItem = Container(selected);
            // FocusPostAsync already focused the container with NavigationMethod.Unspecified. Move to a
            // child first so this is a real Tab focus transition, not a no-op focus of the same element.
            Button selectedNumber = selectedItem.GetVisualDescendants().OfType<Button>()
                .Single(button => button.Classes.Contains("reader-post-number"));
            Require(selectedNumber.Focus(NavigationMethod.Tab), "Could not focus the post number for " + postId);
            Require(selectedItem.Focus(NavigationMethod.Tab), "Could not keyboard-focus post " + postId);
            await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Background);
            Require(ReferenceEquals(list.SelectedItem, selected) && selectedItem.IsSelected && selectedItem.IsFocused
                && selectedItem.Classes.Contains(":focus-visible")
                && Surface(selectedItem) == Token("Workspace.AccentSurface")
                && (Presenter(selectedItem).BorderBrush as ISolidColorBrush)?.Color == Token("Workspace.Accent")
                && Presenter(selectedItem).BorderThickness == new Thickness(3, 2, 2, 2),
                "The selected post lost its separate keyboard-focus edge: " + CardDetails(selectedItem));
            Program.CaptureWindow(window, "smoke-reader-cards-focused-" + postId + "-" + window.ActualThemeVariant.Key + ".png");
        }
        Program.CaptureWindow(window, "smoke-reader-cards-" + window.ActualThemeVariant.Key + ".png");
        Console.WriteLine("SMOKE reader cards: OP on page, reply card tokens, header count and number copy, inline >>No. hit-test/navigation, keyboard quote row, Noto Sans body passed.");
    }

    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}
