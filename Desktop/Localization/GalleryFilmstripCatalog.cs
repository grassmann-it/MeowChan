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
        ("settings.unitDip", "Device-independent pixels (dip)", "Geräteunabhängige Pixel (dip)"),
        ("settings.galleryFilmstrip", "Show the filmstrip in the gallery", "Filmstreifen in der Galerie anzeigen"),
        ("settings.galleryFilmstripHint", "A row of thumbnails below the back and star buttons. Click a thumbnail to jump to that medium; the L key shows or hides the row. With hidden controls the row appears with them or when the pointer touches the top edge; with permanent controls it stays and moves the picture down.", "Eine Reihe von Vorschaubildern unter den Schaltflächen Zurück und Stern. Ein Klick auf ein Vorschaubild springt zu diesem Medium; die Taste L zeigt oder verbirgt die Reihe. Bei ausgeblendeter Steuerung erscheint sie mit der Steuerung oder wenn der Mauszeiger den oberen Rand berührt; bei dauerhafter Steuerung bleibt sie stehen und schiebt das Bild nach unten."),
        ("settings.galleryFilmstripHeight", "Filmstrip thumbnail height", "Höhe der Vorschaubilder im Filmstreifen"),
        ("settings.galleryFilmstripHeightHint", "Height and width of one thumbnail in the gallery filmstrip. Default {0}, from {1} to {2}.", "Höhe und Breite eines Vorschaubilds im Filmstreifen der Galerie. Standard {0}, von {1} bis {2}."),
        ("settings.galleryFilmstripPreload", "Filmstrip thumbnails prepared in advance", "Im Voraus vorbereitete Vorschaubilder"),
        ("settings.galleryFilmstripPreloadHint", "How many thumbnails on each side of the visible part of the filmstrip are decoded ahead of scrolling. More makes fast scrolling smoother and uses more memory. Default {0}, from {1} to {2}.", "Wie viele Vorschaubilder auf jeder Seite des sichtbaren Teils des Filmstreifens vor dem Scrollen schon dekodiert werden. Mehr macht schnelles Scrollen flüssiger und braucht mehr Speicher. Standard {0}, von {1} bis {2}."),
    };
}
