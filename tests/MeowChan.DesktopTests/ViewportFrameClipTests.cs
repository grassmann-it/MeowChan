using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Raw;
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
    private enum Edge { Top, Bottom, Right }

    [Theory]
    [InlineData("Booru List")]
    [InlineData("Booru Grid")]
    [InlineData("Booru Gallery")]
    [InlineData("Booru Grid rating tooltip")]
    [InlineData("Media Grid")]
    [InlineData("Archive thread cards")]
    [InlineData("Archive thread table")]
    [InlineData("Reader Saved media Grid")]
    [InlineData("Reader Saved media Table")]
    [InlineData("Tagging List")]
    [InlineData("Tagging Grid")]
    [InlineData("Gallery Filmstrip")]
    [InlineData("Downloads Threads")]
    [InlineData("Downloads Boards")]
    public void PartiallyScrolledDecorationsStayInsideTheViewport(string view)
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
                case "Booru Grid rating tooltip": CheckBooru(BooruResultView.Grid, ratingTip: true); break;
                case "Media Grid": CheckMedia(profile.Path); break;
                case "Archive thread cards": CheckArchive(profile.Path, savedMedia: false, table: false); break;
                case "Archive thread table": CheckArchive(profile.Path, savedMedia: false, table: true); break;
                case "Reader Saved media Grid": CheckArchive(profile.Path, savedMedia: true, table: false); break;
                case "Reader Saved media Table": CheckArchive(profile.Path, savedMedia: true, table: true); break;
                case "Tagging List": CheckTagging(profile.Path, grid: false); break;
                case "Tagging Grid": CheckTagging(profile.Path, grid: true); break;
                case "Gallery Filmstrip": CheckFilmstrip(profile.Path); break;
                default: throw new ArgumentException(view);
            }
        });
    }

    private static void CheckBooru(BooruResultView mode, bool ratingTip = false)
    {
        BooruUiHarness ports = new();
        BooruPostView[] posts = Enumerable.Range(1, ItemCount)
            .Select(i => BooruUiFixtures.Post(i.ToString(), rating: BooruRating.Explicit, preview: false)).ToArray();
        ports.Search.OnSearch = (query, page, _) =>
            Task.FromResult(BooruCallResult<BooruResultPage>.Ok(BooruUiFixtures.Page(query, page, true, posts)));
        using BooruWorkbenchModel model = new(ports.Services);
        using BooruWorkbenchView view = new(model, ownsModel: false);
        view.SetResultView(mode, save: false);
        InWindow(view, window =>
        {
            BooruComponentDispatcher.Wait(BooruTestSupport.SearchAsync(model.Search, "cat"));
            BooruComponentDispatcher.WaitUntil(() => Descendants<Control>(view)
                .Any(control => mode == BooruResultView.Gallery ? control == view.Gallery.Stage && view.Gallery.CurrentPostId is not null : control.Name == "BooruResult:1"),
                "the Booru results to be rendered");
            ScrollViewer scroll = Find<ScrollViewer>(view, "BooruResultScroll");
            scroll.MaxHeight = 280;
            Layout(window);
            Control viewport = Viewport(scroll);
            bool gallery = mode == BooruResultView.Gallery;
            Edge edge = mode == BooruResultView.Grid ? Edge.Bottom : Edge.Top;
            string name = gallery ? view.Gallery.Stage.Name! : "BooruResult:1";
            if (edge == Edge.Bottom)
            {
                Rect area = Bounds(viewport, window);
                name = Descendants<Border>(view).First(card => card.Name?.StartsWith("BooruResult:", StringComparison.Ordinal) == true
                    && Bounds(card, window).Top >= area.Bottom).Name!;
            }

            Control Target() => Find<Control>(view, name);
            void Select()
            {
                Assert.True(Target().Focus(NavigationMethod.Tab));
                window.KeyPressQwerty(PhysicalKey.Space, RawInputModifiers.None);
                window.KeyReleaseQwerty(PhysicalKey.Space, RawInputModifiers.None);
                Layout(window);
                if (gallery) Assert.True(view.Gallery.SelectBox.IsChecked);
                else Assert.True(Descendants<CheckBox>(Target()).Single(box => box.Name?.StartsWith("BooruSelect:", StringComparison.Ordinal) == true).IsChecked);
                if (mode == BooruResultView.Grid)
                    Assert.Contains("booru-tile-selected", Target().Classes);
            }

            if (ratingTip)
                CheckRatingTip(window, viewport, Target(), offset => scroll.Offset = new Vector(0, offset));
            else
                CheckFrame(window, viewport, Target, offset => scroll.Offset = new Vector(0, offset), Select, edge);
        });
    }

    private static void CheckMedia(string directory)
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
            InWindow(page, window => CheckList(window, page.Grid));
        }
        finally { page.EndLifetime(); }
    }

    private static void CheckArchive(string directory, bool savedMedia, bool table)
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
                    page.FindControl<TabControl>("ArchiveContentTabs")!.SelectedIndex = 1;
                    page.MediaTilesPreference = !table;
                    Layout(window);
                    if (table) CheckTable(window, page.FindControl<DataGrid>("ArchiveMediaGrid")!);
                    else CheckList(window, page.FindControl<ListBox>("ArchiveMediaTiles")!);
                });
                return;
            }

            ArchiveThreadRow[] rows = Enumerable.Range(1, ItemCount)
                .Select(i => new ArchiveThreadRow(Summary(ThreadId with { Thread = i.ToString() }, directory))).ToArray();
            ListBox cards = page.FindControl<ListBox>("ArchiveThreads")!;
            DataGrid grid = page.FindControl<DataGrid>("ArchiveThreadTable")!;
            cards.ItemsSource = grid.ItemsSource = rows;
            cards.IsVisible = !table;
            grid.IsVisible = table;
            InWindow(page, window =>
            {
                if (table) CheckTable(window, grid);
                else CheckList(window, cards);
            });
        }
        finally { page.EndLifetime(); }
    }

    private static void CheckTagging(string directory, bool grid)
    {
        DesktopSessionHost host = new(directory, []);
        try
        {
            using TaggingPage page = new(host);
            if (grid) page.RestoreViewMode(TaggingViewMode.Grid);
            ListBox list = page.ShownList;
            list.ItemsSource = Enumerable.Range(1, ItemCount).Select(i =>
                new TaggingQueueRow(new TagQueueItem(ThreadId, directory, i.ToString(), new string('a', 64), 1,
                    i.ToString(), "original", null, null, null, null, false, null, i + ".png",
                    SavedMediaKind.Image, TagQueueState.Open, DateTimeOffset.UnixEpoch), i - 1, Next: false)).ToArray();
            Find<Control>(page, "TaggingEmptyState").IsVisible = false;
            InWindow(page, window => CheckList(window, list));
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
        Control viewport = Viewport(Descendants<ScrollViewer>(list).First());
        ScrollViewer scroll = viewport.FindAncestorOfType<ScrollViewer>()!;
        Control Target() => Assert.IsType<ListBoxItem>(list.ContainerFromIndex(0));
        CheckFrame(window, viewport, Target, offset => scroll.Offset = new Vector(0, offset),
            () => { list.SelectedIndex = 0; Assert.Same(list.Items[0], list.SelectedItem); }, Edge.Top);
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
        Action select, Edge edge, Control? focus = null)
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
        Assert.True((focus ?? target()).Focus(NavigationMethod.Tab));
        Layout(window);
        scroll(offset); // Selection and keyboard focus may first bring the item fully into view.
        Layout(window);
        Assert.True((focus ?? target()).IsKeyboardFocusWithin);
        Rect item = Bounds(target(), window);
        area = Bounds(viewport, window);
        AssertPartial(item, area, edge);
        Assert.Equal(partiallyVisible, item);
        using SKBitmap decorated = Render(window);

        Rect visible = item.Intersect(area);
        Assert.True(ChangedPixels(idle, decorated, visible) > 0, "Selection and keyboard focus must remain visible inside the viewport.");
        Rect nearby = item.Inflate(4);
        Rect outside = edge switch
        {
            Edge.Top => new Rect(visible.Left, nearby.Top, visible.Width, area.Top - nearby.Top),
            Edge.Bottom => new Rect(visible.Left, area.Bottom, visible.Width, nearby.Bottom - area.Bottom),
            Edge.Right => new Rect(area.Right, visible.Top, nearby.Right - area.Right, visible.Height),
            _ => throw new ArgumentOutOfRangeException(nameof(edge))
        };
        int escaped = ChangedPixels(idle, decorated, outside);
        Assert.True(escaped == 0, $"{escaped} selection/focus pixels escaped at the {edge} edge into {outside} (viewport {area}, item {item}).");
    }

    private static void CheckRatingTip(Window window, Control viewport, Control tile, Action<double> scroll)
    {
        Rect area = Bounds(viewport, window);
        Rect start = Bounds(tile, window);
        ToolTip.SetShowDelay(tile, 0);
        scroll(start.Top - area.Top - 8);
        Layout(window);
        Rect shown = Bounds(tile, window).Intersect(area);
        window.MouseMove(new Point(shown.Center.X, shown.Top + 8));
        Layout(window);
        Assert.True(ToolTip.GetIsOpen(tile), "The rating tooltip must still open on a fully visible tile.");
        window.MouseMove(new Point(1, 1));

        scroll(start.Top - area.Bottom + 32);
        Layout(window);
        Rect item = Bounds(tile, window);
        AssertPartial(item, area, Edge.Bottom);
        window.MouseMove(new Point(item.Center.X, area.Bottom - 2));
        Layout(window);
        Assert.True(tile.IsPointerOver);
        foreach (ToolTip tip in window.OpenedPopups.Select(popup => popup.Child).OfType<ToolTip>())
        {
            PixelPoint topLeft = tip.PointToScreen(default);
            Rect popup = new(window.PointToClient(topLeft), tip.Bounds.Size);
            Assert.True(area.Contains(popup), $"The Booru rating tooltip {popup} covered neighboring UI outside {area}.");
        }
    }

    private static void AssertPartial(Rect item, Rect area, Edge edge) =>
        Assert.True(edge switch
        {
            Edge.Top => item.Top < area.Top && area.Top < item.Bottom,
            Edge.Bottom => item.Top < area.Bottom && area.Bottom < item.Bottom,
            Edge.Right => item.Left < area.Right && area.Right < item.Right,
            _ => false
        }, $"The fixture must cut through the item at the {edge} edge (viewport {area}, item {item}).");

    private static int ChangedPixels(SKBitmap first, SKBitmap second, Rect region)
    {
        int count = 0;
        for (int y = Math.Max(0, (int)Math.Ceiling(region.Top)); y < Math.Min(first.Height, (int)Math.Floor(region.Bottom)); y++)
            for (int x = Math.Max(0, (int)Math.Ceiling(region.Left)); x < Math.Min(first.Width, (int)Math.Floor(region.Right)); x++)
                if (first.GetPixel(x, y) != second.GetPixel(x, y)) count++;
        return count;
    }

    private static SKBitmap Render(Window window)
    {
        Layout(window);
        using WriteableBitmap rendered = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("No rendered frame.");
        using MemoryStream png = new();
        rendered.Save(png, PngBitmapEncoderOptions.Default);
        png.Position = 0;
        return SKBitmap.Decode(png);
    }

    private static void InWindow(Control content, Action<Window> exercise)
    {
        Border toolbar = new() { Height = 32, Classes = { "workspace-section" } };
        Border navigation = new() { Height = 48, Classes = { "workspace-section" } };
        Grid layout = new() { RowDefinitions = new RowDefinitions("Auto,*,Auto"), Children = { toolbar, content, navigation } };
        Grid.SetRow(content, 1);
        Grid.SetRow(navigation, 2);
        Window window = new() { Content = layout, Width = 1040, Height = 650, RequestedThemeVariant = ThemeVariant.Dark,
            Classes = { MotionPolicy.OffClass } };
        try
        {
            window.Show();
            window.PlatformImpl!.Activated?.Invoke();
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
    private static T Find<T>(Control root, string name) where T : Control => Descendants<T>(root).Single(control => control.Name == name);
    private static ScrollContentPresenter Viewport(ScrollViewer scroll) => Descendants<ScrollContentPresenter>(scroll).First();
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
