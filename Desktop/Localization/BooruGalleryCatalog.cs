namespace MeowChan.Desktop.Localization;

/// <summary>
/// EN/DE texts of the inline Danbooru gallery (the third result view) and of its two limits. The sentences that carry a count are phrased as
/// labels with a number ("{0} of {1}", "{0} MiB"), so no plural forms are needed.
/// </summary>
internal static class BooruGalleryCatalog
{
    internal static readonly (string Key, string English, string German)[] Entries =
    {
        ("booru.gallery.name", "Gallery", "Galerie"),
        ("booru.gallery.hint", "Show the results of this page one large image at a time. Esc goes back to the list or grid.", "Zeigt die Treffer dieser Seite als einzelnes großes Bild. Mit Esc geht es zurück zu Liste oder Raster."),
        ("booru.gallery.listName", "Gallery of the results on this page", "Galerie der Treffer dieser Seite"),
        ("booru.gallery.position", "{0} of {1}", "{0} von {1}"),
        ("booru.gallery.shown.preview", "Shown: preview", "Angezeigt: Vorschau"),
        ("booru.gallery.shown.sample", "Shown: reduced-size version (sample)", "Angezeigt: verkleinerte Fassung (Sample)"),
        ("booru.gallery.sample.loading", "Larger version loading…", "Größere Fassung wird geladen…"),
        ("booru.gallery.sample.none", "The source offers no reduced-size version, so the preview stays.", "Die Quelle bietet keine verkleinerte Fassung an, daher bleibt die Vorschau."),
        ("booru.gallery.sample.failed", "The larger version could not be loaded (not delivered, or larger than {0} MiB); the preview stays.", "Die größere Fassung konnte nicht geladen werden (nicht geliefert oder größer als {0} MiB); die Vorschau bleibt."),
        ("booru.gallery.openWindow", "Open in new window", "In neuem Fenster öffnen"),
        ("settings.limits.booruGallerySampleMiB", "Danbooru gallery: largest image (MiB)", "Danbooru-Galerie: größtes Bild (MiB)"),
        ("settings.limits.booruGallerySampleMiB.hint", "Largest reduced-size image the gallery loads for display. A larger one is not loaded and the preview stays. The original is never loaded for display.", "Größtes verkleinertes Bild, das die Galerie zur Anzeige lädt. Ein größeres wird nicht geladen, die Vorschau bleibt. Das Original wird nie zur Anzeige geladen."),
        ("settings.limits.booruGalleryCacheMiB", "Danbooru gallery: image memory (MiB)", "Danbooru-Galerie: Bildspeicher (MiB)"),
        ("settings.limits.booruGalleryCacheMiB.hint", "How many MiB of loaded gallery images are kept in memory so paging back does not load them again. Nothing is stored on disk.", "So viele MiB geladener Galeriebilder bleiben im Arbeitsspeicher, damit das Zurückblättern sie nicht erneut lädt. Nichts wird auf der Festplatte gespeichert."),
    };
}
