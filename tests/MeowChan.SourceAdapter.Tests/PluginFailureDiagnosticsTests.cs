using System.ComponentModel;
using System.Net;
using MeowChan.Downloader;
using MeowChan.Interfaces;
using MeowChan.PluginHost.Contracts;
using MeowChan.PluginHost.Sources;
using MeowChan.PluginSdk;

namespace MeowChan.SourceAdapter.Tests;

// Public adapter/monitor seams only. No Registry startup workaround, reflection,
// process launch, HTTP, delay-based observation or private FailureInfo mapper call.
public sealed class PluginFailureDiagnosticsTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);
    private static readonly DateTimeOffset ReportedRetry = new(2042, 3, 4, 5, 6, 7, TimeSpan.FromHours(5.5));
    private static readonly DateTimeOffset PastRetry = new(2001, 2, 3, 4, 5, 6, TimeSpan.FromHours(-4));
    private const string ThreadUrl = "https://boards.4chan.org/g/thread/123";
    private const string BoardUrl = "https://boards.4chan.org/g/";
    private const string Message = "synthetic failure; HTTP 403 DNS TLS timeout words are not evidence";

    [Theory]
    [InlineData(403)]
    [InlineData(404)]
    [InlineData(429)]
    [InlineData(499)] // Range-valid extension status must not require a named enum member.
    public void FactoryPreservesExplicitStatusCodeRetryMessageAndInnerException(int status)
    {
        FailureDetail detail = new("provider-specific", Message, status, ReportedRetry);
        Exception inner = new TimeoutException("worker deadline, not an observed network phase");
        PluginWorkerException exception = PluginWorkerException.FromFailure(detail, inner);

        Assert.Equal(detail.Code, exception.Code);
        Assert.Equal(detail.Message, exception.Message);
        Assert.Same(inner, exception.InnerException);
        Assert.Same(exception.FailureInfo, Assert.IsAssignableFrom<IFailureInfoProvider>(exception).FailureInfo);
        AssertEvidence(exception.FailureInfo, detail, FailureKind.Http);
    }

    [Theory]
    [InlineData("timeout", FailureKind.Unknown)]
    [InlineData("unresponsive", FailureKind.Unknown)]
    [InlineData("worker-exit", FailureKind.Unknown)]
    [InlineData("stopped", FailureKind.Unknown)]
    [InlineData("protocol", FailureKind.Unknown)]
    [InlineData("http-403", FailureKind.Unknown)]
    [InlineData("http-timeout-extra", FailureKind.Unknown)]
    [InlineData("HTTP-TIMEOUT", FailureKind.Unknown)]
    [InlineData("filter_stage_io", FailureKind.Unknown)]
    [InlineData("provider_future_code", FailureKind.Unknown)]
    [InlineData("http-timeout", FailureKind.Timeout)]
    [InlineData("http-network", FailureKind.Network)]
    [InlineData("invalid_metadata", FailureKind.InvalidData)]
    [InlineData("invalid_source_result", FailureKind.InvalidData)]
    [InlineData("http-encoding", FailureKind.InvalidData)]
    public void StatuslessCodesUseExactAllowlistWithoutMessageOrInnerExceptionInference(string code, FailureKind kind)
    {
        Exception inner = new HttpRequestException(HttpRequestError.NameResolutionError, "synthetic DNS failure");
        // Existing constructor remains callable with its original signature and preserves its inner.
        PluginWorkerException legacy = new(code, Message, inner);
        Assert.Equal(code, legacy.Code);
        Assert.Equal(Message, legacy.Message);
        Assert.Same(inner, legacy.InnerException);
        AssertEvidence(legacy.FailureInfo, new FailureDetail(code, Message), kind);

        PluginWorkerException factory = PluginWorkerException.FromFailure(new FailureDetail(code, Message), inner);
        AssertEvidence(factory.FailureInfo, new FailureDetail(code, Message), kind);
        Assert.Same(inner, factory.InnerException);
    }

    [Theory]
    [InlineData("http-timeout")]
    [InlineData("http-network")]
    [InlineData("invalid_metadata")]
    [InlineData("timeout")]
    public void ExplicitStatusTakesPrecedenceOverCodeFallback(string code)
    {
        FailureDetail detail = new(code, Message, 429, ReportedRetry);
        AssertEvidence(PluginWorkerException.FromFailure(detail).FailureInfo, detail, FailureKind.Http);
    }

    [Fact]
    public void ReportedPastRetryIsNotClampedToTheSourceSchedulerFloor()
    {
        FailureDetail detail = new("timeout", Message, RetryAt: PastRetry);
        AssertEvidence(PluginWorkerException.FromFailure(detail).FailureInfo, detail, FailureKind.Unknown);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(99)]
    [InlineData(600)]
    [InlineData(999)]
    public void FactoryRejectsOutOfRangeStatusRatherThanPublishingAnHttpFact(int status)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            PluginWorkerException.FromFailure(new FailureDetail("http-timeout", Message, status, ReportedRetry)));
    }

    [Fact]
    public void FactoryRejectsNullDetail() =>
        Assert.Throws<ArgumentNullException>(() => PluginWorkerException.FromFailure(null!));

    public static IEnumerable<object[]> SourceFailureCases()
    {
        (string Code, int? Status, FailureKind Kind)[] cases =
        [
            ("http_status", 403, FailureKind.Http),
            ("http_status", 404, FailureKind.Http),
            ("http_status", 429, FailureKind.Http),
            ("http-timeout", null, FailureKind.Timeout),
            ("http-network", null, FailureKind.Network),
            ("invalid_metadata", null, FailureKind.InvalidData),
            ("timeout", null, FailureKind.Unknown),
            ("unresponsive", null, FailureKind.Unknown),
            ("worker-exit", null, FailureKind.Unknown),
            ("http-403", null, FailureKind.Unknown),
            ("provider_future_code", null, FailureKind.Unknown)
        ];
        foreach (bool catalog in new[] { false, true })
            foreach (var item in cases)
                yield return new object[] { catalog, item.Code, item.Status!, item.Kind };
    }

    [Theory]
    [MemberData(nameof(SourceFailureCases))]
    public async Task RealSourceFailureReachesPublicMonitorSnapshotWithoutLosingEvidence(
        bool catalog, string code, int? status, FailureKind kind)
    {
        // A fixed historical value avoids long real timers and exposes any scheduler-floor substitution.
        FailureDetail detail = new(code, Message, status, PastRetry);
        // Return actual SDK ThreadFailure/CatalogFailure. Do not pre-convert it in the fake:
        // these assertions must catch the old Code/Message-only source adapter wiring.
        FailureInfo observed = await ObserveFailureAsync(catalog, detail);
        AssertEvidence(observed, detail, kind);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SourceSnapshotDoesNotInventReportedRetryFromInternalMinimumDelay(bool catalog)
    {
        FailureDetail detail = new("http_status", Message, 429);
        AssertEvidence(await ObserveFailureAsync(catalog, detail), detail, FailureKind.Http);
    }

    [Theory]
    [InlineData(false, "timeout")]
    [InlineData(true, "timeout")]
    [InlineData(false, "unresponsive")]
    [InlineData(true, "unresponsive")]
    [InlineData(false, "worker-exit")]
    [InlineData(true, "worker-exit")]
    [InlineData(false, "http-timeout")]
    [InlineData(true, "http-timeout")]
    public async Task WorkerExceptionBridgePreservesProducerClassificationOverInnerTransportCause(bool catalog, string code)
    {
        PluginWorkerException exception = new(code, Message,
            new HttpRequestException(HttpRequestError.SecureConnectionError, "synthetic TLS failure"));
        FailureInfo observed = await ObserveFailureAsync(catalog, null, exception);
        // Real monitor -> FailureInfo.FromException must prefer the public provider contract.
        Assert.Same(exception.FailureInfo, observed);
        AssertEvidence(observed, new FailureDetail(code, Message),
            code == "http-timeout" ? FailureKind.Timeout : FailureKind.Unknown);
    }

    [Theory]
    [InlineData(false, 99)]
    [InlineData(true, 99)]
    [InlineData(false, 600)]
    [InlineData(true, 600)]
    public async Task InvalidSourceStatusIsRejectedBeforeItCanBecomeNetworkEvidence(bool catalog, int status)
    {
        FailureInfo observed = await ObserveFailureAsync(catalog,
            new FailureDetail("http-timeout", Message, status, ReportedRetry));
        AssertRejectedResult(observed);
        Assert.Contains("httpStatusCode", observed.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvalidFailureMetadataDoesNotLaunderReportedStatusOrRetryIntoSnapshot(bool catalog)
    {
        // Empty code is invalid SDK metadata even though the numeric status itself is valid.
        FailureInfo observed = await ObserveFailureAsync(catalog, new FailureDetail("", Message, 403, ReportedRetry));
        AssertRejectedResult(observed);
        Assert.Contains("code", observed.Message);
    }

    [Fact]
    public async Task ExplicitThreadNotFound404IsSourceEndRatherThanCheckFailure()
    {
        using SourceAdapterTests.TemporaryProfile profile = new();
        using DenyHttp transport = new();
        using HttpClient http = new(transport);
        int calls = 0;
        SourceAdapterTests.TestWorker worker = new((request, _, _) =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult<ThreadReadResult>(new ThreadNotFound(request.Thread, 404));
        });
        using PluginSourceImageBoard source = new(worker.Description, "4chan",
            _ => Task.FromResult<IPluginWorkerClient>(worker), http);
        DownloadQueue queue = new(1, 1048576);
        IThread thread = source.GetThread(new Uri(ThreadUrl));
        TaskCompletionSource ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
        void Changed(object? sender, PropertyChangedEventArgs args)
        {
            if (thread.SourceState == ThreadSourceState.NotFound) ended.TrySetResult();
        }
        thread.PropertyChanged += Changed;
        try
        {
            thread.StartMonitoring(queue, profile.Path, TimeSpan.FromHours(1));
            await ended.Task.WaitAsync(Deadline);
            await thread.StopMonitoringAsync().WaitAsync(Deadline);
            MeowChan.ThreadSnapshot snapshot = thread.GetSnapshot(new DownloadCounts(0, 0));
            Assert.Equal(ThreadSourceState.NotFound, snapshot.SourceState);
            Assert.Null(snapshot.LastCheckFailure);
            Assert.Empty(snapshot.MediaFailures);
            Assert.Equal(1, Volatile.Read(ref calls));
            Assert.Equal(0, Volatile.Read(ref transport.Requests));
        }
        finally
        {
            thread.PropertyChanged -= Changed;
            try { await thread.StopMonitoringAsync().WaitAsync(Deadline); }
            finally { await queue.StopAllAsync().WaitAsync(Deadline); }
        }
    }

    private static void AssertRejectedResult(FailureInfo observed)
    {
        Assert.Equal(FailureKind.InvalidData, observed.Kind);
        Assert.Equal("invalid_source_result", observed.DiagnosticCode);
        Assert.Null(observed.HttpStatusCode);
        Assert.Null(observed.NetworkStage);
        Assert.Null(observed.RetryAt);
    }

    private static void AssertEvidence(FailureInfo actual, FailureDetail detail, FailureKind kind)
    {
        Assert.Equal(kind, actual.Kind);
        Assert.Equal(detail.Code, actual.DiagnosticCode);
        Assert.Equal(detail.Message, actual.Message);
        Assert.Equal(detail.HttpStatusCode is int status ? (HttpStatusCode?)status : null, actual.HttpStatusCode);
        Assert.Equal(detail.RetryAt, actual.RetryAt);
        Assert.Equal(detail.RetryAt?.Offset, actual.RetryAt?.Offset);
        Assert.Null(actual.NetworkStage); // SDK carries no typed DNS/connect/TLS evidence.
    }

    private static async Task<FailureInfo> ObserveFailureAsync(bool catalog, FailureDetail? detail,
        PluginWorkerException? thrown = null)
    {
        using SourceAdapterTests.TemporaryProfile profile = new();
        using DenyHttp transport = new();
        using HttpClient http = new(transport);
        int calls = 0;
        SourceAdapterTests.TestWorker worker = new((request, _, _) =>
        {
            Interlocked.Increment(ref calls);
            if (thrown != null) throw thrown;
            return Task.FromResult<ThreadReadResult>(new ThreadFailure(request.Thread, detail!));
        }, catalog: (request, _, _) =>
        {
            Interlocked.Increment(ref calls);
            if (thrown != null) throw thrown;
            return Task.FromResult<CatalogReadResult>(new CatalogFailure(request.Board, detail!));
        });
        using PluginSourceImageBoard source = new(worker.Description, "4chan",
            _ => Task.FromResult<IPluginWorkerClient>(worker), http);
        DownloadQueue queue = new(1, 1048576);
        IThread? thread = catalog ? null : source.GetThread(new Uri(ThreadUrl));
        IBoard? board = catalog ? source.GetBoard(new Uri(BoardUrl)) : null;
        INotifyPropertyChanged target = catalog ? board! : thread!;
        TaskCompletionSource<FailureInfo> reported = new(TaskCreationOptions.RunContinuationsAsynchronously);
        void Changed(object? sender, PropertyChangedEventArgs args)
        {
            // Notifications are coalesced all-properties invalidations (PropertyName may be null).
            FailureInfo? failure = catalog ? board!.LastCheckFailure : thread!.LastCheckFailure;
            if (failure != null) reported.TrySetResult(failure);
        }
        target.PropertyChanged += Changed;
        try
        {
            if (catalog) board!.StartMonitoring(3600);
            else thread!.StartMonitoring(queue, profile.Path, TimeSpan.FromHours(1));
            FailureInfo reportedFailure = await reported.Task.WaitAsync(Deadline);
            if (catalog)
            {
                BoardSnapshot snapshot = board!.GetSnapshot(profile.Path);
                Assert.Equal(new ResourceId("4chan", "g"), snapshot.Id);
                Assert.Equal(BoardUrl, snapshot.Url);
                Assert.Same(reportedFailure, snapshot.LastCheckFailure);
                Assert.Same(reportedFailure, board.GetSnapshot(profile.Path).LastCheckFailure);
            }
            else
            {
                MeowChan.ThreadSnapshot snapshot = thread!.GetSnapshot(new DownloadCounts(0, 0));
                Assert.Equal(new ResourceId("4chan", "g", "123"), snapshot.Id);
                Assert.Equal(ThreadUrl, snapshot.Url);
                // In particular ThreadFailure(404) is NOT the distinct ThreadNotFound result.
                Assert.Equal(ThreadSourceState.Active, snapshot.SourceState);
                Assert.Empty(snapshot.MediaFailures);
                Assert.Same(reportedFailure, snapshot.LastCheckFailure);
                Assert.Same(reportedFailure, thread.GetSnapshot(new DownloadCounts(0, 0)).LastCheckFailure);
            }
            Assert.Equal(1, Volatile.Read(ref calls)); // Passive snapshot reads cannot re-enter a worker.
            Assert.Equal(0, Volatile.Read(ref transport.Requests));
            return reportedFailure;
        }
        finally
        {
            target.PropertyChanged -= Changed;
            try
            {
                if (catalog) await board!.StopMonitoringAsync().WaitAsync(Deadline);
                else await thread!.StopMonitoringAsync().WaitAsync(Deadline);
            }
            finally { await queue.StopAllAsync().WaitAsync(Deadline); }
        }
    }

    private sealed class DenyHttp : HttpMessageHandler
    {
        internal int Requests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Interlocked.Increment(ref Requests);
            throw new InvalidOperationException("Plugin diagnostics regression must never send HTTP.");
        }
    }
}
