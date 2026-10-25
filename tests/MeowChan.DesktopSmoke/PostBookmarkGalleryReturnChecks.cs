using System.Security.Cryptography;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Interfaces;
using MeowChan.Persistence;
using MeowChan.Testing;
using SkiaSharp;

namespace MeowChan.DesktopSmoke;

/// <summary>Exercises the production shell's bookmark → reader → gallery → reader → overview route.</summary>
internal static class PostBookmarkGalleryReturnChecks
{
    internal static async Task RunAsync(MainWindow owner)
    {
        string profile = Path.Combine(TemporaryWorkspacePaths.TempRoot,
            "meowchan-bookmark-gallery-return-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(profile);
        DesktopSessionHost? session = null;
        MainWindow? shell = null;
        ResourceId thread = new("4chan", "g", "9100");
        PostBookmarkKey key = new(thread, "9100");
        string? captureFixture = null;
        try
        {
            await SeedAsync(profile, thread);
            if (Environment.GetEnvironmentVariable("MEOWCHAN_POST_BOOKMARK_COMPARISON_ONLY") == "1")
                captureFixture = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(profile, "media", ".meowchan-manifest.json"))));
            new Config { DownloadsPaused = true, SaveUrlsOnExit = false,
                CloseToTray = false, MinimizeToTray = false, WorkspaceMode = "Advanced" }.SaveConfig(profile);
            session = new DesktopSessionHost(profile, Array.Empty<IImageBoard>());
            shell = new MainWindow(session);
            shell.Show(owner);
            await Program.WaitUntilAsync(() => session.State == DesktopSessionState.Ready
                && shell.FindControl<Button>("AddUrlButton")!.IsEnabled);
            await session.SavePostBookmarkAsync(key, "Keep exact reader A", 0, false);
            shell.Width = 1280; shell.Height = 900;
            Click(shell.FindControl<MenuItem>("ArchiveMenuItem")!);
            ArchivePage page = shell.ArchivePage;
            await Program.WaitUntilAsync(() => !page.IsBusy);
            Button manager = page.FindControl<ActionToolBar>("ArchiveResultActions")!.Items
                .OfType<Button>().Single(button => button.Name == "ArchivePostBookmarks");
            await Program.WaitUntilAsync(() => manager.Command?.CanExecute(manager.CommandParameter) == true);
            Execute(manager);
            await Program.WaitUntilAsync(() => shell.OwnedWindows.OfType<PostBookmarksDialog>().Any());
            PostBookmarksDialog overview = shell.OwnedWindows.OfType<PostBookmarksDialog>().Single();
            ListBox bookmarks = overview.FindControl<ListBox>("Bookmarks")!;
            Button open = overview.FindControl<Button>("OpenTarget")!;
            await Program.WaitUntilAsync(() => bookmarks.ItemCount == 1 && open.IsEnabled);
            Require((bookmarks.SelectedItem as PostBookmark)?.Key == key,
                "Shell bookmark overview did not select the saved exact target.");
            Execute(open);
            ContentControl readerHost = shell.FindControl<ContentControl>("ReaderHost")!;
            ListBox posts = page.FindControl<ListBox>("ArchivePosts")!;
            await Program.WaitUntilAsync(() => readerHost.IsVisible && !overview.IsVisible
                && SelectedPost(posts) == key.Post && !page.IsBusy && open.IsEnabled);
            await page.MediaLoadTask;
            if (Environment.GetEnvironmentVariable("MEOWCHAN_POST_BOOKMARK_COMPARISON_ONLY") == "1")
                await PostBookmarkVisualEvidence.CaptureMatrixAsync(shell, shell, "exact-reader-before-gallery", captureFixture);
            await FlushAsync(shell);
            ScrollViewer scroll = posts.GetVisualDescendants().OfType<ScrollViewer>().First();
            scroll.Offset = new Vector(scroll.Offset.X, scroll.Offset.Y + 110);
            await FlushAsync(shell);
            double originTop = PostTop(posts, key.Post);
            Require(originTop < -20 && SelectedPost(posts) == key.Post,
                "Shell bookmark fixture did not establish A's partial reader anchor.");

            // Open and move through actual controls. No shell flags or return delegates are fabricated.
            Click(page.FindControl<Button>("ArchiveOpenGallery")!);
            ContentControl galleryHost = shell.FindControl<ContentControl>("GalleryHost")!;
            await Program.WaitUntilAsync(() => galleryHost.IsVisible && galleryHost.Content is GalleryViewer);
            GalleryViewer gallery = (GalleryViewer)galleryHost.Content!;
            await Program.WaitUntilAsync(() => gallery.CaptureState().PostId == "9100"
                && GalleryControl<Image>(gallery, "GalleryImage").Source is Bitmap
                && GalleryControl<Button>(gallery, "GalleryNext").IsEnabled);
            Click(GalleryControl<Button>(gallery, "GalleryNext"));
            await Program.WaitUntilAsync(() => gallery.CaptureState().PostId == "9101"
                && GalleryControl<Image>(gallery, "GalleryImage").Source is Bitmap);
            if (Environment.GetEnvironmentVariable("MEOWCHAN_POST_BOOKMARK_COMPARISON_ONLY") == "1")
                await PostBookmarkVisualEvidence.CaptureMatrixAsync(shell, shell, "gallery-cursor-b", captureFixture);
            Require(!overview.IsVisible && readerHost.IsVisible,
                "Moving the gallery cursor prematurely ended the bookmark reader route.");
            Click(GalleryControl<Button>(gallery, "GalleryClose"));
            await Program.WaitUntilAsync(() => !galleryHost.IsVisible);
            await shell.GalleryCleanupTask;
            await FlushAsync(shell);
            await Program.WaitUntilAsync(() => SelectedPost(posts) == key.Post
                && Math.Abs(PostTop(posts, key.Post) - originTop) < 2);
            // A second settled observation rejects an asynchronous FollowGalleryPostAsync(B) overwrite.
            await Task.Delay(150);
            await FlushAsync(shell);
            Require(readerHost.IsVisible && !overview.IsVisible
                && page.CurrentThread?.Summary.Id == thread && SelectedPost(posts) == key.Post
                && Math.Abs(PostTop(posts, key.Post) - originTop) < 2,
                "Closing gallery cursor B lost bookmark reader A's identity or partial anchor.");
            if (Environment.GetEnvironmentVariable("MEOWCHAN_POST_BOOKMARK_COMPARISON_ONLY") == "1")
                await PostBookmarkVisualEvidence.CaptureMatrixAsync(shell, shell, "exact-reader-returned-from-gallery", captureFixture);
            Click(page.FindControl<Button>("ArchiveCloseReader")!);
            await Program.WaitUntilAsync(() => !readerHost.IsVisible && overview.IsVisible && open.IsEnabled);
            Require((bookmarks.SelectedItem as PostBookmark)?.Key == key
                && overview.FindControl<TextBox>("Note")!.Text == "Keep exact reader A",
                "Closing the restored bookmark reader lost its overview selection or note.");
            Console.WriteLine($"SMOKE bookmark shell return: overview A → reader A top={originTop:F2} → gallery A/B → reader A exact anchor → same overview passed.");
        }
        finally
        {
            if (shell != null)
            {
                shell.Close();
                await Program.WaitUntilAsync(() => !shell.IsVisible);
                await shell.GalleryCleanupTask;
            }
            else if (session != null) await session.DisposeAsync();
            try { Directory.Delete(profile, recursive: true); } catch (IOException) { }
            owner.Activate();
        }
    }

    private static async Task SeedAsync(string profile, ResourceId thread)
    {
        string media = Path.Combine(profile, "media");
        Directory.CreateDirectory(media);
        Dictionary<string, object> entries = new();
        for (int index = 0; index < 2; index++)
        {
            string name = index + ".png";
            using SKBitmap bitmap = new(80, 60);
            using (SKCanvas canvas = new(bitmap)) canvas.Clear(index == 0 ? SKColors.Navy : SKColors.Gold);
            using SKImage image = SKImage.FromBitmap(bitmap);
            using SKData data = image.Encode(SKEncodedImageFormat.Png, 100);
            byte[] bytes = data.ToArray();
            await File.WriteAllBytesAsync(Path.Combine(media, name), bytes);
            entries[name] = new { MediaIdentifier = index.ToString(), SourceUrl = "https://offline.invalid/" + name,
                FileName = name, Length = bytes.Length, Sha256 = Convert.ToHexString(SHA256.HashData(bytes)),
                Md5 = Convert.ToBase64String(MD5.HashData(bytes)) };
        }
        await File.WriteAllTextAsync(Path.Combine(media, ".meowchan-manifest.json"),
            JsonSerializer.Serialize(new { Version = 1, Entries = entries }));
        using ProfileLease lease = ProfileLease.Acquire(profile);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(profile, lease);
        CapturedPost[] posts = await StrictMediaFixture.BindAsync(media, thread,
            [new CapturedPost("9100", 0, PostFieldPresence.All,
                Html: "<p>" + string.Join(" ", Enumerable.Repeat("Long saved reader A anchor.", 500)) + "</p>",
                Attachments: [new CapturedAttachment(0, "0", OriginalFileName: "0.png")]),
             new CapturedPost("9101", 1, PostFieldPresence.All, Html: "<p>Gallery cursor B.</p>",
                Attachments: [new CapturedAttachment(0, "1", OriginalFileName: "1.png")])]);
        await catalog.CaptureAsync(new CapturedThreadSnapshot(thread,
            "https://boards.4chan.org/g/thread/9100", "Bookmark gallery exact return",
            ThreadSourceState.Archived, DateTimeOffset.UtcNow, media, posts));
    }

    private static string? SelectedPost(ListBox posts) => (posts.SelectedItem as ArchivePostRow)?.Id;
    private static double PostTop(ListBox posts, string id) => posts.GetVisualDescendants().OfType<ListBoxItem>()
        .Where(item => item.DataContext is ArchivePostRow row && row.Id == id)
        .Select(item => item.TranslatePoint(default, posts)?.Y ?? double.NaN).DefaultIfEmpty(double.NaN).Single();
    private static T GalleryControl<T>(GalleryViewer viewer, string name) where T : Control =>
        viewer.GetGalleryVisualDescendants().OfType<T>().Single(control => control.Name == name);
    private static void Execute(Button button)
    {
        Require(button.Command?.CanExecute(button.CommandParameter) == true,
            "Bookmark shell command is unavailable: " + button.Name);
        button.Command!.Execute(button.CommandParameter);
    }
    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static void Click(MenuItem item) => item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
    private static async Task FlushAsync(Window window)
    {
        window.UpdateLayout();
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Loaded);
        window.UpdateLayout();
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        window.UpdateLayout();
    }
    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}
