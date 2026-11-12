namespace MeowChan.Desktop.Localization;

// Texts for downloaded Danbooru media in the media page, the gallery and favorites: the source badge, the post
// details, the provider's rating and source tags (always apart from the user's own tags), finding labels and the
// statement that own tags are not available for provider media yet. Prefix reserved for this catalog:
// booru.media.*. The rating, tag category, media role and finding vocabulary comes from BooruStatusCatalog
// (booru.rating.*, booru.tagCategory.*, booru.mediaRole.*, booru.finding.*); search screens use booru.ui.*.
// The es/fr/ru pack entries are machine translations that no human has proofread.
internal static class BooruMediaCatalog
{
    internal static readonly (string Key, string English, string German)[] Entries =
    {
        ("booru.media.source", "Danbooru", "Danbooru"),
        ("booru.media.badge", "Danbooru", "Danbooru"),
        ("booru.media.tile.post", "Danbooru post #{0}", "Danbooru-Beitrag #{0}"),
        ("booru.media.statusCount", "{0} from Danbooru", "{0} von Danbooru"),
        ("booru.media.detail.title", "Danbooru post #{0}", "Danbooru-Beitrag #{0}"),
        ("booru.media.detail.post", "Post #{0} at the provider", "Beitrag #{0} beim Anbieter"),
        ("booru.media.openPost", "Open post at provider", "Beitrag beim Anbieter öffnen"),
        ("booru.media.copyPost", "Copy post link", "Beitragslink kopieren"),
        ("booru.media.copied", "Post link copied.", "Beitragslink kopiert."),
        ("booru.media.copyFailed", "The link could not be copied.", "Der Link konnte nicht kopiert werden."),
        ("booru.media.openFailed", "The post page could not be opened.", "Die Beitragsseite konnte nicht geöffnet werden."),
        ("booru.media.details.unavailable", "The provider details could not be read from the local catalog. The saved file is not affected.", "Die Anbieterdaten konnten nicht aus dem lokalen Katalog gelesen werden. Die gespeicherte Datei ist nicht betroffen."),
        ("booru.media.variants.heading", "Versions", "Fassungen"),
        ("booru.media.variant.Stored", "Saved on this computer", "Auf diesem Computer gespeichert"),
        ("booru.media.variant.LocalFileMissing", "Linked, but the saved file is not available right now", "Verknüpft, aber die gespeicherte Datei ist gerade nicht verfügbar"),
        ("booru.media.variant.NotDownloaded", "Offered by the provider, not downloaded", "Vom Anbieter angeboten, nicht heruntergeladen"),
        ("booru.media.variant.ProviderClaimOnly", "Listed by the provider, not saved by MeowChan", "Vom Anbieter aufgeführt, von MeowChan nicht gespeichert"),
        ("booru.media.variant.NotOffered", "Not offered by the provider", "Vom Anbieter nicht angeboten"),
        ("booru.media.variants.note", "MeowChan saves only the original and never replaces it with a smaller version.", "MeowChan speichert nur das Original und ersetzt es nie durch eine kleinere Fassung."),
        ("booru.media.provider.heading", "Provider data", "Anbieterdaten"),
        ("booru.media.provider.note", "Information from the provider itself. It is not a rating or a favorite of your own.", "Angaben des Anbieters selbst. Sie sind weder eine eigene Bewertung noch ein eigener Favorit."),
        ("booru.media.provider.score", "Score: {0}", "Punktzahl: {0}"),
        ("booru.media.provider.favorites", "Provider favorites: {0}", "Favoriten beim Anbieter: {0}"),
        ("booru.media.provider.created", "Posted at the provider: {0:g}", "Beim Anbieter veröffentlicht: {0:g}"),
        ("booru.media.provider.source", "Source stated by the provider: {0}", "Vom Anbieter genannte Quelle: {0}"),
        ("booru.media.provider.seen", "First seen {0:g} · last seen {1:g}", "Zuerst gesehen {0:g} · zuletzt gesehen {1:g}"),
        ("booru.media.findings.heading", "Found by", "Gefunden über"),
        ("booru.media.findings.none", "No search, watcher or single-post request is recorded.", "Keine Suche, kein Watcher und keine Einzelbeitrag-Anfrage ist aufgezeichnet."),
        ("booru.media.finding.watcherRemoved", "watcher removed", "Watcher entfernt"),
        ("booru.media.finding.watcherState", "watcher: {0}", "Watcher: {0}"),
        ("booru.media.findings.note", "Removing a watcher or the provider does not remove saved media or this information.", "Das Entfernen eines Watchers oder des Anbieters entfernt weder gespeicherte Medien noch diese Angaben."),
        ("booru.media.sourceTags.note", "Read-only data from the provider. These are not your own tags and are never saved as your tags.", "Nur lesbare Angaben des Anbieters. Das sind keine eigenen Tags und sie werden nie als eigene Tags gespeichert."),
        ("booru.media.tags.none", "The provider lists no tags for this post.", "Der Anbieter nennt für diesen Beitrag keine Tags."),
        ("booru.media.ownTags.heading", "Own tags", "Eigene Tags"),
        ("booru.media.ownTags.unavailable", "Own tags cannot be added to downloaded provider media yet.", "Eigene Tags lassen sich für heruntergeladene Anbieter-Medien noch nicht vergeben."),
        ("booru.media.tags.unavailable", "Own tags are not available for downloaded provider media yet.", "Eigene Tags sind für heruntergeladene Anbieter-Medien noch nicht verfügbar."),
        ("booru.media.occurrence.findings", "Found by: {0}", "Gefunden über: {0}"),
        ("booru.media.gallery.tags", "{0} source tags from the provider", "{0} Quelltags vom Anbieter")
    };
}
