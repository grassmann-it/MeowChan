using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Desktop.Booru;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Motion;
using MeowChan.Gallery;
using MeowChan.Interfaces;
using MeowChan.PluginSdk;
using MeowChan.Storage;
using MeowChan.Tags;
using MeowChan.Tests.Shared;
using Semi.Avalonia.DataGrid;
using SkiaSharp;

namespace MeowChan.DesktopTests;

[Collection("Avalonia global state")]
public sealed class ViewportFrameClipTests
{
    private const int ItemCount = 36;
    private static readonly ResourceId ThreadId = new("offline", "test", "1");
    private enum Edge { Top, Bottom, Left, Right }
    private enum BooruDecoration { Frame, OpenGallery, Select, RatingTip, RatingTipTooTall }

    [Theory]
    [InlineData("Booru List", 1)]
    [InlineData("Booru Grid", 1)]
    [InlineData("Booru Gallery", 1)]
    [InlineData("Media Grid", 1)]
    [InlineData("Media Grid", 2)]
    [InlineData("Archive thread cards", 1)]
    [InlineData("Archive thread cards", 2)]
    [InlineData("Archive thread table", 1)]
    [InlineData("Reader Saved media Grid", 1)]
    [InlineData("Reader Saved media Table", 1)]
    [InlineData("Tagging List", 1)]
    [InlineData("Tagging List", 2)]
    [InlineData("Tagging Grid", 1)]
    [InlineData("Gallery Filmstrip", 1)]
    [InlineData("Downloads Threads", 1)]
    [InlineData("Downloads Boards", 1)]
    public void PartiallyScrolledDecorationsStayInsideTheViewport(string view, double scale)
    {
        if (view.StartsWith("Downloads", StringComparison.Ordinal))
        {
            DownloadWindowFixture.Run(fixture =>
            {
                CheckDownloads(fixture, view == "Downloads Boards");
                return Task.CompletedTask;
            }, AddGridTheme);
            return;
        }

        BooruComponentDispatcher.Run(() =>
        {
            using IDisposable platform = BooruReviewTestPlatform.Enter();
            AddGridTheme();
            using TempDirectory profile = new();
            switch (view)
            {
                case "Booru List": CheckBooru(BooruResultView.List); break;
                case "Booru Grid": CheckBooru(BooruResultView.Grid); break;
                case "Booru Gallery": CheckBooru(BooruResultView.Gallery); break;
                case "Media Grid": CheckMedia(profile.Path, scale); break;
                case "Archive thread cards": CheckArchive(profile.Path, savedMedia: false, table: false, scale); break;
                case "Archive thread table": CheckArchive(profile.Path, savedMedia: false, table: true); break;
                case "Reader Saved media Grid": CheckArchive(profile.Path, savedMedia: true, table: false); break;
                case "Reader Saved media Table": CheckArchive(profile.Path, savedMedia: true, table: true); break;
                case "Tagging List": CheckTagging(profile.Path, grid: false, scale); break;
                case "Tagging Grid": CheckTagging(profile.Path, grid: true, scale); break;
                case "Gallery Filmstrip": CheckFilmstrip(profile.Path); break;
                default: throw new ArgumentException(view);
            }
        });
    }

    [Theory]
    [InlineData(false, 1, 280)]
    [InlineData(false, 2, 1920)]
    [InlineData(true, 1, 1920)]
    [InlineData(true, 2, 280)]
    public void BooruTileActionFocusStaysInsideTheViewportWhileScrolling(bool checkbox, double scale, double viewportWidth) =>
        InBooruPlatform(() => CheckBooru(BooruResultView.Grid, checkbox ? BooruDecoration.Select : BooruDecoration.OpenGallery, scale, viewportWidth));

    [Theory]
    [InlineData(1, 1920)]
    [InlineData(2, 280)]
    public void BooruRatingTooltipOpensInsideTheViewportOnAnObscuredTile(double scale, double viewportWidth) =>
        InBooruPlatform(() => CheckBooru(BooruResultView.Grid, BooruDecoration.RatingTip, scale, viewportWidth));

    [Fact]
    public void BooruRatingTooltipClosesWhenTheViewportIsTooShort() =>
        InBooruPlatform(() => CheckBooru(BooruResultView.Grid, BooruDecoration.RatingTipTooTall, scale: 2, viewportWidth: 280));

    private static void InBooruPlatform(Action exercise) => BooruComponentDispatcher.Run(() =>
    {
        using IDisposable platform = BooruReviewTestPlatform.Enter();
        exercise();
    });

    private static void CheckBooru(BooruResultView mode, BooruDecoration decoration = BooruDecoration.Frame,
        double scale = 1, double? viewportWidth = null)
    {
        BooruUiHarness ports = new();
        BooruPostView[] posts = Enumerable.Range(1, BooruPagerLimits.Default.PostsPerPage)
            .Select(i => BooruUiFixtures.Post(i.ToString(), rating: BooruRating.Explicit, preview: false)).ToArray();
        ports.Search.OnSearch = (query, page, _) =>
            Task.FromResult(BooruCallResult<BooruResultPage>.Ok(BooruUiFixtures.Page(query, page, true, posts)));
        using BooruWorkbenchModel model = new(ports.Services);
        using BooruWorkbenchView view = new(model, ownsModel: false);
        view.SetResultView(mode, save: false);
        InWindow(view, window =>
        {
            BooruComponentDispatcher.Wait(BooruTestSupport.SearchAsync(model.Search, "cat"));
            Assert.Equal(BooruSearchStatus.Results, model.Search.View.Status);
            bool gallery = mode == BooruResultView.Gallery;
            BooruComponentDispatcher.WaitUntil(() => gallery ? view.Gallery.CurrentPostId is not null
                : Descendants<Border>(view).Any(control => AutomationId(control) == "BooruResult:1"),
                "the Booru results to be rendered");
            ScrollViewer scroll = Find<ScrollViewer>(view, "BooruResultScroll");
            scroll.MaxHeight = 280;
            Layout(window);
            Control viewport = Viewport(scroll);
            if (viewportWidth is { } width)
            {
                viewport.Width = width;
                Layout(window);
                Assert.Equal(width, viewport.Bounds.Width);
            }

            if (decoration is BooruDecoration.OpenGallery or BooruDecoration.Select)
            {
                Control first = Find<Border>(view, "BooruResult:1");
                string id = decoration == BooruDecoration.Select ? "BooruSelect:1" : "BooruView:1";
                CheckBooruActionFocus(window, scroll, viewport, first, id);
                return;
            }

            Edge edge = mode == BooruResultView.Grid ? Edge.Bottom : Edge.Top;
            string postId = posts[0].PostId;
            if (edge == Edge.Bottom && decoration != BooruDecoration.RatingTipTooTall)
            {
                Rect area = Bounds(viewport, window);
                postId = posts.Select(post => post.PostId)
                    .First(id => Bounds(Find<Border>(view, "BooruResult:" + id), window).Top >= area.Bottom);
            }

            Control Target() => gallery ? view.Gallery.Stage : Find<Border>(view, "BooruResult:" + postId);
            void Select()
            {
                Assert.True(Target().Focus(NavigationMethod.Tab));
                window.KeyPressQwerty(PhysicalKey.Space, RawInputModifiers.None);
                window.KeyReleaseQwerty(PhysicalKey.Space, RawInputModifiers.None);
                Layout(window);
                if (gallery) Assert.True(view.Gallery.SelectBox.IsChecked);
                else Assert.True(Find<CheckBox>(Target(), "BooruSelect:" + postId).IsChecked);
                if (mode == BooruResultView.Grid)
                    Assert.Contains("booru-tile-selected", Target().Classes);
            }

            if (decoration == BooruDecoration.RatingTip)
                CheckRatingTip(window, scroll, viewport, Target());
            else if (decoration == BooruDecoration.RatingTipTooTall)
                CheckRatingTipTooTall(window, scroll, viewport, Target());
            else
                CheckFrame(window, viewport, Target, offset => scroll.Offset = new Vector(0, offset), Select, edge);
        }, scale, viewportWidth is { } requested ? Math.Max(400, requested + 96) : 1040);
    }

    private static void CheckMedia(string directory, double scale)
    {
        MediaPage page = new(_ => throw new InvalidOperationException("The fixture supplies the media rows."));
        try
        {
            page.Grid.ItemsSource = Enumerable.Range(1, ItemCount).Select(i =>
            {
                string name = i + ".png";
                MediaLibraryEntry entry = new(ThreadId, "Fixture", 1, DateTimeOffset.UnixEpoch, null,
                    null, null, null, null, null, directory, null, null, null, null, "original", name,
                    SavedMediaKind.Image, MediaLibraryPresence.Missing, Unassigned: true, Historical: false);
                return new MediaLibraryTileRow(new MediaLibraryTile(entry, [entry]), i - 1);
            }).ToArray();
            InWindow(page, window =>
            {
                Control selection = Find<Control>(page, "MediaSelectionRow");
                Control status = (Control)Find<TextBlock>(page, "MediaStatus").Parent!;
                // Hold the header layout steady for the before-and-after pixel comparison.
                using var selectionVisibility = selection.SetValue(Visual.IsVisibleProperty, false, BindingPriority.Animation);
                using var statusVisibility = status.SetValue(Visual.IsVisibleProperty, status.IsVisible, BindingPriority.Animation);
                CheckList(window, page.Grid);
            }, scale);
        }
        finally { page.EndLifetime(); }
    }

    private static void CheckArchive(string directory, bool savedMedia, bool table, double scale = 1)
    {
        MediaAssetSnapshot manifest = Manifest(directory);
        ArchivePage page = new(_ => throw new InvalidOperationException("The fixture supplies the archive rows."),
            (_, _) => Task.CompletedTask, _ => { }, (_, _) => Task.FromResult(manifest)) { IsAvailable = true };
        try
        {
            if (savedMedia)
            {
                ArchivedThreadView thread = new(Summary(ThreadId, directory),
                    [new CapturedPost("1", 1, PostFieldPresence.All)], [directory]);
                BooruComponentDispatcher.Wait(page.LoadThreadAsync(ThreadId, preparedThread: thread));
                BooruComponentDispatcher.Wait(page.MediaLoadTask);
                InWindow(page.DetachReader(), window =>
                {
                    Find<TabControl>(window, "ArchiveContentTabs").SelectedIndex = 1;
                    page.MediaTilesPreference = !table;
                    Layout(window);
                    if (table) CheckTable(window, Find<DataGrid>(window, "ArchiveMediaGrid"));
                    else CheckList(window, Find<ListBox>(window, "ArchiveMediaTiles"));
                }, scale);
                return;
            }

            ArchiveThreadRow[] rows = Enumerable.Range(1, ItemCount)
                .Select(i => new ArchiveThreadRow(Summary(ThreadId with { Thread = i.ToString() }, directory))).ToArray();
            ListBox cards = page.FindControl<ListBox>("ArchiveThreads")!;
            DataGrid grid = page.FindControl<DataGrid>("ArchiveThreadTable")!;
            page.ArchiveLayoutPreference = table ? "Table" : "List";
            cards.ItemsSource = grid.ItemsSource = rows;
            InWindow(page, window =>
            {
                if (table) CheckTable(window, grid);
                else CheckList(window, cards);
            }, scale);
        }
        finally { page.EndLifetime(); }
    }

    private static void CheckTagging(string directory, bool grid, double scale)
    {
        new Config { DownloadPath = Path.Combine(directory, "media"), AutomaticUpdateChecks = false, SaveUrlsOnExit = false }
            .SaveConfig(directory);
        DesktopSessionHost host = new(directory, []);
        try
        {
            BooruComponentDispatcher.Wait(host.StartAsync());
            TagQueueItem[] items = Enumerable.Range(1, ItemCount).Select(i =>
                new TagQueueItem(ThreadId, directory, i.ToString(), new string('a', 64), i, i.ToString(), "original",
                    i.ToString(), null, null, null, false, null, i + ".png", SavedMediaKind.Image, TagQueueState.Open,
                    DateTimeOffset.UnixEpoch)).ToArray();
            BooruComponentDispatcher.Wait(host.EnqueueTagQueueAsync(items));
            using TaggingPage page = new(host);
            if (grid) page.RestoreViewMode(TaggingViewMode.Grid);
            InWindow(page, window =>
            {
                BooruComponentDispatcher.Wait(page.ActivateAsync());
                Assert.Equal(ItemCount, page.ShownList.Items.Count);
                using var selectionVisibility = page.SelectionBar.SetValue(Visual.IsVisibleProperty, false, BindingPriority.Animation);
                CheckList(window, page.ShownList);
            }, scale);
        }
        finally { BooruComponentDispatcher.Wait(host.DisposeAsync().AsTask()); }
    }

    private static void CheckFilmstrip(string directory)
    {
        GalleryFilmstrip strip = new() { ThumbnailLoader = (_, _, _) => Task.FromResult<Bitmap?>(null) };
        MediaAssetSnapshot manifest = Manifest(directory);
        GalleryEntry[] entries = manifest.GetPage().Select(asset =>
            new GalleryEntry(new GalleryAssetKey(ThreadId, directory, asset.AssetId), manifest, asset, null, null,
                Historical: false, Unassigned: true)).ToArray();
        strip.SetEntries(entries, entries.Length);
        strip.SetPositionText("1 / " + entries.Length);
        try
        {
            InWindow(strip, window =>
            {
                Control viewport = Find<Control>(strip, "GalleryFilmstripTiles");
                double pitch = strip.TileSize + strip.TileGapSize;
                int index = (int)Math.Ceiling(strip.ViewportWidth / pitch);
                double start = index * pitch - strip.ViewportWidth + 8;
                strip.ScrollTo(start);
                Layout(window);
                CheckFrame(window, viewport, () => strip.TileControl(index)!, offset => strip.ScrollTo(start + offset),
                    () => { strip.SetCurrent(index); Assert.Equal(index, strip.CurrentIndex); }, Edge.Right);
            });
        }
        finally { strip.Retire(); }
    }

    private static void CheckDownloads(DownloadWindowFixture fixture, bool boards)
    {
        if (!boards)
        {
            fixture.ShowThreads(Enumerable.Range(1, ItemCount).Select(i => DownloadWindowFixture.Thread(i)).ToArray());
            CheckTable(fixture.Window, fixture.Threads);
            return;
        }

        fixture.Find<TabControl>("DownloadsTabs").SelectedIndex = 1;
        DataGrid grid = fixture.Find<DataGrid>("BoardsDataGrid");
        BoardRow[] rows = Enumerable.Range(1, ItemCount).Select(i => new BoardRow(new DesktopBoardEntry(i,
            new BoardSnapshot(new ResourceId("offline", "test" + i, null), "https://offline.invalid/" + i,
                "Board " + i, string.Empty, null, null), "Offline fixture", "test" + i))).ToArray();
        try
        {
            grid.ItemsSource = rows;
            CheckTable(fixture.Window, grid);
        }
        finally
        {
            grid.ItemsSource = null;
            foreach (BoardRow row in rows) row.Dispose();
        }
    }

    private static void CheckList(Window window, ListBox list)
    {
        Layout(window);
        ScrollViewer scroll = Find<ScrollViewer>(list, "PART_ScrollViewer");
        Control viewport = Viewport(scroll);
        object? entry = list.Items[0];
        ListBoxItem Target() => Assert.IsType<ListBoxItem>(list.ContainerFromItem(entry!));
        CheckFrame(window, viewport, Target, offset => scroll.Offset = new Vector(0, offset), () =>
        {
            list.SelectedItem = entry;
            Assert.Same(entry, list.SelectedItem);
        }, Edge.Top, insetFocus: true);
        Assert.Same(entry, list.SelectedItem);

        window.FocusManager!.Focus(null);
        scroll.Offset = default;
        Layout(window);
        ListBoxItem item = Target();
        Rect bounds = Bounds(item, window);
        Rect area = Bounds(viewport, window);
        Assert.True(area.Contains(bounds), $"The whole item {bounds} must be visible inside {area}.");
        Assert.False(item.IsFocused);
        Assert.True(item.IsSelected);
        using SKBitmap selected = Render(window);
        Assert.True(item.Focus(NavigationMethod.Tab));
        using SKBitmap focused = Render(window);
        Assert.Equal(bounds, Bounds(item, window));
        Assert.Same(entry, list.SelectedItem);
        CheckInsetFocusEdges(window, selected, focused, bounds, area);
    }

    private static void CheckTable(Window window, DataGrid grid)
    {
        Layout(window);
        DataGridRowsPresenter viewport = Descendants<DataGridRowsPresenter>(grid).Single();
        ScrollBar scrollbar = Find<ScrollBar>(grid, "PART_VerticalScrollbar");
        object first = grid.ItemsSource!.Cast<object>().First();
        DataGridRow Target() => Descendants<DataGridRow>(viewport).Single(row => ReferenceEquals(row.DataContext, first));
        CheckFrame(window, viewport, Target, offset =>
        {
            double change = scrollbar.LargeChange;
            double delta = offset - scrollbar.Value;
            try
            {
                scrollbar.SetCurrentValue(ScrollBar.LargeChangeProperty, Math.Abs(delta));
                if (delta > 0) scrollbar.PageDown();
                else if (delta < 0) scrollbar.PageUp();
            }
            finally { scrollbar.SetCurrentValue(ScrollBar.LargeChangeProperty, change); }
        }, () => { grid.SelectedItem = first; Assert.Same(first, grid.SelectedItem); }, Edge.Top, focus: grid);
    }

    private static void CheckFrame(Window window, Control viewport, Func<Control> target, Action<double> scroll,
        Action select, Edge edge, Control? focus = null, bool insetFocus = false)
    {
        Rect area = Bounds(viewport, window);
        Rect start = Bounds(target(), window);
        double offset = edge switch
        {
            Edge.Top => start.Top - area.Top + Math.Min(16, start.Height / 2),
            Edge.Bottom => start.Top - area.Bottom + 32,
            Edge.Right => start.Left - area.Right + 22,
            _ => throw new ArgumentOutOfRangeException(nameof(edge))
        };
        scroll(offset);
        Layout(window);
        Rect partiallyVisible = Bounds(target(), window);
        AssertPartial(partiallyVisible, Bounds(viewport, window), edge);

        window.FocusManager!.Focus(null, NavigationMethod.Unspecified, KeyModifiers.None);
        using SKBitmap idle = Render(window);

        select();
        window.FocusManager!.Focus(null);
        Layout(window);
        scroll(offset);
        Layout(window);
        Rect item = Bounds(target(), window);
        area = Bounds(viewport, window);
        AssertPartial(item, area, edge);
        Assert.Equal(partiallyVisible, item);
        Control focusTarget = focus ?? target();
        Assert.False(focusTarget.IsKeyboardFocusWithin);
        using SKBitmap selected = Render(window);

        Assert.True(focusTarget.Focus(NavigationMethod.Tab));
        Layout(window);
        scroll(offset); // Keyboard focus may first bring the item fully into view.
        using SKBitmap focused = Render(window);
        Assert.True(focusTarget.IsKeyboardFocusWithin);
        Assert.Equal(item, Bounds(target(), window));
        Assert.Equal(area, Bounds(viewport, window));

        Rect visible = item.Intersect(area);
        double scale = window.RenderScaling;
        int selectionPixels = ChangedPixels(idle, selected, visible, scale);
        int focusPixels = ChangedPixels(selected, focused, visible, scale);
        Assert.True(Math.Max(selectionPixels, focusPixels) >= Math.Min(visible.Width, visible.Height) * scale,
            $"Only {selectionPixels} selection pixels and {focusPixels} focus pixels changed inside {visible}.");
        if (insetFocus)
            CheckInsetFocusEdges(window, selected, focused, item, area);
        Rect nearby = item.Inflate(4);
        Rect outside = edge switch
        {
            Edge.Top => new Rect(visible.Left, nearby.Top, visible.Width, area.Top - nearby.Top),
            Edge.Bottom => new Rect(visible.Left, area.Bottom, visible.Width, nearby.Bottom - area.Bottom),
            Edge.Right => new Rect(area.Right, visible.Top, nearby.Right - area.Right, visible.Height),
            _ => throw new ArgumentOutOfRangeException(nameof(edge))
        };
        int escapedSelection = ChangedPixels(idle, selected, outside, scale);
        int escapedFocus = ChangedPixels(selected, focused, outside, scale);
        Assert.True(escapedSelection == 0 && escapedFocus == 0,
            $"{escapedSelection} selection pixels and {escapedFocus} focus pixels escaped at the {edge} edge into {outside} (viewport {area}, item {item}).");
    }

    private static void CheckInsetFocusEdges(Window window, SKBitmap before, SKBitmap after, Rect item, Rect viewport)
    {
        Assert.True(window.TryFindResource("Workspace.Accent", out object? accent));
        Assert.True(window.TryFindResource("Workspace.Background", out object? background));
        SKColor outer = BrushColor(accent);
        SKColor inner = BrushColor(background);
        double scale = window.RenderScaling;
        foreach (Edge edge in new[] { Edge.Top, Edge.Bottom, Edge.Left, Edge.Right })
        {
            const double corner = 8;
            const double strokes = 3;
            Rect band = edge switch
            {
                Edge.Top => new Rect(item.Left + corner, item.Top, item.Width - 2 * corner, strokes),
                Edge.Bottom => new Rect(item.Left + corner, item.Bottom - strokes, item.Width - 2 * corner, strokes),
                Edge.Left => new Rect(item.Left, item.Top + corner, strokes, item.Height - 2 * corner),
                Edge.Right => new Rect(item.Right - strokes, item.Top + corner, strokes, item.Height - 2 * corner),
                _ => throw new ArgumentOutOfRangeException(nameof(edge))
            };
            Rect visible = band.Intersect(viewport);
            if (visible.Width <= 0 || visible.Height <= 0)
            {
                Assert.Equal(Edge.Top, edge);
                continue;
            }

            double minimum = Math.Max(visible.Width, visible.Height) * scale / 2;
            int accentPixels = CountPixels(after, visible, scale, (x, y) => after.GetPixel(x, y) == outer);
            int contrastChanges = CountPixels(after, visible, scale, (x, y) => after.GetPixel(x, y) == inner && before.GetPixel(x, y) != inner);
            Assert.True(accentPixels >= minimum && contrastChanges >= minimum,
                $"The {edge} focus strokes need at least {minimum} pixels each in {visible} at {scale:P0}. Found {accentPixels} accent and {contrastChanges} new contrast pixels.");
        }
    }

    private static SKColor BrushColor(object? value)
    {
        Color color = Assert.IsAssignableFrom<ISolidColorBrush>(value).Color;
        return new SKColor(color.R, color.G, color.B, color.A);
    }

    private static void CheckBooruActionFocus(Window window, ScrollViewer scroll, Control viewport, Control tile, string actionId)
    {
        Control action = Find<Control>(tile, actionId);
        Rect area = Bounds(viewport, window);
        Rect start = Bounds(action, window);
        Assert.True(area.Contains(start), $"The action {start} must begin inside {area}.");
        Assert.True(tile.Focus(NavigationMethod.Pointer));
        double offset = start.Center.Y - area.Top;
        scroll.Offset = new Vector(0, offset);
        using SKBitmap idle = Render(window);
        Rect item = Bounds(action, window);
        AssertPartial(item, area, Edge.Top);

        scroll.Offset = default;
        Layout(window);
        foreach (string id in new[] { "BooruView:1", actionId }.Distinct(StringComparer.Ordinal))
        {
            window.KeyPressQwerty(PhysicalKey.Tab, RawInputModifiers.None);
            window.KeyReleaseQwerty(PhysicalKey.Tab, RawInputModifiers.None);
            Assert.Same(Find<Control>(tile, id), window.FocusManager!.GetFocusedElement());
        }

        scroll.Offset = new Vector(0, offset);
        using SKBitmap focused = Render(window);
        Assert.Same(action, window.FocusManager!.GetFocusedElement());
        Assert.Equal(item, Bounds(action, window));
        CheckInsetFocusEdges(window, idle, focused, item, area);
        Rect outside = new(item.Left - 4, item.Top - 4, item.Width + 8, area.Top - item.Top + 4);
        int escaped = ChangedPixels(idle, focused, outside, window.RenderScaling);
        Assert.True(escaped == 0, $"{escaped} focus pixels from {actionId} escaped above {area} into {outside}.");
    }

    private static void CheckRatingTip(Window window, ScrollViewer scroll, Control viewport, Control tile)
    {
        Rect area = Bounds(viewport, window);
        Rect start = Bounds(tile, window);
        ToolTip.SetShowDelay(tile, 0);
        scroll.Offset = new Vector(0, start.Top - area.Top - 8);
        Layout(window);
        Rect shown = Bounds(tile, window);
        Assert.True(area.Contains(shown), $"The whole tile {shown} must be visible inside {area}.");
        window.MouseMove(new Point(shown.Center.X, shown.Top + 8));
        Layout(window);
        AssertRatingTipInside(window, tile, area);
        window.MouseMove(new Point(1, 1));
        Layout(window);
        Assert.False(ToolTip.GetIsOpen(tile));

        scroll.Offset = new Vector(0, start.Top - area.Bottom + 32);
        Layout(window);
        Rect item = Bounds(tile, window);
        AssertPartial(item, area, Edge.Bottom);
        window.MouseMove(new Point(item.Center.X, area.Bottom - 2));
        Layout(window);
        Assert.True(tile.IsPointerOver);
        AssertRatingTipInside(window, tile, area);
    }

    private static void AssertRatingTipInside(Window window, Control tile, Rect area)
    {
        Assert.True(ToolTip.GetIsOpen(tile), "The rating tooltip must open when the viewport can fit it.");
        Popup popup = Assert.Single(window.OpenedPopups, popup => ReferenceEquals(popup.PlacementTarget, tile));
        ToolTip tip = Assert.IsType<ToolTip>(popup.Child);
        Assert.Equal(BooruText.Rating(BooruRating.Explicit), tip.Content);
        Assert.True(tip.Bounds.Width > 0 && tip.Bounds.Height > 0);
        Point topLeft = window.PointToClient(tip.PointToScreen(default));
        Point bottomRight = window.PointToClient(tip.PointToScreen(new Point(tip.Bounds.Width, tip.Bounds.Height)));
        Rect bounds = new(topLeft, bottomRight);
        Assert.True(area.Contains(bounds), $"The Booru rating tooltip {bounds} covered neighboring UI outside {area}.");
    }

    private static void CheckRatingTipTooTall(Window window, ScrollViewer scroll, Control viewport, Control tile)
    {
        scroll.MaxHeight = 8;
        Layout(window);
        Rect area = Bounds(viewport, window);
        Rect tileBounds = Bounds(tile, window);
        AssertPartial(tileBounds, area, Edge.Bottom);
        Assert.InRange(area.Height, 1, 8);
        int openings = 0;
        ToolTip.AddToolTipOpeningHandler(tile, (_, _) => openings++);
        ToolTip.SetShowDelay(tile, 0);
        window.MouseMove(tileBounds.Intersect(area).Center);
        Layout(window);
        Assert.True(tile.IsPointerOver);
        Assert.Equal(1, openings);
        Assert.False(ToolTip.GetIsOpen(tile));
        Assert.DoesNotContain(window.OpenedPopups, popup => ReferenceEquals(popup.PlacementTarget, tile));
    }

    private static void AssertPartial(Rect item, Rect area, Edge edge) =>
        Assert.True(edge switch
        {
            Edge.Top => item.Top < area.Top && area.Top < item.Bottom,
            Edge.Bottom => item.Top < area.Bottom && area.Bottom < item.Bottom,
            Edge.Right => item.Left < area.Right && area.Right < item.Right,
            _ => false
        }, $"The fixture must cut through the item at the {edge} edge (viewport {area}, item {item}).");

    private static int ChangedPixels(SKBitmap first, SKBitmap second, Rect region, double scale) =>
        CountPixels(first, region, scale, (x, y) => first.GetPixel(x, y) != second.GetPixel(x, y));

    private static int CountPixels(SKBitmap bitmap, Rect region, double scale, Func<int, int, bool> matches)
    {
        int count = 0;
        int left = Math.Max(0, (int)Math.Ceiling(region.Left * scale));
        int right = Math.Min(bitmap.Width, (int)Math.Floor(region.Right * scale));
        int top = Math.Max(0, (int)Math.Ceiling(region.Top * scale));
        int bottom = Math.Min(bitmap.Height, (int)Math.Floor(region.Bottom * scale));
        for (int y = top; y < bottom; y++)
            for (int x = left; x < right; x++)
                if (matches(x, y)) count++;
        return count;
    }

    private static SKBitmap Render(Window window)
    {
        Layout(window);
        using WriteableBitmap rendered = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("No rendered frame.");
        Assert.Equal(PixelSize.FromSize(window.ClientSize, window.RenderScaling), rendered.PixelSize);
        using MemoryStream png = new();
        rendered.Save(png, PngBitmapEncoderOptions.Default);
        png.Position = 0;
        return SKBitmap.Decode(png);
    }

    private static void InWindow(Control content, Action<Window> exercise, double scale = 1, double width = 1040)
    {
        Border toolbar = new() { Height = 32, Classes = { "workspace-section" } };
        Border navigation = new() { Height = 48, Classes = { "workspace-section" } };
        Grid layout = new() { RowDefinitions = new RowDefinitions("Auto,*,Auto"), Children = { toolbar, content, navigation } };
        Grid.SetRow(content, 1);
        Grid.SetRow(navigation, 2);
        Window window = new() { Content = layout, Width = width, Height = 650, RequestedThemeVariant = ThemeVariant.Dark,
            Classes = { MotionPolicy.OffClass } };
        try
        {
            window.Show();
            window.PlatformImpl!.Activated?.Invoke();
            window.SetRenderScaling(scale);
            Assert.Equal(scale, window.RenderScaling);
            Layout(window);
            exercise(window);
        }
        finally { window.Close(); Layout(window); }
    }

    private static void Layout(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }

    private static Rect Bounds(Control control, Window window) => new(control.TranslatePoint(default, window)!.Value, control.Bounds.Size);
    private static IEnumerable<T> Descendants<T>(Control root) where T : Control => root.GetVisualDescendants().OfType<T>();
    private static string? AutomationId(Control control) => ControlAutomationPeer.CreatePeerForElement(control).GetAutomationId();
    private static T Find<T>(Control root, string id) where T : Control => Assert.Single(Descendants<T>(root), control => AutomationId(control) == id);
    private static ScrollContentPresenter Viewport(ScrollViewer scroll) => Assert.IsType<ScrollContentPresenter>(scroll.Presenter);
    private static void AddGridTheme() => Application.Current!.Styles.Insert(1, new DataGridSemiTheme());

    private static ArchiveThreadSummary Summary(ResourceId id, string directory) =>
        new(id, "https://offline.invalid/test/" + id, "Fixture thread " + id, directory, ThreadSourceState.Active,
            true, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, 1, 1);

    private static MediaAssetSnapshot Manifest(string directory) => new(directory, MediaManifestStatus.Available, 1, "fixture",
        Enumerable.Range(1, ItemCount).Select(i =>
        {
            string name = i + ".png";
            return new MediaAssetReference(i.ToString(), i.ToString(), "original", name, Path.Combine(directory, name), 1,
                new string('a', 64), "https://offline.invalid/" + name, name, true);
        }));
}
