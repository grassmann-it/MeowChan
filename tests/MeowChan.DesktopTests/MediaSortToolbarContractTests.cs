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
        foreach (MediaToolbarLayout.Level level in new[] { MediaToolbarLayout.Level.Reduced, MediaToolbarLayout.Level.Compact })
        {
            Assert.Equal(["media.toolbar.focus", "media.toolbar.refresh", "media.toolbar.shuffle"], MediaToolbarLayout.MovedUnderMore(level));
            Assert.All(MediaToolbarLayout.MovedUnderMore(level), id => Assert.True(MediaToolbarLayout.IsMovedUnderMore(level, id)));
            foreach (string kept in new[] { "media.sort", "media.search", "media.filter.toggle", "media.toolbar.more", "media.toolbar.export-all", null })
                Assert.False(MediaToolbarLayout.IsMovedUnderMore(level, kept), kept);
        }
        Assert.False(MediaToolbarLayout.IsMovedUnderMore(MediaToolbarLayout.Level.Wide, "media.toolbar.refresh"));
        Assert.False(MediaToolbarLayout.IsMovedUnderMore(MediaToolbarLayout.Level.Wide, "media.toolbar.focus"));
        // Every id that can move is a policy row of the media page that the view puts under More as well, so it has a place and an order there.
        Assert.All(MediaToolbarLayout.MovedUnderMore(MediaToolbarLayout.Level.Reduced), id =>
        {
            Desktop.WorkViews.WorkViewEntry entry = Desktop.WorkViews.WorkViewPolicy.Get(id);
            Assert.Equal(Desktop.WorkViews.WorkViewPage.Media, entry.Page);
            Assert.True(entry.OverflowOrder > 0);
        });
    }

    [Fact]
    public void OnlyTheCompactRowShrinksTheSearchFieldAndTheCriterionAndTheyNeverGetNarrowerThanTheirMinimum()
    {
        Assert.Equal(MediaToolbarLayout.SearchMinWidth, MediaToolbarLayout.SearchMin(MediaToolbarLayout.Level.Wide));
        Assert.Equal(MediaToolbarLayout.SearchMinWidth, MediaToolbarLayout.SearchMin(MediaToolbarLayout.Level.Reduced));
        Assert.Equal(MediaToolbarLayout.NarrowSearchMinWidth, MediaToolbarLayout.SearchMin(MediaToolbarLayout.Level.Compact));
        Assert.Equal(MediaToolbarLayout.SortMinWidth, MediaToolbarLayout.SortMin(MediaToolbarLayout.Level.Reduced));
        Assert.Equal(MediaToolbarLayout.NarrowSortMinWidth, MediaToolbarLayout.SortMin(MediaToolbarLayout.Level.Compact));
        Assert.True(MediaToolbarLayout.NarrowSearchMinWidth > 0 && MediaToolbarLayout.NarrowSortMinWidth > 0);
        Assert.True(MediaToolbarLayout.CompactWidth < MediaToolbarLayout.ReducedWidth);
        Assert.True(MediaToolbarLayout.SearchMaxWidth >= MediaToolbarLayout.SearchMinWidth);
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
}
