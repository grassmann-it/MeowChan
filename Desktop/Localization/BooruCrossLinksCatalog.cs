namespace MeowChan.Desktop.Localization;

/// <summary>
/// EN/DE texts owned by the Booru ↔ media cross-link slice. The parent registers this catalog in
/// <see cref="BuiltInCatalog"/> together with the coordinated language-pack changes.
/// </summary>
internal static class BooruCrossLinksCatalog
{
    internal static readonly (string Key, string English, string German)[] Entries =
    {
        ("booru.ui.result.showInMedia", "Show in Media", "In Medien zeigen"),
        ("booru.ui.result.showInMediaFor", "Show saved post #{0} in Media", "Gespeicherten Post #{0} in Medien zeigen"),
        ("booru.ui.watcher.showInMediaFor", "Show saved post #{0} from watcher “{1}” in Media", "Gespeicherten Post #{0} aus der Suchbeobachtung „{1}“ in Medien zeigen"),
        ("booru.crossLinks.missingLocal", "The saved original for post #{0} is no longer available in the local media library.", "Das gespeicherte Original von Post #{0} ist in der lokalen Medienbibliothek nicht mehr verfügbar."),
        ("booru.crossLinks.navigationFailed", "Could not show post #{0} in Media. Try again.", "Post #{0} konnte nicht in Medien angezeigt werden. Versuche es erneut."),
        ("media.toolbar.booru", "Open Booru", "Booru öffnen"),
        ("media.toolbar.booruTip", "Open Danbooru without changing the media library or sending a request.", "Danbooru öffnen, ohne die Medienbibliothek zu ändern oder eine Anfrage zu senden."),
        ("media.menu.md5", "Search by MD5", "Per MD5 suchen"),
        ("media.menu.md5.source", "Search {0} by MD5", "Bei {0} per MD5 suchen"),
        ("booru.md5.batch.title", "Danbooru MD5 batch lookup", "MD5-Stapelabgleich mit Danbooru"),
        ("booru.md5.batch.selected", "Check selected media on Danbooru…", "Ausgewählte Medien mit Danbooru abgleichen…"),
        ("booru.md5.batch.all", "Check all local media on Danbooru…", "Alle lokalen Medien mit Danbooru abgleichen…"),
        ("booru.md5.batch.optIn", "Allow this profile to send local media MD5 hashes to Danbooru in batches", "Diesem Profil erlauben, MD5-Prüfsummen lokaler Medien im Stapel an Danbooru zu senden"),
        ("booru.md5.batch.contentRequired", "Choose a content limit in Booru before starting the batch.", "Lege vor dem Stapelabgleich in Booru eine Inhaltsgrenze fest."),
        ("booru.md5.batch.start", "Start lookup", "Abgleich starten"),
        ("booru.md5.batch.resume", "Resume lookup", "Abgleich fortsetzen"),
        ("booru.md5.batch.stop", "Pause lookup", "Abgleich pausieren"),
        ("booru.md5.batch.progress", "Checked {0} of {1} · {2} matches · {3} without a checksum", "{0} von {1} geprüft · {2} Fundstellen · {3} ohne Prüfsumme"),
        ("booru.md5.batch.matches", "Matching Danbooru posts", "Passende Danbooru-Posts"),
        ("booru.md5.batch.saveFailed", "Could not save this profile’s permission.", "Die Freigabe für dieses Profil konnte nicht gespeichert werden."),
        ("booru.md5.batch.finished", "Lookup complete.", "Abgleich abgeschlossen."),
        ("booru.md5.batch.interrupted", "Lookup interrupted. You can resume the batch.", "Abgleich unterbrochen. Du kannst ihn fortsetzen."),
        ("booru.md5.batch.paused", "Lookup paused.", "Abgleich pausiert."),
    };
}
