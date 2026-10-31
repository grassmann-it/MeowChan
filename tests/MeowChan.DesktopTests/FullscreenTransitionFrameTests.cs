using Avalonia;
using Avalonia.Controls;

namespace MeowChan.DesktopTests;

public sealed class FullscreenTransitionFrameTests
{
    [Fact]
    public void RecordedEntryFrameCannotReplaceTheNewerNormalResizeTarget()
    {
        // Actual ARM64 trace: entry acknowledged an old native normal frame,
        // then fullscreen; after exit and an explicit resize it repeated the
        // old frame with Reason=User. Keep that observation across entry/exit.
        Size intermediate = new(1380, 850);
        Size fullscreen = new(1920, 1080);
        Size originalNormal = new(1000, 700);
        Size requestedNormal = new(1011, 709);
        List<Size> history = new();
        MainWindow.RememberFullscreenTransitionClient(history, intermediate);
        MainWindow.RememberFullscreenTransitionClient(history, fullscreen);
        MainWindow.RememberFullscreenTransitionClient(history, originalNormal);
        MainWindow.FullscreenOrigin intent = new(WindowState.Normal, requestedNormal,
            new PixelPoint(270, 58), new PixelRect(0, 0, 1920, 1080), default);

        Assert.Contains(intermediate, history);
        Assert.True(MainWindow.ShouldRepairKnownMaximizedExitFrame(intermediate, intent.Client, history));
        Assert.Equal(requestedNormal, intent.Client);
        Assert.False(MainWindow.ShouldRepairKnownMaximizedExitFrame(requestedNormal, intent.Client, history));
        Assert.DoesNotContain(new Size(1280, 684), history); // unrelated user drag is not native feedback
    }

    [Fact]
    public void DuplicateNativeFeedbackDoesNotEvictAnotherObservedTransitionFrame()
    {
        List<Size> history = new();
        Size intermediate = new(1380, 850);
        Assert.True(MainWindow.RememberFullscreenTransitionClient(history, intermediate));
        for (int i = 0; i < 10; i++)
            Assert.False(MainWindow.RememberFullscreenTransitionClient(history, intermediate));
        Assert.Equal(intermediate, Assert.Single(history));
    }

    [Fact]
    public void NativeHistoryRemainsBoundedAndFreshEntryCanDiscardPreviousGeneration()
    {
        List<Size> history = new();
        for (int i = 0; i < 20; i++)
            MainWindow.RememberFullscreenTransitionClient(history, new Size(1000 + i, 700 + i));
        Assert.Equal(4, history.Count);
        Assert.Equal(new Size(1016, 716), history[0]);
        Assert.Equal(new Size(1019, 719), history[^1]);
        history.Clear(); // ClearFullscreenExitIntent at the next explicit entry
        Assert.Empty(history);
        Assert.True(MainWindow.RememberFullscreenTransitionClient(history, new Size(1019, 719)));
    }
}
