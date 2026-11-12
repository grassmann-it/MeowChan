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

    [Fact]
    public async Task ChanSourceKeepsItsPolicyAndRedirectsBesideTheZeroRedirectBooruRoute()
    {
        List<string> sent = [];
        using HttpClient transport = new(new Handler(request =>
        {
            string url = request.RequestUri!.AbsoluteUri;
            lock (sent) sent.Add(url);
            if (url == "https://chan.test/a")
            {
                Assert.Equal("MeowChan", request.Headers.UserAgent.ToString());
                HttpResponseMessage moved = new(HttpStatusCode.MovedPermanently) { RequestMessage = request };
                moved.Headers.Location = new("/b", UriKind.Relative);
                return moved;
            }
            if (url == "https://chan.test/b")
                return new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request, Content = new StringContent("chan") };
            Assert.Equal("danbooru.test", request.RequestUri.Host);
            Assert.StartsWith("MeowChan/", request.Headers.UserAgent.ToString());
            HttpResponseMessage redirect = new(HttpStatusCode.Found) { RequestMessage = request };
            redirect.Headers.Location = new("https://danbooru.test/posts/2.json");
            return redirect;
        }));

        using PluginHostHttp host = new(transport, new RequestPolicyState(), [Source(), ChanSource()]);

        HostHttpResponse chan = Assert.IsType<HostHttpResponse>(await host.SendAsync(new(
            "chan", HostHttpMethod.Get, new("https://chan.test/a"))));
        Assert.Equal("https://chan.test/b", chan.FinalUri.AbsoluteUri);
        Assert.Equal("chan", Encoding.UTF8.GetString(chan.Body));

        HostHttpFailure booru = Assert.IsType<HostHttpFailure>(await host.SendAsync(new(
            "danbooru", HostHttpMethod.Get, new("https://danbooru.test/posts/1.json"))));
        Assert.Equal("http-redirect", booru.Failure.Code);
        Assert.Equal(302, booru.Failure.HttpStatusCode);

        // The chan source followed its one redirect; the booru source never followed Location.
        Assert.Equal(["https://chan.test/a", "https://chan.test/b", "https://danbooru.test/posts/1.json"], sent);

        // A chan HEAD still works: the GET-only booru restriction does not leak into chan sources.
        Assert.IsType<HostHttpResponse>(await host.SendAsync(new("chan", HostHttpMethod.Head, new("https://chan.test/b"))));
    }

    private static SourceDescription ChanSource() => new(
        "chan", "Chan", new("https://chan.test/"),
        SourceCapabilities.ThreadRead, [],
        [new("chan.test", HttpHostPurpose.Metadata)], new("1", []));

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
