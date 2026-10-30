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

    [Fact]
    public void ScopeDeduplicatesWatchFlagsWithoutMixingStorageBoardMediaOrDisplayLabels()
    {
        DiagnosticEntry check = Problem("check", 1, DiagnosticKind.Check);
        DiagnosticEntry media = Problem("media", 1, DiagnosticKind.Media);
        DiagnosticEntry capture = Problem("capture", 2, DiagnosticKind.Media) with { CheckAlso = true };
        DiagnosticEntry board = Problem("board", 1, DiagnosticKind.Check) with
        { Target = new(DiagnosticTargetKind.Board, 1), ResourceId = new("fixture", "b") };
        DiagnosticEntry storage = Problem("storage", 3, DiagnosticKind.Storage) with { DownloadTargetPath = "/fixture/storage" };
        DiagnosticRetryTarget[] scope = DiagnosticRetryScope.Capture(new[] { check, media, media, capture, board, storage,
            Problem("request", 4, DiagnosticKind.Request), Problem("pending", 5, DiagnosticKind.PendingWork) });
        Assert.Equal(3, scope.Length);
        Assert.Equal(new[] { true, true, true }, scope.Select(item => item.Check));
        Assert.Equal(new[] { true, true, false }, scope.Select(item => item.Media));
        Assert.Equal(board.Target, scope[2].Target);
        Assert.Equal(check.ResourceId, scope[0].ResourceId);
        Assert.Empty(DiagnosticRetryScope.Capture(new[] { storage }));
        Assert.Empty(DiagnosticRetryScope.Capture(new[] { media with { Target = board.Target } }));
        Assert.Empty(DiagnosticRetryScope.Capture(new[] { check with { ResourceId = null } }));
        Assert.Empty(DiagnosticRetryScope.Capture(new[] { check, media with { ResourceId = new("other", "b", "1") } }));
    }

    [Theory]
    [InlineData("en")]
    [InlineData("de")]
    public void FiltersUseExactSourceIdentityAndSearchThreadEvenWhenTitlesAreIdentical(string language)
    {
        string previous = Texts.Language;
        try
        {
            Texts.SetLanguage(language);
            DiagnosticEntry first = Problem("first", 1, DiagnosticKind.Media);
            DiagnosticEntry second = Problem("second", 2, DiagnosticKind.Media) with { ResourceId = new("fixture-other", "b", "2") };
            Assert.True(DiagnosticRetryScope.Matches(first, "fixture", " #1 ", true));
            Assert.False(DiagnosticRetryScope.Matches(second, "fixture", null, false));
            Assert.False(DiagnosticRetryScope.Matches(first, "FIXTURE", null, false));
            Assert.True(DiagnosticRetryScope.Matches(second, null, "fixture-other /b/ #2", true));
            Assert.False(DiagnosticRetryScope.Matches(first with { Kind = DiagnosticKind.PendingWork }, null, "", true));
            Assert.True(DiagnosticRetryScope.Matches(first with { Kind = DiagnosticKind.PendingWork }, null, "", false));
            Assert.False(DiagnosticRetryScope.Matches(first, null, "no match", false));
        }
        finally { Texts.SetLanguage(previous); }
    }

    [Fact]
    public async Task ExecutorOwnsScopeBeforeAwaitAndRechecksBothOperationsInOrder()
    {
        DiagnosticRetryTarget[] frozen = DiagnosticRetryScope.Capture(new[] {
            Problem("media", 1, DiagnosticKind.Media) with { CheckAlso = true }, Problem("check", 2, DiagnosticKind.Check) });
        TaskCompletionSource<int> held = new(TaskCreationOptions.RunContinuationsAsynchronously);
        List<string> calls = new();
        HashSet<long> live = new() { 1, 2 };
        Task<DiagnosticRetryResult> running = DiagnosticRetryScope.ExecuteAsync(frozen,
            (item, _) => live.Contains(item.Target.WatchId),
            target => { calls.Add("check:" + target.WatchId); return Task.FromResult(CheckRequestStatus.Scheduled); },
            target => { calls.Add("media:" + target.WatchId); return held.Task; });
        Assert.Equal(new[] { "media:1" }, calls);
        // A filter/selection change or newly appearing problem cannot redirect the owned array.
        frozen[1] = new(new(DiagnosticTargetKind.Thread, 999), true, false) { ResourceId = new("new", "b", "999") };
        live.Remove(1); live.Add(999);
        held.SetResult(3);
        DiagnosticRetryResult result = await running;
        Assert.Equal(new[] { "media:1", "check:2" }, calls);
        Assert.Equal((2, 1, 3, 1, 0), (result.Targets, result.Checks, result.Files, result.Skipped, result.Failures));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LosingAvailabilityOrLifetimeDuringAwaitSkipsEveryRemainingOperation(bool ended)
    {
        DiagnosticRetryTarget[] scope = DiagnosticRetryScope.Capture(new[] {
            Problem("first", 1, DiagnosticKind.Media) with { CheckAlso = true }, Problem("second", 2, DiagnosticKind.Check) });
        bool available = true, alive = true;
        int calls = 0;
        TaskCompletionSource<int> held = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<DiagnosticRetryResult> running = DiagnosticRetryScope.ExecuteAsync(scope, (_, _) => available && alive,
            _ => { calls++; return Task.FromResult(CheckRequestStatus.Scheduled); },
            _ => { calls++; return held.Task; });
        if (ended) alive = false; else available = false;
        held.SetResult(1);
        DiagnosticRetryResult result = await running;
        Assert.Equal(1, calls);
        Assert.Equal(1, result.Files);
        Assert.Equal(2, result.Skipped);
        Assert.Equal(0, result.Failures);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task FirstOrMiddleFailureRetainsHonestPartialResultsAndContinuesOnlyFrozenTargets(int failingWatch)
    {
        DiagnosticRetryTarget[] scope = DiagnosticRetryScope.Capture(Enumerable.Range(1, 3).Select(id => Problem("p" + id, id, DiagnosticKind.Check)));
        List<long> calls = new();
        DiagnosticRetryResult result = await DiagnosticRetryScope.ExecuteAsync(scope, (_, _) => true, target =>
        {
            calls.Add(target.WatchId);
            return target.WatchId == failingWatch ? Task.FromException<CheckRequestStatus>(new IOException("failure " + target.WatchId))
                : Task.FromResult(target.WatchId == 3 ? CheckRequestStatus.AlreadyScheduled : CheckRequestStatus.Scheduled);
        }, null);
        Assert.Equal(new long[] { 1, 2, 3 }, calls);
        Assert.Equal(1, result.Checks);
        Assert.Equal(1, result.AlreadyScheduled);
        Assert.Equal(1, result.Failures);
        Assert.Contains("#" + failingWatch, Assert.Single(result.Errors));
    }

    [Fact]
    public async Task CancellationStopsIntentAndAccountsUnsubmittedChecksAndMedia()
    {
        DiagnosticRetryTarget[] scope = DiagnosticRetryScope.Capture(new[] {
            Problem("first", 1, DiagnosticKind.Media) with { CheckAlso = true }, Problem("second", 2, DiagnosticKind.Media) with { CheckAlso = true } });
        int checks = 0, media = 0;
        DiagnosticRetryResult result = await DiagnosticRetryScope.ExecuteAsync(scope, (_, _) => true,
            _ => { checks++; return Task.FromResult(CheckRequestStatus.Scheduled); },
            _ => { media++; return Task.FromException<int>(new OperationCanceledException("canceled fixture")); });
        Assert.Equal(0, checks); Assert.Equal(1, media);
        Assert.Equal(1, result.Failures); Assert.Equal(3, result.Skipped);
    }

    [Fact]
    public async Task CooldownDisabledPausedAndStaleAdmissionNeverBecomeSuccessfulRequests()
    {
        DiagnosticRetryTarget[] scope = DiagnosticRetryScope.Capture(Enumerable.Range(1, 4).Select(id => Problem("p" + id, id, DiagnosticKind.Check)));
        List<long> calls = new();
        DiagnosticRetryResult result = await DiagnosticRetryScope.ExecuteAsync(scope, (item, _) => item.Target.WatchId > 2,
            target => { calls.Add(target.WatchId); return Task.FromResult(CheckRequestStatus.Stale); }, null);
        Assert.Equal(new long[] { 3, 4 }, calls);
        Assert.Equal(4, result.Skipped); Assert.Equal(0, result.Checks); Assert.Equal(0, result.Failures);
        DiagnosticRetryResult empty = await DiagnosticRetryScope.ExecuteAsync(Array.Empty<DiagnosticRetryTarget>(), (_, _) => throw new Exception(), null, null);
        Assert.Equal(0, empty.Targets); Assert.Empty(empty.Errors);
    }

    [Theory]
    [InlineData(CheckRequestStatus.Paused)]
    [InlineData(CheckRequestStatus.Stale)]
    [InlineData(CheckRequestStatus.Unsupported)]
    [InlineData(CheckRequestStatus.Stopping)]
    public async Task AuthoritativeHostRejectionIsASkippedActionNotAQueuedCheck(CheckRequestStatus status)
    {
        DiagnosticRetryResult result = await DiagnosticRetryScope.ExecuteAsync(
            DiagnosticRetryScope.Capture(new[] { Problem("p", 1, DiagnosticKind.Check) }),
            (_, _) => true, _ => Task.FromResult(status), null);
        Assert.Equal(1, result.Skipped);
        Assert.Equal(0, result.Checks);
        Assert.Equal(0, result.AlreadyScheduled);
        Assert.Equal(0, result.Failures);
    }

    private static DiagnosticEntry Problem(string key, long watch, DiagnosticKind kind) => new(key, "same category", "same title", "same message", key,
        new(DiagnosticTargetKind.Thread, watch)) { Kind = kind, ResourceId = new("fixture", "b", watch.ToString()) };

    private static DesktopSessionView View(IReadOnlyList<DesktopThreadEntry> threads, IReadOnlyList<DesktopBoardEntry>? boards = null) =>
        new DesktopSessionView(threads, boards ?? Array.Empty<DesktopBoardEntry>(), Array.Empty<string>(), DesktopSessionState.Ready, null);
    private static DesktopThreadEntry Thread(long watch) => new DesktopThreadEntry(watch,
        new ThreadSnapshot(new ResourceId("4chan", "g", "123"), "https://boards.4chan.org/g/thread/123", "Thread", "/media/g/123",
            ThreadActivity.Pending, ThreadSourceState.Active, null, 2, 0, new Dictionary<string, FailureInfo>(), null, new DownloadCounts(0, 0)), "4chan", "g");
}
