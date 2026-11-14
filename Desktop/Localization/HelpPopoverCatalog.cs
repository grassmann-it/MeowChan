namespace MeowChan.Desktop.Localization;

/// <summary>
/// Texts of the structured help popover (Controls/HelpButton.cs, HelpPopover.cs). A popover reads its text from the keys
/// <c>help.&lt;id&gt;.title</c>, <c>.p1</c>, <c>.p2</c> ... (paragraphs) and <c>.li1</c>, <c>.li2</c> ... (bullet points); the
/// button's accessible name is <c>help.button.name</c>. help.stats.* is the note of the Statistics page, split from stats.footnote
/// (which stays as the one-paragraph help text for assistive technology). Registered in BuiltInCatalog.
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
    };
}
