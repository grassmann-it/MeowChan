namespace MeowChan.Desktop.Localization;

// Texts of the footer group on the settings page (F1, Settings › Appearance › Footer): the heading, the explanation, the Booru hint, the check
// box of a page, the move buttons, "Reset to default" and the announcements. Prefix reserved for this catalog: settings.footer.*. Counts and page
// names stand in placeholders, so no language needs plural variants.
internal static class FooterSettingsCatalog
{
    internal static readonly (string Key, string English, string German)[] Entries =
    {
        ("settings.footer.title", "Footer", "Fußleiste"),
        ("settings.footer.help", "Choose which pages have a button of their own in the footer and in which order. A page you hide stays reachable through More, the ⋯ menu and its keyboard shortcut. Ctrl/Cmd+1 to 9 count the visible footer buttons from the left. A work view can still move more pages under More.", "Lege fest, welche Seiten einen eigenen Knopf in der Fußleiste haben und in welcher Reihenfolge. Eine ausgeblendete Seite bleibt über Mehr, das ⋯-Menü und ihr Tastenkürzel erreichbar. Strg/Cmd+1 bis 9 zählen die sichtbaren Fußleisten-Knöpfe von links. Eine Arbeitsansicht kann weitere Seiten unter Mehr einsortieren."),
        ("settings.footer.booruHint", "Booru is not in the footer by default. \"Show Booru actions\" below stays the master: while it is off, the Booru page and the Danbooru tools are not available, whatever this list says.", "Booru steht standardmäßig nicht in der Fußleiste. \"Booru-Aktionen anzeigen\" weiter unten bleibt maßgeblich: Solange es aus ist, sind die Booru-Seite und die Danbooru-Werkzeuge nicht verfügbar, was auch immer diese Liste sagt."),
        ("settings.footer.booruActions", "Show Booru actions", "Booru-Aktionen anzeigen"),
        ("settings.footer.booruActions.hint", "Shows or hides the Booru page and the Danbooru tools in the ⋯ menu. Switching it off does not stop sources, watchers or downloads; it only hides the Booru surfaces.", "Zeigt oder verbirgt die Booru-Seite und die Danbooru-Werkzeuge im ⋯-Menü. Ausschalten hält weder Quellen, Beobachtungen noch Downloads an; es verbirgt nur die Booru-Oberflächen."),
        ("settings.footer.show", "show directly in the footer", "direkt in der Fußleiste zeigen"),
        ("settings.footer.always", "always shown in the footer", "immer in der Fußleiste sichtbar"),
        ("settings.footer.moveUp", "Move {0} up", "{0} nach oben"),
        ("settings.footer.moveDown", "Move {0} down", "{0} nach unten"),
        ("settings.footer.reset", "Reset to default", "Auf Standard zurücksetzen"),
        ("settings.footer.position", "{0}: position {1} of {2}", "{0}: Position {1} von {2}"),
        ("settings.footer.shortcut", "{0} shortcut: {1}", "Tastenkürzel {0}: {1}"),
    };
}
