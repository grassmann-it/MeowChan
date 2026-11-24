using System.Security.Cryptography;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Interfaces;
using MeowChan.Persistence;
using MeowChan.Storage;
using SkiaSharp;

namespace MeowChan.DesktopSmoke;

// The reader's "Saved media" tiles show the local video poster the reader already
// uses for post attachments (cache keyed by the verified bytes), not the
// "no built-in preview" text. Images keep their decoded thumbnail.
internal static class ArchiveSavedMediaPosterChecks
{
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

    private static byte[] Png(int width, int height, SKColor color)
    {
        using SKBitmap bitmap = new(width, height);
        bitmap.Erase(color);
        using SKImage image = SKImage.FromBitmap(bitmap);
        using SKData data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    internal static async Task RunAsync(MainWindow owner)
    {
        // The default tile decoder reads the profile's poster cache; never touch a real profile.
        Require(!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MEOWCHAN_PROFILE")),
            "Saved-media poster smoke requires the isolated runner profile.");
        string profile = Path.Combine(Path.GetTempPath(), "meowchan-saved-media-poster-" + Guid.NewGuid().ToString("N"));
        string media = Path.Combine(profile, "media");
        Directory.CreateDirectory(media);
        byte[] video = new byte[] { 0x1A, 0x45, 0xDF, 0xA3 }.Concat(Guid.NewGuid().ToByteArray()).ToArray();
        byte[] still = Png(80, 60, SKColors.CornflowerBlue);
        string videoHash = Convert.ToHexString(SHA256.HashData(video)).ToLowerInvariant();
        string posters = Path.Combine(Config.GetConfigDirectory(), "cache", "video-posters-v1");
        string poster = Path.Combine(posters, videoHash + ".png");
        ResourceId id = new("offline", "media", "posters");
        ArchivePage? page = null;
        Window? host = null;
        try
        {
            Directory.CreateDirectory(posters);
            await File.WriteAllBytesAsync(poster, Png(160, 90, SKColors.OrangeRed));
            await File.WriteAllBytesAsync(Path.Combine(media, "clip.webm"), video);
            await File.WriteAllBytesAsync(Path.Combine(media, "still.png"), still);
            object Entry(string file, byte[] bytes) => new
            {
                MediaIdentifier = "poster-" + file, SourceUrl = "https://offline.invalid/" + file, FileName = file,
                OriginalFileName = file, Length = bytes.Length, Sha256 = Convert.ToHexString(SHA256.HashData(bytes))
            };
            await File.WriteAllTextAsync(Path.Combine(media, ".meowchan-manifest.json"), JsonSerializer.Serialize(new
            {
                Version = 1, Entries = new Dictionary<string, object> { ["clip.webm"] = Entry("clip.webm", video), ["still.png"] = Entry("still.png", still) }
            }));
            using (ProfileLease lease = ProfileLease.Acquire(profile))
            await using (ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(profile, lease))
                await catalog.CaptureAsync(new CapturedThreadSnapshot(id, "https://offline.invalid/posters", "Saved media posters",
                    ThreadSourceState.Archived, DateTimeOffset.UtcNow, media, Array.Empty<CapturedPost>()));
            page = new ArchivePage(token => ArchiveReader.OpenAsync(profile, token), (_, _) => Task.CompletedTask, _ => { }) { IsAvailable = true };
            host = new Window { Width = 1200, Height = 800, Content = page, Title = "Saved media posters — diagnostic", ShowInTaskbar = false };
            host.Show(owner);
            await page.ActivateAsync();
            await page.LoadThreadAsync(id);
            await page.MediaLoadTask;
            page.FindControl<TabControl>("ArchiveContentTabs")!.SelectedIndex = 1;
            ListBox tiles = page.FindControl<ListBox>("ArchiveMediaTiles")!;
            await Program.WaitUntilAsync(() => { host.UpdateLayout(); return tiles.ItemCount == 2; });
            if (!tiles.IsVisible) CommandBindings.InvokeAction(page.FindControl<ToggleButton>("ArchiveMediaLayoutTiles")!);
            ArchiveMediaTileView View(string name) => tiles.GetVisualDescendants().OfType<ArchiveMediaTileView>()
                .First(view => view.DataContext is ArchiveAssetRow row && (row.Asset.OriginalFileName ?? row.Asset.Path).EndsWith(name, StringComparison.Ordinal));
            await Program.WaitUntilAsync(() =>
            {
                host.UpdateLayout();
                return tiles.IsEffectivelyVisible && tiles.GetVisualDescendants().OfType<ArchiveMediaTileView>().Count() == 2
                    && View("clip.webm").HasThumbnail && View("still.png").HasThumbnail;
            });
            Require(ArchiveMediaTileView.UsesVideoPoster((ArchiveAssetRow)View("clip.webm").DataContext!)
                && !ArchiveMediaTileView.UsesVideoPoster((ArchiveAssetRow)View("still.png").DataContext!),
                "Saved-media tiles chose the wrong preview path.");
            Program.CaptureWindow(host, "smoke-archive-saved-media-video-poster.png");
            Console.WriteLine("SMOKE saved media posters: the reader tile shows the local video poster from the verified-bytes cache; the image keeps its thumbnail; nothing fetched.");
        }
        finally
        {
            page?.EndLifetime();
            host?.Close();
            File.Delete(poster);
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        }
    }
}
