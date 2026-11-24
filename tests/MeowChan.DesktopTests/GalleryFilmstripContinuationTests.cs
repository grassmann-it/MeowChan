using System.Security.Cryptography;
using System.Text.Json;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Motion;
using MeowChan.Desktop.Updates;
using MeowChan.Interfaces;
using MeowChan.Persistence;
using MeowChan.Storage;
using SkiaSharp;
using Texts = MeowChan.Desktop.Localization.Texts;

namespace MeowChan.DesktopTests;

[Collection("Avalonia global state")]
public sealed class GalleryFilmstripContinuationTests
{
    [Fact]
    public void EndButtonKeepsMediaPositionsAndRetriesTheSameThreadAfterFailure() => BooruComponentDispatcher.Run(() =>
    {
        using IDisposable platform = BooruReviewTestPlatform.Enter();
        DirectoryInfo profile = Directory.CreateTempSubdirectory("meowchan-filmstrip-");
        try { BooruComponentDispatcher.Wait(ExerciseAsync(profile.FullName)); }
        finally { profile.Delete(recursive: true); }
    });

    private static async Task ExerciseAsync(string profile)
    {
        Texts.SetLanguage("en");
        string downloads = Path.Combine(profile, "downloads");
        new Config
        {
            DownloadPath = downloads,
            FirstRunSetupVersion = 1,
            Language = "en",
            WorkspaceMode = "Advanced",
            DownloadsPaused = true,
            SaveUrlsOnExit = false,
            GalleryOverlayControls = false,
            GalleryContextOpen = true,
            GalleryFilmstripHeight = 96,
            GalleryFilmstripPreload = Config.GalleryFilmstripPreloadMinimum,
            GalleryWheelMode = Config.GalleryWheelModeBrowse
        }.SaveConfig(profile);
        ResourceId first = new("filmstrip", "test", "1");
        ResourceId second = new("filmstrip", "test", "2");
        ResourceId thirdThread = new("filmstrip", "test", "3");
        string secondMedia = Path.Combine(downloads, "B", "media");
        using (ProfileLease lease = ProfileLease.Acquire(profile))
        {
            await using ArchiveCatalog archive = await ArchiveCatalog.OpenAsync(profile, lease);
            await SeedAsync(archive, first, Path.Combine(downloads, "A"), "A", 2, postMedia: [0, 1, 0]);
            await SeedAsync(archive, second, secondMedia, "B", 1);
            await SeedAsync(archive, thirdThread, Path.Combine(downloads, "C"), "C", 1);
        }

        await using DesktopSessionHost session = new(profile, [],
            updateSessionFactory: (path, config) => new UpdateSession(path, config, disableEnvironment: () => "1"));
        await using MainWindow window = new(session) { Width = 1200, Height = 800, Classes = { MotionPolicy.OffClass } };
        window.SetRenderScaling(1.5);
        window.Show();
        await window.StartupTimingRestoreTask;
        window.PlatformImpl!.Activated?.Invoke();
        Assert.True(window.IsActive);
        await window.OpenSearchGalleryAsync(new ArchiveThreadQuery(Source: "filmstrip", Sort: ArchiveThreadSort.Title));
        GalleryViewer viewer = Assert.IsType<GalleryViewer>(window.FindControl<ContentControl>("GalleryHost")!.Content);
        GalleryFilmstrip strip = viewer.Filmstrip;
        TextBlock position = viewer.GetVisualDescendants().OfType<TextBlock>().Single(block => block.Name == "GalleryCompactPosition");
        TextBlock status = viewer.GetVisualDescendants().OfType<TextBlock>().Single(block => block.Name == "GalleryStatus");
        Image image = viewer.GetVisualDescendants().OfType<Image>().Single(control => control.Name == "GalleryImage");
        Button next = viewer.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "GalleryNext");
        next.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await BooruTestSupport.WaitAsync(() => strip.CurrentIndex == 1, "the second media position");
        next.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await BooruTestSupport.WaitAsync(() => strip.CurrentIndex == 2 && image.Source != null && status.Text == string.Empty,
            "the third picture to finish loading");
        window.UpdateLayout();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
        object picture = image.Source!;

        Assert.True(viewer.IsFilmstripVisible);
        Assert.True(strip.HasMore);
        Button load = Assert.IsType<Button>(strip.TileControl(3));
        Control third = Assert.IsAssignableFrom<Control>(strip.TileControl(2));
        Assert.Equal("3 / 3", position.Text);
        Assert.Equal(3, AutomationProperties.GetPositionInSet(third));
        Assert.Equal(3, AutomationProperties.GetSizeOfSet(third));
        Assert.StartsWith("3 of 3", AutomationProperties.GetName(third));
        Assert.Equal("Load more media", AutomationProperties.GetName(load));
        Assert.Equal("Load more media", ToolTip.GetTip(load));
        Assert.Equal(WorkspaceIcons.ArrowDown, WorkspaceIcons.KeyOf(load.Content));
        Assert.Equal(1.5, window.RenderScaling);
        Rect tileBounds = BoundsIn(third, strip);
        Rect loadBounds = BoundsIn(load, strip);
        Assert.True(loadBounds.Width > 0 && loadBounds.Height > 0);
        Assert.InRange(loadBounds.Left - tileBounds.Right - Config.GalleryFilmstripTileGapDefault, -1, 1);
        Assert.InRange(loadBounds.Top - tileBounds.Top, -1, 1);
        Assert.InRange(loadBounds.Width - tileBounds.Width, -1, 1);
        Assert.InRange(loadBounds.Height - tileBounds.Height, -1, 1);

        Control viewport = viewer.GetVisualDescendants().OfType<Control>().Single(control => control.Name == "GalleryViewport");
        bool wheelHandled = false;
        viewport.AddHandler(InputElement.PointerWheelChangedEvent, (_, e) => wheelHandled = e.Handled, handledEventsToo: true);
        window.MouseWheel(BoundsIn(viewport, window).Center, new Vector(0, -1), RawInputModifiers.None);
        Assert.True(wheelHandled);
        Assert.Equal(2, strip.CurrentIndex);
        Assert.Equal(string.Empty, status.Text);
        Capture(window, "ready");

        int failures = 0;
        int attempts = 0;
        TaskCompletionSource<string> started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource failRead = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource reported = new(TaskCreationOptions.RunContinuationsAsynchronously);
        viewer.DiagnosticNotice += notice =>
        {
            if (notice.Operation != "load-more") return;
            failures++;
            reported.TrySetResult();
        };
        MediaAssetSnapshot secondSnapshot = await MediaAssetReader.OpenAsync(secondMedia);
        // The manifest provider holds the first read until both input attempts have been delivered.
        using IDisposable manifest = VirtualMediaManifests.Register(Path.GetDirectoryName(secondMedia)!, async (directory, preview, token) =>
        {
            if (preview || Interlocked.Increment(ref attempts) != 1) return secondSnapshot;
            started.SetResult(directory);
            await failRead.Task.WaitAsync(token);
            throw new IOException("The saved media manifest is temporarily unavailable.");
        });
        try
        {
            Point loadCenter = BoundsIn(load, window).Center;
            Click(window, loadCenter);
            Assert.Equal(secondMedia, await started.Task.WaitAsync(BooruComponentDispatcher.Cancellation));
            Assert.Equal("Loading…", AutomationProperties.GetName(load));
            Assert.Equal("Loading…", ToolTip.GetTip(load));
            Assert.Equal(WorkspaceIcons.Refresh, WorkspaceIcons.KeyOf(load.Content));
            Assert.Equal("Loading…", status.Text);
            Assert.Equal("3 / 3", position.Text);
            Click(window, loadCenter);
            Assert.Equal(1, Volatile.Read(ref attempts));
            Capture(window, "loading");
        }
        finally { failRead.SetResult(); }
        await reported.Task.WaitAsync(BooruComponentDispatcher.Cancellation);

        Assert.Equal(1, failures);
        Assert.Equal("Load more media", AutomationProperties.GetName(load));
        Assert.Equal("3 / 3", position.Text);
        Assert.Equal(2, strip.CurrentIndex);
        Assert.Same(picture, image.Source);
        Assert.Equal("Could not load more media. Try again.", status.Text);
        Capture(window, "retry");

        ComboBox association = viewer.GetVisualDescendants().OfType<ComboBox>().Single(control => control.Name == "GalleryPostAssociation");
        Assert.Equal(1, association.SelectedIndex);
        association.SelectedIndex = 0;
        Assert.Equal("1 / 3", position.Text);
        Assert.Equal(2, strip.CurrentIndex);
        Assert.Same(picture, image.Source);
        Assert.True(load.Focus(NavigationMethod.Tab));
        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        window.KeyReleaseQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        await BooruTestSupport.WaitAsync(() => strip.CurrentIndex == 3 && next.IsEnabled && status.Text == string.Empty,
            "thread B to finish loading after retry");
        Assert.Equal("4 / 4", position.Text);
        Assert.Equal(4, strip.Count);
        Assert.Equal(4, AutomationProperties.GetSizeOfSet(third));
        Assert.Equal(second, strip.EntryAt(3)!.Key.Thread);
        Assert.Same(strip.TileControl(3), window.FocusManager!.GetFocusedElement());
        Assert.Equal(1, failures);
        Assert.Equal(2, Volatile.Read(ref attempts));
        Capture(window, "loaded");

        Assert.True(strip.HasMore);
        Button loadThird = Assert.IsType<Button>(strip.TileControl(4));
        window.UpdateLayout();
        Click(window, BoundsIn(loadThird, window).Center);
        await BooruTestSupport.WaitAsync(() => strip.CurrentIndex == 4 && !strip.HasMore && status.Text == string.Empty,
            "thread C to load after thread B");
        Assert.Equal("5 / 5", position.Text);
        Assert.Equal(thirdThread, strip.EntryAt(4)!.Key.Thread);
        Assert.Equal(1, failures);
        Assert.Equal(2, Volatile.Read(ref attempts));
    }

    private static void Click(Window window, Point point)
    {
        window.MouseMove(point);
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
    }

    private static Rect BoundsIn(Control control, Visual relativeTo) =>
        new(control.TranslatePoint(default, relativeTo)!.Value, control.Bounds.Size);

    private static void Capture(Window window, string state)
    {
        string? directory = Environment.GetEnvironmentVariable("MEOWCHAN_CAPTURE_FILMSTRIP");
        if (string.IsNullOrEmpty(directory)) return;
        window.UpdateLayout();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        using WriteableBitmap frame = window.CaptureRenderedFrame()
            ?? throw new InvalidOperationException("The headless gallery did not render.");
        frame.Save(Path.Combine(directory, state + ".png"), PngBitmapEncoderOptions.Default);
    }

    private static async Task SeedAsync(ArchiveCatalog archive, ResourceId id, string directory, string title, int count,
        int[]? postMedia = null)
    {
        Directory.CreateDirectory(directory);
        string url = "https://filmstrip.invalid/" + id.Thread;
        Dictionary<string, object> entries = new();
        using SKBitmap picture = new(320, 180);
        picture.Erase(SKColors.SteelBlue);
        using SKData encoded = picture.Encode(SKEncodedImageFormat.Png, 100);
        byte[] png = encoded.ToArray();
        for (int index = 0; index < count; index++)
        {
            string file = $"image-{index}.png";
            await File.WriteAllBytesAsync(Path.Combine(directory, file), png);
            entries[file] = new { MediaIdentifier = file, Variant = "original", SourceUrl = url + "/" + file,
                FileName = file, Length = png.Length, Md5 = Convert.ToBase64String(MD5.HashData(png)), Sha256 = Convert.ToHexString(SHA256.HashData(png)) };
        }
        await File.WriteAllTextAsync(Path.Combine(directory, ".meowchan-manifest.json"), JsonSerializer.Serialize(new { Version = 1, Entries = entries }));
        if (postMedia == null)
        {
            await archive.RegisterThreadAsync(new(id, url, title, directory, true));
            return;
        }
        CapturedPost[] posts = postMedia.Select((media, index) =>
        {
            string name = $"image-{media}.png";
            return new CapturedPost((index + 1).ToString(), index, PostFieldPresence.All,
                Attachments: [new CapturedAttachment(0, name, Url: url + "/" + name)]);
        }).ToArray();
        await archive.CapturePreservingRevisionsAsync(new(id, url, title, ThreadSourceState.Archived, DateTimeOffset.UnixEpoch, directory, posts));
    }
}
