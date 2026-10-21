namespace MeowChan.Desktop.Localization;
internal static class BoardAdmissionCatalog
{
    internal static readonly (string Key, string English, string German)[] Entries =
    {
        ("settings.boardAdmissionWizard", "Preview and select threads when adding boards", "Beim Hinzufügen von Boards Threads vorab auswählen"),
        ("wizard.title", "Select board threads", "Board-Threads auswählen"),
        ("wizard.intro", "Preview the current catalog before monitoring starts. Rules also apply to future discoveries. Existing watches continue; remove those separately in Downloads. Only catalog text is loaded; no thread pages or media are downloaded.", "Aktuellen Katalog vor Beginn der Beobachtung prüfen. Regeln gelten auch für künftig entdeckte Threads. Bestehende Watches laufen weiter; entferne diese separat unter Downloads. Es wird nur Katalogtext geladen, keine Threadseiten oder Medien."),
        ("wizard.semantics", "Contains matches literal text, ignoring case. Include rules: any match is enough; without includes, all qualify. Exclude rules win. Individual choices override rules. Previously removed threads stay excluded until you select them explicitly.", "Enthält sucht wörtlich ohne Groß-/Kleinschreibung. Bei Einschlussregeln genügt ein Treffer; ohne solche Regeln sind alle zugelassen. Ausschlussregeln haben Vorrang. Einzelentscheidungen überschreiben Regeln. Entfernte Threads bleiben ausgeschlossen, bis du sie ausdrücklich auswählst."),
        ("wizard.loading", "Loading catalog…", "Katalog wird geladen…"),
        ("wizard.failed", "Catalog could not be loaded: {0}", "Katalog konnte nicht geladen werden: {0}"),
        ("wizard.retry", "Retry catalog", "Katalog erneut laden"),
        ("wizard.ruleAdd", "Add text rule", "Textregel hinzufügen"),
        ("wizard.titleField", "Title", "Titel"),
        ("wizard.opField", "Opening post", "Eröffnungsbeitrag"),
        ("wizard.bothField", "Title or opening post", "Titel oder Eröffnungsbeitrag"),
        ("wizard.include", "Include", "Einschließen"),
        ("wizard.exclude", "Exclude", "Ausschließen"),
        ("wizard.contains", "Contains text…", "Enthält Text…"),
        ("wizard.removeRule", "Remove rule", "Regel entfernen"),
        ("wizard.summary", "Adopt: {0} · Exclude: {1}", "Übernehmen: {0} · Ausschließen: {1}"),
        ("wizard.matches", "Text-rule matches: {0} · Previously excluded: {1}. Matching threads appear first; select excluded threads to include them again.", "Textregel-Treffer: {0} · Zuvor ausgeschlossen: {1}. Passende Threads stehen zuerst; ausgeschlossene Threads können wieder aufgenommen werden."),
        ("wizard.reinclude", "Include these {0} previously excluded matches", "Diese {0} zuvor ausgeschlossenen Treffer wieder aufnehmen"),
        ("wizard.empty", "No threads in the current catalog. Saved rules will apply to future discoveries.", "Keine Threads im aktuellen Katalog. Gespeicherte Regeln gelten für künftige Entdeckungen."),
        ("wizard.invalid", "Enter search text for every rule, or remove the empty rule.", "Für jede Regel Suchtext eingeben oder die leere Regel entfernen."),
        ("wizard.apply", "Apply selection and monitor", "Auswahl übernehmen und beobachten"),
        ("wizard.all", "Monitor all", "Alle beobachten"),
        ("wizard.allHint", "Monitor all clears text rules for these boards. Existing individual exclusions and removed threads remain excluded.", "Alle beobachten entfernt Textregeln dieser Boards. Bestehende Einzelausschlüsse und entfernte Threads bleiben ausgeschlossen."),
        ("wizard.directFuture", "In future, monitor boards directly without this wizard", "Boards künftig direkt ohne diesen Assistenten beobachten"),
        ("wizard.cancel", "Cancel", "Abbrechen"),
        ("wizard.reason.suppressed", "Previously removed — select to reactivate", "Zuvor entfernt – zum Reaktivieren auswählen"),
        ("wizard.reason.manual", "Individual choice", "Einzelentscheidung"),
        ("wizard.reason.excludeRule", "Excluded by text rule", "Durch Textregel ausgeschlossen"),
        ("wizard.reason.noInclude", "No include rule matches", "Keine Einschlussregel trifft zu"),
        ("wizard.reason.includeRule", "Include rule matches", "Einschlussregel trifft zu"),
        ("wizard.reason.all", "Included by default", "Standardmäßig eingeschlossen"),
        ("wizard.resetChoice", "Use rules again", "Wieder Regeln verwenden"),
        ("wizard.manualWatch", "Manually watched — continues", "Manuell beobachtet – läuft weiter"),
        ("wizard.existingWatch", "Already watched — continues", "Bereits beobachtet – läuft weiter"),
        ("wizard.edit", "Thread filters and exclusions…", "Threadfilter und Ausschlüsse…")
    };
}
