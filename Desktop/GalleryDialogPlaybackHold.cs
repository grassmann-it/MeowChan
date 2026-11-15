using MeowChan.Gallery;

namespace MeowChan.Desktop;

/// <summary>
/// A modal dialog over the gallery (the rating dialog, review 15.11.) pauses the shown video and a running slideshow while it is open,
/// as "manage ratings" pauses the slideshow; when it closes only what was running comes back. Pure, so the rule is unit tested.
/// </summary>
internal readonly record struct GalleryDialogPlaybackHold(GalleryEntryKey? Entry, bool VideoWasPlaying, bool SlideshowWasRunning)
{
    /// <summary>
    /// What to resume after the dialog. The slideshow belongs to the viewer, so it comes back even when the dialog changed the shown entry
    /// (a rating filter can move the slideshow on); the video belongs to its entry, so it only comes back for the same medium. Nothing
    /// comes back for a retired viewer, nothing that is running again already, and never something that was not running when the dialog
    /// opened. A pending navigation replaces the shown medium, so its video is not resumed, while the slideshow holds itself meanwhile.
    /// </summary>
    internal (bool Video, bool Slideshow) Resume(GalleryEntryKey? entryNow, bool videoPlayingNow, bool slideshowRunningNow, bool retired,
        bool navigationPending = false)
    {
        if (retired || Entry == null || entryNow == null) return (false, false);
        bool same = entryNow == Entry && !navigationPending;
        return (same && VideoWasPlaying && !videoPlayingNow, SlideshowWasRunning && !slideshowRunningNow);
    }
}
