namespace MeowChan.Desktop.Localization;

/// <summary>Texts of the gallery filmstrip (V3): the strip's accessible names and the three settings that shape it.</summary>
internal static class GalleryFilmstripCatalog
{
    internal static readonly (string Key, string English, string German)[] Entries =
    {
        ("gallery.filmstrip.name", "Filmstrip, number of media: {0}", "Filmstreifen, Anzahl der Medien: {0}"),
        ("gallery.filmstrip.tile", "{0} of {1}, {2}, {3}", "{0} von {1}, {2}, {3}"),
        ("gallery.filmstrip.more", "More media are being loaded", "Weitere Medien werden geladen"),
        ("gallery.filmstrip.kind.image", "image", "Bild"),
        ("gallery.filmstrip.kind.video", "video", "Video"),
        ("gallery.filmstrip.kind.gif", "animated image", "Animiertes Bild"),
        ("gallery.filmstrip.scrollBack", "Scroll filmstrip back", "Filmstreifen zurückscrollen"),
        ("gallery.filmstrip.scrollForward", "Scroll filmstrip forward", "Filmstreifen vorwärtsscrollen"),
        ("gallery.filmstrip.dock.top", "Filmstrip position: above the picture, below the back button", "Position des Filmstreifens: über dem Bild, unter dem Zurück-Knopf"),
        ("gallery.filmstrip.dock.bottom", "Filmstrip position: below the picture, above the tools", "Position des Filmstreifens: unter dem Bild, über den Werkzeugen"),
        ("settings.unitDip", "Device-independent pixels (dip)", "Geräteunabhängige Pixel (dip)"),
        ("settings.galleryFilmstrip", "Show the filmstrip in the gallery", "Filmstreifen in der Galerie anzeigen"),
        ("settings.galleryFilmstripHint", "A row of thumbnails of the gallery's media. By default it sits at the bottom, above the video bar and the tools, so the picture keeps its height; it can also sit at the top, below the back and star buttons. Click a thumbnail to jump to that medium. With hidden controls the row appears with them (at the top also when the pointer touches the top edge); with permanent controls it stays (at the top it moves the picture down).", "Eine Reihe von Vorschaubildern der Medien der Galerie. Standardmäßig steht sie unten, über der Videoleiste und den Werkzeugen, damit das Bild seine Höhe behält; sie kann auch oben unter den Schaltflächen Zurück und Stern stehen. Ein Klick auf ein Vorschaubild springt zu diesem Medium. Bei ausgeblendeter Steuerung erscheint sie mit der Steuerung (oben auch, wenn der Mauszeiger den oberen Rand berührt); bei dauerhafter Steuerung bleibt sie stehen (oben schiebt sie das Bild nach unten)."),
        ("settings.galleryFilmstripKeyHint", "{0} shows or hides the row.", "{0} zeigt oder verbirgt die Reihe."),
        ("settings.galleryFilmstripHeight", "Filmstrip thumbnail height", "Höhe der Vorschaubilder im Filmstreifen"),
        ("settings.galleryFilmstripHeightHint", "Height and width of one thumbnail in the gallery filmstrip. Default {0}, from {1} to {2}.", "Höhe und Breite eines Vorschaubilds im Filmstreifen der Galerie. Standard {0}, von {1} bis {2}."),
        // GL5e: the gap between two tiles.
        ("settings.galleryFilmstripTileGap", "Gap between filmstrip thumbnails", "Abstand zwischen den Vorschaubildern"),
        ("settings.galleryFilmstripTileGapHint", "The space between two thumbnails of the gallery filmstrip. Default {0}, from {1} to {2}.", "Der Abstand zwischen zwei Vorschaubildern im Filmstreifen der Galerie. Standard {0}, von {1} bis {2}."),
        ("settings.galleryFilmstripPreload", "Filmstrip thumbnails prepared in advance", "Im Voraus vorbereitete Vorschaubilder"),
        ("settings.galleryFilmstripPreloadHint", "How many thumbnails on each side of the visible part of the filmstrip are decoded ahead of scrolling. More makes fast scrolling smoother and uses more memory. Default {0}, from {1} to {2}.", "Wie viele Vorschaubilder auf jeder Seite des sichtbaren Teils des Filmstreifens vor dem Scrollen schon dekodiert werden. Mehr macht schnelles Scrollen flüssiger und braucht mehr Speicher. Standard {0}, von {1} bis {2}."),
    };
}
