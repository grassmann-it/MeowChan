using System.Net;
using System.Reflection;
using MeowChan.Http;
using MeowChan.OfficialSources.FourChan;
using MeowChan.PluginHost.Contracts;
using MeowChan.PluginHost.Http;
using MeowChan.PluginSdk;

namespace MeowChan.SourceAdapter.Tests;

// Contract sidecars with a fake transport, NOT production WorkerConnection/IPC coverage.
// Reflection intentionally avoids adding public API or another InternalsVisibleTo grant.
public sealed class MetadataHistoryTests
{
    private static readonly Type HistoryType = typeof(PluginHostHttp).GetNestedType("MetadataHistory", BindingFlags.NonPublic)!;
    private const BindingFlags Internal = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static;
    private static object? Current => HistoryType.GetProperty("Current", Internal)!.GetValue(null);
    private static bool Initial(object history) => (bool)HistoryType.GetProperty("InitialNoSend", Internal)!.GetValue(history)!;
    private static IDisposable NewHistory() => (IDisposable)Activator.CreateInstance(HistoryType, nonPublic: true)!;
    private static void Bind(object history, PluginOperationContext context) => HistoryType.GetMethod("Bind", Internal)!.Invoke(history, new object[] { context });
    private static IDisposable Resume(PluginOperationContext context) => (IDisposable)HistoryType.GetMethod("Resume", Internal)!.Invoke(null, new object[] { context })!;

    private static Task Detached(Func<Task> action)
    {
        // Suppression is disposed synchronously before awaiting; no AsyncLocal can flow in.
        using (ExecutionContext.SuppressFlow()) return Task.Run(action);
    }

    [Fact]
    public async Task InitialNoSendWaitResumesOriginalReferenceAcrossDetachedFlow()
    {
        using Harness harness = new();
        Assert.IsType<HostHttpResponse>(await harness.Get("a.4cdn.org", "/warm"));
        using IDisposable history = NewHistory();
        var context = new PluginOperationContext("resource", 1);
        Bind(history, context);
        await Detached(async () =>
        {
            Assert.Null(Current);
            using (Resume(context))
            {
                Assert.Same(history, Current);
                Assert.IsType<HostHttpDeferred>(await harness.Get("a.4cdn.org", "/initial"));
            }
            Assert.Null(Current);
        });
        Assert.True(Initial(history));
        Assert.Equal(1, harness.Sends);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RedirectAfterSendIsNotInitialAdmission(bool crossHost)
    {
        using Harness harness = new(crossHost ? "8kun.top" : "a.4cdn.org");
        if (crossHost) Assert.IsType<HostHttpResponse>(await harness.Get("8kun.top", "/warm"));
        using IDisposable history = NewHistory();
        var context = new PluginOperationContext("redirect", 2);
        Bind(history, context);
        await Detached(async () =>
        {
            using (Resume(context))
                Assert.IsType<HostHttpDeferred>(await harness.Get("a.4cdn.org", "/redirect"));
            Assert.Null(Current);
        });
        Assert.False(Initial(history));
        Assert.Equal(crossHost ? 2 : 1, harness.Sends);
    }

    [Fact]
    public async Task CatalogPageRequestsShareOriginalOperationHistoryAcrossDetachedCallbacks()
    {
        using Harness harness = new();
        using IDisposable history = NewHistory();
        var context = new PluginOperationContext("catalog", 3);
        Bind(history, context);
        await Detached(async () =>
        {
            using (Resume(context)) Assert.IsType<HostHttpResponse>(await harness.Get("a.4cdn.org", "/page1"));
            Assert.Null(Current);
        });
        await Detached(async () =>
        {
            using (Resume(context)) Assert.IsType<HostHttpDeferred>(await harness.Get("a.4cdn.org", "/page2"));
            Assert.Null(Current);
        });
        Assert.Equal(1, harness.Sends);
        Assert.False(Initial(history));
    }

    [Fact]
    public async Task EqualValuedUntrustedContextCannotAcquireBindingOrInheritAmbientHistory()
    {
        using IDisposable history = NewHistory();
        var original = new PluginOperationContext("same-resource", 4);
        var forged = original with { };
        Assert.Equal(original, forged);
        Assert.NotSame(original, forged);
        Bind(history, original);
        using (Resume(forged)) Assert.Null(Current); // Must mask, not inherit ambient history.
        Assert.Same(history, Current);
        await Detached(() =>
        {
            using (Resume(forged)) Assert.Null(Current);
            using (Resume(original)) Assert.Same(history, Current);
            Assert.Null(Current);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task CancellationBeforeDispatchCannotBecomeAdmissionOrLeakIntoNextContext()
    {
        using Harness harness = new();
        using IDisposable history = NewHistory();
        var context = new PluginOperationContext("cancelled", 5);
        Bind(history, context);
        using CancellationTokenSource cancelled = new();
        cancelled.Cancel();
        await Detached(async () =>
        {
            using (Resume(context))
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => harness.Get("a.4cdn.org", "/cancel", cancelled.Token));
            Assert.Null(Current);
            using (Resume(new PluginOperationContext("next", 6))) Assert.Null(Current);
        });
        Assert.Equal(0, harness.Sends);
        Assert.False(Initial(history));
    }

    [Fact]
    public async Task DisposedOperationBindingCannotBeResumedByLateCallback()
    {
        IDisposable history = NewHistory();
        var context = new PluginOperationContext("ended", 7);
        Bind(history, context);
        history.Dispose();
        await Detached(() =>
        {
            using (Resume(context)) Assert.Null(Current);
            Assert.Null(Current);
            return Task.CompletedTask;
        });
    }

    private sealed class Harness : IDisposable
    {
        private readonly HttpClient _transport;
        private readonly PluginHostHttp _http;
        private int _sends;
        internal int Sends => Volatile.Read(ref _sends);
        internal Harness(string? redirectHost = null)
        {
            SourceDescription source = new FourChanPlugin().Describe().Sources[0] with
            {
                HttpHosts = new[] { new HttpHostDeclaration("a.4cdn.org", HttpHostPurpose.Metadata),
                    new HttpHostDeclaration("8kun.top", HttpHostPurpose.Metadata) }
            };
            _transport = new HttpClient(new Transport(() => Interlocked.Increment(ref _sends), redirectHost));
            _http = new PluginHostHttp(_transport, new RequestPolicyState(), new[] { source });
        }
        internal Task<HostHttpResult> Get(string host, string path, CancellationToken token = default) =>
            _http.SendAsync(new HostHttpRequest("4chan", HostHttpMethod.Get, new Uri($"https://{host}{path}")), token);
        public void Dispose() { _http.Dispose(); _transport.Dispose(); }
    }

    private sealed class Transport(Action sent, string? redirectHost) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            sent();
            HttpResponseMessage response = new(request.RequestUri!.AbsolutePath == "/redirect" ? HttpStatusCode.Found : HttpStatusCode.OK)
                { RequestMessage = request, Content = new StringContent("{}") };
            if (response.StatusCode == HttpStatusCode.Found) response.Headers.Location = new Uri($"https://{redirectHost}/destination");
            return Task.FromResult(response);
        }
    }
}
