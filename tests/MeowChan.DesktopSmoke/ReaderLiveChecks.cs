using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Interfaces;
using MeowChan.Persistence;

namespace MeowChan.DesktopSmoke;

internal static class ReaderLiveChecks
{
    internal static async Task RunAsync(MainWindow owner)
    {
        string profile = Path.Combine(Path.GetTempPath(), "meowchan-reader-live-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(profile);
        ArchivePage? page = null;
        Window? window = null;
        try
        {
            using ProfileLease lease = ProfileLease.Acquire(profile);
            await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(profile, lease);
            ResourceId id = new("4chan", "g", "7000");
            string media = Path.Combine(profile, "media"); Directory.CreateDirectory(media);
            CapturedThreadSnapshot Snapshot(int count) => new(id, "https://boards.4chan.org/g/thread/7000", "Local reader updates",
                ThreadSourceState.Active, DateTimeOffset.UtcNow, media,
                Enumerable.Range(7000, count).Select((post, index) => new CapturedPost(post.ToString(), index,
                    PostFieldPresence.All, Html: "<p>Saved post " + post + "</p>")).ToArray());
            await catalog.CaptureAsync(Snapshot(2));
            bool mayRefresh = true;
            bool watched = true;
            page = new ArchivePage(token => ArchiveReader.OpenAsync(profile, token), (_, _) => Task.CompletedTask, _ => { })
            { CanFollowLive = _ => watched, ReaderRefreshAllowed = () => mayRefresh };
            Control pane = page.DetachReader();
            window = new Window { Width = 1120, Height = 820, Content = pane, Title = "Private reader live regression" };
            window.Show(owner); page.IsAvailable = true;
            await page.LoadThreadAsync(id);
            await page.FocusPostAsync("7000");
            int consciousMoves = 0;
            page.ConsciousReadingPositionChanged += _ => ++consciousMoves;
            ListBox posts = page.FindControl<ListBox>("ArchivePosts")!;
            CheckBox follow = page.FindControl<CheckBox>("ReaderFollowLive")!;
            Button show = page.FindControl<Button>("ReaderShowNewPosts")!;
            await page.CheckReaderUpdatesAsync();
            Require(follow.IsEnabled && follow.IsChecked != true, "Watched reader must offer explicit, initially disabled live following.");
            object originalRows = posts.ItemsSource!;
            await catalog.CaptureAsync(Snapshot(2));
            await page.CheckReaderUpdatesAsync();
            Require(!show.IsVisible, "Unchanged source poll advertised new posts.");
            await catalog.CaptureAsync(Snapshot(4));
            await page.CheckReaderUpdatesAsync();
            await Program.WaitUntilAsync(() => show.IsVisible);
            Require(page.CurrentThread!.PostCount == 2 && ReferenceEquals(originalRows, posts.ItemsSource)
                && (posts.SelectedItem as ArchivePostRow)?.Id == "7000", "New capture changed the frozen reader without consent.");
            Program.CaptureWindow(window, "smoke-reader-new-posts.png");
            show.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Program.WaitUntilAsync(() => page.CurrentThread?.PostCount == 4 && (posts.SelectedItem as ArchivePostRow)?.Id == "7002");
            Require(!show.IsVisible, "Manual update retained a stale notice.");
            Require(consciousMoves > 0, "Manual new-post action did not publish a conscious reading position.");
            consciousMoves = 0; // Equivalent to discarding all pre-forget captures.
            follow.IsChecked = true;
            mayRefresh = false;
            await catalog.CaptureAsync(Snapshot(5));
            await page.CheckReaderUpdatesAsync();
            await Program.WaitUntilAsync(() => show.IsVisible);
            Require(page.CurrentThread!.PostCount == 4, "Live following replaced a gallery/modal-owned snapshot.");
            mayRefresh = true;
            await page.CheckReaderUpdatesAsync();
            await Program.WaitUntilAsync(() => page.CurrentThread?.PostCount == 5 && (posts.SelectedItem as ArchivePostRow)?.Id == "7004");
            Require(consciousMoves == 0, "Automatic live following published a conscious reading position after forget.");
            Program.CaptureWindow(window, "smoke-reader-follow-live.png");
            watched = false;
            await page.CheckReaderUpdatesAsync();
            Require(!follow.IsEnabled && follow.IsChecked != true, "Watch removal left live following active.");
            await catalog.CaptureAsync(Snapshot(6));
            await page.CheckReaderUpdatesAsync();
            Require(page.CurrentThread!.PostCount == 5 && show.IsVisible, "An unobserved archive thread auto-followed new content.");
            ResourceId other = id with { Thread = "8000" };
            await catalog.RegisterThreadAsync(new ArchiveThreadRegistration(other, "https://boards.4chan.org/g/thread/8000", "Other thread", media, false));
            await page.LoadThreadAsync(other);
            Require(!show.IsVisible && follow.IsChecked != true, "Thread switch retained another thread's pending update.");
            Console.WriteLine("SMOKE reader live: unchanged poll silent, new-post notice preserves snapshot/anchor, explicit refresh, opt-in following, modal/gallery deferral, watch removal and thread switch passed.");
        }
        finally
        {
            page?.EndLifetime(); window?.Close();
            Directory.Delete(profile, recursive: true);
        }
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
