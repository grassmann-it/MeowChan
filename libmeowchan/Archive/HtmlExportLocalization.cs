using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;

namespace MeowChan.Archive;

internal enum HtmlExportText
{
    LastCapture,
    NoPosts,
    Retention,
    ExternalThreads,
    UncapturedReferences,
    CollectionStart,
    Removed,
    ExternalReference,
    PostNotCaptured,
    OtherThread,
    UnresolvedReference,
    SavedMedia,
    GalleryDescription,
    GalleryOmitted,
    ExportResult,
    ResultCounts,
    VerifiedOffline,
    DeletedAttachment,
    SavedFile,
    Imported,
    Mismatch,
    SourceInfo,
    Size,
    SizeHint,
    SavedSha1,
    Unspecified,
    CollectionTitle,
    CollectionIntro,
    PostCount,
    CollectionCounts,
    Offline,
    OmissionMediaNotIncluded,
    OmissionExcludedByRule,
    OmissionUnavailable,
    OmissionDifferentVersion,
    OmissionVerificationFailed,
    OmissionNoMediaIdentity,
    WarningManifestUnavailable,
    WarningDirectoryUnreadable,
    WarningManifestInvalid,
    PostNumber,
    Theme,
    ThemeSystem,
    ThemeLight,
    ThemeDark,
    About,
    AboutTitle,
    AboutClose,
    AboutSource,
    AboutThreadNumber,
    AboutThreadTitle,
    AboutPosts,
    AboutMedia,
    AboutThreads,
    AboutCopiedMedia,
    AboutFirstPost,
    AboutLastPost,
    AboutLastCapture,
    AboutExported,
    CollectedWith,
    MadeBy,
    CatsAlt,
    PhotoCredit,
    ThemeHint
}

/// <summary>Small, dependency-free localization surface for generated HTML framing text.</summary>
internal sealed class HtmlExportLocalization
{
    private static readonly IReadOnlyDictionary<HtmlExportText, string> English = new Dictionary<HtmlExportText, string>
    {
        [HtmlExportText.LastCapture] = "Last observed capture: {0}",
        [HtmlExportText.NoPosts] = "No captured posts",
        [HtmlExportText.Retention] = "Only observed posts are retained. Missing source posts do not erase earlier captures.",
        [HtmlExportText.ExternalThreads] = "External threads are not included in this folder.",
        [HtmlExportText.UncapturedReferences] = "References outside captured export posts remain unavailable offline.",
        [HtmlExportText.CollectionStart] = "Collection start page",
        [HtmlExportText.Removed] = "Removed at source",
        [HtmlExportText.ExternalReference] = "External reference",
        [HtmlExportText.PostNotCaptured] = "Post not captured",
        [HtmlExportText.OtherThread] = "Other thread, outside this export",
        [HtmlExportText.UnresolvedReference] = "Unresolved reference",
        [HtmlExportText.SavedMedia] = "Saved media",
        [HtmlExportText.GalleryDescription] = "No post text was captured. This gallery contains current manifest-published originals.",
        [HtmlExportText.GalleryOmitted] = "Saved media omitted by export choice.",
        [HtmlExportText.ExportResult] = "Export result",
        [HtmlExportText.ResultCounts] = "{0} captured posts · {1} copied media files · {2} attachment omissions",
        [HtmlExportText.VerifiedOffline] = "Media bytes were verified against their manifest before copying. This folder requires no MeowChan profile, server or network connection.",
        [HtmlExportText.DeletedAttachment] = "Deleted attachment",
        [HtmlExportText.SavedFile] = "Saved file: {0} bytes.",
        [HtmlExportText.Imported] = "Imported archive file; no local source permission is implied.",
        [HtmlExportText.Mismatch] = "Saved with source-specific permission; the source size or checksum differs from the saved file.",
        [HtmlExportText.SourceInfo] = "Source-reported media information",
        [HtmlExportText.Size] = "Size: {0} bytes",
        [HtmlExportText.SizeHint] = "Size hint (not exact bytes): {0} {1} ({2})",
        [HtmlExportText.SavedSha1] = "Saved file SHA-1: {0}",
        [HtmlExportText.Unspecified] = "Unspecified",
        [HtmlExportText.CollectionTitle] = "MeowChan collection",
        [HtmlExportText.CollectionIntro] = "Independent local thread captures, with shared verified media. Only captured posts are included.",
        [HtmlExportText.PostCount] = "{0} posts",
        [HtmlExportText.CollectionCounts] = "{0} threads · {1} captured posts · {2} copied media files · {3} attachment omissions",
        [HtmlExportText.Offline] = "Requires no MeowChan profile, server or network connection.",
        [HtmlExportText.OmissionMediaNotIncluded] = "Media omitted by export choice",
        [HtmlExportText.OmissionExcludedByRule] = "Excluded by the original download rule",
        [HtmlExportText.OmissionUnavailable] = "Saved media unavailable",
        [HtmlExportText.OmissionDifferentVersion] = "No saved version matches the captured attachment",
        [HtmlExportText.OmissionVerificationFailed] = "Saved media failed verification or could not be read",
        [HtmlExportText.OmissionNoMediaIdentity] = "No retained media identity",
        [HtmlExportText.WarningManifestUnavailable] = "A saved media manifest is unavailable.",
        [HtmlExportText.WarningDirectoryUnreadable] = "A saved media directory could not be read.",
        [HtmlExportText.WarningManifestInvalid] = "The media manifest is invalid; no assets were inferred or repaired.",
        [HtmlExportText.PostNumber] = "No. {0}",
        [HtmlExportText.Theme] = "Theme",
        [HtmlExportText.ThemeSystem] = "System",
        [HtmlExportText.ThemeLight] = "Light",
        [HtmlExportText.ThemeDark] = "Dark",
        [HtmlExportText.About] = "About",
        [HtmlExportText.AboutTitle] = "About this export",
        [HtmlExportText.AboutClose] = "Close",
        [HtmlExportText.AboutSource] = "Source and board",
        [HtmlExportText.AboutThreadNumber] = "Thread number",
        [HtmlExportText.AboutThreadTitle] = "Title",
        [HtmlExportText.AboutPosts] = "Captured posts",
        [HtmlExportText.AboutMedia] = "Media files on this page",
        [HtmlExportText.AboutThreads] = "Exported threads",
        [HtmlExportText.AboutCopiedMedia] = "Copied media files",
        [HtmlExportText.AboutFirstPost] = "First post",
        [HtmlExportText.AboutLastPost] = "Last post",
        [HtmlExportText.AboutLastCapture] = "Last capture",
        [HtmlExportText.AboutExported] = "Exported",
        [HtmlExportText.CollectedWith] = "Collected and exported with MeowChan {0}",
        [HtmlExportText.MadeBy] = "MeowChan by {0}",
        [HtmlExportText.CatsAlt] = "Simba and Mila, the MeowChan cats",
        [HtmlExportText.PhotoCredit] = "Cat logos: Simba (orange) and Mila (grey), each derived from a photograph by {0}. They are used with the photographer's permission for the projects of Tom Graßmann, on condition of attribution by name; the photographs are not covered by MeowChan's license.",
        [HtmlExportText.ThemeHint] = "The theme choice applies to this view only and is not saved."
    };

    private static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<HtmlExportText, string>> Translations =
        new Dictionary<string, IReadOnlyDictionary<HtmlExportText, string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["de"] = new Dictionary<HtmlExportText, string>
            {
                [HtmlExportText.LastCapture] = "Letzte beobachtete Erfassung: {0}",
                [HtmlExportText.NoPosts] = "Keine erfassten Posts",
                [HtmlExportText.Retention] = "Es werden nur beobachtete Posts behalten. Fehlende Posts der Quelle löschen frühere Erfassungen nicht.",
                [HtmlExportText.ExternalThreads] = "Externe Threads sind in diesem Ordner nicht enthalten.",
                [HtmlExportText.UncapturedReferences] = "Verweise auf Posts, die nicht im Export erfasst sind, bleiben offline nicht verfügbar.",
                [HtmlExportText.CollectionStart] = "Startseite der Sammlung",
                [HtmlExportText.Removed] = "An der Quelle entfernt",
                [HtmlExportText.ExternalReference] = "Externer Verweis",
                [HtmlExportText.PostNotCaptured] = "Post nicht erfasst",
                [HtmlExportText.OtherThread] = "Anderer Thread außerhalb dieses Exports",
                [HtmlExportText.UnresolvedReference] = "Nicht aufgelöster Verweis",
                [HtmlExportText.SavedMedia] = "Gespeicherte Medien",
                [HtmlExportText.GalleryDescription] = "Kein Post-Text wurde erfasst. Diese Galerie enthält die aktuell im Manifest veröffentlichten Originale.",
                [HtmlExportText.GalleryOmitted] = "Gespeicherte Medien gemäß Exportauswahl ausgelassen.",
                [HtmlExportText.ExportResult] = "Exportergebnis",
                [HtmlExportText.ResultCounts] = "{0} erfasste Posts · {1} kopierte Mediendateien · {2} ausgelassene Anhänge",
                [HtmlExportText.VerifiedOffline] = "Medienbytes wurden vor dem Kopieren anhand ihres Manifests verifiziert. Dieser Ordner benötigt kein MeowChan-Profil, keinen Server und keine Netzwerkverbindung.",
                [HtmlExportText.DeletedAttachment] = "Gelöschter Anhang",
                [HtmlExportText.SavedFile] = "Gespeicherte Datei: {0} Bytes.",
                [HtmlExportText.Imported] = "Importierte Archivdatei; daraus folgt keine lokale Quellberechtigung.",
                [HtmlExportText.Mismatch] = "Mit quellenspezifischer Berechtigung gespeichert; Größe oder Prüfsumme der Quelle weichen von der gespeicherten Datei ab.",
                [HtmlExportText.SourceInfo] = "Von der Quelle gemeldete Medieninformationen",
                [HtmlExportText.Size] = "Größe: {0} Bytes",
                [HtmlExportText.SizeHint] = "Größenhinweis (keine exakte Bytezahl): {0} {1} ({2})",
                [HtmlExportText.SavedSha1] = "Gespeicherte Datei SHA-1: {0}",
                [HtmlExportText.Unspecified] = "Nicht angegeben",
                [HtmlExportText.CollectionTitle] = "MeowChan-Sammlung",
                [HtmlExportText.CollectionIntro] = "Unabhängige lokale Thread-Erfassungen mit gemeinsam genutzten verifizierten Medien. Es werden nur erfasste Posts aufgenommen.",
                [HtmlExportText.PostCount] = "{0} Posts",
                [HtmlExportText.CollectionCounts] = "{0} Threads · {1} erfasste Posts · {2} kopierte Mediendateien · {3} ausgelassene Anhänge",
                [HtmlExportText.Offline] = "Benötigt kein MeowChan-Profil, keinen Server und keine Netzwerkverbindung.",
                [HtmlExportText.OmissionMediaNotIncluded] = "Medien gemäß Exportauswahl ausgelassen",
                [HtmlExportText.OmissionExcludedByRule] = "Durch die ursprüngliche Downloadregel ausgeschlossen",
                [HtmlExportText.OmissionUnavailable] = "Gespeicherte Medien nicht verfügbar",
                [HtmlExportText.OmissionDifferentVersion] = "Keine gespeicherte Version passt zum erfassten Anhang",
                [HtmlExportText.OmissionVerificationFailed] = "Gespeicherte Medien konnten nicht verifiziert oder gelesen werden",
                [HtmlExportText.OmissionNoMediaIdentity] = "Keine gespeicherte Medienidentität",
                [HtmlExportText.WarningManifestUnavailable] = "Ein gespeichertes Medienmanifest ist nicht verfügbar.",
                [HtmlExportText.WarningDirectoryUnreadable] = "Ein gespeichertes Medienverzeichnis konnte nicht gelesen werden.",
                [HtmlExportText.WarningManifestInvalid] = "Das Medienmanifest ist ungültig; es wurden keine Medien abgeleitet oder repariert.",
                [HtmlExportText.PostNumber] = "Nr. {0}",
                [HtmlExportText.Theme] = "Farbschema",
                [HtmlExportText.ThemeSystem] = "Systemvorgabe",
                [HtmlExportText.ThemeLight] = "Hell",
                [HtmlExportText.ThemeDark] = "Dunkel",
                [HtmlExportText.About] = "Über",
                [HtmlExportText.AboutTitle] = "Über diesen Export",
                [HtmlExportText.AboutClose] = "Schließen",
                [HtmlExportText.AboutSource] = "Quelle und Board",
                [HtmlExportText.AboutThreadNumber] = "Threadnummer",
                [HtmlExportText.AboutThreadTitle] = "Titel",
                [HtmlExportText.AboutPosts] = "Erfasste Posts",
                [HtmlExportText.AboutMedia] = "Mediendateien auf dieser Seite",
                [HtmlExportText.AboutThreads] = "Exportierte Threads",
                [HtmlExportText.AboutCopiedMedia] = "Kopierte Mediendateien",
                [HtmlExportText.AboutFirstPost] = "Erster Post",
                [HtmlExportText.AboutLastPost] = "Letzter Post",
                [HtmlExportText.AboutLastCapture] = "Letzte Erfassung",
                [HtmlExportText.AboutExported] = "Exportiert",
                [HtmlExportText.CollectedWith] = "Gesammelt und exportiert mit MeowChan {0}",
                [HtmlExportText.MadeBy] = "MeowChan von {0}",
                [HtmlExportText.CatsAlt] = "Simba und Mila, die MeowChan-Katzen",
                [HtmlExportText.PhotoCredit] = "Katzenlogos: Simba (orange) und Mila (grau), jeweils abgeleitet von einem Foto von {0}. Sie werden mit Erlaubnis des Fotografen für die Projekte von Tom Graßmann verwendet, unter der Bedingung der Namensnennung; die Fotos fallen nicht unter die Lizenz von MeowChan.",
                [HtmlExportText.ThemeHint] = "Die Wahl des Farbschemas gilt nur für diese Ansicht und wird nicht gespeichert."
            },
            ["es"] = new Dictionary<HtmlExportText, string>
            {
                [HtmlExportText.LastCapture] = "Última captura observada: {0}",
                [HtmlExportText.NoPosts] = "No hay publicaciones capturadas",
                [HtmlExportText.Retention] = "Solo se conservan las publicaciones observadas. Las publicaciones que faltan en el origen no borran capturas anteriores.",
                [HtmlExportText.ExternalThreads] = "Los hilos externos no están incluidos en esta carpeta.",
                [HtmlExportText.UncapturedReferences] = "Las referencias a publicaciones fuera de la exportación capturada no están disponibles sin conexión.",
                [HtmlExportText.CollectionStart] = "Página inicial de la colección",
                [HtmlExportText.Removed] = "Eliminada en el origen",
                [HtmlExportText.ExternalReference] = "Referencia externa",
                [HtmlExportText.PostNotCaptured] = "Publicación no capturada",
                [HtmlExportText.OtherThread] = "Otro hilo, fuera de esta exportación",
                [HtmlExportText.UnresolvedReference] = "Referencia sin resolver",
                [HtmlExportText.SavedMedia] = "Medios guardados",
                [HtmlExportText.GalleryDescription] = "No se capturó texto de publicaciones. Esta galería contiene los originales actuales publicados en el manifiesto.",
                [HtmlExportText.GalleryOmitted] = "Medios guardados omitidos por la opción de exportación.",
                [HtmlExportText.ExportResult] = "Resultado de la exportación",
                [HtmlExportText.ResultCounts] = "{0} publicaciones capturadas · {1} archivos multimedia copiados · {2} archivos adjuntos omitidos",
                [HtmlExportText.VerifiedOffline] = "Los bytes multimedia se verificaron con su manifiesto antes de copiarlos. Esta carpeta no requiere perfil de MeowChan, servidor ni conexión de red.",
                [HtmlExportText.DeletedAttachment] = "Archivo adjunto eliminado",
                [HtmlExportText.SavedFile] = "Archivo guardado: {0} bytes.",
                [HtmlExportText.Imported] = "Fichero de archivo importado; no implica permiso local sobre el origen.",
                [HtmlExportText.Mismatch] = "Guardado con permiso específico del origen; el tamaño o la suma de comprobación del origen difiere del archivo guardado.",
                [HtmlExportText.SourceInfo] = "Información multimedia indicada por el origen",
                [HtmlExportText.Size] = "Tamaño: {0} bytes",
                [HtmlExportText.SizeHint] = "Indicación de tamaño (no son bytes exactos): {0} {1} ({2})",
                [HtmlExportText.SavedSha1] = "SHA-1 del archivo guardado: {0}",
                [HtmlExportText.Unspecified] = "No especificado",
                [HtmlExportText.CollectionTitle] = "Colección de MeowChan",
                [HtmlExportText.CollectionIntro] = "Capturas locales independientes de hilos, con medios verificados compartidos. Solo se incluyen publicaciones capturadas.",
                [HtmlExportText.PostCount] = "{0} publicaciones",
                [HtmlExportText.CollectionCounts] = "{0} hilos · {1} publicaciones capturadas · {2} archivos multimedia copiados · {3} archivos adjuntos omitidos",
                [HtmlExportText.Offline] = "No requiere perfil de MeowChan, servidor ni conexión de red.",
                [HtmlExportText.OmissionMediaNotIncluded] = "Medios omitidos por la opción de exportación",
                [HtmlExportText.OmissionExcludedByRule] = "Excluido por la regla de descarga original",
                [HtmlExportText.OmissionUnavailable] = "Medios guardados no disponibles",
                [HtmlExportText.OmissionDifferentVersion] = "Ninguna versión guardada coincide con el archivo adjunto capturado",
                [HtmlExportText.OmissionVerificationFailed] = "Los medios guardados no superaron la verificación o no se pudieron leer",
                [HtmlExportText.OmissionNoMediaIdentity] = "No hay identidad de medios conservada",
                [HtmlExportText.WarningManifestUnavailable] = "El manifiesto de medios guardado no está disponible.",
                [HtmlExportText.WarningDirectoryUnreadable] = "No se pudo leer un directorio de medios guardado.",
                [HtmlExportText.WarningManifestInvalid] = "El manifiesto de medios no es válido; no se dedujeron ni repararon archivos multimedia.",
                [HtmlExportText.PostNumber] = "N.º {0}",
                [HtmlExportText.Theme] = "Tema",
                [HtmlExportText.ThemeSystem] = "Sistema",
                [HtmlExportText.ThemeLight] = "Claro",
                [HtmlExportText.ThemeDark] = "Oscuro",
                [HtmlExportText.About] = "Acerca de",
                [HtmlExportText.AboutTitle] = "Acerca de esta exportación",
                [HtmlExportText.AboutClose] = "Cerrar",
                [HtmlExportText.AboutSource] = "Fuente y tablón",
                [HtmlExportText.AboutThreadNumber] = "Número de hilo",
                [HtmlExportText.AboutThreadTitle] = "Título",
                [HtmlExportText.AboutPosts] = "Publicaciones capturadas",
                [HtmlExportText.AboutMedia] = "Archivos multimedia en esta página",
                [HtmlExportText.AboutThreads] = "Hilos exportados",
                [HtmlExportText.AboutCopiedMedia] = "Archivos multimedia copiados",
                [HtmlExportText.AboutFirstPost] = "Primera publicación",
                [HtmlExportText.AboutLastPost] = "Última publicación",
                [HtmlExportText.AboutLastCapture] = "Última captura",
                [HtmlExportText.AboutExported] = "Exportado",
                [HtmlExportText.CollectedWith] = "Recopilado y exportado con MeowChan {0}",
                [HtmlExportText.MadeBy] = "MeowChan por {0}",
                [HtmlExportText.CatsAlt] = "Simba y Mila, los gatos de MeowChan",
                [HtmlExportText.PhotoCredit] = "Logotipos de gato: Simba (naranja) y Mila (gris), cada uno derivado de una fotografía de {0}. Se usan con el permiso del fotógrafo para los proyectos de Tom Graßmann, con la condición de citar su nombre; las fotografías no están cubiertas por la licencia de MeowChan.",
                [HtmlExportText.ThemeHint] = "La elección del tema solo se aplica a esta vista y no se guarda."
            },
            ["fr"] = new Dictionary<HtmlExportText, string>
            {
                [HtmlExportText.LastCapture] = "Dernière capture observée : {0}",
                [HtmlExportText.NoPosts] = "Aucun message capturé",
                [HtmlExportText.Retention] = "Seuls les messages observés sont conservés. Les messages absents de la source n’effacent pas les captures précédentes.",
                [HtmlExportText.ExternalThreads] = "Les fils externes ne sont pas inclus dans ce dossier.",
                [HtmlExportText.UncapturedReferences] = "Les références à des messages hors de l’export capturé restent indisponibles hors ligne.",
                [HtmlExportText.CollectionStart] = "Page d’accueil de la collection",
                [HtmlExportText.Removed] = "Supprimé à la source",
                [HtmlExportText.ExternalReference] = "Référence externe",
                [HtmlExportText.PostNotCaptured] = "Message non capturé",
                [HtmlExportText.OtherThread] = "Autre fil, hors de cet export",
                [HtmlExportText.UnresolvedReference] = "Référence non résolue",
                [HtmlExportText.SavedMedia] = "Médias enregistrés",
                [HtmlExportText.GalleryDescription] = "Aucun texte de message n’a été capturé. Cette galerie contient les originaux actuels publiés dans le manifeste.",
                [HtmlExportText.GalleryOmitted] = "Médias enregistrés omis selon le choix d’exportation.",
                [HtmlExportText.ExportResult] = "Résultat de l’exportation",
                [HtmlExportText.ResultCounts] = "{0} messages capturés · {1} fichiers multimédias copiés · {2} pièces jointes omises",
                [HtmlExportText.VerifiedOffline] = "Les octets des médias ont été vérifiés avec leur manifeste avant la copie. Ce dossier ne nécessite ni profil MeowChan, ni serveur, ni connexion réseau.",
                [HtmlExportText.DeletedAttachment] = "Pièce jointe supprimée",
                [HtmlExportText.SavedFile] = "Fichier enregistré : {0} octets.",
                [HtmlExportText.Imported] = "Fichier d’archive importé ; cela n’implique aucune autorisation locale sur la source.",
                [HtmlExportText.Mismatch] = "Enregistré avec une autorisation spécifique à la source ; la taille ou la somme de contrôle de la source diffère du fichier enregistré.",
                [HtmlExportText.SourceInfo] = "Informations multimédias rapportées par la source",
                [HtmlExportText.Size] = "Taille : {0} octets",
                [HtmlExportText.SizeHint] = "Indication de taille (octets non exacts) : {0} {1} ({2})",
                [HtmlExportText.SavedSha1] = "SHA-1 du fichier enregistré : {0}",
                [HtmlExportText.Unspecified] = "Non spécifié",
                [HtmlExportText.CollectionTitle] = "Collection MeowChan",
                [HtmlExportText.CollectionIntro] = "Captures locales indépendantes de fils, avec des médias vérifiés partagés. Seuls les messages capturés sont inclus.",
                [HtmlExportText.PostCount] = "{0} messages",
                [HtmlExportText.CollectionCounts] = "{0} fils · {1} messages capturés · {2} fichiers multimédias copiés · {3} pièces jointes omises",
                [HtmlExportText.Offline] = "Ne nécessite ni profil MeowChan, ni serveur, ni connexion réseau.",
                [HtmlExportText.OmissionMediaNotIncluded] = "Médias omis selon le choix d’exportation",
                [HtmlExportText.OmissionExcludedByRule] = "Exclu par la règle de téléchargement d’origine",
                [HtmlExportText.OmissionUnavailable] = "Médias enregistrés indisponibles",
                [HtmlExportText.OmissionDifferentVersion] = "Aucune version enregistrée ne correspond à la pièce jointe capturée",
                [HtmlExportText.OmissionVerificationFailed] = "Les médias enregistrés n’ont pas pu être vérifiés ou lus",
                [HtmlExportText.OmissionNoMediaIdentity] = "Aucune identité de média conservée",
                [HtmlExportText.WarningManifestUnavailable] = "Un manifeste de médias enregistré est indisponible.",
                [HtmlExportText.WarningDirectoryUnreadable] = "Un répertoire de médias enregistré n’a pas pu être lu.",
                [HtmlExportText.WarningManifestInvalid] = "Le manifeste des médias est invalide ; aucun média n’a été déduit ni réparé.",
                [HtmlExportText.PostNumber] = "N° {0}",
                [HtmlExportText.Theme] = "Thème",
                [HtmlExportText.ThemeSystem] = "Système",
                [HtmlExportText.ThemeLight] = "Clair",
                [HtmlExportText.ThemeDark] = "Sombre",
                [HtmlExportText.About] = "À propos",
                [HtmlExportText.AboutTitle] = "À propos de cet export",
                [HtmlExportText.AboutClose] = "Fermer",
                [HtmlExportText.AboutSource] = "Source et forum",
                [HtmlExportText.AboutThreadNumber] = "Numéro du fil",
                [HtmlExportText.AboutThreadTitle] = "Titre",
                [HtmlExportText.AboutPosts] = "Messages capturés",
                [HtmlExportText.AboutMedia] = "Fichiers multimédias sur cette page",
                [HtmlExportText.AboutThreads] = "Fils exportés",
                [HtmlExportText.AboutCopiedMedia] = "Fichiers multimédias copiés",
                [HtmlExportText.AboutFirstPost] = "Premier message",
                [HtmlExportText.AboutLastPost] = "Dernier message",
                [HtmlExportText.AboutLastCapture] = "Dernière capture",
                [HtmlExportText.AboutExported] = "Exporté",
                [HtmlExportText.CollectedWith] = "Collecté et exporté avec MeowChan {0}",
                [HtmlExportText.MadeBy] = "MeowChan par {0}",
                [HtmlExportText.CatsAlt] = "Simba et Mila, les chats de MeowChan",
                [HtmlExportText.PhotoCredit] = "Logos de chat : Simba (orange) et Mila (gris), chacun dérivé d'une photographie de {0}. Ils sont utilisés avec l'autorisation du photographe pour les projets de Tom Graßmann, à condition de le citer nommément ; les photographies ne sont pas couvertes par la licence de MeowChan.",
                [HtmlExportText.ThemeHint] = "Le choix du thème ne s’applique qu’à cet affichage et n’est pas enregistré."
            },
            ["ru"] = new Dictionary<HtmlExportText, string>
            {
                [HtmlExportText.LastCapture] = "Последний наблюдаемый снимок: {0}",
                [HtmlExportText.NoPosts] = "Сохранённых публикаций нет",
                [HtmlExportText.Retention] = "Сохраняются только наблюдаемые публикации. Отсутствующие в источнике публикации не удаляют прежние снимки.",
                [HtmlExportText.ExternalThreads] = "Внешние треды не включены в эту папку.",
                [HtmlExportText.UncapturedReferences] = "Ссылки на публикации вне сохранённого экспорта недоступны офлайн.",
                [HtmlExportText.CollectionStart] = "Начальная страница коллекции",
                [HtmlExportText.Removed] = "Удалено в источнике",
                [HtmlExportText.ExternalReference] = "Внешняя ссылка",
                [HtmlExportText.PostNotCaptured] = "Публикация не сохранена",
                [HtmlExportText.OtherThread] = "Другой тред вне этого экспорта",
                [HtmlExportText.UnresolvedReference] = "Неразрешённая ссылка",
                [HtmlExportText.SavedMedia] = "Сохранённые медиафайлы",
                [HtmlExportText.GalleryDescription] = "Текст публикаций не был сохранён. В этой галерее находятся текущие оригиналы из манифеста.",
                [HtmlExportText.GalleryOmitted] = "Сохранённые медиафайлы исключены по выбору экспорта.",
                [HtmlExportText.ExportResult] = "Результат экспорта",
                [HtmlExportText.ResultCounts] = "Публикаций сохранено: {0} · скопировано медиафайлов: {1} · пропущено вложений: {2}",
                [HtmlExportText.VerifiedOffline] = "Байты медиафайлов проверены по манифесту перед копированием. Для этой папки не нужны профиль MeowChan, сервер или сетевое подключение.",
                [HtmlExportText.DeletedAttachment] = "Удалённое вложение",
                [HtmlExportText.SavedFile] = "Сохранённый файл: {0} байт.",
                [HtmlExportText.Imported] = "Импортированный архивный файл; локальное разрешение источника не предполагается.",
                [HtmlExportText.Mismatch] = "Сохранено с разрешением для конкретного источника; размер или контрольная сумма источника отличается от сохранённого файла.",
                [HtmlExportText.SourceInfo] = "Сведения о медиафайле от источника",
                [HtmlExportText.Size] = "Размер: {0} байт",
                [HtmlExportText.SizeHint] = "Подсказка о размере (не точное число байт): {0} {1} ({2})",
                [HtmlExportText.SavedSha1] = "SHA-1 сохранённого файла: {0}",
                [HtmlExportText.Unspecified] = "Не указано",
                [HtmlExportText.CollectionTitle] = "Коллекция MeowChan",
                [HtmlExportText.CollectionIntro] = "Независимые локальные снимки тредов с общими проверенными медиафайлами. Включены только сохранённые публикации.",
                [HtmlExportText.PostCount] = "публикаций: {0}",
                [HtmlExportText.CollectionCounts] = "тредов: {0} · сохранено публикаций: {1} · скопировано медиафайлов: {2} · пропущено вложений: {3}",
                [HtmlExportText.Offline] = "Профиль MeowChan, сервер и сетевое подключение не требуются.",
                [HtmlExportText.OmissionMediaNotIncluded] = "Медиафайлы исключены по выбору экспорта",
                [HtmlExportText.OmissionExcludedByRule] = "Исключено исходным правилом загрузки",
                [HtmlExportText.OmissionUnavailable] = "Сохранённые медиафайлы недоступны",
                [HtmlExportText.OmissionDifferentVersion] = "Нет сохранённой версии, соответствующей захваченному вложению",
                [HtmlExportText.OmissionVerificationFailed] = "Сохранённые медиафайлы не прошли проверку или не могут быть прочитаны",
                [HtmlExportText.OmissionNoMediaIdentity] = "Идентификатор сохранённого медиафайла отсутствует",
                [HtmlExportText.WarningManifestUnavailable] = "Сохранённый манифест медиафайлов недоступен.",
                [HtmlExportText.WarningDirectoryUnreadable] = "Не удалось прочитать сохранённый каталог медиафайлов.",
                [HtmlExportText.WarningManifestInvalid] = "Манифест медиафайлов недействителен; файлы не восстанавливались и не определялись по косвенным признакам.",
                [HtmlExportText.PostNumber] = "№ {0}",
                [HtmlExportText.Theme] = "Тема",
                [HtmlExportText.ThemeSystem] = "Системная",
                [HtmlExportText.ThemeLight] = "Светлая",
                [HtmlExportText.ThemeDark] = "Тёмная",
                [HtmlExportText.About] = "Сведения",
                [HtmlExportText.AboutTitle] = "Об этом экспорте",
                [HtmlExportText.AboutClose] = "Закрыть",
                [HtmlExportText.AboutSource] = "Источник и доска",
                [HtmlExportText.AboutThreadNumber] = "Номер треда",
                [HtmlExportText.AboutThreadTitle] = "Заголовок",
                [HtmlExportText.AboutPosts] = "Сохранённые публикации",
                [HtmlExportText.AboutMedia] = "Медиафайлы на этой странице",
                [HtmlExportText.AboutThreads] = "Экспортированные треды",
                [HtmlExportText.AboutCopiedMedia] = "Скопированные медиафайлы",
                [HtmlExportText.AboutFirstPost] = "Первая публикация",
                [HtmlExportText.AboutLastPost] = "Последняя публикация",
                [HtmlExportText.AboutLastCapture] = "Последний захват",
                [HtmlExportText.AboutExported] = "Экспортировано",
                [HtmlExportText.CollectedWith] = "Собрано и экспортировано с помощью MeowChan {0}",
                [HtmlExportText.MadeBy] = "MeowChan от {0}",
                [HtmlExportText.CatsAlt] = "Simba и Mila, кошки MeowChan",
                [HtmlExportText.PhotoCredit] = "Логотипы с котами: Simba (рыжий) и Mila (серая), каждый получен из фотографии автора {0}. Они используются с разрешения фотографа для проектов Tom Graßmann при условии указания его имени; фотографии не охватываются лицензией MeowChan.",
                [HtmlExportText.ThemeHint] = "Выбор темы действует только для этого просмотра и не сохраняется."
            }
        };

    private static readonly IReadOnlyDictionary<string, HtmlExportText> WarningKeys = new Dictionary<string, HtmlExportText>(StringComparer.Ordinal)
    {
        [English[HtmlExportText.WarningManifestUnavailable]] = HtmlExportText.WarningManifestUnavailable,
        [English[HtmlExportText.WarningDirectoryUnreadable]] = HtmlExportText.WarningDirectoryUnreadable,
        [English[HtmlExportText.WarningManifestInvalid]] = HtmlExportText.WarningManifestInvalid
    };

    private readonly IReadOnlyDictionary<HtmlExportText, string> _strings;

    public HtmlExportLocalization(string? language)
    {
        string code = NormalizeLanguage(language);
        Language = code;
        if (Translations.TryGetValue(code, out IReadOnlyDictionary<HtmlExportText, string>? translated)
            || HtmlExportAdditionalTranslations.All.TryGetValue(code, out translated))
            _strings = translated;
        else
            _strings = English;
    }

    public string Language { get; }

    public string Get(HtmlExportText text) => _strings.TryGetValue(text, out string? value) ? value : English[text];

    public string Html(HtmlExportText text, params object?[] arguments)
    {
        string value = Get(text);
        if (arguments.Length != 0)
            value = string.Format(CultureInfo.InvariantCulture, value, arguments);
        return WebUtility.HtmlEncode(value);
    }

    public string WarningHtml(string warning)
    {
        if (WarningKeys.TryGetValue(warning, out HtmlExportText key))
            return Html(key);
        return WebUtility.HtmlEncode(warning);
    }

    public HtmlExportText Omission(ArchiveExportOmissionReason reason) => reason switch
    {
        ArchiveExportOmissionReason.MediaNotIncluded => HtmlExportText.OmissionMediaNotIncluded,
        ArchiveExportOmissionReason.ExcludedByRule => HtmlExportText.OmissionExcludedByRule,
        ArchiveExportOmissionReason.Unavailable => HtmlExportText.OmissionUnavailable,
        ArchiveExportOmissionReason.DifferentVersion => HtmlExportText.OmissionDifferentVersion,
        ArchiveExportOmissionReason.VerificationFailed => HtmlExportText.OmissionVerificationFailed,
        _ => HtmlExportText.OmissionNoMediaIdentity
    };

    private static string NormalizeLanguage(string? language)
    {
        if (string.IsNullOrWhiteSpace(language) || language.Length > 35) return "en";
        string tag = language.Trim();
        foreach (char character in tag)
            if (!char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_')) return "en";
        string normalized = tag.Replace('_', '-');
        string code = normalized.Split('-', 2)[0].ToLowerInvariant();
        if (code is "de" or "es" or "fr" or "ru" or "cs" or "it" or "ja" or "ko" or "pl")
            return code;
        if (code == "pt" && string.Equals(normalized, "pt-BR", StringComparison.OrdinalIgnoreCase))
            return "pt-BR";
        if (code == "zh" && string.Equals(normalized, "zh-Hans", StringComparison.OrdinalIgnoreCase))
            return "zh-Hans";
        return "en";
    }
}
