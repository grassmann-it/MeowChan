using System.Net;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;
using MeowChan.Downloader;
using MeowChan.Http;
using MeowChan.Interfaces;

namespace MeowChan.DesktopTests;

[Collection("Localization")]
public sealed class DiagnosticsTests
{
    [Fact]
    public void ProjectionIncludesHttpCooldownMediaStorageAndBoardErrorsWithoutCombiningTheirIdentity()
    {
        Texts.Initialize();
        Texts.SetLanguage("en");
        FailureInfo http = new FailureInfo(FailureKind.Http, "Rate limited", HttpStatusCode.TooManyRequests);
        DesktopThreadEntry thread = Thread(42) with
        {
            Snapshot = Thread(42).Snapshot with
            {
                LastCheckFailure = http,
                RequestProgress = new RequestPolicyProgress(RequestPolicyPhase.Cooldown, TimeSpan.FromSeconds(25), 2),
                Downloads = new DownloadCounts(7, 2),
                MediaFailures = new Dictionary<string, FailureInfo>
                {
                    ["image1"] = new FailureInfo(FailureKind.Storage, "Disk full"),
                    ["image2"] = new FailureInfo(FailureKind.Network, "Connection reset")
                }
            }
        };
        DesktopBoardEntry board = new DesktopBoardEntry(42, new BoardSnapshot(new ResourceId("4chan", "g"),
            "https://boards.4chan.org/g/", "g", "/media/g", null, new FailureInfo(FailureKind.InvalidData, "Malformed response")), "4chan", "g");
        DiagnosticsSnapshot diagnostics = DiagnosticsSnapshot.From(View(new[] { thread }, new[] { board }) with
        { Warnings = new[] { "Cannot save watches", "Cannot save watches" } });

        Assert.Equal(7, diagnostics.Queued);
        Assert.Equal(2, diagnostics.Active);
        Assert.Equal(6, diagnostics.Entries.Count);
        Assert.Equal(6, diagnostics.Entries.Select(entry => entry.Key).Distinct().Count());
        Assert.Contains(diagnostics.Entries, entry => entry.Details.Contains("HTTP 429") && entry.Target?.Kind == DiagnosticTargetKind.Thread);
        Assert.Contains(diagnostics.Entries, entry => entry.Details.Contains("Attempt 2") && entry.Summary.Contains("25"));
        Assert.Contains(diagnostics.Entries, entry => entry.Details.Contains("image1") && entry.Details.Contains("Storage"));
        Assert.Contains(diagnostics.Entries, entry => entry.Target == new DiagnosticTarget(DiagnosticTargetKind.Board, 42));
        Assert.Single(diagnostics.Entries, entry => entry.Target == null);
    }

    [Fact]
    public void RemovedWatchCannotTargetReaddedUrlOrBoardWithSameLifetimeNumber()
    {
        DesktopThreadEntry old = Thread(17);
        DesktopSessionView readded = View(new[] { Thread(18) });
        DiagnosticTarget target = new DiagnosticTarget(DiagnosticTargetKind.Thread, old.WatchId);
        Assert.Equal(old.Snapshot.Url, readded.Threads[0].Snapshot.Url);
        Assert.False(DiagnosticsSnapshot.ContainsTarget(readded, target));
        Assert.True(DiagnosticsSnapshot.ContainsTarget(readded, target with { WatchId = 18 }));
        Assert.False(DiagnosticsSnapshot.ContainsTarget(readded, target with { WatchId = 18, Kind = DiagnosticTargetKind.Board }));
    }

    [Fact]
    public void RecoveryErrorIsReadableAndQuietSessionDoesNotInventEvents()
    {
        DesktopSessionView quiet = View(Array.Empty<DesktopThreadEntry>());
        Assert.Empty(DiagnosticsSnapshot.From(quiet).Entries);
        DiagnosticsSnapshot failed = DiagnosticsSnapshot.From(quiet with
        { State = DesktopSessionState.RecoveryFailed, StartupError = "Backup is corrupt" });
        DiagnosticEntry entry = Assert.Single(failed.Entries);
        Assert.Null(entry.Target);
        Assert.Equal("Backup is corrupt", entry.Details);
    }

    private static DesktopSessionView View(IReadOnlyList<DesktopThreadEntry> threads, IReadOnlyList<DesktopBoardEntry>? boards = null) =>
        new DesktopSessionView(threads, boards ?? Array.Empty<DesktopBoardEntry>(), Array.Empty<string>(), DesktopSessionState.Ready, null);
    private static DesktopThreadEntry Thread(long watch) => new DesktopThreadEntry(watch,
        new ThreadSnapshot(new ResourceId("4chan", "g", "123"), "https://boards.4chan.org/g/thread/123", "Thread", "/media/g/123",
            ThreadActivity.Pending, ThreadSourceState.Active, null, 2, 0, new Dictionary<string, FailureInfo>(), null, new DownloadCounts(0, 0)), "4chan", "g");
}
