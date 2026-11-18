using System.ComponentModel;
using Avalonia.Collections;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;
using MeowChan.Interfaces;

namespace MeowChan.DesktopTests;

// The thread table's Tags column: where it shows, how it sorts, and that it follows the tag store
// without ever presenting stale tags. Controls need a real window, so the live page is covered by the
// MEOWCHAN_PRIVATE_TAGS_ONLY smoke; everything decidable without one is decided here.
[Collection("Localization")]
public sealed class ArchiveTagColumnTests : IDisposable
{
    private const string Table = "ArchiveThreadTable";

    public ArchiveTagColumnTests() { Texts.Initialize(); Texts.SetLanguage("en"); }
    public void Dispose() { Texts.Initialize(); Texts.SetLanguage("en"); }

    private static ArchiveThreadRow Row(string thread, params string[]? tags)
    {
        ArchiveThreadRow row = new(new ArchiveThreadSummary(new ResourceId("synthetic", "b", thread), "", "", "",
            ThreadSourceState.NotFound, true, DateTimeOffset.UnixEpoch, null, 1, 1));
        if (tags != null) row.SetTags(tags);
        return row;
    }

    private static string[] Sorted(IEnumerable<ArchiveThreadRow> rows, ListSortDirection direction)
    {
        DataGridCollectionView view = new(rows.ToList());
        view.SortDescriptions.Add(DataGridSortDescription.FromComparer(ArchiveThreadRow.TagColumnComparer.Instance, direction));
        return view.Cast<ArchiveThreadRow>().Select(row => row.Summary.Id.Thread!).ToArray();
    }

    [Theory]
    [InlineData("Barebones", false)]
    [InlineData("Simple", false)]
    [InlineData("Advanced", true)]
    public void ColumnIsAbsentInBarebonesHiddenInSimpleAndShownInAdvanced(string mode, bool expected)
    {
        Assert.Equal(expected, ArchivePage.DefaultColumnVisible(Table, mode, "tags"));
        // The chooser can still reach it in Simple; Barebones keeps its compact identifying set.
        Assert.True(ArchivePage.ResolveColumnVisible(Table, mode, new Dictionary<string, bool> { [Table + ".tags"] = true }, "tags"));
        Assert.False(ArchivePage.ResolveColumnVisible(Table, mode, new Dictionary<string, bool> { [Table + ".tags"] = false }, "tags"));
    }

    [Fact]
    public void OtherColumnDefaultsAndTheMediaGridAreUnchanged()
    {
        string[] keys = ["identity", "title", "posts", "media", "source", "work", "lastCapture"];
        Assert.True(ArchivePage.DefaultColumnVisible(Table, "Barebones", "favorite"));
        Assert.Equal(new[] { true, true, true, true }, keys.Take(4).Select(key => ArchivePage.DefaultColumnVisible(Table, "Barebones", key)));
        Assert.Equal(new[] { false, false, false }, keys.Skip(4).Select(key => ArchivePage.DefaultColumnVisible(Table, "Barebones", key)));
        Assert.Equal(new[] { true, true, true, true, true, false, false }, keys.Select(key => ArchivePage.DefaultColumnVisible(Table, "Simple", key)));
        Assert.All(keys, key => Assert.True(ArchivePage.DefaultColumnVisible(Table, "Advanced", key)));
        Assert.False(ArchivePage.DefaultColumnVisible("ArchiveMediaGrid", "Barebones", "tags"));
    }

    [Fact]
    public void PreferencesSavedBeforeTheColumnExistedLoadWithModeDefaultsAndNaturalPlace()
    {
        using TempDirectory directory = new();
        // Shape written by earlier versions: semantic visibility, widths and order without any "tags" key.
        File.WriteAllText(Path.Combine(directory.Path, "settings.json"),
            "{\"WorkspaceMode\":\"Simple\",\"LayoutProfiles\":{\"Simple\":{" +
            "\"ColumnVisibility\":{\"ArchiveThreadTable.work\":true,\"ArchiveThreadTable.source\":false}," +
            "\"ColumnWidths\":{\"ArchiveThreadTable.title\":321,\"ArchiveThreadTable.5\":99}," +
            "\"ColumnOrder\":{\"ArchiveThreadTable.title\":0,\"ArchiveThreadTable.identity\":1,\"ArchiveThreadTable.posts\":2," +
            "\"ArchiveThreadTable.media\":3,\"ArchiveThreadTable.source\":4,\"ArchiveThreadTable.work\":5,\"ArchiveThreadTable.lastCapture\":6}}}}");
        Config loaded = Config.LoadConfig(directory.Path);
        Assert.Null(loaded.LoadWarning);

        Assert.True(ArchivePage.ResolveColumnVisible(Table, "Simple", loaded.ColumnVisibility, "work"));
        Assert.False(ArchivePage.ResolveColumnVisible(Table, "Simple", loaded.ColumnVisibility, "source"));
        Assert.False(ArchivePage.ResolveColumnVisible(Table, "Simple", loaded.ColumnVisibility, "tags"));
        Assert.True(ArchivePage.ResolveColumnVisible(Table, "Advanced", loaded.ColumnVisibility, "tags"));

        string[] keys = ["identity", "title", "posts", "media", "source", "work", "lastCapture", "tags"];
        // Column index -> display position: the saved arrangement stays, the new column follows it.
        Assert.Equal(new[] { 1, 0, 2, 3, 4, 5, 6, 7 }, DisplayPositions(ArchivePage.ResolveColumnOrder(Table, keys, loaded.ColumnOrder)));
        Assert.Equal(Enumerable.Range(0, 8), ArchivePage.ResolveColumnOrder(Table, keys, new Dictionary<string, int>()));
        // A saved position for the new key is honoured like any other; equal positions keep column order.
        Dictionary<string, int> moved = new(loaded.ColumnOrder) { [Table + ".tags"] = 0 };
        Assert.Equal(new[] { 2, 0, 3, 4, 5, 6, 7, 1 }, DisplayPositions(ArchivePage.ResolveColumnOrder(Table, keys, moved)));
    }

    private static int[] DisplayPositions(int[] order) => Enumerable.Range(0, order.Length).Select(index => Array.IndexOf(order, index)).ToArray();

    [Fact]
    public void TableAndCatalogCarryTheColumnAtAStableLastPlace()
    {
        string xaml = Source("Desktop/Views/Archive/ArchivePage.axaml");
        string table = xaml[xaml.IndexOf("x:Name=\"ArchiveThreadTable\"", StringComparison.Ordinal)..];
        table = table[..table.IndexOf("</DataGrid.Columns>", StringComparison.Ordinal)];
        // The last column of the table is the tag column, bound to the row's display text and tooltip.
        // The favourite column (A2) follows it by key; the tag column is the last one before it.
        string beforeFavorite = table[..table.LastIndexOf("<!-- A2:", StringComparison.Ordinal)];
        string lastColumn = beforeFavorite[beforeFavorite.LastIndexOf("<DataGridTemplateColumn ", StringComparison.Ordinal)..];
        Assert.Contains("Header=\"Tags\"", lastColumn);
        Assert.Contains("Text=\"{Binding TagColumnText}\"", lastColumn);
        Assert.Contains("ToolTip.Tip=\"{Binding TagColumnTip}\"", lastColumn);
        string preferences = Source("Desktop/Views/Archive/ArchivePage.ViewPreferences.cs");
        Assert.Contains("\"lastCapture\", \"tags\", \"favorite\", \"foundIn\"];", preferences);
        Assert.Contains("[150, 0, 70, 120, 90, 110, 145, 170, 48, 140];", preferences);
        Assert.Contains("CustomSortComparer = ArchiveThreadRow.TagColumnComparer.Instance", Source("Desktop/Views/Archive/ArchivePage.Tags.cs"));
        // Rows follow every published tag view in place; no reload of the list is involved.
        string tags = Source("Desktop/Views/Archive/ArchivePage.Tags.cs");
        string adopt = tags[tags.IndexOf("private void AdoptPrivateTags(", StringComparison.Ordinal)..];
        adopt = adopt[..adopt.IndexOf("private void RefreshThreadTagRows", StringComparison.Ordinal)];
        Assert.Contains("RefreshThreadTagRows(ArchiveVisibleRows);", adopt);
        Assert.Contains("bool available = _privateTags != null;", tags);
    }

    [Theory]
    [InlineData("en", "Tags")]
    [InlineData("de", "Tags")]
    [InlineData("es", "Etiquetas")]
    [InlineData("fr", "Étiquettes")]
    [InlineData("ru", "Теги")]
    public void HeaderAndUnavailableTooltipAreLocalized(string language, string header)
    {
        Texts.SetLanguage(language);
        Assert.Equal(header, Texts.T("archive.columns.tags"));
        ArchiveThreadRow row = Row("1", "a");
        row.SetTags(null, available: false);
        Assert.Equal(Texts.T("tags.filter.unavailable"), row.TagColumnTip);
        Assert.NotEqual("tags.filter.unavailable", row.TagColumnTip);
        if (language == "de") Assert.Equal("Tags nicht verfügbar", row.TagColumnTip);
    }

    [Fact]
    public void CellShowsSortedNamesWithTheFullListAsTooltipAndNothingWithoutTags()
    {
        ArchiveThreadRow row = Row("1");
        Assert.Equal(string.Empty, row.TagColumnText);
        Assert.Null(row.TagColumnTip);

        row.SetTags(["zeta", "Alpha", "/rpt/"]);
        Assert.Equal("/rpt/, Alpha, zeta", row.TagColumnText);
        Assert.Equal(row.TagColumnText, row.TagColumnTip);
        // The list-mode line keeps vocabulary order; only the column is sorted.
        Assert.Equal("Tags: zeta, Alpha, /rpt/", row.TagsText);
    }

    [Fact]
    public void CellFollowsRenameRemoveAndDeleteInPlaceAndNotifiesOnlyOnRealChanges()
    {
        ArchiveThreadRow row = Row("1", "old");
        List<string?> changed = new();
        row.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        row.SetTags(["old"]);
        Assert.Empty(changed);

        row.SetTags(["renamed"]);
        Assert.Equal("renamed", row.TagColumnText);
        Assert.Contains(nameof(ArchiveThreadRow.TagColumnText), changed);
        Assert.Contains(nameof(ArchiveThreadRow.TagColumnTip), changed);

        row.SetTags(["renamed", "second"]);
        Assert.Equal("renamed, second", row.TagColumnText);
        row.SetTags(["renamed"]);
        Assert.Equal("renamed", row.TagColumnText);
        row.SetTags(null); // last tag removed or the tag deleted
        Assert.Equal(string.Empty, row.TagColumnText);
        Assert.Null(row.TagColumnTip);
        changed.Clear();
        row.SetTags(Array.Empty<string>());
        Assert.Empty(changed);
    }

    [Fact]
    public void UnreadableStoreShowsADashNeverTheLastKnownTagsAndRecovers()
    {
        ArchiveThreadRow row = Row("1", "secret");
        List<string?> changed = new();
        row.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        row.SetTags(null, available: false);
        Assert.Equal("—", row.TagColumnText);
        Assert.Equal("Tags unavailable", row.TagColumnTip);
        Assert.False(row.HasTags);
        Assert.DoesNotContain("secret", row.TagColumnText + row.TagColumnTip + row.TagsText);
        Assert.Contains(nameof(ArchiveThreadRow.TagColumnText), changed);

        // Even if a caller still passes names, an unavailable store never lets them through.
        row.SetTags(["secret"], available: false);
        Assert.Equal("—", row.TagColumnText);
        Assert.False(row.HasTags);

        row.SetTags(["secret"]);
        Assert.Equal("secret", row.TagColumnText);
        row.SetTags(null);
        Assert.Equal(string.Empty, row.TagColumnText); // readable and empty is not the dash
    }

    [Fact]
    public void SortingOrdersBySortedTagNamesAndKeepsThreadsWithoutTagsLastAndListOrderStable()
    {
        ArchiveThreadRow[] rows =
        [
            Row("1"),                          // no tags
            Row("2", "beta"),
            Row("3", "zeta", "alpha"),         // sorts as alpha, zeta
            Row("4", "alpha"),
            Row("5"),                          // no tags
            Row("6", "alpha", "beta"),
            Row("7", "BETA"),                  // equal to 2 ignoring case
            Row("8", "Alpha")                  // equal to 4 ignoring case
        ];
        rows[0].SetTags(null, available: false); // unreadable ranks with "no tags"

        // Equal keys keep list order; elementwise, so ("alpha") < ("alpha","beta") < ("alpha","zeta") < ("beta").
        Assert.Equal(new[] { "4", "8", "6", "3", "2", "7", "1", "5" }, Sorted(rows, ListSortDirection.Ascending));
        // Descending reverses the order of distinct keys (the grid's own header toggle); equal keys stay in list order.
        Assert.Equal(new[] { "1", "5", "2", "7", "3", "6", "4", "8" }, Sorted(rows, ListSortDirection.Descending));
    }

    [Fact]
    public void ComparerTreatsMissingStateAsEqualAndMatchesTheNameOrderOfTheActiveLanguage()
    {
        var comparer = ArchiveThreadRow.TagColumnComparer.Instance;
        Assert.Equal(0, comparer.Compare(Row("1"), Row("2")));
        Assert.True(comparer.Compare(Row("1", "a"), Row("2")) < 0);
        Assert.True(comparer.Compare(Row("1"), Row("2", "a")) > 0);
        Assert.Equal(0, comparer.Compare(Row("1", "a", "b"), Row("2", "B", "A")));
        Assert.True(comparer.Compare(null, Row("2", "a")) > 0);
        // A shorter list that is a prefix of a longer one comes first.
        Assert.True(comparer.Compare(Row("1", "a"), Row("2", "a", "b")) < 0);
    }

    private static string Source(string relative, [System.Runtime.CompilerServices.CallerFilePath] string path = "") =>
        File.ReadAllText(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, "../..", relative)));
}
