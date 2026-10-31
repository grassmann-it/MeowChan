using Avalonia.Controls;
using MeowChan.Desktop;

namespace MeowChan.DesktopTests;

public sealed class NativeVideoOverlayVisibilityTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OwnedOverlaySurfaceTracksThePlayersExplicitVisibility(bool visible)
    {
        Border surface = new() { IsVisible = !visible };
        GalleryVideoPlayer.SynchronizeNativeOverlaySurfaceVisibility(surface, false, false, true, visible);
        Assert.Equal(visible, surface.IsVisible);
    }

    [Theory]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(false, false, false)]
    public void SoftwareRetirementAndDockedFallbackDoNotChangeNativeSurface(bool software, bool disposing, bool hasOverlay)
    {
        Border surface = new();
        GalleryVideoPlayer.SynchronizeNativeOverlaySurfaceVisibility(surface, software, disposing, hasOverlay, false);
        Assert.True(surface.IsVisible);
    }
}
