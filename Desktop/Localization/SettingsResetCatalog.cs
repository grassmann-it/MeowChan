namespace MeowChan.Desktop.Localization;

/// <summary>Labels and confirmation copy for the settings draft reset actions.</summary>
internal static class SettingsResetCatalog
{
    internal static readonly (string Key, string English, string German)[] Entries =
    [
        ("settings.reset", "Reset", "Zurücksetzen"),
        ("settings.reset.all", "Reset all editable settings", "Alle bearbeitbaren Einstellungen zurücksetzen"),
        ("settings.reset.section", "Reset {0}", "{0} zurücksetzen"),
        ("settings.reset.question", "Reset {0} to its defaults? Only the unsaved draft changes; Save is still required.", "{0} auf die Standardwerte zurücksetzen? Nur der ungespeicherte Entwurf ändert sich; Speichern ist weiterhin nötig."),
        ("settings.reset.allQuestion", "Reset all editable settings to their defaults? Only the unsaved draft changes; Save is still required.", "Alle bearbeitbaren Einstellungen auf ihre Standardwerte zurücksetzen? Nur der ungespeicherte Entwurf ändert sich; Speichern ist weiterhin nötig."),
        ("settings.reset.confirm", "Reset draft", "Entwurf zurücksetzen"),
        ("settings.reset.cancel", "Cancel", "Abbrechen"),
        ("settings.reset.done", "Defaults loaded into the draft. Save to apply them.", "Standards in den Entwurf geladen. Zum Übernehmen speichern."),
    ];
}
