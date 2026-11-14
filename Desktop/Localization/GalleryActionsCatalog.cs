namespace MeowChan.Desktop.Localization;

/// <summary>
/// Texts of the gallery and media quick wins: the plate for a file without a viewer, copy path, the post menu copies,
/// the live-follow reasons, the favorite loading tip and the slideshow duration. Registered in BuiltInCatalog;
/// es/fr/ru are machine translations that no human has proofread.
/// </summary>
internal static class GalleryActionsCatalog
{
    internal static readonly (string Key, string English, string German)[] Entries =
    {
        ("gallery.fileOnly.body", "This file ({0}, {1}) has no built-in viewer.", "Diese Datei ({0}, {1}) hat keinen eingebauten Viewer."),
        ("gallery.fileOnly.type", "{0} file", "{0}-Datei"),
        ("gallery.fileOnly.typeUnknown", "file", "Datei"),
        ("gallery.fileOnly.blocked", "This file type could run as a program, so it is not opened from here. Show it in its folder instead.", "Dieser Dateityp könnte als Programm laufen und wird deshalb nicht von hier geöffnet. Stattdessen im Ordner anzeigen."),
        ("gallery.fileOnly.missing", "The saved file is not on disk any more.", "Die gespeicherte Datei liegt nicht mehr auf dem Datenträger."),
        ("gallery.actionFailed", "The action could not be carried out. Nothing was changed. Try again; details: {0}", "Die Aktion konnte nicht ausgeführt werden. Es wurde nichts geändert. Erneut versuchen; Details: {0}"),
        ("media.copyPath.done", "Path copied", "Pfad kopiert"),
        ("media.copyPath.failed", "The path could not be copied to the clipboard.", "Der Pfad konnte nicht in die Zwischenablage kopiert werden."),
        ("post.copy.textDone", "Post text copied", "Post-Text kopiert"),
        ("post.copy.numberDone", "Post number copied", "Post-Nummer kopiert"),
        ("post.copy.linkDone", "Post link copied", "Post-Link kopiert"),
        ("post.copy.failed", "Could not copy to the clipboard.", "Konnte nicht in die Zwischenablage kopieren."),
        ("archive.threadFavorite.loading", "Thread favorite status is loading…", "Favoritenstatus wird geladen…"),
        ("reader.live.reason.notWatched", "Not possible: this thread is not being watched.", "Nicht möglich: Dieser Thread wird nicht beobachtet."),
        ("reader.live.reason.captureOff", "Not possible: post capture is off for this thread.", "Nicht möglich: Die Post-Erfassung ist für diesen Thread aus."),
        ("reader.live.reason.sourceArchived", "Not possible: this thread is archived at its source.", "Nicht möglich: Der Thread ist am Ursprung archiviert."),
        ("reader.live.reason.sourceGone", "Not possible: this thread is no longer available at its source.", "Nicht möglich: Der Thread ist am Ursprung nicht mehr vorhanden."),
        ("gallery.slideshow.seconds", "{0} s", "{0} s"),
        ("gallery.slideshow.secondsLabel", "Seconds per image in the slideshow", "Sekunden pro Bild in der Diashow"),
        // G1: the gallery's mode switch "View | Tag" (tagging is a mode of the gallery) and what the screen reader hears when it changes.
        ("gallery.mode.name", "Gallery mode", "Galeriemodus"),
        ("gallery.mode.view", "View", "Ansehen"),
        ("gallery.mode.tag", "Tag", "Taggen"),
        ("gallery.mode.announce.tag", "Tag mode: favorite, rating and tags are in the tag panel.", "Modus Taggen: Favorit, Bewertung und Tags stehen im Tag-Panel."),
        ("gallery.mode.announce.view", "View mode.", "Modus Ansehen."),
        ("gallery.overflow.label", "More gallery actions", "Weitere Galerieaktionen"),
        ("gallery.overflow.help", "Zoom, rating, tags, export, playback, view options and shortcut help", "Zoom, Bewertung, Tags, Export, Wiedergabe, Ansichtsoptionen und Tastaturhilfe"),
        // V2: the gallery side panel host (segment "Post | Tags", the isolated close button and the drag edge).
        ("gallery.panel.post", "Post", "Post"),
        ("gallery.panel.tags", "Tags", "Tags"),
        ("gallery.panel.segments", "Panel content", "Panelinhalt"),
        ("gallery.panel.segmentHelp", "The left and right arrow keys switch between post and tags.", "Die Pfeiltasten links und rechts wechseln zwischen Post und Tags."),
        ("gallery.panel.close", "Close panel", "Panel schließen"),
        ("gallery.panel.resize", "Panel width: drag to change ({0}–{1})", "Panelbreite: zum Ändern ziehen ({0}–{1})")
    };
}
