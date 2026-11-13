using System.Net;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.Plugins;
using MeowChan.Downloader;
using MeowChan.Http;
using MeowChan.Interfaces;
using MeowChan.PluginSdk;

namespace MeowChan.DesktopTests;

[Collection("Localization")]
public sealed class SourcesTests
{
    [Fact]
    public void FailureAndCooldownBelongToTheWatchWithoutInventingProviderOnlineStatus()
    {
        Texts.Initialize(); Texts.SetLanguage("en");
        DesktopThreadEntry thread = new DesktopThreadEntry(42, new ThreadSnapshot(new ResourceId("4chan", "g", "123"),
            "https://boards.4chan.org/g/thread/123", "Thread", "/media/123", ThreadActivity.Error, ThreadSourceState.NotFound,
            new RequestPolicyProgress(RequestPolicyPhase.Cooldown, TimeSpan.FromSeconds(25), 2), 3, 0,
            new Dictionary<string, FailureInfo> { ["image"] = new FailureInfo(FailureKind.Storage, "Disk full") },
            new FailureInfo(FailureKind.Http, "Thread missing", HttpStatusCode.NotFound), new DownloadCounts(2, 1)), "4chan", "g");
        DesktopSessionView view = new DesktopSessionView([thread], [], [], DesktopSessionState.Ready, null);
        IReadOnlyList<SourceRow> rows = SourcesSnapshot.From([Provider("4chan"), Provider("8kun")], view);
        Assert.Contains("Threads: 1", rows[0].Summary);
        Assert.Contains("Queued: 2", rows[0].Summary);
        SourceWatchRow watch = Assert.Single(rows[0].Watches);
        Assert.Equal(new DiagnosticTarget(DiagnosticTargetKind.Thread, 42), watch.Target);
        Assert.Contains("25", watch.Observation);
        Assert.Contains("Thread missing", watch.Observation);
        Assert.Contains("Disk full", watch.Observation);
        Assert.Equal("No active observation", rows[1].Summary);
        Assert.Empty(rows[1].Watches);
        Assert.DoesNotContain("offline", rows[0].Summary, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("online", rows[1].Summary, StringComparison.OrdinalIgnoreCase);
        Assert.False(DiagnosticsSnapshot.ContainsTarget(view with { Threads = [] }, watch.Target));
    }

    [Fact]
    public void EmptyAndStoppedViewsStayHonestAndTranslationsKeepWatchTargetsStable()
    {
        Texts.Initialize(); Texts.SetLanguage("de");
        DesktopSessionView empty = new DesktopSessionView([], [], [], DesktopSessionState.Ready, null);
        Assert.Empty(SourcesSnapshot.From([], empty));
        Assert.Equal("Keine aktive Beobachtung", Assert.Single(SourcesSnapshot.From([Provider("4chan")], empty)).Summary);
        Assert.Equal("Sitzung gestoppt oder wird beendet", Assert.Single(SourcesSnapshot.From([Provider("4chan")],
            empty with { State = DesktopSessionState.Stopped })).Summary);
        Texts.SetLanguage("en");
    }

    [Fact]
    public void SourcePolicyProjectionSelectsActualOwnerAndFailsClosedForAmbiguity()
    {
        SourceMediaIntegrityPolicyView old = new(new("profile", "old-owner", "source", false, 4), true, false, SourceMaturity.Experimental);
        SourceMediaIntegrityPolicyView current = new(new("profile", "new-owner", "source", false, 1), false, true, SourceMaturity.Unspecified);
        Assert.Same(current, SourcesSnapshot.FindPolicy("source", [old, current]));
        Assert.Null(SourcesSnapshot.FindPolicy("other-source", [current]));
        Assert.Null(SourcesSnapshot.FindPolicy("source", [old, current with { IsActive = false }]));
        Assert.Null(SourcesSnapshot.FindPolicy("source", [old with { IsActive = true }, current]));
        Assert.Same(old, SourcesSnapshot.FindPolicy("source", [old]));
    }

    [Fact]
    public void SourcesAreSortedByMaturityThenAlphabeticallyAndEveryRowCarriesItsBadges()
    {
        Texts.Initialize(); Texts.SetLanguage("en");
        DesktopSessionView empty = new DesktopSessionView([], [], [], DesktopSessionState.Ready, null);
        SourceMediaIntegrityPolicyView Policy(string id, SourceWordingMaintenance maintenance, SourceWordingMaturity maturity, bool active = true) =>
            new(new("profile", "owner." + id, id, false, 1), false, active, SourceMaturity.Unspecified,
                new SourceWordingClassification(id, id, maintenance, maturity));
        ProviderDescriptor[] providers = [Provider("zeta"), Provider("Beta"), Provider("alpha"), Provider("omega"), Provider("gamma")];
        IReadOnlyList<SourceRow> rows = SourcesSnapshot.From(providers, empty,
        [
            Policy("zeta", SourceWordingMaintenance.MeowChan, SourceWordingMaturity.Stable),
            Policy("Beta", SourceWordingMaintenance.MeowChan, SourceWordingMaturity.Experimental),
            Policy("alpha", SourceWordingMaintenance.MeowChan, SourceWordingMaturity.Experimental),
            Policy("omega", SourceWordingMaintenance.Unknown, SourceWordingMaturity.Unclassified, active: false)
            // "gamma" has no policy at all: unclassified, built in.
        ]);
        // Stable, then unclassified (alphabetical), then experimental (alphabetical); case never splits a group.
        Assert.Equal(["zeta", "gamma", "omega", "alpha", "Beta"], rows.Select(row => row.Id));
        Assert.Equal(["Plugin", "MeowChan", "Stable", "Active"], rows[0].Badges.Select(badge => badge.Value));
        Assert.Empty(rows[0].Badges.Where(badge => badge.Deviates));
        Assert.Equal("Built-in", rows[1].Badges[0].Value);
        Assert.Equal(["Maintenance unverified", "Not activated"], rows[2].Badges.Where(badge => badge.Deviates).Select(badge => badge.ChipText));
        Assert.True(rows[3].Badges.Single(badge => badge.Kind == SourceBadgeKind.Maturity).Warning);
    }

    private static ProviderDescriptor Provider(string id) => new ProviderDescriptor(id, id,
        new Uri("https://" + (id == "4chan" ? "boards.4chan.org" : "8kun.top")),
        ProviderCapabilities.ThreadWatch | ProviderCapabilities.BoardCatalog, Array.Empty<string>(), null, false);
}
