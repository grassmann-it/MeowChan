using System.Net;
using System.Text;
using MeowChan.Http;
using MeowChan.PluginHost.Http;
using MeowChan.PluginSdk;

namespace MeowChan.PluginRuntime.Tests;

public sealed class PluginHostBooruRoutingTests
{
    [Fact]
    public async Task PluginHostHttpRoutesPostSearchThroughBoundedBooruTransport()
    {
        int sends = 0;
        using HttpClient transport = new(new Handler(request =>
        {
            ++sends;
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("https://danbooru.test/posts.json?tags=cat&page=1&limit=20", request.RequestUri!.AbsoluteUri);
            Assert.Equal("MeowChan/1.1.0 (+https://github.com/grassmann-it/MeowChan)",
                request.Headers.UserAgent.ToString());
            Assert.Equal("identity", Assert.Single(request.Headers.AcceptEncoding).Value);
            Assert.False(request.Headers.Authorization is not null);
            Assert.False(request.Headers.Contains("Cookie"));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent("{}", Encoding.UTF8, "application/json")
            };
        }));

        using PluginHostHttp host = new(transport, new RequestPolicyState(), [Source()]);
        HostHttpResponse result = Assert.IsType<HostHttpResponse>(await host.SendAsync(new(
            "danbooru", HostHttpMethod.Get,
            new("https://danbooru.test/posts.json?tags=cat&page=1&limit=20"))));

        Assert.Equal(200, result.StatusCode);
        Assert.Equal("{}", Encoding.UTF8.GetString(result.Body));
        Assert.Equal(1, sends);
    }

    private static SourceDescription Source() => new(
        "danbooru", "Danbooru", new("https://danbooru.test/"),
        SourceCapabilities.PostSearch, [],
        [new("danbooru.test", HttpHostPurpose.Metadata)], new("1", []));

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> callback) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(callback(request));
    }
}
