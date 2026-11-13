using System.Net;
using System.Text;
using MeowChan.Http;
using MeowChan.PluginHost.Http;
using MeowChan.PluginSdk;

namespace MeowChan.PluginRuntime.Tests;

/// <summary>
/// The plugin capture points hand their source to the metering handler on the shared transport.
/// Every test uses source ids and hosts of its own, because the counters are process-wide.
/// </summary>
public sealed class PluginTransportMeteringTests
{
    private static string NewId() => "p1" + Guid.NewGuid().ToString("N")[..12];

    private static long Read(ref long field) => Volatile.Read(ref field);

    private static HttpClient Metered(Func<HttpRequestMessage, HttpResponseMessage> inner)
        => new(new MeteringHandler(new Handler(inner)));

    private static SourceDescription Source(string id, string host, SourceCapabilities capabilities, HttpHostPurpose purpose)
        => new(id, id, new($"https://{host}/"), capabilities, [], [new(host, purpose)], new("1", []));

    [Fact]
    public async Task ChanMetadataIsCountedUnderItsSourceNotItsHostThroughTheHistoryTransport()
    {
        string id = NewId(), host = id + ".test";
        using HttpClient transport = Metered(request => new HttpResponseMessage(HttpStatusCode.OK)
        { RequestMessage = request, Content = new StringContent("{\"threads\":[]}", Encoding.UTF8, "application/json") });
        using PluginHostHttp http = new(transport, new RequestPolicyState(),
            [Source(id, host, SourceCapabilities.ThreadRead, HttpHostPurpose.Metadata)]);

        HostHttpResponse result = Assert.IsType<HostHttpResponse>(await http.SendAsync(new(id, HostHttpMethod.Get, new($"https://{host}/api"))));

        SourceCounters counters = TransferMeter.For(id);
        Assert.Equal(1, Read(ref counters.Requests));
        Assert.Equal(result.Body.Length, Read(ref counters.NetDown));
        Assert.Equal(0, Read(ref counters.Errors));
        // The forwarded request kept the source: nothing was filed under the host name.
        Assert.DoesNotContain(TransferMeter.All, pair => pair.Key == host);
    }

    [Fact]
    public async Task ChanMetadataFailureCountsAnError()
    {
        string id = NewId(), host = id + ".test";
        using HttpClient transport = Metered(request => new HttpResponseMessage(HttpStatusCode.NotFound)
        { RequestMessage = request, Content = new StringContent("gone") });
        using PluginHostHttp http = new(transport, new RequestPolicyState(),
            [Source(id, host, SourceCapabilities.ThreadRead, HttpHostPurpose.Metadata)]);

        Assert.IsType<HostHttpResponse>(await http.SendAsync(new(id, HostHttpMethod.Get, new($"https://{host}/missing"))));

        SourceCounters counters = TransferMeter.For(id);
        Assert.Equal(1, Read(ref counters.Requests));
        Assert.Equal(1, Read(ref counters.Errors));
    }

    [Fact]
    public async Task BooruMetadataIsCountedUnderItsSource()
    {
        // The booru route only accepts its allowlisted provider, so this source id is fixed; counts are measured as deltas.
        const string id = "danbooru", host = "danbooru.test";
        SourceCounters counters = TransferMeter.For(id);
        long requests = Read(ref counters.Requests), bytes = Read(ref counters.NetDown);
        using HttpClient transport = Metered(request => new HttpResponseMessage(HttpStatusCode.OK)
        { RequestMessage = request, Content = new StringContent("[]", Encoding.UTF8, "application/json") });
        using PluginHostHttp http = new(transport, new RequestPolicyState(),
            [Source(id, host, SourceCapabilities.PostSearch, HttpHostPurpose.Metadata)]);

        HostHttpResponse result = Assert.IsType<HostHttpResponse>(await http.SendAsync(new(id, HostHttpMethod.Get,
            new($"https://{host}/posts.json?tags=cat&page=1&limit=20"))));

        Assert.True(Read(ref counters.Requests) - requests >= 1);
        Assert.True(Read(ref counters.NetDown) - bytes >= result.Body.Length);
        Assert.DoesNotContain(TransferMeter.All, pair => pair.Key == host);
    }

    [Fact]
    public async Task OriginalMediaBytesAreCountedUnderTheSourceAcrossARedirect()
    {
        string id = NewId(), host = id + ".test", cdn = id + "cdn.test";
        SourceDescription source = new(id, id, new($"https://{host}/"), SourceCapabilities.OriginalMedia, [],
            [new(host, HttpHostPurpose.OriginalMedia), new(cdn, HttpHostPurpose.OriginalMedia)], new("1", []));
        using HttpClient transport = Metered(request =>
        {
            if (request.RequestUri!.Host == host)
            {
                HttpResponseMessage moved = new(HttpStatusCode.Found) { RequestMessage = request, Content = new ByteArrayContent(new byte[10]) };
                moved.Headers.Location = new($"https://{cdn}/file.bin");
                return moved;
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request, Content = new ByteArrayContent(new byte[12_345]) };
        });
        using HttpClient media = PluginOriginalMediaHttp.CreateClient(transport, source, new RequestPolicyState());

        using HttpResponseMessage response = await media.GetAsync($"https://{host}/file.bin", HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(12_345, (await response.Content.ReadAsByteArrayAsync()).Length);

        SourceCounters counters = TransferMeter.For(id);
        Assert.Equal(2, Read(ref counters.Requests));
        // The redirect body is disposed unread; only the delivered original counts.
        Assert.Equal(12_345, Read(ref counters.NetDown));
        Assert.DoesNotContain(TransferMeter.All, pair => pair.Key == host || pair.Key == cdn);
    }

    [Fact]
    public async Task BooruPreviewBytesAreCountedUnderTheSource()
    {
        string id = NewId(), host = id + ".test";
        byte[] png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4];
        using HttpClient transport = Metered(request => new HttpResponseMessage(HttpStatusCode.OK)
        { RequestMessage = request, Content = new ByteArrayContent(png) });
        using BooruPreviewHttp preview = new(transport, Source(id, host, SourceCapabilities.PostSearch, HttpHostPurpose.PreviewMedia));

        Assert.Equal(png, await preview.LoadAsync(new($"https://{host}/thumb.png")));

        SourceCounters counters = TransferMeter.For(id);
        Assert.Equal(1, Read(ref counters.Requests));
        Assert.Equal(png.Length, Read(ref counters.NetDown));
    }

    [Fact]
    public async Task TheSharedPluginTransportCountsWireBytesAndKeepsItsHeaders()
    {
        string id = NewId();
        byte[] body = new byte[4321];
        HttpListener listener = StartListener(out string prefix);
        try
        {
            Task serving = Task.Run(async () =>
            {
                HttpListenerContext context = await listener.GetContextAsync();
                context.Response.ContentType = "application/octet-stream";
                context.Response.ContentLength64 = body.Length;
                await context.Response.OutputStream.WriteAsync(body);
                context.Response.Close();
            });
            using HttpClient transport = PluginHostHttp.CreateTransport();
            using HttpRequestMessage request = new(HttpMethod.Get, prefix + "x");
            request.Options.Set(MeteringHandler.SourceKey, id);
            using HttpResponseMessage response = await transport.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            Assert.Equal(body.Length, response.Content.Headers.ContentLength);
            Assert.Equal("application/octet-stream", response.Content.Headers.ContentType!.MediaType);
            Assert.Equal(body.Length, (await response.Content.ReadAsByteArrayAsync()).Length);
            await serving;
        }
        finally { listener.Close(); }

        SourceCounters counters = TransferMeter.For(id);
        Assert.Equal(body.Length, Read(ref counters.NetDown));
        Assert.Equal(1, Read(ref counters.Requests));
    }

    private static HttpListener StartListener(out string prefix)
    {
        for (int attempt = 0; ; attempt++)
        {
            int port = Random.Shared.Next(20000, 60000);
            HttpListener listener = new();
            prefix = $"http://127.0.0.1:{port}/";
            listener.Prefixes.Add(prefix);
            try { listener.Start(); return listener; }
            catch (HttpListenerException) when (attempt < 20) { listener.Close(); }
        }
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> callback) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(callback(request));
    }
}
