using System.Reflection;
using System.Runtime.CompilerServices;
using MeowChan.Desktop;
using MeowChan.Gallery;

namespace MeowChan.DesktopTests;

public sealed class GalleryImagePanAdmissionTests
{
    [Theory]
    [InlineData(SavedMediaKind.Image, true, true)]
    [InlineData(SavedMediaKind.Unknown, true, true)]
    [InlineData(SavedMediaKind.Video, true, false)] // Defensive even with stale image metadata.
    [InlineData(SavedMediaKind.Other, true, false)]
    [InlineData(null, true, false)]
    [InlineData(SavedMediaKind.Image, false, false)]
    [InlineData(SavedMediaKind.Unknown, false, false)]
    [InlineData(SavedMediaKind.Video, false, false)]
    [InlineData(SavedMediaKind.Other, false, false)]
    [InlineData(null, false, false)]
    public void OnlyDecodedImageContentMayCaptureForPanning(SavedMediaKind? kind, bool decoded, bool expected) =>
        Assert.Equal(expected, GalleryViewer.CanPanDecodedImage(kind,
            decoded ? new GalleryImageInfo(640, 480, "PNG", false) : null));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AnimatedAndStillGifMetadataKeepImagePanAdmission(bool stillFrame) =>
        Assert.True(GalleryViewer.CanPanDecodedImage(SavedMediaKind.Image, new(640, 480, "GIF", stillFrame)));

    [Theory]
    [InlineData("_viewerInteractionStopped")]
    [InlineData("_videoSuspended")]
    [InlineData("_mediaFailed")]
    [InlineData("_interactionAvailable")]
    public void UnavailableViewerRejectsPressBeforeFocusOrPointerAccess(string state)
    {
        var viewer = (GalleryViewer)RuntimeHelpers.GetUninitializedObject(typeof(GalleryViewer));
        typeof(GalleryViewer).GetField("_interactionAvailable", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(viewer, state != "_interactionAvailable");
        if (state != "_interactionAvailable")
            typeof(GalleryViewer).GetField(state, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(viewer, true);
        // No initialized visual tree or pointer: this must return at admission.
        typeof(GalleryViewer).GetMethod("OnViewportPointerPressed", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(viewer, new object?[] { null, null });
        Assert.False((bool)typeof(GalleryViewer).GetField("_dragging", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(viewer)!);
    }
}
