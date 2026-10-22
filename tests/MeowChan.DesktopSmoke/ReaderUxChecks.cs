using System.Security.Cryptography;
using System.ComponentModel;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Archive;
using MeowChan.Desktop;
using Texts = MeowChan.Desktop.Localization.Texts;
using MeowChan.Gallery;
using MeowChan.Interfaces;
using MeowChan.Persistence;
using MeowChan.Storage;
using SkiaSharp;

namespace MeowChan.DesktopSmoke;

internal static class ReaderUxChecks
{
    private static readonly ResourceId Hero = new("4chan", "g", "1000");
    internal static async Task SeedAsync(string profile, bool deletionFixture = false)
    {
        string media = Path.Combine(profile, "Reader media");
        Directory.CreateDirectory(media);
        using SKBitmap bitmap = new(1800, 1000);
        using (SKCanvas canvas = new(bitmap))
        {
            canvas.Clear(SKColors.LightSteelBlue);
            using SKPaint paint = new() { Color = SKColors.MidnightBlue, IsAntialias = true };
            using SKFont font = new(SKTypeface.Default, 64);
            canvas.DrawText("Readable saved media · 1800 × 1000", 70, 150, SKTextAlign.Left, font, paint);
            paint.StrokeWidth = 2;
            for (int x = 0; x < 1800; x += 20) canvas.DrawLine(x, 260, x, 900, paint);
        }
        using SKImage image = SKImage.FromBitmap(bitmap);
        using SKData data = image.Encode(SKEncodedImageFormat.Png, 100);
        byte[] bytes = data.ToArray();
        await File.WriteAllBytesAsync(Path.Combine(media, "reader.png"), bytes);
        await File.WriteAllTextAsync(Path.Combine(media, ".meowchan-manifest.json"), JsonSerializer.Serialize(new
        {
            Version = 1, Entries = new Dictionary<string, object> { ["reader.png"] = new {
                MediaIdentifier = "shared", SourceUrl = "https://offline.invalid/reader.png", FileName = "reader.png",
                Length = bytes.Length, Sha256 = Convert.ToHexString(SHA256.HashData(bytes)), Md5 = Convert.ToBase64String(MD5.HashData(bytes)) } }
        }));
        using ProfileLease lease = ProfileLease.Acquire(profile);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(profile, lease);
        for (int thread = 1000; thread < 1052; thread++)
        {
            ResourceId id = Hero with { Thread = thread.ToString() };
            CapturedPost[] posts = Enumerable.Range(thread, 55).Select((post, ordinal) => new CapturedPost(post.ToString(), ordinal,
                PostFieldPresence.All, Html: "<p>Reader UX post " + post + ". Saved text remains readable beside its media.</p>",
                Attachments: new[] { new CapturedAttachment(0, "shared", OriginalFileName: "reader.png") })).ToArray();
            string threadMedia = media;
            if (deletionFixture)
            {
                threadMedia = Path.Combine(profile, "Delete media " + thread);
                Directory.CreateDirectory(threadMedia);
                File.Copy(Path.Combine(media, "reader.png"), Path.Combine(threadMedia, "reader.png"));
                File.Copy(Path.Combine(media, ".meowchan-manifest.json"), Path.Combine(threadMedia, ".meowchan-manifest.json"));
                await File.WriteAllTextAsync(Path.Combine(threadMedia, "unrelated.txt"), "keep me");
            }
            await catalog.CaptureAsync(new CapturedThreadSnapshot(id, "https://boards.4chan.org/g/thread/" + thread,
                "Reader UX " + thread, ThreadSourceState.Archived, DateTimeOffset.UtcNow, threadMedia, posts));
        }
    }

    internal static async Task RunAsync(MainWindow window)
    {
        window.Width = 1440; window.Height = 920;
        // Exercise the hidden ArchivePage before it has ever been navigated to.
        await CheckDownloadOriginAsync(window);
        Click(window.FindControl<MenuItem>("ArchiveMenuItem")!);
        ArchivePage page = window.ArchivePage;
        await Program.WaitUntilAsync(() => !page.IsBusy && page.FindControl<ListBox>("ArchiveThreads")!.ItemCount > 0);
        page.FindControl<TextBox>("ArchiveSearchText")!.Text = "Reader UX";
        page.FindControl<ComboBox>("ArchiveSort")!.SelectedIndex = 2;
        await page.ApplySearchAsync();
        page.FindControl<ListBox>("ArchiveThreads")!.SelectedIndex = 0;
        Click(page.FindControl<Button>("ArchiveReadPosts")!);
        ContentControl reader = window.FindControl<ContentControl>("ReaderHost")!;
        await Program.WaitUntilAsync(() => reader.IsVisible && !page.IsBusy && SelectedPost(page) == "1000");
        await page.MediaLoadTask;
        await CheckDetachedReaderIconsAsync(window, page, reader);
        await ReaderInteractionChecks.RunAsync(window, page);
        await Program.WaitUntilAsync(() => reader.GetVisualDescendants().OfType<ArchiveAttachmentView>()
            .Any(view => view.Thumbnail.Source is Bitmap bitmap && bitmap.PixelSize.Width > 240));
        await CheckOverlayChromeAsync(window, reader);
        Require(page.FindControl<Grid>("ArchiveReaderPane")!.Parent == reader, "Reader pane has the wrong owner.");
        Program.CaptureWindow(window, "smoke-reader-initial.png");
        Click(page.FindControl<Button>("ExportArchiveButton")!);
        await Program.WaitUntilAsync(() => page.FindControl<Border>("ArchiveExportDialog")!.IsVisible);
        Require(page.FindControl<Border>("ArchiveExportDialog")!.GetVisualAncestors().Contains(reader), "Export dialog was hidden behind the reader.");
        Click(page.FindControl<Button>("ArchiveExportClose")!);
        await page.FocusPostAsync("1049");
        PressKey(page.FindControl<Button>("ReaderNextPost")!, Key.Down);
        await Program.WaitUntilAsync(() => SelectedPost(page) == "1050");
        Require(page.FindControl<ListBox>("ArchivePosts")!.ItemsSource!.Cast<ArchivePostRow>().First().Id == "1050",
            "Reader Down failed to cross the post-page boundary.");
        PressKey(page.FindControl<Button>("ReaderPreviousPost")!, Key.Up);
        await Program.WaitUntilAsync(() => SelectedPost(page) == "1049");
        await Program.WaitUntilAsync(() => page.FindControl<Button>("ReaderNextThread")!.IsEnabled);
        PressKey(page.FindControl<Button>("ReaderNextThread")!, Key.Right);
        await Program.WaitUntilAsync(() => !page.IsBusy && page.CurrentThread?.Summary.Id.Thread == "1001");
        PressKey(page.FindControl<Button>("ReaderPreviousThread")!, Key.Left);
        await Program.WaitUntilAsync(() => !page.IsBusy && page.CurrentThread?.Summary.Id == Hero);
        await page.LoadThreadAsync(Hero with { Thread = "1049" });
        PressKey(page.FindControl<Button>("ReaderNextThread")!, Key.Right);
        await Program.WaitUntilAsync(() => !page.IsBusy && page.CurrentThread?.Summary.Id.Thread == "1050");
        PressKey(page.FindControl<Button>("ReaderNextThread")!, Key.Right);
        await Program.WaitUntilAsync(() => !page.IsBusy && page.CurrentThread?.Summary.Id.Thread == "1051");
        PressKey(page.FindControl<Button>("ReaderPreviousThread")!, Key.Right);
        Require(page.CurrentThread?.Summary.Id.Thread == "1051" && !page.FindControl<Button>("ReaderNextThread")!.IsEnabled,
            "Reader wrapped beyond the frozen query's final thread.");
        await page.LoadThreadAsync(Hero);
        await page.FocusPostAsync("1000");
        await page.MediaLoadTask;
        ArchiveAttachmentView inline = reader.GetVisualDescendants().OfType<ArchiveAttachmentView>()
            .First(view => view.DataContext is ArchiveAttachmentRow);
        Click(inline.PreviewButton);
        ContentControl gallery = window.FindControl<ContentControl>("GalleryHost")!;
        await Program.WaitUntilAsync(() => gallery.IsVisible && gallery.GetVisualDescendants().OfType<Image>()
            .Any(image => image.Source is Bitmap));
        GalleryViewer viewer = (GalleryViewer)gallery.Content!;
        await CheckOverlayChromeAsync(window, gallery);
        await WindowChromeChecks.RunAsync(window);
        await CheckSmallOverlayChromeAsync(window, gallery);
        for (int step = 0; step < 3; step++) PressKey(GalleryButton(viewer, "GalleryNext"), Key.Right);
        await Program.WaitUntilAsync(() => viewer.CaptureState().PostId == "1003");
        await Program.WaitUntilAsync(() => GalleryButton(viewer, "GalleryActual").IsEnabled);
        await CheckZoomAsync(window, viewer);
        Click(GalleryButton(viewer, "GalleryPlus"));
        GalleryViewerState zoom = viewer.CaptureState();
        Click(GalleryButton(viewer, "GalleryToggleContext"));
        await Program.WaitUntilAsync(() => viewer.GetGalleryVisualDescendants().OfType<Button>().Any(button => button.Name == "GalleryReadPost"));
        ComboBox association = viewer.GetGalleryVisualDescendants().OfType<ComboBox>().Single();
        PressKey(association, Key.Right);
        Require(viewer.CaptureState().PostId == "1003", "Gallery shortcut captured the association selector's key.");
        Click(GalleryButton(viewer, "GalleryReadPost"));
        await Program.WaitUntilAsync(() => !gallery.IsVisible && SelectedPost(page) == "1003");
        Click(page.FindControl<Button>("ArchiveGalleryReturn")!);
        Require(ReferenceEquals(viewer, gallery.Content) && viewer.CaptureState().PostId == "1003", "Reader return lost the media session.");
        Require(Math.Abs(viewer.CaptureState().PhysicalZoom - zoom.PhysicalZoom) < 1e-8, "Reader return lost image zoom.");
        Click(GalleryButton(viewer, "GalleryClose"));
        await Program.WaitUntilAsync(() => !gallery.IsVisible && SelectedPost(page) == "1003");
        Require(reader.IsVisible, "Reader-origin gallery close left the large reader.");
        await Program.WaitUntilAsync(() => window.FocusManager!.GetFocusedElement() is Visual focused && focused.GetVisualAncestors()
            .Contains(page.FindControl<ListBox>("ArchivePosts")!));
        await Program.WaitUntilAsync(() => reader.GetVisualDescendants().OfType<ArchiveAttachmentView>().Any(view =>
            view.Thumbnail.Source is Bitmap loaded && loaded.PixelSize.Width > 240
            && view.GetVisualAncestors().OfType<ListBoxItem>().Any(item => item.DataContext is ArchivePostRow post && post.Id == "1003")));
        window.UpdateLayout();
        ListBox postList = page.FindControl<ListBox>("ArchivePosts")!;
        ListBoxItem targetPost = postList.GetVisualDescendants().OfType<ListBoxItem>().Single(item => item.DataContext is ArchivePostRow post && post.Id == "1003");
        double targetTop = targetPost.TranslatePoint(default, postList)!.Value.Y;
        Require(targetTop >= -40 && targetTop < postList.Bounds.Height / 2,
            "Loading large previews scrolled the returned post out of view.");
        Program.CaptureWindow(window, "smoke-reader-current-post.png");
        ArchiveAssetRow savedAsset = page.FindControl<DataGrid>("ArchiveMediaGrid")!.ItemsSource.Cast<ArchiveAssetRow>().First();
        GalleryEntry unassigned = new(new GalleryAssetKey(Hero, savedAsset.Manifest.Directory, savedAsset.Asset.AssetId),
            savedAsset.Manifest, savedAsset.Asset, null, null, false, true);
        string? retainedAnchor = TopPost(page);
        string? retainedSelection = SelectedPost(page);
        await window.OpenMediaGalleryAsync(new[] { unassigned }, unassigned.EntryKey);
        viewer = (GalleryViewer)gallery.Content!;
        Require(viewer.CaptureState().PostId == null, "Unassigned medium acquired an invented post association.");
        Click(GalleryButton(viewer, "GalleryClose"));
        await Program.WaitUntilAsync(() => reader.IsVisible && TopPost(page) == retainedAnchor && SelectedPost(page) == retainedSelection
            && window.FocusManager!.GetFocusedElement() is Visual anchorFocus
            && anchorFocus.GetVisualAncestors().Contains(page.FindControl<ListBox>("ArchivePosts")!));
        page.FindControl<Button>("ArchiveCloseReader")!.Focus();
        for (int i = 0; i < 24; i++)
        {
            window.FocusManager.TryMoveFocus(NavigationDirection.Next);
            Require(window.FocusManager.GetFocusedElement() is Visual focus && focus.GetVisualAncestors().Contains(reader),
                "Tab escaped the full-window reader.");
        }
        Click(page.FindControl<Button>("ArchiveCloseReader")!);
        Require(!reader.IsVisible && page.FindControl<Grid>("ArchiveReaderPane")!.Parent == page.FindControl<Grid>("ArchiveSplit"),
            "Closing reader did not return control ownership.");
        Click(page.FindControl<Button>("NextThreadsButton")!);
        await Program.WaitUntilAsync(() => !page.IsBusy && page.FindControl<ListBox>("ArchiveThreads")!.ItemCount == 2);
        string? listPage = page.FindControl<TextBlock>("ArchiveThreadPage")!.Text;
        page.FindControl<Button>("ArchiveResultsGallery")!.Focus();
        Click(page.FindControl<Button>("ArchiveResultsGallery")!);
        await Program.WaitUntilAsync(() => gallery.IsVisible && gallery.GetVisualDescendants().OfType<Image>().Any(image => image.Source is Bitmap));
        viewer = (GalleryViewer)gallery.Content!;
        Click(GalleryButton(viewer, "GalleryToggleContext"));
        await Program.WaitUntilAsync(() => viewer.GetGalleryVisualDescendants().OfType<Button>().Any(button => button.Name == "GalleryReadPost"));
        Click(GalleryButton(viewer, "GalleryReadPost"));
        await Program.WaitUntilAsync(() => reader.IsVisible && !gallery.IsVisible && !page.IsBusy);
        Click(page.FindControl<Button>("ArchiveGalleryReturn")!);
        Click(GalleryButton(viewer, "GalleryClose"));
        Require(!reader.IsVisible && page.FindControl<TextBlock>("ArchiveThreadPage")!.Text == listPage
            && page.FindControl<TextBox>("ArchiveSearchText")!.Text == "Reader UX", "List-origin gallery close lost search or page.");
        Require(ReferenceEquals(window.FocusManager!.GetFocusedElement(), page.FindControl<Button>("ArchiveResultsGallery")), "List-origin focus was not restored.");
        Console.WriteLine("SMOKE reader UX: full-window ownership, readable media, real arrow routing, post page boundary, frozen thread navigation, A→D return, zoom, Tab and list-origin restore passed.");
    }

    internal static async Task CheckOverlayChromeAsync(MainWindow window, ContentControl overlay)
    {
        await Program.WaitUntilAsync(() =>
        {
            window.UpdateLayout();
            Control header = window.FindControl<Grid>("WorkspaceTitleBar")!;
            Point? top = overlay.TranslatePoint(default, window);
            return overlay.IsEffectivelyVisible && top is Point origin
                && Math.Abs(origin.Y - header.Bounds.Height) <= 1
                && Math.Abs(overlay.Bounds.Width - window.ClientSize.Width) <= 1
                && Math.Abs(origin.Y + overlay.Bounds.Height - window.ClientSize.Height) <= 1;
        });
        await WindowChromeChecks.CheckReachableAsync(window);
    }

    private static async Task CheckDetachedReaderIconsAsync(MainWindow window, ArchivePage page, ContentControl reader)
    {
        string originalLanguage = Texts.Language;
        string[] names =
        {
            "ArchiveCloseReader", "ArchiveOpenGallery", "ArchiveReadPosts",
            "ArchiveReaderFullscreen", "ExportArchiveButton", "ArchiveHistoryBack",
            "ReaderPreviousThread", "ReaderPreviousPost", "ReaderNextPost", "ReaderNextThread"
        };
        string[] labeled = { "ArchiveCloseReader", "ArchiveOpenGallery", "ArchiveReaderFullscreen", "ExportArchiveButton" };
        try
        {
            foreach (string language in new[] { "de", "en" })
            {
                Texts.SetLanguage(language);
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
                window.UpdateLayout();
                Require(ReferenceEquals(page.FindControl<Grid>("ArchiveReaderPane")!.Parent, reader),
                    "Language change reattached the large reader.");
                foreach (string name in names)
                {
                    Button button = page.FindControl<Button>(name)!;
                    if (button.Content is not Control icon)
                        throw new InvalidOperationException(name + " lost its compact icon after language change.");
                    if (labeled.Contains(name))
                        Require(icon is StackPanel panel && panel.Children.OfType<TextBlock>().Any(text => !string.IsNullOrWhiteSpace(text.Text)),
                            name + " lost its visible action label.");
                    if (!button.IsEffectivelyVisible) continue;
                    Point? position = icon.TranslatePoint(default, button);
                    Require(button.Bounds.Width > 0 && (labeled.Contains(name) || button.Bounds.Width <= 40)
                        && icon.Bounds.Width > 0 && icon.Bounds.Width <= button.Bounds.Width
                        && position is Point p && p.X >= -1 && p.Y >= -1
                        && p.X + icon.Bounds.Width <= button.Bounds.Width + 1
                        && p.Y + icon.Bounds.Height <= button.Bounds.Height + 1,
                        name + " icon overflows its compact reader button.");
                }
            }
        }
        finally
        {
            Texts.SetLanguage(originalLanguage);
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            window.UpdateLayout();
        }
    }

    private static async Task CheckSmallOverlayChromeAsync(MainWindow window, ContentControl overlay)
    {
        double width = window.Width, height = window.Height;
        string mode = Config.LoadConfig().WorkspaceMode;
        try
        {
            await PresentationChecks.SwitchModeAsync(window, "Barebones");
            window.Width = 640; window.Height = 480;
            await Program.WaitUntilAsync(() => PresentationChecks.MatchesRequestedSize(window, 640, 480));
            await CheckOverlayChromeAsync(window, overlay);
            Program.CaptureWindow(window, "smoke-gallery-caption-small.png");
            await WindowChromeChecks.RunAsync(window);
        }
        finally
        {
            await PresentationChecks.SwitchModeAsync(window, mode);
            window.Width = width; window.Height = height;
            await Program.WaitUntilAsync(() => PresentationChecks.MatchesRequestedSize(window, width, height));
        }
        await CheckOverlayChromeAsync(window, overlay);
    }

    private static async Task CheckDownloadOriginAsync(MainWindow window)
    {
        Click(window.FindControl<Button>("DownloadsNavigationButton")!);
        DataGrid grid = window.FindControl<DataGrid>("ThreadsDataGrid")!;
        ThreadRow[] rows = Enumerable.Range(1000, 3).Select(id => new ThreadRow(
            SnapshotFixtures.Thread(990000 + id, id.ToString(), "Reader scope " + id))).ToArray();
        try
        {
            foreach (ThreadRow row in rows) window.UrlList.Add(row);
            grid.CollectionView.SortDescriptions.Clear();
            DataGridColumn order = grid.Columns.Single(column => column.SortMemberPath == nameof(ThreadRow.ThreadNumber));
            order.Sort(ListSortDirection.Descending);
            await Program.WaitUntilAsync(() => grid.CollectionView.Cast<ThreadRow>().First().ThreadId == "1002");
            grid.SelectedItem = rows[1];
            PressKey(grid, Key.Enter);
            ContentControl reader = window.FindControl<ContentControl>("ReaderHost")!;
            ArchivePage page = window.ArchivePage;
            await Program.WaitUntilAsync(() => reader.IsVisible && !page.IsBusy && page.CurrentThread?.Summary.Id.Thread == "1001");
            Require(page.FindControl<WrapPanel>("ReaderNavigation")!.IsVisible
                && page.FindControl<Button>("ReaderPreviousThread")!.IsEnabled
                && page.FindControl<Button>("ReaderNextThread")!.IsEnabled, "Downloads-first reader did not initialize its navigation.");
            await ExportReaderSizingChecks.RunAsync(window, page);
            PressKey(page.FindControl<Button>("ReaderNextThread")!, Key.Right);
            await Program.WaitUntilAsync(() => !page.IsBusy && page.CurrentThread?.Summary.Id.Thread == "1000");
            // The underlying list may later change order; the open reader retains its snapshot.
            order.Sort(ListSortDirection.Ascending);
            PressKey(page.FindControl<Button>("ReaderPreviousThread")!, Key.Right);
            Require(page.CurrentThread?.Summary.Id.Thread == "1000", "Download reader wrapped past its frozen end.");
            PressKey(page.FindControl<Button>("ReaderPreviousThread")!, Key.Left);
            await Program.WaitUntilAsync(() => !page.IsBusy && page.CurrentThread?.Summary.Id.Thread == "1001");
            PressKey(page.FindControl<Button>("ReaderPreviousThread")!, Key.Left);
            await Program.WaitUntilAsync(() => !page.IsBusy && page.CurrentThread?.Summary.Id.Thread == "1002");
            Require(!page.FindControl<Button>("ReaderPreviousThread")!.IsEnabled, "Download reader escaped its originating list scope.");
            Click(page.FindControl<Button>("ArchiveCloseReader")!);
            Require(!reader.IsVisible && window.SelectedPage == MainWindow.WorkspacePage.Downloads, "Download reader did not return to Downloads.");
            Console.WriteLine("SMOKE download reader: Enter freezes actual descending download order independently of archive query, retains scope after resort and does not wrap.");
        }
        finally
        {
            grid.CollectionView.SortDescriptions.Clear();
            foreach (ThreadRow row in rows) { window.UrlList.Remove(row); row.Dispose(); }
        }
    }

    private static async Task CheckZoomAsync(MainWindow window, GalleryViewer viewer)
    {
        Grid viewport = viewer.GetGalleryVisualDescendants().OfType<Grid>().Single(grid => grid.Name == "GalleryViewport");
        Size originalSize = window.ClientSize;
        PixelPoint originalPosition = window.Position;
        double ExpectedFit() => Math.Min(viewport.Bounds.Width * window.RenderScaling / 1800,
            viewport.Bounds.Height * window.RenderScaling / 1000);
        bool MatchesFit() => viewport.Bounds.Width > 0 && viewport.Bounds.Height > 0
            && Math.Abs(viewer.CaptureState().PhysicalZoom - ExpectedFit()) < 1e-8;
        void TraceZoom(string stage) => Console.WriteLine($"SMOKE gallery zoom {stage}: requested={window.Width}x{window.Height}; "
            + $"client={window.ClientSize}; frame={window.FrameSize}; viewport={viewport.Bounds.Size}; scale={window.RenderScaling}; "
            + $"expected-fit={ExpectedFit()}; actual={viewer.CaptureState().PhysicalZoom}; context={viewer.CaptureState().ContextOpen}.");
        async Task ResizeGalleryAsync(Size desired, string stage)
        {
            Avalonia.Platform.Screen screen = window.Screens.ScreenFromWindow(window)
                ?? throw new InvalidOperationException("Gallery zoom fixture has no native screen.");
            Size client = window.ClientSize;
            Size frame = window.FrameSize ?? client;
            WindowStartupFit target = WindowStartupGeometry.Fit(desired,
                new Size(desired.Width + Math.Max(0, frame.Width - client.Width),
                    desired.Height + Math.Max(0, frame.Height - client.Height)),
                new Size(window.MinWidth, window.MinHeight), screen.WorkingArea, window.RenderScaling,
                window.Position, WindowStartupGeometry.NormalFrameInsetPixels);
            window.Width = target.Client.Width; window.Height = target.Client.Height;
            window.Position = target.Position;
            try
            {
                // A clamped request can retain the current native size. Still
                // drain the pending context layout before checking its Fit.
                await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Background);
                await Program.WaitUntilAsync(() => PresentationChecks.MatchesRequestedSize(window,
                    target.Client.Width, target.Client.Height) && MatchesFit());
            }
            finally { TraceZoom(stage); }
        }
        if (viewer.CaptureState().ContextOpen) Click(GalleryButton(viewer, "GalleryToggleContext"));
        Click(GalleryButton(viewer, "GalleryFit"));
        await ResizeGalleryAsync(new Size(850, 530), "baseline");
        GalleryViewerState fit = viewer.CaptureState();
        for (int i = 0; i < 40; i++) Click(GalleryButton(viewer, "GalleryMinus"));
        Require(viewer.CaptureState().PhysicalZoom == fit.PhysicalZoom, "Minus shrank below Fit.");
        for (int i = 0; i < 40; i++) PressKey(GalleryButton(viewer, "GalleryPlus"), Key.OemMinus);
        Require(viewer.CaptureState().PhysicalZoom == fit.PhysicalZoom, "Keyboard minus shrank below Fit.");
        Wheel(window, viewport, 0.1);
        double fractional = viewer.CaptureState().PhysicalZoom / fit.PhysicalZoom;
        Require(fractional > 1 && fractional < 1.02, "Fractional wheel delta zoomed too strongly.");
        Click(GalleryButton(viewer, "GalleryFit"));
        Wheel(window, viewport, 1);
        Require(Math.Abs(viewer.CaptureState().PhysicalZoom / fit.PhysicalZoom - 1.1) < 1e-8,
            "Normal wheel tick did not use the expected fine increment.");
        Wheel(window, viewport, 100000);
        Require(viewer.CaptureState().PhysicalZoom < fit.PhysicalZoom * 2, "Huge wheel impulse was not bounded.");
        // An off-center zoom creates pan; returning to the floor must discard it.
        Click(GalleryButton(viewer, "GalleryActual"));
        Wheel(window, viewport, 2);
        for (int i = 0; i < 80; i++) Wheel(window, viewport, -1);
        GalleryViewerState floor = viewer.CaptureState();
        Require(!floor.ManualZoom && floor.PhysicalZoom == fit.PhysicalZoom && floor.PanX == 0 && floor.PanY == 0,
            "Wheel Fit floor did not center and reset pan.");
        Size baselineViewport = viewport.Bounds.Size;
        await ResizeGalleryAsync(new Size(1600, 1100), "bounded growth");
        GalleryViewerState resized = viewer.CaptureState();
        Size resizedViewport = viewport.Bounds.Size;
        Require(resizedViewport.Width > baselineViewport.Width || resizedViewport.Height > baselineViewport.Height,
            "The bounded gallery resize did not enlarge the actual viewport.");
        Require(resized.PhysicalZoom >= fit.PhysicalZoom
            && (fit.PhysicalZoom == 1 || resized.PhysicalZoom > fit.PhysicalZoom),
            "Fit did not increase when the actual gallery viewport grew.");
        Click(GalleryButton(viewer, "GalleryToggleContext"));
        try
        {
            await Program.WaitUntilAsync(() => viewer.CaptureState().ContextOpen
                && viewport.Bounds.Width < resizedViewport.Width - 1 && MatchesFit());
        }
        finally { TraceZoom("context narrowed viewport"); }
        GalleryViewerState contextFit = viewer.CaptureState();
        Require(contextFit.PhysicalZoom <= resized.PhysicalZoom,
            "Opening context increased Fit despite narrowing the viewport.");
        await Program.WaitUntilAsync(() => viewer.GetGalleryVisualDescendants().OfType<SelectableTextBlock>().Any());
        viewer.GetGalleryVisualDescendants().OfType<SelectableTextBlock>().First().Focus();
        Wheel(window, viewport, -100000);
        Require(viewer.CaptureState().PhysicalZoom == contextFit.PhysicalZoom, "Context resize lost the Fit minimum.");
        Require(ReferenceEquals(window.FocusManager!.GetFocusedElement(), viewer), "Image wheel did not return keyboard focus to the gallery.");
        viewer.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.OemPlus, KeyModifiers = KeyModifiers.Shift });
        Require(viewer.CaptureState().PhysicalZoom > contextFit.PhysicalZoom, "Shift plus failed to zoom from gallery focus.");
        GalleryViewerState savedContext = viewer.CaptureState();
        Click(GalleryButton(viewer, "GalleryToggleContext"));
        viewer.UpdateLayout();
        viewer.RestoreState(savedContext);
        viewer.UpdateLayout();
        Require(Math.Abs(viewer.CaptureState().PhysicalZoom - savedContext.PhysicalZoom) < 1e-8
            && viewer.CaptureState().ContextOpen, "Restoring context used the stale full-width Fit and lost manual zoom.");
        Click(GalleryButton(viewer, "GalleryToggleContext"));
        Click(GalleryButton(viewer, "GalleryFit"));
        await ResizeGalleryAsync(new Size(2200, 1500), "bounded maximum fit");
        Require(MatchesFit(), "Fit did not follow the enlarged viewport.");
        Click(GalleryButton(viewer, "GalleryActual"));
        await Program.WaitUntilAsync(() => viewer.CaptureState().PhysicalZoom == 1
            && viewer.GetGalleryVisualDescendants().OfType<Image>().Any(image => image.Name == "GalleryImage"
            && image.Source is Bitmap bitmap && bitmap.PixelSize.Width == 1800));
        Click(GalleryButton(viewer, "GalleryFit"));
        await ResizeGalleryAsync(originalSize, "original viewport restored");
        window.Position = originalPosition;
        Program.CaptureWindow(window, "smoke-gallery-fit-floor.png");
    }
    private static void Wheel(MainWindow window, Control viewport, double delta)
    {
        using Pointer pointer = new(Pointer.GetNextFreeId(), PointerType.Mouse, true);
        Point point = viewport.TranslatePoint(new Point(5, 5), window)!.Value;
        viewport.RaiseEvent(new PointerWheelEventArgs(viewport, pointer, window, point, 0,
            new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.Other), KeyModifiers.None, new Vector(0, delta)));
    }

    private static string? TopPost(ArchivePage page)
    {
        ListBox list = page.FindControl<ListBox>("ArchivePosts")!;
        return list.GetVisualDescendants().OfType<ListBoxItem>().Select(item =>
            (Item: item, Top: item.TranslatePoint(default, list)?.Y ?? double.NaN))
            .Where(value => value.Item.DataContext is ArchivePostRow && value.Top + value.Item.Bounds.Height > 0)
            .OrderBy(value => value.Top).Select(value => ((ArchivePostRow)value.Item.DataContext!).Id).FirstOrDefault();
    }
    private static string? SelectedPost(ArchivePage page) => (page.FindControl<ListBox>("ArchivePosts")!.SelectedItem as ArchivePostRow)?.Id;
    private static Button GalleryButton(GalleryViewer viewer, string name) => viewer.GetGalleryVisualDescendants().OfType<Button>().Single(button => button.Name == name);
    private static void PressKey(Control control, Key key) { control.Focus(); control.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key }); }
    private static void Click(Control control) => control.RaiseEvent(new RoutedEventArgs(control is MenuItem ? MenuItem.ClickEvent : Button.ClickEvent));
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
