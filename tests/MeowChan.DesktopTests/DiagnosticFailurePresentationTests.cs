using System.Net;
using System.Text.Json;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;
using MeowChan.Downloader;
using MeowChan.Http;
using MeowChan.Interfaces;
using MeowChan.Persistence;
using MeowChan.PluginHost.Contracts;

namespace MeowChan.DesktopTests;

[Collection("Localization")]
public sealed class DiagnosticFailurePresentationTests : IDisposable
{
    private readonly string _previousLanguage = Texts.Language;
    private static readonly DateTimeOffset ReportedRetry = new(2031, 2, 3, 4, 5, 0, TimeSpan.Zero);
    private const string WatchUrl = "https://watch.invalid/b/thread/123?context=watch-only";
    private const string UnobservedEndpoint = "https://cdn.invalid/not-observed/file.bin";
    private static readonly ResourceId ThreadId = new("fixture", "b", "123");

    public DiagnosticFailurePresentationTests()
    {
        Texts.Initialize();
        Texts.SetLanguage("en");
    }

    public void Dispose()
    {
        Texts.Initialize();
        Texts.SetLanguage(_previousLanguage);
    }

    [Fact]
    public void RefusedRequestRetainsFactsWithoutDiagnosingBanLoginOrCaptcha()
    {
        FailureInfo failure = new(FailureKind.Http, "producer-detail", HttpStatusCode.Forbidden)
            { DiagnosticCode = "source-refused" };
        foreach (DiagnosticOperation operation in new[] { DiagnosticOperation.Thread, DiagnosticOperation.Board, DiagnosticOperation.Media })
        {
            string details = DiagnosticFailurePresentation.Format(failure, operation);
            Assert.Contains("The server refused this request (HTTP 403); this alone does not prove a ban or login problem.", details);
            Assert.Contains("Http · HTTP 403" + Environment.NewLine + failure.Message, details);
            Assert.Contains(Texts.T("diagnostics.observed.code", "source-refused"), details);
            Assert.Contains(DiagnosticFailurePresentation.Operation(operation), details);
            Assert.EndsWith(DiagnosticFailurePresentation.Next("diagnostics.observed.checkSource"), details);
            Assert.DoesNotContain("You are banned", details);
            Assert.DoesNotContain("Login required", details);
            Assert.DoesNotContain("CAPTCHA required", details);
            AssertNoTransportStage(details);
        }
    }

    [Fact]
    public void CheckAndMedia404KeepDistinctOperationsKeysAndTargetsEvenWithTheSameWatchNumber()
    {
        FailureInfo notFound = new(FailureKind.Http, "resource-detail", HttpStatusCode.NotFound);
        DesktopThreadEntry thread = Thread() with
        {
            Snapshot = Thread().Snapshot with
            {
                LastCheckFailure = notFound,
                MediaFailures = new Dictionary<string, FailureInfo> { ["media-id"] = notFound }
            }
        };
        DesktopBoardEntry board = Board(notFound);
        DiagnosticEntry[] entries = DiagnosticsSnapshot.From(View(thread, board)).Entries.ToArray();
        Assert.Equal(new[] { "Thread:42:check", "thread:42:media:media-id", "Board:42:check" }, entries.Select(item => item.Key));
        Assert.Equal(new[] { DiagnosticKind.Check, DiagnosticKind.Media, DiagnosticKind.Check }, entries.Select(item => item.Kind));
        Assert.All(entries, item => Assert.True(item.IsProblem));
        Assert.All(entries, item => Assert.Equal(notFound.Message, item.Summary));
        Assert.Equal(new DiagnosticTarget(DiagnosticTargetKind.Thread, 42), entries[0].Target);
        Assert.Equal(entries[0].Target, entries[1].Target);
        Assert.Equal(new DiagnosticTarget(DiagnosticTargetKind.Board, 42), entries[2].Target);
        Assert.Equal(ThreadId, entries[0].ResourceId);
        Assert.Equal(ThreadId, entries[1].ResourceId);
        Assert.Equal(board.Snapshot.Id, entries[2].ResourceId);
        foreach (DiagnosticEntry check in new[] { entries[0], entries[2] })
        {
            Assert.Contains(Texts.T("diagnostics.observed.http404"), check.Details);
            Assert.DoesNotContain(Texts.T("diagnostics.observed.media404"), check.Details);
        }
        Assert.Contains(Texts.T("diagnostics.observed.media404"), entries[1].Details);
        Assert.Contains("does not establish the thread state", entries[1].Details);
        Assert.Contains(Texts.T("diagnostics.observed.mediaItem") + ": media-id", entries[1].Details);
        Assert.All(entries, item => Assert.DoesNotContain(Texts.T("work.sourceEnded"), item.Details));
        Assert.All(entries, item => Assert.Null(item.DownloadTargetPath));
        Assert.All(entries, item => Assert.False(item.CheckAlso));
    }

    [Fact]
    public void ThrottlingWithoutProgressDoesNotClaimAScheduledRetry()
    {
        FailureInfo throttled = new(FailureKind.Http, "rate-detail", HttpStatusCode.TooManyRequests);
        string details = DiagnosticFailurePresentation.Format(throttled, DiagnosticOperation.Thread);
        Assert.Contains(Texts.T("diagnostics.observed.http429"), details);
        Assert.EndsWith(DiagnosticFailurePresentation.Next("diagnostics.observed.retry"), details);
        Assert.DoesNotContain(Texts.T("diagnostics.observed.wait"), details);
        Assert.DoesNotContain(Texts.T("diagnostics.attempt", 0), details);
        Assert.DoesNotContain(Texts.T("request.cooldown"), details);
        Assert.DoesNotContain("Reported retry time:", details);
        Assert.DoesNotContain(Texts.T("check.scheduled"), details);
        Assert.DoesNotContain(Texts.T("check.alreadyScheduled"), details);
        Assert.DoesNotContain(Texts.T("work.retryAccepted", 1), details);
        DesktopThreadEntry thread = Thread(throttled);
        DiagnosticEntry entry = Assert.Single(DiagnosticsSnapshot.From(View(thread)).Entries);
        Assert.Equal("Thread:42:check", entry.Key);
        Assert.Null(thread.Snapshot.RequestProgress);
        Assert.Null(thread.Snapshot.Work);
    }

    [Theory]
    [InlineData(FailureNetworkStage.NameResolution, "diagnostics.observed.dns")]
    [InlineData(FailureNetworkStage.Connection, "diagnostics.observed.connection")]
    [InlineData(FailureNetworkStage.SecureConnection, "diagnostics.observed.tls")]
    public void TypedTransportStageIsExplainedWithoutInventingOtherStages(FailureNetworkStage stage, string key)
    {
        FailureInfo failure = new(FailureKind.Network, "same opaque detail")
            { NetworkStage = stage, DiagnosticCode = "producer-stage-detail" };
        string details = DiagnosticFailurePresentation.Format(failure, DiagnosticOperation.Media);
        Assert.Contains(Texts.T(key), details);
        foreach (string other in StageKeys.Where(candidate => candidate != key))
            Assert.DoesNotContain(Texts.T(other), details);
        Assert.Contains("Network" + Environment.NewLine + failure.Message, details);
        Assert.Contains(Texts.T("diagnostics.observed.code", failure.DiagnosticCode!), details);
        Assert.DoesNotContain(Texts.T("diagnostics.observed.unknown"), details);
        Assert.DoesNotContain("HTTP ", details);
    }

    [Theory]
    [InlineData(FailureKind.Unknown)]
    [InlineData(FailureKind.Network)]
    public void MessagesAndDiagnosticCodesCannotSupplyMissingTypedStage(FailureKind kind)
    {
        FailureInfo failure = new(kind, "DNS TLS connection HTTP 429 timeout are untrusted words")
            { DiagnosticCode = "http-name-resolution" };
        string details = DiagnosticFailurePresentation.Format(failure, DiagnosticOperation.Thread);
        Assert.Contains(Texts.T("diagnostics.observed.unknown"), details);
        Assert.Contains(Texts.T("diagnostics.observed.code", failure.DiagnosticCode!), details);
        Assert.Contains(failure.Message, details);
        AssertNoTransportStage(details);
        Assert.DoesNotContain(Texts.T("diagnostics.observed.http429"), details);
        Assert.DoesNotContain(Texts.T("diagnostics.observed.timeout"), details);
        Assert.EndsWith(DiagnosticFailurePresentation.Next("diagnostics.observed.retry"), details);
    }

    [Fact]
    public void UnrecognizedTypedStageAlsoStaysUnknown()
    {
        FailureInfo failure = new(FailureKind.Network, "opaque detail") { NetworkStage = (FailureNetworkStage)999 };
        string details = DiagnosticFailurePresentation.Format(failure, DiagnosticOperation.Thread);
        Assert.Contains(Texts.T("diagnostics.observed.unknown"), details);
        AssertNoTransportStage(details);
    }

    [Fact]
    public void CancellationAndDeadlineUseRealCoreProjectionWithoutGuessingANetworkPhase()
    {
        FailureInfo cancellation = FailureInfo.FromException(new OperationCanceledException("opaque detail"), media: false);
        FailureInfo deadline = FailureInfo.FromException(new TimeoutException("opaque detail"), media: false);
        Assert.Equal(FailureKind.Unknown, cancellation.Kind);
        Assert.Equal("operation-cancelled", cancellation.DiagnosticCode);
        Assert.Equal(FailureKind.Timeout, deadline.Kind);
        string cancelled = DiagnosticFailurePresentation.Format(cancellation, DiagnosticOperation.Thread);
        string timedOut = DiagnosticFailurePresentation.Format(deadline, DiagnosticOperation.Thread);
        Assert.Contains(Texts.T("diagnostics.observed.cancelled"), cancelled);
        Assert.DoesNotContain(Texts.T("diagnostics.observed.timeout"), cancelled);
        Assert.Contains(Texts.T("diagnostics.observed.timeout"), timedOut);
        Assert.DoesNotContain(Texts.T("diagnostics.observed.cancelled"), timedOut);
        AssertNoTransportStage(cancelled);
        AssertNoTransportStage(timedOut);
    }

    [Theory]
    [InlineData("timeout", FailureKind.Unknown)]
    [InlineData("unresponsive", FailureKind.Unknown)]
    [InlineData("vendor-dns-timeout", FailureKind.Unknown)]
    [InlineData("http-timeout", FailureKind.Timeout)]
    public void ActualWorkerFailureDoesNotTurnGenericOperationDeadlineIntoHttpTimeout(string code, FailureKind expected)
    {
        PluginWorkerException worker = new(code, "opaque worker detail");
        FailureInfo failure = FailureInfo.FromException(worker, media: false);
        Assert.Equal(expected, failure.Kind);
        Assert.Equal(code, failure.DiagnosticCode);
        Assert.Null(failure.NetworkStage);
        string details = DiagnosticFailurePresentation.Format(failure, DiagnosticOperation.Board);
        Assert.Contains(Texts.T("diagnostics.observed.code", code), details);
        Assert.Contains(worker.Message, details);
        Assert.Contains(Texts.T(expected == FailureKind.Timeout
            ? "diagnostics.observed.timeout" : "diagnostics.observed.unknown"), details);
        if (expected != FailureKind.Timeout)
            Assert.DoesNotContain(Texts.T("diagnostics.observed.timeout"), details);
        AssertNoTransportStage(details);
    }

    [Theory]
    [InlineData(FailureKind.Storage, "diagnostics.observed.storage", "diagnostics.observed.checkStorage")]
    [InlineData(FailureKind.InvalidData, "diagnostics.observed.invalidData", "diagnostics.observed.checkSource")]
    public void LocalStorageAndInvalidDataRemainDifferentFromTransport(FailureKind kind, string evidence, string next)
    {
        FailureInfo failure = new(kind, "opaque local detail");
        foreach (DiagnosticOperation operation in new[] { DiagnosticOperation.Media, DiagnosticOperation.Capture, DiagnosticOperation.Selection })
        {
            // Selection's legacy Storage bucket is covered separately: it cannot establish local I/O.
            if (kind == FailureKind.Storage && operation == DiagnosticOperation.Selection) continue;
            string details = DiagnosticFailurePresentation.Format(failure, operation);
            Assert.Contains(Texts.T(evidence), details);
            Assert.Contains(DiagnosticFailurePresentation.Operation(operation), details);
            Assert.EndsWith(DiagnosticFailurePresentation.Next(next), details);
            AssertNoTransportStage(details);
            Assert.DoesNotContain("HTTP ", details);
            Assert.DoesNotContain(Texts.T(kind == FailureKind.Storage
                ? "diagnostics.observed.invalidData" : "diagnostics.observed.storage"), details);
        }
    }

    [Theory]
    [InlineData("filter_host_unavailable")]
    [InlineData("restored_technical_omission")]
    [InlineData("disk full DNS TLS are not typed origin evidence")]
    public void SelectionStorageCatchAllCannotDiagnoseLocalDiskOrSpaceFromRawReason(string reason)
    {
        // Mirrors the broad SelectionFailure record shape used both for missing filter hosts and
        // restored technical omissions. The formatter must not parse these producer reason strings.
        FailureInfo selection = new(FailureKind.Storage, reason);
        DesktopThreadEntry thread = Thread() with
        {
            Snapshot = Thread().Snapshot with { Content = new ThreadContentStatus(true, null, null, selection, 0) }
        };
        DiagnosticEntry entry = Assert.Single(DiagnosticsSnapshot.From(View(thread)).Entries);
        Assert.Equal("thread:42:selection", entry.Key);
        Assert.Equal(DiagnosticKind.Selection, entry.Kind);
        Assert.Equal(new DiagnosticTarget(DiagnosticTargetKind.Thread, 42), entry.Target);
        Assert.Equal(ThreadId, entry.ResourceId);
        Assert.Contains(DiagnosticFailurePresentation.Operation(DiagnosticOperation.Selection), entry.Details);
        Assert.Contains(Texts.T("diagnostics.observed.unknown"), entry.Details);
        Assert.Contains("Storage" + Environment.NewLine + reason, entry.Details);
        Assert.EndsWith(DiagnosticFailurePresentation.Next("diagnostics.observed.checkSource"), entry.Details);
        Assert.DoesNotContain(Texts.T("diagnostics.observed.storage"), entry.Details);
        Assert.DoesNotContain(Texts.T("diagnostics.observed.checkStorage"), entry.Details);
        AssertNoTransportStage(entry.Details);
        Assert.True(entry.IsProblem);
        Assert.False(entry.CheckAlso);
    }

    [Theory]
    [InlineData(RequestPolicyPhase.WaitingForAdmission, true)]
    [InlineData(RequestPolicyPhase.Cooldown, true)]
    [InlineData(RequestPolicyPhase.Sending, false)]
    public void ThrottlingOnlyUsesWaitAdviceWhenRequestProgressActuallyWaits(RequestPolicyPhase phase, bool waits)
    {
        FailureInfo failure = new(FailureKind.Http, "rate detail", HttpStatusCode.TooManyRequests)
            { RetryAt = ReportedRetry };
        RequestPolicyProgress progress = new(phase, TimeSpan.FromSeconds(25), 2);
        string details = DiagnosticFailurePresentation.Format(failure, DiagnosticOperation.Thread, progress);
        Assert.Contains(Texts.T("diagnostics.observed.http429"), details);
        Assert.Contains(Texts.T("diagnostics.attempt", 2), details);
        Assert.Contains(RequestStatus.Format(progress)!, details);
        Assert.EndsWith(DiagnosticFailurePresentation.Next(waits
            ? "diagnostics.observed.wait" : "diagnostics.observed.retry"), details);
        if (!waits) Assert.DoesNotContain(Texts.T("diagnostics.observed.wait"), details);
    }

    [Theory]
    [InlineData(MediaWorkStatus.NeedsIntervention, false)]
    [InlineData(MediaWorkStatus.NeedsIntervention, true)]
    [InlineData(MediaWorkStatus.Expired, false)]
    [InlineData(MediaWorkStatus.Expired, true)]
    public void Terminal429DoesNotPromiseAutomaticRetryAfterManualAttemptOrRestore(MediaWorkStatus status, bool manual)
    {
        foreach (bool restored in new[] { false, true })
        {
            MediaWork media = Media() with
            {
                Status = status, Attempts = 3, HttpStatus = 429, Failure = "throttled detail",
                DueAt = null, ManualAttempt = manual
            };
            // Restore only retains the old category/message/status. The live reported time is
            // intentionally present to prove that even optional RetryAt cannot create a plan.
            FailureInfo failure = new(FailureKind.Http, media.Failure!, HttpStatusCode.TooManyRequests);
            if (!restored) failure = failure with { DiagnosticCode = "reported-rate-limit", RetryAt = ReportedRetry };
            DesktopThreadEntry thread = Thread() with
            {
                Snapshot = Thread().Snapshot with
                {
                    Work = Work(media), MediaFailures = new Dictionary<string, FailureInfo> { ["media-id"] = failure }
                }
            };
            DiagnosticEntry[] entries = DiagnosticsSnapshot.From(View(thread)).Entries.ToArray();
            Assert.Equal(new[] { "thread:42:work", "thread:42:media:media-id" }, entries.Select(entry => entry.Key));
            Assert.All(entries, entry =>
            {
                Assert.Contains(Texts.T("diagnostics.observed.http429"), entry.Details);
                Assert.EndsWith(DiagnosticFailurePresentation.Next("diagnostics.observed.retry"), entry.Details);
                Assert.DoesNotContain(Texts.T("diagnostics.observed.wait"), entry.Details);
                Assert.DoesNotContain("Earliest retry:", entry.Details);
                Assert.DoesNotContain(Texts.T("check.scheduled"), entry.Details);
                Assert.Equal(new DiagnosticTarget(DiagnosticTargetKind.Thread, 42), entry.Target);
                Assert.Equal(ThreadId, entry.ResourceId);
            });
            Assert.Null(media.DueAt);
            Assert.Null(thread.Snapshot.RequestProgress);
        }
    }

    [Fact]
    public void Waiting429WorkShowsActualDueEvidenceButDoesNotMakeStandaloneFailureAwaitAPlan()
    {
        MediaWork media = Media() with
        {
            Status = MediaWorkStatus.Waiting, HttpStatus = 429, Failure = "throttled detail",
            DueAt = ReportedRetry, Attempts = 1
        };
        FailureInfo failure = new(FailureKind.Http, media.Failure!, HttpStatusCode.TooManyRequests);
        DesktopThreadEntry thread = Thread() with
        {
            Snapshot = Thread().Snapshot with
            {
                Work = Work(media), MediaFailures = new Dictionary<string, FailureInfo> { ["media-id"] = failure }
            }
        };
        DiagnosticEntry[] entries = DiagnosticsSnapshot.From(View(thread)).Entries.ToArray();
        DiagnosticEntry work = Assert.Single(entries, entry => entry.Key == "thread:42:work");
        DiagnosticEntry standalone = Assert.Single(entries, entry => entry.Key == "thread:42:media:media-id");
        Assert.Contains(Texts.T("diagnostics.observed.http429"), work.Details);
        Assert.Contains(Texts.T("diagnostics.observed.retryAt",
            ReportedRetry.ToLocalTime().ToString("g", Texts.Culture)), work.Details);
        Assert.EndsWith(DiagnosticFailurePresentation.Next("diagnostics.observed.wait"), work.Details);
        // No media identity/due matching is invented across the two separate projections.
        Assert.EndsWith(DiagnosticFailurePresentation.Next("diagnostics.observed.retry"), standalone.Details);
        Assert.DoesNotContain(Texts.T("diagnostics.observed.wait"), standalone.Details);
        Assert.DoesNotContain(UnobservedEndpoint, work.Details);
    }

    [Fact]
    public void LegacyFailureWithNoMetadataRetainsConstructorDeconstructionAndHonestUnknownDetails()
    {
        FailureInfo legacy = new(FailureKind.Unknown, "legacy detail");
        var (kind, message, status) = legacy;
        Assert.Equal(FailureKind.Unknown, kind);
        Assert.Equal("legacy detail", message);
        Assert.Null(status);
        Assert.Null(legacy.DiagnosticCode);
        Assert.Null(legacy.RetryAt);
        Assert.Null(legacy.NetworkStage);
        string details = DiagnosticFailurePresentation.Format(legacy, DiagnosticOperation.Capture);
        Assert.Contains("Unknown" + Environment.NewLine + message, details);
        Assert.Contains(Texts.T("diagnostics.observed.unknown"), details);
        AssertNoTransportStage(details);
        Assert.DoesNotContain("Diagnostic code:", details);
        Assert.DoesNotContain("Reported retry time:", details);
        Assert.DoesNotContain("HTTP ", details);
    }

    [Fact]
    public void RetryAtIsOnlyReportedEvidenceAndWatchUrlIsNotAClaimedFailedEndpoint()
    {
        FailureInfo original = new(FailureKind.Unknown, "opaque detail");
        FailureInfo reported = original with { RetryAt = ReportedRetry, DiagnosticCode = "opaque-code" };
        DesktopThreadEntry thread = Thread(reported);
        DesktopSessionView view = View(thread);
        DiagnosticEntry entry = Assert.Single(DiagnosticsSnapshot.From(view).Entries);
        string retryLine = Texts.T("diagnostics.observed.retryAt", ReportedRetry.ToLocalTime().ToString("g", Texts.Culture));
        Assert.Contains(retryLine, entry.Details);
        Assert.StartsWith(Texts.T("diagnostics.observed.watchUrl") + ": " + WatchUrl + Environment.NewLine, entry.Details);
        Assert.DoesNotContain(UnobservedEndpoint, entry.Details);
        Assert.EndsWith(DiagnosticFailurePresentation.Next("diagnostics.observed.retry"), entry.Details);
        Assert.DoesNotContain(DiagnosticFailurePresentation.Next("diagnostics.observed.wait"), entry.Details);
        // Removing only the reported time must produce exactly the no-time projection.
        string withTime = DiagnosticFailurePresentation.Format(reported, DiagnosticOperation.Thread);
        string withoutTime = DiagnosticFailurePresentation.Format(reported with { RetryAt = null }, DiagnosticOperation.Thread);
        Assert.Equal(withoutTime, withTime.Replace(retryLine + Environment.NewLine, string.Empty, StringComparison.Ordinal));
        Assert.Same(thread, view.Threads[0]);
        Assert.Same(reported, thread.Snapshot.LastCheckFailure);
        Assert.Null(thread.Snapshot.RequestProgress);
        Assert.Null(thread.Snapshot.Work);
        Assert.Equal(new DownloadCounts(0, 0), thread.Snapshot.Downloads);
        Assert.Equal("Thread:42:check", entry.Key);
        Assert.Equal(new DiagnosticTarget(DiagnosticTargetKind.Thread, 42), entry.Target);
    }

    [Theory]
    [InlineData(RequestPolicyPhase.WaitingForAdmission)]
    [InlineData(RequestPolicyPhase.Cooldown)]
    public void ActualWaitingProgressRemainsNeutralAndDoesNotBecomeRetryAuthority(RequestPolicyPhase phase)
    {
        RequestPolicyProgress progress = new(phase, TimeSpan.FromSeconds(25), 2);
        DesktopThreadEntry thread = Thread() with { Snapshot = Thread().Snapshot with { RequestProgress = progress } };
        DiagnosticEntry request = Assert.Single(DiagnosticsSnapshot.From(View(thread)).Entries);
        Assert.Equal("Thread:42:request", request.Key);
        Assert.Equal(DiagnosticKind.Request, request.Kind);
        Assert.False(request.IsProblem);
        Assert.Contains(RequestStatus.Format(progress)!, request.Details);
        Assert.Contains(Texts.T("diagnostics.attempt", 2), request.Details);
        Assert.EndsWith(DiagnosticFailurePresentation.Next("diagnostics.observed.wait"), request.Details);
        Assert.DoesNotContain(Texts.T("diagnostics.observed.unknown"), request.Details);
        Assert.False(DiagnosticRetryScope.Matches(request, "fixture", null, problemsOnly: true));
        Assert.True(DiagnosticRetryScope.Matches(request, "fixture", null, problemsOnly: true, includeWaiting: true));
        Assert.False(DiagnosticRetryScope.Matches(request, "fixture-other", null, problemsOnly: true, includeWaiting: true));
        Assert.Empty(DiagnosticRetryScope.Capture(new[] { request }));
    }

    [Fact]
    public void CaptureAndSelectionRowsKeepTheirOriginalIdentityAndDoNotBecomeMediaOrNetworkFailures()
    {
        FailureInfo capture = new(FailureKind.Storage, "capture detail");
        FailureInfo selection = new(FailureKind.InvalidData, "selection detail");
        DesktopThreadEntry thread = Thread() with
        {
            Snapshot = Thread().Snapshot with { Content = new ThreadContentStatus(true, null, capture, selection, 1) }
        };
        DiagnosticEntry[] entries = DiagnosticsSnapshot.From(View(thread)).Entries.ToArray();
        Assert.Equal(new[] { "thread:42:capture", "thread:42:selection" }, entries.Select(item => item.Key));
        Assert.Equal(new[] { DiagnosticKind.Capture, DiagnosticKind.Selection }, entries.Select(item => item.Kind));
        Assert.Equal(new[] { Texts.T("nav.archive"), Texts.T("nav.rules") }, entries.Select(item => item.Category));
        Assert.Contains(DiagnosticFailurePresentation.Operation(DiagnosticOperation.Capture), entries[0].Details);
        Assert.Contains(Texts.T("diagnostics.observed.storage"), entries[0].Details);
        Assert.Contains(DiagnosticFailurePresentation.Operation(DiagnosticOperation.Selection), entries[1].Details);
        Assert.Contains(Texts.T("diagnostics.observed.invalidData"), entries[1].Details);
        Assert.All(entries, item =>
        {
            Assert.Equal(new DiagnosticTarget(DiagnosticTargetKind.Thread, 42), item.Target);
            Assert.Equal(ThreadId, item.ResourceId);
            Assert.True(item.IsProblem);
            Assert.False(item.CheckAlso);
            AssertNoTransportStage(item.Details);
            Assert.DoesNotContain(DiagnosticFailurePresentation.Operation(DiagnosticOperation.Media), item.Details);
        });
    }

    [Fact]
    public void SourceEndIsAnExplicitWorkFactNotAnInferenceFromMissingMedia()
    {
        MediaWork media = Media() with { Status = MediaWorkStatus.Waiting, HttpStatus = 404, DueAt = ReportedRetry };
        ThreadWorkSnapshot active = Work(media);
        string activeDetails = DiagnosticFailurePresentation.FormatWork(active, WorkStatusFormatter.Format(active).Details);
        Assert.Contains(Texts.T("diagnostics.observed.media404"), activeDetails);
        Assert.DoesNotContain(Texts.T("work.sourceEnded"), activeDetails);
        ThreadWorkSnapshot ended = active with { Source = ThreadSourceStateValue.NotFound };
        string endedDetails = DiagnosticFailurePresentation.FormatWork(ended, WorkStatusFormatter.Format(ended).Details);
        Assert.Contains(Texts.T("work.sourceEnded"), endedDetails);
        Assert.Contains(Texts.T("diagnostics.observed.media404"), endedDetails);
        AssertNoTransportStage(endedDetails);
        Assert.DoesNotContain(UnobservedEndpoint, endedDetails);
    }

    [Fact]
    public void DurableFilterExclusionIsSelectionNotNewNetworkEvidenceOrRetryAdvice()
    {
        MediaWork excluded = Media() with { Status = MediaWorkStatus.Excluded, Failure = "filter_nomatch" };
        ThreadWorkSnapshot work = Work(excluded);
        DesktopThreadEntry thread = Thread() with { Snapshot = Thread().Snapshot with { Work = work } };
        DiagnosticEntry entry = Assert.Single(DiagnosticsSnapshot.From(View(thread)).Entries);
        Assert.Equal("thread:42:work", entry.Key);
        Assert.Equal(new DiagnosticTarget(DiagnosticTargetKind.Thread, 42), entry.Target);
        Assert.Equal(ThreadId, entry.ResourceId);
        Assert.Contains(DiagnosticFailurePresentation.Operation(DiagnosticOperation.Selection), entry.Details);
        Assert.Contains("Excluded", entry.Details);
        Assert.Contains("filter_nomatch", entry.Details);
        Assert.DoesNotContain(DiagnosticFailurePresentation.Operation(DiagnosticOperation.Media), entry.Details);
        Assert.DoesNotContain(DiagnosticFailurePresentation.Next("diagnostics.observed.retry"), entry.Details);
        Assert.DoesNotContain(DiagnosticFailurePresentation.Next("diagnostics.observed.wait"), entry.Details);
        Assert.DoesNotContain(Texts.T("diagnostics.observed.unknown"), entry.Details);
        AssertNoTransportStage(entry.Details);
        Assert.DoesNotContain("HTTP ", entry.Details);
        Assert.DoesNotContain(UnobservedEndpoint, entry.Details);
    }

    [Fact]
    public void StorageReserveWaitKeepsPathIdentityWithoutClaimingAFailedWriteOrNetworkProblem()
    {
        const string path = "/synthetic/storage";
        DownloadTargetWaitState wait = new(path, "free-space-reserve", ReportedRetry);
        DiagnosticEntry entry = Assert.Single(DiagnosticsSnapshot.From(
            new DesktopSessionView(Array.Empty<DesktopThreadEntry>(), Array.Empty<DesktopBoardEntry>(),
                Array.Empty<string>(), DesktopSessionState.Ready, null), new[] { wait }).Entries);
        Assert.Equal("target:" + path, entry.Key);
        Assert.Equal(path, entry.DownloadTargetPath);
        Assert.Equal(path, entry.Resource);
        Assert.Null(entry.Target);
        Assert.Null(entry.ResourceId);
        Assert.Equal(DiagnosticKind.Storage, entry.Kind);
        Assert.True(entry.IsProblem);
        Assert.Contains(Texts.T("targets.freeSpace"), entry.Details);
        Assert.Contains(Texts.T("targets.nextCheck", ReportedRetry.ToLocalTime()), entry.Details);
        Assert.EndsWith(DiagnosticFailurePresentation.Next("diagnostics.observed.checkStorage"), entry.Details);
        Assert.DoesNotContain(Texts.T("diagnostics.observed.storage"), entry.Details);
        AssertNoTransportStage(entry.Details);
        Assert.Empty(DiagnosticRetryScope.Capture(new[] { entry }));
    }

    [Fact]
    public void DurableFailureTextDoesNotReconstructTransientCodeOrNetworkStage()
    {
        MediaWork media = Media() with
        {
            Status = MediaWorkStatus.NeedsIntervention,
            Failure = "http-name-resolution TLS timeout",
            Attempts = 3
        };
        ThreadWorkSnapshot work = Work(media);
        string details = DiagnosticFailurePresentation.FormatWork(work, WorkStatusFormatter.Format(work).Details);
        Assert.Contains(media.Failure, details);
        Assert.Contains(Texts.T("diagnostics.attempt", 3), details);
        Assert.Contains(Texts.T("diagnostics.observed.unknown"), details);
        AssertNoTransportStage(details);
        Assert.DoesNotContain(Texts.T("diagnostics.observed.code", media.Failure), details);
        Assert.DoesNotContain(Texts.T("diagnostics.observed.timeout"), details);
    }

    [Theory]
    [InlineData("en")]
    [InlineData("de")]
    [InlineData("es")]
    [InlineData("fr")]
    [InlineData("ru")]
    public void RefreshUsesLatestRealCatalogAndPreservesPreviouslyFrozenSnapshot(string language)
    {
        // This is an integration gate: ES/FR/RU must contain root's W28 pack additions.
        // Do not skip or silently accept the loader's English fallback for missing translations.
        IReadOnlyDictionary<string, string> catalog = Catalog(language);
        FailureInfo failure = new(FailureKind.Http, "opaque detail", HttpStatusCode.Forbidden)
            { DiagnosticCode = "source-refused", RetryAt = ReportedRetry };
        DesktopSessionView view = View(Thread(failure));
        Texts.SetLanguage(language == "en" ? "de" : "en");
        DiagnosticEntry frozen = Assert.Single(DiagnosticsSnapshot.From(view).Entries);
        string frozenDetails = frozen.Details;
        Texts.SetLanguage(language);
        Assert.Equal(language, Texts.Language);
        Assert.Empty(Texts.Warnings);
        foreach (string key in CanonicalKeys)
        {
            Assert.True(catalog.ContainsKey(key), language + ": missing " + key);
            Assert.False(string.IsNullOrWhiteSpace(catalog[key]));
            Assert.Equal(catalog[key], Texts.T(key));
        }
        DiagnosticEntry refreshed = Assert.Single(DiagnosticsSnapshot.From(view).Entries);
        Assert.Contains(catalog["diagnostics.observed.watchUrl"] + ": " + WatchUrl, refreshed.Details);
        Assert.Contains(catalog["diagnostics.observed.operation"] + ": " + catalog["diagnostics.observed.thread"], refreshed.Details);
        Assert.Contains(catalog["diagnostics.observed.evidence"] + ":", refreshed.Details);
        Assert.Contains(catalog["diagnostics.observed.http403"], refreshed.Details);
        Assert.Contains(string.Format(Texts.Culture, catalog["diagnostics.observed.code"], "source-refused"), refreshed.Details);
        Assert.Contains(string.Format(Texts.Culture, catalog["diagnostics.observed.retryAt"],
            ReportedRetry.ToLocalTime().ToString("g", Texts.Culture)), refreshed.Details);
        Assert.EndsWith(catalog["diagnostics.observed.next"] + ": " + catalog["diagnostics.observed.checkSource"], refreshed.Details);
        Assert.NotEqual(frozenDetails, refreshed.Details);
        Assert.Equal(frozenDetails, frozen.Details);
        Assert.Equal(frozen.Key, refreshed.Key);
        Assert.Equal(frozen.Target, refreshed.Target);
        Assert.Equal(frozen.ResourceId, refreshed.ResourceId);
        Assert.Equal(frozen.Kind, refreshed.Kind);
        Assert.Equal(frozen.Summary, refreshed.Summary);
    }

    private static readonly string[] StageKeys =
        { "diagnostics.observed.dns", "diagnostics.observed.connection", "diagnostics.observed.tls" };

    private static void AssertNoTransportStage(string details)
    {
        foreach (string key in StageKeys) Assert.DoesNotContain(Texts.T(key), details);
    }

    private static readonly string[] CanonicalKeys = new[]
    {
        "operation", "evidence", "next", "watchUrl", "mediaItem", "thread", "board", "media", "capture", "selection",
        "unknown", "dns", "connection", "tls", "http403", "http404", "media404", "http429", "cancelled", "timeout",
        "storage", "invalidData", "retryAt", "retry", "wait", "checkStorage", "checkSource", "code"
    }.Select(key => "diagnostics.observed." + key)
        .Concat(new[] { "diagnostics.includeWaiting", "diagnostics.copyPrivacy" }).ToArray();

    private static IReadOnlyDictionary<string, string> Catalog(string language)
    {
        if (language == "en") return BuiltInCatalog.English;
        if (language == "de") return BuiltInCatalog.German;
        using Stream stream = typeof(Texts).Assembly.GetManifestResourceStream(
            "MeowChan.Desktop.Localization.Packs." + language + ".json")
            ?? throw new InvalidOperationException("Missing real embedded catalog: " + language);
        using JsonDocument document = JsonDocument.Parse(stream);
        Assert.Equal(language, document.RootElement.GetProperty("code").GetString());
        return document.RootElement.GetProperty("strings").EnumerateObject()
            .ToDictionary(property => property.Name, property => property.Value.GetString()!, StringComparer.Ordinal);
    }

    private static DesktopThreadEntry Thread(FailureInfo? failure = null) => new(42,
        new ThreadSnapshot(ThreadId, WatchUrl, "Opaque title", "/synthetic/output", ThreadActivity.Pending,
            ThreadSourceState.Active, null, 1, 0, new Dictionary<string, FailureInfo>(), failure, new DownloadCounts(0, 0)), "fixture", "b");

    private static DesktopBoardEntry Board(FailureInfo? failure = null) => new(42,
        new BoardSnapshot(new ResourceId("fixture", "b"), "https://watch.invalid/b/", "b", "/synthetic/output", null, failure), "fixture", "b");

    private static DesktopSessionView View(DesktopThreadEntry thread, DesktopBoardEntry? board = null) => new(
        new[] { thread }, board == null ? Array.Empty<DesktopBoardEntry>() : new[] { board },
        Array.Empty<string>(), DesktopSessionState.Ready, null);

    private static MediaWork Media() => new()
    {
        ResourceUrl = WatchUrl, Generation = 7, MediaIdentifier = "media-id", DestinationPath = "/synthetic/output/media.bin",
        DownloadUrl = UnobservedEndpoint, OriginalFileName = "media.bin", Status = MediaWorkStatus.Ready
    };

    private static ThreadWorkSnapshot Work(params MediaWork[] media) =>
        new(WatchUrl, WorkIntent.Watching, ThreadSourceStateValue.Active, null, false, media);
}
