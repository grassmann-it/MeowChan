namespace MeowChan.Desktop.Localization;

// QW-A (review of 15.11.2026, items 1, 7, 10, 13): texts of the ways into things that used to hide: the empty Media page's folder flow and the
// Media "More" entries, "Show in Media" of the thread menus, "Post bookmarks…" of the reader's Thread menu, the Continue reading button's two
// click actions, and the three labelled groups of the header menu. No arrow or glyph characters in the texts (the arrow is a drawn icon).
internal static class ShellReachabilityCatalog
{
    internal static readonly (string Key, string English, string German)[] Entries =
    {
        ("command.thread.show-in-media", "Show in Media", "In Medien zeigen"),
        ("command.media.link-folder", "Link folder…", "Ordner verknüpfen…"),
        ("command.media.folders", "Media folders…", "Medienordner…"),
        ("media.empty.copyOrLink", "Copy files into your collection, or link a folder: its files stay where they are and are only read.", "Kopiere Dateien in deine Sammlung oder verknüpfe einen Ordner: Seine Dateien bleiben, wo sie sind, und werden nur gelesen."),
        ("media.queueAll.hint", "Add every medium the filter shows to the tagging queue. Above the confirmation limit you are asked first.", "Alle Medien, die der Filter zeigt, der Tagging-Warteschlange hinzufügen. Über der Bestätigungsgrenze wirst du vorher gefragt."),
        ("media.queueAll.reason.none", "No tiles are shown, so there is nothing to add.", "Es sind keine Kacheln zu sehen, also gibt es nichts hinzuzufügen."),
        ("reader.thread.postBookmarks", "Post bookmarks…", "Post-Lesezeichen…"),
        ("resume.tip.jump", "Click: continue at the last reading place. Right-click, the menu key or the arrow button: reading history.", "Klick: an der letzten Lesestelle weiterlesen. Rechtsklick, Menütaste oder Pfeilknopf: Leseverlauf."),
        ("resume.tip.history", "Click: show the reading history, because you are already reading a thread. Right-click, the menu key and the arrow button open the same list.", "Klick: den Leseverlauf zeigen, weil du schon einen Thread liest. Rechtsklick, Menütaste und Pfeilknopf öffnen dieselbe Liste."),
        ("resume.history.open", "Show reading history", "Leseverlauf zeigen"),
        ("nav.more.currentPage", "More pages · Current page: {0}", "Weitere Seiten · Aktuell: {0}"),
        ("menu.group.read", "Reading", "Lesen"),
        ("menu.group.collection", "Collection", "Sammlung"),
        ("menu.group.app", "App", "App"),
    };
}
