namespace MeowChan.Desktop.Localization;

// Texts of the gallery marking (PLAN-SCHNELL-TAGGEN §1.4, decision E4, TP4): the "n marked" chip with its clear button, the counter
// suffix "· marked" and the sentences the polite status line says when a medium is marked, unmarked, refused or the marks are cleared.
// Counts are numbers in a fixed phrase ("Marked in all: 3"), so no language needs plural forms for them.
internal static class GalleryMarkCatalog
{
    internal static readonly (string Key, string English, string German)[] Entries =
    {
        ("tags.quick.marked", "{0} marked", "{0} markiert"),
        ("tags.quick.marked.clear", "Clear marks", "Markierungen löschen"),
        ("tags.quick.marked.clearName", "Clear all marks", "Alle Markierungen löschen"),
        ("tags.quick.marked.counter", "{0} · marked", "{0} · markiert"),
        ("tags.quick.marked.announce.added", "Marked “{0}”. Marked in all: {1}.", "„{0}“ markiert. Insgesamt markiert: {1}."),
        ("tags.quick.marked.announce.removed", "Mark removed from “{0}”. Marked in all: {1}.", "Markierung von „{0}“ entfernt. Insgesamt markiert: {1}."),
        ("tags.quick.marked.announce.already", "“{0}” is already marked. Marked in all: {1}.", "„{0}“ ist schon markiert. Insgesamt markiert: {1}."),
        ("tags.quick.marked.announce.limit", "Marking limit reached ({0}). Clear marks or raise the limit in the settings.", "Markierungslimit erreicht ({0}). Markierungen löschen oder das Limit in den Einstellungen erhöhen."),
        ("tags.quick.marked.announce.unavailable", "Not marked: “{0}”. {1}", "Nicht markiert: „{0}“. {1}"),
        ("tags.quick.marked.announce.cleared", "All marks cleared.", "Alle Markierungen gelöscht."),
    };
}
