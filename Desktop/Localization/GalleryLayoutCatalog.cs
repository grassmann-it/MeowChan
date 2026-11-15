namespace MeowChan.Desktop.Localization;

/// <summary>Texts of the saved gallery layout (GL4): the settings page's "Reset gallery layout" row.</summary>
internal static class GalleryLayoutCatalog
{
    internal static readonly (string Key, string English, string German)[] Entries =
    {
        ("settings.galleryLayoutReset", "Reset gallery layout", "Galerie-Layout zurücksetzen"),
        ("settings.galleryLayoutResetHint", "Blocks and filmstrip position, for each mode. View and Tag both return to the defaults of the work view.", "Blöcke und Filmstreifen-Position, je Modus. Ansehen und Taggen kehren zum Standard der Ansicht zurück."),
        ("settings.galleryLayoutResetDone", "The gallery layout of both modes returns to the defaults of the work view when you save.", "Das Galerie-Layout beider Modi kehrt beim Speichern zum Standard der Ansicht zurück."),
        // GL5c: how long a transient status of the info line stays.
        ("settings.galleryStatusDwell", "Show status messages for", "Statusmeldungen anzeigen für"),
        ("settings.galleryStatusDwellHint", "How long a short status of the gallery's info line, such as “ready” of the slideshow, stays on the screen. A running slideshow, reasons for a pause and errors stay. Default {0} ms, from {1} to {2} ms.", "Wie lange ein kurzer Status der Infozeile der Galerie, etwa „Bereit“ der Diashow, sichtbar bleibt. Eine laufende Diashow, Pausengründe und Fehler bleiben stehen. Standard {0} ms, von {1} bis {2} ms."),
        // GL3: the layout menu (the "Layout" button beside "More" and the right click on free dock area).
        ("gallery.layout.button", "Layout", "Layout"),
        ("gallery.layout.help", "Choose which blocks the gallery shows: filmstrip, tools, info line and hints.", "Wähle, welche Blöcke die Galerie zeigt: Filmstreifen, Werkzeuge, Infozeile und Hinweise."),
        ("gallery.layout.mode", "Layout for the mode: {0}", "Layout für den Modus: {0}"),
        ("gallery.layout.filmstrip", "Filmstrip", "Filmstreifen"),
        ("gallery.layout.filmstripTop", "Filmstrip at the top", "Filmstreifen oben"),
        ("gallery.layout.filmstripBottom", "Filmstrip at the bottom", "Filmstreifen unten"),
        ("gallery.layout.tools", "Tools", "Werkzeuge"),
        ("gallery.layout.info", "Info line", "Infozeile"),
        ("gallery.layout.hints", "Hints", "Hinweise"),
        ("gallery.layout.reset", "Reset layout", "Layout zurücksetzen"),
        ("gallery.layout.resetHelp", "Back to the default of the work view for this mode.", "Zurück zum Standard der Arbeitsansicht für diesen Modus."),
        ("gallery.layout.resetDone", "{0}: layout reset to the default of the work view.", "{0}: Layout auf den Standard der Arbeitsansicht zurückgesetzt."),
        ("gallery.layout.filmstripHelp", "Thumbnails of the media before and after the shown one.", "Vorschaubilder der Medien vor und nach dem gezeigten."),
        ("gallery.layout.toolsHelp", "Zoom, post panel, favorite, view tools and slideshow. The arrows with the position and ⋯ stay.", "Zoom, Post-Panel, Favorit, Ansichtswerkzeuge und Diashow. Die Pfeile mit der Position und ⋯ bleiben."),
        ("gallery.layout.infoHelp", "One line: size, zoom, source and number, plus the rating where the bar has no rating button. Errors and warnings always stay.", "Eine Zeile: Größe, Zoom, Quelle und Nummer, dazu die Bewertung, wo die Leiste keinen Bewertungsknopf hat. Fehler und Warnungen bleiben immer."),
        ("gallery.layout.hintsHelp", "The status on the right of the info line: slideshow ready or running. “Ready” fades out after a few seconds; reasons for a pause and errors always stay.", "Der Status rechts in der Infozeile: Diashow bereit oder läuft. „Bereit“ verschwindet nach einigen Sekunden; Pausengründe und Fehler bleiben immer."),
        ("gallery.layout.hintOwn", "Your choice", "Eigene Wahl"),
        ("gallery.layout.hintView", "Default of the work view", "Standard der Arbeitsansicht"),
        ("gallery.layout.whyFocus", "The tools are hidden. Show them again to change the layout.", "Die Werkzeuge sind ausgeblendet. Blende sie wieder ein, um das Layout zu ändern."),
        ("gallery.layout.whyLow", "The window is too low for the filmstrip.", "Das Fenster ist zu niedrig für den Filmstreifen."),
        ("gallery.layout.whyTag", "The Tag mode has its own controls instead of the tools.", "Der Modus Taggen hat eigene Bedienelemente statt der Werkzeuge."),
        ("gallery.layout.whyPanel", "The tag panel says these sentences itself.", "Das Tag-Panel sagt diese Sätze selbst."),
        ("gallery.layout.whyFilmstripOff", "Turn the filmstrip on first.", "Schalte zuerst den Filmstreifen ein."),
        ("gallery.layout.whyDefault", "Already the default of the work view.", "Bereits der Standard der Arbeitsansicht."),
    };
}
