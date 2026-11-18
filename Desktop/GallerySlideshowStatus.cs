namespace MeowChan.Desktop;

internal enum SlideshowStatus { None, Ready, Running, Loading, Paused, SeekPaused, Context, Help, Menu, Inactive, Ended, NoMedia, Failed, TagHold }

// Tagging (QW3): the gallery walks the tagging queue. None of the slideshow's progress lines applies there ("Paused: close the post context
// to continue" next to the tag panel was plainly wrong); a failed medium still says so. G1 (Tom's E3 addition): the slideshow may run in the
// mode, and the one line it then has is TagHold: it waits while the tag panel is worked in and goes on with the shown medium afterwards.
internal readonly record struct SlideshowStatusInput(bool Selected, bool Running, bool Started, bool Terminal,
    bool HasMedia, bool Failed, bool Loading, bool Context, bool Help, bool Menu, bool Active, bool SeekPaused, bool Tagging = false,
    bool TagHold = false)
{
    internal SlideshowStatus Resolve() => Failed ? SlideshowStatus.Failed
        : Tagging ? (Running && TagHold ? SlideshowStatus.TagHold : SlideshowStatus.None) : !HasMedia ? SlideshowStatus.NoMedia
        : Loading ? SlideshowStatus.Loading : Context ? SlideshowStatus.Context : Help ? SlideshowStatus.Help
        : Menu ? SlideshowStatus.Menu : !Active ? SlideshowStatus.Inactive : !Selected ? SlideshowStatus.None
        : Running ? SlideshowStatus.Running : Terminal ? SlideshowStatus.Ended : !Started ? SlideshowStatus.Ready
        : SeekPaused ? SlideshowStatus.SeekPaused : SlideshowStatus.Paused;
}
