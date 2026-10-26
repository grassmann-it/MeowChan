using Avalonia;
using Avalonia.Controls;

namespace MeowChan.DesktopTests;

public sealed class FullscreenWindowStateFeedbackTests
{
    [Fact]
    public void LaterNormalDestinationKeepsMaximizedProvenanceAndOriginalNormalGeometry()
    {
        MainWindow.FullscreenOrigin captured = MaximizedOrigin();
        MainWindow.FullscreenOrigin latest = captured.WithNormalRequest();

        Assert.Equal(WindowState.Normal, latest.State);
        Assert.True(latest.RestoredFromMaximized);
        Assert.Equal(captured.Client, latest.Client);
        Assert.Equal(captured.Position, latest.Position);
        Assert.Equal(captured.Monitor, latest.Monitor);
        Assert.Equal(captured.Decoration, latest.Decoration);
        Assert.Equal(WindowState.Maximized, captured.State);
    }

    [Fact]
    public void UnmaximizedExitRepairsKnownStaleFramesButAllowsLaterDeliberateLayoutSizes()
    {
        MainWindow.FullscreenOrigin latest = MaximizedOrigin().WithNormalRequest();
        Size stale = new(850, 530);
        Size[] recordedFrames = [stale];

        Assert.True(latest.ShouldRepairExitFrame(stale, recordedFrames, maximizePending: false));
        Assert.False(latest.ShouldRepairExitFrame(stale, recordedFrames, maximizePending: true));
        Assert.False(latest.ShouldRepairExitFrame(latest.Client, recordedFrames, maximizePending: false));
        Assert.False(latest.ShouldRepairExitFrame(new Size(1280, 684), recordedFrames, maximizePending: false));
        Assert.False(latest.ShouldRepairExitFrame(new Size(640, 480), recordedFrames, maximizePending: false));
    }

    [Fact]
    public void NormalResizeDestinationSurvivesAnotherRestoreWithTransientCancellationReset()
    {
        MainWindow.FullscreenOrigin captured = MaximizedOrigin();
        MainWindow.FullscreenOrigin latest = captured.WithNormalRequest();
        // CapturePendingFullscreenExitSize records the requested client after
        // cancellation. A late repair must retain both that size and Normal.
        latest = latest with { Client = new Size(1011, 709) };
        Size[] recordedFrames = [captured.Client];

        Assert.Equal(WindowState.Normal, latest.State);
        Assert.True(latest.RestoredFromMaximized);
        Assert.Equal(new Size(1011, 709), latest.Client);
        Assert.True(latest.ShouldRepairExitFrame(captured.Client, recordedFrames, maximizePending: false));
        Assert.False(latest.ShouldRepairExitFrame(latest.Client, recordedFrames, maximizePending: false));
    }

    [Fact]
    public void FreshFullscreenEntryAfterCancellationDoesNotInheritPreviousMaximizedProvenance()
    {
        MainWindow.FullscreenOrigin cancelled = MaximizedOrigin().WithNormalRequest();
        MainWindow.FullscreenOrigin nextEntry = new(cancelled.State, cancelled.Client,
            cancelled.Position, cancelled.Monitor, cancelled.Decoration);

        Assert.False(nextEntry.RestoredFromMaximized);
        Assert.True(nextEntry.ShouldRepairExitFrame(new Size(850, 530), Array.Empty<Size>(), maximizePending: false));
        Assert.True(MaximizedOrigin().RestoredFromMaximized);
    }

    private static MainWindow.FullscreenOrigin MaximizedOrigin() => new(WindowState.Maximized,
        new Size(1000, 700), new PixelPoint(270, 55), new PixelRect(0, 0, 1920, 1080), new Size(0, 0));

    [Fact]
    public void NativeNormalFeedbackIsNotAnExplicitRestoreAndDoesNotHideLaterManagedNormal()
    {
        MainWindow.FullscreenWindowStateFeedback? feedback = null;
        List<WindowState> delivered = new();
        feedback = new(state =>
        {
            delivered.Add(state);
            Assert.False(feedback!.IsExplicitNormalRestore(WindowState.Maximized, state, false));
        });

        // Fullscreen entry and exit can acknowledge Normal/Maximized out of
        // order. Passing them on must not reinterpret them as new commands.
        feedback.OnNativeStateChanged(WindowState.Normal);
        feedback.OnNativeStateChanged(WindowState.Maximized);
        Assert.True(feedback.IsExplicitNormalRestore(WindowState.Maximized, WindowState.Normal, false));
        feedback.OnNativeStateChanged(WindowState.FullScreen);
        feedback.OnNativeStateChanged(WindowState.Normal);
        Assert.Equal(new[] { WindowState.Normal, WindowState.Maximized,
            WindowState.FullScreen, WindowState.Normal }, delivered);
        Assert.True(feedback.IsExplicitNormalRestore(WindowState.Maximized, WindowState.Normal, false));
    }

    [Fact]
    public void OnlyAnUnstagedManagedUnmaximizeIsAnExplicitNormalRestore()
    {
        MainWindow.FullscreenWindowStateFeedback feedback = new(null);
        Assert.False(feedback.IsExplicitNormalRestore(WindowState.Maximized, WindowState.Normal, true));
        Assert.False(feedback.IsExplicitNormalRestore(WindowState.FullScreen, WindowState.Normal, false));
        Assert.False(feedback.IsExplicitNormalRestore(WindowState.Normal, WindowState.Normal, false));
        Assert.False(feedback.IsExplicitNormalRestore(WindowState.Normal, WindowState.FullScreen, false));
        Assert.False(feedback.IsExplicitNormalRestore(WindowState.Maximized, WindowState.FullScreen, false));
        Assert.True(feedback.IsExplicitNormalRestore(WindowState.Maximized, WindowState.Normal, false));
    }

    [Fact]
    public void ReentrantNativeAcknowledgementKeepsOuterCallbackMarkedAsFeedback()
    {
        MainWindow.FullscreenWindowStateFeedback? feedback = null;
        feedback = new(state =>
        {
            if (state == WindowState.FullScreen)
                feedback!.OnNativeStateChanged(WindowState.Normal);
            Assert.False(feedback!.IsExplicitNormalRestore(WindowState.Maximized, WindowState.Normal, false));
        });

        feedback.OnNativeStateChanged(WindowState.FullScreen);
        Assert.True(feedback.IsExplicitNormalRestore(WindowState.Maximized, WindowState.Normal, false));
    }

    [Fact]
    public void NativeCallbackFailurePropagatesAndDoesNotMisclassifyLaterManagedRequest()
    {
        InvalidOperationException failure = new("native callback fixture");
        MainWindow.FullscreenWindowStateFeedback feedback = new(_ => throw failure);
        Assert.Same(failure, Assert.Throws<InvalidOperationException>(() =>
            feedback.OnNativeStateChanged(WindowState.Normal)));
        Assert.True(feedback.IsExplicitNormalRestore(WindowState.Maximized, WindowState.Normal, false));
    }
}
