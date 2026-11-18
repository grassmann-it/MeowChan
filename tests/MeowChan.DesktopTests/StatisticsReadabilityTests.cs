using System.Globalization;
using System.Runtime.CompilerServices;
using System.Xml.Linq;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.Stats;

namespace MeowChan.DesktopTests;

[Collection("Localization")]
public sealed class StatisticsReadabilityTests : IDisposable
{
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    public StatisticsReadabilityTests() { Texts.Initialize(); Texts.SetLanguage("en"); }
    public void Dispose() { Texts.Initialize(); Texts.SetLanguage("en"); }

    private static IoSeries Series(int capacity, float[] net, float[]? write = null, float[]? read = null) =>
        new(capacity: capacity, count: net.Length, netDown: net, netUp: new float[net.Length],
            diskWrite: write ?? new float[net.Length], diskRead: read ?? new float[net.Length], requests: new float[net.Length]);

    [Theory]
    [InlineData(300, 2500, "en", "750")]
    [InlineData(120, 500, "en", "60")]
    [InlineData(3, 2500, "en", "7.5")]
    [InlineData(3, 2500, "de", "7,5")]
    [InlineData(0, 2500, "de", "0")]
    public void AxisUsesTheConfiguredDurationWithNoWholeSecondRounding(int samples, int interval, string language, string expected)
    {
        Texts.SetLanguage(language);
        Assert.Equal(expected, IoStatsDisplay.HistorySecondsText(samples, interval));
        Assert.Contains(expected, Texts.T("stats.history", expected));
    }

    [Fact]
    public void FillingHistoryDoesNotStretchAShortSessionAcrossTheConfiguredWindow()
    {
        IoSeries series = Series(300, [10, 20, 30]);
        (int skip, int capacity) = IoStatsDisplay.GraphWindow(series, 2500);
        Assert.Equal((0, 301), (skip, capacity));
        var points = IoGraphGeometry.Points(series.NetDown, capacity, 750, 150, 30, bottom: 150);
        Assert.Equal(750, points[^1].X);
        Assert.Equal(745, points[0].X); // oldest instant is 5 s ago, not the left boundary 750 s ago
    }

    [Theory]
    [InlineData(500, 121)]
    [InlineData(2500, 25)]
    [InlineData(10000, 7)]
    public void RecentHeaderWindowKeepsOnlyInstantsInTheLastSixtySeconds(int interval, int count)
    {
        float[] net = Enumerable.Repeat(1000f, 300).ToArray();
        net[0] = 50_000_000; // deliberately outside the shown window
        IoGraph graph = new() { HistoryWindowSeconds = 60, SampleIntervalMs = () => interval };
        graph.SetSeries(Series(300, net));
        Assert.Equal(count, graph.SampleCount);
        Assert.Equal("60", IoStatsDisplay.HistorySecondsText(graph.WindowIntervals, interval, CultureInfo.InvariantCulture));
        Assert.Equal(1000, graph.TrafficPeak);
        Assert.Equal(IoGraphGeometry.DefaultMinimumPeak, graph.Peak);
        Assert.Equal(50_000_000, net[0]); // never mutate the snapshot or the page's history
    }

    [Fact]
    public void HeaderUsesTheShorterRetainedWindowAndReadsNewIntervalsOnEachSnapshot()
    {
        int interval = 2500;
        IoGraph graph = new() { HistoryWindowSeconds = 60, SampleIntervalMs = () => interval };
        graph.SetSeries(Series(10, [1, 2]));
        Assert.Equal(10, graph.WindowIntervals);
        Assert.Equal(2, graph.SampleCount);
        interval = 10000;
        graph.SetSeries(Series(10, [1, 2]));
        Assert.Equal(6, graph.WindowIntervals);
    }

    [Fact]
    public void DiskScaleIgnoresOldSpikesAndNetworkOnlyIgnoresDisk()
    {
        float[] disk = new float[300];
        disk[0] = 50_000_000; // 750 s ago: outside the 60 s window
        disk[^1] = 2_000_000;
        IoSeries series = Series(300, new float[300], disk);
        IoGraph write = new() { Source = IoGraphSource.DiskWrite, HistoryWindowSeconds = 60, SampleIntervalMs = () => 2500 };
        write.SetSeries(series);
        Assert.Equal(2_000_000, write.TrafficPeak);
        Assert.Equal(2_097_152, write.Peak); // the 2 MiB step of the visible value, not the old 48 MiB spike
        IoGraph network = new() { HistoryWindowSeconds = 60, SampleIntervalMs = () => 2500 };
        network.SetSeries(series);
        Assert.False(network.HasTraffic);
        Assert.Equal(0, network.TrafficPeak);
        Assert.Equal(IoGraphGeometry.DefaultMinimumPeak, network.Peak);
        // ShowDisk only keeps old markup compiling: it switches no combined network-plus-disk graph on.
        network.ShowDisk = true;
        network.SetSeries(series);
        Assert.False(network.HasTraffic);
        Assert.Equal(IoGraphGeometry.DefaultMinimumPeak, network.Peak);
    }

    [Fact]
    public void ZeroTrafficAndResetAreNotReportedAsASixtyFourKibPeak()
    {
        IoGraph graph = new();
        graph.SetSeries(Series(300, [0, 0]));
        Assert.False(graph.HasTraffic);
        graph.SetSeries(Series(300, [123, 456]));
        Assert.True(graph.HasTraffic);
        Assert.Equal(456, graph.TrafficPeak);
        graph.SetSeries(IoSeries.Empty);
        Assert.False(graph.HasTraffic);
        Assert.Equal(0, graph.TrafficPeak);
        Assert.Equal(0, graph.SampleCount);
        Assert.Equal(0, graph.WindowIntervals);
    }

    [Theory]
    [InlineData("en", "Network, 7 days")]
    [InlineData("de", "Netz, 7 Tage")]
    public void CompactRowDetailsKeepEveryHiddenCounterAndTheRealRetention(string language, string retention)
    {
        Texts.SetLanguage(language);
        IoStatsRowText text = new("fixture", false, "Synthetic source", "1 B/s", "2 B", "3 B", "4 B", "5 B", "6 B", "7", "8");
        StatisticsRow row = new(text, 7);
        Assert.Equal(5, row.Details.Split('\n').Length);
        Assert.Contains(retention + ": 4 B", row.Details);
        Assert.Contains(Texts.T("stats.col.today") + ": 3 B", row.Details);
        Assert.Contains(Texts.T("stats.col.diskWritten") + ": 5 B", row.Details);
        Assert.Contains(Texts.T("stats.col.diskRead") + ": 6 B", row.Details);
        Assert.Contains(Texts.T("stats.col.requests") + ": 7", row.Details);
        int changes = 0;
        row.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(StatisticsRow.Details)) changes++; };
        row.Update(text, 7);
        Assert.Equal(0, changes);
        row.Update(text, 8);
        Assert.Equal(1, changes);
    }

    // The kept days are a count: one day, many days, and the languages with more forms pick theirs.
    [Theory]
    [InlineData("en", 1, "Network, 1 day")]
    [InlineData("en", 30, "Network, 30 days")]
    [InlineData("de", 1, "Netz, 1 Tag")]
    [InlineData("de", 30, "Netz, 30 Tage")]
    [InlineData("ru", 1, "Сеть, 1 день")]
    [InlineData("ru", 3, "Сеть, 3 дня")]
    [InlineData("ru", 30, "Сеть, 30 дней")]
    [InlineData("cs", 1, "Síť, 1 den")]
    [InlineData("cs", 3, "Síť, 3 dny")]
    [InlineData("cs", 30, "Síť, 30 dnů")]
    [InlineData("pl", 1, "Sieć, 1 dzień")]
    [InlineData("pl", 30, "Sieć, 30 dni")]
    [InlineData("fr", 1, "Réseau, 1 jour")]
    [InlineData("es", 1, "Red, 1 día")]
    [InlineData("it", 1, "Rete, 1 giorno")]
    [InlineData("pt-BR", 1, "Rede, 1 dia")]
    [InlineData("ja", 30, "ネットワーク、30日分")]
    [InlineData("ko", 1, "네트워크, 1일")]
    [InlineData("zh-Hans", 30, "网络，30 天")]
    public void TheRetentionColumnNamesItsDaysInTheRightForm(string language, int days, string expected)
    {
        Texts.SetLanguage(language);
        Assert.Equal(expected, Texts.Count("stats.col.retained", days));
    }

    // An error count leads to the diagnostics only where there is somewhere to go: a counted error on a source the diagnostics
    // know by id, or the total. The collective rows (other, local files, unknown) are no source id.
    [Theory]
    [InlineData("4chan", false, 3, true)]
    [InlineData("4chan", false, 0, false)]
    [InlineData("", true, 3, true)]
    [InlineData("", true, 0, false)]
    [InlineData("other", false, 3, false)]
    [InlineData("local", false, 3, false)]
    [InlineData("unknown", false, 3, false)]
    public void TheErrorCountOpensTheDiagnosticsOnlyForASourceOrTheTotal(string key, bool total, long errors, bool canOpen)
    {
        Texts.SetLanguage("en");
        StatisticsRow row = new(new IoStatsRowText(key, total, total ? "Total" : "Name", "0 B/s", "0 B", "0 B", "0 B", "0 B", "0 B", "0", errors.ToString(), errors));
        Assert.Equal(canOpen, row.CanOpenDiagnostics);
        Assert.Equal(total ? null : key, row.DiagnosticsSourceId);
        Assert.Equal(total ? "Show all errors in Diagnostics" : "Show the errors of Name in Diagnostics", row.ErrorsLinkText);
    }

    [Fact]
    public void TheErrorLinkAppearsAndGoesWhenTheCountChangesAndFollowsTheLanguage()
    {
        Texts.SetLanguage("en");
        IoStatsRowText none = new("4chan", false, "4chan", "0 B/s", "0 B", "0 B", "0 B", "0 B", "0 B", "0", "0");
        StatisticsRow row = new(none);
        List<string?> changed = [];
        row.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        row.Update(none with { Errors = "2", ErrorCount = 2 });
        Assert.True(row.CanOpenDiagnostics);
        Assert.Contains(nameof(StatisticsRow.CanOpenDiagnostics), changed);
        changed.Clear();
        row.Update(none with { Errors = "3", ErrorCount = 3 });
        Assert.DoesNotContain(nameof(StatisticsRow.CanOpenDiagnostics), changed); // already true: nothing to announce
        Texts.SetLanguage("de");
        row.Update(none with { Errors = "3", ErrorCount = 3 });
        Assert.Equal("Fehler von 4chan in der Diagnose zeigen", row.ErrorsLinkText);
        row.Update(none);
        Assert.False(row.CanOpenDiagnostics);
    }

    [Fact]
    public void TheStatisticsPageHasNoActivityLineNoHeaderNoteAndResetSitsInTheHistoryBar()
    {
        XDocument page = Markup("Desktop/Views/Statistics/StatisticsPage.axaml");
        Assert.DoesNotContain(page.Descendants(), e => (string?)e.Attribute(X + "Name") is "ActivityText" or "HistoryNote");
        // Reset is a control of the history bar, not of the page head: the right edge of the head is for Back and Close.
        XElement reset = Named(page, "ResetStatisticsButton");
        static string? WorkViewId(XElement e) => e.Attributes().FirstOrDefault(a => a.Name.LocalName == "WorkViewElement.Id")?.Value;
        XElement bar = reset.Ancestors().First(e => e.Name.LocalName == "Grid" && WorkViewId(e) != null);
        Assert.Equal("statistics.history", WorkViewId(bar));
        Assert.Contains(bar.Descendants(), e => (string?)e.Attribute(X + "Name") == "PeakSlot");
        // Hosted in the Status page the head (title, subtitle) goes and the margin is the page's: both are named for SetHosted.
        Assert.Contains(Named(page, "StatisticsHeader").Descendants(), e => (string?)e.Attribute(X + "Name") == "StatisticsTitle");
        Named(page, "StatisticsContent");
        // The upload figure and the axes of all three graphs.
        foreach (string name in new[] { "NetUpCaption", "NetUpValue", "ScaleTop", "ScaleHalf", "HistoryStart", "HistoryEnd", "WriteScaleTop", "WriteScaleHalf",
                     "WriteHistoryStart", "WriteHistoryEnd", "ReadScaleTop", "ReadScaleHalf", "ReadHistoryStart", "ReadHistoryEnd" })
            Named(page, name);
    }

    [Fact]
    public void CompactLayoutHasFourSummaryColumnsAndExpandableDetailsNotAHiddenHorizontalTail()
    {
        XDocument page = Markup("Desktop/Views/Statistics/StatisticsPage.axaml");
        XElement list = Named(page, "StatisticsCompactList");
        XElement expander = Assert.Single(list.Descendants(), e => e.Name.LocalName == "Expander");
        Assert.Contains(expander.Descendants(), e => (string?)e.Attribute("Text") == "{Binding Details}");
        foreach (string property in new[] { "Name", "Now", "Session", "Errors" })
            Assert.Contains(expander.Descendants(), e => (string?)e.Attribute("Text") == "{Binding " + property + "}");
        Assert.Equal(9, Named(page, "StatisticsGrid").Descendants().Count(e => e.Name.LocalName == "DataGridTemplateColumn"));
        Assert.Equal("Disabled", (string?)Named(page, "StatisticsScroll").Attribute("HorizontalScrollBarVisibility"));
    }

    [Fact]
    public void PeakHasAnAlwaysMeasuredHistoryContainerRatherThanAHiddenColumn()
    {
        XDocument page = Markup("Desktop/Views/Statistics/StatisticsPage.axaml");
        XElement slot = Named(page, "PeakSlot");
        XElement peak = Named(page, "PeakText");

        Assert.Equal("2", (string?)slot.Attribute("Grid.Column")); // left of it: the title and, in the same bar, Reset
        Assert.Null(slot.Attribute("IsVisible"));
        Assert.Equal("False", (string?)slot.Attribute("IsHitTestVisible"));
        // The peak belongs to the scrolling history header, not to the overlaid result toast.
        // The exact retained width is captured from native layout, not a fixed locale-specific DIP guess;
        // StatisticsReadabilityChecks verifies unchanged graph/label bounds and scroll offset on reset.
        Assert.Contains(slot.Descendants(), element => ReferenceEquals(element, peak));
        Assert.Contains(peak.Ancestors(), element => element.Name.LocalName == "ScrollViewer");
    }

    [Fact]
    public void ConfirmationAndResultAreOutsideThePageFlowAndTheHeaderUsesInterTabularFigures()
    {
        XDocument page = Markup("Desktop/Views/Statistics/StatisticsPage.axaml");
        Assert.Contains(Named(page, "ResetConfirmPanel").Ancestors(), e => e.Name.LocalName == "Flyout");
        Assert.DoesNotContain(Named(page, "StatisticsStatus").Ancestors(), e => e.Name.LocalName == "ScrollViewer");
        // HB1: the measurement note is the structured popover of the help button, no longer a flyout of the page itself.
        XElement help = Named(page, "StatisticsHelpButton");
        Assert.Equal("HelpButton", help.Name.LocalName);
        Assert.Equal("help.stats", (string?)help.Attribute("HelpKey"));
        XDocument shell = Markup("Desktop/Shell/MainWindow.axaml");
        XElement graph = Named(shell, "TransferSpeedGraph");
        Assert.True(double.Parse((string)graph.Attribute("Width")!, CultureInfo.InvariantCulture) >= 120);
        Assert.Equal("60", (string?)graph.Attribute("HistoryWindowSeconds"));
        foreach (string name in new[] { "TransferSpeedLabel", "TransferUploadLabel", "TransferDiskReadLabel", "TransferDiskLabel" })
        {
            XElement label = Named(shell, name);
            Assert.Equal("tnum", (string?)label.Attribute("FontFeatures"));
            Assert.Equal("{DynamicResource Workspace.Font.UI}", (string?)label.Attribute("FontFamily"));
            Assert.DoesNotContain("mono", (string?)label.Attribute("Classes") ?? "");
        }
    }

    [Theory]
    [InlineData("en")]
    [InlineData("de")]
    public void ResetQuestionDoesNotExposeThePersistenceFilename(string language)
    {
        Texts.SetLanguage(language);
        Assert.DoesNotContain("io-stats.json", Texts.T("stats.reset.question"));
    }

    private static XElement Named(XDocument document, string name) =>
        Assert.Single(document.Descendants(), e => (string?)e.Attribute(X + "Name") == name);
    private static XDocument Markup(string path, [CallerFilePath] string sourcePath = "") =>
        XDocument.Load(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourcePath)!, "../..", path)));
}
