using MeowChan.Desktop.Localization;
using MeowChan.Desktop.Plugins;
using MeowChan.PluginHost.Activation;
using MeowChan.PluginHost.Compilation;
using MeowChan.PluginHost.Contracts;
using MeowChan.PluginSdk;

namespace MeowChan.DesktopTests;

public sealed class PluginCardOrderTests
{
    private static PluginCardRank Card(string key, string name, PluginAvailability availability, bool running = false) =>
        new(key, name, availability, running);

    private static string[] Keys(IEnumerable<PluginCardRank> cards) => PluginCardOrder.Order(cards).Select(card => card.Key).ToArray();

    [Fact]
    public void RunningFirstThenStatusThenName()
    {
        PluginCardRank[] cards =
        [
            Card("notloaded-a", "Aaa", PluginAvailability.NotLoaded),
            Card("unavailable-a", "Aaa", PluginAvailability.Unavailable),
            Card("disabled-a", "Aaa", PluginAvailability.Disabled),
            Card("active-z", "Zzz", PluginAvailability.Active),
            Card("active-b", "Bbb", PluginAvailability.Active),
            Card("running-disabled-q", "Qqq", PluginAvailability.Disabled, running: true),
            Card("running-active-m", "Mmm", PluginAvailability.Active, running: true),
            Card("running-active-c", "Ccc", PluginAvailability.Active, running: true),
        ];

        Assert.Equal(["running-active-c", "running-active-m", "running-disabled-q",
            "active-b", "active-z", "disabled-a", "unavailable-a", "notloaded-a"], Keys(cards));
    }

    [Fact]
    public void NameIgnoresCaseAndTiesFallBackToTheKey()
    {
        PluginCardRank[] cards =
        [
            Card("k3", "same", PluginAvailability.Active),
            Card("k1", "Same", PluginAvailability.Active),
            Card("k2", "SAME", PluginAvailability.Active),
            Card("k0", "alpha", PluginAvailability.Active),
        ];

        string[] order = Keys(cards);

        Assert.Equal("k0", order[0]);
        // Same name ignoring case: exact name decides (ordinal, upper before lower), identical names fall to the key.
        Assert.Equal(["k0", "k2", "k1", "k3"], order);
    }

    [Fact]
    public void OrderIsStableAndIndependentOfInputOrder()
    {
        PluginCardRank[] cards =
        [
            Card("a", "Same", PluginAvailability.Active), Card("b", "Same", PluginAvailability.Active),
            Card("c", "Same", PluginAvailability.Active, true), Card("d", "Other", PluginAvailability.Disabled),
            Card("e", "Other", PluginAvailability.Disabled), Card("f", "x", PluginAvailability.NotLoaded),
        ];
        string[] expected = Keys(cards);
        Assert.Equal(expected, Keys(cards));
        Random random = new(7);
        for (int round = 0; round < 25; round++)
            Assert.Equal(expected, Keys(cards.OrderBy(_ => random.Next()).ToArray()));
        Assert.Equal(expected, Keys(cards.Reverse()));
    }

    [Fact]
    public void ActivityHoldKeepsABriefIdleGapInTheRunningGroup()
    {
        PluginActivityTracker tracker = new();
        DateTimeOffset start = new(2026, 11, 17, 12, 0, 0, TimeSpan.Zero);

        Assert.False(tracker.Observe("p", false, start));
        Assert.True(tracker.Observe("p", true, start.AddSeconds(1)));
        // Idle again, but within the hold: no flicker in the order.
        Assert.True(tracker.Observe("p", false, start.AddSeconds(3)));
        Assert.True(tracker.Observe("p", false, start.AddSeconds(1) + PluginActivityTracker.Hold - TimeSpan.FromMilliseconds(1)));
        Assert.False(tracker.Observe("p", false, start.AddSeconds(1) + PluginActivityTracker.Hold));
        Assert.False(tracker.Observe("never", false, start.AddHours(1)));
    }

    [Fact]
    public void ActivityHoldFollowsThePluginWhenItsRepresentativeFileChanges()
    {
        PluginActivityTracker tracker = new();
        DateTimeOffset start = new(2026, 11, 17, 12, 0, 0, TimeSpan.Zero);

        Assert.True(tracker.Observe(PluginActivityTracker.ActivityKey("plugin-id", "candidate-a"), true, start));
        // Selecting another candidate changes the card key, but not the plugin identity or its held activity.
        Assert.True(tracker.Observe(PluginActivityTracker.ActivityKey("plugin-id", "candidate-b"), false, start.AddSeconds(1)));
        Assert.True(tracker.Observe(PluginActivityTracker.ActivityKey(null, "standalone"), true, start));
        Assert.Equal("standalone", PluginActivityTracker.ActivityKey(null, "standalone"));
    }

    [Fact]
    public void OnlyRequestStatesCountAsBusy()
    {
        Assert.True(PluginActivityTracker.IsBusyState("running"));
        Assert.True(PluginActivityTracker.IsBusyState("waiting-for-call"));
        foreach (string state in new[] { "ready", "idle", "starting", "stopped", "unavailable", "waiting-for-worker", "restart-delay", "intervention-required" })
            Assert.False(PluginActivityTracker.IsBusyState(state));
        Assert.False(PluginActivityTracker.IsBusyState(null));
    }

    [Theory]
    [InlineData(false, true, true)] // Disabled, with a retained active release.
    [InlineData(false, false, true)] // Disabled, without an active release.
    [InlineData(true, false, true)] // Unavailable.
    [InlineData(true, false, false)] // Not loaded / unused candidate.
    public void DisabledAndUnusedPluginsShowFourZeroStatsWithTheSameIdsAsEveryCard(bool enabled, bool active, bool loaded)
    {
        PluginCatalogEntry entry = Entry(enabled: enabled, active: active, description: Description(source: true, filter: true));
        PluginEntryPresentation view = PluginEntryPresentation.Create(loaded ? entry : entry with { Activation = null });
        Assert.Single(view.Sources);
        Assert.Single(view.Filters);

        // Even non-zero metadata, candidate files and a stale busy observation must not become live usage.
        IReadOnlyList<MeowChan.Desktop.Controls.StatFooterItem> items = PluginCardStats.Items(view, files: 3, busyNow: true);

        Assert.Equal(["sources", "filters", "running", "files"], items.Select(item => item.Id));
        Assert.All(items, item => Assert.Equal(0, item.Value));
        Assert.All(items, item => Assert.False(string.IsNullOrEmpty(item.Label)));
        Assert.Equal(4, items.Select(item => item.Priority).Distinct().Count());
        // Unknown presentation (nothing observed yet) is also all zeros, never a missing row.
        Assert.All(PluginCardStats.Items(null, 3, true), item => Assert.Equal(0, item.Value));
    }

    [Fact]
    public void ActiveCardCountsEffectiveSourcesFiltersFilesAndActivityUsingTheSameFooterShape()
    {
        // The candidate may describe a different build than the release currently in use.
        PluginEntryPresentation view = PluginEntryPresentation.Create(Entry(active: true,
            description: Description(), activeDescription: Description(source: true, filter: true)));
        Assert.Empty(view.Sources);
        Assert.Empty(view.Filters);

        IReadOnlyList<MeowChan.Desktop.Controls.StatFooterItem> idle = PluginCardStats.Items(view, files: 2, busyNow: false);
        IReadOnlyList<MeowChan.Desktop.Controls.StatFooterItem> busy = PluginCardStats.Items(view, files: 2, busyNow: true);

        Assert.Equal(["sources", "filters", "running", "files"], idle.Select(item => item.Id));
        Assert.Equal([1L, 1L, 0L, 2L], idle.Select(item => item.Value));
        Assert.Equal([1L, 1L, 1L, 2L], busy.Select(item => item.Value));
        Assert.Equal(0, PluginCardStats.Items(view, files: -1, busyNow: false).Single(item => item.Id == "files").Value);
        Assert.Equal(idle.Select(item => item.Icon), busy.Select(item => item.Icon));
    }

    [Fact]
    public void StatLabelsExistInBothLanguages()
    {
        foreach (string key in new[] { "plugins.stat.sources", "plugins.stat.filters", "plugins.stat.running", "plugins.stat.files" })
        {
            string text = Texts.T(key);
            Assert.False(string.IsNullOrWhiteSpace(text));
            Assert.NotEqual(key, text);
        }
    }

    private const string PluginId = "fixture.card-order";
    private const string CandidateId = "candidate-card";

    private static PluginDescription Description(bool source = false, bool filter = false) =>
        new(PluginId, "Card order", "1.0.0", SdkContract.Version, "Fixture.",
            source ? [new SourceDescription("fixture-source", "Fixture source", new Uri("https://fixture.invalid/"),
                SourceCapabilities.ThreadRead, [], [], new("1", []))] : [],
            filter ? [new FilterDescription("fixture-filter", "Fixture filter", "Checks.", FilterDataAccess.OriginalImage, new("1", []), ["image/jpeg"])] : [],
            new("1", []), []);

    private static PluginCatalogEntry Entry(bool enabled = true, bool active = true, PluginDescription? description = null,
        PluginDescription? activeDescription = null)
    {
        PluginArtifact artifact = new("compile", "exec", "/fixture/plugin.dll", "assembly-hash", "sdk-hash", "compiler-hash", []);
        PluginReleaseSnapshot? release = active
            ? new(CandidateId, artifact, activeDescription ?? description ?? Description(), new("cfg", new("1", []), new Dictionary<string, SettingsSnapshot>()))
            : null;
        PluginCandidateSnapshot candidate = new(CandidateId, PluginId, artifact, description, true, null, "/fixture/plugin.cs");
        PluginActivationSnapshot state = new(PluginId, enabled, 1, CandidateId, release, null, null, null, [candidate], null);
        return new(CandidateId, PluginId, CandidateId, "/fixture/plugin.cs", description, state, candidate, Array.Empty<PluginBuildDiagnostic>());
    }
}
