using System.Net;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;
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
        Assert.Contains("1 threads", rows[0].Summary);
        Assert.Contains("2 queued", rows[0].Summary);
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

    private static ProviderDescriptor Provider(string id) => new ProviderDescriptor(id, id,
        new Uri("https://" + (id == "4chan" ? "boards.4chan.org" : "8kun.top")),
        ProviderCapabilities.ThreadWatch | ProviderCapabilities.BoardCatalog, Array.Empty<string>(), null, false);
}
