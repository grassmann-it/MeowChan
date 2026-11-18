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
    };
}
