using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MeowChan.DesktopTests;

/// <summary>
/// A terminal in-memory site for the harness tests: a vichan-style source (lainchan.org), a LynxChan-style source
/// (kohlchan.net, capybarachan.org) with three boards each. Unknown URLs throw, so nothing can reach a socket.
/// The shapes follow DesktopPluginFixtures; the originals are generated PNGs, one per board and slot.
/// </summary>
internal sealed class StabilityStubSite : HttpMessageHandler
{
    internal static readonly string[] Boards = ["demo", "demo2", "demo3"];
    internal ConcurrentQueue<string> Requests { get; } = new();
    /// <summary>Returns a replacement response for a URL (without query), or null to fall through to the site.</summary>
    internal Func<string, HttpRequestMessage, HttpResponseMessage?>? Override { get; set; }
    private readonly Dictionary<string, byte[]> _originals = new(StringComparer.Ordinal);

    internal StabilityStubSite()
    {
        foreach ((string host, int offset) in new[] { ("lainchan.org", 0), ("kohlchan.net", 40), ("capybarachan.org", 80) })
            for (int board = 0; board < 3; board++)
                for (int slot = 0; slot < 2; slot++)
                    _originals[Original(host, Boards[board], slot)] = Png(4 + offset / 10 + board * 3 + slot, 6 + slot + board);
    }

    internal HttpClient Client() => new(this, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
    internal int RequestsTo(string host) => Requests.Count(entry => new Uri(entry[(entry.IndexOf(' ') + 1)..]).Host == host);

    internal static string Original(string host, string board, int slot) => host == "lainchan.org"
        ? $"https://lainchan.org/{board}/src/{board}-1000-{slot}.png"
        : $"https://{host}/.media/{board}-{slot}.png";
    internal byte[] OriginalBytes(string host, string board, int slot) => _originals[Original(host, board, slot)];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        string url = request.RequestUri!.AbsoluteUri;
        Requests.Enqueue(request.Method + " " + url);
        if (Override?.Invoke(url, request) is { } replacement)
        {
            replacement.RequestMessage = request;
            return Task.FromResult(replacement);
        }
        if (request.Method == HttpMethod.Get)
        {
            Uri uri = request.RequestUri;
            foreach (string board in Boards)
                switch (uri.Host)
                {
                    case "lainchan.org":
                        if (url == $"https://lainchan.org/{board}/catalog.json") return Json(request, LainchanCatalog());
                        if (url == $"https://lainchan.org/{board}/res/123.json") return Json(request, LainchanThread(board));
                        break;
                    case "kohlchan.net" or "capybarachan.org":
                        if (url == $"https://{uri.Host}/{board}/catalog.json") return Json(request, LynxCatalog());
                        if (url == $"https://{uri.Host}/{board}/res/123.json") return Json(request, LynxThread(uri.Host, board));
                        break;
                }
            if (url == "https://capybarachan.org/boards.json")
                return Json(request, """{"boards":[{"boardUri":"bad-id"},{"boardUri":"demo"},{"boardUri":"demo2"},{"boardUri":"demo3"}]}""");
            if (_originals.TryGetValue(url, out byte[]? bytes)) return Task.FromResult(Response(request, bytes, "image/png"));
        }
        throw new InvalidOperationException("Network blocked: outside the stability stub site: " + request.Method + " " + url);
    }

    private static string LainchanCatalog() =>
        """[{"page":1,"threads":[{"no":100,"sub":"No image","com":"<p>text</p>","replies":0,"images":0},{"no":123,"sub":"Fixture catalog","com":"<p>Opening</p>","replies":1,"images":2}]}]""";

    private string LainchanThread(string board) => JsonSerializer.Serialize(new { posts = new object[]
    {
        new { no = 123, resto = 0, sub = "Stability stub", com = "<p>Synthetic opening</p>", time = 1700000000,
            tim = board + "-1000-0", ext = ".png", filename = "landscape", fsize = _originals[Original("lainchan.org", board, 0)].Length,
            md5 = Convert.ToBase64String(MD5.HashData(_originals[Original("lainchan.org", board, 0)])),
            extra_files = new[] { new { tim = board + "-1000-1", ext = ".png", filename = "portrait",
                fsize = _originals[Original("lainchan.org", board, 1)].Length,
                md5 = Convert.ToBase64String(MD5.HashData(_originals[Original("lainchan.org", board, 1)])) } } },
        new { no = 124, resto = 123, com = "<p>Synthetic reply</p>" }
    } });

    private static string LynxCatalog() =>
        """[{"threadId":123,"subject":"Fixture catalog","markdown":"<p>Opening</p>","postCount":1,"fileCount":2,"page":1},{"threadId":99,"subject":"No files","markdown":"text","postCount":1,"fileCount":0,"page":1}]""";

    private string LynxThread(string host, string board) => JsonSerializer.Serialize(new
    {
        threadId = 123, boardUri = board, subject = "Stability stub", markdown = "<p>Synthetic opening</p>", creation = "2023-11-14T22:13:20.000Z",
        files = new[]
        {
            new { originalName = "landscape.png", path = $"/.media/{board}-0.png", size = _originals[Original(host, board, 0)].Length, mime = "image/png" },
            new { originalName = "portrait.png", path = $"/.media/{board}-1.png", size = _originals[Original(host, board, 1)].Length, mime = "image/png" }
        },
        posts = new[] { new { postId = 124, message = "Synthetic reply", files = Array.Empty<object>() } }
    });

    private static Task<HttpResponseMessage> Json(HttpRequestMessage request, string text) => Task.FromResult(Response(request, Encoding.UTF8.GetBytes(text), "application/json"));

    internal static HttpResponseMessage Response(HttpRequestMessage request, byte[] bytes, string contentType, HttpStatusCode status = HttpStatusCode.OK)
    {
        HttpResponseMessage response = new(status) { RequestMessage = request, Content = new ByteArrayContent(bytes) };
        response.Content.Headers.ContentType = new(contentType);
        return response;
    }

    internal static byte[] Png(int width, int height)
    {
        using MemoryStream image = new();
        image.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        void Chunk(string kind, byte[] data)
        {
            byte[] length = new byte[4]; BinaryPrimitives.WriteInt32BigEndian(length, data.Length); image.Write(length);
            byte[] name = Encoding.ASCII.GetBytes(kind); image.Write(name); image.Write(data);
            uint crc = 0xffffffff;
            foreach (byte value in name.Concat(data))
            {
                crc ^= value;
                for (int bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xedb88320u : 0);
            }
            BinaryPrimitives.WriteUInt32BigEndian(length, ~crc); image.Write(length);
        }
        byte[] header = new byte[13]; BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height); header[8] = 8; header[9] = 6;
        Chunk("IHDR", header);
        using MemoryStream compressed = new();
        using (ZLibStream zip = new(compressed, CompressionLevel.Fastest, leaveOpen: true))
            for (int row = 0; row < height; row++) zip.Write(new byte[1 + width * 4]);
        Chunk("IDAT", compressed.ToArray()); Chunk("IEND", Array.Empty<byte>());
        return image.ToArray();
    }
}
