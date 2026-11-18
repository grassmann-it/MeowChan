namespace MeowChan.Desktop.Localization;

/// <summary>Consent-dialog entries included in the shared built-in and bundled-pack catalogs.</summary>
internal static class PluginChangeReviewCatalog
{
    internal static readonly (string Key, string English, string German)[] Entries =
    {
        ("plugins.changeReview.title", "Review changed plugin", "Geändertes Plugin prüfen"),
        ("plugins.changeReview.explanation", "This update changes the plugin’s capabilities or settings. Review the changes and correct any settings here. Saving applies only after restarting the app.", "Dieses Update ändert Fähigkeiten oder Einstellungen des Plugins. Prüfe die Änderungen und korrigiere die Einstellungen direkt hier. Gespeicherte Änderungen gelten erst nach einem Neustart der App."),
        ("plugins.changeReview.plugin", "Plugin: {0}", "Plugin: {0}"),
        ("plugins.changeReview.capabilities", "Changed capabilities", "Geänderte Fähigkeiten"),
        ("plugins.changeReview.old", "Before", "Vorher"),
        ("plugins.changeReview.new", "After", "Nachher"),
        ("plugins.changeReview.noCapabilities", "No source capability is declared.", "Es ist keine Quellenfähigkeit deklariert."),
        ("plugins.changeReview.sourceCapabilities", "{0}: {1}", "{0}: {1}"),
        ("plugins.changeReview.settings", "Settings for next start", "Einstellungen für nächsten Start"),
        ("plugins.changeReview.settingsHelp", "Your existing settings are prefilled where possible. Correct invalid values here. Removed settings are retained with the old plugin version; their removal from the new version requires your acknowledgement.", "Deine bisherigen Einstellungen sind soweit möglich vorausgefüllt. Korrigiere ungültige Werte direkt hier. Entfernte Einstellungen bleiben in der alten Pluginfassung erhalten; ihre Entfernung aus der neuen Fassung musst du bestätigen."),
        ("plugins.changeReview.oldSettings", "Previous settings (read-only)", "Bisherige Einstellungen (nur lesen)"),
        ("plugins.changeReview.newSettings", "New settings (editable)", "Neue Einstellungen (bearbeitbar)"),
        ("plugins.changeReview.noSettings", "No plugin or source settings are declared.", "Es sind keine Plugin- oder Quelleinstellungen deklariert."),
        ("plugins.changeReview.rights", "Existing rights that would apply to the new build", "Vorhandene Rechte, die für den neuen Build gelten würden"),
        ("plugins.changeReview.rightsNone", "No existing special rights would be carried into this review.", "In diese Prüfung würden keine vorhandenen Sonderrechte übernommen."),
        ("plugins.changeReview.sourceRight", "Source {0}: provider-reported size/MD5 mismatches", "Quelle {0}: vom Anbieter gemeldete Größen-/MD5-Abweichungen"),
        ("plugins.changeReview.filterRight", "Filter {0} · source {1} · board {2} · rule {3} · {4}", "Filter {0} · Quelle {1} · Board {2} · Regel {3} · {4}"),
        ("plugins.changeReview.ackChanges", "I have reviewed and explicitly accept these capability/settings changes for this plugin.", "Ich habe diese Änderungen an Fähigkeiten/Einstellungen für dieses Plugin geprüft und akzeptiere sie ausdrücklich."),
        ("plugins.changeReview.ackRights", "I separately authorize the existing rights listed above for this exact new build.", "Ich erteile die oben aufgeführten vorhandenen Rechte für genau diesen neuen Build gesondert."),
        ("plugins.changeReview.save", "Save reviewed plugin for next start", "Geprüftes Plugin für nächsten Start speichern"),
        ("plugins.changeReview.saved", "Saved for next app start — nothing was activated now.", "Für den nächsten Appstart gespeichert — jetzt wurde nichts aktiviert."),
        ("plugins.changeReview.technical", "Technical details", "Technische Details"),
        ("plugins.changeReview.identities", "Selected: {0}\nTarget: {1}\nDelivered slot: {2}\nOld execution: {3}\nNew execution: {4}", "Ausgewählt: {0}\nZiel: {1}\nAuslieferungs-Slot: {2}\nAlte Ausführung: {3}\nNeue Ausführung: {4}"),
        ("plugins.changeReview.json", "Exact review data (JSON)", "Exakte Prüfdaten (JSON)"),
        ("plugins.changeReview.invalidReview", "This change review is unavailable or invalid. Nothing can be saved from this row; reload the review.", "Diese Änderungsprüfung fehlt oder ist ungültig. Aus dieser Zeile kann nichts gespeichert werden; lade die Prüfung neu."),
        ("plugins.changeReview.invalidFields", "Correct the highlighted settings before saving.", "Korrigiere die markierten Einstellungen vor dem Speichern."),
        ("plugins.changeReview.reload", "Reload the review", "Prüfung neu laden"),
        ("plugins.changeReview.removedSource", "Removed source settings: {0}", "Entfernte Quelleinstellungen: {0}"),
        ("plugins.changeReview.removedValues", "Removed settings remain in the old release. Use the existing acknowledgement below before saving.", "Entfernte Einstellungen bleiben in der alten Fassung erhalten. Verwende vor dem Speichern die vorhandene Bestätigung unten."),
        ("plugins.changeReview.oldConfig", "Configured values: {0}", "Konfigurierte Werte: {0}"),
        ("plugins.changeReview.newConfig", "Fields in the new schema: {0}", "Felder im neuen Schema: {0}"),
        ("plugins.changeReview.sources", "Sources and network access", "Quellen und Netzwerkzugriff"),
        ("plugins.changeReview.filters", "Filters and data access", "Filter und Datenzugriff"),
        ("plugins.changeReview.domains", "Allowed hosts: {0}", "Erlaubte Hosts: {0}"),
        ("plugins.changeReview.noFilters", "No filters declared.", "Keine Filter deklariert."),
        ("plugins.changeReview.filterCapabilities", "{0}: requested data access {1}", "{0}: angeforderter Datenzugriff {1}"),
        ("plugins.changeReview.schema", "Settings schema: {0} → {1}", "Einstellungsschema: {0} → {1}"),
        ("plugins.changeReview.noEditableSettings", "This update does not require any settings to be entered.", "Für dieses Update müssen keine Einstellungen eingegeben werden."),
        ("plugins.changeReview.ackNeeded", "Confirm the changes below to save. Existing special permissions require a separate confirmation.", "Bestätige unten die Änderungen, um zu speichern. Vorhandene Sonderrechte benötigen eine gesonderte Bestätigung."),
        ("plugins.changeReview.details", "Details of the changes", "Details der Änderungen"),
    };

    internal static string T(string key, params object?[] arguments) => Texts.T(key, arguments);
}
