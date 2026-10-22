using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Controls.Presenters;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.Interactivity;
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
            Require(zero.HasConfirmedNoReplies && !zero.CanExpandReplies,
                "A confirmed zero remained an interactive expander.");
            Require(target.Replies.Count == 0 && !target.RepliesLoaded,
                "Collapsed replies loaded the backlink page eagerly.");
            Require(target.BacklinksLabel.Contains("25"), "Inline reply count was not displayed.");
            list.ScrollIntoView(target); list.UpdateLayout();
            await Program.WaitUntilAsync(() => list.GetVisualDescendants().OfType<Expander>()
                .Any(expander => ReferenceEquals(expander.DataContext, target)));
            Expander inline = list.GetVisualDescendants().OfType<Expander>()
                .Single(expander => ReferenceEquals(expander.DataContext, target));
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
            inline.IsExpanded = true;
            await Program.WaitUntilAsync(() => target.Replies.Count == 20 && !target.RepliesLoading);
            Require(target.HasMoreReplies && target.Replies[0].Quote.Origin.PostId == "101",
                "First inline reply page was not bounded or ordered.");
            Button more = inline.GetVisualDescendants().OfType<Button>()
                .Single(button => Equals(button.Content, target.MoreRepliesLabel));
            more.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Program.WaitUntilAsync(() => target.Replies.Count == 25 && !target.RepliesLoading);
            Require(!target.HasMoreReplies && target.Replies[^1].Quote.Origin.PostId == "125",
                "Additional inline replies were not appended once.");
            await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Background);
            Button[] replyButtons = inline.GetVisualDescendants().OfType<Button>()
                .Where(button => button.DataContext is ArchiveQuoteRow).ToArray();
            Require(replyButtons.Length == 25 && replyButtons.All(button =>
                button.DataContext is ArchiveQuoteRow reply && Equals(button.Content, "#" + reply.Quote.Origin.PostId)
                && AutomationProperties.GetName(button) == reply.AccessibleLabel
                && Equals(ToolTip.GetTip(button), reply.AccessibleLabel)),
                "Compact local backlinks lost their full accessible target identity.");
            Require(replyButtons.Select(button => button.TranslatePoint(default, inline)!.Value.Y).Distinct().Count() > 1,
                "Reply references did not wrap inside the available reading width.");
            Program.CaptureWindow(window, "smoke-reader-compact-replies.png");
            Button firstReply = inline.GetVisualDescendants().OfType<Button>()
                .First(button => Equals(button.Content, target.Replies[0].Label));
            firstReply.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Program.WaitUntilAsync(() => list.SelectedItem is ArchivePostRow { Id: "101" });
            Require(page.CurrentThread?.Summary.Id == id, "Inline backlink navigation left the local thread.");
            Console.WriteLine("SMOKE reader replies: collapsed count, bounded 20-item inline page, more replies and quote navigation passed.");
        }
        finally
        {
            page?.EndLifetime();
            window?.Close();
            Directory.Delete(profile, recursive: true);
        }
    }

    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}
