namespace MeowChan.Desktop.Localization;

/// <summary>
/// EN/DE texts of the inline Danbooru gallery (the third result view) and of its settings. The sentences that carry a count are phrased as
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
        ("booru.gallery.sample.pixelLimit", "The larger version exceeds the limit of {0:N0} pixels; the preview stays.", "Die größere Fassung überschreitet die Grenze von {0:N0} Pixeln. Die Vorschau bleibt."),
        ("booru.gallery.sample.timeout", "The larger version did not arrive within {0} s; the preview stays.", "Die größere Fassung wurde nicht innerhalb von {0} s geliefert. Die Vorschau bleibt."),
        ("settings.limits.booruGalleryMaximumPixels", "Danbooru gallery: image pixel limit", "Danbooru-Galerie: maximale Pixelzahl"),
        ("settings.limits.booruGalleryMaximumPixels.hint", "Maximum total pixels in a reduced-size image before decoding.", "Höchste Gesamtzahl an Pixeln einer verkleinerten Fassung vor dem Dekodieren."),
        ("settings.limits.booruGalleryMaximumWidth", "Danbooru gallery: decoded image width (px)", "Danbooru-Galerie: dekodierte Bildbreite (px)"),
        ("settings.limits.booruGalleryMaximumWidth.hint", "The gallery scales wider reduced-size images down to this width.", "Die Galerie skaliert breitere verkleinerte Fassungen auf diese Breite."),
        ("settings.limits.booruGalleryTimeoutSeconds", "Danbooru gallery: response timeout (s)", "Danbooru-Galerie: Antwortfrist (s)"),
        ("settings.limits.booruGalleryTimeoutSeconds.hint", "How long the gallery waits for a reduced-size image from the source.", "So lange wartet die Galerie auf eine verkleinerte Fassung von der Quelle."),
    };
}
