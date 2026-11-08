using System.ComponentModel;
using System.Net;
using MeowChan.Downloader;
using MeowChan.Http;
using MeowChan.OfficialSources.FourChan;
using MeowChan.Interfaces;
using MeowChan.PluginHost.Contracts;
using MeowChan.PluginHost.Http;
using MeowChan.PluginHost.Sources;
using MeowChan.PluginSdk;

namespace MeowChan.SourceAdapter.Tests;

// Synthetic public adapter/monitor seams, following PluginFailureDiagnosticsTests.
// No provider requests, worker processes, reflection or Registry startup workaround.
public sealed class PluginSourceRequestDiagnosticContextTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);
    private static readonly DateTimeOffset PastRetry = new(2001, 2, 3, 4, 5, 6, TimeSpan.FromHours(-4));
    private const string ThreadUrl = "https://boards.4chan.org/g/thread/123";
    private const string BoardUrl = "https://boards.4chan.org/g/";
    private const string Reason = "provider_future_reason_HTTP403_DNS_TLS_timeout";

    public static IEnumerable<object[]> DeferredStatusCases()
    {
        foreach (string path in new[] { "thread", "catalog", "cursor", "page" })
            foreach (int status in new[] { 403, 404, 429, 499 })
                yield return new object[] { path, status };
    }

    [Theory]
    [MemberData(nameof(DeferredStatusCases))]
    public async Task DeferredSdkStatusSurvivesMonitorAndCatalogContinuations(string path, int status)
    {
        DateTimeOffset retry = DateTimeOffset.UtcNow.AddHours(1).ToOffset(TimeSpan.FromHours(5.5));
        FailureInfo failure = await ObserveDeferredAsync(path, status, Reason, retry, checkCachedRetry: true);
        AssertDeferredFacts(failure, status, retry);
    }

    [Theory]
    [InlineData("thread")]
    [InlineData("cursor")]
    public async Task ReportedPastRetryIsNotReplacedByInternalSchedulerFloor(string path)
    {
        FailureInfo failure = await ObserveDeferredAsync(path, 429, Reason, PastRetry);
        AssertDeferredFacts(failure, 429, PastRetry);
    }

    [Theory]
    [InlineData("thread", 99, "cooldown")]
    [InlineData("thread", 600, "cooldown")]
    [InlineData("catalog", 99, "cooldown")]
    [InlineData("catalog", 600, "cooldown")]
    [InlineData("thread", 429, "")]
    [InlineData("cursor", 429, "")]
    public async Task InvalidDeferredMetadataIsRejectedBeforeBecomingDiagnosticEvidence(string path, int status, string reason)
    {
        FailureInfo failure = await ObserveDeferredAsync(path, status, reason, PastRetry);
        Assert.Equal(FailureKind.InvalidData, failure.Kind);
        Assert.Equal("invalid_source_result", failure.DiagnosticCode);
        Assert.Contains(status is < 100 or > 599 ? "httpStatusCode" : "reasonCode", failure.Message);
        Assert.Null(failure.HttpStatusCode);
        Assert.Null(failure.RetryAt);
        Assert.Null(failure.NetworkStage);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task StatuslessAndProvenInitialNoSendWaitsStayNeutralThroughMonitorAndCachedRetry(bool catalog, bool initialWait)
    {
        using SourceAdapterTests.TemporaryProfile profile = new();
        using DenyHttp mediaTransport = new();
        using HttpClient media = new(mediaTransport);
        // Prime shared cooldown outside this watch's history using only a synthetic handler.
        // The watched operation must not send or inherit the other operation's 429 failure.
        using CooldownTransport transport = new();
        using HttpClient http = new(transport);
        using PluginHostHttp host = new(http, new RequestPolicyState(), new FourChanPlugin().Describe().Sources);
        HostHttpRequest httpRequest = new("4chan", HostHttpMethod.Get, new Uri("https://a.4cdn.org/g/catalog.json"));
        if (initialWait) Assert.IsType<HostHttpDeferred>(await host.SendAsync(httpRequest).WaitAsync(Deadline));
        int calls = 0;
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<(DateTimeOffset Retry, string Reason, int? Status)> ReadAsync(CancellationToken token)
        {
            Interlocked.Increment(ref calls);
            entered.TrySetResult();
            if (!initialWait) return (DateTimeOffset.UtcNow.AddHours(1), Reason, null);
            HostHttpDeferred wait = Assert.IsType<HostHttpDeferred>(await host.SendAsync(httpRequest, token));
            // Even an SDK-reported status cannot turn a proven initial no-send wait into a failure.
            return (wait.RetryAt, wait.ReasonCode, 429);
        }
        SourceAdapterTests.TestWorker worker = new(async (request, _, token) =>
        {
            var wait = await ReadAsync(token);
            return new ThreadDeferred(request.Thread, wait.Retry, wait.Reason, wait.Status);
        }, catalog: async (request, _, token) =>
        {
            var wait = await ReadAsync(token);
            return new CatalogDeferred(request.Board, wait.Retry, wait.Reason, wait.Status);
        });
        using PluginSourceImageBoard source = new(worker.Description, "4chan",
            _ => Task.FromResult<IPluginWorkerClient>(worker), media);
        DownloadQueue queue = new(1, 1048576);
        IThread? thread = catalog ? null : source.GetThread(new Uri(ThreadUrl));
        IBoard? board = catalog ? source.GetBoard(new Uri(BoardUrl)) : null;
        Task CheckAsync() => catalog ? board!.GetThreadsAsync() : thread!.checkThreadAsync(queue);
        Task StopAsync() => catalog ? board!.StopMonitoringAsync() : thread!.StopMonitoringAsync();
        try
        {
            if (catalog) board!.StartMonitoring(3600);
            else thread!.StartMonitoring(queue, profile.Path, TimeSpan.FromHours(1));
            await entered.Task.WaitAsync(Deadline);
            // The adapter's read gate fences completion of the first observation before
            // this cached read returns. Drain the monitor before asserting neutrality:
            // a direct GetThreadsAsync alone never populates LastCheckFailure.
            Exception first = await Assert.ThrowsAnyAsync<Exception>(() => CheckAsync().WaitAsync(Deadline));
            Exception cached = await Assert.ThrowsAnyAsync<Exception>(() => CheckAsync().WaitAsync(Deadline));
            Assert.Same(first, cached);
            await StopAsync().WaitAsync(Deadline);
            Assert.Null(catalog ? board!.LastCheckFailure : thread!.LastCheckFailure);
            if (catalog) Assert.Null(board!.GetSnapshot(profile.Path).LastCheckFailure);
            else
            {
                var snapshot = thread!.GetSnapshot(new DownloadCounts(0, 0));
                Assert.Equal(ThreadSourceState.Active, snapshot.SourceState);
                Assert.Null(snapshot.LastCheckFailure);
                Assert.Empty(snapshot.MediaFailures);
            }
            Assert.Equal(1, Volatile.Read(ref calls));
            Assert.Equal(initialWait ? 1 : 0, Volatile.Read(ref transport.Requests));
            Assert.Equal(0, Volatile.Read(ref mediaTransport.Requests));
        }
        finally
        {
            try { await StopAsync().WaitAsync(Deadline); }
            finally { await queue.StopAllAsync().WaitAsync(Deadline); }
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task CallerStopAndLateDeferredResultDoNotBecomeDiagnosticFailures(bool catalog, bool returnLateResult)
    {
        using SourceAdapterTests.TemporaryProfile profile = new();
        using DenyHttp transport = new();
        using HttpClient http = new(transport);
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken observedToken = default;
        int calls = 0;
        async Task WaitForStopAsync(CancellationToken token)
        {
            Interlocked.Increment(ref calls);
            observedToken = token;
            entered.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            catch (OperationCanceledException) when (returnLateResult) { }
        }
        SourceAdapterTests.TestWorker worker = new(async (request, _, token) =>
        {
            await WaitForStopAsync(token);
            return new ThreadDeferred(request.Thread, DateTimeOffset.UtcNow.AddHours(1), Reason, 429);
        }, catalog: async (request, _, token) =>
        {
            await WaitForStopAsync(token);
            return new CatalogDeferred(request.Board, DateTimeOffset.UtcNow.AddHours(1), Reason, 429);
        });
        using PluginSourceImageBoard source = new(worker.Description, "4chan",
            _ => Task.FromResult<IPluginWorkerClient>(worker), http);
        DownloadQueue queue = new(1, 1048576);
        IThread? thread = catalog ? null : source.GetThread(new Uri(ThreadUrl));
        IBoard? board = catalog ? source.GetBoard(new Uri(BoardUrl)) : null;
        Task StopAsync() => catalog ? board!.StopMonitoringAsync() : thread!.StopMonitoringAsync();
        try
        {
            if (catalog) board!.StartMonitoring(3600);
            else thread!.StartMonitoring(queue, profile.Path, TimeSpan.FromHours(1));
            await entered.Task.WaitAsync(Deadline);
            await StopAsync().WaitAsync(Deadline);
            Assert.True(observedToken.IsCancellationRequested);
            Assert.Null(catalog ? board!.LastCheckFailure : thread!.LastCheckFailure);
            if (!catalog) Assert.Equal(ThreadSourceState.Active, thread!.SourceState);
            Assert.Equal(1, Volatile.Read(ref calls));
            Assert.Equal(0, Volatile.Read(ref transport.Requests));
        }
        finally
        {
            try { await StopAsync().WaitAsync(Deadline); }
            finally { await queue.StopAllAsync().WaitAsync(Deadline); }
        }
    }

    private static void AssertDeferredFacts(FailureInfo failure, int status, DateTimeOffset retry)
    {
        Assert.Equal(FailureKind.Http, failure.Kind);
        Assert.Equal((HttpStatusCode)status, failure.HttpStatusCode);
        // Unknown plugin reasons are not a privacy-vetted diagnostic vocabulary.
        Assert.Equal("source_check_deferred", failure.DiagnosticCode);
        Assert.Equal(retry, failure.RetryAt);
        Assert.Equal(retry.Offset, failure.RetryAt?.Offset);
        Assert.Null(failure.NetworkStage); // ReasonCode words are not DNS/connect/TLS evidence.
    }

    private static async Task<FailureInfo> ObserveDeferredAsync(string path, int status, string reason,
        DateTimeOffset retry, bool checkCachedRetry = false)
    {
        bool catalog = path != "thread";
        bool paged = path is "cursor" or "page";
        using SourceAdapterTests.TemporaryProfile profile = new();
        using DenyHttp transport = new();
        using HttpClient http = new(transport);
        int calls = 0;
        PluginOperationContext? first = null;
        SourceAdapterTests.TestWorker worker = new((request, operation, _) =>
        {
            Interlocked.Increment(ref calls);
            first = operation;
            return Task.FromResult<ThreadReadResult>(new ThreadDeferred(request.Thread, retry, reason, status));
        }, catalog: (request, operation, _) =>
        {
            int call = Interlocked.Increment(ref calls);
            if (call == 1)
            {
                first = operation;
                Assert.Null(request.Cursor);
                Assert.Null(request.Page);
                if (paged)
                    return Task.FromResult<CatalogReadResult>(new CatalogSnapshot(new SourceCatalogSnapshot(
                        request.Board, Array.Empty<SourceThreadReference>(), false,
                        NextCursor: path == "cursor" ? "next" : null, NextPage: path == "page" ? 2 : null)));
            }
            else
            {
                Assert.Equal(path == "cursor" ? "next" : null, request.Cursor);
                Assert.Equal(path == "page" ? (int?)2 : null, request.Page);
                Assert.Same(first, operation); // all pages retain the resource/generation context
            }
            return Task.FromResult<CatalogReadResult>(new CatalogDeferred(request.Board, retry, reason, status));
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
            FailureInfo? failure = catalog ? board!.LastCheckFailure : thread!.LastCheckFailure;
            if (failure != null) reported.TrySetResult(failure);
        }
        target.PropertyChanged += Changed;
        try
        {
            if (catalog) board!.StartMonitoring(3600);
            else thread!.StartMonitoring(queue, profile.Path, TimeSpan.FromHours(1));
            FailureInfo failure = await reported.Task.WaitAsync(Deadline);
            Assert.Equal(catalog ? BoardUrl : ThreadUrl, first!.ResourceId);
            if (checkCachedRetry)
            {
                Task CheckAsync() => catalog ? board!.GetThreadsAsync() : thread!.checkThreadAsync(queue);
                Exception firstWait = await Assert.ThrowsAnyAsync<Exception>(() => CheckAsync().WaitAsync(Deadline));
                Exception cachedWait = await Assert.ThrowsAnyAsync<Exception>(() => CheckAsync().WaitAsync(Deadline));
                Assert.Same(firstWait, cachedWait);
            }
            if (catalog) Assert.Same(failure, board!.GetSnapshot(profile.Path).LastCheckFailure);
            else
            {
                var snapshot = thread!.GetSnapshot(new DownloadCounts(0, 0));
                Assert.Equal(ThreadSourceState.Active, snapshot.SourceState); // deferred 404 is not source end
                Assert.Same(failure, snapshot.LastCheckFailure);
                Assert.Empty(snapshot.MediaFailures);
            }
            Assert.Equal(paged ? 2 : 1, Volatile.Read(ref calls));
            Assert.Equal(0, Volatile.Read(ref transport.Requests));
            return failure;
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

    private sealed class CooldownTransport : HttpMessageHandler
    {
        internal int Requests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Interlocked.Increment(ref Requests);
            HttpResponseMessage response = new(HttpStatusCode.TooManyRequests)
                { RequestMessage = request, Content = new StringContent("") };
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromHours(1));
            return Task.FromResult(response);
        }
    }

    private sealed class DenyHttp : HttpMessageHandler
    {
        internal int Requests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Interlocked.Increment(ref Requests);
            throw new InvalidOperationException("Synthetic source context tests must not send HTTP.");
        }
    }
}
