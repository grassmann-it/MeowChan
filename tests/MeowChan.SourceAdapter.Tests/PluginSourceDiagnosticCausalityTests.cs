using System.ComponentModel;
using System.Net;
using MeowChan.Downloader;
using MeowChan.Http;
using MeowChan.Interfaces;
using MeowChan.OfficialSources.FourChan;
using MeowChan.PluginHost.Contracts;
using MeowChan.PluginHost.Http;
using MeowChan.PluginHost.Sources;
using MeowChan.PluginSdk;

namespace MeowChan.SourceAdapter.Tests;

// Real adapters/monitors/host policy, synthetic handlers only. No private mapper,
// reflection, worker process, network, or Registry startup bypass.
public sealed class PluginSourceDiagnosticCausalityTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);
    private const string Secret = "CredentialSentinel9f8c";
    private static DateTimeOffset ReportedRetry => DateTimeOffset.UtcNow.AddHours(1).ToOffset(TimeSpan.FromHours(5.5));

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task SuccessfulFallbackThenInvalidMetadataDoesNotInheritEarlierHostFailure(bool catalog, bool typedFailure)
    {
        using Host host = new((request, _) => Task.FromResult(request.RequestUri!.Host == "metadata.example"
            ? typedFailure ? Broken(new HttpRequestException(HttpRequestError.NameResolutionError, Secret))
                : Response(HttpStatusCode.Forbidden)
            : Response(HttpStatusCode.OK, "not valid json")));
        DateTimeOffset retry = ReportedRetry;
        FailureDetail final = new("invalid_metadata", "The response is invalid.", RetryAt: retry);
        var worker = Worker(host.Description, async token =>
        {
            HostHttpResult previous = await host.SendAsync("metadata.example", token);
            if (typedFailure) Assert.IsType<HostHttpFailure>(previous);
            else Assert.Equal(403, Assert.IsType<HostHttpResponse>(previous).StatusCode);
            Assert.Equal(200, Assert.IsType<HostHttpResponse>(await host.SendAsync("second.example", token)).StatusCode);
            return new Outcome(final);
        });
        FailureInfo observed = await ObserveAsync(catalog, worker);
        AssertSdk(observed, final, FailureKind.InvalidData);
        Assert.DoesNotContain(Secret, observed.ToString());
        Assert.Equal(2, host.Requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExplicitSdkStatusCodeAndRetrySurviveARealHostStatus(bool catalog)
    {
        using Host host = new((_, _) => Task.FromResult(Response(HttpStatusCode.Forbidden)));
        FailureDetail final = new("provider_backoff", "synthetic provider decision", 403, ReportedRetry);
        var worker = Worker(host.Description, async token =>
        {
            Assert.Equal(403, Assert.IsType<HostHttpResponse>(await host.SendAsync("metadata.example", token)).StatusCode);
            return new Outcome(final);
        });
        AssertSdk(await ObserveAsync(catalog, worker), final, FailureKind.Http);
        Assert.Equal(1, host.Requests);
    }

    [Theory]
    [InlineData(false, "dns")]
    [InlineData(true, "dns")]
    [InlineData(false, "connect")]
    [InlineData(true, "connect")]
    [InlineData(false, "tls")]
    [InlineData(true, "tls")]
    [InlineData(false, "plain-io")]
    [InlineData(true, "plain-io")]
    [InlineData(false, "ended-403")]
    [InlineData(true, "ended-403")]
    [InlineData(false, "unrequested-cancel")]
    [InlineData(true, "unrequested-cancel")]
    public async Task ImmediateHostFailureEchoRetainsTypedSanitizedFactsAndExplicitSdkCodeRetry(bool catalog, string mode)
    {
        Exception error = mode switch
        {
            "dns" => new HttpRequestException(HttpRequestError.NameResolutionError, Secret),
            "connect" => new HttpRequestException(HttpRequestError.ConnectionError, Secret),
            "tls" => new HttpRequestException(HttpRequestError.SecureConnectionError, Secret),
            "ended-403" => new HttpIOException(HttpRequestError.ResponseEnded, Secret),
            "unrequested-cancel" => new OperationCanceledException(Secret),
            _ => new IOException("DNS TLS timeout " + Secret)
        };
        using Host host = new((_, _) => Task.FromResult(Broken(error,
            mode == "ended-403" ? HttpStatusCode.Forbidden : HttpStatusCode.OK)));
        DateTimeOffset retry = ReportedRetry;
        var worker = Worker(host.Description, async token =>
        {
            FailureDetail echo = Assert.IsType<HostHttpFailure>(await host.SendAsync("metadata.example", token)).Failure;
            // A fresh record models deserialized SDK data, not in-process object identity.
            return new Outcome(new FailureDetail(echo.Code, echo.Message, echo.HttpStatusCode, retry));
        });
        FailureInfo observed = await ObserveAsync(catalog, worker);
        Assert.Equal(mode is "plain-io" or "unrequested-cancel" ? FailureKind.Unknown : FailureKind.Network, observed.Kind);
        Assert.Equal(mode switch { "dns" => FailureNetworkStage.NameResolution,
            "connect" => FailureNetworkStage.Connection, "tls" => FailureNetworkStage.SecureConnection,
            _ => (FailureNetworkStage?)null }, observed.NetworkStage);
        Assert.Equal(mode == "ended-403" ? HttpStatusCode.Forbidden : (HttpStatusCode?)null, observed.HttpStatusCode);
        Assert.Equal(mode == "unrequested-cancel" ? "http-timeout" : "http-network", observed.DiagnosticCode);
        Assert.Equal("Metadata request failed.", observed.Message);
        AssertRetry(retry, observed);
        Assert.DoesNotContain(Secret, observed.ToString());
        Assert.Equal(1, host.Requests);
    }

    [Theory]
    [InlineData(false, "code")]
    [InlineData(true, "code")]
    [InlineData(false, "message")]
    [InlineData(true, "message")]
    [InlineData(false, "status")]
    [InlineData(true, "status")]
    [InlineData(false, "later-success")]
    [InlineData(true, "later-success")]
    public async Task NonmatchingOrSupersededEchoCannotBorrowHostStageOrStatus(bool catalog, string difference)
    {
        using Host host = new((request, _) => Task.FromResult(request.RequestUri!.Host == "metadata.example"
            ? Broken(new HttpRequestException(HttpRequestError.NameResolutionError, "synthetic host text"), HttpStatusCode.Forbidden)
            : Response(HttpStatusCode.OK)));
        FailureDetail? final = null;
        DateTimeOffset retry = ReportedRetry;
        var worker = Worker(host.Description, async token =>
        {
            FailureDetail echo = Assert.IsType<HostHttpFailure>(await host.SendAsync("metadata.example", token)).Failure;
            if (difference == "later-success")
                Assert.IsType<HostHttpResponse>(await host.SendAsync("second.example", token));
            final = new FailureDetail(difference == "code" ? "invalid_metadata" : echo.Code,
                difference == "message" ? "A separate failure." : echo.Message,
                difference == "status" ? 404 : echo.HttpStatusCode, retry);
            return new Outcome(final);
        });
        FailureInfo observed = await ObserveAsync(catalog, worker);
        AssertSdk(observed, final!, difference switch { "code" => FailureKind.InvalidData,
            "status" => FailureKind.Http, _ => FailureKind.Network });
        Assert.Equal(difference == "later-success" ? 2 : 1, host.Requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OverlappingHostRequestsDoNotCreateUnprovenFinalCorrelation(bool catalog)
    {
        Gate body = new();
        using Host host = new((request, _) => Task.FromResult(request.RequestUri!.Host == "metadata.example"
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new GatedContent(body) }
            : Broken(new HttpRequestException(HttpRequestError.NameResolutionError, "synthetic overlap"))));
        FailureDetail? final = null;
        var worker = Worker(host.Description, async token =>
        {
            Task<HostHttpResult> first = host.SendAsync("metadata.example", token);
            try
            {
                await body.Entered.Task.WaitAsync(token);
                final = Assert.IsType<HostHttpFailure>(await host.SendAsync("second.example", token)).Failure with { RetryAt = ReportedRetry };
            }
            finally { body.Release.TrySetResult(); await first; }
            return new Outcome(final);
        });
        try { AssertSdk(await ObserveAsync(catalog, worker), final!, FailureKind.Network); }
        finally { body.Release.TrySetResult(); }
        Assert.Equal(2, host.Requests);
    }

    [Fact]
    public async Task PriorCatalogPageCannotLendItsFailureEchoToAnotherSdkResult()
    {
        using Host host = new((_, _) => Task.FromResult(Broken(
            new HttpRequestException(HttpRequestError.NameResolutionError, "synthetic earlier page"))));
        FailureDetail? earlier = null;
        var worker = new SourceAdapterTests.TestWorker((_, _, _) => throw new InvalidOperationException("Catalog only"),
            host.Description, async (request, _, token) =>
            {
                if (request.Cursor == null)
                {
                    earlier = Assert.IsType<HostHttpFailure>(await host.SendAsync("metadata.example", token)).Failure;
                    return new CatalogSnapshot(new SourceCatalogSnapshot(request.Board, Array.Empty<SourceThreadReference>(), false, NextCursor: "next"));
                }
                Assert.Equal("next", request.Cursor);
                return new CatalogFailure(request.Board, earlier! with { });
            });
        AssertSdk(await ObserveAsync(true, worker, 2), earlier!, FailureKind.Network);
        Assert.Equal(1, host.Requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PostFailureDeferredKeepsObservedHistoryWithoutTreatingItAsFinalSdkFailure(bool catalog)
    {
        using Host host = new((request, _) => Task.FromResult(request.RequestUri!.Host == "second.example"
            ? new HttpResponseMessage(HttpStatusCode.TooManyRequests)
            {
                Headers = { RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromHours(1)) },
                Content = new StringContent("")
            }
            : Broken(new HttpRequestException(HttpRequestError.NameResolutionError, Secret))));
        // Prime a different operation's cooldown, not the monitored history.
        Assert.IsType<HostHttpDeferred>(await host.SendAsync("second.example").WaitAsync(Deadline));
        var worker = Worker(host.Description, async token =>
        {
            Assert.IsType<HostHttpFailure>(await host.SendAsync("metadata.example", token));
            HostHttpDeferred wait = Assert.IsType<HostHttpDeferred>(await host.SendAsync("second.example", token));
            return new Outcome(null, wait);
        });
        FailureInfo observed = await ObserveAsync(catalog, worker);
        Assert.Equal(FailureKind.Network, observed.Kind);
        Assert.Equal(FailureNetworkStage.NameResolution, observed.NetworkStage);
        Assert.Equal("http-name-resolution", observed.DiagnosticCode); // Existing deferred history, not an SDK Failure echo.
        Assert.Null(observed.HttpStatusCode); // The priming operation's 429 is not this operation's status.
        Assert.Null(observed.RetryAt);
        Assert.Equal("Metadata request failed.", observed.Message);
        Assert.DoesNotContain(Secret, observed.ToString());
        Assert.Equal(2, host.Requests); // prime + failure, no send on the watched wait
    }

    public static IEnumerable<object[]> DeferredReasons()
    {
        (string Reason, int Status, string Code)[] cases =
        [
            ("admission", 403, "admission"),
            ("cooldown", 429, "cooldown"),
            ("future_machine_reason", 499, "source_check_deferred"),
            ($"https://user:{Secret}@metadata.example/x?token={Secret}", 429, "source_check_deferred"),
            ($"user:{Secret}@metadata.example", 403, "source_check_deferred"),
            ($"token={Secret}&password={Secret}", 404, "source_check_deferred"),
            ($"https%3A%2F%2Fmetadata.example%2Fx%3Ftoken%3D{Secret}", 499, "source_check_deferred"),
            (Secret, 429, "source_check_deferred"), // A short machine-shaped credential is still not a safe public code.
            (new string('A', 180) + Secret, 499, "source_check_deferred")
        ];
        foreach (bool catalog in new[] { false, true })
            foreach (var item in cases) yield return new object[] { catalog, item.Reason, item.Status, item.Code };
    }

    [Theory]
    [MemberData(nameof(DeferredReasons))]
    public async Task NewDeferredDiagnosisPublishesOnlyFiniteSafeCodesAndExplicitStatusRetry(bool catalog, string reason, int status, string code)
    {
        using Host host = new((_, _) => throw new InvalidOperationException("SDK-only deferred must not send HTTP."));
        DateTimeOffset retry = ReportedRetry;
        var worker = Worker(host.Description, _ => Task.FromResult(new Outcome(null, new HostHttpDeferred(retry, reason, status))));
        FailureInfo observed = await ObserveAsync(catalog, worker);
        Assert.Equal(FailureKind.Http, observed.Kind);
        Assert.Equal((HttpStatusCode)status, observed.HttpStatusCode);
        Assert.Null(observed.NetworkStage);
        Assert.Equal(code, observed.DiagnosticCode);
        AssertRetry(retry, observed);
        Assert.Equal("Source check deferred with a reported HTTP status.", observed.Message);
        Assert.DoesNotContain(Secret, observed.ToString());
        Assert.DoesNotContain("metadata.example", observed.ToString());
        Assert.DoesNotContain("token=", observed.ToString());
        Assert.Equal(0, host.Requests);
    }

    private static void AssertRetry(DateTimeOffset retry, FailureInfo observed)
    {
        Assert.Equal(retry, observed.RetryAt);
        Assert.Equal(retry.Offset, observed.RetryAt?.Offset);
    }
    private static void AssertSdk(FailureInfo observed, FailureDetail final, FailureKind kind)
    {
        Assert.Equal(kind, observed.Kind);
        Assert.Equal(final.Code, observed.DiagnosticCode);
        Assert.Equal(final.Message, observed.Message); // Legacy SDK-only message semantics remain intact.
        Assert.Equal(final.HttpStatusCode is int status ? (HttpStatusCode)status : (HttpStatusCode?)null, observed.HttpStatusCode);
        Assert.Null(observed.NetworkStage);
        Assert.Equal(final.RetryAt, observed.RetryAt);
        Assert.Equal(final.RetryAt?.Offset, observed.RetryAt?.Offset);
    }

    private sealed record Outcome(FailureDetail? Failure, HostHttpDeferred? Deferred = null);
    private static SourceAdapterTests.TestWorker Worker(PluginDescription description, Func<CancellationToken, Task<Outcome>> read) =>
        new(async (request, _, token) =>
        {
            Outcome result = await read(token);
            return result.Failure != null ? new ThreadFailure(request.Thread, result.Failure)
                : new ThreadDeferred(request.Thread, result.Deferred!.RetryAt, result.Deferred.ReasonCode, result.Deferred.HttpStatusCode);
        }, description, async (request, _, token) =>
        {
            Outcome result = await read(token);
            return result.Failure != null ? new CatalogFailure(request.Board, result.Failure)
                : new CatalogDeferred(request.Board, result.Deferred!.RetryAt, result.Deferred.ReasonCode, result.Deferred.HttpStatusCode);
        });

    private static async Task<FailureInfo> ObserveAsync(bool catalog, SourceAdapterTests.TestWorker worker, int expectedCalls = 1)
    {
        using SourceAdapterTests.TemporaryProfile profile = new();
        using DenyHttp mediaHandler = new();
        using HttpClient media = new(mediaHandler);
        int calls = 0;
        // Count actual source adapter invocations without altering their result projection.
        var counted = new SourceAdapterTests.TestWorker((request, operation, token) =>
        {
            Interlocked.Increment(ref calls);
            return worker.ReadThreadAsync(request, operation, token);
        }, worker.Description, (request, operation, token) =>
        {
            Interlocked.Increment(ref calls);
            return worker.ReadCatalogAsync(request, operation, token);
        });
        using PluginSourceImageBoard source = new(counted.Description, "4chan", _ => Task.FromResult<IPluginWorkerClient>(counted), media);
        DownloadQueue queue = new(1, 1048576);
        IThread? thread = catalog ? null : source.GetThread(new Uri("https://boards.4chan.org/g/thread/123"));
        IBoard? board = catalog ? source.GetBoard(new Uri("https://boards.4chan.org/g/")) : null;
        INotifyPropertyChanged target = catalog ? board! : thread!;
        TaskCompletionSource<FailureInfo> reported = new(TaskCreationOptions.RunContinuationsAsynchronously);
        void Changed(object? sender, PropertyChangedEventArgs args)
        {
            if ((catalog ? board!.LastCheckFailure : thread!.LastCheckFailure) is FailureInfo failure)
                reported.TrySetResult(failure);
        }
        Task StopAsync() => catalog ? board!.StopMonitoringAsync() : thread!.StopMonitoringAsync();
        target.PropertyChanged += Changed;
        try
        {
            if (catalog) board!.StartMonitoring(3600);
            else thread!.StartMonitoring(queue, profile.Path, TimeSpan.FromHours(1));
            FailureInfo observed = await reported.Task.WaitAsync(Deadline);
            await StopAsync().WaitAsync(Deadline);
            if (catalog)
            {
                var snapshot = board!.GetSnapshot(profile.Path);
                Assert.Same(observed, snapshot.LastCheckFailure);
                Assert.Null(snapshot.RequestProgress);
            }
            else
            {
                var snapshot = thread!.GetSnapshot(new DownloadCounts(0, 0));
                Assert.Same(observed, snapshot.LastCheckFailure);
                Assert.Equal(ThreadSourceState.Active, snapshot.SourceState);
                Assert.Empty(snapshot.MediaFailures);
                Assert.Null(snapshot.RequestProgress);
            }
            Assert.Equal(expectedCalls, Volatile.Read(ref calls));
            Assert.Equal(0, mediaHandler.Requests);
            return observed;
        }
        finally
        {
            target.PropertyChanged -= Changed;
            try { await StopAsync().WaitAsync(Deadline); }
            finally { await queue.StopAllAsync().WaitAsync(Deadline); }
        }
    }

    private static HttpResponseMessage Response(HttpStatusCode status, string body = "{}") => new(status) { Content = new StringContent(body) };
    private static HttpResponseMessage Broken(Exception error, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StreamContent(new FailingStream(error)) };
    private sealed class Host : IDisposable
    {
        private readonly Probe _probe;
        private readonly HttpClient _transport;
        private readonly PluginHostHttp _http;
        internal PluginDescription Description { get; }
        internal int Requests => Volatile.Read(ref _probe.Requests);
        internal Host(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send)
        {
            PluginDescription original = new FourChanPlugin().Describe();
            Description = original with { Sources = new[] { original.Sources[0] with
            {
                HttpHosts = new[] { new HttpHostDeclaration("metadata.example", HttpHostPurpose.Metadata),
                    new HttpHostDeclaration("second.example", HttpHostPurpose.Metadata) }
            } } };
            _probe = new Probe(send);
            _transport = new HttpClient(_probe);
            _http = new PluginHostHttp(_transport, new RequestPolicyState(), Description.Sources);
        }
        internal Task<HostHttpResult> SendAsync(string host, CancellationToken token = default) =>
            _http.SendAsync(new HostHttpRequest("4chan", HostHttpMethod.Get, new Uri($"https://{host}/metadata")), token);
        public void Dispose() { _http.Dispose(); _transport.Dispose(); _probe.Dispose(); }
    }
    private sealed class Probe(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        internal int Requests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        { Interlocked.Increment(ref Requests); return send(request, token); }
    }
    private sealed class DenyHttp : HttpMessageHandler
    {
        internal int Requests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        { Interlocked.Increment(ref Requests); throw new InvalidOperationException("No media HTTP in causality fixtures."); }
    }
    private sealed class FailingStream(Exception failure) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw failure;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default) => ValueTask.FromException<int>(failure);
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
    private sealed class Gate
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private sealed class GatedContent(Gate gate) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => WriteAsync(stream, CancellationToken.None);
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken token) => WriteAsync(stream, token);
        private async Task WriteAsync(Stream stream, CancellationToken token)
        {
            gate.Entered.TrySetResult();
            await gate.Release.Task.WaitAsync(token);
            await stream.WriteAsync("{}"u8.ToArray(), token);
        }
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
    }
}
