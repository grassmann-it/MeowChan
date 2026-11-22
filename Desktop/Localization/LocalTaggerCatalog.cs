namespace MeowChan.Desktop.Localization;

internal static class LocalTaggerCatalog
{
    internal static readonly (string Key, string English, string German)[] Entries =
    [
        ("localTagger.title", "Local image tagger", "Lokaler Bildtagger"),
        ("localTagger.choose", "Choose model manifest…", "Modellmanifest wählen…"),
        ("localTagger.import", "Verify and import", "Prüfen und importieren"),
        ("localTagger.checking", "Checking local model files…", "Lokale Modelldateien werden geprüft…"),
        ("localTagger.importing", "Verifying and importing the model…", "Modell wird geprüft und importiert…"),
        ("localTagger.installed", "Verified model installed: {0}", "Geprüftes Modell installiert: {0}"),
        ("localTagger.invalid", "The installed model could not be verified.", "Das installierte Modell konnte nicht verifiziert werden."),
        ("localTagger.empty", "No model is installed.", "Kein Modell installiert."),
        ("localTagger.engineUnavailable", "CPU tagging is unavailable until a model and adapter are approved.", "CPU-Tagging ist erst nach Freigabe von Modell und Adapter verfügbar."),
        ("localTagger.limits", "Import limits: {0} for the manifest, {1} for the model weights.", "Importlimits: {0} für das Manifest, {1} für die Modellgewichte."),
        ("localTagger.modelInfo", "{0} by {1} · revision {2} · {3} · {4}", "{0} von {1} · Revision {2} · {3} · {4}"),
        ("localTagger.space", "New package: {0} · Free space: {1}", "Neues Paket: {0} · Freier Speicher: {1}"),
        ("localTagger.spaceUnknown", "New package: {0} · Free space unknown", "Neues Paket: {0} · Freier Speicher unbekannt"),
        ("localTagger.cancelled", "Model operation cancelled.", "Modellvorgang abgebrochen."),
        ("localTagger.limitError", "The manifest or model exceeds the import limit.", "Das Manifest oder Modell überschreitet das Importlimit."),
        ("localTagger.spaceError", "There is not enough free space to import this model.", "Für den Modellimport ist zu wenig Speicher frei."),
        ("localTagger.integrityError", "The files do not match the manifest, byte count or SHA-256.", "Die Dateien stimmen nicht mit Manifest, Bytezahl oder SHA-256 überein."),
        ("localTagger.manifestError", "The model manifest is invalid or unsupported.", "Das Modellmanifest ist ungültig oder wird nicht unterstützt."),
        ("localTagger.readError", "The local model files could not be read or saved.", "Die lokalen Modelldateien konnten nicht gelesen oder gespeichert werden."),
    ];
}
