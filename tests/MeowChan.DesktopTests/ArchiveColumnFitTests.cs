using System.Globalization;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using MeowChan.Interfaces;

namespace MeowChan.DesktopTests;

// A1 (fit widths, numbers and dates never wrap), A2 (the pinned favourite column) and A3 (fit commands) of the archive table.
// Text measurement needs the platform's text engine, so the measured widths are covered by the archive smokes; the geometry rules,
// the migration of saved layouts and the markup contracts are decided here.
[Collection("Localization")]
public sealed class ArchiveColumnFitTests : IDisposable
{
    private const string Table = "ArchiveThreadTable";
    private static readonly string[] Keys = ["identity", "title", "posts", "media", "source", "work", "lastCapture", "tags", "favorite"];

    public ArchiveColumnFitTests() { Texts.Initialize(); Texts.SetLanguage("en"); }
    public void Dispose() { Texts.Initialize(); Texts.SetLanguage("en"); }

    [Fact]
    public void ContentWidthPlusCellPaddingWinsWhenTheHeaderIsNarrower()
    {
        // header 30 + arrow 16 + padding 20 = 66; content 50 + cell padding 24 = 74.
        Assert.Equal(74, ColumnFit.Width(30, 50, 55, 200));
        // the header wins: 80 + 16 + 20 = 116 against 40 + 24 = 64.
        Assert.Equal(116, ColumnFit.Width(80, 40, 55, 200));
    }

    [Fact]
    public void WithoutASortArrowTheHeaderNeedsNoArrowSlot()
    {
        Assert.Equal(100, ColumnFit.Width(80, 0, 55, 200, sortable: false));
        Assert.Equal(116, ColumnFit.Width(80, 0, 55, 200, sortable: true));
        // a text column adds its own 24 dip margin to the cell padding
        Assert.Equal(40 + 24 + 24, ColumnFit.Width(10, 40, 55, 200, ColumnFit.CellPadding + ColumnFit.TextColumnExtraPadding));
    }

    [Theory]
    [InlineData(10, 10, 70, 140, 70)]    // below the minimum: the minimum
    [InlineData(300, 300, 70, 140, 140)] // above the maximum: the maximum
    [InlineData(10, 10, 120, 60, 120)]   // a maximum below the minimum gives the minimum
    public void FitWidthStaysInsideTheLayoutBounds(double header, double content, double min, double max, double expected) =>
        Assert.Equal(expected, ColumnFit.Width(header, content, min, max));

    [Fact]
    public void WidthRoundsUpToWholeDipAndIgnoresNonFiniteMeasures()
    {
        Assert.Equal(75, ColumnFit.Width(0, 50.2, 55, 200));
        Assert.Equal(36, ColumnFit.Width(double.NaN, double.PositiveInfinity, 0, 200));
        Assert.Equal(55, ColumnFit.Width(-4, -9, 55, 200));
    }

    [Fact]
    public void WidestTakesTheLongestSampleAndSkipsEmptyOnes()
    {
        Assert.Equal(7, ColumnFit.Widest(["ab", "", "abcdefg", "abc"], text => text.Length));
        Assert.Equal(0, ColumnFit.Widest([], text => text.Length));
        Assert.Equal(0, ColumnFit.Widest(["x"], _ => double.NaN));
    }

    [Theory]
    [InlineData(0, 5)]
    [InlineData(18, 5)]
    [InlineData(99_999, 5)]
    [InlineData(100_000, 6)]
    [InlineData(12_345_678, 8)]
    public void NumberPatternHasAtLeastFiveWidestDigitsAndGrowsWithTheValue(long value, int digits) =>
        Assert.Equal(new string('8', digits), ColumnFit.NumberPattern(value));

    [Theory]
    [InlineData("en")]
    [InlineData("de")]
    [InlineData("ja")]
    [InlineData("ru")]
    [InlineData("zh-Hans")]
    [InlineData("ko")]
    public void DatePatternsAreTheLongestDateOfTheCulture(string language)
    {
        Texts.SetLanguage(language);
        string[] patterns = ColumnFit.DatePatterns(Texts.Culture);
        Assert.Equal(2, patterns.Length);
        Assert.Equal(new DateTime(2026, 12, 28, 23, 58, 0).ToString("g", Texts.Culture), patterns[0]);
        Assert.All(patterns, pattern => Assert.Contains("58", pattern));
        // the real cells use "g" of the same culture, so the pattern is never shorter than a real value of the year
        ArchiveThreadRow row = new(new ArchiveThreadSummary(new ResourceId("synthetic", "b", "1"), "", "", "",
            ThreadSourceState.NotFound, true, new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 3, 9, 8, 5, 0, TimeSpan.Zero), 1, 1));
        Assert.True(row.LastCapture.Length <= patterns.Max(pattern => pattern.Length));
    }

    [Fact]
    public void CacheKeyChangesWithLanguageCultureFontAndSizes()
    {
        string baseKey = ColumnFit.CacheKey(Table, "en", "en", "Inter", 13, 12);
        Assert.Equal(baseKey, ColumnFit.CacheKey(Table, "en", "en", "Inter", 13, 12));
        Assert.NotEqual(baseKey, ColumnFit.CacheKey(Table, "de", "de", "Inter", 13, 12));
        Assert.NotEqual(baseKey, ColumnFit.CacheKey(Table, "en", "en-GB", "Inter", 13, 12));
        Assert.NotEqual(baseKey, ColumnFit.CacheKey(Table, "en", "en", "Noto", 13, 12));
        Assert.NotEqual(baseKey, ColumnFit.CacheKey(Table, "en", "en", "Inter", 14, 12));
        Assert.NotEqual(baseKey, ColumnFit.CacheKey(Table, "en", "en", "Inter", 13, 11));
    }

    // ---- A2: the pinned favourite column and saved layouts ----

    [Fact]
    public void PinnedColumnComesFirstWithoutSavedOrderAndTheOthersFollowInColumnOrder()
    {
        int[] order = ArchivePage.ResolveColumnOrder(Table, Keys, new Dictionary<string, int>());
        Assert.Equal(new[] { 8, 0, 1, 2, 3, 4, 5, 6, 7 }, order);
    }

    [Fact]
    public void OrdersSavedBeforeTheFavouriteColumnKeepTheirArrangementBehindIt()
    {
        // Eight positions as older versions wrote them: title first, identity second, the rest in place.
        Dictionary<string, int> saved = new()
        {
            [Table + ".title"] = 0, [Table + ".identity"] = 1, [Table + ".posts"] = 2, [Table + ".media"] = 3,
            [Table + ".source"] = 4, [Table + ".work"] = 5, [Table + ".lastCapture"] = 6
        };
        Assert.Equal(new[] { 8, 1, 0, 2, 3, 4, 5, 6, 7 }, ArchivePage.ResolveColumnOrder(Table, Keys, saved));
        // Tags moved to the front in a newer layout (equal positions keep column order): the star still stays before everything.
        saved[Table + ".tags"] = 0;
        Assert.Equal(new[] { 8, 1, 7, 0, 2, 3, 4, 5, 6 }, ArchivePage.ResolveColumnOrder(Table, Keys, saved));
        saved[Table + ".tags"] = -1; // out of range counts as unsaved: natural place
        Assert.Equal(new[] { 8, 1, 0, 2, 3, 4, 5, 6, 7 }, ArchivePage.ResolveColumnOrder(Table, Keys, saved));
    }

    [Fact]
    public void AStoredPositionForThePinnedColumnIsIgnored()
    {
        Dictionary<string, int> saved = new() { [Table + ".favorite"] = 5, [Table + ".title"] = 0, [Table + ".identity"] = 1 };
        int[] order = ArchivePage.ResolveColumnOrder(Table, Keys, saved);
        Assert.Equal(8, order[0]);
        Assert.Equal(new[] { 1, 0 }, order.Skip(1).Take(2));
    }

    [Fact]
    public void TheMediaGridHasNoPinnedColumnAndKeepsItsOrder()
    {
        string[] media = ["name", "size", "version", "rating"];
        Assert.Equal(new[] { 0, 1, 2, 3 }, ArchivePage.ResolveColumnOrder("ArchiveMediaGrid", media, new Dictionary<string, int>()));
        Assert.Equal(new[] { 2, 0, 1, 3 }, ArchivePage.ResolveColumnOrder("ArchiveMediaGrid", media,
            new Dictionary<string, int> { ["ArchiveMediaGrid.version"] = 0, ["ArchiveMediaGrid.name"] = 1, ["ArchiveMediaGrid.size"] = 2 }));
    }

    [Fact]
    public void PreferencesSavedBeforeTheColumnExistedLoadWithTheStarShownAndThePinnedPlace()
    {
        using TempDirectory directory = new();
        File.WriteAllText(Path.Combine(directory.Path, "settings.json"),
            "{\"WorkspaceMode\":\"Barebones\",\"LayoutProfiles\":{\"Barebones\":{" +
            "\"ColumnVisibility\":{\"ArchiveThreadTable.work\":true}," +
            "\"ColumnWidths\":{\"ArchiveThreadTable.title\":321,\"ArchiveThreadTable.5\":99,\"ArchiveThreadTable.posts\":77}," +
            "\"ColumnOrder\":{\"ArchiveThreadTable.title\":0,\"ArchiveThreadTable.identity\":1,\"ArchiveThreadTable.posts\":2," +
            "\"ArchiveThreadTable.media\":3,\"ArchiveThreadTable.source\":4,\"ArchiveThreadTable.work\":5,\"ArchiveThreadTable.lastCapture\":6}}}}");
        Config loaded = Config.LoadConfig(directory.Path);
        Assert.Null(loaded.LoadWarning);
        // A column the saved preferences never heard of follows the mode default, which is shown in every work view.
        foreach (string mode in new[] { "Barebones", "Simple", "Advanced" })
            Assert.True(ArchivePage.ResolveColumnVisible(Table, mode, loaded.ColumnVisibility, "favorite"));
        // The star can be hidden in the chooser and stays hidden.
        Assert.False(ArchivePage.ResolveColumnVisible(Table, "Advanced",
            new Dictionary<string, bool> { [Table + ".favorite"] = false }, "favorite"));
        // Saved widths and the order are untouched; the star only takes the first place.
        Assert.Equal(77, loaded.ColumnWidths[Table + ".posts"]);
        Assert.Equal(99, loaded.ColumnWidths[Table + ".5"]);
        Assert.Equal(new[] { 8, 1, 0, 2, 3, 4, 5, 6, 7 }, ArchivePage.ResolveColumnOrder(Table, Keys, loaded.ColumnOrder));
    }

    [Fact]
    public void FavouriteSortKeyPutsMarkedThreadsFirstAndLoadingLast()
    {
        ArchiveThreadRow Row(string id, bool? favorite)
        {
            ArchiveThreadRow row = new(new ArchiveThreadSummary(new ResourceId("synthetic", "b", id), "", "", "",
                ThreadSourceState.NotFound, true, DateTimeOffset.UnixEpoch, null, 1, 1));
            row.SetThreadFavorite(favorite, favorite != null, null);
            return row;
        }
        Assert.Equal(new[] { 0, 1, 2 }, new[] { Row("1", true), Row("2", false), Row("3", null) }.Select(row => row.FavoriteSortKey));
    }

    // ---- markup contracts (the live table is covered by the archive smokes) ----

    [Fact]
    public void FavouriteColumnIsFixedNarrowSortableAndLeavesTheTitleCell()
    {
        string table = ThreadTableXaml();
        string favorite = table[table.LastIndexOf("<DataGridTemplateColumn ", StringComparison.Ordinal)..];
        Assert.Contains("Header=\"Favorite\"", favorite);
        Assert.Contains("SortMemberPath=\"FavoriteSortKey\"", favorite);
        Assert.Contains("Width=\"48\" MinWidth=\"48\" MaxWidth=\"48\"", favorite);
        Assert.Contains("CanUserResize=\"False\" CanUserReorder=\"False\"", favorite);
        // 12 dip cell padding leaves 24 dip of the 48: the button is a 24 dip square (no negative margin, which the design lint forbids).
        Assert.Contains("<desktop:ArchiveThreadFavoriteButton Width=\"24\" Height=\"24\" MinWidth=\"24\" MinHeight=\"24\"", favorite);
        int title = table.IndexOf("Header=\"Title / ID\"", StringComparison.Ordinal);
        string titleColumn = table[title..table.IndexOf("</DataGridTemplateColumn>", title, StringComparison.Ordinal)];
        Assert.DoesNotContain("ArchiveThreadFavoriteButton", titleColumn);
        // the list mode keeps its own star
        Assert.Contains("<desktop:ArchiveThreadFavoriteButton Grid.Column=\"2\" />", Source("Desktop/Views/Archive/ArchivePage.axaml"));
    }

    [Fact]
    public void NumbersAndDatesAreTemplateColumnsThatNeverWrap()
    {
        string table = ThreadTableXaml();
        Assert.DoesNotContain("<DataGridTextColumn", table);
        string posts = Column(table, "Header=\"Posts\"");
        Assert.Contains("<controls:TrimTipTextBlock Text=\"{Binding Posts}\" TextAlignment=\"Right\" />", posts);
        Assert.Contains("SortMemberPath=\"Posts\"", posts);
        string capture = Column(table, "Header=\"Last capture\"");
        Assert.Contains("<controls:TrimTipTextBlock Text=\"{Binding LastCapture}\" />", capture);
        Assert.Contains("SortMemberPath=\"LastCapture\"", capture);
        string versions = Column(table, "Header=\"Saved versions\"");
        Assert.Contains("TextAlignment=\"Right\" TextWrapping=\"NoWrap\" TextTrimming=\"CharacterEllipsis\"", versions);
        foreach (string header in new[] { "Header=\"Source\"", "Header=\"Work\"" })
            Assert.Contains("TextWrapping=\"NoWrap\" TextTrimming=\"CharacterEllipsis\"", Column(table, header));
        // The XAML widths agree with the fallback table of the code (the code table stays the source).
        Assert.Contains("Header=\"Posts\" SortMemberPath=\"Posts\" Width=\"70\"", posts);
        Assert.Contains("Width=\"120\" MinWidth=\"110\"", versions);
        // Widths come from the one-off measurement; SizeToCells would measure only the realised rows and jump while scrolling.
        Assert.DoesNotContain("SizeToCells", Source("Desktop/Views/Archive/ArchivePage.axaml"));
        Assert.DoesNotContain("SizeToCells", Source("Desktop/Views/Archive/ArchivePage.ViewPreferences.cs"));
        Assert.DoesNotContain("SizeToCells", Source("Desktop/Views/Archive/ArchivePage.ColumnFit.cs"));
    }

    [Fact]
    public void FitCommandsAreOnTheHeaderMenuAndTheColumnsButtonThroughOneBuilder()
    {
        string preferences = Source("Desktop/Views/Archive/ArchivePage.ViewPreferences.cs");
        Assert.Contains("BuildArchiveColumnsMenu(grid, null)", preferences);
        Assert.Contains("archive.columnsFit\"", preferences);
        Assert.Contains("archive.columnsFitAll\"", preferences);
        string fit = Source("Desktop/Views/Archive/ArchivePage.ColumnFit.cs");
        Assert.Contains("ContextRequestedEvent, OnArchiveHeaderContextRequested", fit);
        Assert.Contains("BuildArchiveColumnsMenu(grid, target)", fit);
        // Fitting stores like a drag does: same preference dictionary, same save callback.
        Assert.Contains("SaveArchiveColumnWidthsAsync", fit);
        Assert.Contains("_archiveColumnWidths = next;", fit);
    }

    [Fact]
    public void TheStarIsTheLastKeyAndThePinnedKeySoOlderIndexesStayValid()
    {
        string preferences = Source("Desktop/Views/Archive/ArchivePage.ViewPreferences.cs");
        Assert.Contains("\"lastCapture\", \"tags\", \"favorite\"];", preferences);
        Assert.Equal("favorite", ArchivePage.PinnedColumnKey);
    }

    [Theory]
    [InlineData("en", "Fit column", "Fit all columns", "Favorite")]
    [InlineData("de", "Spalte anpassen", "Alle Spalten anpassen", "Favorit")]
    [InlineData("es", "Ajustar columna", "Ajustar todas las columnas", "Favorito")]
    [InlineData("fr", "Ajuster la colonne", "Ajuster toutes les colonnes", "Favori")]
    [InlineData("ru", "Подогнать столбец по ширине", "Подогнать все столбцы по ширине", "Избранное")]
    [InlineData("zh-Hans", "适应列宽", "适应所有列宽", "收藏")]
    [InlineData("ja", "列の幅を自動調整", "すべての列の幅を自動調整", "お気に入り")]
    [InlineData("ko", "열 너비 맞춤", "모든 열 너비 맞춤", "즐겨찾기")]
    [InlineData("pt-BR", "Ajustar coluna", "Ajustar todas as colunas", "Favorito")]
    [InlineData("cs", "Přizpůsobit sloupec", "Přizpůsobit všechny sloupce", "Oblíbené")]
    [InlineData("it", "Adatta colonna", "Adatta tutte le colonne", "Preferito")]
    [InlineData("pl", "Dopasuj kolumnę", "Dopasuj wszystkie kolumny", "Ulubione")]
    public void NewTextsExistInAllTwelveLanguages(string language, string fit, string fitAll, string favorite)
    {
        Texts.SetLanguage(language);
        Assert.Equal(fit, Texts.T("archive.columnsFit"));
        Assert.Equal(fitAll, Texts.T("archive.columnsFitAll"));
        Assert.Equal(favorite, Texts.T("archive.columns.favorite"));
    }

    private static string ThreadTableXaml()
    {
        string xaml = Source("Desktop/Views/Archive/ArchivePage.axaml");
        string table = xaml[xaml.IndexOf("x:Name=\"ArchiveThreadTable\"", StringComparison.Ordinal)..];
        return table[..table.IndexOf("</DataGrid.Columns>", StringComparison.Ordinal)];
    }

    private static string Column(string table, string header)
    {
        int start = table.LastIndexOf("<DataGridTemplateColumn ", table.IndexOf(header, StringComparison.Ordinal), StringComparison.Ordinal);
        return table[start..table.IndexOf("</DataGridTemplateColumn>", start, StringComparison.Ordinal)];
    }

    private static string Source(string relative, [System.Runtime.CompilerServices.CallerFilePath] string path = "") =>
        File.ReadAllText(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, "../..", relative)));
}
