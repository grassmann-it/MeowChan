namespace MeowChan.Desktop.Localization;

// Keep these additive labels independent of the shared settings catalog. Like the
// gallery itself, unsupported language packs use the complete English fallback.
internal static class GalleryPlaybackCatalog
{
    internal static string Title => GalleryViewerText.Get("Gallery & playback", "Galerie & Wiedergabe");
    internal static string Mode => GalleryViewerText.Get("Playback mode", "Wiedergabemodus");
    internal static string Manual => GalleryViewerText.Get("Manual", "Manuell");
    internal static string Repeat => GalleryViewerText.Get("Repeat current video", "Aktuelles Video wiederholen");
    internal static string Slideshow => GalleryViewerText.Get("Slideshow · images & videos", "Diashow · Bilder & Videos");
    internal static string ImageSeconds => GalleryViewerText.Get("Image duration (seconds)", "Bilddauer (Sekunden)");
    internal static string ImageSecondsHelp => GalleryViewerText.Get("Whole seconds, from 1 to 300. Videos advance when playback ends.", "Ganze Sekunden von 1 bis 300. Videos wechseln nach Wiedergabeende.");
    internal static string ModeHelp => GalleryViewerText.Get("Slideshow includes images and videos. Start it explicitly in the gallery; saving this choice never starts a slideshow.", "Die Diashow umfasst Bilder und Videos. Starte sie ausdrücklich in der Galerie; das Speichern dieser Auswahl startet keine Diashow.");
    internal static string MediaFocus => GalleryViewerText.Get("Use media focus in the gallery", "Medienfokus in der Galerie verwenden");
    internal static string OverlayControls => GalleryViewerText.Get("Auto-hide overlay controls · reveal at the bottom edge", "Overlay-Bedienelemente automatisch ausblenden · am unteren Rand einblenden");
    internal static string AutoHideControls => OverlayControls;
    internal static string OverlayPostContext => GalleryViewerText.Get("Show post context on right-edge hover", "Postkontext beim Überfahren des rechten Rands anzeigen");
    internal static string OverlayHelp => GalleryViewerText.Get("Controls overlay and auto-hide are one choice: reveal controls at the bottom edge; turn off to keep controls docked. Media focus and right-edge post context are independent choices. Overlay support depends on the platform and renderer; unsupported layouts retain docked controls and context.", "Overlay-Bedienelemente und automatisches Ausblenden sind eine gemeinsame Option: am unteren Rand einblenden; ausgeschaltet bleiben die Bedienelemente angedockt. Medienfokus und Postkontext am rechten Rand sind unabhängige Optionen. Overlay-Unterstützung hängt von Plattform und Renderer ab; nicht unterstützte Darstellungen behalten angedockte Bedienelemente und Kontext.");
    internal static string ProfileHelp => GalleryViewerText.Get("Playback mode and gallery layout are remembered for this profile. Video autoplay, start-muted and volume remain separate choices.", "Wiedergabemodus und Galeriedarstellung werden für dieses Profil gemerkt. Video-Autoplay, Stummstart und Lautstärke bleiben getrennte Einstellungen.");
}
