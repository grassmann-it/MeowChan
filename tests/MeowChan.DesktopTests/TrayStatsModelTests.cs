using MeowChan.Desktop;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.Stats;

namespace MeowChan.DesktopTests;

/// <summary>The tray tooltip and menu lines as text: binary units, number format of the language, the 120-character ceiling.</summary>
[Collection("Localization")]
public sealed class TrayStatsModelTests : IDisposable
{
    private const long KiB = 1024, MiB = 1024 * KiB, GiB = 1024 * MiB;
    private readonly string _previous = Texts.Language;

    public TrayStatsModelTests() => Texts.Initialize();
    public void Dispose() => Texts.SetLanguage(_previous);

    private static TrayStatsInput Running(int active = 3, int waiting = 0, double net = 2.4 * MiB, double write = 12 * MiB, double read = 0,
        long today = 1288490189, bool known = true) =>
        new(false, active, waiting, new IoRates(net, write, read, 0), new IoCounts(today, 450 * MiB, 0, 0, 0), known);

    private static TrayStatsInput Quiet(bool paused = false, long today = 1288490189) =>
        new(paused, 0, 0, IoRates.Zero, new IoCounts(today, 450 * MiB, 0, 0, 0), RatesKnown: false);

    [Theory]
    [InlineData("en", "MeowChan · ↓ 2.4 MiB/s · Disk 12.0 MiB/s · 3 downloads active")]
    [InlineData("de", "MeowChan · ↓ 2,4 MiB/s · Platte 12,0 MiB/s · 3 Downloads aktiv")]
    public void TheActiveTooltipShowsBinaryRatesAndTheDownloadCount(string language, string expected)
    {
        Texts.SetLanguage(language);
        Assert.Equal(expected, TrayStatsModel.Tooltip(Running()));
    }

    [Theory]
    [InlineData("en", "MeowChan · ↓ 512 KiB/s · Disk 1.5 MiB/s · 1 download active")]
    [InlineData("de", "MeowChan · ↓ 512 KiB/s · Platte 1,5 MiB/s · 1 Download aktiv")]
    public void TheDiskRateAddsWritingAndReadingAndTheSingularIsUsed(string language, string expected)
    {
        Texts.SetLanguage(language);
        Assert.Equal(expected, TrayStatsModel.Tooltip(Running(active: 1, net: 512 * KiB, write: MiB, read: 0.5 * MiB)));
    }

    [Theory]
    [InlineData("en", "MeowChan · paused · Today ↓ 1.2 GiB", "MeowChan · idle · Today ↓ 1.2 GiB", "MeowChan · idle")]
    [InlineData("de", "MeowChan · pausiert · Heute ↓ 1,2 GiB", "MeowChan · Leerlauf · Heute ↓ 1,2 GiB", "MeowChan · Leerlauf")]
    public void PausedAndIdleShowTheStateAndTodaysTotalOnlyWhenThereIsOne(string language, string paused, string idle, string idleNothingToday)
    {
        Texts.SetLanguage(language);
        Assert.Equal(paused, TrayStatsModel.Tooltip(Quiet(paused: true)));
        Assert.Equal(idle, TrayStatsModel.Tooltip(Quiet()));
        Assert.Equal(idleNothingToday, TrayStatsModel.Tooltip(Quiet(today: 0)));
    }

    [Fact]
    public void WithoutAMeasurementTheTooltipLeavesTheRatesOutInsteadOfShowingZero()
    {
        Texts.SetLanguage("en");
        Assert.Equal("MeowChan · 3 downloads active · Today ↓ 1.2 GiB", TrayStatsModel.Tooltip(Running(known: false)));
        // A paused session shows no rates even if a timer happens to run.
        Assert.Equal("MeowChan · paused · Today ↓ 1.2 GiB", TrayStatsModel.Tooltip(Running(known: true) with { Paused = true }));
    }

    [Theory]
    [InlineData("en")]
    [InlineData("de")]
    [InlineData("es")]
    [InlineData("fr")]
    [InlineData("ru")]
    public void EveryStateInEveryLanguageFitsTheWindowsTooltipLimitAtTheWorstCase(string language)
    {
        Texts.SetLanguage(language);
        double top = 1023.9 * GiB;
        TrayStatsInput[] states =
        [
            Running(active: 999, waiting: 999, net: top, write: top, read: top, today: 1023 * GiB),
            Running(active: 999, known: false, today: 1023 * GiB), Quiet(paused: true, today: 1023 * GiB), Quiet(today: 1023 * GiB)
        ];
        foreach (TrayStatsInput state in states)
        {
            string tip = TrayStatsModel.Tooltip(state);
            Assert.InRange(tip.Length, 9, TrayStatsModel.MaxTooltipLength);
            Assert.StartsWith("MeowChan · ", tip);
        }
    }

    [Fact]
    public void ATextOverTheCeilingLosesTrailingPartsFirstAndIsCutAsALastResort()
    {
        string wide = new('x', 70);
        Assert.Equal("MeowChan · " + wide, TrayStatsModel.Fit("MeowChan", [wide, wide, wide]));
        Assert.Equal("MeowChan · a · b", TrayStatsModel.Fit("MeowChan", ["a", "b"]));
        string cut = TrayStatsModel.Fit("MeowChan", [new string('y', 300)]);
        Assert.Equal(TrayStatsModel.MaxTooltipLength, cut.Length);
        Assert.EndsWith("…", cut);
    }

    [Theory]
    [InlineData("en", "↓ 2.4 MiB/s · Disk 12.0 MiB/s", "3 downloads active · 41 waiting · Today ↓ 1.2 GiB")]
    [InlineData("de", "↓ 2,4 MiB/s · Platte 12,0 MiB/s", "3 Downloads aktiv · 41 wartend · Heute ↓ 1,2 GiB")]
    public void TheMenuLinesShowRatesAndCountsWhileDownloadsRunAndAreMeasured(string language, string rates, string detail)
    {
        Texts.SetLanguage(language);
        Assert.Equal((rates, detail), TrayStatsModel.MenuLines(Running(waiting: 41)));
    }

    [Theory]
    [InlineData("en", "3 downloads active · 41 waiting", "Today ↓ 1.2 GiB · Disk: 450.0 MiB written")]
    [InlineData("de", "3 Downloads aktiv · 41 wartend", "Heute ↓ 1,2 GiB · Platte: 450,0 MiB geschrieben")]
    public void WithoutAMeasurementTheMenuShowsCountsAndTodaysTotalsButNoRates(string language, string first, string second)
    {
        Texts.SetLanguage(language);
        Assert.Equal((first, second), TrayStatsModel.MenuLines(Running(waiting: 41, known: false)));
    }

    [Theory]
    [InlineData("en", "Idle", "Paused")]
    [InlineData("de", "Leerlauf", "Pausiert")]
    public void IdleAndPausedMenusShowTheStateAndTodaysTotals(string language, string idle, string paused)
    {
        Texts.SetLanguage(language);
        string detail = language == "en" ? "Today ↓ 1.2 GiB · Disk: 450.0 MiB written" : "Heute ↓ 1,2 GiB · Platte: 450,0 MiB geschrieben";
        Assert.Equal((idle, detail), TrayStatsModel.MenuLines(Quiet()));
        Assert.Equal((paused, detail), TrayStatsModel.MenuLines(Quiet(paused: true)));
    }

    [Fact]
    public void TheLegacyTooltipOfTheTrayMenuModelIsUnchanged()
    {
        Texts.SetLanguage("en");
        Assert.Equal("MeowChan - Image Downloader", TrayMenuModel.Tooltip(false, 0));
        Assert.Equal("MeowChan · Active downloads: 2", TrayMenuModel.Tooltip(false, 2));
        Assert.Equal("MeowChan · paused", TrayMenuModel.Tooltip(true, 2));
    }

    [Fact]
    public void AllTrayTextKeysExistInEveryLanguageWithTheSamePlaceholders()
    {
        string[] keys = [.. IoStatsTrayTexts.Entries.Select(entry => entry.Key)];
        Assert.Equal(keys.Length, keys.Distinct(StringComparer.Ordinal).Count());
        foreach (string language in new[] { "en", "de", "es", "fr", "ru" })
        {
            Texts.SetLanguage(language);
            foreach ((string key, string english, _) in IoStatsTrayTexts.Entries)
            {
                string text = Texts.T(key, "A", "B");
                Assert.NotEqual(key, text);
                // Every placeholder of the English text is filled in the translation (the Russian count text may use one only).
                if (language is not "ru" && english.Contains("{0}")) Assert.Contains("A", text);
                if (language is not "ru" && english.Contains("{1}")) Assert.Contains("B", text);
            }
        }
    }
}
