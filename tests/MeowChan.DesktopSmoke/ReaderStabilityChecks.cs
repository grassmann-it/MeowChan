using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Presenters;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Interfaces;

namespace MeowChan.DesktopSmoke;

internal static class ReaderStabilityChecks
{
    private static readonly ResourceId Thread = new("4chan", "g", "9998");

    internal static Task SeedAsync(ArchiveCatalog catalog, string media)
    {
        CapturedPost[] posts = Enumerable.Range(1000, 168).Select((post, ordinal) => new CapturedPost(
            post.ToString(), ordinal, PostFieldPresence.All,
            Html: "<p>Reader mixed post " + post + ". " + string.Join(" ", Enumerable.Repeat(
                "Variable reader text remains selectable and locally readable.", 1 + ordinal * 37 % 160)) + "</p>",
            Attachments: ordinal % 3 == 0
                ? new[] { new CapturedAttachment(0, "shared", OriginalFileName: "reader.png") }
                : Array.Empty<CapturedAttachment>())).ToArray();
        return SeedCoreAsync();
        async Task SeedCoreAsync()
        {
            posts = await StrictMediaFixture.BindAsync(media, Thread, posts);
            await catalog.CaptureAsync(new CapturedThreadSnapshot(Thread,
                "https://boards.4chan.org/g/thread/9998", "Reader mixed navigation", ThreadSourceState.Archived,
                DateTimeOffset.UtcNow, media, posts));
        }
    }

    internal static async Task RunAsync(MainWindow window)
    {
        double width = window.Width, height = window.Height;
        PixelPoint position = window.Position;
        try { await RunCoreAsync(window); }
        finally
        {
            // Keep the following fullscreen/close scenario in its own native
            // geometry, particularly when 2× makes our wide fixture oversized.
            window.Width = width; window.Height = height;
            window.Position = position;
            await Program.WaitUntilAsync(() => PresentationChecks.MatchesRequestedSize(window, width, height));
            await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Background);
        }
    }

    private static async Task RunCoreAsync(MainWindow window)
    {
        window.Width = 1915; window.Height = 1045;
        Click(window.FindControl<MenuItem>("ArchiveMenuItem")!);
        ArchivePage page = window.ArchivePage;
        await Program.WaitUntilAsync(() => !page.IsBusy);
        page.FindControl<TextBox>("ArchiveSearchText")!.Text = "Reader mixed navigation";
        await page.ApplySearchAsync();
        page.FindControl<ListBox>("ArchiveThreads")!.SelectedIndex = 0;
        Grid pane = page.FindControl<Grid>("ArchiveReaderPane")!;
        KeyboardNavigationMode originalTab = KeyboardNavigation.GetTabNavigation(pane);
        Click(page.FindControl<Button>("ArchiveReadPosts")!);
        ContentControl host = window.FindControl<ContentControl>("ReaderHost")!;
        await Program.WaitUntilAsync(() => host.IsVisible && !page.IsBusy && page.CurrentThread?.Summary.Id == Thread);
        await page.MediaLoadTask;
        ListBox posts = page.FindControl<ListBox>("ArchivePosts")!;

        await CheckDelayedReplyCountsAsync(window, page, posts);

        TracePagination("before-focus-1000", window, page, posts);
        await page.FocusPostAsync("1000");
        TracePagination("after-focus-1000", window, page, posts);
        Click(page.FindControl<Button>("NextPostsButton")!);
        TracePagination("after-next-page-click", window, page, posts);
        await Flush(window);
        TracePagination("after-page-flush", window, page, posts);
        // Preserve Press's Focus + routed Down sequence; observe the actual
        // source and task without another wait, retry, or state adjustment.
        Button nextPosts = page.FindControl<Button>("NextPostsButton")!;
        Task previousNavigation = page.ReaderNavigationTask;
        bool focusRequested = nextPosts.Focus();
        TracePagination("after-down-source-focus", window, page, posts, new { focusRequested });
        KeyEventArgs paginationDown = new() { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Down };
        nextPosts.RaiseEvent(paginationDown);
        TracePagination("after-down-event", window, page, posts, new
        {
            paginationDown.Handled,
            source = (paginationDown.Source as Control)?.Name ?? paginationDown.Source?.GetType().Name,
            navigationTaskChanged = !ReferenceEquals(previousNavigation, page.ReaderNavigationTask)
        });
        await page.ReaderNavigationTask;
        TracePagination("after-navigation-task", window, page, posts);
        await Flush(window);
        TracePagination("before-pagination-gate", window, page, posts);
        Require((posts.SelectedItem as ArchivePostRow)?.Id == "1051", "Pagination reused a previous page's navigation post.");
        Visible(posts, "1051", "pagination-down");

        await page.FocusPostAsync("1000");
        posts.SelectedItem = posts.ItemsSource!.Cast<ArchivePostRow>().ElementAt(1);
        Require(page.FindControl<Button>("ReaderPreviousPost")!.IsEnabled, "Native selection did not update previous-post availability.");
        posts.SelectedIndex = 0;
        Require(!page.FindControl<Button>("ReaderPreviousPost")!.IsEnabled, "Native first-post selection left previous-post enabled.");

        // Events originate at the actually focused list descendant. Assert
        // geometry after a Loaded pass and again after Background settles.
        for (int index = 1; index < 168; index++) await Navigate(Key.Down, (1000 + index).ToString());
        for (int index = 166; index >= 0; index--) await Navigate(Key.Up, (1000 + index).ToString());
        Require(posts.ItemsPanelRoot is VirtualizingStackPanel, "Reader navigation disabled virtualization.");
        Require(posts.GetVisualDescendants().OfType<ListBoxItem>().Count() < 50,
            "Mixed reader realized an entire 50-post page.");

        ScrollViewer wheelTarget = posts.GetVisualDescendants().OfType<ScrollViewer>().First();
        using (Pointer pointer = new(Pointer.GetNextFreeId(), PointerType.Mouse, true))
        {
            Point point = wheelTarget.TranslatePoint(new Point(40, 40), window)!.Value;
            wheelTarget.RaiseEvent(new PointerWheelEventArgs(wheelTarget, pointer, window, point, 0,
                new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.Other),
                KeyModifiers.None, new Vector(0, -6)));
        }
        await Flush(window);
        for (int count = 0; count < 20; count++) Press(posts, Key.Down);
        await page.ReaderNavigationTask;
        await Flush(window);
        Require((posts.SelectedItem as ArchivePostRow)?.Id == "1020", "Rapid reader arrows lost or duplicated a navigation step.");
        Visible(posts, "1020", "wheel then rapid arrows");

        // Native scopes must still own their arrows in the detached reader.
        TabControl tabs = page.FindControl<TabControl>("ArchiveContentTabs")!;
        TabItem header = tabs.GetVisualDescendants().OfType<TabItem>().First();
        Require(page.ReaderArrowBelongsToNativeControl(header), "Tab header lost native arrows.");
        string? selection = (posts.SelectedItem as ArchivePostRow)?.Id;
        Press(header, Key.Right);
        await Flush(window);
        Require((posts.SelectedItem as ArchivePostRow)?.Id == selection, "Header arrows changed the reader post.");
        tabs.SelectedIndex = 1;
        foreach (Control media in new Control[] { page.FindControl<ListBox>("ArchiveMediaTiles")!, page.FindControl<DataGrid>("ArchiveMediaGrid")! })
        {
            Require(page.ReaderArrowBelongsToNativeControl(media), "Media list lost native arrows.");
            Press(media, Key.Down);
            await Flush(window);
            Require((posts.SelectedItem as ArchivePostRow)?.Id == selection, "Media arrows changed the reader post.");
        }
        tabs.SelectedIndex = 0;
        foreach (double width in new[] { 760d, 900d, 1915d })
        {
            window.Width = width;
            await Program.WaitUntilAsync(() => PresentationChecks.MatchesRequestedSize(window, width, window.Height));
            await Flush(window);
            await page.FocusPostAsync("1048");
            await Flush(window);
            ArchivePostRow mediaRow = posts.ItemsSource!.Cast<ArchivePostRow>().Single(row => row.Id == "1048");
            ReaderPostLayout layout = posts.ContainerFromItem(mediaRow)!.GetVisualDescendants().OfType<ReaderPostLayout>().Single();
            bool beside = layout.Bounds.Width >= 760;
            Console.WriteLine("SMOKE responsive reader layout: " + System.Text.Json.JsonSerializer.Serialize(new
            {
                requestedWidth = width, requestedHeight = window.Height,
                client = window.ClientSize.ToString(), frame = window.FrameSize?.ToString(),
                windowBounds = window.Bounds.ToString(), scale = window.RenderScaling,
                layoutBounds = layout.Bounds.ToString(), layoutDesired = layout.DesiredSize.ToString(),
                mediaBounds = layout.Children[0].Bounds.ToString(), mediaDesired = layout.Children[0].DesiredSize.ToString(),
                bodyBounds = layout.Children[1].Bounds.ToString(), bodyDesired = layout.Children[1].DesiredSize.ToString(),
                largeReader = layout.GetValue(ReaderPostLayout.LargeReaderProperty), beside,
                mediaCapped = layout.Children[0].Bounds.Width <= 376 + 1,
                bodyMatchesThreshold = beside ? layout.Children[1].Bounds.X > 0
                    : layout.Children[1].Bounds.X == 0 && layout.Children[1].Bounds.Y > 0
            }));
            Require(layout.Children[0].Bounds.Width <= 376 + 1
                && (beside ? layout.Children[1].Bounds.X > 0 : layout.Children[1].Bounds.X == 0 && layout.Children[1].Bounds.Y > 0),
                "Responsive post layout did not preserve its threshold or capped media measure.");
            await Navigate(Key.Down, "1049");
            await Navigate(Key.Down, "1050");
            await Navigate(Key.Up, "1049");
        }
        Click(page.FindControl<Button>("ArchiveCloseReader")!);
        await Flush(window);
        Require(KeyboardNavigation.GetTabNavigation(pane) == originalTab,
            "Attach retained the detached reader's Tab-cycle override.");
        // A deliberately set pre-existing value must survive another lifecycle.
        pane.SetValue(KeyboardNavigation.TabNavigationProperty, KeyboardNavigationMode.Continue);
        Click(page.FindControl<Button>("ArchiveReadPosts")!);
        await Program.WaitUntilAsync(() => host.IsVisible);
        Require(KeyboardNavigation.GetTabNavigation(pane) == KeyboardNavigationMode.Cycle, "Detached reader has no cyclic Tab scope.");
        Click(page.FindControl<Button>("ArchiveCloseReader")!);
        Require(KeyboardNavigation.GetTabNavigation(pane) == KeyboardNavigationMode.Continue,
            "Attach lost a pre-existing local Tab value.");
        pane.ClearValue(KeyboardNavigation.TabNavigationProperty);
        Console.WriteLine("SMOKE mixed reader stability: 168 variable posts, all forward/backward boundaries, settled target visibility, native scopes, pagination, selection availability, virtualized realization and reversible Tab scope passed.");

        async Task Navigate(Key key, string expected)
        {
            Control focused = window.FocusManager!.GetFocusedElement() as Control ?? posts;
            if (!focused.GetSelfAndVisualAncestors().Contains(posts)) focused = posts;
            Press(focused, key);
            await page.ReaderNavigationTask;
            await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Loaded);
            await Program.WaitUntilAsync(() => (posts.SelectedItem as ArchivePostRow)?.Id == expected);
            // Loaded continuations must complete before the first assertion.
            await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Loaded);
            Visible(posts, expected, key + " loaded");
            await Program.WaitUntilAsync(() => posts.ItemsSource!.Cast<ArchivePostRow>().All(row => row.BacklinkCount.HasValue));
            await Flush(window);
            Visible(posts, expected, key + " settled");
        }
    }

    private static async Task CheckDelayedReplyCountsAsync(MainWindow window, ArchivePage page, ListBox posts)
    {
        await Resize(900);
        await page.FocusPostAsync("1048");
        await Resize(1915);
        await page.FocusPostAsync("1050");
        await Program.WaitUntilAsync(() => posts.ItemsSource!.Cast<ArchivePostRow>().All(row => row.BacklinkCount.HasValue));
        await Flush(window);
        Func<CancellationToken, Task>? previous = page.BeforeInlineReplyCountsPublicationAsync;
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        page.BeforeInlineReplyCountsPublicationAsync = async token =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
        };
        try
        {
            Task navigation = page.FocusPostAsync("1049");
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            // Navigation remains usable while the current page's counts are pending.
            await navigation.WaitAsync(TimeSpan.FromSeconds(5));
            await Flush(window);
            Require(posts.ItemsSource!.Cast<ArchivePostRow>().All(row => !row.BacklinkCount.HasValue),
                "Delayed-count fixture did not hold current-page count publication.");
            Require((posts.SelectedItem as ArchivePostRow)?.Id == "1049", "Delayed counts changed the selected target.");
            Visible(posts, "1049", "reply counts held");
            Console.WriteLine("SMOKE delayed reply counts: initial target visible; releasing counts.");
            release.TrySetResult();
            await Program.WaitUntilAsync(() => posts.ItemsSource!.Cast<ArchivePostRow>().All(row => row.BacklinkCount.HasValue));
            await Flush(window);
            Require((posts.SelectedItem as ArchivePostRow)?.Id == "1049", "Count publication changed the selected target.");
            Visible(posts, "1049", "reply counts settled");
            Console.WriteLine("SMOKE delayed reply counts: settled target visible.");
        }
        finally
        {
            release.TrySetResult();
            page.BeforeInlineReplyCountsPublicationAsync = previous;
        }
        async Task Resize(double width)
        {
            double height = window.Bounds.Height;
            window.Width = width;
            await Program.WaitUntilAsync(() => PresentationChecks.MatchesRequestedSize(window, width, height));
            await Flush(window);
        }
    }

    private static void TracePagination(string stage, MainWindow window, ArchivePage page, ListBox posts, object? action = null)
    {
        try
        {
            ArchivePostRow[] rows = posts.ItemsSource?.Cast<ArchivePostRow>().ToArray() ?? Array.Empty<ArchivePostRow>();
            Control? focused = window.FocusManager?.GetFocusedElement() as Control;
            Button? next = page.FindControl<Button>("NextPostsButton");
            Task navigation = page.ReaderNavigationTask;
            object? State(string field) => typeof(ArchivePage).GetField(field,
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.GetValue(page);
            Console.WriteLine("SMOKE reader pagination state: " + System.Text.Json.JsonSerializer.Serialize(new
            {
                stage, action, page.IsBusy, page.IsMediaLoading, page.IsReaderDialogOpen, page.ReaderShowsPosts,
                thread = page.CurrentThread?.Summary.Id.ToString(), postCount = page.CurrentThread?.PostCount,
                selectedId = (posts.SelectedItem as ArchivePostRow)?.Id, posts.SelectedIndex,
                selectedBelongsToItems = rows.Any(row => ReferenceEquals(row, posts.SelectedItem)),
                itemCount = rows.Length, firstId = rows.FirstOrDefault()?.Id, lastId = rows.LastOrDefault()?.Id,
                postOffset = State("_postOffset"), retainedPostId = State("_readerPostId"),
                contentRequest = State("_contentRequest"), focusRequest = State("_readerFocusRequest"),
                interactionRevision = State("_readerInteractionRevision"), largeReader = State("_largeReader"), ended = State("_ended"),
                navigationTask = new { navigation.Id, status = navigation.Status.ToString(), navigation.IsCompleted },
                mediaTask = new { page.MediaLoadTask.Id, status = page.MediaLoadTask.Status.ToString(), page.MediaLoadTask.IsCompleted },
                focus = focused == null ? null : new { type = focused.GetType().Name, focused.Name,
                    root = TopLevel.GetTopLevel(focused)?.GetType().Name, focused.IsEffectivelyVisible, focused.IsEffectivelyEnabled },
                nextButton = next == null ? null : new { next.Name, next.IsVisible, next.IsEffectivelyVisible,
                    next.IsEnabled, next.IsEffectivelyEnabled, next.Focusable, attached = next.IsAttachedToVisualTree(),
                    nativeArrowOwner = page.ReaderArrowBelongsToNativeControl(next),
                    ancestors = next.GetSelfAndVisualAncestors().Select(node => node.GetType().Name + ":" + (node as Control)?.Name).ToArray() },
                tabIndex = page.FindControl<TabControl>("ArchiveContentTabs")?.SelectedIndex,
                pageLabel = page.FindControl<TextBlock>("ArchivePostPage")?.Text,
                readerHostVisible = window.FindControl<ContentControl>("ReaderHost")?.IsVisible
            }));
        }
        catch (Exception exception)
        {
            // Diagnostics must not replace the unchanged selection/visibility gate.
            Console.WriteLine("SMOKE reader pagination diagnostic failed: " + exception.GetType().Name + ": " + exception.Message);
        }
    }

    private static void Visible(ListBox posts, string expected, string phase)
    {
        ArchivePostRow row = posts.ItemsSource!.Cast<ArchivePostRow>().Single(row => row.Id == expected);
        Control? target = posts.ContainerFromItem(row);
        ScrollContentPresenter viewport = posts.GetVisualDescendants().OfType<ScrollContentPresenter>().First();
        Point? point = target?.TranslatePoint(default, viewport);
        Require(target != null && point is Point p && p.Y >= -1 && p.Y < viewport.Bounds.Height && p.Y + target.Bounds.Height > 0,
            $"Mixed reader target {expected} is invisible ({phase}); target={point}, height={target?.Bounds.Height}, viewport={viewport.Bounds.Height}.");
    }

    private static async Task Flush(Window window)
    {
        await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Loaded);
        await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Background);
        window.UpdateLayout();
    }
    private static void Press(Control control, Key key) { control.Focus(); control.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key }); }
    private static void Click(Control control) => control.RaiseEvent(new RoutedEventArgs(control is MenuItem ? MenuItem.ClickEvent : Button.ClickEvent));
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
