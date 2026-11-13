using System.Globalization;
using System.Text.RegularExpressions;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.Stats;

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
    [InlineData("Both", "Auto")]
    [InlineData("", "Auto")]
    [InlineData(null, "Auto")]
    public void AnyTextIsNormalizedToOneOfTheThreeValues(string? input, string expected)
    {
        Assert.Equal(expected, IoStatsDisplay.NormalizeHeaderGraph(input));
        Config config = new() { StatsHeaderGraph = input! };
        Assert.Equal(expected, config.StatsHeaderGraph);
    }

    [Theory]
    // Automatic follows the work view: disk only in Advanced.
    [InlineData("Auto", "Advanced", true)]
    [InlineData("Auto", "Simple", false)]
    [InlineData("Auto", "Barebones", false)]
    // Explicit values ignore the work view.
    [InlineData("Network", "Advanced", false)]
    [InlineData("Network", "Simple", false)]
    [InlineData("Network", "Barebones", false)]
    [InlineData("NetworkAndDisk", "Advanced", true)]
    [InlineData("NetworkAndDisk", "Simple", true)]
    [InlineData("NetworkAndDisk", "Barebones", true)]
    // Unknown text behaves like Auto.
    [InlineData("nonsense", "Advanced", true)]
    [InlineData("nonsense", "Simple", false)]
    public void TheHeaderDrawsTheDiskRowByTheSettingAndTheWorkView(string setting, string mode, bool disk) =>
        Assert.Equal(disk, IoStatsDisplay.HeaderShowsDisk(setting, mode));

    [Fact]
    public void SwitchingTheWorkViewUpdatesAutomaticImmediatelyAndLeavesExplicitValuesAlone()
    {
        Config config = new();
        bool[] auto = new[] { "Barebones", "Simple", "Advanced", "Simple" }.Select(mode => IoStatsDisplay.HeaderShowsDisk(config.StatsHeaderGraph, mode)).ToArray();
        Assert.Equal(new[] { false, false, true, false }, auto);
        config.StatsHeaderGraph = "Network";
        Assert.All(new[] { "Barebones", "Simple", "Advanced" }, mode => Assert.False(IoStatsDisplay.HeaderShowsDisk(config.StatsHeaderGraph, mode)));
        config.StatsHeaderGraph = "NetworkAndDisk";
        Assert.All(new[] { "Barebones", "Simple", "Advanced" }, mode => Assert.True(IoStatsDisplay.HeaderShowsDisk(config.StatsHeaderGraph, mode)));
    }

    [Fact]
    public void TheValueSurvivesCloneBothCopiesAndSaveReload()
    {
        using TempDirectory directory = new();
        Config original = new() { StatsHeaderGraph = "NetworkAndDisk" };
        Config clone = original.Clone();
        Config live = new();
        live.CopySettingsFieldsFrom(clone);
        Config copy = new();
        copy.CopyFrom(live);
        copy.SaveConfig(directory.Path);
        Config loaded = Config.LoadConfig(directory.Path);
        Assert.Null(loaded.LoadWarning);
        foreach (Config config in new[] { clone, live, copy, loaded }) Assert.Equal("NetworkAndDisk", config.StatsHeaderGraph);
        Assert.Contains("\"StatsHeaderGraph\": \"NetworkAndDisk\"", Regex.Replace(File.ReadAllText(Path.Combine(directory.Path, "settings.json")), "\"\\s*:\\s*\"", "\": \""));
    }

    [Theory]
    [InlineData("\"Both\"", "Auto")]
    [InlineData("\"network\"", "Network")]
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
    public void TheAccessibleNameNamesOnlyWhatIsDrawn()
    {
        IoRates rates = new(NetDown: 2.4 * MiB, DiskWrite: 1.5 * MiB, DiskRead: 0.4 * MiB, Requests: 3);
        IoActivity activity = new(3, 41, 0);
        string both = IoStatsDisplay.GraphDescription(rates, activity, showDisk: true, En);
        string network = IoStatsDisplay.GraphDescription(rates, activity, showDisk: false, En);
        Assert.Equal("Network download 2.4 MiB/s, disk 1.9 MiB/s, active downloads: 3. Opens the statistics.", both);
        Assert.Equal("Network download 2.4 MiB/s, active downloads: 3. Opens the statistics.", network);
        Assert.DoesNotContain("disk", network, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("1.9", network);
        Texts.SetLanguage("de");
        Assert.Equal("Netzwerk herunter 2,4 MiB/s, aktive Downloads: 3. Öffnet die Statistik.",
            IoStatsDisplay.GraphDescription(rates, activity, showDisk: false, CultureInfo.GetCultureInfo("de")));
    }

    [Fact]
    public void TheNetworkOnlyNameExistsInEveryLanguageWithItsTwoPlaceholders()
    {
        foreach (string language in new[] { "en", "de", "es", "fr", "ru" })
        {
            Texts.SetLanguage(language);
            string text = Texts.T("stats.graph.nameNet", "A", "B");
            Assert.DoesNotContain("stats.graph.nameNet", text);
            Assert.Contains("A", text);
            Assert.Contains("B", text);
            Assert.DoesNotContain("{", text);
        }
    }

    [Fact]
    public void TheSettingsTextsExistInEveryLanguageAndTheHintIsTheAgreedSentence()
    {
        string[] keys =
        [
            "stats.headerGraph.block", "stats.headerGraph.label", "stats.headerGraph.hint",
            "stats.headerGraph.auto", "stats.headerGraph.network", "stats.headerGraph.networkAndDisk"
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
        Assert.Equal("Chooses what the graph at the top shows. Automatic: network and disk in the Advanced view, network only otherwise.", Texts.T("stats.headerGraph.hint"));
        Texts.SetLanguage("de");
        Assert.Equal("Legt fest, was der Graph oben zeigt. Automatisch: Netz und Datenträger in der erweiterten Ansicht, sonst nur das Netz.", Texts.T("stats.headerGraph.hint"));
        Assert.Equal("Graph in der Kopfleiste", Texts.T("stats.headerGraph.label"));
    }

    [Fact]
    public void TheScaleOfANetworkOnlyGraphIgnoresTheDisk()
    {
        float[] network = [1000f, 2000f];
        float[] disk = [4_000_000f, 1f];
        Assert.Equal(4_000_000d, IoGraphGeometry.Peak(network, disk));
        Assert.Equal(IoGraphGeometry.DefaultMinimumPeak, IoGraphGeometry.Peak(network, ReadOnlySpan<float>.Empty));
    }
}
