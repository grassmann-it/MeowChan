namespace MeowChan.Desktop;

internal enum SlideshowStatus { None, Ready, Running, Loading, Paused, SeekPaused, Context, Help, Menu, Inactive, Ended, NoMedia, Failed }

// Tagging (QW3): the gallery walks the tagging queue. The slideshow is not part of that mode, so none of its progress lines applies
// ("Paused: close the post context to continue" next to the tag panel was plainly wrong); a failed medium still says so.
internal readonly record struct SlideshowStatusInput(bool Selected, bool Running, bool Started, bool Terminal,
    bool HasMedia, bool Failed, bool Loading, bool Context, bool Help, bool Menu, bool Active, bool SeekPaused, bool Tagging = false)
{
    internal SlideshowStatus Resolve() => Failed ? SlideshowStatus.Failed : Tagging ? SlideshowStatus.None : !HasMedia ? SlideshowStatus.NoMedia
        : Loading ? SlideshowStatus.Loading : Context ? SlideshowStatus.Context : Help ? SlideshowStatus.Help
        : Menu ? SlideshowStatus.Menu : !Active ? SlideshowStatus.Inactive : !Selected ? SlideshowStatus.None
        : Running ? SlideshowStatus.Running : Terminal ? SlideshowStatus.Ended : !Started ? SlideshowStatus.Ready
        : SeekPaused ? SlideshowStatus.SeekPaused : SlideshowStatus.Paused;
}
