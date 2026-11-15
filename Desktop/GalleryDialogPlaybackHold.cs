using MeowChan.Gallery;

namespace MeowChan.Desktop;

/// <summary>
/// A modal dialog over the gallery (the rating dialog, review 15.11.) pauses the shown video and a running slideshow while it is open,
/// as "manage ratings" pauses the slideshow; when it closes only what was running comes back. Pure, so the rule is unit tested.
/// </summary>
internal readonly record struct GalleryDialogPlaybackHold(GalleryEntryKey? Entry, bool VideoWasPlaying, bool SlideshowWasRunning)
{
    /// <summary>
    /// What to resume after the dialog: nothing for another medium or a retired viewer, nothing that is running again already, and never
    /// something that was not running when the dialog opened.
    /// </summary>
    internal (bool Video, bool Slideshow) Resume(GalleryEntryKey? entryNow, bool videoPlayingNow, bool slideshowRunningNow, bool retired)
    {
        if (retired || Entry == null || entryNow != Entry) return (false, false);
        return (VideoWasPlaying && !videoPlayingNow, SlideshowWasRunning && !slideshowRunningNow);
    }
}
