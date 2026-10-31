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

    [Fact]
    public void TerminalStopClosesViewerAndPlayerPlacementBeforeFirstAwait()
    {
        // Source-order contract only: do not instantiate a player/native drawable or
        // invoke UI-thread-only retirement from this otherwise framework-free suite.
        string source = ReadDesktopSource("GalleryViewer.cs");
        int start = source.IndexOf("internal async Task StopAsync()", StringComparison.Ordinal);
        Assert.True(start >= 0, "Viewer StopAsync declaration was not found.");
        string stop = WithoutLineComments(source[start..]);
        var firstAwait = System.Text.RegularExpressions.Regex.Match(stop, @"\bawait\b");
        Assert.True(firstAwait.Success, "StopAsync no longer has the expected asynchronous cleanup boundary.");
        string admission = stop[..firstAwait.Index];

        int terminal = admission.IndexOf("_terminalRetirement = true;", StringComparison.Ordinal);
        int cancel = admission.IndexOf("GalleryControlTransfer.Cancel(control);", StringComparison.Ordinal);
        int player = admission.IndexOf("_video?.RetireTransportPlacement();", StringComparison.Ordinal);
        int interaction = admission.IndexOf("StopViewerInteraction();", StringComparison.Ordinal);
        Assert.True(terminal >= 0 && cancel > terminal && player > cancel && interaction > player,
            "Close viewer admission, cancel owned placements, and retire player placement before interaction callbacks or any await.");
        foreach (string control in new[] { "_overlayFooter", "_chrome", "_infoOverlay", "_contextPanel",
            "_helpPanel", "_controlsEdgeIndicator", "_postEdgeIndicator" })
            Assert.Contains(control, admission[terminal..cancel]);
        Assert.DoesNotContain("IsDisposed", admission);
    }

    [Fact]
    public void ExplicitTransportRetirementClosesAdmissionWithoutWaitingForPlayerDisposal()
    {
        string source = ReadDesktopSource("GalleryVideoPlayer.cs");
        int start = source.IndexOf("internal void RetireTransportPlacement()", StringComparison.Ordinal);
        Assert.True(start >= 0, "Explicit transport placement retirement was not found.");
        int end = source.IndexOf("private void DetachTransport()", start, StringComparison.Ordinal);
        Assert.True(end > start, "Transport retirement boundary was not found.");
        string retirement = WithoutLineComments(source[start..end]);
        int terminal = retirement.IndexOf("_transportPlacementRetired = true;", StringComparison.Ordinal);
        int cancel = retirement.IndexOf("GalleryControlTransfer.Cancel(_transportPanel);", StringComparison.Ordinal);
        Assert.True(terminal >= 0 && cancel > terminal,
            "Transport admission must close before cancellation can trigger visual-tree callbacks.");
        Assert.DoesNotContain("IsDisposed", retirement);
        Assert.DoesNotContain("await", retirement);
        Assert.DoesNotContain("if (", retirement);
    }

    private static string WithoutLineComments(string source) =>
        System.Text.RegularExpressions.Regex.Replace(source, @"//[^\r\n]*", string.Empty);

    private static string ReadDesktopSource(string name, [CallerFilePath] string testPath = "") =>
        File.ReadAllText(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(testPath)!,
            "..", "..", "Desktop", name)));

}
