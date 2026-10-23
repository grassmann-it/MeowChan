using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Interfaces;
using MeowChan.Persistence;
using MeowChan.Testing;

namespace MeowChan.DesktopSmoke;

internal static class ReaderReturnChecks
{
    internal static async Task RunAsync(MainWindow owner)
    {
        await CheckQuoteAndGalleryAsync(owner);
        await CheckCloseFocusAsync(owner);
    }

    private static async Task CheckQuoteAndGalleryAsync(MainWindow owner)
    {
        string profile = Path.Combine(TemporaryWorkspacePaths.TempRoot, "meowchan-reader-returns-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(profile);
        ArchivePage? page = null;
        Window? window = null;
        ResourceId a = new("4chan", "g", "7100"), b = a with { Thread = "8100" };
        try
        {
            using ProfileLease lease = ProfileLease.Acquire(profile);
            await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(profile, lease);
            CapturedThreadSnapshot Snapshot(ResourceId id, int count) => new(id,
                "https://boards.4chan.org/g/thread/" + id.Thread, "Return " + id.Thread,
                ThreadSourceState.Active, DateTimeOffset.UtcNow, profile,
                Enumerable.Range(int.Parse(id.Thread!), count).Select((number, index) => new CapturedPost(
                    number.ToString(), index, PostFieldPresence.All,
                    Html: index == 0 ? "<p>" + string.Join(" ", Enumerable.Repeat("Long saved opening post for an exact partial return anchor.", 250)) + "</p>"
                        : "<a href='#p" + id.Thread + "'>&gt;&gt;" + id.Thread + "</a> reply " + number)).ToArray());
            await catalog.CaptureAsync(Snapshot(a, 36));
            await catalog.CaptureAsync(Snapshot(b, 2));
            page = new ArchivePage(token => ArchiveReader.OpenAsync(profile, token), (_, _) => Task.CompletedTask, _ => { })
                { CanFollowLive = _ => true, ReaderRefreshAllowed = () => false };
            window = new Window { Width = 1120, Height = 820, Content = page.DetachReader(), Title = "Private reader returns" };
            window.Show(owner); page.IsAvailable = true;
            await page.LoadThreadAsync(a);
            await page.FocusPostAsync("7100");
            ListBox list = page.FindControl<ListBox>("ArchivePosts")!;
            ArchivePostRow opening = Rows().Single(row => row.Id == "7100");
            await Program.WaitUntilAsync(() => opening.BacklinkCount == 35);
            await Flush(window);
            Expander expander = list.GetVisualDescendants().OfType<Expander>().Single(item => ReferenceEquals(item.DataContext, opening));
            expander.IsExpanded = true;
            await Program.WaitUntilAsync(() => opening.Replies.Count == 20 && !opening.RepliesLoading);
            Button more = expander.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, opening.MoreRepliesLabel));
            Click(more);
            await Program.WaitUntilAsync(() => opening.Replies.Count == 35 && !opening.RepliesLoading);
            await Flush(window);
            ScrollViewer scroll = list.GetVisualDescendants().OfType<ScrollViewer>().First();
            // Leave the opening post partially above the viewport while its >20
            // reply pages remain expanded, then follow a real saved quote.
            scroll.Offset = new Vector(0, 120);
            await Flush(window);
            double top = Top(list, "7100");
            Require(top < -20, "Return fixture did not start in a partially visible opening post.");
            await page.NavigateQuoteAsync(opening.Replies[0]);
            Button back = page.FindControl<Button>("ArchiveHistoryBack")!;
            Click(back);
            await Program.WaitUntilAsync(() => Rows().Single(row => row.Id == "7100").RepliesExpanded);
            await Flush(window);
            ArchivePostRow restored = Rows().Single(row => row.Id == "7100");
            Require(restored.RepliesLoaded && restored.Replies.Count == 35 && !restored.HasMoreReplies
                && restored.Replies[^1].Quote.Origin.PostId == "7135", "Quote Back lost expanded reply pages above the 20-item boundary.");
            await Program.WaitUntilAsync(() => Math.Abs(Top(list, "7100") - top) < 2);
            Require((list.SelectedItem as ArchivePostRow)?.Id == "7100", "Quote Back lost the selected opening post.");
            Console.WriteLine($"SMOKE reader return F5: expanded35 replies restored, origin top={top:F2}, actual={Top(list, "7100"):F2}.");

            Action restoreGallery = page.CaptureGalleryOrigin();
            await page.LoadThreadAsync(b);
            await catalog.CaptureAsync(Snapshot(b, 5));
            await page.CheckReaderUpdatesAsync();
            Button show = page.FindControl<Button>("ReaderShowNewPosts")!;
            CheckBox follow = page.FindControl<CheckBox>("ReaderFollowLive")!;
            Require(show.IsVisible, "Gallery fixture did not create B's pending live update.");
            follow.IsChecked = true;
            restoreGallery();
            Require(page.CurrentThread?.Summary.Id == a && !show.IsVisible && follow.IsChecked != true,
                "Gallery return retained another thread's pending update or live-follow state.");
            await page.CheckReaderUpdatesAsync();
            Require(!show.IsVisible, "Unchanged origin poll resurrected another thread's pending update.");
            restored = Rows().Single(row => row.Id == "7100");
            Require(restored.RepliesExpanded && restored.Replies.Count == 35,
                "Gallery return lost loaded reply pages.");
            Console.WriteLine("SMOKE reader return F6: A -> capture -> B pending/follow -> restore A -> unchanged poll clears foreign state and retains35 replies.");

            IEnumerable<ArchivePostRow> Rows() => list.ItemsSource!.Cast<ArchivePostRow>();
        }
        finally
        {
            page?.EndLifetime(); window?.Close();
            Directory.Delete(profile, recursive: true);
        }
    }

    private static async Task CheckCloseFocusAsync(MainWindow window)
    {
        // Use the actual MainWindow detach/attach/close path. The embedded reader
        // already has a visible anchor before opening the large reader.
        window.Width = 1915; window.Height = 1045;
        window.Activate();
        Click(window.FindControl<MenuItem>("ArchiveMenuItem")!);
        ArchivePage page = window.ArchivePage;
        await Program.WaitUntilAsync(() => !page.IsBusy);
        await page.LoadThreadAsync(new ResourceId("4chan", "g", "1000"));
        await page.FocusPostAsync("1000");
        await page.MediaLoadTask;
        await Flush(window);
        ListBox posts = page.FindControl<ListBox>("ArchivePosts")!;
        ScrollViewer scroll = posts.GetVisualDescendants().OfType<ScrollViewer>().First();
        scroll.Offset = new Vector(scroll.Offset.X, scroll.Offset.Y + 42);
        await Flush(window);
        double expectedTop = Top(posts, "1000");
        string? expectedSelection = (posts.SelectedItem as ArchivePostRow)?.Id;
        Require(expectedTop < -20, "Large reader focus fixture did not retain a nonzero partial origin anchor.");
        TextBox origin = page.FindControl<TextBox>("ArchiveSearchText")!;
        Require(origin.IsEffectivelyVisible && origin.IsEffectivelyEnabled && origin.Focus(),
            $"Large reader origin could not receive focus: visible={origin.IsEffectivelyVisible}, enabled={origin.IsEffectivelyEnabled}, page={page.Bounds}, active={window.IsActive}.");
        MethodInfo open = typeof(MainWindow).GetMethod("OpenLargeReader", BindingFlags.Instance | BindingFlags.NonPublic,
            null, [typeof(Control)], null)!;
        open.Invoke(window, [origin]);
        await Flush(window);
        Click(page.FindControl<Button>("ArchiveCloseReader")!);
        bool immediate = ReferenceEquals(window.FocusManager?.GetFocusedElement(), origin);
        await Flush(window);
        await Flush(window);
        await Program.WaitUntilAsync(() => Math.Abs(Top(posts, "1000") - expectedTop) < 2
            && (posts.SelectedItem as ArchivePostRow)?.Id == expectedSelection);
        object? focused = window.FocusManager?.GetFocusedElement();
        bool final = ReferenceEquals(focused, origin);
        Console.WriteLine($"SMOKE reader return F8: immediateOrigin={immediate}, settledOrigin={final}, finalType={focused?.GetType().Name}, top={expectedTop:F2}/{Top(posts, "1000"):F2}, selected={expectedSelection}/{(posts.SelectedItem as ArchivePostRow)?.Id}.");
        if (Environment.GetEnvironmentVariable("MEOWCHAN_EXPECT_FOCUS_RACE") == "1")
            Require(immediate && !final, "Expected pre-fix anchor/focus race was not reproduced dynamically.");
        else Require(final, "Delayed reader anchor restore replaced the intended origin focus.");
    }

    private static double Top(ListBox list, string id) => list.GetVisualDescendants().OfType<ListBoxItem>()
        .Where(item => item.DataContext is ArchivePostRow row && row.Id == id)
        .Select(item => item.TranslatePoint(default, list)?.Y ?? double.NaN).DefaultIfEmpty(double.NaN).Single();
    private static async Task Flush(Window window)
    {
        await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Loaded);
        await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Background);
    }
    private static void Click(Control control) => control.RaiseEvent(new RoutedEventArgs(control is MenuItem ? MenuItem.ClickEvent : Button.ClickEvent));
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
