using System.Runtime.CompilerServices;
using System.Text.Json;

namespace MeowChan.DesktopTests;

// Static desktop/core seam checks only. The runtime sort semantics live in the X04 core tests;
// this contract prevents the MediaPage toolbar from silently dropping a core criterion or its
// direction/user-order arguments while the shell preferences API is integrated separately.
public sealed class MediaSortToolbarContractTests
{
    private static readonly string[] Criteria =
    {
        "ByThread", "Newest", "Random", "Captured", "Name", "Size", "References", "TagCount", "Rating"
    };

    [Fact]
    public void ToolbarUsesEveryCoreSortCriterionAndKeepsTheOriginalFirstThreeSlots()
    {
        string view = Source("libmeowchan/Gallery/MediaLibraryView.cs");
        string page = Source("Desktop/Views/Media/MediaPage.cs") + Source("Desktop/Views/Media/MediaPage.Sorting.cs");
        foreach (string criterion in Criteria)
        {
            Assert.Contains("MediaLibrarySort." + criterion, view);
            Assert.Contains("MediaLibrarySort." + criterion, page);
        }

        Assert.Contains("_sortDirection", page);
        Assert.Contains("MediaLibraryView.HasDirection(sort)", page);
        Assert.Contains("MediaLibraryView.IsDescendingByDefault(sort)", page);
        Assert.Contains("MediaLibrarySortDirection.Reversed", page);
        Assert.Contains("MediaLibrarySortDirection.Default", page);
    }

    [Fact]
    public void PresentationPassesDirectionAndUserOrderThroughBothCorePathsAndBuildsPresentIndex()
    {
        string page = Source("Desktop/Views/Media/MediaPage.cs");
        Assert.Contains("UserOrderFor(snapshot)", page);
        Assert.Contains("holder.Index.Build(passing, input.Sort, input.IncludeMissing, input.Seed, userData.IsFavorite, token, input.Direction, input.UserOrder)", page);
        Assert.Contains("MediaLibraryGrouping.Build(all, passing, input.Sort, input.IncludeMissing, input.Seed, userData.IsFavorite, input.Direction, input.UserOrder)", page);
        Assert.Contains("MediaLibraryView.PresentIndex(shown)", page);
    }

    [Fact]
    public void LocaleCatalogAndBundledPacksNameAllToolbarControls()
    {
        string catalog = Source("Desktop/Localization/MediaLibraryCatalog.cs");
        foreach (string key in new[]
        {
            "media.sort.captured", "media.sort.name", "media.sort.size", "media.sort.references",
            "media.sort.tagCount", "media.sort.rating", "media.sort.direction",
            "media.sort.direction.ascending", "media.sort.direction.descending", "media.sort.direction.unavailable"
        }) Assert.Contains("\"" + key + "\"", catalog);

        foreach (string file in Directory.EnumerateFiles(Root("Desktop/Localization/Packs"), "*.json"))
        {
            using JsonDocument json = JsonDocument.Parse(File.ReadAllText(file));
            JsonElement strings = json.RootElement.GetProperty("strings");
            Assert.True(strings.TryGetProperty("media.sort.direction", out _), file);
            Assert.True(strings.TryGetProperty("media.sort.rating", out _), file);
        }
    }

    private static string Source(string relative, [CallerFilePath] string testPath = "") => File.ReadAllText(Root(relative, testPath));

    private static string Root(string relative, [CallerFilePath] string testPath = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(testPath)!, "../..", relative));
}
