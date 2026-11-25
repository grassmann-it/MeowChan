using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml.Styling;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Desktop.Controls;
using MeowChan.Interfaces;

namespace MeowChan.DesktopTests;

/// <summary>The post navigator retains its position and leaves the Saved media tab and its controls in place.</summary>
[Collection("Gallery viewer construction")]
public sealed class ReaderBackgroundNavigationTests
{
    private static readonly ResourceId Thread = new("synthetic-A", "board-A", "1000");
    private const int Posts = 130;

    [Fact]
    public async Task LegacyPagingShortcutsLeavePostsAndSavedMediaInPlace()
    {
        using ReaderArchiveTestFixture archive = new();
        ArchivedThreadView view = await SeedAndReadAsync(archive);
        archive.Run(page =>
        {
            ReaderArchiveTestFixture.Load(page, view);
            ListBox posts = page.FindControl<ListBox>("ArchivePosts")!;
            TabControl tabs = page.FindControl<TabControl>("ArchiveContentTabs")!;
            ListBox media = page.FindControl<ListBox>("ArchiveMediaTiles")!;
            Assert.Equal("p000", FirstRow(posts));
            tabs.SelectedIndex = 1;
            Assert.True(page.ReaderPostsInBackground);
            object? mediaItems = media.ItemsSource;

            object? items = posts.ItemsSource;
            Press(tabs, Key.PageDown, KeyModifiers.Alt);
            Press(tabs, Key.PageUp, KeyModifiers.Alt);
            Assert.Equal(1, tabs.SelectedIndex);
            Assert.Equal("p000", page.CurrentReaderPostId);
            Assert.Same(items, posts.ItemsSource);
            Assert.Same(mediaItems, media.ItemsSource);
        });
    }

    [Fact]
    public async Task RefreshWithMediaTabOpenPreservesTheScrolledPostAndIntentionalSelection()
    {
        using ReaderArchiveTestFixture archive = new();
        ArchivedThreadView view = await SeedAndReadAsync(archive);
        archive.Run(page =>
        {
            ReaderArchiveTestFixture.Load(page, view);
            ListBox posts = page.FindControl<ListBox>("ArchivePosts")!;
            TabControl tabs = page.FindControl<TabControl>("ArchiveContentTabs")!;
            RealizeCards(posts, scrolledPast: 50);
            var anchor = page.CurrentReaderAnchor;
            Assert.Equal("p050", anchor?.PostId);
            ArchivePostRow selected = (ArchivePostRow)posts.Items[60]!;
            posts.SelectedItem = selected;
            tabs.SelectedIndex = 1;
            ReaderArchiveTestFixture.Load(page, view, resetPage: false);

            Assert.Equal(anchor, page.CurrentReaderAnchor);
            Assert.Equal(1, tabs.SelectedIndex);
            Assert.Equal(Posts, posts.ItemCount);
            Assert.Equal(selected.Id, (posts.SelectedItem as ArchivePostRow)?.Id);
        });
    }

    [Fact]
    public async Task TheLargeReaderStepsAcrossFormerPageBreaksBehindTheMediaTab()
    {
        using ReaderArchiveTestFixture archive = new();
        ArchivedThreadView view = await SeedAndReadAsync(archive);
        archive.Run(page =>
        {
            ReaderArchiveTestFixture.Load(page, view);
            ListBox posts = page.FindControl<ListBox>("ArchivePosts")!;
            TabControl tabs = page.FindControl<TabControl>("ArchiveContentTabs")!;
            Control frame = page.DetachReader();
            tabs.SelectedIndex = 1;
            Assert.Equal("p000", page.CurrentReaderPostId);

            // Post down: the next post becomes the current and the selected one, the tab stays.
            BooruComponentDispatcher.Wait(page.NavigateReaderAsync(1, 0));
            Assert.Equal(1, tabs.SelectedIndex);
            Assert.Equal("p001", (posts.SelectedItem as ArchivePostRow)?.Id);
            Assert.Equal("p001", page.CurrentReaderPostId);
            BooruComponentDispatcher.Wait(page.NavigateReaderAsync(1, 0));
            Assert.Equal("p002", (posts.SelectedItem as ArchivePostRow)?.Id);
            BooruComponentDispatcher.Wait(page.NavigateReaderAsync(-1, 0));
            Assert.Equal("p001", (posts.SelectedItem as ArchivePostRow)?.Id);
            Assert.Equal(1, tabs.SelectedIndex);

            object? items = posts.ItemsSource;
            posts.SelectedIndex = 49;
            BooruComponentDispatcher.Wait(page.NavigateReaderAsync(1, 0));
            Assert.Equal("p050", page.CurrentReaderPostId);
            BooruComponentDispatcher.Wait(page.NavigateReaderAsync(-1, 0));
            Assert.Equal("p049", page.CurrentReaderPostId);
            Assert.Same(items, posts.ItemsSource);
            Assert.Equal(1, tabs.SelectedIndex);
            page.AttachReader(frame);
        });
    }

    [Fact]
    public async Task TheDetachedPaneRunsItsKeysInTheReaderContextAndLeavesNoneBehind()
    {
        using ReaderArchiveTestFixture archive = new();
        ArchivedThreadView view = await SeedAndReadAsync(archive);
        archive.Run(page =>
        {
            ReaderArchiveTestFixture.Load(page, view);
            Grid pane = page.FindControl<Grid>("ArchiveReaderPane")!;
            Border inside = new();
            pane.Children.Add(inside);
            // A tunnel handler of the pane that existed before the large reader was opened runs after the ones the wiring adds (reverse registration order):
            // the pane's own host has entered the context by then, as it has for everything deeper in the pane.
            string? onPane = "unset", deeper = "unset";
            pane.AddHandler(InputElement.KeyDownEvent, (_, _) => onPane = KeyContextHost.Current, RoutingStrategies.Tunnel, handledEventsToo: true);
            inside.AddHandler(InputElement.KeyDownEvent, (_, _) => deeper = KeyContextHost.Current, RoutingStrategies.Tunnel, handledEventsToo: true);
            Control frame = page.DetachReader();
            Press(inside, Key.F, KeyModifiers.None);
            Assert.Equal("reader", onPane); // outside the page the pane's own host enters the context
            Assert.Equal("reader", deeper);
            // Back inside the page the pane's host is gone (the page's host serves it again); whatever was still open is ended with it.
            page.AttachReader(frame);
            Assert.Null(KeyContextHost.Current);
        });
    }

    [Theory]
    [InlineData(1)] // Media
    [InlineData(2)] // References
    public async Task TheAnchorBehindAnotherTabIsTheVisibleCardWhileNothingMovedInTheBackground(int tab)
    {
        using ReaderArchiveTestFixture archive = new();
        ArchivedThreadView view = await SeedAndReadAsync(archive);
        archive.Run(page =>
        {
            ReaderArchiveTestFixture.Load(page, view);
            ListBox posts = page.FindControl<ListBox>("ArchivePosts")!;
            TabControl tabs = page.FindControl<TabControl>("ArchiveContentTabs")!;
            RealizeCards(posts, scrolledPast: 5); // the user scrolled the list: p005 is the first card in view, p003 and p004 are above
            (string PostId, double Top)? onPosts = page.CurrentReaderAnchor;
            Assert.Equal("p005", onPosts?.PostId);
            tabs.SelectedIndex = tab;
            Assert.Equal(onPosts, page.CurrentReaderAnchor); // as in 1.0.x: a refresh or a quote-back returns to the scroll position, not to the selected post
        });
    }

    [Fact]
    public async Task TheAnchorAfterABackgroundStepIsTheCurrentPost()
    {
        using ReaderArchiveTestFixture archive = new();
        ArchivedThreadView view = await SeedAndReadAsync(archive);
        archive.Run(page =>
        {
            ReaderArchiveTestFixture.Load(page, view);
            ListBox posts = page.FindControl<ListBox>("ArchivePosts")!;
            TabControl tabs = page.FindControl<TabControl>("ArchiveContentTabs")!;
            Control frame = page.DetachReader();
            RealizeCards(posts, scrolledPast: 5);
            tabs.SelectedIndex = 1;
            BooruComponentDispatcher.Wait(page.NavigateReaderAsync(1, 0)); // a background step waits for alignment: the realized cards are stale
            Assert.Equal(("p001", 0d), page.CurrentReaderAnchor);
            BooruComponentDispatcher.Wait(page.NavigateReaderAsync(1, 0));
            Assert.Equal(("p002", 0d), page.CurrentReaderAnchor);
            Assert.Equal(Posts, posts.ItemCount);
            page.AttachReader(frame);
        });
    }

    [Theory]
    [InlineData("tiles")]
    [InlineData("grid")]
    [InlineData("posts")]
    public async Task AltPageKeysFromListItemsKeepThePostCollectionAndSelectedTab(string from)
    {
        using ReaderArchiveTestFixture archive = new();
        ArchivedThreadView view = await SeedAndReadAsync(archive);
        archive.Run(page =>
        {
            ReaderArchiveTestFixture.Load(page, view);
            ListBox posts = page.FindControl<ListBox>("ArchivePosts")!;
            TabControl tabs = page.FindControl<TabControl>("ArchiveContentTabs")!;
            ListBox tiles = page.FindControl<ListBox>("ArchiveMediaTiles")!;
            DataGrid grid = page.FindControl<DataGrid>("ArchiveMediaGrid")!;
            tabs.SelectedIndex = from == "posts" ? 0 : 1;
            object[] mediaItems = { "m0", "m1", "m2" };
            tiles.ItemsSource = mediaItems;
            grid.ItemsSource = mediaItems;
            Control source = from switch
            {
                "tiles" => Hosted(RealizeItems(tiles, mediaItems), tiles, tabs),
                "posts" => Hosted(RealizeCards(posts, scrolledPast: 0), posts, tabs),
                _ => Hosted(grid, grid, tabs), // a DataGrid builds its rows only in its theme: the grid itself stands for the focused row
            };
            int tab = tabs.SelectedIndex;
            Assert.Equal("p000", FirstRow(posts));

            object? items = posts.ItemsSource;
            Press(source, Key.PageDown, KeyModifiers.Alt);
            Press(source, Key.PageUp, KeyModifiers.Alt);
            Assert.Same(items, posts.ItemsSource);
            Assert.Equal(Posts, posts.ItemCount);
            Assert.Equal(tab, tabs.SelectedIndex);
            Assert.Same(mediaItems, tiles.ItemsSource);
            Assert.Same(mediaItems, grid.ItemsSource);
            if (from != "posts") Assert.Equal("p000", page.CurrentReaderPostId);
        });
    }

    [Fact]
    public async Task MediaSortAndTileKeysDoNotMoveThePostNavigator()
    {
        using ReaderArchiveTestFixture archive = new();
        ArchivedThreadView view = await SeedAndReadAsync(archive);
        archive.Run(page =>
        {
            ReaderArchiveTestFixture.Load(page, view);
            ListBox posts = page.FindControl<ListBox>("ArchivePosts")!;
            TabControl tabs = page.FindControl<TabControl>("ArchiveContentTabs")!;
            ListBox tiles = page.FindControl<ListBox>("ArchiveMediaTiles")!;
            ComboBox sort = page.FindControl<ComboBox>("ArchiveMediaSort")!;
            tabs.SelectedIndex = 1;
            object[] mediaItems = { "m0", "m1", "m2" };
            tiles.ItemsSource = mediaItems;
            Control tile = Hosted(RealizeItems(tiles, mediaItems), tiles, tabs);
            Hosted(sort, sort, tabs);
            Press(tile, Key.PageDown, KeyModifiers.None);
            Press(tile, Key.PageDown, KeyModifiers.Shift);
            Press(tile, Key.PageDown, KeyModifiers.Control);
            Assert.Equal("p000", FirstRow(posts));
            Press(sort, Key.PageDown, KeyModifiers.Alt); // a combo box keeps its keys in every reader
            Press(sort, Key.PageUp, KeyModifiers.Alt);
            Assert.Equal("p000", FirstRow(posts));
            Assert.Equal(1, tabs.SelectedIndex);
            Press(tile, Key.PageDown, KeyModifiers.Alt);
            Assert.Equal("p000", page.CurrentReaderPostId);
            Assert.Equal(Posts, posts.ItemCount);
        });
    }

    // ---- helpers ----

    private const double CardHeight = 40;

    // The tab control has no template here, so its content has no visual parent and a key never routes through it. The content gets the tab control as
    // visual parent (where the page's theme puts it), so the events of the tab's lists run through the tab control's handlers as in the application.
    private static Control Hosted(Control source, Control content, TabControl tabs)
    {
        typeof(Visual).GetMethod("SetVisualParent", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(content, new object?[] { tabs });
        return source;
    }

    // The test host has no theme and no window: a list gets a bare template (its items presenter inside a panel that may be shifted up, as a scroll
    // offset would), plain containers and fixed-height content, and is measured by hand so its containers exist with real bounds.
    private static ListBoxItem RealizeItems(ListBox list, IReadOnlyList<object> items, int scrolledPast = 0)
    {
        ItemsPresenter presenter = new() { Name = "PART_ItemsPresenter", ItemsPanel = new FuncTemplate<Panel?>(() => new StackPanel()) };
        list.Template = new FuncControlTemplate<ListBox>((_, scope) => new Panel
        {
            Children = { presenter },
            Margin = new Thickness(0, -scrolledPast * CardHeight, 0, 0)
        });
        list.ItemContainerTheme = new ControlTheme(typeof(ListBoxItem))
        {
            Setters = { new Setter(TemplatedControl.TemplateProperty, new FuncControlTemplate<ListBoxItem>((_, _) => new Border { Height = CardHeight })) }
        };
        list.ItemTemplate = new FuncDataTemplate<object>((_, _) => new Border { Height = CardHeight });
        list.Measure(new Size(400, 300));
        list.Arrange(new Rect(0, 0, 400, 300));
        return list.GetVisualDescendants().OfType<ListBoxItem>().First();
    }

    private static ListBoxItem RealizeCards(ListBox posts, int scrolledPast) =>
        RealizeItems(posts, ((IEnumerable<ArchivePostRow>)posts.ItemsSource!).Cast<object>().ToList(), scrolledPast);

    private static void Press(Control target, Key key, KeyModifiers modifiers) =>
        target.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key, KeyModifiers = modifiers, Source = target });

    private static string? FirstRow(ListBox posts) => (posts.ItemsSource as IEnumerable<ArchivePostRow>)?.FirstOrDefault()?.Id;

    private static Task<ArchivedThreadView> SeedAndReadAsync(ReaderArchiveTestFixture archive)
    {
        CapturedPost[] posts = Enumerable.Range(0, Posts).Select(i => new CapturedPost(
            "p" + i.ToString("D3"), i, PostFieldPresence.All, Html: "synthetic stored text " + i)).ToArray();
        return archive.CaptureAsync(Thread, "Synthetic background navigation", posts);
    }
}
