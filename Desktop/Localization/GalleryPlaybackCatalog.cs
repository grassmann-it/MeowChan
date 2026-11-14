namespace MeowChan.Desktop.Localization;

// Settings presentation and viewer playback strings use the normal global catalog.
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

    // Data-only entries integrated into the normal global EN/DE catalog and language packs.
    internal static readonly (string Key, string English, string German)[] Entries =
    {
        ("viewer.playback.status.ready", "Ready — start slideshow explicitly", "Bereit — Diashow bewusst starten"),
        ("viewer.playback.status.running", "Slideshow running", "Diashow läuft"),
        ("viewer.playback.status.loading", "Waiting for the medium to load", "Wartet auf das Laden des Mediums"),
        ("viewer.playback.status.context", "Paused — close post context to continue", "Pausiert — Post-Kontext zum Fortsetzen schließen"),
        ("viewer.playback.status.help", "Paused — close help to continue", "Pausiert — Hilfe zum Fortsetzen schließen"),
        ("viewer.playback.status.menu", "Paused — close the menu or dialog", "Pausiert — Menü oder Dialog schließen"),
        ("viewer.playback.status.inactive", "Paused — gallery is not active", "Pausiert — Galerie ist nicht aktiv"),
        ("viewer.playback.status.ended", "End of this sequence", "Ende dieser Medienfolge"),
        ("viewer.playback.status.noMedia", "No playable medium selected", "Kein abspielbares Medium ausgewählt"),
        ("viewer.playback.status.failed", "Playback failed — inspect the media error", "Wiedergabe fehlgeschlagen — Medienfehler prüfen"),
        ("viewer.playback.status.seekPaused", "Paused after seeking — resume explicitly", "Nach Spulen pausiert — bewusst fortsetzen"),
        ("viewer.playback.status.paused", "Paused — resume explicitly", "Pausiert — bewusst fortsetzen"),
        ("viewer.playback.manual", "Manual", "Manuell"),
        ("viewer.playback.repeat", "Repeat video", "Video wiederholen"),
        ("viewer.playback.slideshow", "Slideshow", "Diashow"),
        ("viewer.playback.mode", "Playback mode", "Wiedergabemodus"),
        ("viewer.playback.modeHelp", "Choosing Slideshow does not start it. Use Start slideshow.", "Diashow auswählen startet sie nicht. Mit Diashow starten beginnen."),
        ("viewer.playback.pause", "Pause slideshow", "Diashow pausieren"),
        ("viewer.playback.start", "Start slideshow", "Diashow starten"),
        ("viewer.playback.controlsOverlay", "Auto-hide controls overlay", "Controls-Overlay automatisch ausblenden"),
        ("viewer.playback.autoHide", "Auto-hide", "Auto-Hide"),
        ("viewer.playback.autoHideHelp", "Reveal controls at the bottom edge; leave the panel to hide it. Keyboard access remains available.", "Controls am unteren Rand einblenden; beim Verlassen einklappen. Tastaturzugriff bleibt verfügbar."),
        ("viewer.playback.postOverlay", "Post overlay", "Post-Overlay"),
        ("viewer.playback.overlayUnavailable", "Overlay unavailable for this native video surface. Controls remain docked.", "Overlay für diese native Videofläche nicht verfügbar. Controls bleiben angedockt."),
        ("viewer.playback.enableOverlay", "Enable a supported controls overlay first", "Zuerst ein unterstütztes Controls-Overlay aktivieren"),
        ("viewer.playback.repeatHelp", "Repeats the shown video outside a slideshow. In a running slideshow the video rule decides.", "Wiederholt das gezeigte Video außerhalb der Diashow. In einer laufenden Diashow entscheidet die Videoregel."),
        ("viewer.playback.repeatInSlideshow", "The running slideshow decides about repeating (⋯ → Videos in the slideshow)", "Die laufende Diashow entscheidet über Wiederholungen (⋯ → Videos in der Diashow)"),
        ("viewer.playback.videoRule", "Videos in the slideshow", "Videos in der Diashow"),
        ("viewer.playback.videoRule.once", "Play once", "Einmal abspielen"),
        ("viewer.playback.videoRule.atLeast", "Loop for a minimum time", "Für eine Mindestdauer wiederholen"),
        ("viewer.playback.videoRule.repeat", "Repeat a number of times", "Mehrmals wiederholen"),
        ("viewer.playback.videoRuleHelp", "Images stay for the seconds per image. With a minimum time, the pass that crosses it plays to its end; then the slideshow continues. Moving on yourself always works.", "Bilder bleiben die Sekunden pro Bild stehen. Bei einer Mindestdauer läuft der Durchlauf, in dem sie überschritten wird, bis zum Ende; dann geht die Diashow weiter. Selbst weiterblättern geht immer."),
        ("viewer.playback.videoSeconds", "Minimum time (seconds)", "Mindestdauer (Sekunden)"),
        ("viewer.playback.videoSecondsHelp", "The pass that crosses this time plays to its end.", "Der Durchlauf, in dem diese Zeit überschritten wird, läuft bis zum Ende."),
        ("viewer.playback.videoRepeats", "Passes", "Durchläufe"),
        ("viewer.playback.numberHelp", "Arrow keys ±1, Page Up/Page Down ±10", "Pfeiltasten ±1, Bild auf/Bild ab ±10"),
        ("viewer.playback.reducedMotion", "Reduced motion is on: videos play once in the slideshow.", "Reduzierte Bewegung ist an: Videos laufen in der Diashow einmal."),
    };

    internal static string Get(string key) => Texts.T(key);
}
