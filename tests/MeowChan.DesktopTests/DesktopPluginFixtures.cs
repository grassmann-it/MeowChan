using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MeowChan.DesktopTests;

// Terminal, in-memory transport: unknown methods/URLs cannot reach a socket or fallback handler.
// The JSON has the same source shapes as ExamplePlugins.Tests, with generated, valid PNG originals.
internal sealed class DesktopPluginFixtures : HttpMessageHandler
{
    internal static readonly string[] Sources = { "lainchan", "kohlchan" };
    internal static string Origin(string source) => source == "lainchan" ? "https://lainchan.org" : "https://kohlchan.net";
    internal static string ThreadUrl(string source) => Origin(source) + "/demo/res/123.html";
    internal byte[] Landscape { get; } = Png(8, 4);
    internal byte[] Portrait { get; } = Png(4, 8);
    internal ConcurrentQueue<string> Requests { get; } = new();
    internal TaskCompletionSource MetadataStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal bool HoldMetadata { get; init; }
    internal bool RejectAll { get; init; }
    private int _cancelled, _unexpected;
    internal int Cancelled => Volatile.Read(ref _cancelled);
    internal int Unexpected => Volatile.Read(ref _unexpected);
    internal int MediaRequests => Requests.Count(uri => uri.Contains("/src/", StringComparison.Ordinal) || uri.Contains("/.media/", StringComparison.Ordinal));

    internal HttpClient Client() => new(this, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        string uri = request.RequestUri?.AbsoluteUri ?? "missing";
        Requests.Enqueue(uri);
        if (!RejectAll && request.Method == HttpMethod.Get)
        {
            foreach (string source in Sources)
            {
                if (uri == Origin(source) + "/demo/catalog.json")
                    return Response(request, Encoding.UTF8.GetBytes(Catalog(source)), "application/json");
                if (uri == Origin(source) + "/demo/res/123.json")
                {
                    MetadataStarted.TrySetResult();
                    if (HoldMetadata)
                    {
                        try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
                        catch (OperationCanceledException) when (token.IsCancellationRequested)
                        { Interlocked.Increment(ref _cancelled); throw; }
                    }
                    return Response(request, Encoding.UTF8.GetBytes(Thread(source)), "application/json");
                }
            }
            if (uri is "https://lainchan.org/demo/src/1000-0.png" or "https://kohlchan.net/.media/landscape.png")
                return Response(request, Landscape, "image/png");
            if (uri is "https://lainchan.org/demo/src/1001-1.png" or "https://kohlchan.net/.media/portrait.png")
                return Response(request, Portrait, "image/png");
        }
        Interlocked.Increment(ref _unexpected);
        throw new InvalidOperationException("Network blocked: request is outside the desktop plugin fixture: " + uri);
    }

    private static HttpResponseMessage Response(HttpRequestMessage request, byte[] bytes, string type)
    {
        HttpResponseMessage response = new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes), RequestMessage = request };
        response.Content.Headers.ContentType = new(type);
        return response;
    }

    private string Thread(string source) => source == "lainchan"
        ? JsonSerializer.Serialize(new { posts = new object[]
        {
            new { no = 123, resto = 0, sub = "Desktop delivered Lainchan", com = "<p>Synthetic opening</p>",
                time = 1700000000, tim = "1000-0", ext = ".png", filename = "landscape", fsize = Landscape.Length,
                md5 = Convert.ToBase64String(MD5.HashData(Landscape)),
                extra_files = new[] { new { tim = "1001-1", ext = ".png", filename = "portrait", fsize = Portrait.Length,
                    md5 = Convert.ToBase64String(MD5.HashData(Portrait)) } } },
            new { no = 124, resto = 123, com = "<p>Synthetic reply</p>" }
        } })
        : JsonSerializer.Serialize(new { threadId = 123, boardUri = "demo", subject = "Desktop delivered Kohlchan",
            markdown = "<p>Synthetic opening</p>", creation = "2023-11-14T22:13:20.000Z", archived = true,
            files = new[] {
                new { originalName = "landscape.png", path = "/.media/landscape.png", size = Landscape.Length, mime = "image/png" },
                new { originalName = "portrait.png", path = "/.media/portrait.png", size = Portrait.Length, mime = "image/png" } },
            posts = new[] { new { postId = 124, message = "Synthetic reply", files = Array.Empty<object>() } } });

    private static string Catalog(string source) => source == "lainchan"
        ? """[{"page":1,"threads":[{"no":123,"sub":"Fixture catalog","com":"<p>Opening</p>","replies":1,"images":2}]}]"""
        : """[{"threadId":123,"subject":"Fixture catalog","markdown":"<p>Opening</p>","postCount":1,"fileCount":2,"page":1}]""";

    private static byte[] Png(int width, int height)
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
