namespace MeowChan.Desktop.Localization;

// Data only. Root integrates Entries into both BuiltInCatalog dictionaries and
// supplies the corresponding bundled ES/FR/RU keys; no separate fallback path.
internal static class FirstRunLimitCatalog
{
    internal static readonly (string Key, string English, string German)[] Entries =
    {
        ("setup.parallelRequested", "Requested parallel downloads (whole number)", "Gewünschte parallele Downloads (ganze Zahl)"),
        ("setup.parallelEffective", "Effective limit after acceptance: {0} simultaneous media transfers (requested: {1}; safety cap: {2}).", "Wirksames Limit nach Übernahme: {0} gleichzeitige Mediendateitransfers (gewünscht: {1}; Ressourcenobergrenze: {2})."),
        ("setup.parallelEffectiveInvalid", "Effective limit unavailable: enter a valid whole parallel count. Safety cap: {0}.", "Wirksames Limit nicht verfügbar: eine gültige ganze Parallelanzahl eingeben. Ressourcenobergrenze: {0}."),
        ("setup.parallelSafetyHint", "The requested parallel count and the safety cap (media transfer resource limit) are separate; the smaller value applies. The cap limits open media transfers, not watched threads or queued files. Setup keeps the saved cap unchanged. Change both later in Settings → Downloads; source and bandwidth limits still apply.", "Gewünschte Parallelanzahl und Ressourcenobergrenze für Mediendownloads sind getrennte Werte; der kleinere gilt. Die Obergrenze begrenzt offene Mediendateitransfers, nicht beobachtete Threads oder wartende Dateien. Die Einrichtung lässt die gespeicherte Obergrenze unverändert. Beide Werte sind später unter Einstellungen → Downloads änderbar; Quellen- und Bandbreitenlimits gelten weiterhin."),
    };
}
