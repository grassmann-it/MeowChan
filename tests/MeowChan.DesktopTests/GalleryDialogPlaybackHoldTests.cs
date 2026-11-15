using System.Runtime.CompilerServices;
using MeowChan.Desktop;
using MeowChan.Gallery;

namespace MeowChan.DesktopTests;

// Review 15.11.: the rating dialog over a playing gallery video pauses the video and a running slideshow and gives back only what was
// running when it opened; it activates itself and focuses the stars by any opening path. The rule is pure; the wiring is pinned in source.
public sealed class GalleryDialogPlaybackHoldTests
{
    private static GalleryEntryKey Key(string id) => new(new GalleryAssetKey(new("fixture", "gif", "1"), "dir", id), "p1", 0);

    [Theory]
    [InlineData(true, true, true, true)]
    [InlineData(true, false, true, false)]
    [InlineData(false, true, false, true)]
    [InlineData(false, false, false, false)]
    public void OnlyWhatWasRunningResumes(bool video, bool slideshow, bool resumeVideo, bool resumeSlideshow)
    {
        GalleryDialogPlaybackHold hold = new(Key("a"), video, slideshow);
        Assert.Equal((resumeVideo, resumeSlideshow), hold.Resume(Key("a"), videoPlayingNow: false, slideshowRunningNow: false, retired: false));
    }

    [Fact]
    public void NothingResumesForAnotherMediumARetiredViewerOrWhatRunsAgainAlready()
    {
        GalleryDialogPlaybackHold hold = new(Key("a"), VideoWasPlaying: true, SlideshowWasRunning: true);
        Assert.Equal((false, false), hold.Resume(Key("b"), false, false, retired: false));
        Assert.Equal((false, false), hold.Resume(Key("a"), false, false, retired: true));
        Assert.Equal((false, false), hold.Resume(null, false, false, retired: false));
        Assert.Equal((false, true), hold.Resume(Key("a"), videoPlayingNow: true, slideshowRunningNow: false, retired: false));
        Assert.Equal((true, false), hold.Resume(Key("a"), videoPlayingNow: false, slideshowRunningNow: true, retired: false));
        Assert.Equal((false, false), new GalleryDialogPlaybackHold(null, true, true).Resume(null, false, false, retired: false));
    }

    [Fact]
    public void TheShellHoldsThePlaybackAroundTheRatingDialogAndTheDialogActivatesItself()
    {
        string shell = Source("Desktop/Shell/MainWindow.Ratings.cs");
        int begin = shell.IndexOf("held?.BeginDialogPlaybackHold();", StringComparison.Ordinal);
        int show = shell.IndexOf("await dialog.ShowDialog(this);", StringComparison.Ordinal);
        int end = shell.IndexOf("await held.EndDialogPlaybackHoldAsync();", StringComparison.Ordinal);
        Assert.True(begin > 0 && show > begin && end > show);
        string viewer = Source("Desktop/GalleryViewer.Playback.cs");
        Assert.Contains("PauseSlideshow();\n        if (video) _ = _video!.PauseForDialogAsync();", viewer.Replace("\r\n", "\n"));
        // Silent pause/resume: no user Play/Pause request, so the slideshow's own reaction does not run.
        string player = Source("Desktop/GalleryVideoPlayer.cs");
        Assert.Contains("PauseForDialogAsync() => SetPlaybackModeAsync(VideoPlaybackMode.Paused, notify: false)", player);
        Assert.Contains("ResumeAfterDialogAsync() => SetPlaybackModeAsync(VideoPlaybackMode.Playing, notify: false)", player);
        string dialog = Source("Desktop/MediaRatingDialog.cs");
        int opened = dialog.IndexOf("Opened += (_, _) =>", StringComparison.Ordinal);
        Assert.True(opened > 0);
        Assert.True(dialog.IndexOf("Activate();", opened, StringComparison.Ordinal) > opened);
        Assert.True(dialog.IndexOf("_rating.Focus();", opened, StringComparison.Ordinal) > opened);
    }

    private static string Source(string relative, [CallerFilePath] string source = "") =>
        File.ReadAllText(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, "../..", relative)));
}
