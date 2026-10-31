using Avalonia;
using Avalonia.Controls;

namespace MeowChan.DesktopTests;

public sealed class FullscreenFeedbackReplayTests
{
    private static readonly Size Intermediate = new(1380, 850);
    private static readonly Size Requested = new(1011, 709);
    private static readonly Size Deliberate = new(1037, 723);
    private static MainWindow.FullscreenOrigin Intent(Size size) => new(WindowState.Normal,
        size, new PixelPoint(270, 58), new PixelRect(0, 0, 1920, 1080), default);
    private static MainWindow.FullscreenExitRepairSnapshot Snapshot(Size client) =>
        new(Intent(Requested), false, false, false, WindowState.Normal, client, false);

    [Fact]
    public void EntryFeedbackRemainsKnownAndQueuedCorrectionUsesCurrentTarget()
    {
        List<Size> history = new();
        MainWindow.RememberFullscreenTransitionClient(history, Intermediate);
        MainWindow.RememberFullscreenTransitionClient(history, new Size(1920, 1080));
        Assert.Equal(MainWindow.FullscreenUserResizeAction.RememberTransition,
            MainWindow.ClassifyFullscreenUserResize(true, Intermediate, Requested, history));
        // Production LeaveFullscreen does not clear entry history. The native
        // smoke observes real entry/exit frames without fabricated callbacks;
        // bounded FIFO eviction remains permitted during a real transition.
        Assert.Contains(Intermediate, history);
        Assert.Equal(MainWindow.FullscreenUserResizeAction.RepairKnownFrame,
            MainWindow.ClassifyFullscreenUserResize(false, Intermediate, Requested, history));
        var current = Snapshot(Intermediate);
        int generation = 7, queuedGeneration = generation;
        Queue<Action> dispatcher = new();
        List<Size> applied = new();
        Assert.Equal(Requested, MainWindow.ResolveFullscreenExitSizeRepair(current, history, queuedGeneration, generation));
        dispatcher.Enqueue(() =>
        {
            if (MainWindow.ResolveFullscreenExitSizeRepair(current, history, queuedGeneration, generation) is Size target)
                applied.Add(target);
        });
        current = current with { Intent = Intent(Deliberate) }; // Later explicit destination before dispatch.
        Assert.Single(dispatcher);
        dispatcher.Dequeue()();
        Assert.Equal(Deliberate, Assert.Single(applied));
    }

    [Fact]
    public void UnknownUserResizeSupersedesAlreadyQueuedKnownFrameCorrection()
    {
        List<Size> history = [Intermediate];
        var current = Snapshot(Intermediate);
        int generation = 7, queuedGeneration = generation;
        Queue<Action> dispatcher = new();
        List<Size> applied = new();
        Assert.NotNull(MainWindow.ResolveFullscreenExitSizeRepair(current, history, queuedGeneration, generation));
        dispatcher.Enqueue(() =>
        {
            if (MainWindow.ResolveFullscreenExitSizeRepair(current, history, queuedGeneration, generation) is Size target)
                applied.Add(target);
        });
        Assert.Equal(MainWindow.FullscreenUserResizeAction.AcceptUserSize,
            MainWindow.ClassifyFullscreenUserResize(false, Deliberate, Requested, history));
        history.Clear();
        current = current with { Intent = Intent(Deliberate), Client = Deliberate };
        Assert.Single(dispatcher);
        dispatcher.Dequeue()(); // Actually execute obsolete queued work; no native platform exists.
        Assert.Empty(applied);
        Assert.Equal(Deliberate, current.Intent!.Client);
        Assert.Equal(Deliberate, current.Client);
    }

    [Fact]
    public void FreshGenerationInvalidatesAnAlreadyQueuedCorrection()
    {
        List<Size> history = [Intermediate];
        var current = Snapshot(Intermediate);
        Assert.Equal(Requested, MainWindow.ResolveFullscreenExitSizeRepair(current, history, 7, 7));
        Queue<Action> dispatcher = new();
        List<Size> applied = new();
        int generation = 7;
        dispatcher.Enqueue(() =>
        {
            if (MainWindow.ResolveFullscreenExitSizeRepair(current, history, 7, generation) is Size target)
                applied.Add(target);
        });
        generation++;
        history.Clear(); // Same reset performed by a fresh explicit fullscreen entry.
        dispatcher.Dequeue()();
        Assert.Empty(applied);
    }

    [Fact]
    public void CorrectionAdmissionStillRejectsRestoringEnteringClosingAndNonNormalStates()
    {
        List<Size> history = [Intermediate];
        var current = Snapshot(Intermediate);
        foreach (var blocked in new[] { current with { Restoring = true }, current with { Entering = true },
            current with { Closing = true }, current with { State = WindowState.FullScreen },
            current with { Intent = null }, current with { Client = Requested } })
            Assert.Null(MainWindow.ResolveFullscreenExitSizeRepair(blocked, history, 7, 7));
        Assert.Equal(MainWindow.FullscreenUserResizeAction.AcceptUserSize,
            MainWindow.ClassifyFullscreenUserResize(false, Requested, Requested, history));
    }
}
