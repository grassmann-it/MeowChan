using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;

namespace MeowChan.DesktopTests;

/// <summary>Relevance order (default with a text), "Search in" scope and the match group headers of the archive list.</summary>
[Collection("Localization")]
public sealed class ArchiveRelevanceUiTests : IDisposable
{
    public ArchiveRelevanceUiTests() { Texts.Initialize(); Texts.SetLanguage("en"); }
    public void Dispose() { Texts.Initialize(); Texts.SetLanguage("en"); }

    [Fact]
    public void TypingATermSwitchesADefaultSortToRelevanceAndClearingItSwitchesBack()
    {
        Assert.Equal(ArchiveThreadSort.LatestCapture, ArchivePage.DefaultSortFor(""));
        Assert.Equal(ArchiveThreadSort.LatestCapture, ArchivePage.DefaultSortFor("   "));
        Assert.Equal(ArchiveThreadSort.Relevance, ArchivePage.DefaultSortFor("piss"));

        // Untouched sort follows the text, keystroke by keystroke.
        ArchiveThreadSort sort = ArchiveThreadSort.LatestCapture;
        sort = ArchivePage.SortAfterTextChange(sort, "", "p");
        Assert.Equal(ArchiveThreadSort.Relevance, sort);
        sort = ArchivePage.SortAfterTextChange(sort, "p", "pi");
        Assert.Equal(ArchiveThreadSort.Relevance, sort);
        sort = ArchivePage.SortAfterTextChange(sort, "pi", "");
        Assert.Equal(ArchiveThreadSort.LatestCapture, sort);

        // A sort the user chose stays, with and without a text.
        Assert.Equal(ArchiveThreadSort.Title, ArchivePage.SortAfterTextChange(ArchiveThreadSort.Title, "", "piss"));
        Assert.Equal(ArchiveThreadSort.Title, ArchivePage.SortAfterTextChange(ArchiveThreadSort.Title, "piss", ""));
        Assert.Equal(ArchiveThreadSort.OldestFirst, ArchivePage.SortAfterTextChange(ArchiveThreadSort.OldestFirst, "piss", "pis"));
        Assert.Equal(ArchiveThreadSort.LatestCapture, ArchivePage.SortAfterTextChange(ArchiveThreadSort.LatestCapture, "piss", "pis"));
        Assert.Equal(ArchiveThreadSort.Relevance, ArchivePage.SortAfterTextChange(ArchiveThreadSort.Relevance, "", "x"));
    }

    [Fact]
    public void RemovingTheTextChipReturnsADefaultRelevanceSortAndTheScopeChipResetsOnlyTheScope()
    {
        ArchiveSearchSpec ranked = new ArchiveSearchSpec("needle", sources: ["A"], sort: ArchiveThreadSort.Relevance)
            .WithScope(ArchiveSearchScope.TitleAndOpeningPost);
        ArchiveSearchSpec noText = ArchivePage.CopySearch(ranked, "text");
        Assert.Equal("", noText.Text);
        Assert.Equal(ArchiveThreadSort.LatestCapture, noText.Sort);
        Assert.Equal(ArchiveSearchScope.TitleAndOpeningPost, noText.Scope);
        Assert.Equal(["A"], noText.Sources);
        // A chosen sort survives removing the text.
        Assert.Equal(ArchiveThreadSort.Title, ArchivePage.CopySearch(new ArchiveSearchSpec("needle", sort: ArchiveThreadSort.Title), "text").Sort);

        ArchiveSearchSpec allPosts = ArchivePage.CopySearch(ranked, "scope");
        Assert.Equal(ArchiveSearchScope.AllPosts, allPosts.Scope);
        Assert.Equal(ranked.Text, allPosts.Text);
        Assert.Equal(ranked.Sort, allPosts.Sort);
        Assert.Equal(ArchiveSearchScope.TitleAndOpeningPost, ArchivePage.CopySearch(ranked, "source", "A").Scope);
    }

    [Fact]
    public void ANonDefaultScopeIsAnAppliedChipInTheSearchOrder()
    {
        ArchiveSearchSpec spec = new ArchiveSearchSpec("needle").WithScope(ArchiveSearchScope.Title);
        var chips = ArchivePage.AppliedFilterLabels(spec);
        Assert.Equal([("text", null, "Text: needle"), ("scope", null, "Search in: Title")], chips);
        Assert.DoesNotContain(ArchivePage.AppliedFilterLabels(new ArchiveSearchSpec("needle")), chip => chip.Field == "scope");
        Assert.Contains("Search in: Title and opening post", SavedArchiveSearchDescription.Describe(
            new ArchiveSearchSpec("needle", sort: ArchiveThreadSort.Relevance).WithScope(ArchiveSearchScope.TitleAndOpeningPost)));
        Assert.Contains("Sort: Relevance", SavedArchiveSearchDescription.Describe(new ArchiveSearchSpec("needle", sort: ArchiveThreadSort.Relevance)));

        // The field lists the narrowest scope first; an unknown index is the default.
        foreach (ArchiveSearchScope scope in Enum.GetValues<ArchiveSearchScope>())
            Assert.Equal(scope, ArchivePage.TextScopeFromIndex(ArchivePage.TextScopeToIndex(scope)));
        Assert.Equal(ArchiveSearchScope.Title, ArchivePage.TextScopeFromIndex(0));
        Assert.Equal(ArchiveSearchScope.AllPosts, ArchivePage.TextScopeFromIndex(-1));
    }

    [Fact]
    public void EachTierGetsOneHeaderWithItsCountOverTheWholeResult()
    {
        ArchiveSearchMatchTier?[] page =
        [
            ArchiveSearchMatchTier.Title, ArchiveSearchMatchTier.Title, ArchiveSearchMatchTier.OpeningPost,
            ArchiveSearchMatchTier.OtherPosts, ArchiveSearchMatchTier.OtherPosts
        ];
        string?[] headers = ArchivePage.MatchGroupHeaders(page, [12, 1, 40]);
        Assert.Equal(["In title · 12", null, "In opening post · 1", "In other posts · 40", null], headers);
        // A later page that starts inside a tier names it again on its first row.
        Assert.Equal(["In other posts · 40", null], ArchivePage.MatchGroupHeaders(page[3..], [12, 1, 40]));
        // An unranked result has no headers at all.
        Assert.All(ArchivePage.MatchGroupHeaders(new ArchiveSearchMatchTier?[3], null), Assert.Null);
        Assert.Equal("Opening post", ArchivePage.MatchTierLabel(ArchiveSearchMatchTier.OpeningPost));
    }

    [Fact]
    public void TheHeaderIsPartOfTheRowTemplateSoKeyboardAndSelectionOnlySeeThreads()
    {
        string xaml = File.ReadAllText(Path("Desktop/Views/Archive/ArchivePage.axaml"));
        // The list's item template: header first, then the thread row; the header is no item, takes no focus and swallows clicks.
        Match list = Regex.Match(xaml, "<ListBox x:Name=\"ArchiveThreads\".*?</ListBox>", RegexOptions.Singleline);
        Assert.True(list.Success);
        Match template = Regex.Match(list.Value, "<ListBox.ItemTemplate>.*?</ListBox.ItemTemplate>", RegexOptions.Singleline);
        Assert.Contains("IsVisible=\"{Binding HasMatchGroupHeader}\"", template.Value);
        Assert.Contains("PointerPressed=\"OnArchiveMatchGroupHeaderPressed\"", template.Value);
        Assert.Contains("DoubleTapped=\"OnArchiveMatchGroupHeaderTapped\"", template.Value);
        Assert.DoesNotContain("Focusable=\"True\"", template.Value);
        Assert.DoesNotContain("ItemsSource", list.Value.Replace(template.Value, ""));
        // The rows the list and the table show are the thread rows only: the page never inserts separator items.
        string code = string.Join("\n", Directory.EnumerateFiles(Path("Desktop/Views/Archive"), "ArchivePage*.cs").Select(File.ReadAllText));
        Assert.DoesNotContain("MatchGroupSeparator", code);
        Assert.Contains("list.ItemsSource = rows; table.ItemsSource = rows;", code);
        // The table shows the tier as its own column, after the favourite key.
        Assert.Contains("SortMemberPath=\"FoundInSortKey\"", xaml);
        Assert.Equal(ArchivePage.FoundInColumnKey, "foundIn");
    }

    [Fact]
    public void RowsCarryTheirTierAndHeaderWithoutAffectingIdentity()
    {
        ArchiveThreadSummary summary = new(new ResourceId("4chan", "g", "1"), "https://boards.4chan.org/g/thread/1", "t", "/m",
            MeowChan.Interfaces.ThreadSourceState.Active, true, DateTimeOffset.UnixEpoch, null, 1, 1);
        ArchiveThreadRow row = new(summary);
        List<string?> changed = new();
        row.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        row.SetMatchGroup(ArchiveSearchMatchTier.Title, "In title · 1", "Title");
        Assert.True(row.HasMatchGroupHeader);
        Assert.Equal(0, row.FoundInSortKey);
        Assert.Contains(nameof(ArchiveThreadRow.MatchGroupHeader), changed);
        row.SetMatchGroup(null, null, "");
        Assert.False(row.HasMatchGroupHeader);
        Assert.Equal(3, row.FoundInSortKey);
        Assert.Same(summary, row.Summary);
    }

    private static string Path(string relative, [CallerFilePath] string source = "") =>
        System.IO.Path.GetFullPath(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(source)!, "../..", relative));
}
