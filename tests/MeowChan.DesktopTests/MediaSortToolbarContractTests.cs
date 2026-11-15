using System.Text.Json;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;
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
        // N6: Reduced moves the three first tools; Compact adds the tag list switch (MP5, formerly "Booru mode") and the Layout menu and drops the "Sort" label.
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

    // Tom (15.11.2026): the search used to stop at 480 dip and left a hole between "Filters" and the order on a wide window. It now fills the free
    // width from 240 to 900 dip, and the filter toggle moved into the right group, directly in front of the order.
    [Fact]
    public void TheSearchFillsTheFreeWidthFrom240To900DipAndTheFilterToggleOpensTheRightGroupInFrontOfTheOrder()
    {
        Assert.Equal(240, MediaToolbarLayout.SearchMinWidth);
        Assert.Equal(900, MediaToolbarLayout.SearchMaxWidth);
        string toolbar = Source("Desktop/Views/Media/MediaPage.Toolbar.cs");
        // Search column (star, capped at the maximum) | free space | right group; no fixed width for the search.
        Assert.Matches("ColumnDefinitions\\(\"\\d+\\*,\\*,Auto\"\\)", toolbar);
        Assert.Contains("tools.ColumnDefinitions[0].MaxWidth = MediaToolbarLayout.SearchMaxWidth;", toolbar);
        Assert.Contains("ColumnDefinitions[0].MinWidth = _nameFilter.MinWidth", toolbar);
        // The right group: the filter toggle first, then the shuffle, the Sort label and the criterion, the direction, and More last.
        var group = System.Text.RegularExpressions.Regex.Match(toolbar, "foreach \\(Control control in new Control\\[\\] \\{([^}]*)\\}\\)");
        Assert.True(group.Success, "BuildToolbar changed shape.");
        string[] order = group.Groups[1].Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("_filters", order[0]);
        Assert.Equal(["_filters", "_shuffle", "_sortLabel", "_sort", "_sortDirection", "_refresh"], order[..6]);
        Assert.Equal("_more", order[^1]);
        Assert.DoesNotContain("tools.Children.Add(_filters)", toolbar);
        // The gap in front of the right group is one 8 dip step; there is no column spacing that the free-space column would double.
        Assert.DoesNotMatch("ColumnSpacing\\s*=", toolbar);
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
            Assert.True(tip.Length <= 205, code + ": the tooltip must stay short: " + tip.Length);
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
