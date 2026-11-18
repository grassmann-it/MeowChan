namespace MeowChan.Desktop.Localization;

// Data only. Root integrates Entries into both BuiltInCatalog dictionaries and
// supplies the corresponding bundled ES/FR/RU keys; no separate fallback path.
internal static class FirstRunLimitCatalog
{
    internal static readonly (string Key, string English, string German)[] Entries =
    {
        ("setup.parallelRequested", "Requested parallel downloads (whole number)", "Gewünschte parallele Downloads (ganze Zahl)"),
        ("setup.parallelEffective", "Up to {0} files download at the same time (you asked for {1}; upper limit {2}).", "Es laden bis zu {0} Dateien gleichzeitig (gewünscht: {1}; Obergrenze: {2})."),
        ("setup.parallelEffectiveInvalid", "Enter a whole number of parallel downloads. Upper limit: {0}.", "Gib eine ganze Zahl paralleler Downloads ein. Obergrenze: {0}."),
        ("setup.parallelSafetyHint", "The smaller of your number and the upper limit for simultaneous files applies. You can change both later under Settings → Downloads.", "Es gilt der kleinere Wert aus deiner Angabe und der Obergrenze für gleichzeitige Dateien. Beides kannst du später unter Einstellungen → Downloads ändern."),
    };
}
