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
        ("gallery.slideshow.secondsLabel", "Seconds per image in the slideshow", "Sekunden pro Bild in der Diashow")
    };
}
