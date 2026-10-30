using System.Reflection;
using System.Runtime.CompilerServices;
using MeowChan.Desktop;

namespace MeowChan.DesktopTests;

public sealed class GalleryErrorPresentationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NotificationReportsWithoutTouchingFailureNavigationOrOverlay(bool mediaAlreadyFailed)
    {
        // Use the production notification entry without constructing a native window/player.
        // If it tries to reveal/stop/render, missing UI dependencies make this test fail.
        var viewer = (GalleryViewer)RuntimeHelpers.GetUninitializedObject(typeof(GalleryViewer));
        Set(viewer, "_mediaFailed", mediaAlreadyFailed);
        Set(viewer, "_overlayHidden", true);
        Set(viewer, "_navigationPending", true);
        Set(viewer, "_lastOverlayActivity", 123L);
        var reports = new List<GalleryDiagnosticNotice>();
        viewer.DiagnosticNotice += reports.Add;
        viewer.SetNotice("operation failed");
        var notice = Assert.Single(reports);
        Assert.Equal("notice", notice.Operation);
        Assert.False(notice.MediaUnavailable);
        Assert.Null(notice.Entry);
        Assert.Equal("operation failed", notice.Message);
        Assert.Equal(mediaAlreadyFailed, Get<bool>(viewer, "_mediaFailed"));
        Assert.True(Get<bool>(viewer, "_overlayHidden"));
        Assert.True(Get<bool>(viewer, "_navigationPending"));
        Assert.Equal(123L, Get<long>(viewer, "_lastOverlayActivity"));
    }

    [Fact]
    public void ClosedOrCanceledViewerRejectsLateNotificationsAndEmptyMessages()
    {
        var viewer = (GalleryViewer)RuntimeHelpers.GetUninitializedObject(typeof(GalleryViewer));
        int reports = 0;
        viewer.DiagnosticNotice += _ => reports++;
        viewer.SetNotice(""); viewer.SetNotice("   ");
        Set(viewer, "_viewerInteractionStopped", true);
        viewer.SetNotice("late closed");
        Set(viewer, "_viewerInteractionStopped", false);
        Set(viewer, "_sessionCancellation", new CancellationToken(canceled: true));
        viewer.SetNotice("late canceled");
        Assert.Equal(0, reports);
    }

    [Fact]
    public void DiagnosticPayloadIsBoundedAndDoesNotDismissOrClaimSuccess()
    {
        var viewer = (GalleryViewer)RuntimeHelpers.GetUninitializedObject(typeof(GalleryViewer));
        var reports = new List<GalleryDiagnosticNotice>();
        viewer.DiagnosticNotice += reports.Add;
        viewer.SetNotice(new string('x', 10000));
        Assert.Equal(2048, Assert.Single(reports).Message.Length);
        viewer.SetNotice("same operation still failed");
        Assert.Equal(2, reports.Count); // central history/ack policy owns deduplication, not a UI success flag
        Assert.All(reports, notice => Assert.False(notice.MediaUnavailable));
    }

    [Fact]
    public async Task StaleActualFailureCannotRetireOrReclassifyANewerMediaRequest()
    {
        var viewer = (GalleryViewer)RuntimeHelpers.GetUninitializedObject(typeof(GalleryViewer));
        Set(viewer, "_request", 42);
        int reports = 0;
        viewer.DiagnosticNotice += _ => reports++;
        await viewer.SetMediaUnavailable("old request failed", expectedRequestGeneration: 41);
        Assert.Equal(42, viewer.MediaRequestGeneration);
        Assert.Equal(0, reports);
        Assert.False(Get<bool>(viewer, "_mediaFailed"));
    }

    [Fact]
    public void DiagnosticNoticeCannotPinIdleControlsEvenIfObservedByTheOverlayPolicy()
    {
        var state = new GalleryOverlayActivity(Overlay: true, AutoHide: true, Available: true,
            ActiveWindow: true, Focused: false, Captured: false, Popup: false, Reading: false, Notice: true);
        Assert.True(state.CanHide(TimeSpan.FromSeconds(1)));
        Assert.False((state with { Captured = true }).CanHide(TimeSpan.FromSeconds(1)));
        Assert.False((state with { Popup = true }).CanHide(TimeSpan.FromSeconds(1)));
        Assert.False((state with { Reading = true }).CanHide(TimeSpan.FromSeconds(1)));
    }

    private static void Set(GalleryViewer viewer, string name, object value) =>
        typeof(GalleryViewer).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(viewer, value);
    private static T Get<T>(GalleryViewer viewer, string name) =>
        (T)typeof(GalleryViewer).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(viewer)!;
}
