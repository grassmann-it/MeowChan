using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MeowChan.DesktopTests;

// Terminal transport: these requests can never escape to the network. Generated PNGs have
// genuine provider digests and deliberately different dimensions for the production filter.
internal sealed class SushiZzzWaveFixtures(string source) : HttpMessageHandler
{
    internal string Source { get; } = source;
    internal string Origin => Source == "sushichan" ? "https://sushigirl.cafe" : "https://zzzchan.xyz";
    internal string BoardUrl => Origin + "/demo/";
    internal string ThreadUrl => Origin + (Source == "sushichan" ? "/demo/res/123.html" : "/demo/thread/123.html");
    internal string MetadataUrl => Origin + (Source == "sushichan" ? "/demo/res/123.json" : "/demo/thread/123.json");
    internal string OriginalUrl(int slot) => Origin + (Source == "sushichan" ? $"/demo/src/100{slot}-{slot}.png" : $"/file/image-{slot}.png");
    internal byte[] Landscape { get; } = Png(8, 4);
    internal byte[] Portrait { get; } = Png(4, 8);
    internal bool RejectAll { get; init; }
    internal bool CorruptOriginal { get; init; }
    internal ConcurrentQueue<string> Requests { get; } = new();
    private int _unexpected;
    internal int Unexpected => Volatile.Read(ref _unexpected);
    internal int MediaRequests => Requests.Count(url => url == OriginalUrl(0) || url == OriginalUrl(1));
    internal HttpClient Client() => new(this, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        string url = request.RequestUri?.AbsoluteUri ?? "missing";
        Requests.Enqueue(url);
        if (!RejectAll && request.Method == HttpMethod.Get)
        {
            if (url == BoardUrl + "catalog.json") return Reply(request, Encoding.UTF8.GetBytes(Catalog()), "application/json");
            if (url == MetadataUrl) return Reply(request, Encoding.UTF8.GetBytes(Thread()), "application/json");
            if (url == OriginalUrl(0))
            {
                byte[] bytes = (byte[])Landscape.Clone();
                if (CorruptOriginal) bytes[^1] ^= 1; // Same length, different provider MD5/SHA-256.
                return Reply(request, bytes, "image/png");
            }
            if (url == OriginalUrl(1)) return Reply(request, Portrait, "image/png");
        }
        Interlocked.Increment(ref _unexpected);
        throw new InvalidOperationException("Terminal source fixture rejected HTTP: " + url);
    }

    private static Task<HttpResponseMessage> Reply(HttpRequestMessage request, byte[] bytes, string type)
    {
        HttpResponseMessage response = new(HttpStatusCode.OK) { RequestMessage = request, Content = new ByteArrayContent(bytes) };
        response.Content.Headers.ContentType = new(type);
        return Task.FromResult(response);
    }

    private string Thread() => Source == "sushichan"
        ? JsonSerializer.Serialize(new { posts = new object[] {
            new { no = 123, resto = 0, sub = "Sushi strict fixture", com = "<p>Opening</p>", time = 1700000000,
                tim = "1000-0", ext = ".png", filename = "landscape", fsize = Landscape.Length,
                md5 = Convert.ToBase64String(MD5.HashData(Landscape)), extra_files = new[] {
                    new { tim = "1001-1", ext = ".png", filename = "portrait", fsize = Portrait.Length,
                        md5 = Convert.ToBase64String(MD5.HashData(Portrait)) } } },
            new { no = 124, resto = 123, com = "<p>Reply without media</p>" } } })
        : JsonSerializer.Serialize(new { postId = 123, board = "demo", thread = (int?)null, subject = "ZZZ strict fixture",
            message = "<p>Opening</p>", date = "2023-11-14T22:13:20.000Z", files = new[] { File(0, Landscape), File(1, Portrait) },
            replies = new[] { new { postId = 124, board = "demo", thread = 123, message = "<p>Reply without media</p>", files = Array.Empty<object>() } } });
    private static object File(int slot, byte[] bytes) => new { filename = $"image-{slot}.png", originalFilename = slot == 0 ? "landscape.png" : "portrait.png",
        extension = ".png", mimetype = "image/png", size = bytes.Length, hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant() };
    private string Catalog() => Source == "sushichan"
        ? """[{"page":1,"threads":[{"no":123,"sub":"Sushi catalog","com":"<p>Opening</p>","replies":1,"images":2}]}]"""
        : """[{"postId":123,"board":"demo","thread":null,"subject":"ZZZ catalog","message":"<p>Opening</p>","replyposts":1,"replyfiles":2}]""";

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
