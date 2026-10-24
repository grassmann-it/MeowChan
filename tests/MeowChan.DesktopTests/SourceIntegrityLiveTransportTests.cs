using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace MeowChan.DesktopTests;

public sealed class SourceIntegrityLiveTransportTests
{
    private static LiveSourceSelection Selection => LiveSourceAuditTransportTests.Selection;
    private static readonly byte[] Payload = [1, 2, 3];

    [Fact]
    public async Task FourExactGetsRequireAnExplicitSecondPhaseAndPreserveBothBodies()
    {
        int sends = 0, strictChecks = 0, acceptedChecks = 0;
        HttpClient Network() => new(new CallbackHandler(request =>
        {
            sends++;
            Assert.Equal("MeowChan/source-integrity", request.Headers.UserAgent.ToString());
            Assert.Equal("identity", Assert.Single(request.Headers.AcceptEncoding).Value);
            HttpResponseMessage response = new(HttpStatusCode.OK)
            { RequestMessage = request, Content = new ByteArrayContent(Payload) };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
            response.Headers.ETag = new EntityTagHeaderValue("\"provider-unchanged\"");
            return response;
        }));
        using SourceIntegrityLiveTransport gate = new(Selection, Network(), Network());
        gate.BeforeStrictOriginal = _ => { strictChecks++; return Task.CompletedTask; };
        using HttpClient client = gate.Client();
        client.DefaultRequestHeaders.UserAgent.ParseAdd("MeowChan/source-integrity");
        client.DefaultRequestHeaders.AcceptEncoding.ParseAdd("identity");
        Assert.Equal(Payload, await client.GetByteArrayAsync(Selection.CatalogUrl));
        Assert.Equal(Payload, await client.GetByteArrayAsync(Selection.ThreadMetadataUrl));
        Assert.Equal(Payload, await client.GetByteArrayAsync(Selection.OriginalUrl));
        Assert.Equal(3, sends);
        await Assert.ThrowsAsync<LiveHarnessLimitException>(() => client.GetAsync(Selection.OriginalUrl));
        Assert.Equal(3, sends);
        gate.ArmSecondAfterObservedStrictFailure(_ => { acceptedChecks++; return Task.CompletedTask; });
        using HttpResponseMessage accepted = await client.GetAsync(Selection.OriginalUrl);
        Assert.Equal(Payload, await accepted.Content.ReadAsByteArrayAsync());
        Assert.Equal("\"provider-unchanged\"", accepted.Headers.ETag!.Tag);
        Assert.Equal("image/jpeg", accepted.Content.Headers.ContentType!.MediaType);
        Assert.True(gate.SameOriginalBodies);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(Payload)).ToLowerInvariant(), gate.AcceptedOriginalSha256);
        Assert.Equal(Convert.ToBase64String(MD5.HashData(Payload)), gate.OriginalMd5Base64);
        Assert.Equal(1, strictChecks); Assert.Equal(1, acceptedChecks);
        Assert.Equal(4, gate.ActualNetworkSends);
        await Assert.ThrowsAsync<LiveHarnessLimitException>(() => client.GetAsync(Selection.CatalogUrl));
        await Assert.ThrowsAsync<LiveHarnessLimitException>(() => client.GetAsync(Selection.ThreadMetadataUrl));
        await Assert.ThrowsAsync<LiveHarnessLimitException>(() => client.GetAsync(Selection.OriginalUrl));
        Assert.Equal(4, sends);
        Assert.Equal(3, gate.StrictAudit.Count(row => row.Status == 200));
        Assert.Single(gate.AcceptedAudit, row => row.Status == 200);
    }

    [Fact]
    public async Task SecondPhaseCannotBeArmedBeforeACompletedFirstBodyOrArmedTwice()
    {
        using SourceIntegrityLiveTransport gate = new(Selection, Network(Payload), Network(Payload));
        Assert.Throws<LiveHarnessLimitException>(() => gate.ArmSecondAfterObservedStrictFailure(_ => Task.CompletedTask));
        gate.BeforeStrictOriginal = _ => Task.CompletedTask;
        using HttpClient client = gate.Client();
        await client.GetByteArrayAsync(Selection.OriginalUrl);
        gate.ArmSecondAfterObservedStrictFailure(_ => Task.CompletedTask);
        Assert.Throws<LiveHarnessLimitException>(() => gate.ArmSecondAfterObservedStrictFailure(_ => Task.CompletedTask));
        Assert.Equal(1, gate.ActualNetworkSends);
    }

    [Fact]
    public async Task CurrentHostPermissionCheckRunsBeforeTheSecondSocketAndCannotBeRetried()
    {
        using SourceIntegrityLiveTransport gate = new(Selection, Network(Payload), Network(Payload));
        gate.BeforeStrictOriginal = _ => Task.CompletedTask;
        using HttpClient client = gate.Client();
        await client.GetByteArrayAsync(Selection.OriginalUrl);
        gate.ArmSecondAfterObservedStrictFailure(_ => throw new InvalidOperationException("host grant revoked"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetAsync(Selection.OriginalUrl));
        Assert.Equal(1, gate.ActualNetworkSends);
        await Assert.ThrowsAsync<LiveHarnessLimitException>(() => client.GetAsync(Selection.OriginalUrl));
        Assert.Equal(1, gate.ActualNetworkSends);
    }

    [Fact]
    public async Task ChangedSecondBodyFailsBeforeItCanReachProductionAndNeverAddsAThirdGet()
    {
        using SourceIntegrityLiveTransport gate = new(Selection, Network(Payload), Network([1, 2, 4]));
        gate.BeforeStrictOriginal = _ => Task.CompletedTask;
        using HttpClient client = gate.Client();
        await client.GetByteArrayAsync(Selection.OriginalUrl);
        gate.ArmSecondAfterObservedStrictFailure(_ => Task.CompletedTask);
        LiveHarnessLimitException failure = await Assert.ThrowsAsync<LiveHarnessLimitException>(() => client.GetAsync(Selection.OriginalUrl));
        Assert.Equal("original-body-changed-between-phases", failure.Code);
        Assert.False(gate.SameOriginalBodies);
        await Assert.ThrowsAsync<LiveHarnessLimitException>(() => client.GetAsync(Selection.OriginalUrl));
        Assert.Equal(2, gate.ActualNetworkSends);
    }

    [Fact]
    public async Task OfflineUnknownHeadAndRangeRequestsHaveNoNetworkSideEffects()
    {
        using SourceIntegrityLiveTransport gate = new(Selection, Network(Payload), Network(Payload));
        using HttpClient client = gate.Client();
        await Assert.ThrowsAsync<LiveHarnessLimitException>(() => client.GetAsync("https://source.test/unselected.jpg"));
        await Assert.ThrowsAsync<LiveHarnessLimitException>(() => client.SendAsync(new(HttpMethod.Head, Selection.CatalogUrl)));
        using HttpRequestMessage range = new(HttpMethod.Get, Selection.OriginalUrl);
        range.Headers.Range = new RangeHeaderValue(0, 1);
        await Assert.ThrowsAsync<LiveHarnessLimitException>(() => client.SendAsync(range));
        Assert.Equal(0, gate.ActualNetworkSends);
        using SourceIntegrityLiveTransport offline = new(Selection, Network(Payload), Network(Payload)) { RejectAll = true };
        using HttpClient blocked = offline.Client();
        await Assert.ThrowsAsync<LiveHarnessLimitException>(() => blocked.GetAsync(Selection.OriginalUrl));
        Assert.Equal(0, offline.ActualNetworkSends);
        Assert.Equal("harness-limit:offline-request", Assert.Single(offline.Denials).Outcome);
    }

    [Fact]
    public async Task CallerCancellationDoesNotConsumeARequestOrRunAParsedAttachmentGate()
    {
        int gates = 0;
        using SourceIntegrityLiveTransport gate = new(Selection, Network(Payload), Network(Payload));
        gate.BeforeStrictOriginal = _ => { gates++; return Task.CompletedTask; };
        using HttpClient client = gate.Client();
        using CancellationTokenSource cancelled = new(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetAsync(Selection.OriginalUrl, cancelled.Token));
        Assert.Equal(0, gates); Assert.Equal(0, gate.ActualNetworkSends);
        Assert.Equal(Payload, await client.GetByteArrayAsync(Selection.OriginalUrl));
        Assert.Equal(1, gate.ActualNetworkSends);
    }

    [Fact]
    public async Task Non200OriginalCannotArmTheSecondPhase()
    {
        using HttpClient rejected = new(new CallbackHandler(request =>
            new(HttpStatusCode.PartialContent) { RequestMessage = request, Content = new ByteArrayContent(Payload) }));
        using SourceIntegrityLiveTransport gate = new(Selection, rejected, Network(Payload));
        gate.BeforeStrictOriginal = _ => Task.CompletedTask;
        using HttpClient client = gate.Client();
        using HttpResponseMessage response = await client.GetAsync(Selection.OriginalUrl);
        Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
        Assert.Null(gate.StrictOriginalSha256);
        Assert.Throws<LiveHarnessLimitException>(() => gate.ArmSecondAfterObservedStrictFailure(_ => Task.CompletedTask));
        Assert.Equal(1, gate.ActualNetworkSends);
    }

    [Fact]
    public async Task ConcurrentSecondRequestsCanUseOnlyOneNetworkSend()
    {
        int permissionChecks = 0;
        using SourceIntegrityLiveTransport gate = new(Selection, Network(Payload), Network(Payload));
        gate.BeforeStrictOriginal = _ => Task.CompletedTask;
        using HttpClient client = gate.Client();
        await client.GetByteArrayAsync(Selection.OriginalUrl);
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        gate.ArmSecondAfterObservedStrictFailure(async token =>
        {
            Interlocked.Increment(ref permissionChecks);
            entered.SetResult();
            await release.Task.WaitAsync(token);
        });
        Task<HttpResponseMessage> second = client.GetAsync(Selection.OriginalUrl);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            await Assert.ThrowsAsync<LiveHarnessLimitException>(() => client.GetAsync(Selection.OriginalUrl));
            Assert.Equal(1, gate.ActualNetworkSends);
        }
        finally { release.TrySetResult(); }
        using HttpResponseMessage response = await second.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(Payload, await response.Content.ReadAsByteArrayAsync());
        Assert.Equal(1, permissionChecks);
        Assert.Equal(2, gate.ActualNetworkSends);
    }

    private static HttpClient Network(byte[] body) => new(new CallbackHandler(request =>
        new(HttpStatusCode.OK) { RequestMessage = request, Content = new ByteArrayContent(body) }));
    private sealed class CallbackHandler(Func<HttpRequestMessage, HttpResponseMessage> callback) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(callback(request));
    }
}
