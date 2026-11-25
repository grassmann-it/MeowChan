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
    internal static async Task RunAsync(MainWindow owner, bool returnOnly = false)
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
            string manifestPath = Path.Combine(media, ".meowchan-manifest.json");
            object entry = new
            {
                MediaIdentifier = "image", Variant = "original", SourceUrl = "https://offline.invalid/image.png",
                FileName = "image.png", OriginalFileName = "image.png", Length = png.Length,
                Sha256 = Convert.ToHexString(SHA256.HashData(png)).ToLowerInvariant(), Md5 = Convert.ToBase64String(MD5.HashData(png))
            };
            await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(new
                { Version = 1, Entries = new Dictionary<string, object> { ["image.png"] = entry } }));
            MediaAssetSnapshot manifest = await MediaAssetReader.OpenAsync(media);
            MediaAssetReference asset = manifest.GetPage().Single();
            ResourceId id = new("4chan", "dpt", "110125031");
            ResourceId other = new("4chan", "dpt", "110125040");
            MediaProviderMetadata provider = new("image", "original", "https://offline.invalid/image.png", "image.png",
                png.Length, Convert.ToBase64String(MD5.HashData(png)), Convert.ToHexString(SHA256.HashData(png)).ToLowerInvariant());
            MediaActualIntegrity actual = new(asset.Length, asset.Md5, asset.Sha256);
            Require(ProviderIntegrityAssessment.Assess(provider, actual).IsStrictMatch,
                "The reader preview fixture provider values must strictly match its saved PNG.");
            CapturedPost[] captured = Enumerable.Range(0, 5).Select(index => new CapturedPost(
                (110125031 + index).ToString(), index, PostFieldPresence.All,
                Html: "<p>Relaunch of an older downloader. " + string.Join(" ", Enumerable.Repeat("Offline post text.", 20 + index * 10)) + "</p>",
                Attachments: new[] { new CapturedAttachment(0, provider.MediaId, provider.Variant, provider.Url,
                    provider.OriginalFileName, ExpectedSize: provider.ExpectedSize, ExpectedMd5: provider.ExpectedMd5)
                    { ExpectedSha256 = provider.ExpectedSha256 } })).ToArray();
            // These are controlled consumer observations, each with its own exact strict receipt.
            Dictionary<string, MediaAttachmentBinding> bindings = captured.ToDictionary(
                post => MediaRevisionKeys.CreateAttachmentKey(id, post.PostId, 0, provider), post =>
                {
                    string key = MediaRevisionKeys.CreateAttachmentKey(id, post.PostId, 0, provider);
                    return new MediaAttachmentBinding(new(id, post.PostId, 0, key), MediaRevisionKeys.CreateAcquisitionKey(id, provider),
                        asset.AssetId, provider, actual, new(MediaAcceptanceKind.Strict, false, false, DateTimeOffset.UtcNow));
                });
            await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(new
            {
                Version = 3, Entries = new Dictionary<string, object> { ["image.png"] = entry },
                Assets = new Dictionary<string, object> { [asset.AssetId] = new { RequestedFileName = "image.png", Entry = entry } },
                AttachmentBindings = bindings
            }));
            manifest = await MediaAssetReader.OpenAsync(media);
            using (ProfileLease lease = ProfileLease.Acquire(profile))
            await using (ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(profile, lease))
            {
                await catalog.CaptureAsync(new CapturedThreadSnapshot(id, "https://boards.4chan.org/dpt/thread/110125031", "Reader preview regression",
                    ThreadSourceState.Archived, DateTimeOffset.UtcNow, media, captured));
                if (returnOnly)
                    await catalog.CaptureAsync(new CapturedThreadSnapshot(other, "https://boards.4chan.org/dpt/thread/110125040", "Unsaved attachment",
                        ThreadSourceState.Archived, DateTimeOffset.UtcNow, Path.Combine(profile, "unsaved"),
                        [captured[0] with { PostId = other.Thread! }]));
                await using ArchiveReader reader = await ArchiveReader.OpenAsync(profile);
                ArchivedThreadView observed = (await reader.ReadThreadAsync(id))!;
                Require(observed.PostCount == 5, "The reader preview fixture lost captured observations.");
                foreach (CapturedPost post in observed.GetPosts())
                {
                    CapturedAttachment attachment = post.Attachments!.Single();
                    ResolvedMediaAttachment? resolved = MediaAttachmentResolver.Resolve(manifest, id, post.PostId, attachment);
                    Require(attachment.AttachmentRevisionKey != null && resolved is { IsLegacy: false }
                        && resolved.Binding?.Acceptance.Kind == MediaAcceptanceKind.Strict && resolved.Asset.AssetId == asset.AssetId,
                        "A new reader preview observation has no exact strict saved-media binding: " + post.PostId);
                }
            }
            GatedMediaReader mediaReader = new(media);
            page = new ArchivePage(token => ArchiveReader.OpenAsync(profile, token), (_, _) => Task.CompletedTask, _ => { }, mediaReader.OpenAsync);
            Control pane = page.DetachReader();
            window = new Window { Width = 1120, Height = 820, Content = pane, Title = "Private reader regression" };
            window.Show(owner); page.IsAvailable = true;
            await page.LoadThreadAsync(id);
            await page.FocusPostAsync("110125033");
            ListBox posts = page.FindControl<ListBox>("ArchivePosts")!;
            await SmokeDispatcher.SettleAsync();
            if (returnOnly)
            {
                await CheckPreviewReturnsAsync(page, posts, id, other, mediaReader);
                return;
            }
            object? rows = posts.ItemsSource;
            await page.FocusPostAsync("110125033");
            Require(ReferenceEquals(rows, posts.ItemsSource), "Navigation within the thread replaced live attachment loaders.");
            Task earlierFocus = page.FocusPostAsync("110125032");
            Task latestFocus = page.FocusPostAsync("110125033");
            await Task.WhenAll(earlierFocus, latestFocus);
            Require((posts.SelectedItem as ArchivePostRow)?.Id == "110125033", "An older deferred focus replaced the latest post navigation.");
            await SmokeDispatcher.SettleAsync();
            double before = PostTop(posts, "110125033");
            mediaReader.Gate.SetResult(); await page.MediaLoadTask;
            await WaitPreviewAsync(posts, "110125033"); await SmokeDispatcher.SettleAsync();
            // Stable previews: the header pass reserves every post of this five-post fixture at its picture's size (1800 x 1000), not only the visible ones.
            await page.PreviewSizePassTask; await SmokeDispatcher.SettleAsync();
            Require(PageAttachments(posts).All(row => row.KnownAspect is { } aspect && Math.Abs(aspect - 1.8) < 0.02),
                "The header read did not reserve every nearby preview at the picture's size before it was shown.");
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
            await SmokeDispatcher.SettleAsync();
            Require(PostTop(posts, "110125033") < -100, "Refresh regression did not start inside a partially scrolled post.");
            mediaReader.Hold();
            double refreshTop = PostTop(posts, "110125033");
            string refreshBefore = DescribeRefreshAnchor("before", window, pane, page, posts, refreshTop);
            await page.LoadThreadAsync(id, resetPage: false);
            Require((posts.SelectedItem as ArchivePostRow)?.Id == "110125033", "Same-thread refresh lost the selected post.");
            double refreshedTop = PostTop(posts, "110125033");
            // Rebuilt rows take their sizes from the session's size memory before the list measures them.
            Require(PageAttachments(posts).All(row => row.KnownAspect is { } aspect && Math.Abs(aspect - 1.8) < 0.02),
                "Rebuilt reader rows were not reserved from the remembered preview sizes.");
            double refreshedHeight = PostHeight(posts, "110125033");
            Console.WriteLine("SMOKE reader refresh anchor: " + refreshBefore);
            Console.WriteLine("SMOKE reader refresh anchor: " + DescribeRefreshAnchor("after", window, pane, page, posts, refreshedTop));
            Require(Math.Abs(refreshedTop - refreshTop) <= 2,
                $"Same-thread refresh lost the scroll anchor before media completed: {refreshTop:R} → {refreshedTop:R}.");
            mediaReader.Gate.SetResult(); await page.MediaLoadTask; await WaitPreviewAsync(posts, "110125033"); await SmokeDispatcher.SettleAsync();
            Require(Math.Abs(PostTop(posts, "110125033") - refreshTop) <= 2, "Refreshed previews moved the retained post.");
            Require(Math.Abs(PostHeight(posts, "110125033") - refreshedHeight) <= 0.5,
                $"The post's height changed when its preview arrived: {refreshedHeight:R} → {PostHeight(posts, "110125033"):R}.");
            // Reparenting the same controls must resume visible thumbnails after detach cancellation.
            window.Content = null; await SmokeDispatcher.SettleAsync(); window.Content = pane;
            await WaitPreviewAsync(posts, "110125033");
            page.EndLifetime(); window.Content = null;
            await CheckReservedHeightAsync(window);
            await CheckCancellationAndLateResultsAsync(window);
            Console.WriteLine("SMOKE reader preview: five-post delayed images keep geometry/scroll; header-read and remembered sizes keep the post height when the preview arrives; focus within the thread retains loaders; refresh retains selection/anchor; detach/remount reloads; source cancellation/timeout terminate Loading and stale results cannot replace current pixels.");
        }
        finally
        {
            page?.EndLifetime(); if (window != null) { window.Content = null; window.Close(); }
            if (Directory.Exists(profile)) Directory.Delete(profile, true);
        }
    }

    private sealed class GatedMediaReader(string savedDirectory)
    {
        private int _reads;
        private TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Gate => Volatile.Read(ref _gate);
        internal int Reads => Volatile.Read(ref _reads);
        internal volatile bool Unavailable;
        internal void Hold() => Volatile.Write(ref _gate, new(TaskCreationOptions.RunContinuationsAsynchronously));

        internal async Task<MediaAssetSnapshot> OpenAsync(string directory, CancellationToken token)
        {
            if (directory == savedDirectory)
            {
                Task gate = Gate.Task;
                Interlocked.Increment(ref _reads);
                await gate.WaitAsync(token);
                if (Unavailable) throw new IOException("Fixture manifest temporarily unreadable");
            }
            return await MediaAssetReader.OpenAsync(directory, token);
        }
    }

    private static async Task CheckPreviewReturnsAsync(ArchivePage page, ListBox posts, ResourceId original, ResourceId other, GatedMediaReader media)
    {
        const string target = "110125033";
        ArchiveAttachmentView? PreviewFor(string id) => posts.Items.OfType<ArchivePostRow>().FirstOrDefault(post => post.Id == id) is { } row
            ? posts.ContainerFromItem(row)?.GetVisualDescendants().OfType<ArchiveAttachmentView>().FirstOrDefault() : null;

        await Program.WaitUntilAsync(() => media.Reads == 1);
        Require(page.IsMediaLoading, "The origin must be captured before its saved-media scan completes.");
        Task firstScan = page.MediaLoadTask;
        ArchivedThreadView? origin = page.CurrentThread;
        Action restore = page.CaptureGalleryOrigin();
        page.SetObscuredByGallery(true);
        TabControl tabs = page.FindControl<TabControl>("ArchiveContentTabs")!;
        tabs.SelectedItem = page.FindControl<TabItem>("ArchiveMediaTab");
        await SmokeDispatcher.SettleAsync();
        page.SetObscuredByGallery(false);
        restore();
        media.Gate.SetResult();
        await page.MediaLoadTask;
        await Program.WaitUntilAsync(() => PreviewFor(target) is { } view && (view.Thumbnail.Source is Bitmap || !string.IsNullOrEmpty(view.PreviewStatus)));
        Require(PreviewFor(target)?.Thumbnail.Source is Bitmap, "Returning from Media used an empty saved-media inventory: " + PreviewFor(target)?.PreviewStatus);
        Require(media.Reads == 1 && ReferenceEquals(firstScan, page.MediaLoadTask) && ReferenceEquals(origin, page.CurrentThread),
            "Returning to the current reader replaced its running manifest scan or re-read its thread.");

        restore = page.CaptureGalleryOrigin();
        await page.LoadThreadAsync(other);
        await page.MediaLoadTask;
        restore();
        await WaitPreviewAsync(posts, target);
        Require(media.Reads == 1 && ReferenceEquals(origin, page.CurrentThread), "A complete origin inventory was scanned again after a thread jump.");

        media.Hold();
        await page.LoadThreadAsync(original);
        await page.FocusPostAsync(target);
        await Program.WaitUntilAsync(() => media.Reads == 2);
        Task interrupted = page.MediaLoadTask;
        restore = page.CaptureGalleryOrigin();
        await page.LoadThreadAsync(other);
        await interrupted;
        restore();
        await Program.WaitUntilAsync(() => media.Reads == 3);
        Require(page.IsMediaLoading && page.CurrentThread?.Summary.Id == original, "Returning from another thread did not restart the cancelled origin scan.");
        media.Gate.SetResult();
        await page.MediaLoadTask;
        await WaitPreviewAsync(posts, target);

        media.Unavailable = true;
        await page.LoadThreadAsync(original);
        await page.FocusPostAsync(target);
        await page.MediaLoadTask;
        await Program.WaitUntilAsync(() => !string.IsNullOrEmpty(PreviewFor(target)?.PreviewStatus));
        ArchiveAttachmentView failed = PreviewFor(target)!;
        Require(failed.PreviewStatus!.Contains(Texts.T("archive.previewMediaReadFailed"), StringComparison.Ordinal),
            "An unreadable manifest was reported as an unsaved attachment: " + failed.PreviewStatus);
        Button retry = failed.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, Texts.T("action.retry")));
        Require(retry.IsEffectivelyVisible, "The failed preview did not offer Retry.");
        object? rows = posts.ItemsSource;
        media.Unavailable = false;
        retry.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        await Program.WaitUntilAsync(() => failed.Thumbnail.Source is Bitmap);
        Require(media.Reads == 5 && ReferenceEquals(rows, posts.ItemsSource), "Retry did not recover the preview in its existing post row.");

        await page.LoadThreadAsync(other);
        await page.MediaLoadTask;
        await Program.WaitUntilAsync(() => PreviewFor(other.Thread!)?.PreviewStatus == Texts.T("archive.notSaved"));
        Console.WriteLine("SMOKE reader previews keep a pending scan on return from Media. Gallery restoration reuses a complete scan or restarts a cancelled one. A transient read retries in place and an unsaved attachment is identified.");
    }

    // A preview whose size is known before its pixels (header read, size memory) is measured at its final height and does not move.
    private static async Task CheckReservedHeightAsync(Window window)
    {
        ArchiveAttachmentView view = new();
        window.Content = view;
        TaskCompletionSource<Bitmap?> pixels = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ArchiveAttachmentRow row = new(new CapturedAttachment(0, "local", OriginalFileName: "image.png")) { LoadThumbnail = _ => pixels.Task };
        row.KnownAspect = 1.8; // what the header read finds in ImageBytes (1800 x 1000)
        view.DataContext = row;
        await SmokeDispatcher.SettleAsync();
        double before = view.Bounds.Height;
        Require(before > 0 && view.Thumbnail.Source == null, "The reserved preview was not laid out before its pixels.");
        pixels.SetResult(NewBitmap());
        await Program.WaitUntilAsync(() => view.Thumbnail.Source is Bitmap);
        await SmokeDispatcher.SettleAsync();
        Require(Math.Abs(view.Bounds.Height - before) <= 0.5 && row.KnownAspect == 1.8,
            $"The preview's height changed when its pixels arrived although its size was known: {before:R} → {view.Bounds.Height:R}.");
        // A size learned while the row is shown (a late header read) reserves the frame at once, before any pixels.
        TaskCompletionSource<Bitmap?> later = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ArchiveAttachmentRow unknown = new(new CapturedAttachment(0, "local-2", OriginalFileName: "image.png")) { LoadThumbnail = _ => later.Task };
        view.DataContext = unknown;
        await SmokeDispatcher.SettleAsync();
        MediaFramePanel frame = view.GetVisualDescendants().OfType<MediaFramePanel>().Single();
        double wide = frame.Bounds.Height;
        unknown.KnownAspect = 0.5;
        await SmokeDispatcher.SettleAsync();
        Require(frame.Bounds.Height > wide + 10 && view.Thumbnail.Source == null, "A size learned while the row was shown did not reserve the frame.");
        later.SetResult(null);
        window.Content = null; await SmokeDispatcher.SettleAsync();
    }

    private static IEnumerable<ArchiveAttachmentRow> PageAttachments(ListBox posts) =>
        posts.ItemsSource!.Cast<ArchivePostRow>().SelectMany(post => post.Attachments);

    private static double PostHeight(ListBox posts, string id)
    {
        ArchivePostRow row = posts.ItemsSource!.Cast<ArchivePostRow>().Single(row => row.Id == id);
        posts.UpdateLayout();
        return (posts.ContainerFromItem(row) as Control)?.Bounds.Height
            ?? throw new InvalidOperationException("Target post has no realized container: " + id);
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
        CancellationToken oldCancellation = default;
        int oldLoads = 0, currentLoads = 0;
        view.DataContext = Row(token => { oldCancellation = token; oldLoads++; return old.Task; });
        await Program.WaitUntilAsync(() => oldLoads == 1);
        Bitmap? current = null;
        view.DataContext = Row(_ =>
        {
            currentLoads++;
            current = NewBitmap();
            return Task.FromResult<Bitmap?>(current);
        });
        Bitmap obsolete = NewBitmap();
        bool publishedObsolete = false;
        void OnPixelsChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
        {
            if (e.Property == Image.SourceProperty && ReferenceEquals(e.NewValue, obsolete)) publishedObsolete = true;
        }
        view.Thumbnail.PropertyChanged += OnPixelsChanged;
        try
        {
            await SmokeDispatcher.SettleAsync();
            Require(oldCancellation.IsCancellationRequested && oldLoads == 1 && currentLoads == 0
                && current == null && view.Thumbnail.Source == null,
                "Replacing a preview did not cancel the old request or started a replacement before its native owner returned.");
            // The cancelled decoder ignores its token. It must actually return before the
            // replacement may enter; waiting for replacement pixels first deadlocks this fixture.
            old.SetResult(obsolete);
            await Program.WaitUntilAsync(() => current != null && ReferenceEquals(view.Thumbnail.Source, current));
            await SmokeDispatcher.SettleAsync();
            Require(currentLoads == 1 && !publishedObsolete && ReferenceEquals(view.Thumbnail.Source, current),
                "An obsolete cancelled preview published pixels or the latest replacement did not run exactly once.");
        }
        finally
        {
            old.TrySetResult(obsolete);
            view.Thumbnail.PropertyChanged -= OnPixelsChanged;
        }
        TaskCompletionSource<Bitmap?> slow = new(TaskCreationOptions.RunContinuationsAsynchronously);
        view.LoadTimeout = TimeSpan.FromMilliseconds(100);
        view.DataContext = Row(_ => slow.Task);
        await Program.WaitUntilAsync(() => view.PreviewStatus == Texts.T("archive.previewLoadFailed"));
        slow.SetResult(NewBitmap()); await SmokeDispatcher.SettleAsync();
        Require(view.Thumbnail.Source == null && Math.Abs(view.Bounds.Height - height) <= 2,
            "Timeout/late completion changed the stable preview or displayed obsolete pixels.");
        view.DataContext = new ArchiveAttachmentRow(new CapturedAttachment(0, "video-poster", OriginalFileName: "saved-video.webm"))
        { LoadThumbnail = _ => Task.FromResult<Bitmap?>(NewBitmap()) };
        await Program.WaitUntilAsync(() => view.Thumbnail.Source is Bitmap);
        await SmokeDispatcher.SettleAsync();
        // MB1b: the mark is drawn by the shared badge view that lies on the shown picture; its place is the play badge of the plan.
        MeowChan.Desktop.Controls.MediaBadgeView badges = view.Badges;
        MeowChan.Desktop.Controls.PlacedBadge play = badges.Plan.Badges.Single(placed => placed.Badge.Id == MeowChan.Desktop.Controls.MediaBadgeId.PlayMark);
        Point origin = badges.TranslatePoint(default, view.Thumbnail)
            ?? throw new InvalidOperationException("Video badge view has no thumbnail position.");
        Point center = origin + play.Bounds.Center;
        // The mark scales with the shown picture (45 % of its shorter side, 20 to 44 dip, never past the picture).
        double expected = VideoPlayMark.Diameter(view.Thumbnail.Bounds.Size);
        Require(badges.IsEffectivelyVisible && !badges.IsHitTestVisible && expected > 0 && Math.Abs(play.Bounds.Width - expected) <= 1.5 && play.Bounds.Width <= view.Thumbnail.Bounds.Height + 1.5
            && Math.Abs(center.X - view.Thumbnail.Bounds.Width / 2) <= 2
            && Math.Abs(center.Y - view.Thumbnail.Bounds.Height / 2) <= 2,
            "Video preview play affordance is not centered and bounded over the poster.");
        Program.CaptureWindow(window, "smoke-video-poster-play-overlay.png");
        window.Content = null; await SmokeDispatcher.SettleAsync();
        Require(view.Thumbnail.Source == null, "Detached attachment retained pixels.");
    }
    // Read-only snapshots beside the existing PostTop measurements: no layout,
    // dispatcher yield, focus change or media completion may hide the regression.
    private static string DescribeRefreshAnchor(string stage, Window window, Control pane, ArchivePage page, ListBox posts, double targetTop)
    {
        try
        {
            object? State(string field) => typeof(ArchivePage).GetField(field,
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.GetValue(page);
            ScrollViewer? scroll = posts.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
            var realized = posts.GetVisualDescendants().OfType<ListBoxItem>().Select(item => new
            {
                id = (item.DataContext as ArchivePostRow)?.Id, top = item.TranslatePoint(default, posts)?.Y,
                height = item.Bounds.Height, bounds = item.Bounds.ToString(), item.IsEffectivelyVisible,
                previews = item.GetVisualDescendants().OfType<ArchiveAttachmentView>().Select(view => new
                {
                    bounds = view.Bounds.ToString(), top = view.TranslatePoint(default, posts)?.Y,
                    view.PreviewStatus, hasPixels = view.Thumbnail.Source is Bitmap,
                    thumbnailBounds = view.Thumbnail.Bounds.ToString(), view.IsEffectivelyVisible
                }).ToArray()
            }).ToArray();
            var anchor = realized.Where(item => item.id != null && item.top.HasValue
                && item.top.Value < posts.Bounds.Height && item.top.Value + item.height > 0)
                .OrderBy(item => item.top).FirstOrDefault();
            Control? focused = window.FocusManager?.GetFocusedElement() as Control;
            return JsonSerializer.Serialize(new
            {
                stage, targetId = "110125033", targetTop,
                thread = page.CurrentThread?.Summary.Id.ToString(), postCount = page.CurrentThread?.PostCount,
                selectedId = (posts.SelectedItem as ArchivePostRow)?.Id, posts.SelectedIndex,
                firstVisibleAnchor = anchor == null ? null : new { anchor.id, anchor.top, anchor.height },
                page.IsBusy, page.IsMediaLoading, mediaTask = page.MediaLoadTask.Status.ToString(),
                contentRequest = State("_contentRequest"), focusRequest = State("_readerFocusRequest"),
                interactionRevision = State("_readerInteractionRevision"), retainedPostId = State("_readerPostId"),
                ended = State("_ended"), available = State("_available"),
                largeReader = State("_largeReader"), selectedThread = State("_selectedThread")?.ToString(),
                host = new
                {
                    window = window.GetType().Name, window.Title, bounds = window.Bounds.ToString(),
                    clientSize = window.ClientSize.ToString(), window.RenderScaling,
                    paneIsWindowContent = ReferenceEquals(window.Content, pane),
                    postsBelongToWindow = ReferenceEquals(TopLevel.GetTopLevel(posts), window),
                    paneRoot = TopLevel.GetTopLevel(pane)?.GetType().Name,
                    pageRoot = TopLevel.GetTopLevel(page)?.GetType().Name,
                    paneBounds = pane.Bounds.ToString(), posts.IsEffectivelyVisible,
                    postsAttached = posts.IsAttachedToVisualTree(),
                    focus = focused == null ? null : new { type = focused.GetType().Name, focused.Name,
                        postId = (focused.DataContext as ArchivePostRow)?.Id }
                },
                listBounds = posts.Bounds.ToString(), listDesiredSize = posts.DesiredSize.ToString(),
                scroll = scroll == null ? null : new
                {
                    offset = scroll.Offset.ToString(), extent = scroll.Extent.ToString(),
                    viewport = scroll.Viewport.ToString(), bounds = scroll.Bounds.ToString()
                },
                panels = posts.GetVisualDescendants().OfType<VirtualizingStackPanel>()
                    .Select(panel => new { bounds = panel.Bounds.ToString(), desiredSize = panel.DesiredSize.ToString() }).ToArray(),
                realized
            });
        }
        catch (Exception exception)
        {
            // Diagnostics must not replace the unchanged anchor assertion.
            return stage + " diagnostic failed: " + exception.GetType().Name + ": " + exception.Message;
        }
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
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
