using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MeowChan.Archive;
using MeowChan.Diagnostics;
using MeowChan.Gallery;
using MeowChan.Storage;
using SkiaSharp;

namespace MeowChan.DesktopSmoke;

/// <summary>Versioned deterministic bounded fixture, generated before the GUI/run. No terminal network handler.</summary>
internal static class ShellLoadFixture
{
    internal const string Version = "pf00-offline-gallery-v1";
    internal const int MediaCount = 64;
    internal static string Scenario => Environment.GetEnvironmentVariable("MEOWCHAN_SHELL_LOAD_SCENARIO") ?? "held";
    internal static int HoldMilliseconds => int.Parse(Environment.GetEnvironmentVariable("MEOWCHAN_SHELL_LOAD_HOLD_MS") ?? "1000");
    internal static GalleryEntry[] Entries { get; private set; } = [];
    internal static byte[][] Payloads { get; private set; } = [];
    internal static string Hash { get; private set; } = "";
    internal static readonly SKColor[] Colors = [SKColors.Crimson, SKColors.LimeGreen, SKColors.RoyalBlue];

    internal static async Task PrepareAsync(string profile)
    {
        if (Scenario is not ("held" or "paused" or "metadata" or "downloads")) throw new ArgumentException("Unknown shell-load scenario.");
        if (HoldMilliseconds is < 1 or > 5000) throw new ArgumentException("Hold must be 1..5000 ms.");
        List<GalleryEntry> entries = new();
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes(Version));
        // Include transport pacing and dimensions in the fixture binding.
        hash.AppendData(Encoding.UTF8.GetBytes("64x256x192;chunk=4096;delay=25ms;gallery=3x960x640"));
        for (int index = 0; index < Colors.Length; index++)
        {
            string directory = Path.Combine(profile, "gallery-fixture", index.ToString());
            Directory.CreateDirectory(directory);
            byte[] bytes = Png(960, 640, index, noise: false);
            hash.AppendData(bytes);
            await File.WriteAllBytesAsync(Path.Combine(directory, "target.png"), bytes);
            object record = new { MediaIdentifier = "gallery-" + index, SourceUrl = "https://offline.invalid/gallery/" + index,
                FileName = "target.png", Length = bytes.Length, Sha256 = Convert.ToHexString(SHA256.HashData(bytes)),
                Md5 = Convert.ToBase64String(MD5.HashData(bytes)) };
            await File.WriteAllTextAsync(Path.Combine(directory, ".meowchan-manifest.json"), JsonSerializer.Serialize(new {
                Version = 1, Entries = new Dictionary<string, object> { ["target.png"] = record } }));
            MediaAssetSnapshot manifest = await MediaAssetReader.OpenAsync(directory);
            MediaAssetReference asset = manifest.GetPage().Single();
            // Synthetic gallery observations, not production acquisition authority; verified original handles still apply.
            entries.Add(new(new GalleryAssetKey(new ResourceId("4chan", "g", "900"), directory, asset.AssetId),
                manifest, asset, new CapturedPost((900 + index).ToString(), index, PostFieldPresence.Html, Html: "<p>Target " + index + "</p>"),
                null, false, true));
        }
        Payloads = Enumerable.Range(0, MediaCount).Select(index => Png(256, 192, index, noise: true)).ToArray();
        foreach (byte[] bytes in Payloads) hash.AppendData(bytes);
        Entries = entries.ToArray();
        Hash = Convert.ToHexString(hash.GetHashAndReset());
    }

    private static byte[] Png(int width, int height, int index, bool noise)
    {
        using SKBitmap bitmap = new(width, height);
        if (noise)
        {
            uint state = (uint)(index + 1);
            for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
            {
                state ^= state << 13; state ^= state >> 17; state ^= state << 5;
                bitmap.SetPixel(x, y, new SKColor((byte)state, (byte)(state >> 8), (byte)(state >> 16)));
            }
        }
        else bitmap.Erase(Colors[index]);
        using SKImage image = SKImage.FromBitmap(bitmap);
        using SKData data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }
}

internal sealed class ShellLoadTransport : HttpMessageHandler
{
    private readonly TaskCompletionSource _allowed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _requested = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long _bytes;
    private int _metadata, _requests, _active;
    internal long Bytes => Interlocked.Read(ref _bytes);
    internal int MetadataRequests => Volatile.Read(ref _metadata);
    internal int MediaRequests => Volatile.Read(ref _requests);
    internal int Active => Volatile.Read(ref _active);
    internal Task MediaRequested => _requested.Task;
    internal void AllowMedia() => _allowed.TrySetResult();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Uri uri = request.RequestUri ?? throw new InvalidOperationException("Fixture URI missing.");
        if (uri.Host == "a.4cdn.org" && uri.AbsolutePath == "/boards.json")
            return Json("{\"boards\":[{\"board\":\"g\",\"title\":\"Technology\"}]}");
        if (uri.Host == "a.4cdn.org" && uri.AbsolutePath == "/g/thread/123.json")
        {
            int count = Interlocked.Increment(ref _metadata);
            PerformanceDiagnostics.Record("fixture.metadata", "response", value: count);
            await Task.Delay(30, cancellationToken);
            int mediaCount = ShellLoadFixture.Scenario == "metadata" ? 0 : ShellLoadFixture.MediaCount;
            object[] posts = Enumerable.Range(0, Math.Max(1, mediaCount)).Select(index => mediaCount == 0
                ? (object)new { no = 123, sub = "PF00 offline metadata", com = "Metadata only", time = 1700000000 }
                : new { no = 123 + index, sub = "PF00 offline download", com = "Bounded offline media", time = 1700000000,
                    tim = 777 + index, filename = "fixture-" + index, ext = ".png", fsize = ShellLoadFixture.Payloads[index].Length,
                    md5 = Convert.ToBase64String(MD5.HashData(ShellLoadFixture.Payloads[index])) }).ToArray();
            return Json(JsonSerializer.Serialize(new { posts }));
        }
        if (uri.Host == "i.4cdn.org" && uri.AbsolutePath.StartsWith("/g/", StringComparison.Ordinal)
            && uri.AbsolutePath.EndsWith(".png", StringComparison.Ordinal)
            && int.TryParse(Path.GetFileNameWithoutExtension(uri.AbsolutePath), out int tim)
            && tim >= 777 && tim < 777 + ShellLoadFixture.MediaCount)
        {
            Interlocked.Increment(ref _requests); _requested.TrySetResult();
            await _allowed.Task.WaitAsync(cancellationToken);
            return new(HttpStatusCode.OK) { Content = new StreamContent(new PacedStream(this, ShellLoadFixture.Payloads[tim - 777])) };
        }
        // A malformed or unexpected request fails locally; it can never fall through to real HTTP.
        throw new InvalidOperationException("Unexpected offline fixture request: " + uri);
    }
    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK)
        { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed class PacedStream : MemoryStream
    {
        private readonly ShellLoadTransport _owner;
        private int _disposed;
        internal PacedStream(ShellLoadTransport owner, byte[] bytes) : base(bytes, writable: false)
        { _owner = owner; Interlocked.Increment(ref owner._active); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (buffer.Length == 0) return 0;
            await Task.Delay(25, cancellationToken);
            int count = base.Read(buffer.Span[..Math.Min(buffer.Length, 4096)]);
            Interlocked.Add(ref _owner._bytes, count);
            return count;
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        protected override void Dispose(bool disposing)
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) Interlocked.Decrement(ref _owner._active);
            base.Dispose(disposing);
        }
    }
}
