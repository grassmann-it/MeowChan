using System.Reflection;
using System.Runtime.CompilerServices;
using MeowChan.Desktop;

namespace MeowChan.DesktopTests;

public sealed class GalleryTerminalRetirementTests
{
    [Theory]
    [InlineData("ApplyOverlayPlacement")]
    [InlineData("LayoutContextPlacement")]
    [InlineData("LayoutContext")]
    [InlineData("CheckNativeOverlayTransparency")]
    public void TerminalViewerRejectsLatePlacementWithoutTouchingItsRetiredVisualTree(string operation)
    {
        // Deliberately no native window or initialized controls: a late callback
        // must return at the terminal admission guard, not mutate/reparent UI.
        var viewer = (GalleryViewer)RuntimeHelpers.GetUninitializedObject(typeof(GalleryViewer));
        typeof(GalleryViewer).GetField("_terminalRetirement", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(viewer, true);
        typeof(GalleryViewer).GetMethod(operation, BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(viewer, null);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NativeOverlayCannotBeReopenedOrRestoredDuringTerminalRetirement(bool active)
    {
        var viewer = (GalleryViewer)RuntimeHelpers.GetUninitializedObject(typeof(GalleryViewer));
        typeof(GalleryViewer).GetField("_terminalRetirement", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(viewer, true);
        typeof(GalleryViewer).GetMethod("UseNativeVideoOverlay", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(viewer, new object[] { active });
    }

    public static IEnumerable<object[]> RetiredInputCallbacks()
    {
        string[] operations = ["RememberPointer", "RefreshHoverGeometry", "RevealHoveredPanels",
            "OnOverlayPointerMoved", "OnIndicatorExpiry", "OnOverlayPointerExited", "OnOverlayPointerPressed",
            "OnOverlayPointerReleased", "OnOverlayPointerCaptureLost", "OnOverlayPointerWheel", "OnOverlayTap",
            "OnViewerInteractionTick"];
        foreach (string operation in operations)
        foreach (string stoppedState in new[] { "_terminalRetirement", "_viewerInteractionStopped" })
            yield return new object[] { operation, stoppedState };
    }

    [Theory]
    [MemberData(nameof(RetiredInputCallbacks))]
    public void RetiredOrDetachedViewerIgnoresLatePointerAndTimerCallbacks(string operation, string stoppedState)
    {
        // No initialized visual tree or event payload: callbacks queued before
        // Stop/detach must return before touching either, not revive indicators,
        // reacquire the old pointer or perform screen-coordinate conversion.
        var viewer = (GalleryViewer)RuntimeHelpers.GetUninitializedObject(typeof(GalleryViewer));
        typeof(GalleryViewer).GetField(stoppedState, BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(viewer, true);
        MethodInfo method = typeof(GalleryViewer).GetMethod(operation, BindingFlags.Instance | BindingFlags.NonPublic)!;
        method.Invoke(viewer, new object?[method.GetParameters().Length]);
        Assert.Null(typeof(GalleryViewer).GetField("_lastPointer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(viewer));
        Assert.Null(typeof(GalleryViewer).GetField("_lastPointerScreen", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(viewer));
        Assert.Null(typeof(GalleryViewer).GetField("_indicatorExpiryTimer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(viewer));
    }

}
