using System.Reflection;
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
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.Updates;
using MeowChan.Persistence;

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
        new Config { DownloadPath = downloads, FirstRunSetupVersion = 1, GalleryOverlayControls = false }.SaveConfig(profile);
        ResourceId first = new("filmstrip", "test", "1"), second = new("filmstrip", "test", "2");
        using (ProfileLease lease = ProfileLease.Acquire(profile))
        {
            await using ArchiveCatalog archive = await ArchiveCatalog.OpenAsync(profile, lease);
            await SeedAsync(archive, first, downloads, "A", 3);
            await SeedAsync(archive, second, downloads, "B", 1);
        }

        await using DesktopSessionHost session = new(profile, [],
            updateSessionFactory: (path, config) => new UpdateSession(path, config, disableEnvironment: () => "1"));
        await using MainWindow window = new(session) { Width = 1200, Height = 800 };
        window.SetRenderScaling(1.5);
        window.Show();
        window.PlatformImpl!.Activated?.Invoke();
        await window.StartupTimingRestoreTask;
        await window.OpenSearchGalleryAsync(new ArchiveThreadQuery(Source: "filmstrip", Sort: ArchiveThreadSort.Title));
        GalleryViewer viewer = Assert.IsType<GalleryViewer>(window.FindControl<ContentControl>("GalleryHost")!.Content);
        viewer.ConfigureFilmstrip(true, 96, 0);
        GalleryFilmstrip strip = viewer.Filmstrip;
        TextBlock position = viewer.GetVisualDescendants().OfType<TextBlock>().Single(block => block.Name == "GalleryCompactPosition");
        TextBlock status = viewer.GetVisualDescendants().OfType<TextBlock>().Single(block => block.Name == "GalleryStatus");
        Image image = viewer.GetVisualDescendants().OfType<Image>().Single(control => control.Name == "GalleryImage");
        Button next = viewer.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "GalleryNext");
        next.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await UntilAsync(() => strip.CurrentIndex == 1);
        next.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await UntilAsync(() => strip.CurrentIndex == 2 && image.Source != null && status.Text == string.Empty);
        window.UpdateLayout();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
        object picture = image.Source!;

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
        Capture(window, "ready");

        // An invalid identity exercises a failure from the real ArchiveReader. Put the original
        // identity back at the same slot to verify that the failed attempt did not consume it.
        FieldInfo threads = typeof(MainWindow).GetField("_galleryThreads", BindingFlags.Instance | BindingFlags.NonPublic)!;
        ResourceId[] retryable = [first, new("filmstrip", "test", "")];
        threads.SetValue(window, retryable);
        int failures = 0;
        viewer.DiagnosticNotice += notice => { if (notice.Operation == "load-more") failures++; };
        Point center = load.TranslatePoint(new Point(load.Bounds.Width / 2, load.Bounds.Height / 2), window)!.Value;
        window.MouseDown(center, MouseButton.Left);
        window.MouseUp(center, MouseButton.Left);

        Assert.Equal("Loading…", AutomationProperties.GetName(load));
        Assert.Equal("Loading…", ToolTip.GetTip(load));
        Assert.Equal(WorkspaceIcons.Refresh, WorkspaceIcons.KeyOf(load.Content));
        Assert.Equal("Loading…", status.Text);
        Assert.Equal("3 / 3", position.Text);
        window.MouseDown(center, MouseButton.Left);
        window.MouseUp(center, MouseButton.Left);
        Capture(window, "loading");
        await UntilAsync(() => failures > 0 && AutomationProperties.GetName(load) == "Load more media");
        Assert.Equal(1, failures);
        Assert.Equal("3 / 3", position.Text);
        Assert.Equal(2, strip.CurrentIndex);
        Assert.Same(picture, image.Source);
        Assert.Equal("Could not load more media. Try again.", status.Text);
        Capture(window, "retry");

        retryable[1] = second;
        Assert.True(load.Focus(NavigationMethod.Tab));
        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        window.KeyReleaseQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        Assert.Equal("Loading…", AutomationProperties.GetName(load));
        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        window.KeyReleaseQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        await UntilAsync(() => strip.CurrentIndex == 3 && !strip.HasMore);
        Assert.Equal("4 / 4", position.Text);
        Assert.Equal(4, strip.Count);
        Assert.Equal(4, AutomationProperties.GetSizeOfSet(third));
        Assert.Equal(second, strip.EntryAt(3)!.Key.Thread);
        Assert.Same(strip.TileControl(3), window.FocusManager!.GetFocusedElement());
        Assert.Equal(1, failures);
        Capture(window, "loaded");
        window.Close();
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

    private static async Task SeedAsync(ArchiveCatalog archive, ResourceId id, string root, string title, int count)
    {
        string directory = Path.Combine(root, title);
        Directory.CreateDirectory(directory);
        string url = "https://filmstrip.invalid/" + id.Thread;
        Dictionary<string, object> entries = new();
        byte[] png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aN1cAAAAASUVORK5CYII=");
        for (int index = 0; index < count; index++)
        {
            string file = $"image-{index}.png";
            await File.WriteAllBytesAsync(Path.Combine(directory, file), png);
            entries[file] = new { MediaIdentifier = file, Variant = "original", SourceUrl = url + "/" + file,
                FileName = file, Length = png.Length, Md5 = Convert.ToBase64String(MD5.HashData(png)), Sha256 = Convert.ToHexString(SHA256.HashData(png)) };
        }
        await File.WriteAllTextAsync(Path.Combine(directory, ".meowchan-manifest.json"), JsonSerializer.Serialize(new { Version = 1, Entries = entries }));
        await archive.RegisterThreadAsync(new(id, url, title, directory, true));
    }

    private static async Task UntilAsync(Func<bool> condition)
    {
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(10, deadline.Token);
    }
}
