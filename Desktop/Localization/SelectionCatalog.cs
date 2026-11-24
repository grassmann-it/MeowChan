namespace MeowChan.Desktop.Localization;
internal static class SelectionCatalog
{
    internal static readonly (string Key, string English, string German)[] Entries =
    {
        ("selection.export", "Export selection", "Auswahl exportieren"),
        ("selection.remove", "Remove selection", "Auswahl entfernen"),
        ("selection.copyUrls", "Copy selected URLs", "Ausgewählte URLs kopieren"),
        ("selection.actionSubset", "{0} ({1} of {2})", "{0} ({1} von {2})"),
        ("selection.failed", "{0} of {1} actions failed: {2}", "{0} von {1} Aktionen fehlgeschlagen: {2}"),
        ("shell.pausing", "Pausing…", "Wird pausiert…"),
        ("action.pausingShort", "Pausing…", "Pausiert…")
    };
}
