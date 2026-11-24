namespace MeowChan.Desktop.Localization;

// Texts of the footer group on the settings page (F1, Settings › Appearance › Footer): the heading, the explanation, the Booru hint, the check
// box of a page, the move buttons, "Reset to default" and the announcements. Prefix reserved for this catalog: settings.footer.*. Counts and page
// names stand in placeholders, so no language needs plural variants.
internal static class FooterSettingsCatalog
{
    internal static readonly (string Key, string English, string German)[] Entries =
    {
        ("settings.footer.title", "Footer", "Fußleiste"),
        ("settings.footer.displays", "Footer displays", "Fußleistenanzeigen"),
        ("settings.footer.statusLines", "Status lines", "Statuszeilen"),
        ("settings.footer.help", "Choose which pages have a button of their own in the footer and in which order. A page you hide stays reachable through More, the ⋯ menu and its keyboard shortcut. Ctrl/Cmd+1 to 9 count the footer buttons your work view shows, from the left; the pages under More follow. A work view can still move more pages under More.", "Lege fest, welche Seiten einen eigenen Knopf in der Fußleiste haben und in welcher Reihenfolge. Eine ausgeblendete Seite bleibt über Mehr, das ⋯-Menü und ihr Tastenkürzel erreichbar. Strg/Cmd+1 bis 9 zählen die Fußleisten-Knöpfe, die deine Arbeitsansicht zeigt, von links; die Seiten unter Mehr folgen danach. Eine Arbeitsansicht kann weitere Seiten unter Mehr einsortieren."),
        ("settings.footer.booruHint", "Booru is shown by default. A compact work view or narrow window can move it under More. To hide the page from navigation, turn off “Show Booru actions” below.", "Booru wird standardmäßig angezeigt. Eine kompakte Arbeitsansicht oder ein schmales Fenster kann die Seite unter „Mehr“ verschieben. Um die Seite aus der Navigation auszublenden, schalte unten „Booru-Aktionen anzeigen“ aus."),
        ("settings.footer.booruActions", "Show Booru actions", "Booru-Aktionen anzeigen"),
        ("settings.footer.booruActions.hint", "On by default. Turn off to hide the Booru page and Danbooru tools. The Danbooru source, watchers and downloads keep running.", "Standardmäßig an. Ausschalten blendet die Booru-Seite und die Danbooru-Werkzeuge aus. Die Danbooru-Quelle, Suchbeobachtungen und Downloads laufen weiter."),
        ("settings.footer.show", "show directly in the footer", "direkt in der Fußleiste zeigen"),
        ("settings.footer.always", "always shown in the footer", "immer in der Fußleiste sichtbar"),
        ("settings.footer.moveUp", "Move {0} up", "{0} nach oben"),
        ("settings.footer.moveDown", "Move {0} down", "{0} nach unten"),
        ("settings.footer.reset", "Reset footer", "Fußleiste zurücksetzen"),
        ("settings.footer.reset.hint", "Sets the footer buttons and their order back to the default.", "Setzt die Knöpfe der Fußleiste und ihre Reihenfolge auf den Standard zurück."),
        ("settings.footer.position", "{0}: position {1} of {2}", "{0}: Position {1} von {2}"),
        ("settings.footer.shortcut", "{0} shortcut: {1}", "Tastenkürzel für {0}: {1}"),
    };
}
