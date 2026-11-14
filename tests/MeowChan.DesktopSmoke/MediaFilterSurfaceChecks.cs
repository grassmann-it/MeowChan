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
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using MeowChan.Gallery;
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
            FilterToggleButton toggleButton = C<FilterToggleButton>(page, "MediaFilters");
            Require(toggleButton.Label == Texts.T("media.filters") && toggleButton.Count == 0 && !toggleButton.IsBadgeVisible, "The filter toggle must not mark an inactive filter.");
            PrivateTagCommit tag = await host.DefinePrivateTagAsync("surface").WaitAsync(Deadline);
            for (int index = 0; index < SurfaceTagged; index++)
                await host.SetThreadTagAsync(tag.Tag, new ResourceId("media-fixture", "g", (600 + index).ToString(System.Globalization.CultureInfo.InvariantCulture)), true).WaitAsync(Deadline);
            page.RefreshUserData();
            // An applied tag gives a chip in the header, which must stay visible above the open surface.
            Require(page.AddTagFilterByName("surface", include: true) == null, "Tag filter setup failed (popover add).");
            Require(page.Tiles.Count == SurfaceTagged + 1 && page.FilterChips.Children.Count == 1, "Tag filter setup failed: " + page.Tiles.Count);
            foreach (ThemeVariant theme in new[] { ThemeVariant.Dark, ThemeVariant.Light })
            foreach (string code in new[] { "de", "en" })
            foreach ((double width, double height) in new[] { (1400d, 900d), (660d, 560d) })
                await FilterSurfaceStateAsync(window, page, theme, code, width, height);
            foreach (ThemeVariant theme in new[] { ThemeVariant.Dark, ThemeVariant.Light })
            foreach (string code in new[] { "de", "en" })
                await FilterChipRowAsync(window, page, theme, code);
            foreach (ThemeVariant theme in new[] { ThemeVariant.Dark, ThemeVariant.Light })
            foreach (string code in new[] { "de", "en" })
                await FocusModeAsync(window, page, theme, code);
            Console.WriteLine("SMOKE media focus mode: Shift+F / the ⤢ tool hides toolbar, chips, hint, status and filter section and shows one 32 dip row (live counter, filter count with its chips as tooltip, 'Exit focus ×' alone at the right); grid and selection bar stay usable; Escape ends the mode before the selection; the exit button, the shortcut and the search request end it; start and end are announced (polite live region); a narrow page moves the tool under More; DE/EN, Light/Dark passed.");
            Console.WriteLine("SMOKE media filter chips: every active filter is a removable chip in one row with the surface closed, Filters (n), Reset all, empty-state reset, error line style, + Filter excludes a type and a source (chips, combo summaries, grid) and a switched type chip shows the other part; DE/EN, Light/Dark passed.");
            Console.WriteLine("SMOKE media filter surface: the toggle (ToggleButton with count badge, input height like the sort combo) opens the fields inline under the status line and pushes the grid down (no overlay, no Close button; header, chips and status unmoved), live filtering from the section, focus in/out, Tab cycle, Escape order (dropdown first), programmatic open keeps focus, tiles fit their cells; DE/EN, Light/Dark, 1400/660 passed.");
        }
        finally { Texts.SetLanguage(language); page.EndLifetime(); window.Close(); }
    }

    // C3: with the surface closed, type, name, rating and favourites appear as chips beside the tag chip, the toggle counts them,
    // a chip resets exactly its field, "Reset all" clears everything and a filter that matches nothing offers its reset in the grid.
    private static async Task FilterChipRowAsync(Window window, MediaPage page, ThemeVariant theme, string code)
    {
        string state = $"chips {theme}/{code}";
        window.RequestedThemeVariant = theme;
        Texts.SetLanguage(code);
        window.Width = 1400; window.Height = 900;
        FilterToggleButton filters = C<FilterToggleButton>(page, "MediaFilters");
        ComboBox kind = C<ComboBox>(page, "MediaKindFilter");
        TextBox name = C<TextBox>(page, "MediaNameFilter");
        Button resetAll = page.FilterResetAll, emptyReset = C<Button>(page, "MediaEmptyReset");
        TextBlock error = C<TextBlock>(page, "MediaFilterError");
        string[] Labels() => page.FilterChips.Children.OfType<Ursa.Controls.ClosableTag>().Select(tag => ArchivePage.FilterTagLabel(tag)).ToArray();
        // The language switch re-renders after a dispatcher turn.
        await UntilAsync(() => resetAll.Content as string == Texts.T("media.filter.resetAll") && Labels().SequenceEqual(new[] { Texts.T("tags.filter.includeChip", "surface") }));
        Require(filters.IsChecked != true && page.ActiveFilterCount == 1 && filters.Count == 1 && filters.BadgeText == "1" && filters.IsBadgeVisible && resetAll.IsEffectivelyVisible
            && resetAll.Content as string == Texts.T("media.filter.resetAll") && error.Classes.Contains("workspace-error"), state + ": one tag chip with Reset all.");
        kind.SelectedIndex = (int)MediaLibraryKindFilter.Image;
        name.Text = "r-6";
        await UntilAsync(() => page.ActiveFilterCount == 3 && page.Tiles.Count == 20); // r-600 ... r-619, not the loose file
        string[] labels = Labels();
        Require(labels.SequenceEqual(new[] { Texts.T("tags.filter.includeChip", "surface"), Texts.T("media.filter.nameChip", "r-6"),
                Texts.T("media.filter.kindChip", Texts.T("media.filter.kind.image")) })
            && filters.Count == 3 && filters.BadgeText == "3" && filters.Label == Texts.T("media.filters")
            && Avalonia.Automation.AutomationProperties.GetName(filters) == Texts.Count("media.filters.countName", 3)
            && page.FilterChips.Children.OfType<Ursa.Controls.ClosableTag>().All(tag => tag.Classes.Contains("chip-filter") && tag.IsEffectivelyVisible),
            state + ": chip row " + string.Join(" | ", labels) + " / " + filters.Count);
        window.UpdateLayout();
        Program.CaptureWindow(window, $"smoke-media-filter-chips-{code}-{theme.ToString().ToLowerInvariant()}.png");

        // Removing the type chip resets only the type; the name chip removes its text at once.
        System.Windows.Input.ICommand Remove(int index) => ((Ursa.Controls.ClosableTag)page.FilterChips.Children[index]).Command!;
        Remove(2).Execute(null);
        Require(kind.SelectedIndex == 0 && name.Text == "r-6" && page.ActiveFilterCount == 2, state + ": the type chip must reset only the type.");
        Remove(1).Execute(null);
        Require(name.Text == string.Empty && page.ActiveFilterCount == 1 && page.Tiles.Count == SurfaceTagged + 1, state + ": the name chip must clear the name.");

        // Nothing matches: the empty grid offers the reset; it and "Reset all" clear every filter, the toggle loses its count.
        name.Text = "no-such-file";
        await UntilAsync(() => page.Tiles.Count == 0 && emptyReset.IsEffectivelyVisible);
        Require(!C<Button>(page, "MediaGoDownloads").IsVisible && emptyReset.Content as string == Texts.T("media.filter.reset"), state + ": empty filter result must offer reset, not navigation.");
        window.UpdateLayout();
        if (code == "en") Program.CaptureWindow(window, $"smoke-media-filter-empty-{theme.ToString().ToLowerInvariant()}.png");
        emptyReset.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Require(page.ActiveFilterCount == 0 && !resetAll.IsVisible && !page.FilterChips.IsVisible && name.Text == string.Empty
            && filters.Count == 0 && !filters.IsBadgeVisible, state + ": the empty-state reset must clear every filter.");
        await UntilAsync(() => page.Tiles.Count == ReturnThreads + 2);

        // Back to the surface journey's starting point: the tag chip, then "Reset all" once and the tag again.
        Require(page.AddTagFilterByName("surface", include: true) == null, state + ": tag add.");
        C<CheckBox>(page, "MediaFavoritesOnly").IsChecked = true;
        C<ComboBox>(page, "MediaMinRating").SelectedIndex = 2;
        C<ComboBox>(page, "MediaSourceFilter").SelectedIndex = 1;
        Require(page.ActiveFilterCount == 4 && Labels().Skip(1).SequenceEqual(new[] { Texts.T("archive.sourceChip", "media-fixture"),
            Texts.T("media.filter.ratingChip", 2), Texts.T("media.filter.favorites") }), state + ": source, rating and favourites chips: " + string.Join(" | ", Labels()));
        resetAll.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Require(page.ActiveFilterCount == 0 && C<CheckBox>(page, "MediaFavoritesOnly").IsChecked == false && page.Tiles.Count == ReturnThreads + 2, state + ": Reset all.");
        Require(page.AddTagFilterByName("surface", include: true) == null, state + ": tag add again.");
        Require(page.ActiveFilterCount == 1 && page.Tiles.Count == SurfaceTagged + 1, state + ": tag filter restored.");

        // M2: "+ Filter" behind the chips adds any field by keyboard. Shift+Enter excludes: "Without type: Images" is a chip of its own
        // (chip-exclude), the type combo shows "3 types", and the grid holds the tagged media that are no images. Switching the chip
        // (as its body click does) shows exactly the other part; an excluded source summarizes in its combo; removing restores.
        FilterTermPicker add = page.FilterAdd;
        Require(add.IsEffectivelyVisible && Avalonia.Automation.AutomationProperties.GetName(add) == Texts.T("media.filter.add") && add.Classes.Contains("chip-available"),
            state + ": + Filter must follow the chips.");
        void Pick(string text, KeyModifiers modifiers = KeyModifiers.None)
        {
            add.Search.Text = text; add.Search.FlushQuery();
            add.Search.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter, KeyModifiers = modifiers });
        }
        string images = Texts.T("media.filter.kind.image");
        Pick(images, KeyModifiers.Shift);
        await UntilAsync(() => Labels().Contains(Texts.T("media.filter.kindExcludeChip", images)));
        int notImages = page.Tiles.Count;
        Ursa.Controls.ClosableTag typeChip() => page.FilterChips.Children.OfType<Ursa.Controls.ClosableTag>().Single(tag => tag.Tag as string == "type:image");
        Require(typeChip().Classes.Contains("chip-exclude") && kind.SelectedItem as string == Texts.Count("media.filter.kind.count", 3)
            && page.CurrentFilter().Kinds.Count == 3 && !page.CurrentFilter().Kinds.Contains(MediaLibraryKindFilter.Image) && notImages < SurfaceTagged + 1,
            state + ": an excluded type must be a chip, summarize in its combo and narrow the grid: " + string.Join(" | ", Labels()) + " / " + notImages);
        Require(page.ToggleFilterChipFor(MediaFilterTerm.Type(MediaLibraryKindFilter.Image)) == null, state + ": switching the type chip failed.");
        await UntilAsync(() => Labels().Contains(Texts.T("media.filter.kindChip", images)));
        Require(!typeChip().Classes.Contains("chip-exclude") && kind.SelectedIndex == (int)MediaLibraryKindFilter.Image
            && page.Tiles.Count + notImages == SurfaceTagged + 1, state + ": the switched type chip must show exactly the other part: " + page.Tiles.Count);
        Pick("-media-fixture");
        await UntilAsync(() => Labels().Contains(Texts.T("media.filter.sourceExcludeChip", "media-fixture")));
        Require(page.CurrentFilter().ExcludeSources.Contains("media-fixture")
            && C<ComboBox>(page, "MediaSourceFilter").SelectedItem?.ToString() == Texts.T("media.filter.combo.excluded", 1),
            state + ": an excluded source must reach the filter and summarize in its combo.");
        while (page.FilterChips.Children.Count > 1) Remove(page.FilterChips.Children.Count - 1).Execute(null);
        Require(page.ActiveFilterCount == 1 && page.Tiles.Count == SurfaceTagged + 1 && kind.SelectedIndex == 0
            && C<ComboBox>(page, "MediaSourceFilter").SelectedIndex == 0, state + ": removing the added chips must restore the tag filter alone.");

        // M3: Enter in the search field turns the recognized "field:value" tokens into chips and keeps the rest in the field. A token that
        // is not understood stays as text with a hint that says why (a polite live region); plain text is left alone. "Copy filters as
        // text" writes the whole filter back as a query, from the row's menu and from every chip's menu.
        string typed = "type:video source:media-fixture bogus:x";
        name.Text = typed;
        name.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
        await UntilAsync(() => page.ActiveFilterCount == 4 && page.QueryNote != null); // tag, type, source and the name chip of what stayed
        string video = Texts.T("media.filter.kind.video");
        Require(name.Text == "bogus:x" && Labels().Contains(Texts.T("media.filter.kindChip", video))
            && Labels().Contains(Texts.T("archive.sourceChip", "media-fixture")) && Labels().Contains(Texts.T("media.filter.nameChip", "bogus:x")),
            state + ": Enter must turn the type and source tokens into chips and leave the unknown token in the field: " + name.Text + " / " + string.Join(" | ", Labels()));
        Require(page.QueryNote == Texts.T("media.query.converted", Texts.T("media.filter.kindChip", video) + ", " + Texts.T("archive.sourceChip", "media-fixture"))
            && page.QueryProblem == Texts.T("media.query.kept", Texts.T("media.query.kept.item", "bogus:x", Texts.T("media.query.reason.unknownField"))),
            state + ": the hint must name what was added and what stayed text: " + page.QueryNote + " / " + page.QueryProblem);
        TextBlock hintNote = C<TextBlock>(page, "MediaQueryNote"), hintProblem = C<TextBlock>(page, "MediaQueryProblem");
        Require(hintNote.IsEffectivelyVisible && hintProblem.IsEffectivelyVisible && hintProblem.Classes.Contains("workspace-error")
            && Avalonia.Automation.AutomationProperties.GetLiveSetting(hintNote) == Avalonia.Automation.AutomationLiveSetting.Polite
            && Avalonia.Automation.AutomationProperties.GetLiveSetting(hintProblem) == Avalonia.Automation.AutomationLiveSetting.Polite, state + ": the hint must be visible and polite.");
        window.UpdateLayout();
        Program.CaptureWindow(window, $"smoke-media-query-hint-{code}-{theme.ToString().ToLowerInvariant()}.png");
        string? copied = null;
        page.CopyTextForTests = text => { copied = text; return Task.CompletedTask; };
        string expectedText = "tag:surface type:video source:media-fixture name:\"bogus:x\"";
        Require(page.FiltersAsText() == expectedText, state + ": the filter as text was " + page.FiltersAsText());
        Ursa.Controls.ClosableTag firstChip = (Ursa.Controls.ClosableTag)page.FilterChips.Children[0];
        Require(firstChip.ContextMenu!.ItemsSource is System.Collections.IEnumerable entries && entries.OfType<MenuItem>().Any(item => item.Name == WorkspaceChips.CopyTextItemName)
            && (page.FilterChips.Parent as Control)?.ContextMenu != null, state + ": the chip and the row must offer Copy filters as text.");
        await page.CopyFiltersAsTextAsync();
        Require(copied == expectedText && page.QueryNote == Texts.T("media.query.copied") && page.QueryProblem == null, state + ": copying must write the query and say so: " + copied);
        // Plain text is not a query: Enter changes nothing and clears the hint.
        name.Text = "plain";
        await UntilAsync(() => page.QueryNote == null);
        name.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
        Require(name.Text == "plain" && page.QueryNote == null && page.QueryProblem == null && page.ActiveFilterCount == 4, state + ": plain text must stay a name filter.");
        name.Text = string.Empty;
        while (page.FilterChips.Children.Count > 1) Remove(page.FilterChips.Children.Count - 1).Execute(null);
        await UntilAsync(() => page.ActiveFilterCount == 1 && page.Tiles.Count == SurfaceTagged + 1);
        Require(name.Text == string.Empty && page.QueryNote == null, state + ": removing the converted chips must restore the tag filter alone.");
    }

    private static async Task FilterSurfaceStateAsync(Window window, MediaPage page, ThemeVariant theme, string code, double width, double height)
    {
        string state = $"{theme}/{code}/{width}x{height}";
        window.RequestedThemeVariant = theme;
        Texts.SetLanguage(code);
        window.Width = width; window.Height = height;
        FilterToggleButton filters = C<FilterToggleButton>(page, "MediaFilters");
        Border surface = C<Border>(page, "MediaFilterSurface"), grid = C<Border>(page, "MediaGridSurface");
        ScrollViewer scroll = C<ScrollViewer>(page, "MediaFilterSurfaceScroll");
        ComboBox sortCombo = C<ComboBox>(page, "MediaSort");
        TextBox name = C<TextBox>(page, "MediaNameFilter");
        ComboBox kind = C<ComboBox>(page, "MediaKindFilter");
        TextBlock status = C<TextBlock>(page, "MediaStatus");
        await UntilAsync(() =>
        {
            window.UpdateLayout();
            return PresentationChecks.MatchesRequestedSize(window, width, height) && grid.Bounds.Height > 0
                && filters.Count == 1 && filters.Label == Texts.T("media.filters");
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

        Require(filters.IsChecked != true && !surface.IsEffectivelyVisible, state + ": filter fields are shown before the toggle.");
        ToggleButton toggle = filters; // S2: the toggle is the ToggleButton itself
        Require(Bounds(filters).Height <= 48, state + ": the collapsed filter toggle still reserves field height: " + Bounds(filters));
        // S2: same look as the neighbouring combo: the same height, a count badge while a filter is active.
        Require(Math.Abs(Bounds(filters).Height - Bounds(sortCombo).Height) <= 1 && filters.IsBadgeVisible && filters.Badge.IsEffectivelyVisible,
            state + ": the filter toggle and the sort combo differ in height or the badge is missing: " + Bounds(filters) + " / " + Bounds(sortCombo));
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
        filters.IsChecked = true;
        await UntilAsync(() => { window.UpdateLayout(); return surface.IsEffectivelyVisible && FocusIn(surface); });
        Rect area = Bounds(surface);
        Console.WriteLine("SMOKE media filter surface geometry: " + JsonSerializer.Serialize(new
        {
            state, scale = window.RenderScaling, surface = area.ToString(), grid = gridClosed.ToString(), toggle = Bounds(filters).ToString(),
            chips = chipsClosed.ToString(), gridOpen = Bounds(grid).ToString(), extent = scroll.Extent.Height, viewport = scroll.Viewport.Height,
            tileContent = tallest, tileHeight = MediaLibraryTileView.TileHeight
        }));
        // S1: inline. The section sits under the status line and the grid starts below it; nothing above moved, nothing is covered.
        Require(Contains(client, area) && area.Top >= statusClosed.Bottom - 1 && Bounds(grid).Top >= area.Bottom - 1 && Bounds(grid).Bottom <= gridClosed.Bottom + 1
            && Bounds(grid).Height > 0 && Bounds(grid).Top > gridClosed.Top, state + ": the filter section is not inline above a pushed-down grid: " + area + " / " + Bounds(grid) + " / " + gridClosed);
        Require(Same(Bounds(page.FilterChips), chipsClosed) && Same(Bounds(status), statusClosed)
            && page.FilterChips.IsEffectivelyVisible && status.IsEffectivelyVisible && chipsClosed.Bottom <= area.Top + 1,
            state + ": applied chips or status moved or are hidden by the open section.");
        Require(page.GetVisualDescendants().OfType<Control>().All(control => control.Name != "MediaFilterClose") && surface.ZIndex == 0
            && !surface.Classes.Contains("workspace-overlay"), state + ": the media filter section must have no Close button and no overlay level.");
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
        Require(filters.IsChecked == true && Bounds(grid).Top >= Bounds(surface).Bottom - 1, state + ": live filtering closed the section or put the grid under it.");

        // Tab stays inside the open surface: from the last field it wraps to the first.
        reset.Focus(NavigationMethod.Tab);
        Key(Avalonia.Input.Key.Tab);
        await UntilAsync(() => FocusIn(surface) && !ReferenceEquals(Focused(), reset));
        // Escape closes an open dropdown first, then the surface, and focus returns to the toggle.
        kind.Focus(NavigationMethod.Tab);
        kind.IsDropDownOpen = true;
        await UntilAsync(() => kind.IsDropDownOpen);
        Key(Avalonia.Input.Key.Escape);
        await UntilAsync(() => !kind.IsDropDownOpen);
        Require(filters.IsChecked == true, state + ": Escape on an open dropdown closed the whole filter surface.");
        kind.Focus(NavigationMethod.Tab);
        Key(Avalonia.Input.Key.Escape);
        await UntilAsync(() => filters.IsChecked != true && !surface.IsEffectivelyVisible && FocusIn(filters));

        // The toggle closes as well and never leaves focus on a hidden field; the grid returns to its place.
        toggle.Focus(NavigationMethod.Tab);
        filters.IsChecked = true;
        await UntilAsync(() => surface.IsEffectivelyVisible && FocusIn(surface));
        kind.Focus(NavigationMethod.Tab);
        filters.IsChecked = false;
        await UntilAsync(() => !surface.IsEffectivelyVisible && FocusIn(filters));
        window.UpdateLayout();
        Require(Same(Bounds(grid), gridClosed), state + ": closing the filter section left the grid shifted.");

        // A programmatic open keeps the current focus (here: a tile).
        page.FocusGrid();
        await UntilAsync(() => FocusIn(page.Grid));
        filters.IsChecked = true;
        await UntilAsync(() => surface.IsEffectivelyVisible);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        Require(FocusIn(page.Grid), state + ": a programmatic open stole focus from the grid.");
        page.CloseFilterSurface(restoreFocus: false);
        await UntilAsync(() => !surface.IsEffectivelyVisible);
        Require(FocusIn(page.Grid), state + ": closing an unfocused surface moved focus away from the grid.");
    }
}
