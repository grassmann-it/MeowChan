using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;
using MeowChan.Interfaces;
using MeowChan.Persistence;
using MeowChan.Tags;

namespace MeowChan.DesktopSmoke;

// MEOWCHAN_MEDIA_PAGE_ONLY, UX103 P8: the media filter fields open as a surface over the tile grid, like the archive
// filters (ArchiveFilterSurfaceChecks). Private host window and profile, so themes, languages and sizes can change freely.
// Keyboard input is routed, not OS input.
internal static partial class MediaPageChecks
{
    private const int SurfaceTagged = 20; // threads 600..619 carry the tag: their 20 files plus the loose file of 600

    private static async Task FilterSurfaceAsync(MainWindow owner, string profile)
    {
        new Config().SaveConfig(profile);
        await SeedReturnAsync(profile);
        await using DesktopSessionHost host = new(profile, Array.Empty<IImageBoard>());
        await host.StartAsync().WaitAsync(Deadline);
        string language = Texts.Language;
        MediaPage page = new(host.OpenArchiveReaderAsync) { ReadUserData = () => UserData(host) };
        Window window = new() { Width = 1400, Height = 900, Content = page, Title = "Media filter surface — diagnostic", ShowInTaskbar = false };
        try
        {
            window.Show(owner);
            window.Activate();
            page.IsAvailable = true;
            await page.ActivateAsync().WaitAsync(Deadline);
            int all = page.Tiles.Count;
            Require(all == ReturnThreads + 2, "Filter surface fixture must give " + (ReturnThreads + 2) + " tiles: " + all);
            Require(Equals(C<Expander>(page, "MediaFilters").Header, Texts.T("media.filters")), "The filter toggle must not mark an inactive filter.");
            PrivateTagCommit tag = await host.DefinePrivateTagAsync("surface").WaitAsync(Deadline);
            for (int index = 0; index < SurfaceTagged; index++)
                await host.SetThreadTagAsync(tag.Tag, new ResourceId("media-fixture", "g", (600 + index).ToString(System.Globalization.CultureInfo.InvariantCulture)), true).WaitAsync(Deadline);
            page.RefreshUserData();
            // An applied tag gives a chip in the header, which must stay visible above the open surface.
            ComboBox choice = C<ComboBox>(page, "MediaTagChoice");
            choice.SelectedItem = choice.Items.Cast<object>().Single(item => item.ToString() == "surface");
            C<Button>(page, "MediaTagInclude").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(page.Tiles.Count == SurfaceTagged + 1 && page.FilterChips.Children.Count == 1, "Tag filter setup failed: " + page.Tiles.Count);
            foreach (ThemeVariant theme in new[] { ThemeVariant.Dark, ThemeVariant.Light })
            foreach (string code in new[] { "de", "en" })
            foreach ((double width, double height) in new[] { (1400d, 900d), (660d, 560d) })
                await FilterSurfaceStateAsync(window, page, theme, code, width, height);
            Console.WriteLine("SMOKE media filter surface: toggle opens the fields over the grid (grid and header unmoved, chips/status/selection bar visible), live filtering from the surface, active marker, focus in/out, Tab cycle, Escape order (dropdown first), Close button, programmatic open keeps focus, tiles fit their cells; DE/EN, Light/Dark, 1400/660 passed.");
        }
        finally { Texts.SetLanguage(language); page.EndLifetime(); window.Close(); }
    }

    private static async Task FilterSurfaceStateAsync(Window window, MediaPage page, ThemeVariant theme, string code, double width, double height)
    {
        string state = $"{theme}/{code}/{width}x{height}";
        window.RequestedThemeVariant = theme;
        Texts.SetLanguage(code);
        window.Width = width; window.Height = height;
        Expander filters = C<Expander>(page, "MediaFilters");
        Border surface = C<Border>(page, "MediaFilterSurface"), grid = C<Border>(page, "MediaGridSurface");
        ScrollViewer scroll = C<ScrollViewer>(page, "MediaFilterSurfaceScroll");
        Button close = C<Button>(page, "MediaFilterClose");
        TextBox name = C<TextBox>(page, "MediaNameFilter");
        ComboBox kind = C<ComboBox>(page, "MediaKindFilter");
        TextBlock status = C<TextBlock>(page, "MediaStatus");
        await UntilAsync(() =>
        {
            window.UpdateLayout();
            return PresentationChecks.MatchesRequestedSize(window, width, height) && grid.Bounds.Height > 0
                && Equals(filters.Header, Texts.T("media.filters.active")) && close.Content as string == Texts.T("action.close");
        });
        Rect Bounds(Control control) => new(control.TranslatePoint(default, window)
            ?? throw new InvalidOperationException(control.Name + " is detached."), control.Bounds.Size);
        bool Contains(Rect outer, Rect inner) => inner.Left >= outer.Left - 1 && inner.Top >= outer.Top - 1
            && inner.Right <= outer.Right + 1 && inner.Bottom <= outer.Bottom + 1;
        bool Same(Rect a, Rect b) => Math.Abs(a.Left - b.Left) <= 1 && Math.Abs(a.Top - b.Top) <= 1
            && Math.Abs(a.Right - b.Right) <= 1 && Math.Abs(a.Bottom - b.Bottom) <= 1;
        IInputElement? Focused() => window.FocusManager?.GetFocusedElement();
        bool FocusIn(Control owner) => Focused() is Visual visual && visual.GetSelfAndVisualAncestors().Contains(owner);
        void Key(Key key)
        {
            if (Focused() is not InputElement target) throw new InvalidOperationException(state + ": no focused element for " + key);
            target.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key });
        }
        Rect client = new(default, window.ClientSize);
        string file = $"smoke-media-filter-surface-{(int)width}-{code}-{theme.ToString().ToLowerInvariant()}";

        Require(!filters.IsExpanded && !surface.IsEffectivelyVisible,state + ": filter fields are shown before the toggle.");
        ToggleButton toggle = filters.GetVisualDescendants().OfType<ToggleButton>().First();
        Require(Bounds(filters).Height <= 48, state + ": the collapsed filter toggle still reserves field height: " + Bounds(filters));
        // Tiles keep their content inside the denser cell.
        MediaTilePanel panel = page.Grid.GetVisualDescendants().OfType<MediaTilePanel>().Single();
        double tallest = page.GetVisualDescendants().OfType<MediaLibraryTileView>().Where(tile => tile.IsEffectivelyVisible)
            .Select(tile => (tile.Content as Control)?.DesiredSize.Height ?? 0).DefaultIfEmpty(0).Max();
        Require(panel.ItemHeight == MediaLibraryTileView.TileHeight && tallest > 0 && tallest <= MediaLibraryTileView.TileHeight,
            state + ": tile content " + tallest + " does not fit the cell " + panel.ItemHeight);
        // Screenshots show the real verified thumbnails of the tiles in view.
        await UntilAsync(() => page.GetVisualDescendants().OfType<MediaLibraryTileView>()
            .Where(tile => tile.IsEffectivelyVisible && tile.Bounds.Height > 0 && tile.TranslatePoint(default, window) is { } at
                && at.Y >= Bounds(grid).Top && at.Y + tile.Bounds.Height <= Bounds(grid).Bottom)
            .All(tile => tile.HasThumbnail));
        Rect gridClosed = Bounds(grid), chipsClosed = Bounds(page.FilterChips), statusClosed = Bounds(status);
        Program.CaptureWindow(window, file + "-closed.png");

        // Deliberate open from the toggle moves focus into the surface.
        toggle.Focus(NavigationMethod.Tab);
        filters.IsExpanded = true;
        await UntilAsync(() => { window.UpdateLayout(); return surface.IsEffectivelyVisible && FocusIn(surface); });
        Rect area = Bounds(surface);
        Console.WriteLine("SMOKE media filter surface geometry: " + JsonSerializer.Serialize(new
        {
            state, scale = window.RenderScaling, surface = area.ToString(), grid = gridClosed.ToString(), toggle = Bounds(filters).ToString(),
            chips = chipsClosed.ToString(), close = Bounds(close).ToString(), extent = scroll.Extent.Height, viewport = scroll.Viewport.Height,
            tileContent = tallest, tileHeight = MediaLibraryTileView.TileHeight
        }));
        Require(Contains(gridClosed, area) && Contains(client, area) && Math.Abs(area.Top - gridClosed.Top) <= 1,
            state + ": the filter surface is not anchored at the top of the grid: " + area + " / " + gridClosed);
        Require(Same(Bounds(grid), gridClosed), state + ": opening the filter surface moved the grid.");
        Require(Same(Bounds(page.FilterChips), chipsClosed) && Same(Bounds(status), statusClosed)
            && page.FilterChips.IsEffectivelyVisible && status.IsEffectivelyVisible && chipsClosed.Bottom <= area.Top + 1
            && C<TextBlock>(page, "MediaSelectionStatus").IsEffectivelyVisible,
            state + ": applied chips, status or selection bar moved or are hidden by the open surface.");
        Require(close.IsEffectivelyVisible && Contains(area, Bounds(close)) && Contains(client, Bounds(close))
            && close.DesiredSize.Width <= close.Bounds.Width + 1, state + ": the Close button is clipped.");
        // Long content scrolls inside the surface; the last field stays reachable.
        Button reset = C<Button>(page, "MediaFilterReset");
        scroll.Offset = new Vector(0, Math.Max(0, scroll.Extent.Height - scroll.Viewport.Height));
        await UntilAsync(() => { window.UpdateLayout(); return Contains(Bounds(scroll), Bounds(reset)); });
        scroll.Offset = default;
        window.UpdateLayout();
        Program.CaptureWindow(window, file + "-open.png");

        // Fields still filter live from the surface; the toggle marks the active filter while closed.
        name.Text = "r-60";
        await UntilAsync(() => page.Tiles.Count == 10 && status.Text == Texts.T("media.filter.count", 10, ReturnThreads + 2));
        name.Text = string.Empty;
        await UntilAsync(() => page.Tiles.Count == SurfaceTagged + 1);
        Require(filters.IsExpanded && Same(Bounds(grid), gridClosed), state + ": live filtering closed the surface or moved the grid.");

        // Tab stays inside the open surface.
        close.Focus(NavigationMethod.Tab);
        Key(Avalonia.Input.Key.Tab);
        await UntilAsync(() => FocusIn(surface) && !ReferenceEquals(Focused(), close));
        // Escape closes an open dropdown first, then the surface, and focus returns to the toggle.
        kind.Focus(NavigationMethod.Tab);
        kind.IsDropDownOpen = true;
        await UntilAsync(() => kind.IsDropDownOpen);
        Key(Avalonia.Input.Key.Escape);
        await UntilAsync(() => !kind.IsDropDownOpen);
        Require(filters.IsExpanded, state + ": Escape on an open dropdown closed the whole filter surface.");
        kind.Focus(NavigationMethod.Tab);
        Key(Avalonia.Input.Key.Escape);
        await UntilAsync(() => !filters.IsExpanded && !surface.IsEffectivelyVisible && FocusIn(filters));

        // The Close button closes as well and never leaves focus on a hidden field.
        toggle.Focus(NavigationMethod.Tab);
        filters.IsExpanded = true;
        await UntilAsync(() => surface.IsEffectivelyVisible && FocusIn(surface));
        close.Focus(NavigationMethod.Tab);
        close.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await UntilAsync(() => !filters.IsExpanded && !surface.IsEffectivelyVisible && FocusIn(filters));
        Require(Same(Bounds(grid), gridClosed), state + ": closing the filter surface left the grid shifted.");

        // A programmatic open keeps the current focus (here: a tile).
        page.FocusGrid();
        await UntilAsync(() => FocusIn(page.Grid));
        filters.IsExpanded = true;
        await UntilAsync(() => surface.IsEffectivelyVisible);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        Require(FocusIn(page.Grid), state + ": a programmatic open stole focus from the grid.");
        page.CloseFilterSurface(restoreFocus: false);
        await UntilAsync(() => !surface.IsEffectivelyVisible);
        Require(FocusIn(page.Grid), state + ": closing an unfocused surface moved focus away from the grid.");
    }
}
