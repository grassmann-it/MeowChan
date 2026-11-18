namespace MeowChan.Desktop.Localization;

/// <summary>Presentation-only Booru navigation labels. The toggle never changes source authorizations or work.</summary>
internal static class BooruPageCatalog
{
    internal static readonly (string Key, string English, string German)[] Entries =
    {
        ("nav.booru", "Booru", "Booru"),
        ("command.navigation.booru", "Booru", "Booru"),
        ("workview.page.booru", "Booru", "Booru"),
        ("workview.booru.show", "Show Booru", "Booru anzeigen"),
        ("workview.booru.showHint", "Show or hide Booru pages and actions. Sources, watchers and downloads keep running.",
            "Booru-Seite und -Aktionen anzeigen oder ausblenden. Quellen, Suchbeobachtungen und Downloads laufen weiter."),
    };
}
