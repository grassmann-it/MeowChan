using System;
using System.Linq;
using MeowChan.Desktop.Localization;

namespace MeowChan.Desktop;

internal static class ArchiveCleanupText
{
    internal static string Get(string en, string de) => Texts.Language == "de" ? de : en;
    internal static string Title => Get("Clean up archive", "Archiv aufräumen");
    internal static string Identity(ResourceId id) => $"{id.Source} /{id.Board}/ {id.Thread}";
    internal static string State(ArchiveCleanupEligibility value) => value switch
    {
        ArchiveCleanupEligibility.Candidate => Get("Candidate", "Kandidat"),
        ArchiveCleanupEligibility.Protected => Get("Protected", "Geschützt"),
        ArchiveCleanupEligibility.Active => Get("Unfinished work", "Unerledigter Auftrag"),
        ArchiveCleanupEligibility.Kept => Get("Keep: more than 2 saved versions", "Behalten: mehr als 2 gespeicherte Fassungen"),
        _ => Get("Unknown — cannot select", "Unbekannt — nicht auswählbar")
    };
    internal static string Reason(ArchiveCleanupReason value) => value switch
    {
        ArchiveCleanupReason.ThreadFavorite => Get("Thread favorite", "Threadfavorit"),
        ArchiveCleanupReason.MediaFavorite => Get("Media favorite (including older versions)", "Medienfavorit (auch ältere Fassungen)"),
        ArchiveCleanupReason.MediaRating => Get("Rated media (including older versions)", "Bewertetes Medium (auch ältere Fassungen)"),
        ArchiveCleanupReason.PostBookmark => Get("Post bookmark or note", "Beitragslesezeichen oder Notiz"),
        ArchiveCleanupReason.ReaderGroup => Get("Reader group member", "Mitglied einer Readergruppe"),
        ArchiveCleanupReason.ReadingPosition => Get("Saved reading position", "Gespeicherte Lesestelle"),
        ArchiveCleanupReason.OpenView => Get("Open in a reader, gallery or export", "In Reader, Galerie oder Export geöffnet"),
        ArchiveCleanupReason.ActiveWatch => Get("Active or paused watch", "Aktiver oder pausierter Auftrag"),
        ArchiveCleanupReason.PendingWork => Get("Pending work or intervention required", "Unerledigte Arbeit oder Eingriff erforderlich"),
        ArchiveCleanupReason.SharedDirectory => Get("Storage shared with another thread", "Speicher mit anderem Thread geteilt"),
        ArchiveCleanupReason.MediaUnknown => Get("Saved media or storage location not verified", "Medienbestand oder Speicherort nicht bestätigt"),
        ArchiveCleanupReason.ProtectionUnknown => Get("Protection data could not be verified", "Schutzdaten konnten nicht geprüft werden"),
        ArchiveCleanupReason.WorkUnknown => Get("Work state could not be verified", "Auftragsstand konnte nicht geprüft werden"),
        ArchiveCleanupReason.MoreThanTwoAssets => Get("More than 2 saved asset versions", "Mehr als 2 gespeicherte Assetfassungen"),
        _ => Get("State changed — check again", "Stand geändert — neu prüfen")
    };
    internal static string Media(ArchiveCleanupPreviewRow row) => row.Media.SavedAssetCount is int count
        ? Get($"{count:N0} saved versions · {row.Media.VerifiedBytes:N0} bytes · {row.PostCount:N0} posts",
            $"{count:N0} gespeicherte Fassungen · {row.Media.VerifiedBytes:N0} Bytes · {row.PostCount:N0} Beiträge")
        : Get($"Saved versions: unknown · {row.PostCount:N0} posts", $"Gespeicherte Fassungen: unbekannt · {row.PostCount:N0} Beiträge");
    internal static string Reasons(ArchiveCleanupPreviewRow row) => string.Join(" · ", row.Reasons.Select(Reason));
    internal static string Stale => Get("State changed — check again. Nothing else is selected automatically.",
        "Stand geändert — neu prüfen. Es wird nichts anderes automatisch ausgewählt.");
}
