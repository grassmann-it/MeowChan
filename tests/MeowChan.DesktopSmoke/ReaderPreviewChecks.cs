using System.Security.Cryptography;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;
using MeowChan.Persistence;
using MeowChan.Storage;
using MeowChan.Interfaces;
using ArchiveCatalog = MeowChan.Archive.ArchiveCatalog;
using SkiaSharp;

namespace MeowChan.DesktopSmoke;

internal static class ReaderPreviewChecks
{
    internal static async Task RunAsync(MainWindow owner)
    {
        string profile = Path.Combine(Path.GetTempPath(), "meowchan-reader-preview-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(profile);
        Window? window = null;
        ArchivePage? page = null;
        try
        {
            string media = Path.Combine(profile, "media"); Directory.CreateDirectory(media);
            byte[] png = ImageBytes();
            await File.WriteAllBytesAsync(Path.Combine(media, "image.png"), png);
            await File.WriteAllTextAsync(Path.Combine(media, ".meowchan-manifest.json"), JsonSerializer.Serialize(new
            {
                Version = 1, Entries = new Dictionary<string, object> { ["image.png"] = new
                {
                    MediaIdentifier = "image", SourceUrl = "https://offline.invalid/image.png", FileName = "image.png", Length = png.Length,
                    Sha256 = Convert.ToHexString(SHA256.HashData(png)), Md5 = Convert.ToBase64String(MD5.HashData(png))
                } }
            }));
            ResourceId id = new("4chan", "dpt", "110125031");
            using (ProfileLease lease = ProfileLease.Acquire(profile))
            await using (ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(profile, lease))
                await catalog.CaptureAsync(new CapturedThreadSnapshot(id, "https://boards.4chan.org/dpt/thread/110125031", "Reader preview regression",
                    ThreadSourceState.Archived, DateTimeOffset.UtcNow, media, Enumerable.Range(0, 5).Select(index => new CapturedPost(
                        (110125031 + index).ToString(), index, PostFieldPresence.All,
                        Html: "<p>Relaunch of an older downloader. " + string.Join(" ", Enumerable.Repeat("Offline post text.", 20 + index * 10)) + "</p>",
                        Attachments: new[] { new CapturedAttachment(0, "image", OriginalFileName: "image.png") })).ToArray()));
            TaskCompletionSource mediaGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
            page = new ArchivePage(token => ArchiveReader.OpenAsync(profile, token), (_, _) => Task.CompletedTask, _ => { }, async (directory, token) =>
            {
                await mediaGate.Task.WaitAsync(token);
                return await MediaAssetReader.OpenAsync(directory, token);
            });
            Control pane = page.DetachReader();
            window = new Window { Width = 1120, Height = 820, Content = pane, Title = "Private reader regression" };
            window.Show(owner); page.IsAvailable = true;
            await page.LoadThreadAsync(id);
            await page.FocusPostAsync("110125033");
            ListBox posts = page.FindControl<ListBox>("ArchivePosts")!;
            await FlushAsync();
            object? rows = posts.ItemsSource;
            await page.FocusPostAsync("110125033");
            Require(ReferenceEquals(rows, posts.ItemsSource), "Same-page navigation replaced live attachment loaders.");
            Task earlierFocus = page.FocusPostAsync("110125032");
            Task latestFocus = page.FocusPostAsync("110125033");
            await Task.WhenAll(earlierFocus, latestFocus);
            Require((posts.SelectedItem as ArchivePostRow)?.Id == "110125033", "An older deferred focus replaced the latest post navigation.");
            await FlushAsync();
            double before = PostTop(posts, "110125033");
            mediaGate.SetResult(); await page.MediaLoadTask;
            await WaitPreviewAsync(posts, "110125033"); await FlushAsync();
            Require(Math.Abs(PostTop(posts, "110125033") - before) <= 2,
                $"Completing delayed previews moved the focused post: {before} → {PostTop(posts, "110125033")}.");
            ArchiveAttachmentView preview = posts.GetVisualDescendants().OfType<ArchiveAttachmentView>()
                .First(view => view.Thumbnail.Source is Bitmap && view.GetVisualAncestors().OfType<ListBoxItem>()
                    .Any(item => item.DataContext is ArchivePostRow row && row.Id == "110125033"));
            Require(preview.Thumbnail.Bounds.Height <= posts.Bounds.Height * 0.6 && preview.Thumbnail.Bounds.Height >= 120,
                "The large inline image must leave room for post text within a bounded, useful preview.");
            Program.CaptureWindow(window, "smoke-reader-preview-stable.png");
            await page.FocusPostAsync("110125035"); await WaitPreviewAsync(posts, "110125035");
            await page.FocusPostAsync("110125033"); await WaitPreviewAsync(posts, "110125033");
            ScrollViewer scroll = posts.GetVisualDescendants().OfType<ScrollViewer>().First();
            scroll.Offset = new Vector(scroll.Offset.X, scroll.Offset.Y + 208);
            await FlushAsync();
            Require(PostTop(posts, "110125033") < -100, "Refresh regression did not start inside a partially scrolled post.");
            mediaGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
            double refreshTop = PostTop(posts, "110125033");
            await page.LoadThreadAsync(id, resetPage: false);
            Require((posts.SelectedItem as ArchivePostRow)?.Id == "110125033", "Same-thread refresh lost the selected post.");
            Require(Math.Abs(PostTop(posts, "110125033") - refreshTop) <= 2, "Same-thread refresh lost the scroll anchor before media completed.");
            mediaGate.SetResult(); await page.MediaLoadTask; await WaitPreviewAsync(posts, "110125033"); await FlushAsync();
            Require(Math.Abs(PostTop(posts, "110125033") - refreshTop) <= 2, "Refreshed previews moved the retained post.");
            // Reparenting the same controls must resume visible thumbnails after detach cancellation.
            window.Content = null; await FlushAsync(); window.Content = pane;
            await WaitPreviewAsync(posts, "110125033");
            page.EndLifetime(); window.Content = null;
            await CheckCancellationAndLateResultsAsync(window);
            Console.WriteLine("SMOKE reader preview: five-post delayed images keep geometry/scroll; same-page focus retains loaders; refresh retains selection/anchor; detach/remount reloads; source cancellation/timeout terminate Loading and stale results cannot replace current pixels.");
        }
        finally
        {
            page?.EndLifetime(); if (window != null) { window.Content = null; window.Close(); }
            if (Directory.Exists(profile)) Directory.Delete(profile, true);
        }
    }

    private static async Task CheckCancellationAndLateResultsAsync(Window window)
    {
        ArchiveAttachmentView view = new();
        window.Content = view;
        ArchiveAttachmentRow Row(Func<CancellationToken, Task<Bitmap?>> load) => new(new CapturedAttachment(0, "local", OriginalFileName: "image.png"))
            { LargePreview = true, LoadThumbnail = load };
        view.DataContext = Row(_ => Task.FromCanceled<Bitmap?>(new CancellationToken(true)));
        await Program.WaitUntilAsync(() => view.PreviewStatus == Texts.T("archive.previewLoadFailed"));
        double height = view.Bounds.Height;
        TaskCompletionSource<Bitmap?> old = new(TaskCreationOptions.RunContinuationsAsynchronously);
        bool started = false;
        view.DataContext = Row(_ => { started = true; return old.Task; });
        await Program.WaitUntilAsync(() => started);
        Bitmap current = NewBitmap();
        view.DataContext = Row(_ => Task.FromResult<Bitmap?>(current));
        await Program.WaitUntilAsync(() => ReferenceEquals(view.Thumbnail.Source, current));
        old.SetResult(NewBitmap()); await FlushAsync();
        Require(ReferenceEquals(view.Thumbnail.Source, current), "An obsolete cancelled preview replaced current pixels.");
        TaskCompletionSource<Bitmap?> slow = new(TaskCreationOptions.RunContinuationsAsynchronously);
        view.LoadTimeout = TimeSpan.FromMilliseconds(100);
        view.DataContext = Row(_ => slow.Task);
        await Program.WaitUntilAsync(() => view.PreviewStatus == Texts.T("archive.previewLoadFailed"));
        slow.SetResult(NewBitmap()); await FlushAsync();
        Require(view.Thumbnail.Source == null && Math.Abs(view.Bounds.Height - height) <= 2,
            "Timeout/late completion changed the stable preview or displayed obsolete pixels.");
        view.DataContext = new ArchiveAttachmentRow(new CapturedAttachment(0, "video-poster", OriginalFileName: "saved-video.webm"))
        { LoadThumbnail = _ => Task.FromResult<Bitmap?>(NewBitmap()) };
        await Program.WaitUntilAsync(() => view.Thumbnail.Source is Bitmap);
        await FlushAsync();
        Border play = view.GetVisualDescendants().OfType<Border>().Single(control => control.Name == "VideoPlayOverlay");
        Point center = play.TranslatePoint(new Point(play.Bounds.Width / 2, play.Bounds.Height / 2), view.Thumbnail)
            ?? throw new InvalidOperationException("Video play overlay has no thumbnail position.");
        Require(play.IsEffectivelyVisible && play.Bounds.Width >= 32 && play.Bounds.Width <= 48
            && Math.Abs(center.X - view.Thumbnail.Bounds.Width / 2) <= 2
            && Math.Abs(center.Y - view.Thumbnail.Bounds.Height / 2) <= 2,
            "Video preview play affordance is not centered and bounded over the poster.");
        Program.CaptureWindow(window, "smoke-video-poster-play-overlay.png");
        window.Content = null; await FlushAsync();
        Require(view.Thumbnail.Source == null, "Detached attachment retained pixels.");
    }
    private static double PostTop(ListBox posts, string id)
    {
        ArchivePostRow row = posts.ItemsSource!.Cast<ArchivePostRow>().Single(row => row.Id == id);
        posts.UpdateLayout();
        return (posts.ContainerFromItem(row) as Control)?.TranslatePoint(default, posts)?.Y
            ?? throw new InvalidOperationException("Target post has no realized position: " + id);
    }
    private static async Task WaitPreviewAsync(ListBox posts, string id) => await Program.WaitUntilAsync(() => posts.GetVisualDescendants()
        .OfType<ArchiveAttachmentView>().Any(view => view.Thumbnail.Source is Bitmap && view.GetVisualAncestors().OfType<ListBoxItem>()
            .Any(item => item.DataContext is ArchivePostRow row && row.Id == id)));
    private static byte[] ImageBytes()
    {
        using SKBitmap bitmap = new(1800, 1000); bitmap.Erase(SKColors.CornflowerBlue);
        using SKImage image = SKImage.FromBitmap(bitmap); using SKData data = image.Encode(SKEncodedImageFormat.Png, 100); return data.ToArray();
    }
    private static Bitmap NewBitmap() => new(new MemoryStream(ImageBytes()));
    private static async Task FlushAsync()
    {
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Loaded);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
