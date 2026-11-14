using System.Globalization;
using System.Text.RegularExpressions;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.Stats;
using MeowChan.Desktop.WorkViews;

namespace MeowChan.DesktopTests;

/// <summary>The "Header graph" setting: default, what each value draws in each work view, persistence and the accessible name.</summary>
[Collection("Localization")]
public sealed class StatsHeaderGraphTests : IDisposable
{
    private static readonly CultureInfo En = CultureInfo.GetCultureInfo("en");
    private const long MiB = 1024 * 1024;

    public StatsHeaderGraphTests() { Texts.Initialize(); Texts.SetLanguage("en"); }
    public void Dispose() { Texts.Initialize(); Texts.SetLanguage("en"); }

    [Fact]
    public void TheDefaultIsAutomatic() => Assert.Equal("Auto", new Config().StatsHeaderGraph);

    [Theory]
    [InlineData("Auto", "Auto")]
    [InlineData("Network", "Network")]
    [InlineData("NetworkAndDisk", "NetworkAndDisk")]
    [InlineData("network", "Network")]
    [InlineData(" NETWORKANDDISK ", "NetworkAndDisk")]
    [InlineData("disk", "Disk")]
    [InlineData(" sidebyside", "SideBySide")]
    [InlineData("TEXT", "Text")]
    [InlineData("Both", "Auto")]
    [InlineData("", "Auto")]
    [InlineData(null, "Auto")]
    public void AnyTextIsNormalizedToOneOfTheSixValues(string? input, string expected)
    {
        Assert.Equal(expected, IoStatsDisplay.NormalizeHeaderGraph(input));
        Config config = new() { StatsHeaderGraph = input! };
        Assert.Equal(expected, config.StatsHeaderGraph);
    }

    [Theory]
    // Automatic draws both graphs whenever there is room, in every work view; explicit values are taken literally.
    [InlineData("Auto", true)]
    [InlineData("Network", false)]
    [InlineData("NetworkAndDisk", true)]
    [InlineData("Disk", true)]
    [InlineData("SideBySide", true)]
    [InlineData("Text", false)]
    [InlineData("Off", false)]
    [InlineData("nonsense", true)]
    public void TheHeaderDrawsTheDiskGraphBySettingAlone(string setting, bool disk) =>
        Assert.Equal(disk, IoStatsDisplay.HeaderShowsDisk(setting));

    [Theory]
    [InlineData("Auto", true)]
    [InlineData("NetworkAndDisk", true)]
    [InlineData("nonsense", true)]
    [InlineData("Network", false)]
    [InlineData("network", false)]
    [InlineData("Off", false)]
    public void TheDiskValueShowsInEveryViewUnlessTheSettingIsNetworkOrOff(string setting, bool shown) =>
        Assert.Equal(shown, IoStatsDisplay.HeaderShowsDiskValue(setting));

    // (setting, window width) -> net value, disk value, net graph, disk graph, arrangement
    [Theory]
    [InlineData("Auto", 1100.0, true, true, true, true, "Rows")]
    [InlineData("Auto", 1000.0, true, true, true, true, "Rows")]
    [InlineData("Auto", 900.0, true, true, false, false, "TextRows")]
    [InlineData("Auto", 1400.0, true, true, true, true, "GraphBlocks")]
    [InlineData("Auto", 1380.0, true, true, true, true, "GraphBlocks")]
    [InlineData("Auto", 1280.0, true, true, true, true, "GraphBlocks")]
    [InlineData("Auto", 1279.0, true, true, true, true, "Rows")]
    [InlineData("Network", 1100.0, true, false, true, false, "GraphBlocks")]
    [InlineData("Network", 900.0, true, false, false, false, "TextBlocks")]
    [InlineData("Disk", 1100.0, false, true, false, true, "GraphBlocks")]
    [InlineData("Disk", 900.0, false, true, false, false, "TextBlocks")]
    [InlineData("NetworkAndDisk", 1100.0, true, true, true, true, "Rows")]
    [InlineData("NetworkAndDisk", 900.0, true, true, false, false, "TextRows")]
    [InlineData("SideBySide", 1400.0, true, true, true, true, "GraphBlocks")]
    [InlineData("SideBySide", 1280.0, true, true, true, true, "GraphBlocks")]
    // Too narrow for two blocks: side by side falls back to stacked rows, then to labelled text rows.
    [InlineData("SideBySide", 1279.0, true, true, true, true, "Rows")]
    [InlineData("SideBySide", 1100.0, true, true, true, true, "Rows")]
    [InlineData("SideBySide", 900.0, true, true, false, false, "TextRows")]
    [InlineData("Text", 1400.0, true, true, false, false, "TextBlocks")]
    [InlineData("Text", 1000.0, true, true, false, false, "TextBlocks")]
    [InlineData("Text", 900.0, true, true, false, false, "TextRows")]
    [InlineData("nonsense", 1100.0, true, true, true, true, "Rows")]
    // Off hides the whole display at every width: no value, no graph.
    [InlineData("Off", 1400.0, false, false, false, false, "TextRows")]
    [InlineData("Off", 900.0, false, false, false, false, "TextRows")]
    [InlineData("off", 1100.0, false, false, false, false, "TextRows")]
    public void EachSettingResolvesToItsLayoutAtTheWindowWidth(string setting, double width, bool net, bool disk, bool netGraph, bool diskGraph, string arrangement)
    {
        IoStatsDisplay.HeaderLayout layout = IoStatsDisplay.ResolveHeaderLayout(setting, width);
        Assert.Equal((net, disk, netGraph, diskGraph), (layout.NetValue, layout.DiskValue, layout.NetGraph, layout.DiskGraph));
        Assert.Equal(arrangement, layout.Arrangement.ToString());
        Assert.Equal(arrangement is "GraphBlocks" or "TextBlocks", layout.TwoLineLabels);
        Assert.Equal(setting.Equals("off", StringComparison.OrdinalIgnoreCase), layout.Hidden);
    }

    [Fact]
    public void OffIsTheOnlySettingThatHidesTheDisplayAndEveryWidthAndViewKeepsItHidden()
    {
        foreach (string setting in IoStatsDisplay.HeaderGraphChoices)
            foreach (double width in new[] { 0.0, 500.0, 900.0, 1100.0, 1400.0, double.PositiveInfinity })
                Assert.Equal(setting == IoStatsDisplay.HeaderGraphOff, IoStatsDisplay.ResolveHeaderLayout(setting, width).Hidden);
        Assert.True(IoStatsDisplay.HeaderIsOff("Off") && IoStatsDisplay.HeaderIsOff(" off "));
        Assert.False(IoStatsDisplay.HeaderIsOff("Auto") || IoStatsDisplay.HeaderIsOff(null) || IoStatsDisplay.HeaderIsOff("nonsense"));
        // The policy row stays Required in every view; the explicit setting wins (the header never reads a work view).
        foreach (WorkView view in WorkViewSetting.All)
            Assert.True(WorkViewPolicy.OnSurface(WorkViewPolicy.Resolve("shell.header.graph", view)));
    }

    [Theory]
    [InlineData("Network", "Network")]
    [InlineData("SideBySide", "SideBySide")]
    [InlineData("Off", "Auto")]
    [InlineData(null, "Auto")]
    [InlineData("nonsense", "Auto")]
    public void TheStatisticsCheckboxShowsTheLastArrangementOrAutoAgain(string? lastShown, string expected) =>
        Assert.Equal(expected, IoStatsDisplay.HeaderGraphShownAgain(lastShown));

    [Fact]
    public void NarrowWindowsDropGraphsAndWideWindowsStackDirectionsUnlessCompactRowsAreExplicit()
    {
        foreach (string setting in IoStatsDisplay.HeaderGraphChoices)
        {
            IoStatsDisplay.HeaderLayout narrow = IoStatsDisplay.ResolveHeaderLayout(setting, IoStatsDisplay.HeaderGraphsMinimumWidth - 1);
            Assert.False(narrow.NetGraph || narrow.DiskGraph, setting);
            Assert.Equal(setting != "Off", narrow.NetValue || narrow.DiskValue);
            if (setting == IoStatsDisplay.HeaderGraphOff) continue; // nothing at any width
            IoStatsDisplay.HeaderLayout wide = IoStatsDisplay.ResolveHeaderLayout(setting, IoStatsDisplay.HeaderBlocksMinimumWidth);
            Assert.Equal(setting != "NetworkAndDisk", wide.TwoLineLabels);
            Assert.Equal(setting != "Text", wide.NetGraph || wide.DiskGraph);
        }
        Assert.True(IoStatsDisplay.HeaderGraphsMinimumWidth < IoStatsDisplay.HeaderBlocksMinimumWidth);
    }

    [Fact]
    public void TheOriginalThreeValuesKeepTheirMeaningAndAutomaticNoLongerHidesTheDiskGraphInSimpleOrBarebones()
    {
        Assert.True(IoStatsDisplay.HeaderShowsDiskValue("Auto") && IoStatsDisplay.HeaderShowsDisk("Auto"));
        Assert.False(IoStatsDisplay.HeaderShowsDiskValue("Network"));
        Assert.True(IoStatsDisplay.HeaderShowsDiskValue("NetworkAndDisk") && IoStatsDisplay.HeaderShowsDisk("NetworkAndDisk"));
        Assert.Equal(["Auto", "Network", "Disk", "NetworkAndDisk", "SideBySide", "Text", "Off"], IoStatsDisplay.HeaderGraphChoices);
        // An unknown value from a newer release reads as Auto.
        Assert.Equal("Auto", IoStatsDisplay.NormalizeHeaderGraph("Diagonal"));
    }

    private static IoRates SampleRates => new(NetDown: 2.4 * MiB, DiskWrite: 1.5 * MiB, DiskRead: 0.4 * MiB, Requests: 3, NetUp: 0.5 * MiB);

    [Fact]
    public void TheSentenceNamesExactlyTheValuesTheHeaderShows()
    {
        IoActivity activity = new(3, 41, 0);
        Assert.Equal("Network (application data) download 2.4 MiB/s, upload 512 KiB/s; media files on disk read 410 KiB/s, written 1.5 MiB/s; active downloads: 3. Opens the statistics.", IoStatsDisplay.GraphDescription(SampleRates, activity, true, true, En));
        Assert.Equal("Media files on disk read 410 KiB/s, written 1.5 MiB/s; active downloads: 3. Opens the statistics.", IoStatsDisplay.GraphDescription(SampleRates, activity, false, true, En));
        Assert.Equal("Network (application data) download 2.4 MiB/s, upload 512 KiB/s; active downloads: 3. Opens the statistics.", IoStatsDisplay.GraphDescription(SampleRates, activity, true, false, En));
    }

    [Theory]
    [InlineData("en", "10 KiB/s", "100 KiB/s", "0.98 MiB/s", "0.98 KiB/s")]
    [InlineData("de", "10 KiB/s", "100 KiB/s", "0,98 MiB/s", "0,98 KiB/s")]
    public void HeaderLabelsAndAccessibleSentenceShareRoundedCarries(string language, string down, string up, string write, string read)
    {
        Texts.SetLanguage(language);
        IoRates rates = new(NetDown: 9.995 * 1024, NetUp: 99.95 * 1024, DiskWrite: 999.5 * 1024, DiskRead: 1000, Requests: 0);
        Assert.Contains(down, IoStatsDisplay.NetworkLine(rates));
        Assert.Contains(up, IoStatsDisplay.NetworkLine(rates));
        Assert.Contains(write, IoStatsDisplay.DiskLine(rates));
        Assert.Contains(read, IoStatsDisplay.DiskLine(rates));
        string sentence = IoStatsDisplay.GraphDescription(rates, default, true, true);
        foreach (string rate in new[] { down, up, write, read }) Assert.Contains(rate, sentence);
    }

    [Fact]
    public void TheLabelledHeaderTextsExistInAllTwelveLanguages()
    {
        foreach (string language in new[] { "en", "de", "es", "fr", "ru", "zh-Hans", "ja", "ko", "pt-BR", "cs", "it", "pl" })
        {
            Texts.SetLanguage(language);
            foreach (string key in new[] { "stats.header.networkTitle", "stats.header.diskTitle", "stats.header.down", "stats.header.up", "stats.header.write", "stats.header.read" })
            {
                string text = Texts.T(key);
                Assert.NotEqual(key, text);
                Assert.False(string.IsNullOrWhiteSpace(text));
            }
            foreach (string key in new[] { "stats.header.netValues", "stats.header.diskValues" })
            {
                string text = Texts.T(key, "X", "Y");
                Assert.NotEqual(key, text);
                Assert.Contains("X", text); Assert.Contains("Y", text);
                Assert.DoesNotContain("{", text);
            }
            Assert.Contains("↓", Texts.T("stats.header.netValues", "X", "Y"));
            Assert.Contains("↑", Texts.T("stats.header.netValues", "X", "Y"));
        }
        Texts.SetLanguage("en");
        Assert.Equal("Network", Texts.T("stats.header.networkTitle"));
        Assert.Equal("Disk", Texts.T("stats.header.diskTitle"));
        Assert.Equal("Network ↓ 2.4 MiB/s ↑ 512 KiB/s", IoStatsDisplay.NetworkLine(SampleRates, En));
        Assert.Equal("Disk R 410 KiB/s W 1.5 MiB/s", IoStatsDisplay.DiskLine(SampleRates, En));
        Texts.SetLanguage("de");
        Assert.Equal("Netzwerk", Texts.T("stats.header.networkTitle"));
        Assert.Equal("Datenträger", Texts.T("stats.header.diskTitle"));
        Assert.Equal("Datenträger L 410 KiB/s S 1,5 MiB/s", IoStatsDisplay.DiskLine(SampleRates, CultureInfo.GetCultureInfo("de")));
    }

    [Fact]
    public void TheTaglineIsTheCatSentenceInEveryLanguage()
    {
        Texts.SetLanguage("en");
        Assert.Equal("Open-source imageboard downloader — now with 100% more cats", Texts.T("shell.subtitle"));
        Texts.SetLanguage("de");
        Assert.Equal("Open-Source-Imageboard-Downloader — jetzt mit 100 % mehr Katzen", Texts.T("shell.subtitle"));
        foreach (string language in new[] { "es", "fr", "ru", "zh-Hans", "ja", "ko", "pt-BR", "cs", "it", "pl" })
        {
            Texts.SetLanguage(language);
            Assert.DoesNotContain("YChan", Texts.T("shell.subtitle"));
            Assert.Contains("100", Texts.T("shell.subtitle"));
        }
    }

    [Fact]
    public void TheWorkViewNeverChangesWhichGraphsTheHeaderDraws()
    {
        Config config = new();
        Assert.True(IoStatsDisplay.HeaderShowsDisk(config.StatsHeaderGraph));
        config.StatsHeaderGraph = "Network";
        Assert.False(IoStatsDisplay.HeaderShowsDisk(config.StatsHeaderGraph));
        config.StatsHeaderGraph = "NetworkAndDisk";
        Assert.True(IoStatsDisplay.HeaderShowsDisk(config.StatsHeaderGraph));
    }

    [Theory]
    [InlineData("NetworkAndDisk")]
    [InlineData("Off")]
    public void TheValueSurvivesCloneBothCopiesAndSaveReload(string value)
    {
        using TempDirectory directory = new();
        Config original = new() { StatsHeaderGraph = value };
        Config clone = original.Clone();
        Config live = new();
        live.CopySettingsFieldsFrom(clone);
        Config copy = new();
        copy.CopyFrom(live);
        copy.SaveConfig(directory.Path);
        Config loaded = Config.LoadConfig(directory.Path);
        Assert.Null(loaded.LoadWarning);
        foreach (Config config in new[] { clone, live, copy, loaded }) Assert.Equal(value, config.StatsHeaderGraph);
        Assert.Contains("\"StatsHeaderGraph\": \"" + value + "\"", Regex.Replace(File.ReadAllText(Path.Combine(directory.Path, "settings.json")), "\"\\s*:\\s*\"", "\": \""));
    }

    [Theory]
    [InlineData("\"Both\"", "Auto")]
    [InlineData("\"network\"", "Network")]
    [InlineData("\"Off\"", "Off")]
    [InlineData("\"OFF\"", "Off")]
    [InlineData("42", "Auto")]
    [InlineData("null", "Auto")]
    [InlineData("true", "Auto")]
    [InlineData("[]", "Auto")]
    public void AnInvalidValueInTheSettingsFileReadsAsTheDefaultAndKeepsTheRest(string literal, string expected)
    {
        using TempDirectory directory = new();
        new Config { StatsHeaderGraph = "Network", BooruCatalogLimitMiB = 17 }.SaveConfig(directory.Path);
        string file = Path.Combine(directory.Path, "settings.json");
        string text = File.ReadAllText(file);
        string changed = Regex.Replace(text, "(\"StatsHeaderGraph\"\\s*:\\s*)\"Network\"", "${1}" + literal);
        Assert.NotEqual(text, changed);
        File.WriteAllText(file, changed);
        Config loaded = Config.LoadConfig(directory.Path);
        Assert.Null(loaded.LoadWarning);
        Assert.Equal(expected, loaded.StatsHeaderGraph);
        Assert.Equal(17, loaded.BooruCatalogLimitMiB);
    }

    [Fact]
    public void AMissingValueInAnOlderFileIsTheDefault()
    {
        using TempDirectory directory = new();
        new Config().SaveConfig(directory.Path);
        string file = Path.Combine(directory.Path, "settings.json");
        string text = File.ReadAllText(file);
        string changed = Regex.Replace(text, "\\s*\"StatsHeaderGraph\"\\s*:\\s*\"[^\"]*\",?", "");
        Assert.NotEqual(text, changed);
        File.WriteAllText(file, changed);
        Config loaded = Config.LoadConfig(directory.Path);
        Assert.Null(loaded.LoadWarning);
        Assert.Equal("Auto", loaded.StatsHeaderGraph);
    }

    [Fact]
    public void TheAccessibleNameNamesOnlyWhatIsShown()
    {
        IoActivity activity = new(3, 41, 0);
        string network = IoStatsDisplay.GraphDescription(SampleRates, activity, true, false, En);
        Assert.DoesNotContain("disk", network, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("1.5", network);
        Texts.SetLanguage("de");
        Assert.Equal("Netzwerk (Anwendungsdaten) herunter 2,4 MiB/s, hoch 512 KiB/s; aktive Downloads: 3. Öffnet die Statistik.",
            IoStatsDisplay.GraphDescription(SampleRates, activity, true, false, CultureInfo.GetCultureInfo("de")));
    }

    [Fact]
    public void TheSentenceTextsExistInEveryLanguageWithTheirPlaceholders()
    {
        foreach (string language in new[] { "en", "de", "es", "fr", "ru", "zh-Hans", "ja", "ko", "pt-BR", "cs", "it", "pl" })
        {
            Texts.SetLanguage(language);
            foreach ((string key, int count) in new[] { ("stats.graph.name", 5), ("stats.graph.nameNet", 3), ("stats.graph.nameDisk", 3) })
            {
                string[] arguments = ["A", "B", "C", "D", "E"];
                string text = Texts.T(key, arguments.Cast<object>().ToArray());
                Assert.NotEqual(key, text);
                Assert.DoesNotContain("{", text);
                foreach (string argument in arguments.Take(count)) Assert.Contains(argument, text);
            }
        }
    }

    [Fact]
    public void TheSettingsTextsExistInEveryLanguageAndTheHintIsTheAgreedSentence()
    {
        string[] keys =
        [
            "stats.headerGraph.label", "stats.headerGraph.hint",
            "stats.headerGraph.auto", "stats.headerGraph.network", "stats.headerGraph.networkAndDisk",
            "stats.headerGraph.disk", "stats.headerGraph.sideBySide", "stats.headerGraph.text", "stats.headerGraph.off"
        ];
        foreach (string language in new[] { "en", "de", "es", "fr", "ru" })
        {
            Texts.SetLanguage(language);
            foreach (string key in keys)
            {
                string text = Texts.T(key);
                Assert.False(string.IsNullOrWhiteSpace(text), language + " " + key);
                Assert.NotEqual(key, text);
            }
        }
        Texts.SetLanguage("en");
        Assert.Equal("Chooses what the header shows. Automatic: network and disk, each with its own graph, in every view. Side by side needs a wide window; narrow windows drop the graphs and keep the values. Each graph has its own scale. In the header, download and read are drawn solid with a filled area, upload and write dashed. Off hides the display and stops its measuring; the statistics page and the tray menu bring it back.", Texts.T("stats.headerGraph.hint"));
        Texts.SetLanguage("de");
        Assert.Equal("Legt fest, was die Kopfleiste zeigt. Automatisch: Netz und Datenträger mit je einem eigenen Graphen, in jeder Ansicht. Nebeneinander braucht ein breites Fenster; schmale Fenster lassen die Graphen weg und behalten die Werte. Jeder Graph hat seine eigene Skala. In der Kopfleiste sind Download und Lesen durchgezogen mit gefüllter Fläche gezeichnet, Upload und Schreiben gestrichelt. Aus blendet die Anzeige aus und beendet ihre Messung; die Statistikseite und das Menü im Infobereich holen sie zurück.", Texts.T("stats.headerGraph.hint"));
        Assert.Equal("Graph in der Kopfleiste", Texts.T("stats.headerGraph.label"));
    }

    [Fact]
    public void TheGraphTextsAndRetainedStatisticsLabelsResolveWithTheirPlaceholders()
    {
        string[] languages = ["en", "de", "es", "fr", "ru", "zh-Hans", "ja", "ko", "pt-BR", "cs", "it", "pl"];
        (string Key, int Placeholders)[] keys = [("stats.graph.page.name", 3), ("stats.scale", 1), ("stats.metric.netUp", 0), ("stats.sources.title", 0),
            ("stats.peak", 1), ("stats.legend.net", 0), ("stats.legend.disk", 0)];
        foreach (string language in languages)
        {
            Texts.SetLanguage(language);
            foreach ((string key, int placeholders) in keys)
            {
                string text = Texts.T(key, "A", "B", "C");
                Assert.NotEqual(key, text);
                Assert.DoesNotContain("{", text);
                foreach (string argument in new[] { "A", "B", "C" }.Take(placeholders))
                    Assert.Contains(argument, text);
            }
            // The merged statistics page still uses stats.peak and keeps the legend vocabulary.
            // A header layout change does not retire the page's localized labels.
            foreach (string removed in new[] { "stats.header.net", "stats.header.disk", "stats.tray.rates" })
                Assert.Equal(removed, Texts.T(removed));
        }
        Texts.SetLanguage("en");
        Assert.Equal("Scale up to 2 MiB/s", Texts.T("stats.scale", "2 MiB/s"));
    }

    [Fact]
    public void TheNetworkRowScaleIgnoresTheDiskRow()
    {
        float[] network = [1000f, 2000f];
        float[] disk = [4_000_000f, 1f];
        Assert.Equal(IoGraphGeometry.DefaultMinimumPeak, IoGraphGeometry.Peak(network));
        Assert.Equal(4_194_304d, IoGraphGeometry.Peak(disk));   // 4 MiB; its own scale
    }

    [Fact]
    public void TheOffTextsExistInAllTwelveLanguagesAndTheHintNamesTheWayBack()
    {
        string english = "";
        foreach (string language in new[] { "en", "de", "es", "fr", "ru", "zh-Hans", "ja", "ko", "pt-BR", "cs", "it", "pl" })
        {
            Texts.SetLanguage(language);
            foreach (string key in new[] { "stats.headerGraph.off", "stats.headerMonitor.show", "stats.headerGraph.hint" })
            {
                string text = Texts.T(key);
                Assert.NotEqual(key, text);
                Assert.False(string.IsNullOrWhiteSpace(text), language + " " + key);
            }
            if (language == "en") english = Texts.T("stats.headerGraph.off");
            else Assert.NotEqual(english, Texts.T("stats.headerGraph.off"));
            if (language is "en" or "de") Assert.Contains(language == "en" ? "Off hides" : "Aus blendet", Texts.T("stats.headerGraph.hint"));
        }
    }
}
