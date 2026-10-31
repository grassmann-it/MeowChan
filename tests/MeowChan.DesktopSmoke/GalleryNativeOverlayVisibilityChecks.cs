using Avalonia.Controls;
using MeowChan.Desktop;

namespace MeowChan.DesktopSmoke;

internal static partial class GalleryPlaybackFeatureChecks
{
    private static async Task CheckNativeOverlayVisibilityAsync(MainWindow owner)
    {
        // Actual pinned VideoView/owned-window behavior, no MediaPlayer or codec.
        // Normal acceptance uses this on Windows. The explicit private-X11 probe
        // can also reproduce the same upstream visibility contract on Linux.
        LibVLCSharp.Avalonia.VideoView surface = new() { Content = new NativeVideoOverlayRoot() };
        Border player = new() { Child = surface };
        Window window = new() { Width = 700, Height = 500, Content = player,
            Title = "Private native overlay visibility regression" };
        try
        {
            window.Show(owner);
            await LayoutAsync(window);
            await UntilAsync(() => window.OwnedWindows.Count == 1 && window.OwnedWindows.Single().IsVisible,
                "Actual VideoView did not create its owned overlay without a decoder.");
            Window overlay = window.OwnedWindows.Single();
            player.IsVisible = false;
            await LayoutAsync(window);
            Require(surface.IsVisible && !surface.IsEffectivelyVisible && overlay.IsVisible,
                "Pinned upstream ancestor-hide mismatch was not reproduced.");
            Console.WriteLine("SMOKE native overlay visibility baseline: hidden container/effective surface, separate owned window still visible; no decoder/audio");
            player.PropertyChanged += (_, change) =>
            {
                if (change.Property == Avalonia.Visual.IsVisibleProperty)
                    GalleryVideoPlayer.SynchronizeNativeOverlaySurfaceVisibility(surface, false, false,
                        surface.Content != null, player.IsVisible);
            };
            GalleryVideoPlayer.SynchronizeNativeOverlaySurfaceVisibility(surface, false, false, true, player.IsVisible);
            Require(!surface.IsVisible && !overlay.IsVisible, "Direct surface visibility did not hide the old native overlay.");
            player.IsVisible = true;
            await LayoutAsync(window);
            Require(surface.IsVisible && surface.IsEffectivelyVisible && overlay.IsVisible,
                "Native overlay did not return with its restored player.");
            player.IsVisible = false;
            Require(!overlay.IsVisible, "Repeated player hiding leaked the overlay.");
            player.IsVisible = true;
            surface.Content = null;
            overlay.Hide(); // Same explicit unsupported-transparency fallback as the viewer.
            player.IsVisible = false;
            player.IsVisible = true;
            await LayoutAsync(window);
            Require(surface.IsVisible && surface.IsEffectivelyVisible && !overlay.IsVisible,
                "Fallback restoration hid the native picture or reopened an empty overlay.");
            Console.WriteLine("SMOKE native overlay visibility PASS: hide/show synchronized, fallback drawable retained without empty window, actual VideoView/owned window; no decoder/audio");
        }
        finally { window.Close(); }
    }
}
