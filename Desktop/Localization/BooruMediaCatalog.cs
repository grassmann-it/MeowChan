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
        ("booru.media.statusCount", "{0} Danbooru posts downloaded", "{0} Danbooru-Beiträge heruntergeladen"),
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
        ("booru.media.gallery.scope", "Danbooru · {0} posts", "Danbooru · {0} Beiträge"),
        ("booru.media.gallery.tags", "{0} source tags from the provider", "{0} Quelltags vom Anbieter"),
        ("booru.media.tile.inPosts", "in {0} Danbooru posts", "in {0} Danbooru-Beiträgen"),
        ("booru.media.tile.inMixed", "in {0} threads and {1} Danbooru posts", "in {0} Threads und {1} Danbooru-Beiträgen"),
        ("booru.media.occurrences.summaryPosts", "{0} occurrences in {1} Danbooru posts. Tags stay with each occurrence.", "{0} Fundstellen in {1} Danbooru-Beiträgen. Tags bleiben bei jeder Fundstelle gespeichert."),
        ("booru.media.occurrences.summaryMixed", "{0} occurrences in {1} threads and {2} Danbooru posts. Tags stay with each occurrence.", "{0} Fundstellen in {1} Threads und {2} Danbooru-Beiträgen. Tags bleiben bei jeder Fundstelle gespeichert."),
        ("booru.media.tags.copyAll", "Copy all source tags", "Alle Quelltags kopieren"),
        ("booru.media.tags.copySelected", "Copy marked tags", "Markierte Tags kopieren"),
        ("booru.media.tags.copyCategory", "Copy", "Kopieren"),
        ("booru.media.tags.copyCategoryFor", "Copy tags of category {0}", "Tags der Kategorie {0} kopieren"),
        ("booru.media.tags.listFor", "Source tags: {0}", "Quelltags: {0}"),
        ("booru.media.tags.useAsSearch", "Use as Danbooru search", "Als Danbooru-Suche verwenden"),
        ("booru.media.tags.useAsSearch.tip", "Puts the marked tags (at most two) into the search field of the Danbooru window. If that window is not available, the search query is copied instead. Nothing is requested until you search there.", "Übernimmt die markierten Tags (höchstens zwei) in das Suchfeld des Danbooru-Fensters. Ist das Fenster nicht erreichbar, wird die Suchanfrage stattdessen kopiert. Erst wenn Sie dort suchen, wird etwas angefragt."),
        ("booru.media.tags.selection.none", "Mark tags in the lists to copy them, or up to two of them to use as a Danbooru search. Space marks a tag; the usual copy shortcut copies the marked tags.", "Tags in den Listen markieren, um sie zu kopieren oder bis zu zwei davon als Danbooru-Suche zu verwenden. Die Leertaste markiert einen Tag, der übliche Kopier-Kurzbefehl kopiert die markierten Tags."),
        ("booru.media.tags.selection.count", "{0} tags marked.", "{0} Tags markiert."),
        ("booru.media.tags.selection.tooMany", "{0} tags marked. A Danbooru search takes at most {1}: unmark some to use them as a search. Copying them as plain tags works with any number.", "{0} Tags markiert. Eine Danbooru-Suche nimmt höchstens {1}: einige Markierungen aufheben, um sie als Suche zu verwenden. Als einfache Tags lassen sich beliebig viele kopieren."),
        ("booru.media.tags.copied", "Copied {0} tags.", "{0} Tags kopiert."),
        ("booru.media.tags.copyFailed", "The tags could not be copied.", "Die Tags konnten nicht kopiert werden."),
        ("booru.media.tags.search.none", "Mark at least one tag first.", "Zuerst mindestens einen Tag markieren."),
        ("booru.media.tags.search.tooMany", "A Danbooru search takes at most {0} tags, but {1} are marked. Unmark some, or copy them as plain tags instead.", "Eine Danbooru-Suche nimmt höchstens {0} Tags, markiert sind aber {1}. Einige Markierungen aufheben oder die Tags stattdessen als einfache Tags kopieren."),
        ("booru.media.tags.search.unusable", "The tag {0} cannot be used in this Danbooru search, which supports plain tags only. Copy it as a plain tag instead.", "Der Tag {0} lässt sich in dieser Danbooru-Suche nicht verwenden; sie unterstützt nur einfache Tags. Stattdessen als einfachen Tag kopieren."),
        ("booru.media.tags.search.copied", "The Danbooru window is not available right now. The search query was copied instead: {0}", "Das Danbooru-Fenster ist gerade nicht erreichbar. Die Suchanfrage wurde stattdessen kopiert: {0}")
    };
}
