using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MeowChan.DesktopTests;

// Terminal synthetic transport: never opens sockets and never delegates unknown requests.
internal sealed class LeftypolWaveFixtures : HttpMessageHandler
{
    internal const string ThreadUrl = "https://leftypol.org/demo/res/123.html";
    internal const string BoardUrl = "https://leftypol.org/demo/";
    internal static readonly string[] Ids = ["opaque-size", "opaque-md5"];
    internal byte[][] Originals { get; }
    internal ConcurrentQueue<string> Requests { get; } = new();
    internal bool RejectAll { get; init; }
    internal bool RetryArmed { get; set; }
    private readonly ConcurrentDictionary<string, int> counts = new();

    internal LeftypolWaveFixtures()
    {
        using DesktopPluginFixtures png = new();
        Originals = [png.Landscape, png.Portrait];
    }

    internal HttpClient Client() => new(this, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
    internal int OriginalRequests(int index) => counts.GetValueOrDefault(OriginalUrl(index));
    internal static string OriginalUrl(int index) => $"https://leftypol.org/demo/src/{Ids[index]}.png";
    internal long ProviderSize(int index) => Originals[index].LongLength + (index == 0 ? 1 : 0);
    internal string ProviderMd5(int index) => Convert.ToBase64String(MD5.HashData(index == 1 ? new byte[] { 7, 8, 9 } : Originals[index]));

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        string url = request.RequestUri?.AbsoluteUri ?? "missing";
        Requests.Enqueue(url);
        int count = counts.AddOrUpdate(url, 1, (_, current) => current + 1);
        if (RejectAll || request.Method != HttpMethod.Get || request.Headers.Range != null)
            throw new InvalidOperationException("Leftypol fixture denied request: " + request.Method + " " + url);
        if (url == "https://leftypol.org/demo/catalog.json" && count == 1)
            return Response(request, Encoding.UTF8.GetBytes("""[{"page":1,"threads":[{"no":123,"sub":"Synthetic Leftypol","com":"Fixture","replies":1,"images":2}]}]"""), "application/json");
        if (url == "https://leftypol.org/demo/res/123.json" && count == 1)
        {
            object[] files = Enumerable.Range(0, 2).Select(index => (object)new
            {
                id = Ids[index], tim = "must-not-be-used-" + index,
                file_path = "/demo/src/" + Ids[index] + ".png", thumb_path = "/demo/thumb/preview.webp",
                filename = "same-name", ext = ".png", mime = "image/png", fsize = ProviderSize(index), md5 = ProviderMd5(index)
            }).ToArray();
            string json = JsonSerializer.Serialize(new { posts = new object[] {
                new { no = 123, resto = 0, board = "demo", time = 1700000000, sub = "Synthetic Leftypol", com = "Opening", files },
                new { no = 124, resto = 123, com = "Reply", files = Array.Empty<object>() } } });
            return Response(request, Encoding.UTF8.GetBytes(json), "application/json");
        }
        for (int index = 0; index < 2; index++)
            if (url == OriginalUrl(index) && (count == 1 || count == 2 && RetryArmed))
                return Response(request, Originals[index], "image/png");
        throw new InvalidOperationException("Leftypol fixture denied unknown, premature or excess request: " + url);
    }

    private static Task<HttpResponseMessage> Response(HttpRequestMessage request, byte[] bytes, string type)
    {
        HttpResponseMessage response = new(HttpStatusCode.OK) { RequestMessage = request, Content = new ByteArrayContent(bytes) };
        response.Content.Headers.ContentType = new(type);
        return Task.FromResult(response);
    }
}
