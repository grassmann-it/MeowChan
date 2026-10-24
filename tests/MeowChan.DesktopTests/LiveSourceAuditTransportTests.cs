using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace MeowChan.DesktopTests;

public sealed class LiveSourceAuditTransportTests
{
    internal static LiveSourceSelection Selection => new("source", "plugin", "board", "123", "https://source.test/board/",
        "https://source.test/board/res/123.html", "https://source.test/board/catalog.json", "https://source.test/board/res/123.json",
        "https://source.test/original.png", "123-0", "123", 0, 3, ".png");

    [Fact]
    public async Task OnlyOneGetForEachExactEndpointReachesTheNetworkAndPreservesRealResponses()
    {
        int sends = 0;
        using LiveSourceAuditTransport gate = new(Selection, new(new CallbackHandler(request =>
        {
            sends++;
            HttpResponseMessage response = new(HttpStatusCode.Forbidden) { RequestMessage = request, Content = new ByteArrayContent([1, 2, 3]) };
            response.Headers.ETag = new EntityTagHeaderValue("\"real\"");
            response.Headers.TryAddWithoutValidation("Set-Cookie", "secret=not-evidence");
            response.Headers.TryAddWithoutValidation("Server", "nginx");
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(2));
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            return response;
        })));
        using HttpClient client = gate.Client();
        client.DefaultRequestHeaders.UserAgent.ParseAdd("MeowChan/1.0");
        using HttpResponseMessage response = await client.GetAsync(Selection.ThreadMetadataUrl);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("\"real\"", response.Headers.ETag!.Tag);
        Assert.Equal("application/json", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal(new byte[] { 1, 2, 3 }, await response.Content.ReadAsByteArrayAsync());
        Assert.Equal(Selection.ThreadMetadataUrl, response.RequestMessage!.RequestUri!.AbsoluteUri);
        await Assert.ThrowsAsync<LiveHarnessLimitException>(() => client.GetAsync(Selection.ThreadMetadataUrl));
        Assert.Equal(1, sends);
        Assert.Equal(1, gate.ActualNetworkSends);
        var received = Assert.Single(gate.Audit, row => row.Status == 403 && row.Bytes == 3 && row.Outcome == "http-status");
        Assert.Equal("nginx", received.ResponseHeaders!["Server"]);
        Assert.Equal("2", received.ResponseHeaders["Retry-After"]);
        Assert.Equal("\"real\"", received.ResponseHeaders["ETag"]);
        Assert.False(received.ResponseHeaders.ContainsKey("Set-Cookie"));
        Assert.False(received.ResponseHeaders.ContainsKey("Authorization"));
        Assert.Equal("MeowChan/1.0", received.EffectiveUserAgent);
    }

    [Fact]
    public async Task UnknownUrlsHeadRangeAndOfflineRequestsAreBlockedBeforeTheSocket()
    {
        int sends = 0;
        using LiveSourceAuditTransport gate = new(Selection, new(new CallbackHandler(request => { sends++; return new(HttpStatusCode.OK); })));
        using HttpClient client = gate.Client();
        await Assert.ThrowsAsync<LiveHarnessLimitException>(() => client.GetAsync("https://source.test/other.png"));
        await Assert.ThrowsAsync<LiveHarnessLimitException>(() => client.SendAsync(new(HttpMethod.Head, Selection.CatalogUrl)));
        using HttpRequestMessage range = new(HttpMethod.Get, Selection.OriginalUrl);
        range.Headers.Range = new RangeHeaderValue(0, 1);
        await Assert.ThrowsAsync<LiveHarnessLimitException>(() => client.SendAsync(range));
        Assert.Equal(0, sends);
        using LiveSourceAuditTransport offline = new(Selection) { RejectAll = true };
        using HttpClient blocked = offline.Client();
        await Assert.ThrowsAsync<LiveHarnessLimitException>(() => blocked.GetAsync(Selection.CatalogUrl));
        Assert.Equal(0, offline.ActualNetworkSends);
        Assert.Equal("harness-limit:offline-request", Assert.Single(offline.Audit).Outcome);
    }

    [Fact]
    public async Task SelectedParsedAttachmentMustPassBeforeOneOriginalCanBeFetched()
    {
        int validations = 0, sends = 0;
        using LiveSourceAuditTransport gate = new(Selection, new(new CallbackHandler(request =>
        {
            sends++;
            Assert.Equal(1, validations);
            return new(HttpStatusCode.OK) { RequestMessage = request, Content = new ByteArrayContent([1, 2, 3]) };
        })));
        gate.BeforeOriginal = _ => { validations++; return Task.CompletedTask; };
        using HttpClient client = gate.Client();
        Assert.Equal(new byte[] { 1, 2, 3 }, await client.GetByteArrayAsync(Selection.OriginalUrl));
        await Assert.ThrowsAsync<LiveHarnessLimitException>(() => client.GetAsync(Selection.OriginalUrl));
        Assert.Equal(1, validations);
        Assert.Equal(1, sends);
        Assert.Equal(3, gate.OriginalBytes);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(new byte[] { 1, 2, 3 })).ToLowerInvariant(), gate.OriginalSha256);
    }

    [Fact]
    public async Task AFailedParsedAttachmentGateConsumesNoNetworkRequest()
    {
        using LiveSourceAuditTransport gate = new(Selection);
        gate.BeforeOriginal = _ => throw new LiveHarnessLimitException("selected-attachment-mismatch");
        using HttpClient client = gate.Client();
        await Assert.ThrowsAsync<LiveHarnessLimitException>(() => client.GetAsync(Selection.OriginalUrl));
        Assert.Equal(0, gate.ActualNetworkSends);
        Assert.Equal("harness-limit:selected-attachment-mismatch", Assert.Single(gate.Audit).Outcome);
    }

    [Fact]
    public async Task DeclaredOversizeIsRejectedWithoutReadingAnyBodyBytes()
    {
        using CountingStream stream = new(64);
        using StreamContent content = new(stream);
        content.Headers.ContentLength = 65;
        await Assert.ThrowsAsync<LiveHarnessLimitException>(() => LiveSourceAuditTransport.ReadBoundedAsync(content, 64, CancellationToken.None));
        Assert.Equal(0, stream.Consumed);
    }

    [Fact]
    public async Task UnknownLengthOversizeNeverConsumesACapPlusOneByte()
    {
        using CountingStream stream = new(100);
        using StreamContent content = new(stream);
        var error = await Assert.ThrowsAsync<LiveHarnessLimitException>(() => LiveSourceAuditTransport.ReadBoundedAsync(content, 64, CancellationToken.None));
        Assert.Equal("body-reached-limit-without-eof", error.Code);
        Assert.Equal(64, stream.Consumed);
    }

    [Fact]
    public async Task UnknownLengthSmallBodiesAndCancellationAreHandledWithoutPayloadChanges()
    {
        using CountingStream stream = new(3);
        using StreamContent content = new(stream);
        Assert.Equal(new byte[] { 0, 0, 0 }, await LiveSourceAuditTransport.ReadBoundedAsync(content, 64, CancellationToken.None));
        using CountingStream cancelledStream = new(100);
        using StreamContent cancelledContent = new(cancelledStream);
        using CancellationTokenSource cancelled = new();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => LiveSourceAuditTransport.ReadBoundedAsync(cancelledContent, 64, cancelled.Token));
        Assert.Equal(0, cancelledStream.Consumed);
    }

    [Fact]
    public void AuditUrlsRemoveCredentialsQueriesAndFragments()
    {
        Assert.Equal("https://source.test/path/file.png", LiveSourceAuditTransport.AuditUrl("https://name:secret@source.test/path/file.png?token=secret#fragment"));
    }

    [Fact]
    public void ArchivedBytesMustHaveTheSelectedPngOrJpegSignature()
    {
        Assert.True(LiveSourceAcceptanceTests.HasImageSignature(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, ".png"));
        Assert.True(LiveSourceAcceptanceTests.HasImageSignature(new byte[] { 255, 216, 255, 1, 255, 217 }, ".jpg"));
        Assert.False(LiveSourceAcceptanceTests.HasImageSignature(new byte[] { 60, 104, 116, 109, 108, 62 }, ".jpg"));
        Assert.False(LiveSourceAcceptanceTests.HasImageSignature(new byte[] { 137, 80 }, ".png"));
    }

    [Theory]
    [InlineData("4chan")]
    [InlineData("8kun")]
    public async Task FirstApiThreadActionWaitsForAdmissionAndExecutesExactlyOnce(string sourceId)
    {
        AdmissionClock clock = new();
        int actions = 0;
        Task<int> action = LiveSourceAcceptanceTests.AdmitInitialThreadAsync(sourceId,
            () => { actions++; return Task.FromResult(7); }, clock);
        Assert.False(action.IsCompleted);
        Assert.Equal(0, actions);
        Assert.Equal(1, clock.Timers);
        Assert.Equal(TimeSpan.FromSeconds(1), clock.Due);
        clock.Fire();
        Assert.Equal(7, await action);
        Assert.Equal(1, actions);
    }

    [Fact]
    public async Task OtherSourcesDoNotCreateApiAdmissionTimersOrRepeatTheAction()
    {
        AdmissionClock clock = new();
        int actions = 0;
        int value = await LiveSourceAcceptanceTests.AdmitInitialThreadAsync("endchan",
            () => { actions++; return Task.FromResult(7); }, clock);
        Assert.Equal(7, value);
        Assert.Equal(1, actions);
        Assert.Equal(0, clock.Timers);
    }

    [Fact]
    public void AbsentSourceSelectorRequestsAllSixWhileAnExplicitSubsetStaysScoped()
    {
        Assert.Equal(6, LiveSourceAcceptanceTests.SelectSourceIds(null).Count);
        Assert.Equal(new[] { "8kun" }, LiveSourceAcceptanceTests.SelectSourceIds("8kun"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("8kun,8kun")]
    [InlineData("other")]
    public void EmptyDuplicateAndUnknownSourceSelectorsAreRejected(string selector)
    {
        Assert.Throws<ArgumentException>(() => LiveSourceAcceptanceTests.SelectSourceIds(selector));
    }

    [Theory]
    [InlineData(429, "http-429")]
    [InlineData(429, "PASS")]
    [InlineData(500, "http-500")]
    [InlineData(200, "catalog-readable-but-empty")]
    [InlineData(200, "assertion-or-pipeline:InvalidOperationException")]
    public async Task FailedCatalogNeverStartsTheThreadPhaseOrSendsAnotherRequest(int status, string catalogOutcome)
    {
        int phaseStarts = 0;
        using LiveSourceAuditTransport gate = new(Selection, new(new CallbackHandler(request =>
            new((HttpStatusCode)status) { RequestMessage = request, Content = new ByteArrayContent([1, 2, 3]) })));
        using HttpClient client = gate.Client();
        using HttpResponseMessage response = await client.GetAsync(Selection.CatalogUrl);
        LiveSourceResult result = new("source", "plugin", "board", "123", "123-0");
        result.Outcomes["catalog"] = catalogOutcome;

        await LiveSourceAcceptanceTests.RunThreadPhaseAfterCatalogAsync(result, gate.Audit.ToArray(), async () =>
        {
            phaseStarts++;
            using HttpResponseMessage thread = await client.GetAsync(Selection.ThreadMetadataUrl);
        });

        Assert.Equal(0, phaseStarts);
        Assert.Equal(1, gate.ActualNetworkSends);
        Assert.Equal(status, Assert.Single(gate.Audit).Status);
        Assert.Equal(catalogOutcome, result.Outcomes["catalog"]);
        foreach (string phase in new[] { "thread", "original", "archive" })
            Assert.Equal("NOT_REQUESTED:catalog-not-passed", result.Outcomes[phase]);
        Assert.False(result.ThreadCaptured);
        Assert.False(result.Passed);
    }

    [Fact]
    public async Task PassedCatalogStartsTheExistingThreadPhaseExactlyOnce()
    {
        int phaseStarts = 0;
        LiveSourceResult result = new("source", "plugin", "board", "123", "123-0");
        result.Outcomes["catalog"] = "PASS";
        LiveHttpAudit[] audit = [new("catalog", Selection.CatalogUrl, 200, 3, null, "http-200")];
        await LiveSourceAcceptanceTests.RunThreadPhaseAfterCatalogAsync(result, audit,
            () => { phaseStarts++; return Task.CompletedTask; });
        Assert.Equal(1, phaseStarts);
        Assert.Equal("PASS", result.Outcomes["catalog"]);
        Assert.False(result.Outcomes.ContainsKey("thread"));
    }

    private sealed class AdmissionClock : TimeProvider
    {
        private TimerCallback? _callback;
        private object? _state;
        internal int Timers { get; private set; }
        internal TimeSpan Due { get; private set; }
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Timers++; Due = dueTime; _callback = callback; _state = state;
            return new AdmissionTimer();
        }
        internal void Fire() => _callback!(_state);
        private sealed class AdmissionTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => false;
            public void Dispose() { }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class CallbackHandler(Func<HttpRequestMessage, HttpResponseMessage> callback) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => Task.FromResult(callback(request));
    }

    // Nonseekable, so StreamContent cannot invent a Content-Length for the chunked/unknown-length boundary cases.
    private sealed class CountingStream(int length) : Stream
    {
        internal int Consumed { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => Consumed; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count)
        {
            int size = Math.Min(count, length - Consumed);
            buffer.AsSpan(offset, size).Clear(); Consumed += size; return size;
        }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            int size = Math.Min(buffer.Length, length - Consumed);
            buffer.Span[..size].Clear(); Consumed += size; return ValueTask.FromResult(size);
        }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
