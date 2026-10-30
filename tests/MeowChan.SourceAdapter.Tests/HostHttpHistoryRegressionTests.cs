using System.Net;
using System.Reflection;
using MeowChan.Http;
using MeowChan.OfficialSources.FourChan;
using MeowChan.PluginHost.Http;
using MeowChan.PluginSdk;

namespace MeowChan.SourceAdapter.Tests;

public sealed class HostHttpHistoryRegressionTests
{
    private static readonly Type History = typeof(PluginHostHttp).GetNestedType("MetadataHistory", BindingFlags.NonPublic)!;
    private const BindingFlags Members = BindingFlags.NonPublic | BindingFlags.Instance;
    private static IDisposable Begin() => (IDisposable)Activator.CreateInstance(History, nonPublic: true)!;
    private static bool Initial(object history) => (bool)History.GetProperty("InitialNoSend", Members)!.GetValue(history)!;
    private static Exception? Failure(object history) => (Exception?)History.GetProperty("Failure", Members)!.GetValue(history);
    private static HostHttpRequest Request(string? userAgent = null) => new("4chan", HostHttpMethod.Get,
        new Uri("https://a.4cdn.org/g/catalog.json"), Headers: userAgent == null ? null : new Dictionary<string, string> { ["User-Agent"] = userAgent });

    [Theory]
    [InlineData(null, null, "MeowChan")]
    [InlineData("ExampleClient/1.0", null, "ExampleClient/1.0")]
    [InlineData("ExampleClient/1.0", "ExplicitClient/2.0", "ExplicitClient/2.0")]
    [InlineData(null, "ExplicitClient/2.0", "ExplicitClient/2.0")]
    public async Task WrapperRetainsConfiguredAndExplicitIdentityAndBorrowsTransport(string? configured, string? explicitValue, string expected)
    {
        using Probe handler = new();
        using HttpClient transport = new(handler);
        if (configured != null) transport.DefaultRequestHeaders.UserAgent.ParseAdd(configured);
        using (PluginHostHttp http = new(transport, new RequestPolicyState(), new FourChanPlugin().Describe().Sources))
        {
            HostHttpResult result = await http.SendAsync(Request(explicitValue));
            if (explicitValue == null)
            {
                Assert.IsType<HostHttpResponse>(result);
                Assert.Equal(expected, handler.UserAgent);
            }
            else
            {
                // SDK metadata callers cannot override User-Agent. Retain the
                // validation boundary rather than broadening it for this test.
                Assert.Equal("http-request", Assert.IsType<HostHttpFailure>(result).Failure.Code);
                Assert.Equal(0, handler.Sends);
            }
        }
        using HttpResponseMessage response = await transport.GetAsync("https://a.4cdn.org/borrowed-client-still-alive");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(explicitValue == null ? 2 : 1, handler.Sends);
    }

    [Fact]
    public async Task HostBodyFailureSurvivesLaterAdmissionWaitInSameOperation()
    {
        using Probe handler = new(brokenBody: true);
        using HttpClient transport = new(handler);
        using PluginHostHttp http = new(transport, new RequestPolicyState(), new FourChanPlugin().Describe().Sources);
        using IDisposable history = Begin();
        Assert.IsType<HostHttpFailure>(await http.SendAsync(Request()));
        Assert.NotNull(Failure(history));
        Assert.IsType<HostHttpDeferred>(await http.SendAsync(Request()));
        Assert.Equal(1, handler.Sends);
        Assert.False(Initial(history));
        Assert.NotNull(Failure(history));
    }

    [Fact]
    public async Task ValidationFailureThenNoSendWaitCannotBecomeProvenHealthyInitialAdmission()
    {
        using Probe handler = new();
        using HttpClient transport = new(handler);
        using PluginHostHttp http = new(transport, new RequestPolicyState(), new FourChanPlugin().Describe().Sources);
        Assert.IsType<HostHttpResponse>(await http.SendAsync(Request()));
        using IDisposable history = Begin();
        Assert.IsType<HostHttpFailure>(await http.SendAsync(Request() with { SourceId = "undeclared" }));
        Assert.IsType<HostHttpDeferred>(await http.SendAsync(Request()));
        Assert.Equal(1, handler.Sends);
        Assert.False(Initial(history));
        Assert.NotNull(Failure(history));
    }

    private sealed class Probe(bool brokenBody = false) : HttpMessageHandler
    {
        internal string? UserAgent;
        internal int Sends;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Sends++;
            UserAgent = request.Headers.UserAgent.ToString();
            HttpContent content = new ByteArrayContent(brokenBody ? [0x42, 0x41, 0x44] : "[]"u8.ToArray());
            if (brokenBody) content.Headers.ContentEncoding.Add("gzip");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request, Content = content });
        }
    }
}
