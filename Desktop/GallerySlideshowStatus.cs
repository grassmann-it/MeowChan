namespace MeowChan.Desktop;

internal enum SlideshowStatus { None, Ready, Running, Loading, Paused, SeekPaused, Context, Help, Menu, Inactive, Ended, NoMedia, Failed }

internal readonly record struct SlideshowStatusInput(bool Selected, bool Running, bool Started, bool Terminal,
    bool HasMedia, bool Failed, bool Loading, bool Context, bool Help, bool Menu, bool Active, bool SeekPaused)
{
    internal SlideshowStatus Resolve() => Failed ? SlideshowStatus.Failed : !HasMedia ? SlideshowStatus.NoMedia
        : Loading ? SlideshowStatus.Loading : Context ? SlideshowStatus.Context : Help ? SlideshowStatus.Help
        : Menu ? SlideshowStatus.Menu : !Active ? SlideshowStatus.Inactive : !Selected ? SlideshowStatus.None
        : Running ? SlideshowStatus.Running : Terminal ? SlideshowStatus.Ended : !Started ? SlideshowStatus.Ready
        : SeekPaused ? SlideshowStatus.SeekPaused : SlideshowStatus.Paused;
}
