using Avalonia;
using Avalonia.Controls;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using MeowChan.Archive;
using MeowChan.Gallery;
using MeowChan.Persistence;
using MeowChan.Storage;
using MeowChan.Tags;

namespace MeowChan.DesktopTests;

// The gallery tag panel fills its height with suggestion chips: how many fit (pure wrap arithmetic over measured chip sizes), which
// chips keep their number keys, where "More…" and the assigned chips sit, and what limit the ranking is asked for.
[Collection("Localization")]
public sealed class QuickTagStripFitTests : IDisposable
{
    public QuickTagStripFitTests() { Texts.Initialize(); Texts.SetLanguage("en"); }
    public void Dispose() { Texts.Initialize(); Texts.SetLanguage("en"); }

    private static Size[] Chips(int count, double width = 80, double height = 32) => Enumerable.Repeat(new Size(width, height), count).ToArray();

    [Theory]
    // 3 chips of 80 per 280-dip row (80+8+80+8+80 = 256; a fourth would need 344): rows of 32 dip.
    [InlineData(30, 280, 320, 30)]   // ten rows fit exactly
    [InlineData(40, 280, 320, 30)]   // only ten rows fit: 30 chips
    [InlineData(40, 280, 100, 9)]    // three rows would fit (9), a fourth row does not
    [InlineData(40, 280, 128, 12)]   // four rows
    [InlineData(40, 280, 127, 9)]    // the fourth row is 1 dip short: the numbered nine stay, nothing beyond
    [InlineData(40, 280, 0, 9)]      // no height at all: the numbered nine are never dropped
    [InlineData(5, 280, 0, 5)]       // fewer candidates than nine: all of them
    public void TheCountIsTheLongestPrefixWhoseRowsFitTheHeightAndNeverBelowTheConfiguredCount(int candidates, double width, double height, int expected) =>
        Assert.Equal(expected, QuickTagStripFit.Count(Chips(candidates), width, height, 8, QuickTagOptions.DefaultSuggestionCount));

    [Theory]
    // The minimum is the configured suggestion count (3 to 18), not the nine numbered chips. 80-wide chips, three to a 280 row, rows of 32.
    [InlineData(3, 40, 0, 3)]       // no height: just the three
    [InlineData(3, 40, 100, 9)]     // three rows of three fit: more than the minimum fill the height, no forced scrollbar
    [InlineData(3, 40, 64, 6)]      // two rows fit: six
    [InlineData(3, 40, 32, 3)]      // one row: the three
    [InlineData(9, 40, 64, 9)]      // below nine rows the nine stay (the default)
    [InlineData(18, 40, 0, 18)]     // a short panel keeps all eighteen (it scrolls)
    [InlineData(18, 40, 100, 18)]   // three rows would be nine: still eighteen
    [InlineData(18, 40, 320, 30)]   // ten rows: more than the minimum
    [InlineData(18, 12, 0, 12)]     // fewer candidates than the minimum: all of them
    public void TheMinimumIsTheConfiguredSuggestionCountAtThreeNineAndEighteen(int minimum, int candidates, double height, int expected) =>
        Assert.Equal(expected, QuickTagStripFit.Count(Chips(candidates), 280, height, 8, minimum));

    [Theory]
    [InlineData(3)]
    [InlineData(9)]
    [InlineData(18)]
    [InlineData(1, 3)]      // clamped into the setting's range like every other limit
    [InlineData(99, 18)]
    public void ThePanelKeepsTheConfiguredCountAsItsStripMinimum(int configured, int expected = -1)
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        PrivateTagStore store = new(temp.Path, lease);
        (QuickTagSurface surface, QuickTagSession session, List<int> limits) = Surface(store, fill: true, count: configured);
        using (session)
        using (surface)
        {
            Assert.Equal(expected < 0 ? configured : expected, surface.StripMinimum);
            // Not laid out yet: the configured count shows, and the ranking is asked for the panel limit (never below the count).
            Assert.Equal(surface.StripMinimum, surface.Shown.Count);
            Assert.All(limits, limit => Assert.Equal(Math.Max(surface.StripMinimum, 60), limit));
        }
    }

    [Theory]
    [InlineData(false, 0, 200, true)]       // never measured
    [InlineData(true, 280, 280, false)]     // measured for this width: kept
    [InlineData(true, 280, 200, true)]      // the panel got narrower: measure again
    [InlineData(true, 200, 280, true)]      // ... or wider
    public void AChipSizeIsKeptOnlyForTheWidthItWasMeasuredAt(bool measured, double measuredWidth, double width, bool needs) =>
        Assert.Equal(needs, QuickTagStripFit.NeedsMeasure(measured, measuredWidth, width));

    [Fact]
    public void AStaleMeasurementIsTheOnlyDifferenceBetweenTheCachedAndTheFreshFitAfterANarrowing()
    {
        // A chip whose desired width follows the offered width (a label that wraps): at 400 it is 150 wide, at 300 it is 90 wide.
        static Size Measure(double width) => new(width > 350 ? 150 : 90, 32);
        double[] widths = { 400, 300 };
        Size[] cached = Enumerable.Repeat(Measure(widths[0]), 20).ToArray();
        Size[] fresh = Enumerable.Repeat(Measure(widths[1]), 20).ToArray();
        // The cached sizes (measured at 400) make the narrow strip fit fewer chips than the fresh ones: the bug the width stamp removes.
        int stale = QuickTagStripFit.Count(cached, widths[1], 160, 8, 3), real = QuickTagStripFit.Count(fresh, widths[1], 160, 8, 3);
        Assert.True(real > stale, $"fresh {real} vs stale {stale}");
        // What the surface does now: the width differs, so the size is taken again, and the count is the one on fresh sizes.
        Size[] kept = cached.Select(size => QuickTagStripFit.NeedsMeasure(true, widths[0], widths[1]) ? Measure(widths[1]) : size).ToArray();
        Assert.Equal(real, QuickTagStripFit.Count(kept, widths[1], 160, 8, 3));
    }

    [Fact]
    public void ARowTakesAChipOnlyWhileItsWidthFitsAndAWideChipStartsTheNextRow()
    {
        Size[] sizes = { new(200, 32), new(100, 32), new(60, 32), new(60, 32), new(60, 32), new(60, 32), new(60, 32), new(60, 32), new(60, 32), new(60, 32) };
        // 280 wide: [200] | [100 60 60: 100+8+60+8+60 = 236] | [60 60 60 60: 60*4+24 = 264] ... the tenth chip is index 9 (extras start there).
        Assert.Equal(10, QuickTagStripFit.Count(sizes, 280, 4 * 32, 8, 9));
        Assert.Equal(9, QuickTagStripFit.Count(sizes, 280, 3 * 32, 8, 9));
        // A taller chip makes its whole row taller (rows have no line spacing).
        Size[] tall = { new(60, 32), new(60, 40), new(60, 32), new(60, 32), new(60, 32), new(60, 32), new(60, 32), new(60, 32), new(60, 32), new(60, 32), new(60, 32) };
        // Four chips per row: the first row is 40 dip high (the tall chip), the next two 32. The tenth chip sits in the third row.
        Assert.Equal(9, QuickTagStripFit.Count(tall, 280, 40 + 32 + 31, 8, 9));
        Assert.Equal(11, QuickTagStripFit.Count(tall, 280, 40 + 32 + 32, 8, 9));
    }

    [Fact]
    public void TheFitIsStableForTheSameSizesSoItCannotLoop()
    {
        Size[] sizes = Chips(60, 70);
        int first = QuickTagStripFit.Count(sizes, 296, 400, 8, 9);
        Assert.Equal(first, QuickTagStripFit.Count(sizes.Take(first).ToArray(), 296, 400, 8, 9)); // trimming to the fit and fitting again changes nothing
        Assert.True(QuickTagStripFit.Count(sizes, 296, 800, 8, 9) >= first);                      // more height never shows fewer
        Assert.True(QuickTagStripFit.Count(sizes, 400, 400, 8, 9) >= first);                      // more width never shows fewer
    }

    private static readonly DateTimeOffset Day = new(2026, 11, 14, 0, 0, 0, TimeSpan.Zero);

    private static MediaLibraryEntry Entry() =>
        new(new ResourceId("s", "b", "a"), "Ta", 1, Day, Day, "1", 1, null, 0,
            "att-v1:" + new string('a', 64), "/dir/a", "asset-a", new string('a', 64), 1, "m1", "original",
            "fa.png", SavedMediaKind.Image, MediaLibraryPresence.Present, false, false);

    private static (QuickTagSurface Surface, QuickTagSession Session, List<int> Limits) Surface(PrivateTagStore store, bool fill, int panelLimit = 60, int count = QuickTagOptions.DefaultSuggestionCount)
    {
        List<int> limits = new();
        QuickTagSession session = new(QuickTagTarget.For(new MediaTagSession(new MediaTagSubject.Bound(MediaLibraryFilterEngine.TagTarget(Entry())!),
            () => new PrivateTagLoadSnapshot(PrivateTagLoadState.Ready, store.ReadSnapshot(), null),
            (name, subject, token) => Task.FromResult(store.DefineAndSetMediaSubjects(name, [subject], null, token)),
            (id, subject, assigned, token) => Task.FromResult(store.SetMediaSubjects(id, [subject], assigned, null, token)))));
        QuickTagSurface surface = new(session, new QuickTagOptions { PanelSuggestionLimit = panelLimit, SuggestionCount = count }, platform: ShortcutPlatform.Linux) { FillSuggestions = fill };
        surface.Suggestions = new DelegateQuickTagSuggestions(request =>
        {
            limits.Add(request.Limit);
            return Enumerable.Range(1, request.Limit).Select(number => new QuickTagSuggestion(Name("tag" + number), null, 100 - number, QuickTagReason.Frequent, 1)).ToArray();
        });
        return (surface, session, limits);
    }

    private static PrivateTagName Name(string text)
    {
        Assert.True(PrivateTagName.TryCreate(text, out PrivateTagName? name, out _));
        return name!;
    }

    [Fact]
    public void WithoutAHeightTheStripKeepsTheConfiguredCountAndTheRankingIsAskedForNoMore()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        PrivateTagStore store = new(temp.Path, lease);
        (QuickTagSurface popover, QuickTagSession session, List<int> popoverLimits) = Surface(store, fill: false);
        using (session)
        using (popover)
        {
            Assert.Equal(QuickTagOptions.DefaultSuggestionCount, popover.Shown.Count);
            Assert.All(popoverLimits, limit => Assert.Equal(QuickTagOptions.DefaultSuggestionCount, limit));
        }
        // The gallery panel before it was laid out (or in a unit test): the same count, but the ranking is asked for the panel limit.
        (QuickTagSurface panel, QuickTagSession panelSession, List<int> panelLimits) = Surface(store, fill: true, panelLimit: 40);
        using (panelSession)
        using (panel)
        {
            Assert.Equal(QuickTagOptions.DefaultSuggestionCount, panel.Shown.Count);
            Assert.Equal(panel.Shown.Count, panel.SuggestionButtons.Count); // controls only for what is shown, not for all 40 ranked
            Assert.All(panelLimits, limit => Assert.Equal(40, limit));
        }
    }

    [Fact]
    public void OnlyTheFirstNineChipsHaveANumberAndTheLimitsAreClamped()
    {
        Assert.Equal(1, QuickTagBadges.Number(0));
        Assert.Equal(9, QuickTagBadges.Number(8));
        Assert.Null(QuickTagBadges.Number(9));
        Assert.Null(QuickTagKeys.SuggestionGesture(9, ShortcutPlatform.Linux));
        QuickTagOptions low = new QuickTagOptions { PanelSuggestionLimit = -3 }.Normalized(), high = new QuickTagOptions { PanelSuggestionLimit = 99999 }.Normalized();
        Assert.Equal((QuickTagOptions.MinimumPanelSuggestionLimit, QuickTagOptions.MaximumPanelSuggestionLimit), (low.PanelSuggestionLimit, high.PanelSuggestionLimit));
        Assert.Equal(QuickTagOptions.DefaultPanelSuggestionLimit, QuickTagSettings.SurfaceOptions(new Config()).PanelSuggestionLimit);
        Assert.Equal(75, QuickTagSettings.SurfaceOptions(new Config { QuickTagPanelSuggestionLimit = 75 }).PanelSuggestionLimit);
    }

    [Fact]
    public void AssignedChipsSitAboveTheSuggestionsAndMoreIsTheLastThingOfTheSuggestionArea()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        PrivateTagStore store = new(temp.Path, lease);
        (QuickTagSurface surface, QuickTagSession session, _) = Surface(store, fill: true);
        using (session)
        using (surface)
        {
            Grid layout = Assert.IsType<Grid>(surface.Content);
            Control Child(string suffix) => layout.Children.OfType<Control>().Single(child => child.Name == "QuickTag" + suffix);
            int Row(string suffix) => Grid.GetRow(Child(suffix));
            Assert.True(Row("Field") < Row("Body"), "the assigned block follows the field");
            Assert.True(Row("Body") < Row("SuggestionArea"), "assigned tags come before the suggestions");
            Assert.True(Row("SuggestionArea") < Row("Status") && Row("Status") < Row("Actions"));
            Assert.Equal(GridUnitType.Star, layout.RowDefinitions[Row("SuggestionArea")].Height.GridUnitType); // the suggestions take what is left
            Assert.Equal(GridUnitType.Auto, layout.RowDefinitions[Row("Body")].Height.GridUnitType);
            Grid area = Assert.IsType<Grid>(Child("SuggestionArea"));
            Assert.Equal(2, Grid.GetRow(surface.MoreButton));
            Assert.Equal(GridUnitType.Star, area.RowDefinitions[1].Height.GridUnitType);
            Assert.Equal(Avalonia.Layout.VerticalAlignment.Bottom, surface.MoreButton.VerticalAlignment);
        }
    }
}
