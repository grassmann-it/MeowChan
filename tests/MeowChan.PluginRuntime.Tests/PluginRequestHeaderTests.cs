using System.Net;
using MeowChan.Http;
using MeowChan.PluginHost.Http;
using MeowChan.PluginSdk;

namespace MeowChan.PluginRuntime.Tests;

public sealed class PluginRequestHeaderTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OnlyTypedValidatorsAndTheCheckedAuthorityReachTheTransport(bool booru)
    {
        SourceDescription source = new(booru ? "danbooru" : "chan", "Source", new("https://metadata.test/"),
            booru ? SourceCapabilities.PostSearch : SourceCapabilities.ThreadRead, [],
            [new("metadata.test", HttpHostPurpose.Metadata)], new("1", []));
        HostHttpRequest request = new(source.SourceId, HostHttpMethod.Get,
            new Uri(booru ? "https://metadata.test/posts.json" : "https://metadata.test/thread"));
        using Capture handler = new();
        using HttpClient client = new(handler, disposeHandler: false);
        client.DefaultRequestHeaders.Host = "default.test";
        using PluginHostHttp host = new(client, new RequestPolicyState(), [source]);

        foreach (HostHttpRequest invalid in new[]
        {
            request with { Validators = new SourceValidators("unquoted") },
            request with { Validators = new SourceValidators("\"v\u0001\"") },
            request with { Headers = new Dictionary<string, string> { ["If-None-Match"] = "unquoted" } },
            request with { Headers = new Dictionary<string, string> { ["Accept"] = "application/\u007fjson" } },
            request with
            {
                Headers = new Dictionary<string, string> { ["if-none-match"] = "\"v2\"" },
                Validators = new SourceValidators("\"v1\"")
            },
            request with
            {
                Headers = new Dictionary<string, string> { ["If-Modified-Since"] = "Thu, 22 Oct 2026 19:52:42 GMT" },
                Validators = new SourceValidators(LastModified: DateTimeOffset.UnixEpoch)
            }
        })
            Assert.IsType<HostHttpFailure>(await host.SendAsync(invalid));

        Assert.Equal(0, handler.Sends);
        HostHttpResponse response = Assert.IsType<HostHttpResponse>(await host.SendAsync(request with
        {
            Headers = new Dictionary<string, string> { ["If-None-Match"] = "W/\"v1\", \"v2\"" }
        }));
        Assert.Equal(304, response.StatusCode);
        Assert.Equal("metadata.test", handler.Host);
        Assert.Equal(["W/\"v1\"", "\"v2\""], handler.Tags);
    }

    private sealed class Capture : HttpMessageHandler
    {
        internal int Sends;
        internal string? Host;
        internal string[]? Tags;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Sends++;
            Host = request.Headers.Host;
            Tags = request.Headers.IfNoneMatch.Select(tag => tag.ToString()).ToArray();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotModified) { RequestMessage = request });
        }
    }
}
