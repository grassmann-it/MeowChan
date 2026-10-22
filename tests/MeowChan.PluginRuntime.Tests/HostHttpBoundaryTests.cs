using System.IO.Compression;
using System.Net;
using MeowChan.Http;
using MeowChan.PluginHost.Http;
using MeowChan.PluginRuntime.Fixture;
using MeowChan.PluginSdk;

namespace MeowChan.PluginRuntime.Tests;

public sealed class HostHttpBoundaryTests
{
    [Fact]
    public async Task ForbiddenRedirectIsRejectedBeforeTransportSeesItsHost()
    {
        int sent = 0;
        using HttpClient transport = new(new Handler(request =>
        {
            sent++;
            HttpResponseMessage response = new(HttpStatusCode.Found) { RequestMessage = request };
            response.Headers.Location = new("https://undeclared.test/private"); return response;
        }));
        using PluginHostHttp http = new(transport, new RequestPolicyState(), new FixturePlugin().Describe().Sources);
        HostHttpFailure result = Assert.IsType<HostHttpFailure>(await http.SendAsync(Request()));
        Assert.Equal("http-redirect", result.Failure.Code); Assert.Equal(1, sent);
    }
    [Fact]
    public async Task AllowedRedirectUsesSamePolicyAndReturnsFinalDestination()
    {
        int sent = 0;
        using HttpClient transport = new(new Handler(request =>
        {
            sent++;
            HttpResponseMessage response = new(sent == 1 ? HttpStatusCode.MovedPermanently : HttpStatusCode.OK) { RequestMessage = request, Content = new StringContent("metadata") };
            if (sent == 1) response.Headers.Location = new("/second", UriKind.Relative);
            return response;
        }));
        using PluginHostHttp http = new(transport, new RequestPolicyState(), new FixturePlugin().Describe().Sources);
        HostHttpResponse result = Assert.IsType<HostHttpResponse>(await http.SendAsync(Request()));
        Assert.Equal("https://fixture.test/second", result.FinalUri.AbsoluteUri); Assert.Equal(2, sent);
        Assert.Equal("metadata", System.Text.Encoding.UTF8.GetString(result.Body));
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task BodyLimitAppliesToActualDecompressedBytes(bool compressed)
    {
        byte[] bytes = new byte[SdkLimits.MaximumMetadataBytes + 1];
        if (compressed)
        {
            using MemoryStream output = new();
            using (GZipStream gzip = new(output, CompressionLevel.SmallestSize, leaveOpen: true)) gzip.Write(bytes);
            bytes = output.ToArray();
        }
        using HttpClient transport = new(new Handler(request =>
        {
            HttpResponseMessage response = new(HttpStatusCode.OK) { RequestMessage = request, Content = new ByteArrayContent(bytes) };
            if (compressed) response.Content.Headers.ContentEncoding.Add("gzip"); return response;
        }));
        using PluginHostHttp http = new(transport, new RequestPolicyState(), new FixturePlugin().Describe().Sources);
        HostHttpFailure result = Assert.IsType<HostHttpFailure>(await http.SendAsync(Request()));
        Assert.Equal("metadata-too-large", result.Failure.Code);
    }
    [Fact]
    public async Task HeadReturnsNoBodyAndUnknownHeaderNeverReachesTransport()
    {
        int sent = 0;
        using HttpClient transport = new(new Handler(request => { sent++; return new(HttpStatusCode.OK) { RequestMessage = request, Content = new ByteArrayContent([1, 2, 3]) }; }));
        using PluginHostHttp http = new(transport, new RequestPolicyState(), new FixturePlugin().Describe().Sources);
        Assert.Empty(Assert.IsType<HostHttpResponse>(await http.SendAsync(Request() with { Method = HostHttpMethod.Head })).Body);
        HostHttpResult bad = await http.SendAsync(Request() with { Headers = new Dictionary<string, string> { ["Authorization"] = "not-allowed" } });
        Assert.IsType<HostHttpFailure>(bad); Assert.Equal(1, sent);
    }
    private static HostHttpRequest Request() => new("fixture", HostHttpMethod.Get, new("https://fixture.test/api"));
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(send(request));
    }
}
