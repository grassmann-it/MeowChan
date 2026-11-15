using System.Globalization;
using MeowChan.Desktop;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using MeowChan.Downloader;
using MeowChan.Interfaces;

namespace MeowChan.DesktopTests;

// The stat footer of a Sources card (StatFooterRow): the pure rules of how a count is written and how the row adapts to its width, plus
// the data of the card. The controls themselves need the platform (icon masks, text engine) and are covered by the desktop smoke.
[Collection("Localization")]
public sealed class StatFooterLayoutTests
{
    private static readonly CultureInfo En = CultureInfo.GetCultureInfo("en");
    private static readonly CultureInfo De = CultureInfo.GetCultureInfo("de");

    [Theory]
    [InlineData(0, "0")]
    [InlineData(1, "1")]
    [InlineData(10, "10")]
    [InlineData(73, "73")]
    [InlineData(1000, "1000")]
    [InlineData(9999, "9999")]
    [InlineData(10_000, "10k")]
    [InlineData(12_345, "12.3k")]
    [InlineData(99_949, "99.9k")]
    [InlineData(123_456, "123k")]
    [InlineData(999_499, "999k")]
    [InlineData(999_999, "1M")]
    [InlineData(1_234_567, "1.2M")]
    [InlineData(2_500_000_000, "2.5G")]
    public void CountsAreWrittenInFullBelowTheThresholdAndAbbreviatedAbove(long value, string expected) =>
        Assert.Equal(expected, StatFooterLayout.Format(value, En));

    [Fact]
    public void TheThresholdIsOneConstantAndTheDecimalSeparatorFollowsTheLanguage()
    {
        Assert.Equal(10_000, StatFooterLayout.AbbreviateFrom);
        Assert.Equal("9999", StatFooterLayout.Format(StatFooterLayout.AbbreviateFrom - 1, En));
        Assert.Equal("10k", StatFooterLayout.Format(StatFooterLayout.AbbreviateFrom, En));
        Assert.Equal("12,3k", StatFooterLayout.Format(12_345, De));
        Assert.Equal("0", StatFooterLayout.Format(-5, En));
    }

    [Fact]
    public void TheTooltipKeepsTheExactNumber()
    {
        Assert.Equal("12,345", StatFooterLayout.FormatFull(12_345, En));
        Assert.Equal("12.345", StatFooterLayout.FormatFull(12_345, De));
        Assert.Equal("73", StatFooterLayout.FormatFull(73, En));
    }

    // The width of a segment is reserved for ReservedValueText, so no value text may ever be longer than it. A value that grows from 0 to
    // 10 to 1000 (or past the threshold) therefore cannot widen its segment and push a neighbour.
    [Fact]
    public void NoValueTextIsLongerThanTheReservedText()
    {
        int reserved = StatFooterLayout.ReservedValueText.Length;
        long[] samples = [0, 1, 9, 10, 99, 100, 999, 1000, 9999, 10_000, 99_949, 99_950, 100_000, 999_499, 999_500, 999_999, 1_000_000,
            99_949_999, 999_999_999, 1_000_000_000, 999_999_999_999, 123_456_789_012_345, long.MaxValue];
        foreach (long sample in samples)
            foreach (CultureInfo culture in new[] { En, De })
            {
                string text = StatFooterLayout.Format(sample, culture);
                Assert.True(text.Length <= reserved, sample + " -> " + text);
            }
        for (long value = 0; value < 150_000; value += 7)
            Assert.True(StatFooterLayout.Format(value, En).Length <= reserved, value.ToString(CultureInfo.InvariantCulture));
    }

    // Slot widths come from the label-and-value width (Full) and the icon-and-value width (IconsOnly); the numbers are not an input.
    [Fact]
    public void WideCardShowsEverythingAndNarrowerDropsTheLabelsFirst()
    {
        // 4 slots of 56 dip with 8 dip gaps = 248 dip with labels.
        Assert.Equal(new StatFooterLayout.Plan(StatFooterMode.Full, 4), StatFooterLayout.Resolve(256, 4, 56, 48, 8));
        Assert.Equal(new StatFooterLayout.Plan(StatFooterMode.Full, 4), StatFooterLayout.Resolve(248, 4, 56, 48, 8));
        // 4 slots of 48 = 216 without labels.
        Assert.Equal(new StatFooterLayout.Plan(StatFooterMode.IconsOnly, 4), StatFooterLayout.Resolve(247, 4, 56, 48, 8));
        Assert.Equal(new StatFooterLayout.Plan(StatFooterMode.IconsOnly, 4), StatFooterLayout.Resolve(216, 4, 56, 48, 8));
    }

    [Fact]
    public void TheNarrowestCardKeepsTheMostImportantCountsAndAtLeastOne()
    {
        Assert.Equal(new StatFooterLayout.Plan(StatFooterMode.IconsOnly, 3), StatFooterLayout.Resolve(215, 4, 56, 48, 8));
        Assert.Equal(new StatFooterLayout.Plan(StatFooterMode.IconsOnly, 3), StatFooterLayout.Resolve(160, 4, 56, 48, 8));
        Assert.Equal(new StatFooterLayout.Plan(StatFooterMode.IconsOnly, 2), StatFooterLayout.Resolve(159, 4, 56, 48, 8));
        Assert.Equal(new StatFooterLayout.Plan(StatFooterMode.IconsOnly, 1), StatFooterLayout.Resolve(55, 4, 56, 48, 8));
        Assert.Equal(new StatFooterLayout.Plan(StatFooterMode.IconsOnly, 1), StatFooterLayout.Resolve(10, 4, 56, 48, 8));
        Assert.Equal(new StatFooterLayout.Plan(StatFooterMode.IconsOnly, 1), StatFooterLayout.Resolve(0, 4, 56, 48, 8));
    }

    [Fact]
    public void AnUnboundedWidthAndAnEmptyRowAreHandled()
    {
        Assert.Equal(new StatFooterLayout.Plan(StatFooterMode.Full, 4), StatFooterLayout.Resolve(double.PositiveInfinity, 4, 56, 48, 8));
        Assert.Equal(new StatFooterLayout.Plan(StatFooterMode.Full, 0), StatFooterLayout.Resolve(100, 0, 56, 48, 8));
    }

    [Fact]
    public void ThePlanOnlyDependsOnTheWidthNeverOnHowManyDigitsThereAre()
    {
        // The same inputs give the same plan on every call: the function has no value argument at all, and the slot widths it reads are
        // the label-and-reserved-value widths, which do not change when a count goes from 0 to 10 or 1000.
        foreach (double width in new double[] { 120, 190, 216, 247, 248, 256, 400 })
        {
            StatFooterLayout.Plan first = StatFooterLayout.Resolve(width, 4, 56, 48, 8);
            foreach (long value in new long[] { 0, 1, 10, 1000, 123_456 })
                Assert.Equal(first, StatFooterLayout.Resolve(width, 4, 56, 48, 8));
        }
    }

    [Fact]
    public void VisibleSetKeepsTheBestPrioritiesInItemOrder()
    {
        // Sources: Threads 0, Boards 3, Queued 2, Active 1.
        int[] priorities = [0, 3, 2, 1];
        Assert.Equal(new[] { true, true, true, true }, StatFooterLayout.VisibleSet(priorities, 4));
        Assert.Equal(new[] { true, false, true, true }, StatFooterLayout.VisibleSet(priorities, 3));
        Assert.Equal(new[] { true, false, false, true }, StatFooterLayout.VisibleSet(priorities, 2));
        Assert.Equal(new[] { true, false, false, false }, StatFooterLayout.VisibleSet(priorities, 1));
        Assert.Equal(new[] { true, true, false }, StatFooterLayout.VisibleSet([5, 5, 5], 2));
    }

    [Fact]
    public void SourceCardStatsAreStructuredAndTheFooterItemsFollowTheCatalog()
    {
        Texts.Initialize(); Texts.SetLanguage("en");
        DesktopThreadEntry thread = new(42, new ThreadSnapshot(new ResourceId("4chan", "g", "123"), "https://boards.4chan.org/g/thread/123",
            "Thread", "/media/123", ThreadActivity.Error, ThreadSourceState.Active, null, 3, 0, new Dictionary<string, FailureInfo>(), null,
            new DownloadCounts(2, 1)), "4chan", "g");
        DesktopSessionView view = new([thread], [], [], DesktopSessionState.Ready, null);
        ProviderDescriptor Provider(string id) => new(id, id, new Uri("https://example.invalid/" + id), ProviderCapabilities.ThreadWatch,
            Array.Empty<string>(), null, false);
        IReadOnlyList<SourceRow> rows = SourcesSnapshot.From([Provider("4chan"), Provider("8kun")], view);
        Assert.Equal(new SourceWatchStats(1, 0, 2, 1), rows[0].Stats);
        Assert.Null(rows[1].Stats);
        // The summary text stays: the details pane and the accessible name of the card still say it in words.
        Assert.Contains("Threads: 1", rows[0].Summary);
        Assert.Null(Assert.Single(SourcesSnapshot.From([Provider("4chan")], view with { State = DesktopSessionState.Stopped })).Stats);

        IReadOnlyList<StatFooterItem> items = SourceStatFooter.Items(rows[0].Stats!);
        Assert.Equal(["threads", "boards", "queued", "active"], items.Select(item => item.Id));
        Assert.Equal(["Threads", "Boards", "Queued", "Active"], items.Select(item => item.Label));
        Assert.Equal([1L, 0L, 2L, 1L], items.Select(item => item.Value));
        Assert.Equal([0, 3, 2, 1], items.Select(item => item.Priority));
        Assert.Equal("Threads: 73", Texts.T("stats.footer.tip", "Threads", "73"));
        foreach (StatFooterItem item in items)
            Assert.True(File.Exists(Path.Combine(FindRoot(), "Assets", "icons", "feather", "masks", item.Icon + ".png")), item.Icon);
    }

    private static string FindRoot()
    {
        string? directory = AppContext.BaseDirectory;
        while (directory != null && !File.Exists(Path.Combine(directory, "MeowChan.csproj"))) directory = Path.GetDirectoryName(directory);
        return directory ?? throw new DirectoryNotFoundException("MeowChan.csproj");
    }
}
