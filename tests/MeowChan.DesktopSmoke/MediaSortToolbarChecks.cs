using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Desktop;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using MeowChan.Gallery;
using MeowChan.Interfaces;
using MeowChan.Ratings;

namespace MeowChan.DesktopSmoke;

// MEOWCHAN_MEDIA_PAGE_ONLY, S3: the media toolbar is one row in two groups (search and filters left, the order right), the chosen order is
// handed to the shell for saving (and a saved one is restored without saving again), the random order is not kept, "show missing files"
// is a filter (field of the section, chip in the chip row), the less important tools move under More on a narrow page while the order
// stays, and the tag count / rating keys are made for the presentation off the UI thread. Behaviour of the real controls (the pure
// parts are unit tests: MediaSortToolbarContractTests). Private host window and profile; managed input only.
internal static partial class MediaPageChecks
{
    private static async Task SortToolbarAsync(MainWindow owner, string profile)
    {
        new Config().SaveConfig(profile);
        await SeedAsync(profile);
        await using DesktopSessionHost host = new(profile, Array.Empty<IImageBoard>());
        await host.StartAsync().WaitAsync(Deadline);
        string language = Texts.Language;
        Texts.SetLanguage("en");
        List<(MediaLibrarySort Sort, MediaLibrarySortDirection Direction)> saved = new();
        MediaPage page = new(host.OpenArchiveReaderAsync) { SaveSortAsync = (sort, direction) => { saved.Add((sort, direction)); return Task.CompletedTask; } };
        Window window = new() { Width = 1100, Height = 800, Content = page, Title = "Media sort toolbar — diagnostic", ShowInTaskbar = false };
        try
        {
            window.Show(owner);
            window.Activate();
            page.IsAvailable = true;
            await page.ActivateAsync().WaitAsync(Deadline);
            Require(page.Tiles.Count == 3, "The sort toolbar fixture must give three tiles: " + page.Tiles.Count);
            ComboBox sort = C<ComboBox>(page, "MediaSort");
            Button direction = C<Button>(page, "MediaSortDirection"), refresh = C<Button>(page, "MediaRefresh"), layout = C<Button>(page, "MediaLayout"), more = C<Button>(page, "MediaMore");
            Control search = C<Control>(page, "MediaNameFilter"), filters = C<Control>(page, "MediaFilters");
            string[] Order() => page.ShownEntries.Select(entry => entry.Thread.Thread + ":" + (entry.PostId ?? "-")).ToArray();

            // Layout: search and filters left, the order and the tools right, in one row.
            double X(Control control) => control.TranslatePoint(default, page.ToolbarRow)!.Value.X;
            double Y(Control control) => control.TranslatePoint(default, page.ToolbarRow)!.Value.Y;
            // N6: every tool the stage still shows (the last one is More) lies inside the row; nothing is cut off at its right edge.
            void RequireToolbarFits(string stage)
            {
                page.UpdateLayout();
                foreach (Control tool in new Control[] { search, filters, sort, direction, more })
                    Require(!tool.IsVisible || X(tool) + tool.Bounds.Width <= page.ToolbarRow.Bounds.Width + 0.5,
                        "The " + stage + " toolbar must show " + tool.Name + " completely: right edge " + (X(tool) + tool.Bounds.Width) + " of " + page.ToolbarRow.Bounds.Width);
                foreach (Control tool in new Control[] { C<ToggleButton>(page, "MediaBooruMode"), layout, refresh })
                    Require(!tool.IsVisible || X(tool) + tool.Bounds.Width <= page.ToolbarRow.Bounds.Width + 0.5,
                        "The " + stage + " toolbar must show " + tool.Name + " completely.");
            }
            Require(X(search) < X(filters) && X(filters) + filters.Bounds.Width < X(sort) && X(sort) + sort.Bounds.Width <= X(direction)
                && X(direction) + direction.Bounds.Width <= X(refresh) && X(refresh) + refresh.Bounds.Width <= X(more),
                "The toolbar must read search, filters, then (right) order, direction, refresh, more.");
            Require(new[] { search, filters, sort, direction, refresh, more }.Select(Y).Max() - new[] { search, filters, sort, direction, refresh, more }.Select(Y).Min() <= 8,
                "The toolbar must be one row at 1100 wide.");
            Require(X(sort) > page.ToolbarRow.Bounds.Width / 2, "The order belongs to the right half of the row.");
            Require(new Control[] { sort, direction, refresh, more }.All(control => Math.Abs(control.Bounds.Height - 32) < 0.6),
                "The tools of the row are 32 dip high.");
            Require(!C<Button>(page, "MediaExportAll").IsVisible && !C<Button>(page, "MediaManageRatings").IsVisible
                && page.HeaderMoreIds.SequenceEqual(["media.toolbar.export-all", "media.toolbar.manage-ratings"]),
                "Export all and the ratings manager belong under More in every view: " + string.Join(",", page.HeaderMoreIds));
            Require(sort.ItemCount == Enum.GetValues<MediaLibrarySort>().Length, "The criterion box must offer every sort criterion.");

            // Direction: off for the shuffle, and Reversed is the exact mirror of the default order.
            sort.SelectedIndex = (int)MediaLibrarySort.Name;
            string[] byName = Order();
            Require(direction.IsEnabled && byName.Length == 3, "A named order must have a direction.");
            direction.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Require(page.SortDirection == MediaLibrarySortDirection.Reversed && Order().SequenceEqual(byName.Reverse()),
                "Reversed must be the mirror of the default order: " + string.Join(",", byName) + " / " + string.Join(",", Order()));
            sort.SelectedIndex = (int)MediaLibrarySort.Random;
            Require(!direction.IsEnabled, "The random order has no direction.");
            sort.SelectedIndex = (int)MediaLibrarySort.Name;

            // What the user chose is handed over for saving (the shell writes it as a direct preference, except the random order: the last other choice stays).
            Require(saved.SequenceEqual([(MediaLibrarySort.Name, MediaLibrarySortDirection.Default), (MediaLibrarySort.Name, MediaLibrarySortDirection.Reversed),
                (MediaLibrarySort.Random, MediaLibrarySortDirection.Reversed), (MediaLibrarySort.Name, MediaLibrarySortDirection.Reversed)]),
                "The page must report every choice: " + string.Join(";", saved.Select(item => item.Sort + "/" + item.Direction)));
            Require(MediaSortPreference.Format(MediaLibrarySort.Random) == "ByThread"
                && MediaSortPreference.Format(MediaLibrarySort.Random, MediaLibrarySortDirection.Reversed) == "Default", "Random must not be saved.");
            sort.SelectedIndex = 0;
            if (page.SortDirection != MediaLibrarySortDirection.Default) direction.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));

            // Restoring a saved order starts the page with it and saves nothing again; the random order and a stale value fall back.
            int before = saved.Count;
            page.RestoreSort(MediaLibrarySort.Size, MediaLibrarySortDirection.Reversed);
            Require(sort.SelectedIndex == (int)MediaLibrarySort.Size && page.SortDirection == MediaLibrarySortDirection.Reversed && saved.Count == before,
                "A restored order must apply without a save.");
            page.RestoreSort(MediaLibrarySort.Random, MediaLibrarySortDirection.Reversed);
            Require(sort.SelectedIndex == (int)MediaLibrarySort.ByThread && page.SortDirection == MediaLibrarySortDirection.Default && saved.Count == before,
                "A restored random order must read as the default order.");
            Require(Order().SequenceEqual(["200:200", "100:100", "100:-"]), "The default order is by thread: " + string.Join(",", Order()));

            // The missing-files switch is a filter: a field of the section and a chip of the active filters, not part of the toolbar row.
            CheckBox missing = C<CheckBox>(page, "MediaShowMissing");
            Require(missing.GetVisualAncestors().Any(ancestor => ancestor.Name == "MediaFilterSurface") || missing.GetLogicalAncestors().OfType<Control>().Any(ancestor => ancestor.Name == "MediaFilterSurface"),
                "Show missing files must be a field of the filter section.");
            Require(!page.ToolbarRow.GetVisualDescendants().Contains(missing), "Show missing files must not sit in the toolbar row.");
            Require(page.ActiveFilterCount == 0 && !page.FilterChips.IsVisible, "No filter must be active at the start.");
            missing.IsChecked = true;
            string[] Chips() => page.FilterChips.Children.OfType<Ursa.Controls.ClosableTag>().Select(tag => ArchivePage.FilterTagLabel(tag)).ToArray();
            Require(page.ShownEntries.Count == 4 && page.ActiveFilterCount == 1 && page.FilterChips.IsVisible
                && Chips().SequenceEqual([Texts.T("media.filter.missingChip")]),
                "Switching it on must add the chip and show the missing file: " + string.Join(",", Chips()));
            Require(page.FilterResetAll.IsVisible, "Reset all must be offered beside the missing-files chip.");
            Texts.SetLanguage("de");
            await UntilAsync(() => Chips().SequenceEqual(["Mit fehlenden Dateien"]));
            Texts.SetLanguage("en");
            await UntilAsync(() => Chips().SequenceEqual([Texts.T("media.filter.missingChip")]));
            page.ResetFilters();
            Require(missing.IsChecked != true && page.ShownEntries.Count == 3 && page.ActiveFilterCount == 0, "Reset must switch the missing files off.");

            // A narrow page moves the focus-mode tool (M4, first entry) and Refresh (and the shuffle) under More; the order, direction,
            // search and filters stay.
            page.BackgroundPresentEntries = 0;
            window.Width = 640;
            await UntilAsync(() => page.ToolbarLevel == MediaToolbarLayout.Level.Compact);
            Require(sort.IsEffectivelyVisible && direction.IsEffectivelyVisible && search.IsEffectivelyVisible && filters.IsEffectivelyVisible && more.IsEffectivelyVisible
                && !refresh.IsVisible && page.HeaderMoreIds.SequenceEqual(["media.toolbar.tag-list", "media.toolbar.layout", "media.toolbar.focus", "media.toolbar.refresh", "media.toolbar.export-all", "media.toolbar.manage-ratings"]),
                "At 640 the order must stay and Refresh and the Booru mode switch must be under More: " + string.Join(",", page.HeaderMoreIds));
            Require(sort.MinWidth == MediaToolbarLayout.NarrowSortMinWidth && search is { MinWidth: MediaToolbarLayout.SearchMinWidth }
                && X(sort) + sort.Bounds.Width <= page.ToolbarRow.Bounds.Width + 0.5, "The compact row must fit and keep the search at its minimum and shrink the order to its narrow minimum.");
            Require(!C<TextBlock>(page, "MediaSortLabel").IsVisible && !C<ToggleButton>(page, "MediaBooruMode").IsVisible, "The compact row must drop the Sort label and the Booru mode switch.");
            RequireToolbarFits("compact (640)");
            sort.SelectedIndex = (int)MediaLibrarySort.Random;
            // The page presents in the background here (BackgroundPresentEntries = 0); the view re-applies the tools after that.
            await Program.WaitUntilAsync(() => page.HeaderMoreIds.SequenceEqual(["media.toolbar.tag-list", "media.toolbar.layout", "media.toolbar.focus", "media.toolbar.refresh", "media.toolbar.shuffle", "media.toolbar.export-all", "media.toolbar.manage-ratings"]),
                () => "While the order is random the narrow row must also hold Shuffle again under More: " + string.Join(",", page.HeaderMoreIds));
            sort.SelectedIndex = 0;
            window.Width = MediaToolbarLayout.CompactWidth + 80; // inside the Reduced stage whatever the page padding is
            await UntilAsync(() => page.ToolbarLevel == MediaToolbarLayout.Level.Reduced);
            Require(!refresh.IsVisible && sort.MinWidth == MediaToolbarLayout.SortMinWidth, "In the Reduced stage Refresh must stay under More and the order keeps its width.");
            Require(C<ToggleButton>(page, "MediaBooruMode").IsVisible && C<TextBlock>(page, "MediaSortLabel").IsVisible, "The Reduced stage keeps the Sort label and the Booru mode switch.");
            RequireToolbarFits("reduced (" + window.Width + ")");
            window.Width = MediaToolbarLayout.ReducedWidth + 160;
            await UntilAsync(() => page.ToolbarLevel == MediaToolbarLayout.Level.Wide);
            RequireToolbarFits("wide (" + window.Width + ")");
            window.Width = 1100;
            await UntilAsync(() => page.ToolbarLevel == MediaToolbarLayout.Level.Wide);
            Require(refresh.IsVisible && page.HeaderMoreIds.SequenceEqual(["media.toolbar.export-all", "media.toolbar.manage-ratings"]), "A wide page must show Refresh again: " + string.Join(",", page.HeaderMoreIds));

            // The stage constants are row widths, not window widths. Resize the real page until its measured toolbar row is just above/below
            // each boundary; this proves the Layout button follows the same stage and cannot remain stranded on the row when it moves under More.
            await ToolbarBoundariesAsync(page, window, layout, refresh);
            window.Width = 1100;
            await UntilAsync(() => page.ToolbarLevel == MediaToolbarLayout.Level.Wide);

            // The tag count and rating keys are made with the presentation: from this many entries on, off the UI thread (here: always).
            MediaLibraryEntry two = page.Snapshot!.Entries.Single(entry => entry.Thread.Thread == "200"), one = page.Snapshot.Entries.Single(entry => entry.PostId == "100");
            page.ReadUserData = () => new MediaLibraryUserData(null, [], [new MediaRatingEntry(MediaLibraryFilterEngine.AssetKey(one)!, 3), new MediaRatingEntry(MediaLibraryFilterEngine.AssetKey(two)!, 5)]);
            page.RefreshUserData();
            int runs = page.PresentRunCount;
            sort.SelectedIndex = (int)MediaLibrarySort.Rating;
            await page.PresentTask.WaitAsync(Deadline);
            Require(page.PresentRunCount > runs && Order().SequenceEqual(["200:200", "100:100", "100:-"]), "Rating order: best first, unrated last: " + string.Join(",", Order()));
            direction.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            await page.PresentTask.WaitAsync(Deadline);
            Require(Order().SequenceEqual(["100:100", "200:200", "100:-"]), "Reversed rating order keeps the unrated last: " + string.Join(",", Order()));
            sort.SelectedIndex = (int)MediaLibrarySort.TagCount;
            await page.PresentTask.WaitAsync(Deadline);
            Require(Order().Length == 3, "Tag count without readable tags must still show every tile.");
            Console.WriteLine("SMOKE media sort toolbar [en]: one row with search and filters left and the order, direction, refresh and More right (32 dip), export all and ratings under More, every criterion offered, no direction for the shuffle, reversed is the exact mirror, each choice is handed over for saving (random as the default order) and a restored order applies without a save, the missing-files switch is a filter field with a chip (DE/EN) and reset clears it, at 640 and at measured +/-1 dip around the 920/740 row boundaries Refresh, Layout and Shuffle move under More while search, filters, order and direction stay, rating order is computed off the UI thread (best first, unrated last, reversed keeps unrated last).");
        }
        finally { Texts.SetLanguage(language); page.EndLifetime(); window.Close(); }
    }

    private static async Task ToolbarBoundariesAsync(MediaPage page, Window window, Button layout, Button refresh)
    {
        Control row = page.ToolbarRow;
        double X(Control control) => control.TranslatePoint(default, row)!.Value.X;

        async Task SetMeasuredRowWidthAsync(double target)
        {
            // The header margin and the native frame make a requested window width an unreliable proxy for the toolbar row.
            // Correct the requested width from the row's actual DIP bounds until the target is measured by Avalonia.
            for (int attempt = 0; attempt < 5; attempt++)
            {
                page.UpdateLayout();
                double delta = target - row.Bounds.Width;
                if (Math.Abs(delta) <= 0.5) break;
                double before = row.Bounds.Width;
                window.Width += delta;
                // Window bounds can settle to an adjacent whole DIP. Wait for the real row to move, then correct from its new measurement.
                await UntilAsync(() => Math.Abs(row.Bounds.Width - before) > 0.5);
            }
            page.UpdateLayout();
            Require(Math.Abs(row.Bounds.Width - target) <= 1,
                "The smoke could not place the real toolbar row at " + target + " dip: measured " + row.Bounds.Width + ".");
            await UntilAsync(() => page.ToolbarLevel == MediaToolbarLayout.For(row.Bounds.Width));
        }

        async Task CheckBoundaryAsync(double target, MediaToolbarLayout.Level level, bool layoutOnRow, bool refreshOnRow)
        {
            await SetMeasuredRowWidthAsync(target);
            double measured = row.Bounds.Width;
            Require(page.ToolbarLevel == level && page.ToolbarLevel == MediaToolbarLayout.For(measured),
                "At a measured toolbar row width of " + measured + " dip the stage must be " + level + ", not " + page.ToolbarLevel + ".");
            Require(layout.IsEffectivelyVisible == layoutOnRow
                && page.HeaderMoreIds.Contains("media.toolbar.layout") != layoutOnRow,
                "At a measured row width of " + measured + " dip the Layout button must be "
                + (layoutOnRow ? "on the row" : "under More") + ": visible=" + layout.IsEffectivelyVisible
                + ", more=" + string.Join(",", page.HeaderMoreIds));
            Require(refresh.IsEffectivelyVisible == refreshOnRow
                && page.HeaderMoreIds.Contains("media.toolbar.refresh") != refreshOnRow,
                "At a measured row width of " + measured + " dip Refresh must be "
                + (refreshOnRow ? "on the row" : "under More") + ": visible=" + refresh.IsEffectivelyVisible
                + ", more=" + string.Join(",", page.HeaderMoreIds));
            if (layoutOnRow)
                Require(X(layout) + layout.Bounds.Width <= row.Bounds.Width + 0.5,
                    "The Layout button must fit the real toolbar row at " + measured + " dip.");
            Console.WriteLine("SMOKE media toolbar boundary: target " + target + ", measured row " + measured.ToString("0.##")
                + " dip => " + page.ToolbarLevel + "; Layout=" + (layoutOnRow ? "row" : "More")
                + ", Refresh=" + (refreshOnRow ? "row" : "More") + ".");
        }

        // +/- 1 DIP gives an unambiguous real-layout probe for both sides of each strict '<' threshold.
        await CheckBoundaryAsync(MediaToolbarLayout.ReducedWidth + 1, MediaToolbarLayout.Level.Wide, layoutOnRow: true, refreshOnRow: true);
        await CheckBoundaryAsync(MediaToolbarLayout.ReducedWidth - 1, MediaToolbarLayout.Level.Reduced, layoutOnRow: true, refreshOnRow: false);
        await CheckBoundaryAsync(MediaToolbarLayout.CompactWidth + 1, MediaToolbarLayout.Level.Reduced, layoutOnRow: true, refreshOnRow: false);
        await CheckBoundaryAsync(MediaToolbarLayout.CompactWidth - 1, MediaToolbarLayout.Level.Compact, layoutOnRow: false, refreshOnRow: false);
    }
}
