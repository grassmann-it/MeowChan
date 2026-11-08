using System.Net;
using System.Net.Sockets;
using System.Reflection;
using MeowChan.Http;
using MeowChan.OfficialSources.FourChan;
using MeowChan.PluginHost.Http;
using MeowChan.PluginSdk;

namespace MeowChan.SourceAdapter.Tests;

// Real host/policy path with synthetic handlers; no network, worker process or SDK changes.
public sealed class PluginHostHttpDiagnosticFactsTests
{
    private const string Secret = "SECRET-url-body-token";
    private static readonly Type HistoryType = typeof(PluginHostHttp).GetNestedType("MetadataHistory", BindingFlags.NonPublic)!;
    private const BindingFlags Members = BindingFlags.Instance | BindingFlags.NonPublic;
    private static IDisposable Begin() => (IDisposable)Activator.CreateInstance(HistoryType, nonPublic: true)!;
    private static Exception? Failure(object history) => (Exception?)HistoryType.GetProperty("Failure", Members)!.GetValue(history);
    private static bool Initial(object history) => (bool)HistoryType.GetProperty("InitialNoSend", Members)!.GetValue(history)!;
    private static readonly HostHttpRequest Request = new("4chan", HostHttpMethod.Get,
        new Uri("https://a.4cdn.org/g/catalog.json?token=" + Secret));

    private static FailureInfo Facts(object history)
    {
        Exception error = Assert.IsAssignableFrom<Exception>(Failure(history));
        FailureInfo facts = Assert.IsAssignableFrom<IFailureInfoProvider>(error).FailureInfo;
        Assert.Null(error.InnerException);
        Assert.DoesNotContain(Secret, error.ToString());
        Assert.DoesNotContain(Secret, facts.ToString());
        Assert.DoesNotContain("https://", facts.Message);
        return facts;
    }

    [Theory]
    [InlineData(403, false)]
    [InlineData(404, false)]
    [InlineData(429, true)]
    [InlineData(503, true)]
    public async Task ResponseStatusSurvivesPolicyAndSubsequentWait(int status, bool deferred)
    {
        using Probe probe = new((request, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status)
        {
            RequestMessage = request, Content = new StringContent(Secret), ReasonPhrase = Secret
        }));
        using HttpClient transport = new(probe);
        using PluginHostHttp host = Create(transport);
        using IDisposable history = Begin();
        HostHttpResult result = await host.SendAsync(Request);
        if (deferred) Assert.IsType<HostHttpDeferred>(result);
        else
        {
            HostHttpResponse response = Assert.IsType<HostHttpResponse>(result);
            Assert.Equal(status, response.StatusCode);
            // The plugin result still carries the payload; the diagnostic must not.
            Assert.Equal(Secret, System.Text.Encoding.UTF8.GetString(response.Body));
        }
        FailureInfo facts = Facts(history);
        Assert.Equal(FailureKind.Http, facts.Kind);
        Assert.Equal((HttpStatusCode)status, facts.HttpStatusCode);
        Assert.Null(facts.NetworkStage);
        Assert.Equal($"http-{status}", facts.DiagnosticCode);
        Assert.False(Initial(history));
        Assert.IsType<HostHttpDeferred>(await host.SendAsync(Request));
        Assert.Equal(facts, Facts(history));
        Assert.Equal(1, probe.Sends);
    }

    [Theory]
    [InlineData(HttpRequestError.NameResolutionError, FailureNetworkStage.NameResolution)]
    [InlineData(HttpRequestError.ConnectionError, FailureNetworkStage.Connection)]
    [InlineData(HttpRequestError.SecureConnectionError, FailureNetworkStage.SecureConnection)]
    [InlineData(HttpRequestError.Unknown, null)]
    public async Task TypedTransportFactsSurviveRetryDeferralWithoutTextInference(HttpRequestError error, FailureNetworkStage? stage)
    {
        using Probe probe = new((_, _) => throw new HttpRequestException(error, "DNS TLS connection " + Secret));
        using HttpClient transport = new(probe);
        using PluginHostHttp host = Create(transport);
        using IDisposable history = Begin();
        Assert.IsType<HostHttpDeferred>(await host.SendAsync(Request));
        FailureInfo facts = Facts(history);
        Assert.Equal(stage, facts.NetworkStage);
        Assert.Equal(stage == null ? FailureKind.Unknown : FailureKind.Network, facts.Kind);
        Assert.Null(facts.HttpStatusCode);
        Assert.False(Initial(history));
        Assert.Equal(1, probe.Sends);
    }

    [Fact]
    public async Task TypedSocketInnerEvidenceIsProjectedWithoutRetainingInnerException()
    {
        using Probe probe = new((_, _) => throw new HttpRequestException(Secret,
            new SocketException((int)SocketError.HostNotFound)));
        using HttpClient transport = new(probe);
        using PluginHostHttp host = Create(transport);
        using IDisposable history = Begin();
        Assert.IsType<HostHttpDeferred>(await host.SendAsync(Request));
        Assert.Equal(FailureNetworkStage.NameResolution, Facts(history).NetworkStage);
        Assert.Equal("socket-HostNotFound", Facts(history).DiagnosticCode);
        Assert.Equal(1, probe.Sends);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BodyFailureOuterCatchPreservesTypedEvidenceButPlainIoStaysUnknown(bool typed)
    {
        using Probe probe = new((request, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            RequestMessage = request,
            Content = new StreamContent(new BrokenBody(typed ? new HttpIOException(HttpRequestError.ResponseEnded, Secret) : new IOException(Secret)))
        }));
        using HttpClient transport = new(probe);
        using PluginHostHttp host = Create(transport);
        using IDisposable history = Begin();
        Assert.IsType<HostHttpFailure>(await host.SendAsync(Request));
        FailureInfo facts = Facts(history);
        Assert.Equal(typed ? FailureKind.Network : FailureKind.Unknown, facts.Kind);
        Assert.Equal(typed ? "http-response-ended" : null, facts.DiagnosticCode);
        Assert.Null(facts.NetworkStage);
        Assert.Equal(1, probe.Sends);
    }

    [Theory]
    [InlineData(403)]
    [InlineData(404)]
    public async Task BodyFailureDoesNotEraseStatusFromSameResponse(int status)
    {
        using Probe probe = new((request, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status)
        {
            RequestMessage = request,
            Content = new StreamContent(new BrokenBody(new HttpIOException(HttpRequestError.ResponseEnded, Secret)))
        }));
        using HttpClient transport = new(probe);
        using PluginHostHttp host = Create(transport);
        using IDisposable history = Begin();
        Assert.IsType<HostHttpFailure>(await host.SendAsync(Request));
        FailureInfo facts = Facts(history);
        Assert.Equal((HttpStatusCode)status, facts.HttpStatusCode);
        Assert.Equal(FailureKind.Network, facts.Kind);
        Assert.Equal("http-response-ended", facts.DiagnosticCode);
        Assert.Null(facts.NetworkStage);
        Assert.IsType<HostHttpDeferred>(await host.SendAsync(Request));
        Assert.Equal(facts, Facts(history));
        Assert.Equal(1, probe.Sends);
    }

    [Fact]
    public async Task OversizedBodyGuardRetainsObservedStatusWithoutReadingBody()
    {
        using Probe probe = new((request, _) =>
        {
            // This sentinel must never be read when the length guard rejects the body.
            HttpContent content = new StreamContent(new BrokenBody(new InvalidOperationException("Unexpected body read.")));
            content.Headers.ContentLength = SdkLimits.MaximumMetadataBytes + 1L;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden) { RequestMessage = request, Content = content });
        });
        using HttpClient transport = new(probe);
        using PluginHostHttp host = Create(transport);
        using IDisposable history = Begin();
        Assert.Equal("metadata-too-large", Assert.IsType<HostHttpFailure>(await host.SendAsync(Request)).Failure.Code);
        Assert.Equal(HttpStatusCode.Forbidden, Facts(history).HttpStatusCode);
        Assert.Equal("metadata-too-large", Facts(history).DiagnosticCode);
        Assert.Null(Facts(history).NetworkStage);
        Assert.Equal(1, probe.Sends);
    }

    [Fact]
    public async Task ExceptionStatusIsEvidenceEvenWithMisleadingTextAndTransportCategory()
    {
        using Probe probe = new((_, _) => throw new HttpRequestException(HttpRequestError.ConnectionError,
            "HTTP 404 DNS " + Secret, statusCode: HttpStatusCode.Forbidden));
        using HttpClient transport = new(probe);
        using PluginHostHttp host = Create(transport);
        using IDisposable history = Begin();
        Assert.IsType<HostHttpDeferred>(await host.SendAsync(Request));
        FailureInfo facts = Facts(history);
        Assert.Equal(FailureKind.Http, facts.Kind);
        Assert.Equal(HttpStatusCode.Forbidden, facts.HttpStatusCode);
        Assert.Null(facts.NetworkStage);
        Assert.Equal(1, probe.Sends);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TypedTransportIoSurvivesOuterCatchWhileUntypedIoStaysUnknown(bool typed)
    {
        using Probe probe = new((_, _) => throw (typed
            ? new HttpIOException(HttpRequestError.ConnectionError, Secret) : new IOException("DNS TLS " + Secret)));
        using HttpClient transport = new(probe);
        using PluginHostHttp host = Create(transport);
        using IDisposable history = Begin();
        Assert.Equal("http-network", Assert.IsType<HostHttpFailure>(await host.SendAsync(Request)).Failure.Code);
        FailureInfo facts = Facts(history);
        Assert.Equal(typed ? FailureKind.Network : FailureKind.Unknown, facts.Kind);
        Assert.Equal(typed ? FailureNetworkStage.Connection : (FailureNetworkStage?)null, facts.NetworkStage);
        Assert.Null(facts.HttpStatusCode);
        Assert.Equal(1, probe.Sends);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TransportCancellationIsNotPromotedToTimeoutWithoutEvidence(bool timeout)
    {
        using Probe probe = new((_, _) => throw (timeout
            ? (Exception)new TimeoutException(Secret) : new OperationCanceledException(Secret)));
        using HttpClient transport = new(probe);
        using PluginHostHttp host = Create(transport);
        using IDisposable history = Begin();
        Assert.IsType<HostHttpDeferred>(await host.SendAsync(Request));
        FailureInfo facts = Facts(history);
        Assert.Equal(timeout ? FailureKind.Timeout : FailureKind.Unknown, facts.Kind);
        Assert.Equal(timeout ? null : "operation-cancelled", facts.DiagnosticCode);
        Assert.Null(facts.NetworkStage);
        Assert.Null(facts.HttpStatusCode);
        Assert.Equal(1, probe.Sends);
    }

    [Fact]
    public async Task UnrelatedBodyCancellationDoesNotInventADeadline()
    {
        using Probe probe = new((request, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            RequestMessage = request, Content = new StreamContent(new BrokenBody(new OperationCanceledException(Secret)))
        }));
        using HttpClient transport = new(probe);
        using PluginHostHttp host = Create(transport);
        using IDisposable history = Begin();
        // Existing SDK outcome is unchanged, but the passive evidence is conservative.
        Assert.Equal("http-timeout", Assert.IsType<HostHttpFailure>(await host.SendAsync(Request)).Failure.Code);
        FailureInfo facts = Facts(history);
        Assert.Equal(FailureKind.Unknown, facts.Kind);
        Assert.Equal("operation-cancelled", facts.DiagnosticCode);
        Assert.Null(facts.NetworkStage);
        Assert.Null(facts.HttpStatusCode);
        Assert.Equal(1, probe.Sends);
    }

    [Fact]
    public async Task LaterSendDoesNotBorrowStatusFromEarlierResponse()
    {
        int count = 0;
        using Probe probe = new((request, _) => Task.FromResult(new HttpResponseMessage(count++ == 0 ? HttpStatusCode.Forbidden : HttpStatusCode.OK)
        {
            RequestMessage = request,
            Content = count == 1 ? new StringContent(Secret) : new StreamContent(new BrokenBody(new IOException(Secret)))
        }));
        using HttpClient transport = new(probe);
        FrozenClock clock = new();
        using PluginHostHttp host = Create(transport, clock);
        using IDisposable history = Begin();
        Assert.IsType<HostHttpResponse>(await host.SendAsync(Request));
        Assert.Equal(HttpStatusCode.Forbidden, Facts(history).HttpStatusCode);
        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.IsType<HostHttpFailure>(await host.SendAsync(Request));
        Assert.Null(Facts(history).HttpStatusCode);
        Assert.Equal(FailureKind.Unknown, Facts(history).Kind);
        Assert.False(Initial(history));
        Assert.Equal(2, probe.Sends);
    }

    [Fact]
    public async Task HealthyPreSendWaitRemainsNeutralAndDoesNotProbe()
    {
        using Probe probe = new((request, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { RequestMessage = request, Content = new StringContent("[]") }));
        using HttpClient transport = new(probe);
        using PluginHostHttp host = Create(transport);
        Assert.IsType<HostHttpResponse>(await host.SendAsync(Request));
        using IDisposable history = Begin();
        Assert.IsType<HostHttpDeferred>(await host.SendAsync(Request));
        Assert.True(Initial(history));
        Assert.Null(Failure(history));
        Assert.Equal(1, probe.Sends);
    }

    [Fact]
    public async Task CallerCancellationDuringSendIsNotRecordedAsNetworkTimeout()
    {
        using CancellationTokenSource caller = new();
        using Probe probe = new((_, token) =>
        {
            caller.Cancel();
            token.ThrowIfCancellationRequested();
            throw new InvalidOperationException("Linked caller cancellation did not propagate.");
        });
        using HttpClient transport = new(probe);
        using PluginHostHttp host = Create(transport);
        using IDisposable history = Begin();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => host.SendAsync(Request, caller.Token));
        Assert.Null(Failure(history));
        Assert.False(Initial(history));
        Assert.Equal(1, probe.Sends);
    }

    [Fact]
    public async Task CallerCancellationBeforeSendDoesNotCreateFailureOrAdmissionEvidence()
    {
        using CancellationTokenSource caller = new();
        caller.Cancel();
        using Probe probe = new((_, _) => throw new InvalidOperationException("Cancelled caller must not send."));
        using HttpClient transport = new(probe);
        using PluginHostHttp host = Create(transport);
        using IDisposable history = Begin();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => host.SendAsync(Request, caller.Token));
        Assert.Null(Failure(history));
        Assert.False(Initial(history));
        Assert.Equal(0, probe.Sends);
    }

    [Fact]
    public async Task ValidationFailureRetainsProducerCodeWithoutCreatingTransportEvidence()
    {
        using Probe probe = new((_, _) => throw new InvalidOperationException("Invalid source must not send."));
        using HttpClient transport = new(probe);
        using PluginHostHttp host = Create(transport);
        using IDisposable history = Begin();
        Assert.Equal("http-source", Assert.IsType<HostHttpFailure>(await host.SendAsync(Request with { SourceId = Secret })).Failure.Code);
        FailureInfo facts = Facts(history);
        Assert.Equal(FailureKind.Unknown, facts.Kind);
        Assert.Equal("http-source", facts.DiagnosticCode);
        Assert.Null(facts.NetworkStage);
        Assert.Null(facts.HttpStatusCode);
        Assert.False(Initial(history));
        Assert.Equal(0, probe.Sends);
    }

    private static PluginHostHttp Create(HttpClient transport, TimeProvider? clock = null) => new(transport,
        new RequestPolicyState(clock ?? new FrozenClock(), () => 0), new FourChanPlugin().Describe().Sources);

    // Admission/cooldown time advances only explicitly; no sleeps or live requests.
    private sealed class FrozenClock : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref _ticks);
        public override DateTimeOffset GetUtcNow() => new DateTimeOffset(2026, 11, 8, 0, 0, 0, TimeSpan.Zero).AddTicks(GetTimestamp());
        internal void Advance(TimeSpan delta) => Interlocked.Add(ref _ticks, delta.Ticks);
    }

    private sealed class Probe(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        internal int Sends;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Sends++;
            return send(request, token);
        }
    }

    // StreamContent exposes the actual read failure. HttpContent's default buffering
    // would wrap SerializeToStreamAsync IO failures before the host reads the body.
    private sealed class BrokenBody(Exception failure) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw failure;
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token)
            => Task.FromException<int>(failure);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => ValueTask.FromException<int>(failure);
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
