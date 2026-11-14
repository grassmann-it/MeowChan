namespace MeowChan.Desktop.Localization;

/// <summary>Visible limits and their scope, defaults and bounds; arguments are supplied from Config constants.</summary>
internal static class SettingsLimitsCatalog
{
    internal static readonly (string Key, string English, string German)[] Entries =
    [
        ("budgets.archiveTagFilterTagsPerList", "Archive tag filters per list", "Archiv-Tagfilter pro Liste"),
        ("budgets.archiveTagFilterTagsPerListHint", "Maximum tags in each include/exclude filter list. Default: {0}; allowed: {1}–{2}. Lowering this does not truncate stored filters.", "Höchstzahl der Tags je Einschluss-/Ausschlussliste. Standard: {0}; erlaubt: {1}–{2}. Ein niedrigerer Wert kürzt gespeicherte Filter nicht."),
        ("budgets.urlTextExportLimitMiB", "URL text export size (MiB)", "Größe des URL-Textexports (MiB)"),
        ("budgets.urlTextExportLimitMiBHint", "Maximum size of a new URL text export. Default: {0} MiB; allowed: {1}–{2} MiB.", "Maximale Größe eines neuen URL-Textexports. Standard: {0} MiB; erlaubt: {1}–{2} MiB."),
        ("budgets.slashTagSuggestionCount", "Slash-tag suggestions", "Slash-Tagvorschläge"),
        ("budgets.slashTagSuggestionCountHint", "Maximum suggestions shown while entering a slash tag. Default: {0}; allowed: {1}–{2}.", "Höchstzahl angezeigter Vorschläge bei der Eingabe eines Slash-Tags. Standard: {0}; erlaubt: {1}–{2}."),
        ("budgets.videoScrubCacheMiB", "Video scrub preview cache size (MiB)", "Größe des Video-Scrub-Vorschau-Caches (MiB)"),
        ("budgets.videoScrubCacheMiBHint", "Disk budget for video timeline previews in this profile; original media is unaffected. Default: {0} MiB; allowed: {1}–{2} MiB.", "Speicherbudget für Video-Zeitleistenvorschauen in diesem Profil; Originalmedien bleiben unverändert. Standard: {0} MiB; erlaubt: {1}–{2} MiB."),
        ("settings.shrinkWindowForBarebones", "Shrink the window when switching to Minimal", "Fenster beim Wechsel zu Minimal verkleinern"),
        ("settings.shrinkWindowForBarebonesHint", "On by default. When off, switching to Minimal keeps the current window size. Save to apply; the unsaved view selection does not resize the window.", "Standardmäßig an. Wenn ausgeschaltet, bleibt beim Wechsel zu Minimal die aktuelle Fenstergröße erhalten. Zum Übernehmen speichern; die ungespeicherte Ansichtsauswahl ändert die Fenstergröße nicht."),
    ];
}
