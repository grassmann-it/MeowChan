using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;
using MeowChan.Gallery;
using MeowChan.Interfaces;
using MeowChan.Persistence;
using MeowChan.Ratings;
using MeowChan.Storage;
using MeowChan.Testing;
using ArchiveCatalog = MeowChan.Archive.ArchiveCatalog;

namespace MeowChan.DesktopSmoke;

// Inside the registered "media-page" journey (MEOWCHAN_MEDIA_PAGE_ONLY), Tom's screencast 17.11.2026 ("switching to the Media page is buggy; at
// full width Filters is too close to Sort; things must not jump around"). Over a library of 144 tiles in a private window:
//  (a) the search, the filter toggle and the order keep their bounds whatever the selection line says (nothing, 1, 12 selected, the index
//      being built), (b) the filter toggle keeps at least MediaToolbarLayout's group gap (24 dip) from the order at every row width from 616
//      to 1920 in all 12 languages, (c) the buttons of the selection line keep their x with and without the "index is being built" reason,
//  (d) a re-present caused by data (own state, a rebuilt index) keeps the scroll anchor, the selected tile's place on screen and the
//      thumbnails it already shows (no "Loading image…" over a cached preview).
// MEOWCHAN_MEDIA_HEADER_CAPTURES=<directory> runs only this check and writes the header images (Light/Dark, EN/DE, 760 to 1920 dip) and a
// bounds table there (evidence for the review); without it the check runs in the journey with its assertions only.
internal static partial class MediaPageChecks
{
    private const int StabilityThreads = 12, StabilityFilesPerThread = 12;
    private static readonly double[] CaptureWidths = [760, 920, 1099, 1100, 1440, 1920];
    private static readonly double[] SweepRowWidths = [616, 640, 700, 760, 800, 869, 870, 900, 949, 950, 1000, 1075, 1099, 1100, 1200, 1416, 1680, 1896];

    internal static string? HeaderCaptureDirectory => Environment.GetEnvironmentVariable("MEOWCHAN_MEDIA_HEADER_CAPTURES") is { Length: > 0 } directory ? directory : null;

    // The 12 threads of 12 files, and a newer thread of 7 files whose media folder is moved aside until part (d) brings it back: the next
    // rebuild then puts 7 new tiles in front of everything (by thread, newest first), as an archive that captured more since the last visit.
    private static async Task<(string Hidden, string Shown)> SeedStabilityAsync(string profile)
    {
        using ProfileLease lease = ProfileLease.Acquire(profile);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(profile, lease);
        string late = string.Empty;
        for (int thread = 0; thread <= StabilityThreads; thread++)
        {
            string id = (1000 + thread).ToString(CultureInfo.InvariantCulture), directory = Path.Combine(profile, "media", "s" + id);
            ResourceId resource = new("media-fixture", "g", id);
            // One bound file per thread (the post's attachment), the others are files of the thread without a post: every file is a tile.
            (string, string, string?)[] files = Enumerable.Range(0, thread == StabilityThreads ? 7 : StabilityFilesPerThread)
                .Select(file => ("st-" + id + "-" + file.ToString("00", CultureInfo.InvariantCulture) + ".png", "s" + id + "f" + file, file == 0 ? id : (string?)null)).ToArray();
            CapturedAttachment bound = await WriteAsync(directory, resource, files);
            await catalog.CaptureAsync(new CapturedThreadSnapshot(resource, "https://offline.invalid/" + id, "Stable " + id,
                ThreadSourceState.Archived, new DateTimeOffset(2026, 10, 1 + thread, 0, 0, 0, TimeSpan.Zero), directory,
                [new CapturedPost(id, 0, PostFieldPresence.All, Html: "op", Attachments: [bound])]));
            late = directory;
        }
        Directory.Move(late, late + ".later");
        return (late + ".later", late);
    }

    private sealed record HeaderBounds(double Row, double SearchX, double SearchW, double FiltersX, double FiltersW, double SortLeft, double SortRight, double[] Buttons, string Level)
    {
        internal double Gap => SortLeft - (FiltersX + FiltersW);
    }

    private static async Task HeaderStabilityAsync(MainWindow owner, string? captures)
    {
        string profile = TemporaryWorkspacePaths.CreateDirectory("media-page-header-stability-");
        new Config().SaveConfig(profile);
        (string hiddenThread, string shownThread) = await SeedStabilityAsync(profile);
        await using DesktopSessionHost host = new(profile, Array.Empty<IImageBoard>());
        await host.StartAsync().WaitAsync(Deadline);
        string language = Texts.Language;
        TaskCompletionSource hold = new(TaskCreationOptions.RunContinuationsAsynchronously);
        bool holding = false;
        MediaLibraryUserData userData = new(null, [], []);
        MediaPage page = new(host.OpenArchiveReaderAsync, async (directory, token) =>
        {
            if (holding) await hold.Task.WaitAsync(token); // holds a rebuild open: the selection line then says that the index is being built
            return await MediaAssetReader.OpenAsync(directory, token);
        })
        { ReadUserData = () => userData };
        Window window = new() { Width = 1440, Height = 760, Content = page, Title = "Media header stability — diagnostic", ShowInTaskbar = false };
        List<string> failures = new(), table = new();
        if (captures != null) Directory.CreateDirectory(captures);
        try
        {
            Texts.SetLanguage("en");
            window.Show(owner);
            page.IsAvailable = true;
            await page.ActivateAsync().WaitAsync(Deadline);
            Require(page.Tiles.Count == StabilityThreads * StabilityFilesPerThread, "The stability fixture must give 144 tiles: " + page.Tiles.Count);
            Control row = page.ToolbarRow;
            Control header = row.GetVisualAncestors().OfType<StackPanel>().First(panel => panel.Classes.Contains("workspace-page-header"));
            Control search = C<Control>(page, "MediaNameFilter"), filters = C<Control>(page, "MediaFilters"), direction = C<Control>(page, "MediaSortDirection");
            Panel order = C<Panel>(page, "MediaToolbarSort");
            Button[] buttons = new[] { "MediaSelectionQueue", "MediaSelectionTag", "MediaSelectionGallery", "MediaSelectionExport", "MediaSelectionClear" }.Select(name => C<Button>(page, name)).ToArray();
            double X(Control control, Visual to) => control.TranslatePoint(default, to)!.Value.X;

            async Task SettleAsync()
            {
                for (int turn = 0; turn < 3; turn++)
                {
                    await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
                    page.UpdateLayout();
                }
            }

            async Task SetWindowWidthAsync(double width)
            {
                if (Math.Abs(window.Width - width) < 0.5 && Math.Abs(window.Bounds.Width - width) < 1) { await SettleAsync(); return; }
                window.Width = width;
                await UntilAsync(() => Math.Abs(window.Bounds.Width - width) < 1);
                await SettleAsync();
            }

            async Task SetRowWidthAsync(double target)
            {
                await SetWindowWidthAsync(target + 24);
                for (int attempt = 0; attempt < 4 && Math.Abs(row.Bounds.Width - target) > 0.5; attempt++)
                    await SetWindowWidthAsync(window.Width + target - row.Bounds.Width);
                await UntilAsync(() => page.ToolbarLevel == MediaToolbarLayout.For(row.Bounds.Width));
                await SettleAsync();
            }

            HeaderBounds Measure()
            {
                page.UpdateLayout();
                Control first = order.Children.First(child => child.IsVisible);
                return new(row.Bounds.Width, X(search, row), search.Bounds.Width, X(filters, row), filters.Bounds.Width, X(first, row), X(direction, row) + direction.Bounds.Width,
                    buttons.Select(button => button.IsEffectivelyVisible ? Math.Round(X(button, page), 1) : double.NaN).ToArray(), page.ToolbarLevel.ToString());
            }

            void Capture(string name)
            {
                if (captures == null) return;
                page.UpdateLayout();
                SaveRegion(header, Path.Combine(captures, name + ".png"));
            }

            void Record(string cell, string state, HeaderBounds bounds) => table.Add(string.Join(",", cell, state, F(bounds.Row), bounds.Level, F(bounds.SearchX), F(bounds.SearchX + bounds.SearchW),
                F(bounds.FiltersX), F(bounds.FiltersX + bounds.FiltersW), F(bounds.SortLeft), F(bounds.SortRight), F(bounds.Gap), string.Join(" ", bounds.Buttons.Select(F))));

            async Task SelectAsync(int count)
            {
                page.Grid.Selection.Clear();
                if (count == 1) page.Grid.Selection.Select(0);
                else if (count > 1) page.Grid.Selection.SelectRange(0, count - 1);
                await SettleAsync();
            }

            // (a) and (c): the four states of the selection line over one row width, captured.
            async Task StatesAsync(string code, string theme, double width)
            {
                window.RequestedThemeVariant = theme == "Dark" ? ThemeVariant.Dark : ThemeVariant.Light;
                if (Texts.Language != code) Texts.SetLanguage(code);
                await SetWindowWidthAsync(width);
                await UntilAsync(() => AutomationName(C<Control>(page, "MediaSort")) == Texts.T("media.sort"));
                string cell = code + "-" + theme + "-" + width.ToString(CultureInfo.InvariantCulture);
                await SelectAsync(0);
                HeaderBounds none = Measure(); Record(cell, "none", none); Capture("header-" + cell + "-none");
                await SelectAsync(1);
                HeaderBounds one = Measure(); Record(cell, "one", one); Capture("header-" + cell + "-one");
                await SelectAsync(12);
                HeaderBounds twelve = Measure(); Record(cell, "twelve", twelve); Capture("header-" + cell + "-twelve");
                await SelectAsync(1);
                holding = true;
                ClickRaise(C<Button>(page, "MediaRefresh"));
                await UntilAsync(() => !buttons[1].IsEnabled && !buttons[3].IsEnabled);
                await SettleAsync();
                HeaderBounds building = Measure(); Record(cell, "building", building); Capture("header-" + cell + "-building");
                holding = false;
                hold.SetResult();
                await page.BuildTask.WaitAsync(Deadline);
                hold = new(TaskCreationOptions.RunContinuationsAsynchronously);
                await SettleAsync();
                foreach ((string state, HeaderBounds other) in new[] { ("one", one), ("twelve", twelve), ("building", building) })
                    if (!Same(none.SearchX, other.SearchX) || !Same(none.SearchW, other.SearchW) || !Same(none.FiltersX, other.FiltersX)
                        || !Same(none.SortLeft, other.SortLeft) || !Same(none.SortRight, other.SortRight))
                        failures.Add("(a) " + cell + ": the toolbar moved between nothing selected and " + state + ": search " + F(none.SearchX) + "+" + F(none.SearchW) + " vs "
                            + F(other.SearchX) + "+" + F(other.SearchW) + ", filters " + F(none.FiltersX) + " vs " + F(other.FiltersX) + ", order " + F(none.SortLeft) + ".." + F(none.SortRight)
                            + " vs " + F(other.SortLeft) + ".." + F(other.SortRight));
                for (int index = 0; index < buttons.Length; index++)
                    if (!double.IsNaN(one.Buttons[index]) && !Same(one.Buttons[index], building.Buttons[index]))
                        failures.Add("(c) " + cell + ": " + buttons[index].Name + " moved from " + F(one.Buttons[index]) + " to " + F(building.Buttons[index]) + " with the building reason.");
                if (none.Gap < MediaToolbarLayoutGroupGap - 0.5)
                    failures.Add("(b) " + cell + ": filters to order gap " + F(none.Gap) + " < " + MediaToolbarLayoutGroupGap);
            }

            foreach (double width in CaptureWidths)
                foreach (string theme in new[] { "Light", "Dark" })
                    await StatesAsync("en", theme, width);
            foreach (double width in new double[] { 1440, 760 })
                foreach (string theme in new[] { "Light", "Dark" })
                    await StatesAsync("de", theme, width);
            window.RequestedThemeVariant = ThemeVariant.Light;
            await SelectAsync(0);

            // (b): every row width over all languages; the gap never falls below the group gap, the order and the last tool stay inside the row.
            foreach (LanguageInfo info in Texts.Languages)
            {
                Texts.SetLanguage(info.Code);
                await UntilAsync(() => AutomationName(C<Control>(page, "MediaSort")) == Texts.T("media.sort"));
                foreach (double target in SweepRowWidths)
                {
                    await SetRowWidthAsync(target);
                    HeaderBounds bounds = Measure();
                    Record(info.Code + "-sweep-" + target.ToString(CultureInfo.InvariantCulture), "none", bounds);
                    if (bounds.Gap < MediaToolbarLayoutGroupGap - 0.5)
                        failures.Add("(b) " + info.Code + " row " + F(bounds.Row) + " (" + bounds.Level + "): filters to order gap " + F(bounds.Gap) + " < " + MediaToolbarLayoutGroupGap);
                    if (bounds.SortRight > bounds.Row + 0.5)
                        failures.Add("(b) " + info.Code + " row " + F(bounds.Row) + " (" + bounds.Level + "): the order runs out of the row at " + F(bounds.SortRight));
                }
            }
            Texts.SetLanguage("en");
            await UntilAsync(() => AutomationName(C<Control>(page, "MediaSort")) == Texts.T("media.sort"));

            // (d): a data re-present with the grid scrolled deep and a tile selected.
            await SetWindowWidthAsync(1440);
            await AnchorAcrossRefreshAsync(page, captures, failures, ratings => userData = new(null, [], ratings), () => Directory.Move(hiddenThread, shownThread), SettleAsync);

            if (captures != null)
            {
                File.WriteAllText(Path.Combine(captures, "bounds.csv"), "cell,state,row,level,searchX,searchRight,filtersX,filtersRight,sortLeft,sortRight,gap,buttonsX\n"
                    + string.Join("\n", table) + "\n");
                File.WriteAllText(Path.Combine(captures, "failures.txt"), string.Join("\n", failures) + "\n");
            }
            Require(failures.Count == 0, "Media header/grid stability: " + failures.Count + " failure(s):\n" + string.Join("\n", failures.Take(40)));
            Console.WriteLine("SMOKE media header stability [12 languages]: search, filters and order keep their bounds with nothing, 1 and 12 selected and while the index is built; the filters keep >= "
                + MediaToolbarLayoutGroupGap + " dip from the order at every row width 616-1896; the selection buttons keep their x with the building reason; a data re-present keeps the scroll anchor, the selected tile's place and its shown thumbnails.");
        }
        finally
        {
            if (holding) { holding = false; hold.TrySetResult(); }
            Texts.SetLanguage(language);
            page.EndLifetime();
            window.Close();
        }
    }

    // The group gap the design asks for between the filter toggle and the order (MediaToolbarLayout.GroupGap since the fix; a literal so the
    // check also runs against the code before it).
    private const double MediaToolbarLayoutGroupGap = 24;

    // The region as the window draws it (its background included): the whole window is rendered, then the region's pixels are copied out.
    private static void SaveRegion(Control region, string path)
    {
        if (TopLevel.GetTopLevel(region) is not Window window) return;
        double scale = window.RenderScaling;
        Vector dpi = new(96 * scale, 96 * scale);
        using RenderTargetBitmap full = new(PixelSize.FromSize(window.ClientSize, scale), dpi);
        full.Render(window);
        Point origin = region.TranslatePoint(default, window) ?? default;
        PixelRect rect = PixelRect.FromRect(new Rect(origin, region.Bounds.Size), scale).Intersect(new PixelRect(full.PixelSize));
        if (rect.Width <= 0 || rect.Height <= 0) return;
        using WriteableBitmap crop = new(rect.Size, dpi, Avalonia.Platform.PixelFormat.Bgra8888, Avalonia.Platform.AlphaFormat.Premul);
        using (Avalonia.Platform.ILockedFramebuffer buffer = crop.Lock())
            full.CopyPixels(rect, buffer.Address, buffer.RowBytes * rect.Height, buffer.RowBytes);
        crop.Save(path);
    }

    private static string? AutomationName(Control control) => Avalonia.Automation.AutomationProperties.GetName(control);

    private static bool Same(double a, double b) => Math.Abs(a - b) <= 0.5;

    private static string F(double value) => double.IsNaN(value) ? "-" : value.ToString("0.#", CultureInfo.InvariantCulture);

    private static async Task AnchorAcrossRefreshAsync(MediaPage page, string? captures, List<string> failures,
        Action<IReadOnlyList<MediaRatingEntry>> setRatings, Action revealLaterThread, Func<Task> settle)
    {
        ListBox grid = page.Grid;
        MediaTilePanel panel = grid.GetVisualDescendants().OfType<MediaTilePanel>().First();
        string loading = Texts.T("archive.loadingThumbnail");
        // The realized tiles that stand in the grid's visible area (Ursa keeps a buffer of realized rows above and below it).
        IEnumerable<(ListBoxItem Item, MediaLibraryTileRow Row)> Realized() => grid.GetVisualDescendants().OfType<ListBoxItem>()
            .Where(item => item.IsEffectivelyVisible && item.DataContext is MediaLibraryTileRow && item.TranslatePoint(default, grid) is { } at
                && at.Y + item.Bounds.Height > 0 && at.Y < grid.Bounds.Height)
            .Select(item => (item, (MediaLibraryTileRow)item.DataContext!));
        bool ShowsLoading(ListBoxItem item) => item.GetVisualDescendants().OfType<TextBlock>().Any(text => text.IsEffectivelyVisible && text.Text == loading);
        bool Cached(MediaLibraryTile tile)
        {
            if (MediaTileThumbnails.TryGetCached(tile) is not { } bitmap) return false;
            MediaTileThumbnails.Release(bitmap);
            return true;
        }
        Point? OnScreen(int index) => index >= 0 && grid.ContainerFromIndex(index) is Control { IsVisible: true } container ? container.TranslatePoint(default, grid) : null;
        int IndexOf(MediaLibraryTileKey key) => Enumerable.Range(0, page.Tiles.Count).FirstOrDefault(index => MediaLibraryTileKey.Of(page.Tiles[index]) == key, -1);
        string P(Point? point) => point is { } value ? F(value.X) + "/" + F(value.Y) : "none";
        void Capture(string name)
        {
            if (captures == null) return;
            page.UpdateLayout();
            SaveRegion(page, Path.Combine(captures, name + ".png"));
        }

        // reordered: the refresh moves tiles (a rating under the rating order, new tiles in front); then only the selected tile's line (on screen)
        // or the former top tile's line (selection out of view) must stay where it was, not the anchor's position number.
        async Task CheckAsync(string label, bool selectedOnScreen, bool reordered, Func<Task> refresh)
        {
            grid.Selection.Clear();
            await settle();
            panel.RestoreAnchor(60, 0); // deep in the grid
            await settle();
            (int Index, double Offset) before = panel.CaptureAnchor() ?? throw new InvalidOperationException("No anchor after scrolling.");
            int columns = panel.Columns;
            // On screen: the last tile of the second visible line (a shift by one tile wraps it into the next line); otherwise a tile far above the view.
            int selected = selectedOnScreen ? before.Index + 2 * columns - 1 : 2;
            grid.Selection.Select(selected);
            await settle();
            panel.RestoreAnchor(before.Index, before.Offset); // the ListBox scrolls to a new selection; put the view back where it was
            await settle();
            before = panel.CaptureAnchor()!.Value;
            MediaLibraryTileKey selectedKey = MediaLibraryTileKey.Of(page.Tiles[selected]), topKey = MediaLibraryTileKey.Of(page.Tiles[before.Index]);
            Point? selectedAt = OnScreen(selected), topAt = OnScreen(before.Index);
            await UntilAsync(() => Realized().All(item => !ShowsLoading(item.Item)));
            Capture("grid-" + label + "-before");
            // Taken before the refresh: a load that is still running may store its preview in the cache from its own thread meanwhile.
            HashSet<MediaLibraryTileKey> cachedBefore = page.Tiles.Where(Cached).Select(MediaLibraryTileKey.Of).ToHashSet();
            await refresh();
            // Right after the swap (same dispatcher turn, after layout): no cached preview may show the loading placeholder.
            page.UpdateLayout();
            string[] flashing = Realized().Where(item => ShowsLoading(item.Item) && cachedBefore.Contains(MediaLibraryTileKey.Of(item.Row.Tile)))
                .Select(item => (item.Row.Name ?? "?") + "@" + P(item.Item.TranslatePoint(default, grid))).ToArray();
            if (flashing.Length > 0)
                failures.Add("(d) " + label + ": " + flashing.Length + " realized tile(s) with a cached preview show the loading placeholder after the re-present: " + string.Join(", ", flashing.Take(6)));
            await settle();
            Capture("grid-" + label + "-after");
            (int Index, double Offset)? after = panel.CaptureAnchor();
            int now = IndexOf(selectedKey), top = IndexOf(topKey);
            if (!reordered && (after is not { } kept || kept.Index != before.Index || Math.Abs(kept.Offset - before.Offset) > 0.5))
                failures.Add("(d) " + label + ": the scroll anchor moved from " + before.Index + "/" + F(before.Offset) + " to " + (after is { } a ? a.Index + "/" + F(a.Offset) : "none"));
            if (now < 0 || !grid.Selection.SelectedIndexes.Contains(now) || grid.Selection.Count != 1)
                failures.Add("(d) " + label + ": the selected tile must stay selected (now at " + now + ", selected " + string.Join(",", grid.Selection.SelectedIndexes) + ").");
            else if (selectedOnScreen && (OnScreen(now) is not { } at || selectedAt is not { } was || Math.Abs(at.Y - was.Y) > 0.5 || (!reordered && Math.Abs(at.X - was.X) > 0.5)))
                failures.Add("(d) " + label + ": the selected tile moved on screen from " + P(selectedAt) + " to " + P(OnScreen(now)));
            if (!selectedOnScreen && (OnScreen(top) is not { } line || topAt is not { } wasTop || Math.Abs(line.Y - wasTop.Y) > 0.5))
                failures.Add("(d) " + label + ": the tile at the top of the view moved from " + P(topAt) + " to " + P(OnScreen(top)) + " (index " + before.Index + " -> " + top + ")");
        }

        // Own state changed (a favorite, a tag elsewhere): the page re-reads it and presents again (PF-05); nothing moves.
        await CheckAsync("userdata", selectedOnScreen: true, reordered: false, () => { setRatings([]); page.RefreshUserData(); return Task.CompletedTask; });
        // The index was rebuilt (every activation does it) without changes: with the selected tile out of view, and on screen.
        await CheckAsync("rebuild", selectedOnScreen: false, reordered: false, async () =>
        {
            await page.RebuildAsync().WaitAsync(Deadline);
            await page.PresentTask.WaitAsync(Deadline);
        });
        await CheckAsync("rebuild-selected", selectedOnScreen: true, reordered: false, async () =>
        {
            await page.RebuildAsync().WaitAsync(Deadline);
            await page.PresentTask.WaitAsync(Deadline);
        });
        // Under the rating order a new rating moves a tile from the end to the front: every tile after it shifts by one.
        ComboBox sort = C<ComboBox>(page, "MediaSort");
        sort.SelectedIndex = (int)MediaLibrarySort.Rating;
        await page.PresentTask.WaitAsync(Deadline);
        await settle();
        await CheckAsync("rating", selectedOnScreen: true, reordered: true, () =>
        {
            MediaLibraryEntry last = page.Tiles[^1].Entry;
            setRatings([new MediaRatingEntry(MediaLibraryFilterEngine.AssetKey(last)!, 5)]);
            page.RefreshUserData();
            return page.PresentTask;
        });
        sort.SelectedIndex = (int)MediaLibrarySort.ByThread;
        await page.PresentTask.WaitAsync(Deadline);
        await settle();
        // The archive captured a newer thread since the last visit: the rebuilt index puts its 7 tiles in front of everything.
        await CheckAsync("rebuild-inserted", selectedOnScreen: false, reordered: true, async () =>
        {
            int count = page.Tiles.Count;
            revealLaterThread();
            await page.RebuildAsync().WaitAsync(Deadline);
            await page.PresentTask.WaitAsync(Deadline);
            if (page.Tiles.Count != count + 7) failures.Add("(d) rebuild-inserted: the fixture must add 7 tiles: " + count + " -> " + page.Tiles.Count);
        });
    }
}
