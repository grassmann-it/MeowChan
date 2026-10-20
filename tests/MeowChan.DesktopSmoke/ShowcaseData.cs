using System.Security.Cryptography;
using MeowChan.Archive;
using MeowChan.Boards.FourChan;
using MeowChan.Downloader;
using MeowChan.Interfaces;
using MeowChan.Persistence;
using MeowChan.Storage;
using SkiaSharp;

namespace MeowChan.DesktopSmoke;

/// <summary>Original, deterministic offline release-demo content. No source posts or downloaded artwork.</summary>
internal static class ShowcaseData
{
    internal static readonly ResourceId Hero = new("4chan", "wg", "900000001");
    internal const int ThreadCount = 56;
    internal const string Search = "landscape";
    private static readonly DateTimeOffset Epoch = new(2026, 10, 18, 16, 0, 0, TimeSpan.Zero);
    private static readonly string[] Subjects =
    ["Alpine light", "Quiet coast", "Forest layers", "Desert evening", "Blue hour", "Island reflections", "Autumn valley", "Northern sky"];

    // Must run before App/DesktopSessionHost acquires its own lease. The caller owns profile cleanup.
    internal static async Task SeedAsync(string profile)
    {
        Directory.CreateDirectory(profile);
        using ProfileLease lease = ProfileLease.Acquire(profile);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(profile, lease);
        using HttpClient client = new(new OfflineOnlyHandler());
        FourChanImageBoard provider = new(client);
        MediaStore store = new();
        byte[][] artwork = Enumerable.Range(0, Subjects.Length).Select(DrawLandscape).ToArray();
        for (int index = 0; index < ThreadCount; index++)
        {
            string threadId = (900000001 + index).ToString(System.Globalization.CultureInfo.InvariantCulture);
            ResourceId id = Hero with { Thread = threadId };
            Uri url = new($"https://boards.4chan.org/wg/thread/{threadId}");
            IThread passiveThread = provider.GetThread(url); // Only identity is used; never monitored.
            string directory = Path.Combine(profile, "Downloads", "4chan", "wg", threadId);
            List<CapturedPost> posts = new();
            int count = index == 0 ? 8 : 3;
            for (int post = 0; post < count; post++)
            {
                int art = (index + post) % artwork.Length;
                string postId = (900000001L + index * 100L + post).ToString(System.Globalization.CultureInfo.InvariantCulture);
                string mediaId = "art-" + art.ToString("00");
                string name = Subjects[art].ToLowerInvariant().Replace(' ', '-') + ".png";
                string path = Path.Combine(directory, name);
                string md5 = Convert.ToBase64String(MD5.HashData(artwork[art]));
                Uri mediaUrl = new("https://showcase.invalid/art/" + name);
                DownloadItem item = new(mediaUrl, path, provider, passiveThread, mediaId,
                    expectedSize: artwork[art].Length, expectedMd5: md5, originalFileName: name);
                using MemoryStream bytes = new(artwork[art], writable: false);
                await store.CommitAsync(item, bytes, CancellationToken.None);
                string firstPostId = (900000001L + index * 100L).ToString(System.Globalization.CultureInfo.InvariantCulture);
                string body = post == 0
                    ? "A small landscape study in light, shape and colour.<br>Original artwork for the MeowChan demo archive. Saved locally for offline browsing."
                    : $"<a href='#p{firstPostId}'>&gt;&gt;{firstPostId}</a><br>Another view from the landscape sketchbook. Layered ridgelines, soft reflections and a quiet evening sky.";
                posts.Add(new CapturedPost(postId, post, PostFieldPresence.All, Epoch.AddMinutes(index + post),
                    Subjects[art], "Demo artist", body, Attachments:
                    [new CapturedAttachment(0, mediaId, Url: mediaUrl.AbsoluteUri, OriginalFileName: name,
                        PlannedPath: path, ExpectedSize: artwork[art].Length, ExpectedMd5: md5)]));
            }
            string title = index == 0 ? "Landscape sketchbook — mountains & reflections"
                : $"Landscape studies {index + 1:00} — {Subjects[index % Subjects.Length]}";
            await catalog.CaptureAsync(new CapturedThreadSnapshot(id, url.AbsoluteUri, title,
                ThreadSourceState.Archived, Epoch.AddMinutes(-index), directory, posts));
        }
        Console.WriteLine($"SHOWCASE seeded {ThreadCount} synthetic archived threads; original procedural Skia artwork; no HTTP.");
    }

    // Geometric illustration, generated from explicit coordinates rather than an external image or model.
    private static byte[] DrawLandscape(int variant)
    {
        const int width = 1600, height = 1000;
        using SKSurface surface = SKSurface.Create(new SKImageInfo(width, height));
        SKCanvas canvas = surface.Canvas;
        SKColor[][] palettes =
        [
            [new(17, 36, 65), new(246, 177, 120), new(30, 62, 82), new(13, 40, 56)],
            [new(25, 55, 78), new(245, 207, 158), new(50, 99, 110), new(20, 60, 77)],
            [new(25, 57, 59), new(204, 215, 171), new(53, 99, 85), new(21, 62, 59)],
            [new(74, 47, 74), new(250, 171, 120), new(139, 81, 91), new(71, 50, 76)]
        ];
        SKColor[] colours = palettes[variant % palettes.Length];
        using SKPaint paint = new() { IsAntialias = true };
        using (SKShader sky = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(0, 760),
                   [colours[0], colours[1]], null, SKShaderTileMode.Clamp))
        { paint.Shader = sky; canvas.DrawRect(0, 0, width, height, paint); paint.Shader = null; }
        paint.Color = new SKColor(255, 223, 163, 220);
        canvas.DrawCircle(1140 - variant * 35, 265 + variant * 8, 74, paint);
        for (int layer = 0; layer < 5; layer++)
        {
            using SKPath ridge = new();
            float baseline = 510 + layer * 68;
            ridge.MoveTo(-100, baseline);
            for (int point = 0; point <= 16; point++)
            {
                float x = point * 110 - 60;
                float y = baseline - (float)(90 + 95 * Math.Sin(point * 1.73 + layer * 1.1 + variant * .43));
                ridge.LineTo(x, y);
            }
            ridge.LineTo(1700, 1000); ridge.LineTo(-100, 1000); ridge.Close();
            float blend = layer / 4f;
            paint.Color = new SKColor(
                (byte)(colours[2].Red + (colours[3].Red - colours[2].Red) * blend),
                (byte)(colours[2].Green + (colours[3].Green - colours[2].Green) * blend),
                (byte)(colours[2].Blue + (colours[3].Blue - colours[2].Blue) * blend));
            canvas.DrawPath(ridge, paint);
        }
        // Lake and fine horizontal highlights give the original image visible detail at 1:1.
        using (SKShader lake = SKShader.CreateLinearGradient(new SKPoint(0, 730), new SKPoint(0, height),
                   [colours[2], colours[0]], null, SKShaderTileMode.Clamp))
        { paint.Shader = lake; canvas.DrawRect(0, 745, width, 255, paint); paint.Shader = null; }
        paint.StrokeWidth = 1.5f;
        for (int line = 0; line < 105; line++)
        {
            float y = 751 + line * 2.35f;
            float centre = 1110 - variant * 32 + (float)Math.Sin(line * 2.37) * 45;
            float half = 12 + line * .7f + (float)Math.Sin(line * .63) * 10;
            paint.Color = new SKColor(255, 212, 151, (byte)(70 - line / 2));
            canvas.DrawLine(centre - half, y, centre + half, y, paint);
        }
        paint.Color = colours[3];
        for (int tree = 0; tree < 36; tree++)
        {
            float x = tree * 19 - 35;
            float y = 897 + (float)Math.Sin(tree * .4 + variant) * 18;
            float h = 65 + (tree * 37 % 95);
            using SKPath pine = new(); pine.MoveTo(x, y - h); pine.LineTo(x + 30, y); pine.LineTo(x - 30, y); pine.Close();
            canvas.DrawPath(pine, paint);
        }
        using SKImage image = surface.Snapshot();
        using SKData png = image.Encode(SKEncodedImageFormat.Png, 100);
        return png.ToArray();
    }

    private sealed class OfflineOnlyHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new InvalidOperationException("Showcase fixtures must never make an HTTP request.");
    }
}
