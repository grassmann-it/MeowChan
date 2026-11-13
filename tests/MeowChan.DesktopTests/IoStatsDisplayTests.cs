using System.Globalization;
using System.Text.RegularExpressions;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.Stats;

namespace MeowChan.DesktopTests;

/// <summary>Key mapping, formatting, the claim rule and the table of the statistics page and header graph.</summary>
[Collection("Localization")]
public sealed class IoStatsDisplayTests : IDisposable
{
    private static readonly CultureInfo En = CultureInfo.GetCultureInfo("en");
    private static readonly CultureInfo De = CultureInfo.GetCultureInfo("de");
    private const long MiB = 1024 * 1024;

    public IoStatsDisplayTests() { Texts.Initialize(); Texts.SetLanguage("en"); }
    public void Dispose() { Texts.Initialize(); Texts.SetLanguage("en"); }

    [Theory]
    [InlineData("en", "other", "Other sources")]
    [InlineData("en", "local", "Local files")]
    [InlineData("en", "unknown", "Unknown source")]
    [InlineData("de", "other", "Sonstige Quellen")]
    [InlineData("de", "local", "Lokale Dateien")]
    [InlineData("de", "unknown", "Unbekannte Quelle")]
    public void TheTransportKeysOtherLocalAndUnknownReadAsText(string language, string key, string expected)
    {
        Texts.SetLanguage(language);
        Assert.Equal(expected, IoStatsDisplay.SourceName(key));
        // Whatever the lookup says: these three never show a provider name.
        Assert.Equal(expected, IoStatsDisplay.SourceName(key, _ => "Some Provider"));
    }

    [Fact]
    public void ARealSourceShowsItsDisplayNameAndAnUnknownIdShowsAsWritten()
    {
        Assert.Equal("Danbooru", IoStatsDisplay.SourceName("danbooru", id => id == "danbooru" ? "Danbooru" : null));
        Assert.Equal("old-source", IoStatsDisplay.SourceName("old-source", _ => null));
        Assert.Equal("4chan", IoStatsDisplay.SourceName("4chan", _ => "  "));
        Assert.Equal("4chan", IoStatsDisplay.SourceName("4chan"));
    }

    [Fact]
    public void TheGraphSentenceNamesNetworkDiskAndActiveDownloadsInBinaryUnits()
    {
        IoRates rates = new(NetDown: 2.4 * MiB, DiskWrite: 1.5 * MiB, DiskRead: 0.4 * MiB, Requests: 3, NetUp: 0.5 * MiB);
        Assert.Equal(1.5 * MiB, IoStatsDisplay.HeaderDiskRate(rates), 3);          // what MeowChan writes, not what it reads back
        Assert.Equal("Network (application data) download 2.4 MiB/s, upload 512 KiB/s; media files on disk written 1.5 MiB/s, read 410 KiB/s; active downloads: 3. Opens the statistics.",
            IoStatsDisplay.GraphDescription(rates, new IoActivity(3, 41, 0), En));
        Texts.SetLanguage("de");
        Assert.Equal("Netzwerk (Anwendungsdaten) herunter 2,4 MiB/s, hoch 512 KiB/s; Mediendateien auf dem Datenträger geschrieben 1,5 MiB/s, gelesen 410 KiB/s; aktive Downloads: 3. Öffnet die Statistik.",
            IoStatsDisplay.GraphDescription(rates, new IoActivity(3, 41, 0), De));
        Texts.SetLanguage("en");
        Assert.Equal("Network (application data) download 0 B/s, upload 0 B/s; media files on disk written 0 B/s, read 0 B/s; active downloads: 0. Opens the statistics.",
            IoStatsDisplay.GraphDescription(IoRates.Zero, default, En));
    }

    [Fact]
    public void ReadingBackFromDiskNeverChangesTheHeaderGraphRateButIsAValueOfItsOwn()
    {
        IoRates quiet = new(NetDown: 1 * MiB, DiskWrite: 200 * 1024, DiskRead: 0, Requests: 1);
        IoRates busyReading = quiet with { DiskRead = 80 * MiB };
        Assert.Equal(IoStatsDisplay.HeaderDiskRate(quiet), IoStatsDisplay.HeaderDiskRate(busyReading));
        Assert.Contains("80.0 MiB/s", IoStatsDisplay.DiskValues(busyReading, En));
        Assert.Contains("80.0 MiB/s", IoStatsDisplay.GraphDescription(busyReading, default, En));
        Assert.Contains("200 KiB/s", IoStatsDisplay.DiskValues(busyReading, En));
    }

    [Theory]
    [InlineData(120, 1000, 120)]
    [InlineData(120, 500, 60)]
    [InlineData(30, 10000, 300)]
    [InlineData(0, 1000, 0)]
    public void TheHistoryCoversSamplesTimesTheInterval(int samples, int intervalMs, int seconds) =>
        Assert.Equal(seconds, IoStatsDisplay.HistorySeconds(samples, intervalMs));

    [Theory]
    [InlineData(0d, "0.0")]
    [InlineData(3.24d, "3.2")]
    [InlineData(9.96d, "10")]
    [InlineData(42.4d, "42")]
    [InlineData(-1d, "0.0")]
    [InlineData(double.NaN, "0.0")]
    public void RequestRateHasOneDecimalBelowTenAndNoneAbove(double perSecond, string expected) =>
        Assert.Equal(expected, IoStatsDisplay.RequestRate(perSecond, En));

    [Theory]
    [InlineData(false, false, false, false)]
    [InlineData(false, true, false, false)]   // hidden or minimized window: no claim, whatever is "visible"
    [InlineData(false, false, true, false)]
    [InlineData(false, true, true, false)]
    [InlineData(true, false, false, false)]   // shown, but narrow and on another page: nothing to draw
    [InlineData(true, true, false, true)]
    [InlineData(true, false, true, true)]
    [InlineData(true, true, true, true)]
    public void TheWindowClaimsSamplesOnlyWhileItIsShownAndSomethingShowsThem(bool windowShown, bool graph, bool page, bool expectedWindow) =>
        Assert.Equal(expectedWindow ? IoStatsVisibility.Window : IoStatsVisibility.None, IoStatsDisplay.Claim(windowShown, graph, page));

    [Fact]
    public void AHiddenWindowHoldsNoTimerAndAShownOneHoldsExactlyTheSamplingTimer()
    {
        IoManualClock clock = new(new DateTimeOffset(2026, 11, 13, 12, 0, 0, TimeSpan.Zero));
        using IoStatsHub hub = new(new IoFakeSource(), clock, new IoStatsOptions { Persist = false });
        using IoStatsSubscription claim = hub.Subscribe(IoStatsVisibility.None);
        void Apply(bool shown, bool graph, bool page) => claim.Visibility = IoStatsDisplay.Claim(shown, graph, page);

        Assert.Equal(0, hub.ActiveTimerCount);
        Apply(shown: false, graph: true, page: true);                  // minimized with the graph and page "visible"
        Assert.Equal((0, IoStatsVisibility.None), (hub.ActiveTimerCount, hub.SamplingMode));
        Apply(shown: true, graph: true, page: false);                  // shown with the header graph
        Assert.Equal((1, IoStatsVisibility.Window), (hub.ActiveTimerCount, hub.SamplingMode));
        Apply(shown: true, graph: false, page: true);                  // narrow window on the statistics page
        Assert.Equal((1, IoStatsVisibility.Window), (hub.ActiveTimerCount, hub.SamplingMode));
        Apply(shown: true, graph: false, page: false);                 // narrow window on another page
        Assert.Equal((0, IoStatsVisibility.None), (hub.ActiveTimerCount, hub.SamplingMode));
        Apply(shown: true, graph: true, page: false);
        Apply(shown: false, graph: true, page: false);                 // hidden again: the timer is gone
        Assert.Equal(0, hub.ActiveTimerCount);
        Assert.Equal(0, clock.ActiveTimers);
    }

    private static IoStatsHub HubWithTraffic(out IoManualClock clock)
    {
        clock = new IoManualClock(new DateTimeOffset(2026, 11, 13, 12, 0, 0, TimeSpan.Zero));
        IoFakeSource source = new();
        IoStatsHub hub = new(source, clock, new IoStatsOptions { Persist = false, MaxSources = 8 });
        IoStatsSubscription window = hub.Subscribe(IoStatsVisibility.Window);
        source.Add("4chan", net: 5 * MiB, diskWritten: 4 * MiB, diskRead: MiB, requests: 1204, errors: 3);
        source.Add("danbooru", net: MiB, requests: 12);
        source.Add("other", net: 1024, requests: 1);
        source.Add("local", diskWritten: 2 * MiB);
        source.Add("unknown", requests: 2);
        clock.Advance(TimeSpan.FromSeconds(1));
        return hub;
    }

    [Fact]
    public void TheTableListsEverySourceByTheHubsOrderWithTheTotalLastAndNamedKeys()
    {
        using IoStatsHub hub = HubWithTraffic(out _);
        IReadOnlyList<IoStatsRowText> rows = IoStatsRows.Build(hub.Snapshot(), id => id == "danbooru" ? "Danbooru" : null, En);
        Assert.Equal(new[] { "4chan", "Danbooru", "Other sources", "Local files", "Unknown source", "Total" }.Order(), rows.Select(row => row.Name).Order());
        Assert.Equal("4chan", rows[0].Name);                                   // largest session download first
        Assert.Equal("Danbooru", rows[1].Name);
        IoStatsRowText total = rows[^1];
        Assert.True(total.IsTotal && rows.Take(rows.Count - 1).All(row => !row.IsTotal));
        Assert.Equal("Total", total.Name);
        Assert.Equal(new[] { "4chan", "danbooru", "other", "local", "unknown" }.Order(), rows.Take(rows.Count - 1).Select(row => row.Key).Order());
        IoStatsRowText chan = rows[0];
        Assert.Equal("5.0 MiB", chan.Session);
        Assert.Equal("5.0 MiB", chan.Today);
        Assert.Equal("5.0 MiB", chan.Retained);
        Assert.Equal("5.0 MiB/s", chan.Now);
        Assert.Equal("4.0 MiB", chan.DiskWritten);
        Assert.Equal("1.0 MiB", chan.DiskRead);
        Assert.Equal("1,204", chan.Requests);
        Assert.Equal("3", chan.Errors);
        Assert.Equal("6.0 MiB", total.Session);
        Assert.Equal("1,219", total.Requests);
    }

    [Fact]
    public void TheTableUsesTheLanguagesNumberFormat()
    {
        using IoStatsHub hub = HubWithTraffic(out _);
        Texts.SetLanguage("de");
        IoStatsRowText chan = IoStatsRows.Build(hub.Snapshot(), null, De)[0];
        Assert.Equal("5,0 MiB", chan.Session);
        Assert.Equal("1.204", chan.Requests);
        Assert.Equal("Gesamt", IoStatsRows.Build(hub.Snapshot(), null, De)[^1].Name);
    }

    [Fact]
    public void AnEmptyHubStillHasTheTotalRow()
    {
        using IoStatsHub hub = new(new IoFakeSource(), options: new IoStatsOptions { Persist = false });
        IoStatsRowText only = Assert.Single(IoStatsRows.Build(hub.Snapshot(), null, En));
        Assert.True(only.IsTotal);
        Assert.Equal("0 B", only.Session);
        Assert.Equal("0", only.Requests);
    }

    [Theory]
    [InlineData("de")]
    [InlineData("es")]
    [InlineData("fr")]
    [InlineData("ru")]
    public void EveryStatisticsPageTextExistsInTheLanguageWithTheSamePlaceholders(string language)
    {
        Regex placeholder = new(@"\{(\d+)\}");
        static string[] Slots(Regex regex, string text) => regex.Matches(text).Select(match => match.Groups[1].Value).Distinct().Order().ToArray();
        foreach ((string key, string english, string _) in IoStatsPageTexts.Entries)
        {
            Texts.SetLanguage("en");
            string reference = Texts.T(key);
            Assert.Equal(english, reference);
            Texts.SetLanguage(language);
            string translated = Texts.T(key);
            Assert.False(string.IsNullOrWhiteSpace(translated), language + ": " + key);
            Assert.Equal(Slots(placeholder, english), Slots(placeholder, translated));
        }
    }

    [Fact]
    public void TheStatisticsRowRaisesOnlyForTheTextsThatChanged()
    {
        IoStatsRowText first = new("4chan", false, "4chan", "0 B/s", "1 KiB", "1 KiB", "1 KiB", "0 B", "0 B", "1", "0");
        StatisticsRow row = new(first);
        List<string?> changed = [];
        row.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        row.Update(first);
        Assert.Empty(changed);
        row.Update(first with { Now = "2 KiB/s", Requests = "2" });
        Assert.Equal([nameof(StatisticsRow.Now), nameof(StatisticsRow.Requests), nameof(StatisticsRow.Details)], changed);
        Assert.Equal(Avalonia.Media.FontWeight.Normal, row.Weight);
        Assert.Equal(Avalonia.Media.FontWeight.SemiBold, new StatisticsRow(first with { IsTotal = true }).Weight);
    }
}
