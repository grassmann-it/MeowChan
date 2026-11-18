using System.IO.Compression;
using System.Net;
using System.Text;
using MeowChan.Http;
using MeowChan.OfficialSources.FourChan;
using MeowChan.PluginHost.Http;
using MeowChan.PluginSdk;

namespace MeowChan.SourceAdapter.Tests;

/// <summary>
/// The shared transport does not decompress. The ordinary metadata path advertises the encodings its bounded
/// decoder handles, and decodes what the server answers.
/// </summary>
public sealed class HostHttpAcceptEncodingTests
{
    private static HostHttpRequest Request() => new("4chan", HostHttpMethod.Get, new Uri("https://a.4cdn.org/g/catalog.json"));

    [Fact]
    public async Task OrdinaryMetadataAdvertisesCompressionAndDecodesAGzipResponse()
    {
        const string json = "[{\"page\":1,\"threads\":[]}]";
        using Probe handler = new(Encoding.UTF8.GetBytes(json), "gzip");
        using HttpClient transport = new(handler);
        using PluginHostHttp http = new(transport, new RequestPolicyState(), new FourChanPlugin().Describe().Sources);
        HostHttpResponse response = Assert.IsType<HostHttpResponse>(await http.SendAsync(Request()));
        Assert.Equal("gzip, deflate, br", handler.AcceptEncoding);
        Assert.Equal(json, Encoding.UTF8.GetString(response.Body));
    }

    [Theory]
    [InlineData("deflate")]
    [InlineData("br")]
    public async Task DeflateAndBrotliResponsesAreDecodedToo(string encoding)
    {
        byte[] plain = "[1,2,3]"u8.ToArray();
        using Probe handler = new(plain, encoding);
        using HttpClient transport = new(handler);
        using PluginHostHttp http = new(transport, new RequestPolicyState(), new FourChanPlugin().Describe().Sources);
        HostHttpResponse response = Assert.IsType<HostHttpResponse>(await http.SendAsync(Request()));
        Assert.Equal(plain, response.Body);
    }

    private sealed class Probe(byte[] plain, string encoding) : HttpMessageHandler
    {
        internal string? AcceptEncoding;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            AcceptEncoding = request.Headers.AcceptEncoding.ToString();
            using MemoryStream compressed = new();
            using (Stream encoder = encoding switch
            {
                "gzip" => new GZipStream(compressed, CompressionLevel.Fastest, leaveOpen: true),
                "deflate" => new DeflateStream(compressed, CompressionLevel.Fastest, leaveOpen: true),
                _ => new BrotliStream(compressed, CompressionLevel.Fastest, leaveOpen: true)
            }) encoder.Write(plain);
            HttpContent content = new ByteArrayContent(compressed.ToArray());
            content.Headers.ContentEncoding.Add(encoding);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request, Content = content });
        }
    }
}
