namespace MeowChan.Desktop.Localization;

// Settings-only presentation: resolve the active catalog on every read so ordinary
// language changes and same-code pack reloads use the existing Settings refresh.
// The gallery viewer's EN/DE helper and playback behavior are intentionally unchanged.
internal static class GalleryPlaybackCatalog
{
    internal static string Title => Texts.T("settings.galleryPlayback.title");
    internal static string Mode => Texts.T("settings.galleryPlayback.mode");
    internal static string Manual => Texts.T("settings.galleryPlayback.manual");
    internal static string Repeat => Texts.T("settings.galleryPlayback.repeat");
    internal static string Slideshow => Texts.T("settings.galleryPlayback.slideshow");
    internal static string ImageSeconds => Texts.T("settings.galleryPlayback.imageSeconds");
    internal static string ImageSecondsHelp => Texts.T("settings.galleryPlayback.imageSecondsHelp");
    internal static string ModeHelp => Texts.T("settings.galleryPlayback.modeHelp");
    internal static string MediaFocus => Texts.T("settings.galleryPlayback.mediaFocus");
    internal static string OverlayControls => Texts.T("settings.galleryPlayback.overlayControls");
    internal static string AutoHideControls => OverlayControls;
    internal static string OverlayPostContext => Texts.T("settings.galleryPlayback.overlayPostContext");
    internal static string OverlayHelp => Texts.T("settings.galleryPlayback.overlayHelp");
    internal static string ProfileHelp => Texts.T("settings.galleryPlayback.profileHelp");
}
