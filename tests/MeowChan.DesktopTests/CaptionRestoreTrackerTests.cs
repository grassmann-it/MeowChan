using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using MeowChan.Desktop;

namespace MeowChan.DesktopTests;

// Pure decision coverage of the secondary-window caption restore guard; no X11 or window manager evidence.
public sealed class CaptionRestoreTrackerTests
{
    private static readonly Size Normal = new(800, 660);
    private static readonly Size Maximum = new(3200, 2000);

    private static CaptionRestoreTracker Restoring()
    {
        var tracker = new CaptionRestoreTracker();
        tracker.BeforeMaximize(Normal);
        tracker.BeforeRestore(Maximum);
        return tracker;
    }

    [Fact]
    public void NormalStateWithTheOldMaximumRestoresTheSnapshot()
    {
        CaptionRestoreTracker tracker = Restoring();
        Assert.Equal(Normal, tracker.StaleMaximum(WindowState.Normal, Maximum, Maximum.Width, Maximum.Height));
    }

    [Fact]
    public void ARestoredFrameNeedsNoCorrectionButKeepsWatchingForTheLateMaximum()
    {
        CaptionRestoreTracker tracker = Restoring();
        Assert.Null(tracker.StaleMaximum(WindowState.Normal, Normal, Normal.Width, Normal.Height));
        Assert.True(tracker.Pending);
        Assert.Equal(Normal, tracker.StaleMaximum(WindowState.Normal, Maximum, Maximum.Width, Maximum.Height));
    }

    [Fact]
    public void AnUnrelatedResizeSupersedesTheRestore()
    {
        CaptionRestoreTracker tracker = Restoring();
        Assert.Null(tracker.StaleMaximum(WindowState.Normal, new Size(1000, 700), 1000, 700));
        Assert.False(tracker.Pending);
        Assert.Null(tracker.StaleMaximum(WindowState.Normal, Maximum, Maximum.Width, Maximum.Height));
    }

    [Theory]
    [InlineData(WindowState.Maximized)]
    [InlineData(WindowState.Minimized)]
    public void ANewStateTransitionEndsTheWatch(WindowState next)
    {
        CaptionRestoreTracker tracker = Restoring();
        tracker.StateChanged(next);
        Assert.False(tracker.Pending);
        Assert.Null(tracker.StaleMaximum(WindowState.Normal, Maximum, Maximum.Width, Maximum.Height));
    }

    [Fact]
    public void TheAcknowledgementOfNormalKeepsTheWatch()
    {
        CaptionRestoreTracker tracker = Restoring();
        tracker.StateChanged(WindowState.Normal);
        Assert.True(tracker.Pending);
    }

    [Fact]
    public void NothingIsCorrectedWithoutACaptionRestore()
    {
        var tracker = new CaptionRestoreTracker();
        Assert.Null(tracker.StaleMaximum(WindowState.Normal, Maximum, Maximum.Width, Maximum.Height));
        tracker.BeforeRestore(Maximum);
        Assert.False(tracker.Pending);
    }

    [Fact]
    public void AStaleMaximumNeverReplacesTheNormalSnapshot()
    {
        CaptionRestoreTracker tracker = Restoring();
        tracker.BeforeMaximize(Maximum);
        tracker.BeforeRestore(Maximum);
        Assert.Equal(Normal, tracker.StaleMaximum(WindowState.Normal, Maximum, Maximum.Width, Maximum.Height));
    }

    [Fact]
    public void ARealResizeBeforeTheNextMaximizeBecomesTheNewSnapshot()
    {
        CaptionRestoreTracker tracker = Restoring();
        var resized = new Size(900, 700);
        tracker.StateChanged(WindowState.Maximized);
        tracker.BeforeMaximize(resized);
        tracker.BeforeRestore(Maximum);
        Assert.Equal(resized, tracker.StaleMaximum(WindowState.Normal, Maximum, Maximum.Width, Maximum.Height));
    }

    [Fact]
    public void BothSecondaryCaptionsShareTheGuardThroughTheChromeBar()
    {
        string source = ChromeBarSource();
        Assert.Contains("_restore.BeforeRestore(", source);
        Assert.Contains("_restore.StaleMaximum(", source);
    }

    private static string ChromeBarSource([CallerFilePath] string sourcePath = "") =>
        File.ReadAllText(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourcePath)!, "../..", "Desktop/WindowChromeBar.cs")));
}
