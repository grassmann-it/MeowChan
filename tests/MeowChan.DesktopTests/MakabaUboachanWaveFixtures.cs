using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MeowChan.DesktopTests;

// Terminal transport: no fallback handler or socket is reachable. Originals are
// generated PNGs; source-shaped metadata is synthetic, never copied post text.
internal sealed class MakabaUboachanWaveFixtures : HttpMessageHandler
{
    internal static readonly string[] Sources = ["2ch", "uboachan"];
    internal static string ThreadUrl(string source) => BoardUrl(source) + "res/123.html";
    internal static string BoardUrl(string source) => source == "2ch" ? "https://2ch.su/demo/" : "https://uboachan.net/demo/";
    internal ConcurrentQueue<string> Requests { get; } = new();
    internal bool RejectAll { get; init; }
    internal bool CorruptExpectedHash { get; init; }
    internal bool RedirectMetadataToOriginalOnlyHost { get; init; }
    internal string OriginalRedirectHost { get; init; } = "2ch.su";
    private int _unexpected;
    internal int Unexpected => Volatile.Read(ref _unexpected);
    internal byte[][] Originals { get; }

    internal MakabaUboachanWaveFixtures()
    {
        using DesktopPluginFixtures generated = new();
        Originals = [generated.Landscape, generated.Portrait];
    }

    internal HttpClient Client() => new(this, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        string uri = request.RequestUri?.AbsoluteUri ?? "missing";
        Requests.Enqueue(uri);
        if (!RejectAll && request.Method == HttpMethod.Get)
        {
            if (uri == "https://2ch.hk/demo/catalog.json")
                return Task.FromResult(RedirectMetadataToOriginalOnlyHost
                    ? Redirect(request, "https://2ch.su/demo/catalog.json")
                    : Json(request, """{"threads":[{"num":123,"subject":"Synthetic Makaba","comment":"<p>Catalog</p>","files_count":2}]}"""));
            if (uri == "https://uboachan.net/demo/catalog.json")
                return Task.FromResult(Json(request, """[{"page":1,"threads":[{"no":123,"sub":"Synthetic Uboachan","com":"<p>Catalog</p>","images":2}]}]"""));
            if (uri == "https://2ch.hk/demo/res/123.json") return Task.FromResult(Json(request, Makaba()));
            if (uri == "https://uboachan.net/demo/res/123.json") return Task.FromResult(Json(request, Uboachan()));
            for (int slot = 0; slot < Originals.Length; slot++)
            {
                string path = "/demo/src/123/file-" + slot + ".png";
                if (uri == "https://2ch.hk" + path)
                    return Task.FromResult(Redirect(request, "https://" + OriginalRedirectHost + path));
                if (uri == "https://2ch.su" + path || uri == "https://uboachan.net/demo/src/file-" + slot + ".png")
                    return Task.FromResult(Response(request, Originals[slot], "image/png"));
            }
        }
        Interlocked.Increment(ref _unexpected);
        throw new InvalidOperationException("Network blocked outside Makaba/Uboachan fixture: " + uri);
    }

    private string Makaba() => JsonSerializer.Serialize(new { current_thread = 123, threads = new[] { new { posts = new object[]
    {
        new { num = 123, parent = 0, board = "demo", subject = "Synthetic Makaba", comment = "<p>Opening</p>",
            files = Enumerable.Range(0, 2).Select(slot => new
            {
                path = "/demo/src/123/file-" + slot + ".png", name = "file-" + slot + ".png", fullname = "original-" + slot + ".png",
                md5 = CorruptExpectedHash && slot == 1 ? new string('0', 32) : Convert.ToHexString(MD5.HashData(Originals[slot])).ToLowerInvariant(),
                size = slot == 0 ? 24.50m : 0m
            }).ToArray() },
        new { num = 124, parent = 123, comment = "<p>Reply</p>", files = Array.Empty<object>() }
    } } } });

    private string Uboachan() => JsonSerializer.Serialize(new { posts = new object[]
    {
        new { no = 123, resto = 0, sub = "Synthetic Uboachan", com = "<p>Opening</p>",
            tim = "file-0", ext = ".png", filename = "", fsize = Originals[0].Length,
            md5 = Convert.ToBase64String(MD5.HashData(Originals[0])), extra_files = new[]
            {
                new { tim = "file-1", ext = ".png", filename = "original-1", fsize = Originals[1].Length,
                    md5 = CorruptExpectedHash ? Convert.ToBase64String(new byte[20]) : Convert.ToBase64String(SHA1.HashData(Originals[1])) }
            } },
        new { no = 124, resto = 123, com = "<p>Reply</p>" }
    } });

    private static HttpResponseMessage Json(HttpRequestMessage request, string text) => Response(request, Encoding.UTF8.GetBytes(text), "application/json");
    private static HttpResponseMessage Response(HttpRequestMessage request, byte[] bytes, string contentType)
    {
        HttpResponseMessage response = new(HttpStatusCode.OK) { RequestMessage = request, Content = new ByteArrayContent(bytes) };
        response.Content.Headers.ContentType = new(contentType);
        return response;
    }
    private static HttpResponseMessage Redirect(HttpRequestMessage request, string target)
    {
        HttpResponseMessage response = new(HttpStatusCode.MovedPermanently) { RequestMessage = request, Content = new ByteArrayContent([]) };
        response.Headers.Location = new Uri(target);
        return response;
    }
}
