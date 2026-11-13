using System.Reflection;
using Avalonia.Controls;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.Stats;

namespace MeowChan.DesktopTests;

/// <summary>
/// The tray's use of the statistics hub: when it holds its claim, that nothing runs while hidden and idle, that the menu lines are
/// filled when the menu opens and that a click opens the statistics page. The hub and its timers are driven by a manual clock.
/// </summary>
[Collection("Localization")]
public sealed class TrayStatsPresenterTests : IDisposable
{
    private const long MiB = 1024 * 1024;
    private static readonly DateTimeOffset Noon = new(2026, 11, 13, 12, 0, 0, TimeSpan.Zero);
    private readonly string _previous = Texts.Language;

    public TrayStatsPresenterTests() { Texts.Initialize(); Texts.SetLanguage("en"); }
    public void Dispose() => Texts.SetLanguage(_previous);

    private sealed class Harness : IDisposable
    {
        internal readonly IoManualClock Clock = new(Noon);
        internal readonly IoFakeSource Source = new();
        internal readonly IoStatsHub Hub;
        internal readonly IDisposable Attachment;
        internal readonly NativeMenu Menu = new();
        internal readonly TrayStatsPresenter Presenter;
        internal bool Shown = true, TooltipOn = true, MenuOn = true, Closing, Paused;
        internal int Active, Opened;
        internal string? Tooltip;
        internal int TooltipSets;

        internal Harness(bool shown = true, bool tooltipOn = true, bool menuOn = true, int backgroundMs = 5000)
        {
            Shown = shown; TooltipOn = tooltipOn; MenuOn = menuOn;
            Hub = new IoStatsHub(Source, Clock, new IoStatsOptions { Persist = false, BackgroundIntervalMs = backgroundMs, TrayTooltip = tooltipOn });
            Attachment = Hub.Attach(null, () => new IoActivity(Active, Active > 0 ? 4 : 0, 0));
            // The existing tray entries stay behind the three new ones.
            Menu.Items.Add(new NativeMenuItem { Header = "Open" });
            Presenter = new TrayStatsPresenter(Hub, Menu, new TrayStatsHost(
                () => TooltipOn, () => MenuOn, () => Shown, () => Closing, () => (Paused, Paused ? 0 : Active),
                tip => { Tooltip = tip; TooltipSets++; }, () => Opened++, action => action()), Clock);
        }

        internal void Activity(int active) { Active = active; Hub.NotifyActivityChanged(); }
        internal void OpenMenu() => Invoke(Menu, "INativeMenuExporterEventsImplBridge.RaiseNeedsUpdate");
        internal void Click(NativeMenuItem item) => Invoke(item, "INativeMenuItemExporterEventsImplBridge.RaiseClicked");

        private static void Invoke(object target, string suffix)
        {
            MethodInfo method = target.GetType().GetMethods(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
                .Single(candidate => candidate.Name.EndsWith(suffix, StringComparison.Ordinal));
            method.Invoke(target, null);
        }

        public void Dispose() { Presenter.Dispose(); Attachment.Dispose(); Hub.Dispose(); }
    }

    // --- the claim -----------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(false, true, false, true)]   // hidden, tooltip on: claim
    [InlineData(false, false, false, false)] // hidden, tooltip off: none
    [InlineData(true, true, false, false)]   // visible: the window samples for itself
    [InlineData(true, false, false, false)]
    [InlineData(false, true, true, false)]   // closing: none
    public void TheClaimIsWantedOnlyWhileHiddenWithTheTooltipOnAndNotClosing(bool shown, bool tooltipOn, bool closing, bool wanted) =>
        Assert.Equal(wanted, TrayStatsClaim.Wanted(closing, shown, tooltipOn));

    [Fact]
    public void AVisibleWindowHoldsNoClaimAndStartsNoTimerEvenWithDownloadsRunning()
    {
        using Harness h = new(shown: true);
        h.Activity(3);
        h.Presenter.Refresh();
        Assert.False(h.Presenter.ClaimHeld);
        Assert.Equal(0, h.Hub.ActiveTimerCount);
        Assert.Equal(0, h.Clock.ActiveTimers);
        Assert.Equal(IoStatsVisibility.None, h.Hub.SamplingMode);
    }

    [Fact]
    public void WithTheTooltipSettingOffHiddenHoldsNoClaimAndNothingRuns()
    {
        using Harness h = new(shown: false, tooltipOn: false);
        h.Activity(3);
        h.Presenter.Refresh();
        Assert.False(h.Presenter.ClaimHeld);
        Assert.Equal(0, h.Hub.ActiveTimerCount);
        Assert.Equal("MeowChan · Active downloads: 3", h.Tooltip);
    }

    [Fact]
    public void HiddenWithTheTooltipOnHoldsTheClaimAndHidingAndShowingMovesIt()
    {
        using Harness h = new(shown: true);
        h.Presenter.Refresh();
        Assert.False(h.Presenter.ClaimHeld);
        h.Shown = false;
        h.Presenter.Refresh();
        Assert.True(h.Presenter.ClaimHeld);
        h.Shown = true;
        h.Presenter.Refresh();
        Assert.False(h.Presenter.ClaimHeld);
        h.Shown = false;
        h.Presenter.Refresh();
        h.TooltipOn = false; // switched off in the settings while hidden
        h.Presenter.Refresh();
        Assert.False(h.Presenter.ClaimHeld);
        h.TooltipOn = true;
        h.Closing = true;
        h.Presenter.Refresh();
        Assert.False(h.Presenter.ClaimHeld);
    }

    [Fact]
    public void HiddenAndIdleRunsNoTimerEvenWhileTheClaimIsHeld()
    {
        using Harness h = new(shown: false);
        h.Presenter.Refresh();
        Assert.True(h.Presenter.ClaimHeld);
        Assert.Equal(0, h.Hub.ActiveTimerCount);
        Assert.Equal(0, h.Clock.ActiveTimers);
        int reads = h.Source.Reads;
        h.Clock.Advance(TimeSpan.FromHours(6));
        Assert.Equal(reads, h.Source.Reads); // nothing woke up to read the counters
        Assert.Equal(0, h.Hub.ActiveTimerCount);
        // Opening the menu reads on demand and still starts nothing.
        h.Source.Add("4chan", net: 3 * MiB);
        h.OpenMenu();
        Assert.Equal(0, h.Hub.ActiveTimerCount);
        Assert.Equal(0, h.Clock.ActiveTimers);
        Assert.Equal("Today ↓ 3.0 MiB · Disk: 0 B written", h.Presenter.DetailItem.Header);
    }

    [Fact]
    public void HiddenWithDownloadsRunsOneSlowTimerAndTheTooltipFollowsEachSample()
    {
        using Harness h = new(shown: false, backgroundMs: 5000);
        h.Presenter.Refresh();
        h.Activity(3);
        Assert.Equal(1, h.Hub.ActiveTimerCount);
        Assert.Equal(IoStatsVisibility.Tray, h.Hub.SamplingMode);
        Assert.Equal(5000, h.Hub.SamplingIntervalMs);

        h.Source.Add("4chan", net: 10 * MiB, diskWritten: 5 * MiB);
        h.Clock.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal("MeowChan · ↓ 2.0 MiB/s · Disk 1.0 MiB/s · 3 downloads active", h.Tooltip);
        h.Source.Add("4chan", net: 25 * MiB, diskWritten: 25 * MiB);
        h.Clock.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal("MeowChan · ↓ 5.0 MiB/s · Disk 5.0 MiB/s · 3 downloads active", h.Tooltip);

        // The window comes back: the claim ends, the slow timer goes, and samples no longer reach the tooltip.
        h.Shown = true;
        h.Presenter.Refresh();
        Assert.False(h.Presenter.ClaimHeld);
        Assert.Equal(0, h.Hub.ActiveTimerCount);
        string last = h.Tooltip!;
        int sets = h.TooltipSets;
        h.Clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal((last, sets), (h.Tooltip, h.TooltipSets));
    }

    [Fact]
    public void TheTimerEndsWhenTheDownloadsEndAndTheTooltipFallsBackToTheState()
    {
        using Harness h = new(shown: false);
        h.Presenter.Refresh();
        h.Activity(2);
        h.Source.Add("4chan", net: 5 * MiB);
        h.Clock.Advance(TimeSpan.FromSeconds(5));
        h.Activity(0);
        Assert.Equal(0, h.Hub.ActiveTimerCount);
        h.Presenter.Refresh();
        Assert.Equal("MeowChan · idle · Today ↓ 5.0 MiB", h.Tooltip);
    }

    [Fact]
    public void DisposingTheMenuPresenterReleasesTheClaimTheTimerAndTheSampleHandler()
    {
        using Harness h = new(shown: false);
        h.Presenter.Refresh();
        h.Activity(2);
        Assert.Equal(1, h.Hub.ActiveTimerCount);
        h.Presenter.Dispose();
        Assert.False(h.Presenter.ClaimHeld);
        Assert.Equal(0, h.Hub.ActiveTimerCount);
        int sets = h.TooltipSets;
        h.Source.Add("4chan", net: MiB);
        h.Clock.Advance(TimeSpan.FromMinutes(1));
        h.OpenMenu();
        Assert.Equal(sets, h.TooltipSets);
    }

    // --- the tooltip while the window is visible ---------------------------------------------------------------------------

    [Fact]
    public void ThePlainTooltipRefreshIsThrottledToOneReadingPerSecondUnlessTheStateChanges()
    {
        using Harness h = new(shown: true);
        h.Presenter.Refresh();
        int reads = h.Source.Reads;
        for (int i = 0; i < 20; i++) h.Presenter.Refresh();
        Assert.Equal(reads, h.Source.Reads);
        h.Clock.Advance(TimeSpan.FromSeconds(1.1));
        h.Presenter.Refresh();
        Assert.True(h.Source.Reads > reads);
        reads = h.Source.Reads;
        h.Active = 2; // a state change is shown at once
        h.Presenter.Refresh();
        Assert.True(h.Source.Reads > reads);
        Assert.Equal("MeowChan · 2 downloads active", h.Tooltip!.Split(" · Today")[0]);
    }

    // --- the menu lines ---------------------------------------------------------------------------------------------------

    [Fact]
    public void TheMenuStartsWithTwoEnabledLinesAndASeparatorAndTheOldEntriesFollow()
    {
        using Harness h = new();
        Assert.Same(h.Presenter.RatesItem, h.Menu.Items[0]);
        Assert.Same(h.Presenter.DetailItem, h.Menu.Items[1]);
        Assert.Same(h.Presenter.Separator, h.Menu.Items[2]);
        Assert.Equal("Open", ((NativeMenuItem)h.Menu.Items[3]).Header);
        Assert.True(h.Presenter.RatesItem.IsEnabled);
        Assert.True(h.Presenter.DetailItem.IsEnabled);
        Assert.True(h.Presenter.RatesItem.IsVisible && h.Presenter.DetailItem.IsVisible && h.Presenter.Separator.IsVisible);
    }

    [Fact]
    public void TheLinesAreFilledWhenTheMenuOpensNotOnEverySample()
    {
        using Harness h = new(shown: false);
        h.Presenter.Refresh(); // first fill
        Assert.Equal("Idle", h.Presenter.RatesItem.Header);
        h.Activity(3);
        h.Source.Add("4chan", net: 10 * MiB, diskWritten: 5 * MiB);
        h.Clock.Advance(TimeSpan.FromSeconds(5));
        // The tooltip moved on with the sample; the menu lines stay as they were until the menu opens.
        Assert.Equal("Idle", h.Presenter.RatesItem.Header);
        h.OpenMenu();
        Assert.Equal("↓ 2.0 MiB/s · Disk 1.0 MiB/s", h.Presenter.RatesItem.Header);
        Assert.Equal("3 downloads active · 4 waiting · Today ↓ 10.0 MiB", h.Presenter.DetailItem.Header);
        Texts.SetLanguage("de");
        h.OpenMenu();
        Assert.Equal("↓ 2,0 MiB/s · Platte 1,0 MiB/s", h.Presenter.RatesItem.Header);
        Assert.Equal("3 Downloads aktiv · 4 wartend · Heute ↓ 10,0 MiB", h.Presenter.DetailItem.Header);
    }

    [Fact]
    public void OpeningTheMenuWhilePausedShowsPausedAndTodaysTotals()
    {
        using Harness h = new(shown: false) { Paused = true };
        h.Source.Add("4chan", net: 2 * MiB, diskWritten: MiB);
        h.OpenMenu();
        Assert.Equal("Paused", h.Presenter.RatesItem.Header);
        Assert.Equal("Today ↓ 2.0 MiB · Disk: 1.0 MiB written", h.Presenter.DetailItem.Header);
        Assert.Equal("MeowChan · paused · Today ↓ 2.0 MiB", h.Tooltip);
    }

    [Fact]
    public void TheMenuSettingHidesAndShowsTheLinesWithoutRestartAndTheTooltipSettingIsIndependent()
    {
        using Harness h = new(shown: false, menuOn: false, tooltipOn: false);
        Assert.False(h.Presenter.RatesItem.IsVisible);
        Assert.False(h.Presenter.Separator.IsVisible);
        h.Source.Add("4chan", net: MiB);
        h.OpenMenu();
        Assert.Null(h.Presenter.RatesItem.Header); // hidden lines are not computed
        Assert.Equal("MeowChan - Image Downloader", h.Tooltip);
        h.MenuOn = true;
        h.Presenter.Refresh();
        Assert.True(h.Presenter.RatesItem.IsVisible && h.Presenter.DetailItem.IsVisible && h.Presenter.Separator.IsVisible);
        Assert.Equal("Idle", h.Presenter.RatesItem.Header);
        Assert.Equal("Today ↓ 1.0 MiB · Disk: 0 B written", h.Presenter.DetailItem.Header);
        Assert.Equal("MeowChan - Image Downloader", h.Tooltip); // tooltip setting still off: the legacy text
    }

    [Fact]
    public void ClickingEitherLineOpensTheStatisticsPage()
    {
        using Harness h = new();
        h.Click(h.Presenter.RatesItem);
        h.Click(h.Presenter.DetailItem);
        Assert.Equal(2, h.Opened);
        h.Presenter.Dispose();
        h.Click(h.Presenter.RatesItem);
        Assert.Equal(2, h.Opened);
    }

    [Fact]
    public void ClosingStopsTheMenuAndTheSamplesFromTouchingTheTray()
    {
        using Harness h = new(shown: false);
        h.Presenter.Refresh();
        h.Closing = true;
        int sets = h.TooltipSets;
        h.OpenMenu();
        Assert.Equal(sets, h.TooltipSets);
    }
}
