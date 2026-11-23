namespace MeowChan.Desktop.Localization;

internal static class BooruListCatalog
{
    internal static readonly (string Key, string English, string German)[] Entries =
    [
        ("settings.booruList.PreviewWidth", "Booru list preview width (DIP)", "Vorschaubreite in der Booru-Liste (DIP)"),
        ("settings.booruList.MinimumWidth", "Minimum preview width (DIP)", "Minimale Vorschaubreite (DIP)"),
        ("settings.booruList.MaximumWidth", "Maximum preview width (DIP)", "Maximale Vorschaubreite (DIP)"),
        ("settings.booruList.MediumFrom", "Medium layout starts at (DIP)", "Mittleres Layout ab (DIP)"),
        ("settings.booruList.WideAbove", "Wide layout starts above (DIP)", "Breites Layout oberhalb von (DIP)"),
        ("settings.booruList.NarrowPreviewHeight", "Preview height, narrow (DIP)", "Vorschauhöhe, schmal (DIP)"),
        ("settings.booruList.MediumPreviewHeight", "Preview height, medium (DIP)", "Vorschauhöhe, mittel (DIP)"),
        ("settings.booruList.WidePreviewHeight", "Preview height, wide (DIP)", "Vorschauhöhe, breit (DIP)"),
        ("settings.booruList.NarrowRowHeight", "Collapsed row height, narrow (DIP)", "Eingeklappte Zeilenhöhe, schmal (DIP)"),
        ("settings.booruList.MediumRowHeight", "Collapsed row height, medium (DIP)", "Eingeklappte Zeilenhöhe, mittel (DIP)"),
        ("settings.booruList.WideRowHeight", "Collapsed row height, wide (DIP)", "Eingeklappte Zeilenhöhe, breit (DIP)"),
        ("settings.booruList.advanced", "Booru list layout — Advanced", "Layout der Booru-Liste — Erweitert"),
        ("settings.booruList.default", "Default: {0}.", "Standard: {0}."),
        ("settings.booruList.invalid.Width", "Keep the preview width between the minimum and maximum, with the minimum at or below the maximum.", "Die Vorschaubreite muss zwischen Minimum und Maximum liegen. Das Minimum darf das Maximum nicht überschreiten."),
        ("settings.booruList.invalid.Breakpoint", "Set the medium threshold below the wide threshold and leave at least 320 DIP beside the preview.", "Die mittlere Schwelle muss unter der breiten liegen und neben der Vorschau mindestens 320 DIP frei lassen."),
        ("settings.booruList.invalid.Height", "Preview heights must stay the same or increase from narrow to wide.", "Die Vorschauhöhe muss von schmal bis breit gleich bleiben oder steigen."),
        ("settings.booruList.invalid.Row", "Allow at least {0} DIP above the preview height for commands and borders.", "Für Aktionen und Rahmen muss die Zeilenhöhe mindestens {0} DIP über der Vorschauhöhe liegen."),
        ("booru.ui.tags.less", "Fewer tags", "Weniger"),
    ];
}
