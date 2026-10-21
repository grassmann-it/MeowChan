using Avalonia.Controls;
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
            CapturedPost[] posts = [new("100", 0, PostFieldPresence.All, Html: "<p>Target post</p>"),
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
            Require(target.Replies.Count == 0 && !target.RepliesLoaded,
                "Collapsed replies loaded the backlink page eagerly.");
            Require(target.BacklinksLabel.Contains("25"), "Inline reply count was not displayed.");
            list.ScrollIntoView(target); list.UpdateLayout();
            await Program.WaitUntilAsync(() => list.GetVisualDescendants().OfType<Expander>()
                .Any(expander => ReferenceEquals(expander.DataContext, target)));
            Expander inline = list.GetVisualDescendants().OfType<Expander>()
                .Single(expander => ReferenceEquals(expander.DataContext, target));
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
