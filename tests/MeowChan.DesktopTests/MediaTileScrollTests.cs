using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using MeowChan.Desktop.Commands;
using MeowChan.Tests.Shared;

namespace MeowChan.DesktopTests;

[Collection("Avalonia global state")]
public sealed class MediaTileScrollTests
{
    [Fact]
    public void ScrolledMediaTilesSendEachGestureToTheBoundAssetExactlyOnce() => BooruComponentDispatcher.Run(() =>
    {
        using TempDirectory profile = new();
        using IDisposable platform = BooruReviewTestPlatform.Enter();
        ShortcutService.Publish(new ShortcutBindingsSection());
        BooruComponentDispatcher.Wait(MediaTileScrollScenario.RunAsync(profile.Path, BooruComponentDispatcher.Cancellation, capture: Capture));
    });

    private static void Capture(Window window)
    {
        if (Environment.GetEnvironmentVariable("MEOWCHAN_CAPTURE_SCROLLED_MEDIA") is not { Length: > 0 } path) return;
        using WriteableBitmap frame = window.CaptureRenderedFrame()
            ?? throw new InvalidOperationException("The scrolled media grid did not render.");
        frame.Save(path, PngBitmapEncoderOptions.Default);
    }
}
