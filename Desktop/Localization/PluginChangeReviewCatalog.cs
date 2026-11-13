using System;
using System.Collections.Generic;
using System.Linq;

namespace MeowChan.Desktop.Localization;

/// <summary>Small consent-dialog catalog kept separate from the shared built-in catalog.</summary>
internal static class PluginChangeReviewCatalog
{
    internal static readonly (string Key, string English, string German)[] Entries =
    {
        ("plugins.changeReview.title", "Review changed plugin", "Geändertes Plugin prüfen"),
        ("plugins.changeReview.explanation", "{0} changed what this plugin can do or how its settings work. Review the visible changes, correct the settings below, and save this exact choice for the next app start. Nothing is activated now.", "{0} hat geändert, was dieses Plugin kann oder wie seine Einstellungen funktionieren. Prüfe die sichtbaren Änderungen, korrigiere die Einstellungen unten und speichere diese genaue Auswahl für den nächsten Appstart. Jetzt wird nichts aktiviert."),
        ("plugins.changeReview.plugin", "Plugin: {0}", "Plugin: {0}"),
        ("plugins.changeReview.capabilities", "Changed capabilities", "Geänderte Fähigkeiten"),
        ("plugins.changeReview.old", "Before", "Vorher"),
        ("plugins.changeReview.new", "After", "Nachher"),
        ("plugins.changeReview.noCapabilities", "No source capability is declared.", "Keine Quellenfähigkeit ist deklariert."),
        ("plugins.changeReview.sourceCapabilities", "{0}: {1}", "{0}: {1}"),
        ("plugins.changeReview.settings", "Settings for next start", "Einstellungen für nächsten Start"),
        ("plugins.changeReview.settingsHelp", "The previous values are loaded into the new schema. Correct them here; values whose fields disappeared stay visible until you acknowledge their removal.", "Die bisherigen Werte sind in das neue Schema geladen. Korrigiere sie hier; Werte verschwundener Felder bleiben sichtbar, bis du ihre Entfernung bestätigst."),
        ("plugins.changeReview.oldSettings", "Previous settings (read-only)", "Bisherige Einstellungen (nur lesen)"),
        ("plugins.changeReview.newSettings", "New settings (editable)", "Neue Einstellungen (bearbeitbar)"),
        ("plugins.changeReview.noSettings", "No plugin or source settings are declared.", "Keine Plugin- oder Quelleinstellungen sind deklariert."),
        ("plugins.changeReview.rights", "Existing rights that would apply to the new build", "Vorhandene Rechte, die für den neuen Build gelten würden"),
        ("plugins.changeReview.rightsNone", "No existing special rights would be carried into this review.", "Keine besonderen vorhandenen Rechte würden in diese Prüfung übernommen."),
        ("plugins.changeReview.sourceRight", "Source {0}: provider-reported size/MD5 mismatches", "Quelle {0}: vom Anbieter gemeldete Größen-/MD5-Abweichungen"),
        ("plugins.changeReview.filterRight", "Filter {0} · source {1} · board {2} · rule {3} · {4}", "Filter {0} · Quelle {1} · Board {2} · Regel {3} · {4}"),
        ("plugins.changeReview.ackChanges", "I have reviewed and explicitly accept these capability/settings changes for this plugin.", "Ich habe diese Änderungen an Fähigkeiten/Einstellungen für dieses Plugin geprüft und akzeptiere sie ausdrücklich."),
        ("plugins.changeReview.ackRights", "I separately authorize the existing rights listed above for this exact new build.", "Ich erteile die oben aufgeführten vorhandenen Rechte für genau diesen neuen Build gesondert."),
        ("plugins.changeReview.save", "Save reviewed plugin for next start", "Geprüftes Plugin für nächsten Start speichern"),
        ("plugins.changeReview.saved", "Saved for next app start — nothing was activated now.", "Für den nächsten Appstart gespeichert — jetzt wurde nichts aktiviert."),
        ("plugins.changeReview.technical", "Technical details", "Technische Details"),
        ("plugins.changeReview.identities", "Selected: {0}\nTarget: {1}\nDelivered slot: {2}\nOld execution: {3}\nNew execution: {4}", "Ausgewählt: {0}\nZiel: {1}\nAuslieferungs-Slot: {2}\nAlte Ausführung: {3}\nNeue Ausführung: {4}"),
        ("plugins.changeReview.json", "Exact review payload (JSON)", "Exakte Prüflast (JSON)"),
        ("plugins.changeReview.invalidReview", "This change review is unavailable or invalid. Nothing can be saved from this row; reload the review.", "Diese Änderungsprüfung fehlt oder ist ungültig. Aus dieser Zeile kann nichts gespeichert werden; lade die Prüfung neu."),
        ("plugins.changeReview.invalidFields", "Correct the highlighted settings before saving.", "Korrigiere die markierten Einstellungen vor dem Speichern."),
        ("plugins.changeReview.reload", "Reload the review", "Prüfung neu laden"),
        ("plugins.changeReview.removedSource", "Removed source settings: {0}", "Entfernte Quelleinstellungen: {0}"),
        ("plugins.changeReview.removedValues", "Removed settings remain in the old release. Use the existing acknowledgement below before saving.", "Entfernte Einstellungen bleiben in der alten Fassung erhalten. Verwende vor dem Speichern die vorhandene Bestätigung unten."),
        ("plugins.changeReview.oldConfig", "{0} configured value(s)", "{0} konfigurierte Werte"),
        ("plugins.changeReview.newConfig", "{0} field(s) in the new schema", "{0} Felder im neuen Schema"),
    };

    internal static string T(string key, params object?[] arguments)
    {
        bool german = Texts.Language.StartsWith("de", StringComparison.OrdinalIgnoreCase);
        (string Key, string English, string German)? found = Entries.FirstOrDefault(entry => entry.Key == key);
        string value = found?.Key is null ? key : german ? found.Value.German : found.Value.English;
        try { return arguments.Length == 0 ? value : string.Format(german ? System.Globalization.CultureInfo.GetCultureInfo("de") : System.Globalization.CultureInfo.GetCultureInfo("en"), value, arguments); }
        catch (FormatException) { return value; }
    }
}
