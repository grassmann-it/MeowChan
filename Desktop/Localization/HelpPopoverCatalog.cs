namespace MeowChan.Desktop.Localization;

/// <summary>
/// Texts of the structured help popover (Controls/HelpButton.cs, HelpPopover.cs). A popover reads its text from the keys
/// <c>help.&lt;id&gt;.title</c>, <c>.p1</c>, <c>.p2</c> ... (paragraphs) and <c>.li1</c>, <c>.li2</c> ... (bullet points); the
/// button's accessible name is <c>help.button.name</c>. help.stats.* is the note of the Statistics page, split from stats.footnote
/// (which stays as the one-paragraph help text for assistive technology). HB2 adds the notes of Rules, work views, storage limits,
/// statistics settings, board admission, plugins, tag suggestions, URL results and shortcut conflicts: a title and at most four short
/// lines each, and no sentence that the hint line of the same place already says. Registered in BuiltInCatalog.
/// </summary>
internal static class HelpPopoverCatalog
{
    internal static readonly (string Key, string English, string German)[] Entries =
    {
        ("help.button.name", "Help: {0}", "Hilfe: {0}"),
        ("help.popover.more", "More in Help", "Mehr in der Hilfe"),
        ("help.stats.title", "What is counted", "Was wird gezählt"),
        ("help.stats.p1", "Network counts the response data MeowChan receives, without headers or encryption.", "Netzwerk zählt die Antwortdaten, die MeowChan empfängt, ohne Kopfzeilen und Verschlüsselung."),
        ("help.stats.p2", "Upload counts the request payload MeowChan's application writes to the connection once a write has completed, without headers or encryption; the number of requests is shown as well.", "Upload zählt die Nutzdaten der Anfragen, die die Anwendung MeowChan nach abgeschlossenem Schreiben auf die Verbindung schreibt, ohne Kopfzeilen und Verschlüsselung; zusätzlich wird die Zahl der Anfragen gezeigt."),
        ("help.stats.p3", "Disk counts only the media files MeowChan itself writes or reads back, not thumbnails, previews, archive or index files.", "Datenträger zählt nur die Mediendateien, die MeowChan selbst schreibt oder zurückliest, keine Vorschaubilder, Vorschauen, Archiv- oder Indexdateien."),
        ("help.stats.p4", "Daily totals stay on this computer.", "Die Tageswerte bleiben auf diesem Rechner."),
        ("help.rules.title", "How rules apply", "So gelten Regeln"),
        ("help.rules.p1", "For each value, a download uses the most specific rule that sets it: thread, then board, then source, then global, then the built-in defaults.", "Für jeden Wert nimmt ein Download die genaueste Regel, die ihn festlegt: erst Thread, dann Board, dann Quelle, dann global, dann die Programmstandards."),
        ("help.rules.li1", "A rule can set just the file types and inherit the size limit; the line under a value shows where it comes from.", "Eine Regel kann nur die Dateitypen festlegen und die Größengrenze erben; die Zeile unter einem Wert zeigt, woher er stammt."),
        ("help.rules.li2", "Disabled rules, and values set to Inherit, pass the decision on.", "Deaktivierte Regeln und Werte auf „Vererben“ geben die Entscheidung weiter."),
        ("help.workviews.title", "Work views", "Arbeitsansichten"),
        ("help.workviews.p1", "No view removes anything: whatever is not shown directly is under More. Your data and settings stay the same, and you can switch at any time.", "Keine Ansicht entfernt etwas: Was nicht direkt sichtbar ist, steht unter Mehr. Deine Daten und Einstellungen bleiben gleich, und du kannst jederzeit wechseln."),
        ("help.workviews.item", "{0}: {1}", "{0}: {1}"),
        ("help.limits.title", "About these limits", "Zu diesen Grenzen"),
        ("help.limits.p1", "Limits protect disk space and memory. Each one has a default and an allowed range, shown with the field.", "Grenzen schützen Speicherplatz und Arbeitsspeicher. Jede hat einen Standardwert und einen erlaubten Bereich, der am Feld steht."),
        ("help.limits.li1", "Disk space: the free-space reserve and the caches for previews and indexes.", "Speicherplatz: die Platzreserve und die Zwischenspeicher für Vorschauen und Indizes."),
        ("help.limits.li2", "Local data: lists such as favorites and bookmarks, and the size of import and export batches.", "Lokale Daten: Listen wie Favoriten und Lesezeichen sowie die Größe von Import- und Exportstapeln."),
        ("help.statsSettings.title", "Statistics settings", "Statistik-Einstellungen"),
        ("help.statsSettings.li1", "Sampling and history: how fine and how long the live graph is.", "Messung und Verlauf: wie fein und wie lang die Live-Grafik ist."),
        ("help.statsSettings.li2", "Tray: the tooltip and menu lines are read when you point at or open them.", "Infobereich: Kurzinfo und Menüzeilen werden gelesen, wenn du darauf zeigst oder das Menü öffnest."),
        ("help.statsSettings.li3", "Daily totals: kept in your profile only while saving is on; retention and interval apply then.", "Tagessummen: bleiben nur bei eingeschaltetem Speichern im Profil; Aufbewahrung und Intervall gelten dann."),
        ("help.admissionWizard.title", "Preview when adding boards", "Vorschau beim Hinzufügen von Boards"),
        ("help.admissionWizard.p1", "Before a board is watched, MeowChan shows its current thread catalog so you choose what to follow. Only catalog text is loaded, no thread pages or media.", "Bevor ein Board beobachtet wird, zeigt MeowChan seinen aktuellen Thread-Katalog, damit du auswählst, was du verfolgen willst. Es wird nur Katalogtext geladen, keine Threadseiten und keine Medien."),
        ("help.admissionWizard.li1", "Text rules also apply to threads that appear later; threads you already watch keep running.", "Textregeln gelten auch für Threads, die später erscheinen; bereits beobachtete Threads laufen weiter."),
        ("help.admissionWizard.li2", "Turn this off to watch boards directly, without the preview.", "Ausschalten, um Boards direkt ohne Vorschau zu beobachten."),
        ("help.admissionRules.title", "How selection works", "So funktioniert die Auswahl"),
        ("help.admissionRules.p1", "Each thread is decided in this order; the first step that applies wins. Text rules search literal text and ignore case.", "Jeder Thread wird in dieser Reihenfolge entschieden; der erste passende Schritt gilt. Textregeln suchen wörtlich und ignorieren die Groß-/Kleinschreibung."),
        ("help.admissionRules.li1", "Your individual choice. Threads you removed earlier stay excluded until you select them again.", "Deine Einzelentscheidung. Früher entfernte Threads bleiben ausgeschlossen, bis du sie wieder auswählst."),
        ("help.admissionRules.li2", "Exclude rules, then include rules: one match is enough, and without include rules every thread qualifies.", "Ausschlussregeln, dann Einschlussregeln: ein Treffer genügt, und ohne Einschlussregeln sind alle Threads zugelassen."),
        ("help.plugins.title", "Plugins and permissions", "Plugins und Berechtigungen"),
        ("help.plugins.p1", "A plugin is code that runs on your computer. Only use plugins from sources you trust.", "Ein Plugin ist Code, der auf deinem Rechner läuft. Verwende nur Plugins aus Quellen, denen du vertraust."),
        ("help.plugins.li1", "Enabling and disabling takes effect at once; new or changed files and chosen builds apply at the next start.", "Aktivieren und Deaktivieren wirken sofort; neue oder geänderte Dateien und gewählte Fassungen gelten ab dem nächsten Start."),
        ("help.plugins.li2", "Image filters get access only for the source, board or rule you choose in Rules, never archive or write access; you can revoke it here.", "Bildfilter erhalten nur für die Quelle, das Board oder die Regel Zugriff, die du unter Regeln wählst, nie auf das Archiv oder zum Schreiben; hier kannst du ihn widerrufen."),
        ("help.tagSuggestions.title", "Tag suggestions", "Tagvorschläge"),
        ("help.tagSuggestions.p1", "Suggestions are ranked by how likely you want them; point at one to see why it is offered.", "Vorschläge sind danach geordnet, wie wahrscheinlich du sie willst; zeige auf einen, um zu sehen, warum er angeboten wird."),
        ("help.tagSuggestions.li1", "Press the number or letter shown on a suggestion to add or remove it. With an empty field, Down moves to the suggestions.", "Drücke die Ziffer oder den Buchstaben an einem Vorschlag, um ihn zu setzen oder zu entfernen. Bei leerem Feld führt Pfeil runter zu den Vorschlägen."),
        ("help.tagSuggestions.li2", "A name that does not exist yet is created as an own tag.", "Ein Name, den es noch nicht gibt, wird als eigener Tag angelegt."),
        ("help.admissionResults.title", "About these results", "Zu diesen Ergebnissen"),
        ("help.admissionResults.p1", "Every URL you add gets exactly one result.", "Jede URL, die du hinzufügst, erhält genau ein Ergebnis."),
        ("help.admissionResults.li1", "Added: it is watched now. Already watched: it was in the list before and nothing changed.", "Hinzugefügt: wird jetzt beobachtet. Wird bereits beobachtet: war schon in der Liste, nichts hat sich geändert."),
        ("help.admissionResults.li2", "Rejected: the address is invalid or unsupported, or it could not be added. Show only the problems to correct them.", "Abgelehnt: Die Adresse ist ungültig oder nicht unterstützt oder konnte nicht hinzugefügt werden. Zeige nur die Probleme, um sie zu korrigieren."),
        ("help.shortcuts.title", "Shortcut conflicts", "Kürzelkonflikte"),
        ("help.shortcuts.p1", "Two actions conflict when they can be active at the same time and use the same key combination. Both then stay switched off until you change one of them.", "Zwei Aktionen kollidieren, wenn sie gleichzeitig verfügbar sein können und dieselbe Tastenkombination nutzen. Beide bleiben dann ausgeschaltet, bis du eine davon änderst."),
        ("help.shortcuts.li1", "Take over gives the combination to the action you are editing and removes it from the other.", "Übernehmen gibt die Kombination der Aktion, die du bearbeitest, und nimmt sie der anderen."),
        ("help.shortcuts.li2", "A new default key never replaces a key you saved yourself.", "Eine neue Standardtaste ersetzt nie eine Taste, die du selbst gespeichert hast."),
    };
}
