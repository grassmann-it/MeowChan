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
using Avalonia.Platform;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Desktop.Controls;
using MeowChan.Interfaces;
using MeowChan.Persistence;
using ArchiveCatalog = MeowChan.Archive.ArchiveCatalog;

namespace MeowChan.DesktopTests;

/// <summary>
/// Decision of 16.11.2026: with the Media tab selected, paging and the post keys of the reader still move through the posts, behind the tab. The current
/// post, the page and the reading position follow as on the Posts tab; the Media tab stays selected and its list is left alone (no re-render, no focus move).
/// Also: the large reader's pane carries the "reader" key context itself once the shell hosts it outside the page.
/// </summary>
[Collection("Gallery viewer construction")]
public sealed class ReaderBackgroundPagingTests
{
    private static readonly ResourceId Thread = new("synthetic-A", "board-A", "1000");
    private const int Posts = 130; // three pages of the default reader page size

    [Fact]
    public async Task PagingWithTheMediaTabOpenMovesThePostsAndKeepsTheMediaTab()
    {
        ArchivedThreadView view = await SeedAndReadAsync();
        BooruComponentDispatcher.Run(() => WithPage(view, page =>
        {
            ListBox posts = page.FindControl<ListBox>("ArchivePosts")!;
            TabControl tabs = page.FindControl<TabControl>("ArchiveContentTabs")!;
            ListBox media = page.FindControl<ListBox>("ArchiveMediaTiles")!;
            Assert.Equal("p000", FirstRow(posts));
            tabs.SelectedIndex = 1;
            Assert.True(page.ReaderPostsInBackground);
            object? mediaItems = media.ItemsSource;

            // The inline reader: the gesture arrives at the tab control (the media list did not handle it).
            Press(tabs, Key.PageDown, KeyModifiers.Alt);
            Assert.Equal(1, tabs.SelectedIndex);
            Assert.Equal("p050", FirstRow(posts));
            Assert.Equal("p050", page.CurrentReaderPostId);
            Assert.Same(mediaItems, media.ItemsSource); // the media list is not rebuilt: its focus and selection stay

            Press(tabs, Key.PageUp, KeyModifiers.Alt);
            Assert.Equal(1, tabs.SelectedIndex);
            Assert.Equal("p000", FirstRow(posts));
            Assert.Same(mediaItems, media.ItemsSource);
        }));
    }

    [Fact]
    public async Task TheLargeReaderPagesAndStepsThroughThePostsBehindTheMediaTab()
    {
        ArchivedThreadView view = await SeedAndReadAsync();
        BooruComponentDispatcher.Run(() => WithPage(view, page =>
        {
            ListBox posts = page.FindControl<ListBox>("ArchivePosts")!;
            TabControl tabs = page.FindControl<TabControl>("ArchiveContentTabs")!;
            Control frame = page.DetachReader();
            Grid pane = page.FindControl<Grid>("ArchiveReaderPane")!;
            tabs.SelectedIndex = 1;
            Assert.Equal("p000", page.CurrentReaderPostId);

            // Post down: the next post becomes the current and the selected one, the tab stays.
            Task step = page.NavigateReaderAsync(1, 0);
            Assert.True(step.IsCompletedSuccessfully);
            Assert.Equal(1, tabs.SelectedIndex);
            Assert.Equal("p001", (posts.SelectedItem as ArchivePostRow)?.Id);
            Assert.Equal("p001", page.CurrentReaderPostId);
            Assert.True(page.NavigateReaderAsync(1, 0).IsCompletedSuccessfully);
            Assert.Equal("p002", (posts.SelectedItem as ArchivePostRow)?.Id);
            Assert.True(page.NavigateReaderAsync(-1, 0).IsCompletedSuccessfully);
            Assert.Equal("p001", (posts.SelectedItem as ArchivePostRow)?.Id);
            Assert.Equal(1, tabs.SelectedIndex);

            // Page forward and back with the pager key on the pane (the large reader's tunnel handler owns it).
            Press(pane, Key.PageDown, KeyModifiers.Alt);
            Assert.Equal(1, tabs.SelectedIndex);
            Assert.Equal("p050", FirstRow(posts));
            Assert.Equal("p050", page.CurrentReaderPostId);
            // A post move after paging continues from the new page and may cross back over its edge, still behind the tab.
            Assert.True(page.NavigateReaderAsync(-1, 0).IsCompletedSuccessfully);
            Assert.Equal("p049", page.CurrentReaderPostId);
            Assert.Equal("p000", FirstRow(posts));
            Assert.Equal(1, tabs.SelectedIndex);
            Press(pane, Key.PageDown, KeyModifiers.Alt);
            Press(pane, Key.PageDown, KeyModifiers.Alt);
            Assert.Equal("p100", FirstRow(posts));
            Press(pane, Key.PageDown, KeyModifiers.Alt); // the last page: nothing further
            Assert.Equal("p100", FirstRow(posts));
            Press(pane, Key.PageUp, KeyModifiers.Alt);
            Assert.Equal("p050", FirstRow(posts));
            Assert.Equal(1, tabs.SelectedIndex);
            page.AttachReader(frame);
        }));
    }

    [Fact]
    public async Task TheDetachedPaneRunsItsKeysInTheReaderContextAndLeavesNoneBehind()
    {
        ArchivedThreadView view = await SeedAndReadAsync();
        BooruComponentDispatcher.Run(() => WithPage(view, page =>
        {
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
        }));
    }

    [Theory]
    [InlineData(1)] // Media
    [InlineData(2)] // References
    public async Task TheAnchorBehindAnotherTabIsTheVisibleCardWhileNothingMovedInTheBackground(int tab)
    {
        ArchivedThreadView view = await SeedAndReadAsync();
        BooruComponentDispatcher.Run(() => WithPage(view, page =>
        {
            ListBox posts = page.FindControl<ListBox>("ArchivePosts")!;
            TabControl tabs = page.FindControl<TabControl>("ArchiveContentTabs")!;
            RealizeCards(posts, scrolledPast: 5); // the user scrolled the list: p005 is the first card in view, p003 and p004 are above
            (string PostId, double Top)? onPosts = page.CurrentReaderAnchor;
            Assert.Equal("p005", onPosts?.PostId);
            tabs.SelectedIndex = tab;
            Assert.Equal(onPosts, page.CurrentReaderAnchor); // as in 1.0.x: a refresh or a quote-back returns to the scroll position, not to the selected post
        }));
    }

    [Fact]
    public async Task TheAnchorAfterABackgroundStepOrPageIsTheCurrentPostOrThePagesFirstNeverAStaleCard()
    {
        ArchivedThreadView view = await SeedAndReadAsync();
        BooruComponentDispatcher.Run(() => WithPage(view, page =>
        {
            ListBox posts = page.FindControl<ListBox>("ArchivePosts")!;
            TabControl tabs = page.FindControl<TabControl>("ArchiveContentTabs")!;
            Control frame = page.DetachReader();
            RealizeCards(posts, scrolledPast: 5);
            tabs.SelectedIndex = 1;
            Assert.True(page.NavigateReaderAsync(1, 0).IsCompletedSuccessfully); // a background step waits for alignment: the realized cards are stale
            Assert.Equal(("p001", 0d), page.CurrentReaderAnchor);
            Press(page.FindControl<Grid>("ArchiveReaderPane")!, Key.PageDown, KeyModifiers.Alt); // a background page: the old cards no longer belong to the list
            Assert.Equal("p050", FirstRow(posts));
            Assert.Equal(("p050", 0d), page.CurrentReaderAnchor);
            page.AttachReader(frame);
        }));
    }

    [Theory]
    [InlineData("tiles")]
    [InlineData("grid")]
    [InlineData("posts")]
    public async Task AltPageDownAndUpFromAFocusedListItemPageThePostsAndLeaveTheMediaListAlone(string from)
    {
        ArchivedThreadView view = await SeedAndReadAsync();
        BooruComponentDispatcher.Run(() => WithPage(view, page =>
        {
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

            Press(source, Key.PageDown, KeyModifiers.Alt);
            Assert.Equal("p050", FirstRow(posts));
            Assert.Equal(tab, tabs.SelectedIndex);
            if (from != "posts") Assert.Same(mediaItems, tiles.ItemsSource); // the Posts tab refreshes the media list with its page
            Assert.Null(tiles.SelectedItem);
            // A page change rebuilds the post cards: the focused item is the first card of the new page.
            if (from == "posts") source = (Control?)posts.GetVisualDescendants().OfType<ListBoxItem>().FirstOrDefault() ?? posts;
            Press(source, Key.PageUp, KeyModifiers.Alt);
            Assert.Equal("p000", FirstRow(posts));
            Assert.Equal(tab, tabs.SelectedIndex);
            if (from != "posts") Assert.Same(mediaItems, tiles.ItemsSource);
        }));
    }

    [Fact]
    public async Task PlainPageDownOnATileDoesNotPageAndTheMediaSortKeepsItsAltKeys()
    {
        ArchivedThreadView view = await SeedAndReadAsync();
        BooruComponentDispatcher.Run(() => WithPage(view, page =>
        {
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
            Press(tile, Key.PageDown, KeyModifiers.Alt); // the same key from a tile pages
            Assert.Equal("p050", FirstRow(posts));
        }));
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

    private static void WithPage(ArchivedThreadView view, Action<MeowChan.ArchivePage> test)
    {
        using IDisposable scope = AvaloniaLocator.EnterScope();
        AvaloniaLocator.CurrentMutable.Bind<ICursorFactory>().ToConstant(new DeadAffordanceGuard.RecordingCursorFactory());
        TokenApplication app = new();
        AvaloniaLocator.CurrentMutable.Bind<Application>().ToConstant(app);
        app.Initialize();
        MeowChan.ArchivePage page = new(_ => Task.FromResult<ArchiveReader>(null!), (_, _) => Task.CompletedTask, _ => { });
        try
        {
            page.IsReadable = true;
            // The thread is handed over prepared: the inline reader renders it without any await, so the load completes in this turn.
            Task load = page.LoadThreadAsync(view.Summary.Id, true, view);
            Assert.True(load.IsCompletedSuccessfully);
            Assert.Equal(Posts, page.CurrentThread?.PostCount);
            test(page);
        }
        finally { (page as IDisposable)?.Dispose(); }
    }

    private sealed class TokenApplication : Application
    {
        public override void Initialize() =>
            Styles.Add(new StyleInclude(new Uri("avares://MeowChan/")) { Source = new Uri("avares://MeowChan/Desktop/WorkspaceStyles.axaml") });
    }

    private static async Task<ArchivedThreadView> SeedAndReadAsync()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        await using (ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(temp.Path, lease))
        {
            CapturedPost[] posts = Enumerable.Range(0, Posts).Select(i => new CapturedPost(
                "p" + i.ToString("D3"), i, PostFieldPresence.All, Html: "synthetic stored text " + i)).ToArray();
            await catalog.CaptureAsync(new(Thread, "https://example.invalid/not-acquired", "Synthetic background paging",
                ThreadSourceState.NotFound, DateTimeOffset.UnixEpoch, Path.Combine(temp.Path, "uncreated-media"), posts));
        }
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(temp.Path);
        return (await reader.ReadThreadAsync(Thread))!;
    }
}
