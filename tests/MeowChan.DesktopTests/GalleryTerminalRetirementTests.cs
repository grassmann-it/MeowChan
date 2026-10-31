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
}
