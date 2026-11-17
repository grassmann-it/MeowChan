using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MeowChan.Archive;
using MeowChan.Interfaces;
using MeowChan.Persistence;
using MeowChan.Storage;
using MeowChan.Tags;
using SkiaSharp;

namespace MeowChan.DesktopSmoke;

// No input profile, directory scan, HTTP client, or copied media. All bytes originate here or in ffmpeg's lavfi.
internal static class ReviewFixtureData
{
    internal static readonly ResourceId Hero = new("review-fixture", "synthetic", "100000");
    internal static readonly DateTimeOffset Epoch = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    internal static readonly int[] TagCounts = [0, 1, 8, 30, 128];
    internal static readonly int[] ThreadSizes = [1, 50, 250, 1250];
    internal static string Hex(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    internal static Guid TagId(int seed, int n) => new(SHA256.HashData(Encoding.UTF8.GetBytes($"review:{seed}:tag:{n}")).AsSpan(0, 16));
    internal static string TagName(int n) => n switch
    {
        0 => "synthetic_合成試験_태그",
        1 => "synthetic_Donaudampfschifffahrtsgesellschaftskapitänsarbeitsplatzbeschreibung",
        2 => "synthetic_" + new string('x', 100),
        _ => $"synthetic_tag_{n:D5}"
    };

    internal static async Task GenerateAsync(string profile, int count, int seed)
    {
        if (Directory.Exists(profile)) throw new IOException("Refusing existing fixture profile: " + profile);
        if (count is not (0 or 1 or 100 or 1000 or 10000)) throw new ArgumentException("media must be 0, 1, 100, 1000, 10000");
        Directory.CreateDirectory(profile);
        Stopwatch timer = Stopwatch.StartNew();
        byte[][] images = Enumerable.Range(0, 8).Select(n => Image(seed, n)).ToArray();
        string generatedVideo = Path.Combine(profile, "synthetic-generated.webm");
        if (count > 1)
        {
            using Process ffmpeg = Process.Start(new ProcessStartInfo("ffmpeg")
            {
                ArgumentList = { "-nostdin", "-v", "error", "-f", "lavfi", "-i", $"color=c=0x{Hex(Encoding.UTF8.GetBytes(seed.ToString()))[..6]}:s=320x180:r=8", "-t", "0.5", "-an", "-c:v", "libvpx", "-threads", "1", "-fflags", "+bitexact", "-flags:v", "+bitexact", "-metadata", "title=Synthetic review fixture", generatedVideo },
                UseShellExecute = false
            }) ?? throw new IOException("ffmpeg unavailable");
            await ffmpeg.WaitForExitAsync();
            if (ffmpeg.ExitCode != 0) throw new IOException("Generated video failed");
        }
        byte[]? video = count > 1 ? await File.ReadAllBytesAsync(generatedVideo) : null;
        List<object> assignments = new();
        using (ProfileLease lease = ProfileLease.Acquire(profile))
        {
            await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(profile, lease);
            int remaining = count, global = 0, thread = 0;
            while (remaining > 0)
            {
                int size = Math.Min(remaining, ThreadSizes[Math.Min(thread, ThreadSizes.Length - 1)]);
                ResourceId id = Hero with { Thread = (100000 + thread).ToString() };
                string directory = Path.Combine(profile, "media", $"synthetic-thread-{thread:D4}");
                Directory.CreateDirectory(directory);
                Dictionary<string, object> entries = new(), assets = new();
                Dictionary<string, MediaAttachmentBinding> bindings = new();
                List<CapturedPost> posts = new();
                for (int p = 0; p < size; p++, global++)
                {
                    bool isVideo = video != null && global % 97 == 96;
                    // Append a deterministic PNG trailer so each medium is distinct without expensive re-encoding.
                    byte[] bytes = isVideo ? video! : [.. images[global % images.Length], .. Encoding.UTF8.GetBytes($"review:{seed}:{global}")];
                    string name = $"synthetic-{global:D5}" + (isVideo ? ".webm" : ".png");
                    string mediaId = $"synthetic-{global:D5}", postId = (10000000 + global).ToString();
                    string sha = Hex(bytes), md5 = Convert.ToBase64String(MD5.HashData(bytes));
                    await File.WriteAllBytesAsync(Path.Combine(directory, name), bytes);
                    string url = "https://review.invalid/" + name;
                    MediaProviderMetadata provider = new(mediaId, "original", url, name, bytes.Length, md5, sha);
                    string key = MediaRevisionKeys.CreateAttachmentKey(id, postId, 0, provider);
                    string assetId = Hex(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new[] { name, mediaId, name, sha })));
                    var entry = new { MediaIdentifier = mediaId, Variant = "original", SourceUrl = url, FileName = name, OriginalFileName = name, Length = bytes.Length, Md5 = md5, Sha256 = sha };
                    entries[name] = entry;
                    assets[assetId] = new { RequestedFileName = name, Entry = entry };
                    bindings[key] = new(new(id, postId, 0, key), MediaRevisionKeys.CreateAcquisitionKey(id, provider), assetId, provider,
                        new(bytes.Length, md5, sha), new(MediaAcceptanceKind.Strict, false, false, Epoch));
                    posts.Add(new CapturedPost(postId, p, PostFieldPresence.All, PostedAt: Epoch.AddSeconds(global),
                        Html: $"Synthetic post {global}. 合成試験。 Arbeitsplatzdarstellungsgrößenüberprüfung. " + new string('W', global % 7 == 0 ? 600 : 12),
                        Attachments: [new CapturedAttachment(0, mediaId, "original", url, name, ExpectedSize: bytes.Length, ExpectedMd5: md5) { ExpectedSha256 = sha }]));
                    for (int t = 0; global < 1000 && t < TagCounts[global % TagCounts.Length]; t++)
                        assignments.Add(new { Tag = TagId(seed, (global * 31 + t) % PrivateTagStore.MaximumTags), Source = id.Source, Board = id.Board, Thread = id.Thread,
                            Post = postId, Position = 0, RevisionKey = key, MediaId = mediaId, Variant = "original", Role = "original", Sha256 = sha });
                }
                await File.WriteAllTextAsync(Path.Combine(directory, ".meowchan-manifest.json"), JsonSerializer.Serialize(new { Version = 3, Entries = entries, Assets = assets, AttachmentBindings = bindings }));
                string title = $"Synthetic thread {thread:D4} 合成試験 — Arbeitsplatzdarstellungsgrößenüberprüfung " + (thread % 3 == 0 ? new string('W', 400) : "short title");
                await catalog.CaptureAsync(new CapturedThreadSnapshot(id, $"https://review.invalid/synthetic/{id.Thread}", title, ThreadSourceState.Archived, Epoch.AddMinutes(thread), directory, posts));
                remaining -= size;
                thread++;
            }
        }
        int vocabulary = count == 0 ? 0 : PrivateTagStore.MaximumTags;
        await File.WriteAllTextAsync(Path.Combine(profile, "private-tags.json"), JsonSerializer.Serialize(new { Version = 1, Revision = 1,
            Tags = Enumerable.Range(0, vocabulary).Select(n => new { Id = TagId(seed, n), Name = TagName(n) }), Threads = Array.Empty<object>(), Media = assignments }));
        // The product caps private vocabulary. Preserve the several-thousand probe without writing an invalid live store.
        await File.WriteAllTextAsync(Path.Combine(profile, "synthetic-vocabulary-probe.json"), JsonSerializer.Serialize(Enumerable.Range(0, 4096).Select(TagName)));
        new Config { DownloadPath = Path.Combine(profile, "Downloads"), Theme = "Dark", Language = "en", WorkspaceMode = "Advanced", DownloadsPaused = true, SaveUrlsOnExit = false }.SaveConfig();
        await File.WriteAllTextAsync(Path.Combine(profile, "fixture.json"), JsonSerializer.Serialize(new { schema_version = 1, seed, media = count, tag_counts = TagCounts,
            thread_sizes = ThreadSizes, admitted_vocabulary = vocabulary, requested_vocabulary = 4096, vocabulary_limit = PrivateTagStore.MaximumTags,
            generated_ms = timer.Elapsed.TotalMilliseconds, privacy = "entirely synthetic; no source profile read", video = count > 1 }));
    }

    private static byte[] Image(int seed, int variant)
    {
        (int w, int h) = variant switch { 0 => (3840, 2160), 1 => (120, 800), 2 => (900, 100), _ => (240, 160) };
        using SKSurface surface = SKSurface.Create(new SKImageInfo(w, h));
        byte[] colour = SHA256.HashData(Encoding.UTF8.GetBytes($"review:{seed}:{variant}"));
        surface.Canvas.Clear(new SKColor(colour[0], colour[1], colour[2]));
        using SKPaint paint = new() { Color = SKColors.White, IsAntialias = true };
        surface.Canvas.DrawCircle(w / 2f, h / 2f, Math.Min(w, h) / 4f, paint);
        using SKImage image = surface.Snapshot();
        using SKData png = image.Encode(SKEncodedImageFormat.Png, 100);
        return png.ToArray();
    }
}
