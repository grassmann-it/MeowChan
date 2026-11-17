using System.Text.Json;
using MeowChan.Desktop;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.WorkViews;
using MeowChan.Gallery;

namespace MeowChan.DesktopTests;

// Behaviour of the media toolbar's pure parts (S06): what is saved of the order, how the row adapts to its width, and that the text
// of the new chip exists in every language. What needs the Avalonia platform (the controls themselves, the reversed order on the
// page, moving tools under More) is checked by the media-sort-toolbar smoke; the runtime sort semantics live in the core tests
// (MediaLibrarySortDeterminismTests, MediaLibraryUserOrderTests).
public sealed class MediaSortToolbarContractTests
{
    [Theory]
    [InlineData("Name", "Name")]
    [InlineData("name", "Name")]
    [InlineData(" Rating ", "Rating")]
    [InlineData("TagCount", "TagCount")]
    [InlineData("Random", "ByThread")] // never kept: reads back as the default order
    [InlineData("random", "ByThread")]
    [InlineData("", "ByThread")]
    [InlineData(null, "ByThread")]
    [InlineData("Sideways", "ByThread")]
    [InlineData("3", "ByThread")] // a number is no name, although Enum.TryParse would take it
    [InlineData("99", "ByThread")]
    public void ASavedCriterionReadsBackOrFallsBackToTheDefaultOrder(string? saved, string expected) =>
        Assert.Equal(Enum.Parse<MediaLibrarySort>(expected), MediaSortPreference.ParseSort(saved));

    // Wave-3 smoke regression (media-page): a restored Random/Reversed pair became "by thread, reversed"; the fallback takes both parts.
    [Theory]
    [InlineData(2)]  // Random (the enums are internal, so the cases are their values)
    [InlineData(99)] // unknown
    [InlineData(-1)]
    public void ARestoredOrderThatFallsBackTakesTheDefaultDirectionToo(int sort)
    {
        Assert.Equal(MediaLibrarySort.Random, (MediaLibrarySort)2);
        Assert.Equal((MediaLibrarySort.ByThread, MediaLibrarySortDirection.Default),
            MediaSortPreference.Applicable((MediaLibrarySort)sort, MediaLibrarySortDirection.Reversed));
        // A kept criterion keeps its direction; an unknown direction reads as the default one.
        Assert.Equal((MediaLibrarySort.Size, MediaLibrarySortDirection.Reversed), MediaSortPreference.Applicable(MediaLibrarySort.Size, MediaLibrarySortDirection.Reversed));
        Assert.Equal((MediaLibrarySort.Size, MediaLibrarySortDirection.Default), MediaSortPreference.Applicable(MediaLibrarySort.Size, (MediaLibrarySortDirection)7));
        Assert.Contains("(sort, direction) = MediaSortPreference.Applicable(sort, direction);", File.ReadAllText(Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(SourceFile())!, "../..", "Desktop/Views/Media/MediaPage.Sorting.cs"))));
    }

    private static string SourceFile([System.Runtime.CompilerServices.CallerFilePath] string file = "") => file;

    [Fact]
    public void EveryCriterionExceptRandomSurvivesAFormatAndParseRoundTrip()
    {
        foreach (MediaLibrarySort sort in Enum.GetValues<MediaLibrarySort>())
        {
            MediaLibrarySort expected = sort == MediaLibrarySort.Random ? MediaLibrarySort.ByThread : sort;
            Assert.Equal(expected, MediaSortPreference.ParseSort(MediaSortPreference.Format(sort)));
            foreach (MediaLibrarySortDirection direction in Enum.GetValues<MediaLibrarySortDirection>())
            {
                (MediaLibrarySort restoredSort, MediaLibrarySortDirection restoredDirection) =
                    MediaSortPreference.Restore(MediaSortPreference.Format(sort), MediaSortPreference.Format(sort, direction));
                Assert.Equal(expected, restoredSort);
                // The shuffle has no direction; a criterion that has one keeps what the user chose.
                Assert.Equal(sort == MediaLibrarySort.Random ? MediaLibrarySortDirection.Default : direction, restoredDirection);
            }
        }
    }

    [Fact]
    public void ADirectionOnlyCountsForACriterionThatHasOne()
    {
        Assert.Equal("Reversed", MediaSortPreference.Format(MediaLibrarySort.Size, MediaLibrarySortDirection.Reversed));
        Assert.Equal("Default", MediaSortPreference.Format(MediaLibrarySort.Size, MediaLibrarySortDirection.Default));
        Assert.Equal("Default", MediaSortPreference.Format(MediaLibrarySort.Random, MediaLibrarySortDirection.Reversed));
        Assert.Equal(MediaLibrarySortDirection.Reversed, MediaSortPreference.ParseDirection("reversed"));
        Assert.Equal(MediaLibrarySortDirection.Default, MediaSortPreference.ParseDirection("Sideways"));
        Assert.Equal(MediaLibrarySortDirection.Default, MediaSortPreference.ParseDirection(null));
        // Hand-edited settings: a direction beside the default criterion and a direction for an unknown criterion.
        Assert.Equal((MediaLibrarySort.ByThread, MediaLibrarySortDirection.Reversed), MediaSortPreference.Restore(null, "Reversed"));
        Assert.Equal((MediaLibrarySort.ByThread, MediaLibrarySortDirection.Reversed), MediaSortPreference.Restore("Random", "Reversed")); // the default criterion has a direction
    }

    [Fact]
    public void ChoosingRandomKeepsTheLastOtherChoiceForTheNextStart()
    {
        Config config = new();
        Assert.True(MediaSortPreference.Remember(config, MediaLibrarySort.Size, MediaLibrarySortDirection.Reversed));
        Assert.Equal(("Size", "Reversed"), (config.MediaSort, config.MediaSortDirection));
        // Random (with whatever direction the page held) writes nothing: the settings still say Size, reversed.
        Assert.False(MediaSortPreference.Remember(config, MediaLibrarySort.Random, MediaLibrarySortDirection.Reversed));
        Assert.False(MediaSortPreference.Remember(config, MediaLibrarySort.Random, MediaLibrarySortDirection.Default));
        Assert.Equal(("Size", "Reversed"), (config.MediaSort, config.MediaSortDirection));
        Assert.Equal((MediaLibrarySort.Size, MediaLibrarySortDirection.Reversed), MediaSortPreference.Restore(config.MediaSort, config.MediaSortDirection));
        // The next real choice replaces it, also back to the default order.
        Assert.True(MediaSortPreference.Remember(config, MediaLibrarySort.ByThread, MediaLibrarySortDirection.Default));
        Assert.Equal(("ByThread", "Default"), (config.MediaSort, config.MediaSortDirection));
        // A fresh profile that only ever shuffled starts with the default order.
        Config fresh = new();
        Assert.False(MediaSortPreference.Remember(fresh, MediaLibrarySort.Random, MediaLibrarySortDirection.Default));
        Assert.Equal((MediaLibrarySort.ByThread, MediaLibrarySortDirection.Default), MediaSortPreference.Restore(fresh.MediaSort, fresh.MediaSortDirection));
        Assert.Throws<ArgumentNullException>(() => MediaSortPreference.Remember(null!, MediaLibrarySort.Name, MediaLibrarySortDirection.Default));
    }

    [Fact]
    public void TheSettingsFileKeepsTheOrderAndNormalisesWhatIsHandEdited()
    {
        using TempDirectory directory = new();
        string path = Path.Combine(directory.Path, "settings.json");
        File.WriteAllText(path, "{\"RecheckTimer\":120}");
        Config loaded = Config.LoadConfig(directory.Path);
        Assert.Equal("ByThread", loaded.MediaSort);
        Assert.Equal("Default", loaded.MediaSortDirection);

        Config changed = loaded.Clone();
        changed.MediaSort = "Rating";
        changed.MediaSortDirection = "Reversed";
        changed.SaveConfig();
        Config roundTrip = Config.LoadConfig(directory.Path);
        Assert.Equal("Rating", roundTrip.MediaSort);
        Assert.Equal("Reversed", roundTrip.MediaSortDirection);
        Assert.Equal("Rating", roundTrip.Clone().MediaSort);

        // The random order is never written; a hand-edited unknown value, a number or a wrong JSON type read as the defaults.
        changed.MediaSort = "Random";
        Assert.Equal("ByThread", changed.MediaSort);
        File.WriteAllText(path, "{\"RecheckTimer\":120,\"MediaSort\":\"Random\",\"MediaSortDirection\":\"Up\"}");
        loaded = Config.LoadConfig(directory.Path);
        Assert.Equal(("ByThread", "Default"), (loaded.MediaSort, loaded.MediaSortDirection));
        File.WriteAllText(path, "{\"RecheckTimer\":120,\"MediaSort\":{},\"MediaSortDirection\":7}");
        loaded = Config.LoadConfig(directory.Path);
        Assert.Null(loaded.LoadWarning);
        Assert.Equal(120, loaded.RecheckTimer);
        Assert.Equal(("ByThread", "Default"), (loaded.MediaSort, loaded.MediaSortDirection));
        File.WriteAllText(path, "{\"MediaSort\":\"Size\",\"MediaSortDirection\":\"Reversed\"}");
        loaded = Config.LoadConfig(directory.Path);
        Assert.Equal(("Size", "Reversed"), (loaded.MediaSort, loaded.MediaSortDirection));
    }

    [Theory]
    [InlineData(2000, "Wide")]
    [InlineData(MediaToolbarLayout.ReducedWidth, "Wide")]
    [InlineData(MediaToolbarLayout.ReducedWidth - 1, "Reduced")]
    [InlineData(MediaToolbarLayout.CompactWidth, "Reduced")]
    [InlineData(MediaToolbarLayout.CompactWidth - 1, "Compact")]
    [InlineData(300, "Compact")]
    [InlineData(0, "Wide")] // not measured yet
    [InlineData(double.NaN, "Wide")]
    [InlineData(double.PositiveInfinity, "Wide")]
    public void TheToolbarLevelFollowsThePageWidth(double width, string expected) =>
        Assert.Equal(Enum.Parse<MediaToolbarLayout.Level>(expected), MediaToolbarLayout.For(width));

    [Fact]
    public void ANarrowRowMovesTheLessImportantToolsUnderMoreAndNeverTheOrderTheSearchOrTheFilterToggle()
    {
        Assert.Empty(MediaToolbarLayout.MovedUnderMore(MediaToolbarLayout.Level.Wide));
        // N6: Reduced moves the three first tools; Compact adds the tag list switch (MP5, formerly "Booru mode") and the Layout menu and drops the "Sort" prefix.
        Assert.Equal(["media.toolbar.focus", "media.toolbar.refresh", "media.toolbar.shuffle"], MediaToolbarLayout.MovedUnderMore(MediaToolbarLayout.Level.Reduced));
        Assert.Equal(["media.toolbar.focus", "media.toolbar.refresh", "media.toolbar.shuffle", "media.toolbar.tag-list", "media.toolbar.layout"], MediaToolbarLayout.MovedUnderMore(MediaToolbarLayout.Level.Compact));
        foreach (string layoutTool in new[] { "media.toolbar.tag-list", "media.toolbar.layout" })
        {
            Assert.False(MediaToolbarLayout.IsMovedUnderMore(MediaToolbarLayout.Level.Reduced, layoutTool));
            Assert.True(MediaToolbarLayout.IsMovedUnderMore(MediaToolbarLayout.Level.Compact, layoutTool));
        }
        Assert.True(MediaToolbarLayout.ShowsSortLabel(MediaToolbarLayout.Level.Wide) && MediaToolbarLayout.ShowsSortLabel(MediaToolbarLayout.Level.Reduced));
        Assert.False(MediaToolbarLayout.ShowsSortLabel(MediaToolbarLayout.Level.Compact));
        foreach (MediaToolbarLayout.Level level in new[] { MediaToolbarLayout.Level.Reduced, MediaToolbarLayout.Level.Compact })
        {
            Assert.All(MediaToolbarLayout.MovedUnderMore(level), id => Assert.True(MediaToolbarLayout.IsMovedUnderMore(level, id)));
            foreach (string kept in new[] { "media.sort", "media.search", "media.filter.toggle", "media.toolbar.more", "media.toolbar.export-all", null })
                Assert.False(MediaToolbarLayout.IsMovedUnderMore(level, kept), kept);
        }
        Assert.False(MediaToolbarLayout.IsMovedUnderMore(MediaToolbarLayout.Level.Wide, "media.toolbar.refresh"));
        Assert.False(MediaToolbarLayout.IsMovedUnderMore(MediaToolbarLayout.Level.Wide, "media.toolbar.focus"));
        // Every id that can move is a policy row of the media page that the view puts under More as well, so it has a place and an order there.
        Assert.All(MediaToolbarLayout.MovedUnderMore(MediaToolbarLayout.Level.Compact), id =>
        {
            Desktop.WorkViews.WorkViewEntry entry = Desktop.WorkViews.WorkViewPolicy.Get(id);
            Assert.Equal(Desktop.WorkViews.WorkViewPage.Media, entry.Page);
            Assert.True(entry.OverflowOrder > 0);
        });
    }

    // Tom (15.11.2026): More is shown only while something sits under it, and its menu holds exactly those tools. Export all and the saved-ratings
    // manager are under More in every view, so the standard page keeps the button at every width; a tool the view leaves out is in no menu.
    [Theory]
    [InlineData("Wide", false)]
    [InlineData("Reduced", true)]
    [InlineData("Compact", true)]
    public void AToolOfTheRowSitsUnderMoreWhenTheViewOrTheWidthPutsItThere(string levelName, bool movedByWidth)
    {
        MediaToolbarLayout.Level level = Enum.Parse<MediaToolbarLayout.Level>(levelName);
        // A button the view shows directly leaves the row only with the width.
        Assert.Equal(movedByWidth, MediaToolbarLayout.IsUnderMore(level, "media.toolbar.focus", Presence.Direct));
        // The view's own overflow is under More at every width; a tool the view leaves out entirely is under it at none.
        Assert.True(MediaToolbarLayout.IsUnderMore(level, "media.toolbar.export-all", Presence.Overflow));
        Assert.False(MediaToolbarLayout.IsUnderMore(level, "media.toolbar.focus", Presence.Hidden));
        Assert.False(MediaToolbarLayout.IsUnderMore(level, "media.toolbar.export-all", Presence.Hidden));
        // The order, the search and the filter toggle are never under More.
        foreach (string kept in new[] { "media.sort", "media.search", "media.filter.toggle" })
            Assert.False(MediaToolbarLayout.IsUnderMore(level, kept, Presence.Required), kept);
    }

    [Fact]
    public void MoreIsEmptyOnlyWhenNoToolIsUnderItAtTheWidthAndInTheView()
    {
        string[] header = ["media.toolbar.tag-list", "media.toolbar.layout", "media.toolbar.focus", "media.toolbar.refresh", "media.toolbar.shuffle",
            "media.toolbar.booru", "media.toolbar.queue-walk", "media.toolbar.tagging-page", "media.toolbar.queue-all", "media.toolbar.folders",
            "media.toolbar.export-all", "media.toolbar.manage-ratings"];
        string[] Under(MediaToolbarLayout.Level level, WorkView view) => header
            .Where(id => MediaToolbarLayout.IsUnderMore(level, id, WorkViewPolicy.Resolve(id, view))).ToArray();
        foreach (WorkView view in WorkViewSetting.All)
        {
            // The standard tool set: the two entries that live under More in every view keep the button at every width ...
            foreach (MediaToolbarLayout.Level level in Enum.GetValues<MediaToolbarLayout.Level>())
                Assert.Contains("media.toolbar.export-all", Under(level, view));
            // ... and the width adds the tools of its stage on top of what the view holds back, never fewer.
            foreach (string moved in MediaToolbarLayout.MovedUnderMore(MediaToolbarLayout.Level.Reduced))
                Assert.Contains(moved, Under(MediaToolbarLayout.Level.Reduced, view));
            foreach (string moved in MediaToolbarLayout.MovedUnderMore(MediaToolbarLayout.Level.Compact))
                Assert.Contains(moved, Under(MediaToolbarLayout.Level.Compact, view));
            Assert.True(Under(MediaToolbarLayout.Level.Wide, view).Length <= Under(MediaToolbarLayout.Level.Reduced, view).Length);
            Assert.True(Under(MediaToolbarLayout.Level.Reduced, view).Length <= Under(MediaToolbarLayout.Level.Compact, view).Length);
        }
        // A tool set whose entries the view shows directly or leaves out has nothing under More on a wide row: the button is not shown there,
        // and it appears with the first tool the width moves.
        Assert.False(header.Any(id => MediaToolbarLayout.IsUnderMore(MediaToolbarLayout.Level.Wide, id, Presence.Direct)));
        Assert.False(header.Any(id => MediaToolbarLayout.IsUnderMore(MediaToolbarLayout.Level.Compact, id, Presence.Hidden)));
        Assert.True(header.Any(id => MediaToolbarLayout.IsUnderMore(MediaToolbarLayout.Level.Reduced, id, Presence.Direct)));
    }

    // The button is shown from the same list its menu is filled from; opening it with nothing in it closes the bare frame again.
    [Fact]
    public void TheMoreButtonAndItsMenuComeFromOneListAndNoEmptyMenuStaysOpen()
    {
        string view = Source("Desktop/Views/Media/MediaPage.WorkView.cs");
        Assert.Contains("MediaToolbarLayout.IsUnderMore(_toolbarLevel,", view);
        Assert.Contains("UpdateMore(_more, HeaderOverflow())", view);
        Assert.Contains("FillMore((MenuFlyout)_more.Flyout!, HeaderOverflow())", view);
        Assert.Contains("more.IsVisible = items.Count > 0;", view);
        Assert.Contains("if (((MenuFlyout)_more.Flyout!).Items.Count == 0) Dispatcher.UIThread.Post(() => _more.Flyout?.Hide());", view);
    }

    [Fact]
    public void OnlyTheCompactRowShrinksTheCriterionAndTheSearchNeverGetsNarrowerThanItsMinimum()
    {
        // The search keeps its minimum in every stage (the tools leave the row instead); only the criterion has a narrow minimum.
        foreach (MediaToolbarLayout.Level level in Enum.GetValues<MediaToolbarLayout.Level>())
            Assert.Equal(MediaToolbarLayout.SearchMinWidth, MediaToolbarLayout.SearchMin(level));
        Assert.Equal(MediaToolbarLayout.SortMinWidth, MediaToolbarLayout.SortMin(MediaToolbarLayout.Level.Wide));
        Assert.Equal(MediaToolbarLayout.SortMinWidth, MediaToolbarLayout.SortMin(MediaToolbarLayout.Level.Reduced));
        Assert.Equal(MediaToolbarLayout.NarrowSortMinWidth, MediaToolbarLayout.SortMin(MediaToolbarLayout.Level.Compact));
        Assert.True(MediaToolbarLayout.NarrowSortMinWidth > 0 && MediaToolbarLayout.NarrowSortMinWidth < MediaToolbarLayout.SortMinWidth);
        Assert.True(MediaToolbarLayout.CompactWidth < MediaToolbarLayout.ReducedWidth);
        Assert.True(MediaToolbarLayout.SearchMaxWidth >= MediaToolbarLayout.SearchMinWidth);
    }

    // Tom (15.11.2026): the search used to stop at 480 dip and left a hole between "Filters" and the order on a wide window. It fills the free
    // width from 240 dip up to a fixed cap; UX review 17.11.2026: the cap no longer follows the selection line under it (690 dip, the search and the
    // filter toggle then stand as one deliberate group at 1920 dip), and the free-space column keeps the group gap before the order.
    [Fact]
    public void TheSearchFillsTheFreeWidthFrom240To690DipAndTheSpacerKeepsTheGroupGap()
    {
        Assert.Equal(240, MediaToolbarLayout.SearchMinWidth);
        Assert.Equal(690, MediaToolbarLayout.SearchMaxWidth);
        Assert.Equal(24, MediaToolbarLayout.GroupGap);
        string toolbar = Source("Desktop/Views/Media/MediaPage.Toolbar.cs");
        // Search column (star, fixed cap) | filter toggle | free space (at least the group gap) | the order | view tools; no fixed width for the search.
        Assert.Matches("ColumnDefinitions\\(\"\\d+\\*,Auto,\\*,Auto,Auto\"\\)", toolbar);
        Assert.Contains("Name = \"MediaToolbarQuery\"", toolbar);
        Assert.Contains("Name = \"MediaToolbarSort\"", toolbar);
        Assert.Contains("Avalonia.Controls.Grid.SetColumn(order, 3)", toolbar);
        Assert.Contains("Avalonia.Controls.Grid.SetColumn(right, 4)", toolbar);
        Assert.Contains("Name = \"MediaToolbarEnd\"", toolbar);
        Assert.Contains("tools.ColumnDefinitions[0].MaxWidth = MediaToolbarLayout.SearchMaxWidth;", toolbar);
        Assert.Contains("tools.ColumnDefinitions[2].MinWidth = MediaToolbarLayout.GroupGap;", toolbar);
        Assert.Contains("ColumnDefinitions[0].MinWidth = _nameFilter.MinWidth", toolbar);
        // The tools in visual order: the filter toggle first, then the shuffle, the criterion (its "Sort" is a prefix inside it), the direction, and More last.
        var group = System.Text.RegularExpressions.Regex.Match(toolbar, "foreach \\(Control control in new Control\\[\\] \\{([^}]*)\\}\\)");
        Assert.True(group.Success, "BuildToolbar changed shape.");
        string[] order = group.Groups[1].Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("_filters", order[0]);
        Assert.Equal(["_filters", "_shuffle", "_sort", "_sortDirection", "_refresh"], order[..5]);
        Assert.Equal("_more", order[^1]);
        Assert.DoesNotContain("_sortLabel", toolbar);
        Assert.DoesNotContain("tools.Children.Add(_filters)", toolbar);
        // No column spacing that the free-space column would double; the order leads with the free-space column, not with a margin of its own.
        Assert.DoesNotMatch("ColumnSpacing\\s*=", toolbar);
        Assert.Contains("Name = \"MediaToolbarSort\", Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };", toolbar);
    }

    // The width arithmetic is pure (MediaToolbarLayout); the real layout is the media header stability smoke (12 languages, 616 to 1896 dip).
    private const double Filters = 118, Order = 330, ViewTools = 300;

    [Theory]
    [InlineData(1896, 0)] // a 1920 window without the panel
    [InlineData(1896, 414)] // with the panel open on the right (400 dip panel)
    [InlineData(1560, 0)]
    public void TheSearchStopsAtItsFixedCapOnAWideRowWhateverTheSelectionLineShows(double row, double reserve)
    {
        MediaToolbarLayout.Alignment aligned = MediaToolbarLayout.Align(row, reserve, Filters, Order, ViewTools);
        Assert.Equal(MediaToolbarLayout.SearchMaxWidth, aligned.SearchRight, 0.5);
        Assert.True(aligned.Gap >= MediaToolbarLayout.GroupGap, "gap " + aligned.Gap);
    }

    [Fact]
    public void TheSearchGivesUpWidthBeforeTheGroupGapDoes()
    {
        // 1280 with the panel open: the search gives way (to what is left, never below its minimum), the gap stays exactly the group gap.
        MediaToolbarLayout.Alignment aligned = MediaToolbarLayout.Align(1256, 414, Filters, Order, ViewTools);
        Assert.InRange(aligned.SearchRight, MediaToolbarLayout.SearchMinWidth, MediaToolbarLayout.SearchMaxWidth - 1);
        Assert.Equal(MediaToolbarLayout.GroupGap, aligned.Gap, 0.5);
        Assert.True(aligned.SearchRight + Filters + MediaToolbarLayout.GroupGap + Order <= aligned.OrderRight + 0.5);
    }

    [Theory]
    [InlineData(414)] // panel 400 dip: wider than the view tools, so the order ends at the grid card's right edge
    [InlineData(520)]
    public void TheOrderEndsAtTheGridsRightEdgeWhenTheMediaPanelIsWiderThanTheViewTools(double reserve)
    {
        MediaToolbarLayout.Alignment aligned = MediaToolbarLayout.Align(1896, reserve, Filters, Order, ViewTools);
        Assert.Equal(1896 - reserve, aligned.OrderRight, 0.5);
        Assert.True(aligned.OrderRight <= aligned.ViewToolsLeft + 0.5);
    }

    [Fact]
    public void WithoutAPanelOrWithNarrowPanelTheOrderSitsDirectlyLeftOfTheViewTools()
    {
        foreach (double reserve in new[] { 0, 120, ViewTools - 1 })
        {
            MediaToolbarLayout.Alignment aligned = MediaToolbarLayout.Align(1896, reserve, Filters, Order, ViewTools);
            Assert.Equal(aligned.ViewToolsLeft, aligned.OrderRight, 0.5);
        }
    }

    // Every row from the narrowest page to a wide one, with every panel room: nothing of the row overlaps another group while the row can hold
    // the three groups and the gap at all (the width stages decide below that), the filter toggle keeps the group gap from the order, and the order
    // never passes the view tools.
    [Fact]
    public void NoGroupOverlapsAnotherAndTheGroupGapHoldsAtAnyRowWidthOrPanelRoom()
    {
        double minimum = MediaToolbarLayout.SearchMinWidth + Filters + MediaToolbarLayout.GroupGap + Order + ViewTools;
        for (double row = MediaToolbarLayout.NarrowestRowWidth; row <= 2000; row += 7)
            for (double reserve = 0; reserve <= 900; reserve += 25)
            {
                MediaToolbarLayout.Alignment aligned = MediaToolbarLayout.Align(row, reserve, Filters, Order, ViewTools);
                Assert.True(aligned.OrderRight <= aligned.ViewToolsLeft + 0.01, $"row {row}, reserve {reserve}: the order passes the view tools.");
                if (row >= minimum)
                    Assert.True(aligned.Gap >= MediaToolbarLayout.GroupGap - 0.01, $"row {row}, reserve {reserve}: the gap is {aligned.Gap}.");
                Assert.True(aligned.SearchRight >= MediaToolbarLayout.SearchMinWidth && aligned.SearchRight <= MediaToolbarLayout.SearchMaxWidth);
            }
    }

    // The "Sort" prefix inside the criterion yields first: the stage allows it (not Compact) and the row has room for it beside everything else.
    [Fact]
    public void TheSortPrefixShowsOnlyWhereTheStageAllowsItAndTheRowHasRoomForIt()
    {
        const double prefix = 40;
        double needed = MediaToolbarLayout.SearchMinWidth + Filters + MediaToolbarLayout.GroupGap + Order + prefix + ViewTools;
        Assert.True(MediaToolbarLayout.ShowsSortPrefix(MediaToolbarLayout.Level.Wide, needed, Filters, Order, ViewTools, prefix));
        Assert.False(MediaToolbarLayout.ShowsSortPrefix(MediaToolbarLayout.Level.Wide, needed - 1, Filters, Order, ViewTools, prefix));
        Assert.True(MediaToolbarLayout.ShowsSortPrefix(MediaToolbarLayout.Level.Reduced, 2000, Filters, Order, ViewTools, prefix));
        Assert.False(MediaToolbarLayout.ShowsSortPrefix(MediaToolbarLayout.Level.Compact, 2000, Filters, Order, ViewTools, prefix));
        // A row that is not measured yet follows the stage.
        Assert.True(MediaToolbarLayout.ShowsSortPrefix(MediaToolbarLayout.Level.Wide, 0, Filters, Order, ViewTools, prefix));
        // The box keeps its value area's minimum; the prefix's room comes on top while it shows.
        Assert.Equal(MediaToolbarLayout.SortMinWidth + prefix, PrefixedComboBox.MinWidthFor(MediaToolbarLayout.SortMinWidth, true, prefix));
        Assert.Equal(MediaToolbarLayout.NarrowSortMinWidth, PrefixedComboBox.MinWidthFor(MediaToolbarLayout.NarrowSortMinWidth, false, prefix));
        // Inside the box: the prefix needs room for itself, the gap and the whole value; an open (unmeasured) width always has it.
        Assert.True(PrefixedComboBox.PrefixFits(40 + PrefixedComboBox.PrefixGap + 100, 40, 100));
        Assert.False(PrefixedComboBox.PrefixFits(40 + PrefixedComboBox.PrefixGap + 98, 40, 100));
        Assert.True(PrefixedComboBox.PrefixFits(double.PositiveInfinity, 40, 100));
    }

    [Fact]
    public void ThePanelRoomOfTheOrderIsTheRoomTheHeadersLastLineKeepsForThePanelHeader()
    {
        // A 400 dip panel at the right (its column keeps the 12 dip page edge, the handle is 6 dip, the grid card keeps 8 dip beside it): the same
        // 414 dip that MediaPage.Layout.cs reserves for the panel header on the header's last line.
        Assert.Equal(400 + 14, MediaToolbarLayout.GridRightReserve(400 + 12 + 6, 8, 12));
        // The tag list on the same side adds its column and handle; without any block the grid card keeps the page margin, so there is no room.
        Assert.Equal(400 + 14 + 260 + 12 + 6, MediaToolbarLayout.GridRightReserve(400 + 12 + 6 + 260 + 12 + 6, 8, 12));
        Assert.Equal(0, MediaToolbarLayout.GridRightReserve(0, 12, 12));
    }

    // UX review 17.11.2026: the row depends on its width and stage only. Nothing measures the selection actions line any more (its counter and the
    // "index is being built" reason made the search and the filter toggle jump); the tools' column minimum still comes from the pure rules.
    [Fact]
    public void TheAlignmentIsWiredWithoutAFixedWidthAndWithoutTheSelectionLine()
    {
        string alignment = Source("Desktop/Views/Media/MediaPage.ToolbarAlignment.cs");
        Assert.Contains("MediaToolbarLayout.ViewToolsColumnMin(_gridRightReserve, tools, MediaToolbarLayout.Slack(", alignment);
        Assert.Contains("ApplySortPrefix();", alignment);
        Assert.DoesNotContain("_selectionRow", alignment);
        Assert.DoesNotContain("SearchTargetWidth", alignment);
        Assert.DoesNotContain("columns[0].MaxWidth", alignment);
        Assert.DoesNotContain("ScheduleSelectionRowMeasure", Source("Desktop/Views/Media/MediaPage.Selection.cs"));
        Assert.DoesNotContain("_headerRoomLeft", Source("Desktop/Views/Media/MediaPage.PanelAlignment.cs"));
        string layout = Source("Desktop/Views/Media/MediaPage.Layout.cs");
        Assert.Contains("_gridRightReserve = MediaToolbarLayout.GridRightReserve(", layout);
        Assert.Contains("ApplyToolbarAlignment();", layout);
        Assert.DoesNotContain("_headerRoomLeft", layout);
        // The view tools stay at the page edge above the panel while their column grows.
        Assert.Contains("HorizontalAlignment = HorizontalAlignment.Right", Source("Desktop/Views/Media/MediaPage.Toolbar.cs"));
    }

    // The stage ladder in the order the tools leave the row, with the search and the filter toggle on neither list (the new layout keeps both).
    [Fact]
    public void TheStagesKeepTheSearchAndTheFilterToggleAndMoveTheToolsOutInTheDocumentedOrder()
    {
        Assert.Equal(["media.toolbar.focus", "media.toolbar.refresh", "media.toolbar.shuffle"], MediaToolbarLayout.MovedUnderMore(MediaToolbarLayout.Level.Reduced));
        Assert.Equal(["media.toolbar.focus", "media.toolbar.refresh", "media.toolbar.shuffle", "media.toolbar.tag-list", "media.toolbar.layout"],
            MediaToolbarLayout.MovedUnderMore(MediaToolbarLayout.Level.Compact));
        foreach (MediaToolbarLayout.Level level in Enum.GetValues<MediaToolbarLayout.Level>())
        {
            Assert.False(MediaToolbarLayout.IsMovedUnderMore(level, "media.search"));
            Assert.False(MediaToolbarLayout.IsMovedUnderMore(level, "media.filter.toggle"));
            Assert.False(MediaToolbarLayout.IsMovedUnderMore(level, "media.sort"));
            Assert.False(MediaToolbarLayout.IsMovedUnderMore(level, "media.toolbar.more"));
        }
        // The thresholds are measured widths of the row (smoke): the ladder is ordered and leaves the stages room (the Compact stage fits the narrowest row).
        Assert.True(MediaToolbarLayout.NarrowestRowWidth < MediaToolbarLayout.CompactWidth);
        Assert.True(MediaToolbarLayout.CompactWidth < MediaToolbarLayout.ReducedWidth);
        Assert.Equal(MediaToolbarLayout.Level.Compact, MediaToolbarLayout.For(MediaToolbarLayout.NarrowestRowWidth));
    }

    [Fact]
    public void EveryBundledLanguageTranslatesTheMissingFilesChip()
    {
        string english = BuiltInCatalog.English["media.filter.missingChip"];
        Assert.Equal("Including missing files", english);
        Assert.NotEqual(english, BuiltInCatalog.German["media.filter.missingChip"]);
        foreach (string code in new[] { "es", "fr", "ru", "zh-Hans", "ja", "ko", "pt-BR", "cs", "it", "pl" })
        {
            using Stream stream = typeof(Texts).Assembly.GetManifestResourceStream("MeowChan.Desktop.Localization.Packs." + code + ".json")!;
            using JsonDocument document = JsonDocument.Parse(stream);
            string? text = document.RootElement.GetProperty("strings").GetProperty("media.filter.missingChip").GetString();
            Assert.False(string.IsNullOrWhiteSpace(text), code);
            Assert.NotEqual(english, text);
        }
    }

    // The search tooltip was one 240 character run that wrapped to six or more lines at the 360 dip tooltip width (WorkspaceStyles: ToolTip MaxWidth,
    // wrapping TextBlock) and reached into the selection bar. Now the heading, the syntax in three short lines and one closing sentence: five lines.
    // The heading names what free words search (file name, thread, board, source; review 15.11.2026) and fits one tooltip line.
    [Fact]
    public void TheSearchTooltipListsTheFilterSyntaxInThreeShortLinesInEveryLanguage()
    {
        Dictionary<string, string> tips = new() { ["en"] = BuiltInCatalog.English["media.query.tip"], ["de"] = BuiltInCatalog.German["media.query.tip"] };
        foreach (string code in new[] { "es", "fr", "ru", "zh-Hans", "ja", "ko", "pt-BR", "cs", "it", "pl" })
        {
            using Stream stream = typeof(Texts).Assembly.GetManifestResourceStream("MeowChan.Desktop.Localization.Packs." + code + ".json")!;
            using JsonDocument document = JsonDocument.Parse(stream);
            tips[code] = document.RootElement.GetProperty("strings").GetProperty("media.query.tip").GetString()!;
        }
        foreach ((string code, string tip) in tips)
        {
            string[] lines = tip.Split('\n');
            Assert.True(lines.Length == 5, code + ": heading, three syntax lines, closing sentence: " + lines.Length);
            Assert.All(lines[1..4], line => Assert.True(line.Length <= 40, code + ": a syntax line must fit the 360 dip tooltip without wrapping: " + line));
            string syntax = string.Join(" ", lines[1..4]);
            foreach (string token in new[] { "type:video", "sound:none", "source:4chan", "board:g", "rating:>=3", "fav", "missing", "untagged", "thread:1234" })
                Assert.Contains(token, syntax);
            Assert.True(lines[0].Length <= 42, code + ": the heading must fit one tooltip line: " + lines[0]);
            Assert.True(tip.Length <= 230, code + ": the tooltip must stay short: " + tip.Length);
            Assert.DoesNotContain("  ", tip);
        }
        string styles = Source("Desktop/WorkspaceStyles.axaml");
        Assert.Contains("<Style Selector=\"ToolTip\"><Setter Property=\"MaxWidth\" Value=\"360\" /></Style>", styles); // the global tooltip width this one reuses
    }

    private static string Source(string relative, [System.Runtime.CompilerServices.CallerFilePath] string sourcePath = "") =>
        File.ReadAllText(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourcePath)!, "../..", relative)));

    // Wave-6 smoke regression: the Layout button (media.toolbar.layout) moved under More in the Compact stage (MediaToolbarLayout) but
    // stayed visible in the row, because ApplyToolbarLevel did not list it. Every tool the layout rules move must be one the page hides.
    [Fact]
    public void EveryToolTheCompactStageMovesUnderMoreIsHiddenByApplyToolbarLevelAndListedByTheHeaderOverflow()
    {
        string view = Source("Desktop/Views/Media/MediaPage.WorkView.cs"), toolbar = Source("Desktop/Views/Media/MediaPage.Toolbar.cs");
        // field -> work-view id, from the SetId lines
        Dictionary<string, string> ids = new(StringComparer.Ordinal);
        foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(view, "WorkViewElement\\.SetId\\((_\\w+), \"([^\"]+)\"\\)"))
            ids[match.Groups[2].Value] = match.Groups[1].Value;
        var hidden = System.Text.RegularExpressions.Regex.Match(toolbar, "foreach \\(Control tool in new Control\\[\\] \\{([^}]*)\\}\\)");
        Assert.True(hidden.Success, "ApplyToolbarLevel changed shape.");
        string[] hiddenFields = hidden.Groups[1].Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var overflow = System.Text.RegularExpressions.Regex.Match(view, "HeaderOverflow\\(\\) => Overflowed\\(([^)]*)\\)");
        Assert.True(overflow.Success, "HeaderOverflow changed shape.");
        string[] overflowFields = overflow.Groups[1].Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        foreach (MediaToolbarLayout.Level level in new[] { MediaToolbarLayout.Level.Reduced, MediaToolbarLayout.Level.Compact })
            foreach (string id in MediaToolbarLayout.MovedUnderMore(level))
            {
                Assert.True(ids.TryGetValue(id, out string? field), id + " has no work-view id on the media page.");
                Assert.True(hiddenFields.Contains(field), id + " (" + field + ") moves under More at " + level + " but ApplyToolbarLevel does not hide it.");
                Assert.True(overflowFields.Contains(field), id + " (" + field + ") moves under More at " + level + " but HeaderOverflow does not list it.");
            }
        // And the other way: nothing is hidden by ApplyToolbarLevel that the layout rules never move.
        Assert.Contains("_layoutButton", hiddenFields);
        Assert.True(MediaToolbarLayout.IsMovedUnderMore(MediaToolbarLayout.Level.Compact, "media.toolbar.layout"));
        Assert.False(MediaToolbarLayout.IsMovedUnderMore(MediaToolbarLayout.Level.Reduced, "media.toolbar.layout"));
        Assert.False(MediaToolbarLayout.IsMovedUnderMore(MediaToolbarLayout.Level.Wide, "media.toolbar.layout"));
        Assert.Equal(hiddenFields.Length, MediaToolbarLayout.MovedUnderMore(MediaToolbarLayout.Level.Compact).Count);
    }
}
