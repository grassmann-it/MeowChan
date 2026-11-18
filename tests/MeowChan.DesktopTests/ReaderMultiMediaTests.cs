using System.Text.Json;
using Avalonia;
using Avalonia.Input;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.WorkViews;

namespace MeowChan.DesktopTests;

/// <summary>Posts with several files (R11): the two-column grid rules, the sidebar's thumbnail strip window, the settings and the discovery hint.</summary>
[Collection("Localization")]
public sealed class ReaderMultiMediaTests : IDisposable
{
    public ReaderMultiMediaTests() { Texts.Initialize(); Texts.SetLanguage("en"); }
    public void Dispose() { Texts.Initialize(); Texts.SetLanguage("en"); }

    // ---- settings ------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void TheDefaultsAreTwelveThumbnailsAndAGridForTwoHundredEightyCharacters()
    {
        Config config = new();
        Assert.Equal(12, config.ReaderMediaStripLimit);
        Assert.True(config.ReaderMediaGrid);
        Assert.Equal(280, config.ReaderMediaGridTextLimit);
        ReaderMediaPreferences media = ReaderMediaPreferences.From(config);
        Assert.Equal(12, media.StripLimit);
        Assert.True(media.MediaGrid);
        Assert.Equal(280, media.GridTextLimit);
        Assert.Equal(ReaderMediaPreferences.Default.Video, media.Video);
        Assert.Equal(ReaderMediaPreferences.Default, media);
    }

    [Fact]
    public void LimitsHaveDefaultMinimumAndMaximumAndAreClampedOnLoadAndRejectedByValidation()
    {
        Assert.Equal((2, 12, 40), (Config.ReaderMediaStripLimitMinimum, Config.ReaderMediaStripLimitDefault, Config.ReaderMediaStripLimitMaximum));
        Assert.Equal((0, 280, 2000), (Config.ReaderMediaGridTextLimitMinimum, Config.ReaderMediaGridTextLimitDefault, Config.ReaderMediaGridTextLimitMaximum));
        ReaderMediaPreferences clamped = (ReaderMediaPreferences.Default with { StripLimit = 1, GridTextLimit = 99999 }).Clamped();
        Assert.Equal(2, clamped.StripLimit);
        Assert.Equal(2000, clamped.GridTextLimit);
        Config loaded = JsonSerializer.Deserialize<Config>("{\"ReaderMediaStripLimit\":999,\"ReaderMediaGridTextLimit\":-5}")!;
        Assert.Equal(40, loaded.ReaderMediaStripLimit);
        Assert.Equal(0, loaded.ReaderMediaGridTextLimit);
        Assert.Throws<ArgumentOutOfRangeException>(() => new Config { ReaderMediaStripLimit = 1 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new Config { ReaderMediaStripLimit = 41 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new Config { ReaderMediaGridTextLimit = -1 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new Config { ReaderMediaGridTextLimit = 2001 }.Validate());
    }

    [Fact]
    public void CloneAndBothCopiesKeepTheValues()
    {
        Config source = new() { ReaderMediaStripLimit = 7, ReaderMediaGrid = false, ReaderMediaGridTextLimit = 40 };
        Config clone = source.Clone(), copy = new(), settings = new();
        copy.CopyFrom(source);
        settings.CopySettingsFieldsFrom(source);
        foreach (Config config in new[] { clone, copy, settings })
        {
            Assert.Equal(7, config.ReaderMediaStripLimit);
            Assert.False(config.ReaderMediaGrid);
            Assert.Equal(40, config.ReaderMediaGridTextLimit);
        }
    }

    [Fact]
    public void TheGridChoicesRebuildThePostsAndTheStripLimitActsLive()
    {
        ReaderMediaPreferences baseline = ReaderMediaPreferences.Default;
        Assert.True(baseline.SamePostImages(baseline with { StripLimit = 30 }));
        Assert.False(baseline.SamePostImages(baseline with { MediaGrid = false }));
        Assert.False(baseline.SamePostImages(baseline with { GridTextLimit = 100 }));
        ReaderMediaPreferences off = baseline with { MediaGrid = false };
        Assert.True(off.SamePostImages(off with { GridTextLimit = 100 }));
    }

    [Fact]
    public void TheMultiFileSettingsAreOnTheSettingsPageOnlyNotInThePopover()
    {
        // RL1: the gear's popover keeps the display options readers reach for most; these three are set on the settings page (it is one click away).
        string[] ids = ReaderQuickSettings.Fields.Select(field => field.Id).ToArray();
        Assert.DoesNotContain(SettingsFieldId.ReaderMediaStripLimit, ids);
        Assert.DoesNotContain(SettingsFieldId.ReaderMediaGrid, ids);
        Assert.DoesNotContain(SettingsFieldId.ReaderMediaGridTextLimit, ids);
    }

    // ---- the grid --------------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(2, "", true)]
    [InlineData(4, "short text", true)]
    [InlineData(2, "  padded  ", true)]
    [InlineData(1, "", false)]
    [InlineData(0, "", false)]
    public void SeveralImagesAndLittleTextMakeAGrid(int files, string text, bool expected) =>
        Assert.Equal(expected, ReaderMediaMultiRules.UseGrid(ReaderMediaPreferences.Default, files, text));

    [Fact]
    public void TheTextLimitIsInclusiveCountsTrimmedCharactersAndZeroMeansNoText()
    {
        ReaderMediaPreferences media = ReaderMediaPreferences.Default with { GridTextLimit = 10 };
        Assert.True(ReaderMediaMultiRules.UseGrid(media, 3, new string('x', 10)));
        Assert.False(ReaderMediaMultiRules.UseGrid(media, 3, new string('x', 11)));
        Assert.True(ReaderMediaMultiRules.UseGrid(media, 3, "   " + new string('x', 10) + "\n\n"));
        ReaderMediaPreferences none = ReaderMediaPreferences.Default with { GridTextLimit = 0 };
        Assert.True(ReaderMediaMultiRules.UseGrid(none, 2, " \n "));
        Assert.False(ReaderMediaMultiRules.UseGrid(none, 2, "a"));
        Assert.True(ReaderMediaMultiRules.UseGrid(ReaderMediaPreferences.Default, 2, null));
    }

    [Fact]
    public void TheGridSwitchTurnsItOffAndAnOutOfRangeLimitIsClamped()
    {
        Assert.False(ReaderMediaMultiRules.UseGrid(ReaderMediaPreferences.Default with { MediaGrid = false }, 4, ""));
        ReaderMediaPreferences wild = ReaderMediaPreferences.Default with { GridTextLimit = int.MaxValue };
        Assert.True(ReaderMediaMultiRules.UseGrid(wild, 2, new string('x', 2000)));
        Assert.False(ReaderMediaMultiRules.UseGrid(wild, 2, new string('x', 2001)));
    }

    [Theory]
    [InlineData(360, 176)]
    [InlineData(240, 116)]
    [InlineData(540, 266)]
    [InlineData(17, 4)]
    [InlineData(8, 1)]
    [InlineData(0, 1)]
    public void TwoTilesAndTheGapFillTheMediaColumn(int column, int tile)
    {
        Assert.Equal(tile, ReaderMediaMultiRules.TileWidth(column));
        if (column >= 17) Assert.True(2 * tile + ReaderMediaMultiRules.GridSpacing <= column);
    }

    // N7: a tile below the block minimum (140) would not be half the column, so the grid needs two full tiles in the column.
    [Theory]
    [InlineData(360, true)] // the large reader at the default preview width: tiles of 176
    [InlineData(288, true)] // exactly two minimum blocks and the gap
    [InlineData(287, false)]
    [InlineData(240, false)] // the ordinary reader at the default width: tiles of 116
    [InlineData(120, false)]
    [InlineData(0, false)]
    public void TheGridNeedsTwoTilesAtTheBlockMinimumInTheColumn(int column, bool fits)
    {
        Assert.Equal(fits, ReaderMediaMultiRules.TilesFit(column));
        Assert.Equal(fits, ReaderMediaMultiRules.UseGrid(ReaderMediaPreferences.Default, 3, "short", column));
        // Everything else still decides as before: the text, the count and the switch.
        Assert.False(ReaderMediaMultiRules.UseGrid(ReaderMediaPreferences.Default, 3, new string('x', 400), column));
        Assert.False(ReaderMediaMultiRules.UseGrid(ReaderMediaPreferences.Default, 1, "", column));
        Assert.False(ReaderMediaMultiRules.UseGrid(ReaderMediaPreferences.Default with { MediaGrid = false }, 3, "", column));
    }

    // M4: without the grid every tile has a line of its own, however narrow it is; the grid wraps two to a line.
    [Fact]
    public void WithoutTheGridNarrowPortraitTilesStackAndNeverStandBesideEachOther()
    {
        Size[] portraits = [new Size(140, 280), new Size(140, 200), new Size(140, 280)];
        Rect[] stacked = ReaderMediaMultiRules.Place(portraits, 376, grid: false);
        Assert.Equal([new Rect(0, 0, 140, 280), new Rect(0, 280, 140, 200), new Rect(0, 480, 140, 280)], stacked);
        // The same tiles of a grid post, 176 wide in a 360 column: two to a line, the line as high as its highest tile, the third wraps.
        Size[] tiles = [new Size(176, 100), new Size(176, 140), new Size(176, 90)];
        Rect[] wrapped = ReaderMediaMultiRules.Place(tiles, 360, grid: true);
        Assert.Equal([new Rect(0, 0, 176, 140), new Rect(184, 0, 176, 140), new Rect(0, 140, 176, 90)], wrapped);
        // A tile wider than the line still gets a line of its own and is never dropped.
        Assert.Equal([new Rect(0, 0, 500, 10), new Rect(0, 10, 176, 10)], ReaderMediaMultiRules.Place([new Size(500, 10), new Size(176, 10)], 360, grid: true));
        Assert.Empty(ReaderMediaMultiRules.Place([], 360, grid: true));
    }

    // ---- the strip window --------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(0, 4, 12, -1, 0, 4)]
    [InlineData(3, 4, 12, -1, 0, 4)]
    [InlineData(0, 30, 12, -1, 0, 12)]
    [InlineData(15, 30, 12, -1, 9, 12)]
    [InlineData(29, 30, 12, -1, 18, 12)]
    [InlineData(5, 30, 12, 0, 0, 12)]
    [InlineData(12, 30, 12, 0, 6, 12)]
    [InlineData(8, 30, 12, 3, 3, 12)]
    [InlineData(2, 30, 12, 3, 0, 12)]
    [InlineData(1, 30, 1, -1, 0, 2)]
    [InlineData(4, 100, 999, -1, 0, 40)]
    [InlineData(0, 0, 12, -1, 0, 0)]
    public void TheWindowKeepsTheCurrentFileInsideAndStaysPutWhileItDoes(int current, int count, int limit, int previous, int start, int length) =>
        Assert.Equal((start, length), ReaderMediaMultiRules.Window(current, count, limit, previous));

    [Fact]
    public void SteppingThroughAPostNeverLeavesTheWindowWithoutMovingItOnce()
    {
        int start = -1;
        List<int> starts = new();
        for (int current = 0; current < 30; current++)
        {
            (int next, int length) = ReaderMediaMultiRules.Window(current, 30, 10, start);
            Assert.InRange(current, next, next + length - 1);
            if (next != start) starts.Add(next);
            start = next;
        }
        Assert.Equal(starts.Order(), starts);
        Assert.Equal(starts.Distinct().Count(), starts.Count);
        Assert.True(starts.Count <= 30 / 5 + 2, string.Join(",", starts));
    }

    [Fact]
    public void TheStripAppearsForMoreThanOneFile()
    {
        Assert.False(ReaderMediaMultiRules.ShowsStrip(0));
        Assert.False(ReaderMediaMultiRules.ShowsStrip(1));
        Assert.True(ReaderMediaMultiRules.ShowsStrip(2));
    }

    [Theory]
    [InlineData(Key.Left, 2, 4, 1)]
    [InlineData(Key.Right, 2, 4, 3)]
    [InlineData(Key.Right, 3, 4, 3)]
    [InlineData(Key.Left, 0, 4, 0)]
    [InlineData(Key.Home, 2, 4, 0)]
    [InlineData(Key.End, 0, 4, 3)]
    [InlineData(Key.A, 2, 4, 2)]
    public void StripKeysMoveThroughTheWholePostWithoutWrapping(Key key, int current, int count, int expected) =>
        Assert.Equal(expected, ReaderMediaMultiRules.KeyTarget(current, count, key));

    [Fact]
    public void TheCounterAndTheItemNamesAreLocalized()
    {
        Assert.Equal("2 of 4", ReaderMediaMultiRules.CounterText(1, 4));
        Assert.Equal("4 of 4", ReaderMediaMultiRules.CounterText(99, 4));
        Assert.Equal("File 2 of 4: cat.jpg", ReaderMediaMultiRules.ItemName(1, 4, "cat.jpg"));
        Assert.Equal("File 1 of 2: —", ReaderMediaMultiRules.ItemName(0, 2, ""));
        Texts.SetLanguage("de");
        Assert.Equal("2 von 4", ReaderMediaMultiRules.CounterText(1, 4));
        Assert.Equal("Datei 2 von 4: cat.jpg", ReaderMediaMultiRules.ItemName(1, 4, "cat.jpg"));
    }

    // ---- discoverability -----------------------------------------------------------------------------------------------------------

    [Fact]
    public void WhileTheSidebarIsOffAndTheReaderWideEnoughTheViewEntryNamesTheShortcut()
    {
        ReaderMediaPreferences off = ReaderMediaPreferences.Default with { Sidebar = false };
        string? hint = ReaderMediaMultiRules.DiscoveryHint(off, largeReader: true, readerWidth: 1600, "Ctrl+Shift+M");
        Assert.NotNull(hint);
        Assert.Contains("Ctrl+Shift+M", hint);
        // An unknown width (not laid out yet) counts as wide enough; a user who removed the shortcut still gets the sentence.
        Assert.Contains("Ctrl+Shift+M", ReaderMediaMultiRules.DiscoveryHint(off, true, 0, "Ctrl+Shift+M"));
        string? withoutKey = ReaderMediaMultiRules.DiscoveryHint(off, true, 1600, null);
        Assert.NotNull(withoutKey);
        Assert.DoesNotContain("{", withoutKey);
        Assert.Equal(withoutKey, ReaderMediaMultiRules.DiscoveryHint(off, true, 1600, "  "));
    }

    [Fact]
    public void NoHintWhenTheSidebarIsOnTheReaderIsNarrowOrNotTheLargeReaderAndNothingTurnsOnByItself()
    {
        ReaderMediaPreferences off = ReaderMediaPreferences.Default with { Sidebar = false };
        Assert.Null(ReaderMediaMultiRules.DiscoveryHint(off with { Sidebar = true }, true, 1600, "Ctrl+Shift+M"));
        Assert.Null(ReaderMediaMultiRules.DiscoveryHint(off, largeReader: false, 1600, "Ctrl+Shift+M"));
        Assert.Null(ReaderMediaMultiRules.DiscoveryHint(off, true, 1279, "Ctrl+Shift+M"));
        Assert.NotNull(ReaderMediaMultiRules.DiscoveryHint(off, true, 1280, "Ctrl+Shift+M"));
        // The sidebar is on by default (15.11.2026), so the hint only matters to someone who switched it off; it never changes a preference.
        Assert.True(new Config().ReaderMediaSidebar);
        Assert.False(ReaderMediaSidebarRules.IsShown(off, true, 1920));
    }

    // ---- texts and work views ------------------------------------------------------------------------------------------------------

    [Fact]
    public void EveryNewTextExistsInEnglishAndGermanWithTheSamePlaceholders()
    {
        (string Key, int Placeholders)[] keys =
        [
            ("reader.sidebar.strip", 0), ("reader.sidebar.stripItem", 3), ("reader.sidebar.discover", 1), ("reader.sidebar.discoverNoKey", 0),
            ("settings.reader.mediaStripLimit", 0), ("settings.reader.mediaStripLimit.hint", 0), ("settings.reader.mediaGrid", 0),
            ("settings.reader.mediaGrid.hint", 0), ("settings.reader.mediaGridText", 0), ("settings.reader.mediaGridText.hint", 0)
        ];
        foreach (string language in new[] { "en", "de" })
        {
            Texts.SetLanguage(language);
            foreach ((string key, int placeholders) in keys)
            {
                string text = Texts.T(key, "a", "b", "c");
                Assert.NotEqual(key, text);
                Assert.True(text.Length > 3, key);
                if (placeholders == 0) Assert.DoesNotContain("{", text);
            }
        }
    }

    // The strip and grid are content of the sidebar the user turned on, like the inline video: no reader work-view row can reduce or hide them.
    // The sidebar toggle itself is deliberately a direct reader work-view action.
    [Fact]
    public void TheMediaSidebarToggleIsDirectAndNoReaderWorkViewRowCanReduceItsContent()
    {
        WorkViewEntry sidebar = WorkViewPolicy.Get("reader.toolbar.media-sidebar");
        Assert.Equal(Presence.Direct, sidebar.Barebones);
        Assert.Equal(Presence.Direct, sidebar.Simple);
        Assert.Equal(Presence.Direct, sidebar.Advanced);
        Assert.DoesNotContain(WorkViewPolicy.Entries, entry => entry.Page == WorkViewPage.Reader
            && (entry.Id.Contains("strip", StringComparison.Ordinal) || entry.Id.Contains("grid", StringComparison.Ordinal)));
    }
}
