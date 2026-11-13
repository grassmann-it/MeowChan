namespace MeowChan.Desktop.Localization;

/// <summary>
/// Texts of the statistics settings (block "Statistics" on the Behavior page): label and hint of each row, the units and
/// the reason shown while a row is unavailable. English and German are built in; Spanish, French and Russian are in the
/// bundled packs. Every limit's hint states its range.
/// </summary>
internal static class IoStatsSettingsTexts
{
    internal static readonly (string Key, string English, string German)[] Entries =
    [
        ("stats.settings.title", "Statistics", "Statistik"),
        ("stats.settings.intro", "MeowChan counts the network and disk traffic of its own media (downloads, imports, integrity reads) itself. Thumbnails and archive files are not counted. Everything stays in your profile on this computer.", "MeowChan zählt den Netzwerk- und Datenträgerverkehr seiner eigenen Medien (Downloads, Importe, Prüf-Lesevorgänge) selbst. Vorschaubilder und Archivdateien werden nicht mitgezählt. Alles bleibt in deinem Profil auf diesem Rechner."),
        ("stats.settings.sampleInterval", "Statistics update interval", "Aktualisierungstakt der Statistik"),
        ("stats.settings.sampleIntervalHint", "How often the network and disk counters are read while the MeowChan window is visible. A shorter interval makes the graph react faster and uses slightly more processor time; 1000 is one reading per second. Allowed: {0} to {1} milliseconds.", "Wie oft die Netzwerk- und Datenträgerzähler gelesen werden, solange das MeowChan-Fenster sichtbar ist. Ein kürzerer Takt lässt den Verlauf schneller reagieren und braucht etwas mehr Rechenzeit; 1000 ist eine Messung pro Sekunde. Erlaubt: {0} bis {1} Millisekunden."),
        ("stats.settings.historySamples", "Graph length", "Länge des Verlaufs"),
        ("stats.settings.historySamplesHint", "How many readings the graph keeps, which is also its width. The time span shown is this number times the update interval above (120 readings at 1000 milliseconds are 2 minutes). Lowering it drops only the oldest readings; the newest stay. Allowed: {0} to {1} readings.", "Wie viele Messwerte der Verlauf behält; das ist auch seine Breite. Die gezeigte Zeitspanne ist diese Zahl mal der Aktualisierungstakt oben (120 Messwerte bei 1000 Millisekunden sind 2 Minuten). Wird sie verkleinert, entfallen nur die ältesten Werte, die neuesten bleiben. Erlaubt: {0} bis {1} Messwerte."),
        ("stats.settings.backgroundInterval", "Update interval in the tray", "Aktualisierungstakt im Infobereich"),
        ("stats.settings.backgroundIntervalHint", "How often the counters are read while the window is hidden, downloads are running and the tray tooltip shows statistics. While the window is hidden and nothing is downloading, nothing is read at all. Allowed: {0} to {1} milliseconds.", "Wie oft die Zähler gelesen werden, solange das Fenster versteckt ist, Downloads laufen und der Tooltip des Infobereichs Statistik zeigt. Ist das Fenster versteckt und lädt nichts, wird gar nichts gelesen. Erlaubt: {0} bis {1} Millisekunden."),
        ("stats.settings.trayTooltip", "Show statistics in the tray tooltip", "Statistik im Tooltip des Infobereichs zeigen"),
        ("stats.settings.trayTooltipHint", "The tooltip of the tray icon shows the current rates and the number of running downloads. It is only refreshed while downloads are running.", "Der Tooltip des Infobereich-Symbols zeigt die aktuellen Raten und die Zahl der laufenden Downloads. Er wird nur aktualisiert, solange Downloads laufen."),
        ("stats.settings.trayMenu", "Show statistics lines in the tray menu", "Statistikzeilen im Menü des Infobereichs zeigen"),
        ("stats.settings.trayMenuHint", "The tray menu starts with two lines: the current rates and the running downloads with today’s total. They are read when the menu opens.", "Das Menü des Infobereichs beginnt mit zwei Zeilen: den aktuellen Raten und den laufenden Downloads mit der Tagessumme. Sie werden beim Öffnen des Menüs gelesen."),
        ("stats.settings.persist", "Keep statistics on this computer", "Statistik auf diesem Rechner speichern"),
        ("stats.settings.persistHint", "Keeps the daily totals in your profile (io-stats.json), so “today” and the longer totals survive a restart. When this is off, the totals exist only until you close MeowChan; an existing file stays untouched.", "Behält die Tagessummen in deinem Profil (io-stats.json), damit „Heute“ und die längeren Summen einen Neustart überstehen. Ist das ausgeschaltet, gelten die Summen nur, bis du MeowChan schließt; eine vorhandene Datei bleibt unangetastet."),
        ("stats.settings.retentionDays", "Days of statistics kept", "Aufbewahrte Tage der Statistik"),
        ("stats.settings.retentionDaysHint", "How many days of totals are kept, today included. Older days are dropped when the statistics load and at midnight. Allowed: {0} to {1} days.", "Wie viele Tage an Summen aufbewahrt werden, heute eingeschlossen. Ältere Tage entfallen beim Laden der Statistik und um Mitternacht. Erlaubt: {0} bis {1} Tage."),
        ("stats.settings.persistInterval", "Statistics saving interval", "Speicherintervall der Statistik"),
        ("stats.settings.persistIntervalHint", "How often changed totals are written to disk, in minutes. They are also written when you close MeowChan. Nothing is written while nothing has changed. Allowed: {0} to {1} minutes.", "Wie oft geänderte Summen auf den Datenträger geschrieben werden, in Minuten. Sie werden auch beim Schließen von MeowChan geschrieben. Solange sich nichts geändert hat, wird nichts geschrieben. Erlaubt: {0} bis {1} Minuten."),
        ("stats.settings.maxSources", "Most sources in the statistics", "Höchstzahl der Quellen in der Statistik"),
        ("stats.settings.maxSourcesHint", "How many sources get their own row in the statistics and in each saved day. Further sources are added up in one row called Other. Allowed: {0} to {1} sources.", "Wie viele Quellen eine eigene Zeile in der Statistik und in jedem gespeicherten Tag bekommen. Weitere Quellen werden in einer Zeile „Sonstige“ zusammengezählt. Erlaubt: {0} bis {1} Quellen."),
        ("stats.settings.unitMs", "milliseconds", "Millisekunden"),
        ("stats.settings.unitDays", "days", "Tage"),
        ("stats.settings.unitReadings", "readings", "Messwerte"),
        ("stats.settings.unitSources", "sources", "Quellen"),
        ("stats.settings.needsOption", "Not available while “{0}” is turned off.", "Nicht verfügbar, solange „{0}“ ausgeschaltet ist."),
    ];
}
