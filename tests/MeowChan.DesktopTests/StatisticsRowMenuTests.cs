using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.Stats;

namespace MeowChan.DesktopTests;

/// <summary>The context menu of the statistics source table and the text its "Copy row" puts on the clipboard.</summary>
[Collection("Localization")]
public sealed class StatisticsRowMenuTests : IDisposable
{
    public StatisticsRowMenuTests() { Texts.Initialize(); Texts.SetLanguage("en"); }
    public void Dispose() { Texts.Initialize(); Texts.SetLanguage("en"); }

    private static StatisticsRow Row(bool total = false) => new(new IoStatsRowText(total ? string.Empty : "boards.4chan.org", total, total ? "Total" : "4chan",
        "1.2 MiB/s", "300 MiB", "1.1 GiB", "5.0 GiB", "280 MiB", "12 MiB", "4,321", "7"), 14);

    [Fact]
    public void ASourceRowOffersCopyRowCopySourceAndResetLast()
    {
        ContextMenuModel model = StatisticsRowMenu.Compose("4chan", false, true, () => { }, () => { }, () => { }).ToModel();
        Assert.Equal(new[] { "statistics.copy-row", "statistics.copy-source", "statistics.reset" }, model.Entries.Select(entry => entry.CommandId));
        Assert.Equal(new[] { "Copy row", "Copy source name", "Reset statistics…" }, model.Entries.Select(entry => entry.Label));
        Assert.Equal(2, model.Sections.Count);
        Assert.Equal("statistics.reset", model.Sections[1].Single().CommandId);
        Assert.All(model.Entries, entry => Assert.True(CommandCatalog.TryGet(entry.CommandId!, out _)));
    }

    [Fact]
    public void TheTotalRowHasNoSingleSourceToCopy()
    {
        ContextMenuModel model = StatisticsRowMenu.Compose("Total", true, true, () => { }, () => { }, () => { }).ToModel();
        Assert.Equal(new[] { "statistics.copy-row", "statistics.reset" }, model.Entries.Select(entry => entry.CommandId));
    }

    [Fact]
    public void ResetIsListedButUnavailableWhileAResetRunsOrNoHubIsAttached()
    {
        ContextMenuEntry reset = StatisticsRowMenu.Compose("4chan", false, false, () => { }, () => { }, () => { }).ToModel().Entries.Last();
        Assert.False(reset.Available);
        Assert.Equal("Currently unavailable for this target.", reset.Reason);
    }

    [Fact]
    public void EachEntryReachesItsOwnAction()
    {
        List<string> log = [];
        foreach (ContextMenuEntry entry in StatisticsRowMenu.Compose("4chan", false, true, () => log.Add("row"), () => log.Add("source"), () => log.Add("reset")).ToModel().Entries)
            entry.Invoke!();
        Assert.Equal(new[] { "row", "source", "reset" }, log);
    }

    [Fact]
    public void CopyRowIsTheNameAndOneLabelledLinePerColumnInTheTablesOrder()
    {
        Assert.Equal(string.Join("\n", "4chan", "Network now: 1.2 MiB/s", "Network, session: 300 MiB", "Network, today: 1.1 GiB", "Network, 14 days: 5.0 GiB",
            "Disk written, session: 280 MiB", "Disk read, session: 12 MiB", "Requests, session: 4,321", "Errors, session: 7"), StatisticsRowMenu.RowText(Row(), 14));
        Texts.SetLanguage("de");
        Assert.StartsWith("4chan\nNetz jetzt: ", StatisticsRowMenu.RowText(Row(), 14));
    }
}
