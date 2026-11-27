namespace MeowChan.Desktop.Localization;

/// <summary>Presentation-only Booru navigation labels. The toggle never changes source authorizations or work.</summary>
internal static class BooruPageCatalog
{
    internal static readonly (string Key, string English, string German)[] Entries =
    {
        ("booru.content.title", "Content rules", "Inhaltsregeln"),
        ("booru.content.prompt", "Choose the highest rating this profile can show and download.", "Wähle die höchste Einstufung, die dieses Profil anzeigen und herunterladen darf."),
        ("booru.content.general", "General only (g)", "Nur allgemein (g)"),
        ("booru.content.sensitive", "General and sensitive (g/s)", "Allgemein und sensibel (g/s)"),
        ("booru.content.questionable", "Up to questionable (g/s/q)", "Bis fragwürdig (g/s/q)"),
        ("booru.content.explicit", "All known ratings (g/s/q/e)", "Alle bekannten Einstufungen (g/s/q/e)"),
        ("booru.content.blacklist", "Tag blacklist, one rule per line", "Tag-Blacklist, eine Regel pro Zeile"),
        ("booru.content.save", "Save content rules", "Inhaltsregeln speichern"),
        ("booru.content.saveError", "The Booru content rules could not be saved.", "Die Booru-Inhaltsregeln konnten nicht gespeichert werden."),
        ("booru.content.invalidRule", "Check blacklist rule on line {0}.", "Prüfe die Blacklist-Regel in Zeile {0}."),
        ("booru.content.hidden", "{0} hidden", "{0} ausgeblendet"),
        ("nav.booru", "Booru", "Booru"),
        ("command.navigation.booru", "Booru", "Booru"),
        ("workview.page.booru", "Booru", "Booru"),
        ("workview.booru.show", "Show Booru", "Booru anzeigen"),
        ("workview.booru.showHint", "Show or hide Booru pages and actions. Sources, watchers and downloads keep running.",
            "Booru-Seite und -Aktionen anzeigen oder ausblenden. Quellen, Suchbeobachtungen und Downloads laufen weiter."),
    };
}
