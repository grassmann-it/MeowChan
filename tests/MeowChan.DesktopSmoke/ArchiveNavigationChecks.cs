using MeowChan.Testing;
using Avalonia.Controls;
using Avalonia.Interactivity;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Interfaces;
using MeowChan.Storage;
using MeowChan.Persistence;

namespace MeowChan.DesktopSmoke;

internal static class ArchiveNavigationChecks
{
    internal static async Task RunAsync(MainWindow window)
    {
        string profile = Path.Combine(TemporaryWorkspacePaths.TempRoot, "meowchan-archive-nav-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(profile);
        ResourceId origin = new("4chan", "g", "100");
        ResourceId target = new("4chan", "a", "400");
        try
        {
            using (ProfileLease lease = ProfileLease.Acquire(profile))
            await using (ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(profile, lease))
            {
                await catalog.CaptureAsync(new CapturedThreadSnapshot(origin, "https://boards.4chan.org/g/thread/100", "Origin",
                    ThreadSourceState.Active, DateTimeOffset.UtcNow, profile,
                    new[] { new CapturedPost("100", 0, PostFieldPresence.All, Html: "<p>navigationneedle</p><a href='/a/thread/400#p475'>&gt;&gt;475</a><a href='#p999'>&gt;&gt;999</a>") }));
                await catalog.CaptureAsync(new CapturedThreadSnapshot(target, "https://boards.4chan.org/a/thread/400", "Target",
                    ThreadSourceState.Archived, DateTimeOffset.UtcNow, profile,
                    Enumerable.Range(400, 76).Select(id => new CapturedPost(id.ToString(), id, PostFieldPresence.All, Html: "Target post " + id)).ToArray()));
            }
            ContentControl host = window.FindControl<ContentControl>("ArchivePageHost")!;
            object? retained = host.Content;
            ArchivePage fixture = new(token => ArchiveReader.OpenAsync(profile, token), (_, _) => Task.CompletedTask, _ => { });
            host.Content = fixture;
            try
            {
                fixture.IsAvailable = true;
                fixture.FindControl<TextBox>("ArchiveSearchText")!.Text = "navigationneedle";
                await fixture.ApplySearchAsync();
                ListBox threads = fixture.FindControl<ListBox>("ArchiveThreads")!;
                Require(threads.ItemCount == 1 && ((ArchiveThreadRow)threads.Items[0]!).Summary.Id == origin, "Post-text search failed.");
                await fixture.ApplySearchSpecAsync(new ArchiveSearchSpec("navigationneedle", ["8kun"]));
                Require(threads.ItemCount == 0, "Exact source filter was ignored.");
                await fixture.ApplySearchSpecAsync(new ArchiveSearchSpec("navigationneedle", ["4chan"], [new("4chan", "g")]));
                Require(threads.ItemCount == 1, "Combined search/filter failed.");
                await fixture.LoadThreadAsync(origin);
                ArchivePostRow originRow = fixture.FindControl<ListBox>("ArchivePosts")!.ItemsSource!.Cast<ArchivePostRow>().Single();
                Require(originRow.Quotes.Count == 2, "Reader lost quote controls.");
                await fixture.NavigateQuoteAsync(originRow.Quotes[0]);
                Require(fixture.CurrentThread?.Summary.Id == target, "Cross-board local navigation failed.");
                ListBox posts = fixture.FindControl<ListBox>("ArchivePosts")!;
                Require(posts.SelectedItem is ArchivePostRow { Id: "475" } && posts.ItemsSource!.Cast<ArchivePostRow>().First().Id == "450", "Target was not selected on its actual post page.");
                await Program.WaitUntilAsync(() => posts.ContainerFromItem(posts.SelectedItem!) is Control);
                Program.CaptureWindow(window, "smoke-archive-search-quote-target.png");
                ArchivePostRow targetRow = (ArchivePostRow)posts.SelectedItem!;
                // Use the real item template button, including its wired UI action.
                Control container = posts.ContainerFromItem(targetRow)!;
                await Program.WaitUntilAsync(() => targetRow.BacklinkCount == 1);
                Expander backlinks = Avalonia.LogicalTree.LogicalExtensions.GetLogicalDescendants(container).OfType<Expander>()
                    .Single(expander => ReferenceEquals(expander.DataContext, targetRow));
                backlinks.IsExpanded = true;
                await Program.WaitUntilAsync(() => targetRow.Replies.Count == 1);
                ArchiveQuoteRow backlink = targetRow.Replies.Single();
                Require(backlink.Backlink && backlink.Quote.Origin.Thread == origin, "Backlink origin is wrong.");
                Program.CaptureWindow(window, "smoke-archive-local-backlinks.png");
                await fixture.NavigateQuoteAsync(backlink);
                Require(fixture.CurrentThread?.Summary.Id == origin, "Backlink navigation failed.");
                fixture.FindControl<Button>("ArchiveHistoryBack")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Require(fixture.CurrentThread?.Summary.Id == target, "Reading history did not return to target.");
                await fixture.NavigateQuoteAsync(originRow.Quotes[1]);
                Require(fixture.CurrentThread?.Summary.Id == target && fixture.FindControl<TextBlock>("ArchiveMessage")!.Text!.Contains("not captured"), "Missing quote replaced reader or lacks feedback.");
            }
            finally { fixture.EndLifetime(); host.Content = retained; }
        }
        finally { Directory.Delete(profile, recursive: true); }
        Console.WriteLine("SMOKE archive navigation: local indexed search, source/board filters, cross-board/page quote focus, backlinks, history and missing-target feedback passed.");
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
