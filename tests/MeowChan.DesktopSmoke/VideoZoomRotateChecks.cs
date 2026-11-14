using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using MeowChan.Desktop;

namespace MeowChan.DesktopSmoke;

/// <summary>
/// Plan V4 on a real decoded video (runs inside the video-contrast mode, Light theme): zoom, 1:1, Fit and rotate act on the
/// surface as a transform only, playback keeps decoding frames, Fit leaves the rotation alone, the info line and the live
/// region say where the picture is, and four turns bring it upright again. Linux and macOS draw the video into an Avalonia
/// image (matrix checks); the native Windows output is judged by its capture.
/// </summary>
internal static class VideoZoomRotateChecks
{
    internal static async Task RunAsync(MainWindow window, GalleryViewer viewer, GalleryVideoPlayer player, ThemeVariant theme)
    {
        await Program.WaitUntilAsync(() => player.SourceSize != null && player.SurfaceSize.Width > 0);
        Image? surface = player.GetVisualDescendants().OfType<Image>().SingleOrDefault(image => image.Name == "GallerySoftwareVideo");
        Button Tool(string name) => viewer.GetGalleryVisualDescendants().OfType<Button>().Single(button => button.Name == name);
        void Click(string name) => Tool(name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Matrix Surface() => surface?.RenderTransform is MatrixTransform transform ? transform.Matrix : Matrix.Identity;
        string Info() => viewer.GetGalleryVisualDescendants().OfType<TextBlock>().First(block => block.Name != "GalleryVideoStatus"
            && block.Text?.Contains(" × ") == true).Text!;
        async Task PlayingAsync(string stage)
        {
            GalleryVideoPlayer.PlaybackEvidence before = await player.InspectPlaybackAsync();
            if (before.State != "Playing") return; // A short clip may have ended on a loaded machine; the transform alone is checked then.
            await Task.Delay(400);
            GalleryVideoPlayer.PlaybackEvidence after = await player.InspectPlaybackAsync();
            if (after.State == "Playing" && after.SurfaceFrames <= before.SurfaceFrames)
                throw new InvalidOperationException("Video stopped presenting frames after " + stage + ".");
        }

        if (!Tool("GalleryFit").IsEnabled || !Tool("GalleryPlus").IsEnabled || !Tool("GalleryRotate").IsEnabled)
            throw new InvalidOperationException("A shown video did not enable Fit, zoom and rotate.");
        if (viewer.CaptureState().ManualZoom || viewer.Rotation != 0)
            throw new InvalidOperationException("A new video did not start fitted and upright.");

        Click("GalleryPlus"); Click("GalleryPlus"); Click("GalleryPlus");
        await Program.WaitUntilAsync(() => viewer.CaptureState().ManualZoom);
        if (surface != null && Surface().M11 <= 1.2) throw new InvalidOperationException("Zooming a video did not scale its surface: " + Surface());
        if (!Info().Contains('%')) throw new InvalidOperationException("The info line shows no zoom percentage for a zoomed video: " + Info());
        await PlayingAsync("zooming");

        Click("GalleryRotate");
        await Program.WaitUntilAsync(() => viewer.Rotation == 90);
        if (surface != null && Math.Abs(Surface().M12) < 0.5) throw new InvalidOperationException("Rotating a video did not turn its surface: " + Surface());
        if (!Info().Contains("90")) throw new InvalidOperationException("The info line does not name the rotation: " + Info());
        await Program.WaitUntilAsync(() => viewer.ViewAnnouncementText.Contains("90"));
        await PlayingAsync("rotating");
        Program.CaptureWindow(window, $"smoke-video-zoom-rotate-{window.RenderScaling:0.0}-{theme}.png");

        Click("GalleryActual");
        await Program.WaitUntilAsync(() => viewer.CaptureState() is { ManualZoom: true, ActualZoom: true });
        Click("GalleryFit");
        await Program.WaitUntilAsync(() => !viewer.CaptureState().ManualZoom);
        if (viewer.Rotation != 90) throw new InvalidOperationException("Fit reset the rotation; only a new medium may.");
        for (int turn = 0; turn < 3; turn++) Click("GalleryRotate");
        await Program.WaitUntilAsync(() => viewer.Rotation == 0);
        if (surface != null && (Math.Abs(Surface().M12) > 1e-6 || Math.Abs(Surface().M11 - 1) > 1e-6))
            throw new InvalidOperationException("Four turns and Fit did not bring the video surface back: " + Surface());
        await PlayingAsync("returning to Fit");
        Console.WriteLine("SMOKE video-zoom-rotate: " + JsonSerializer.Serialize(new { theme = theme.ToString(), size = player.SourceSize, surface = player.SurfaceSize.ToString(), native = surface == null }));
    }
}
