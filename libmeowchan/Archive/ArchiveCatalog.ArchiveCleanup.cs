using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace MeowChan.Archive;

public sealed partial class ArchiveCatalog
{
    // Only the cleanup host calls this while holding its resource/target reservation.
    // Recheck summary, historical paths and every other owner in the deletion transaction.
    internal Task<bool> DeleteCleanupThreadAsync(ArchiveDeletedThread expected, CancellationToken token = default)
        => WriteAsync((connection, transaction) =>
        {
            ResourceId id = expected.Summary.Id;
            IReadOnlyList<ArchiveDeletedThread> current = ReadSelected(connection, transaction, new[] { id }, token);
            if (current.Count != 1 || current[0].Summary != expected.Summary
                || !current[0].MediaDirectories.Order(StringComparer.Ordinal).SequenceEqual(
                    expected.MediaDirectories.Order(StringComparer.Ordinal)))
                throw new InvalidDataException("The archive changed after cleanup confirmation.");
            StringComparer paths = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
            HashSet<string> selectedPaths = expected.MediaDirectories.Select(CanonicalDeletionDirectory).ToHashSet(paths);
            using (SqliteCommand owners = ArchiveSql.Command(connection, transaction, """
                SELECT source,board,thread,directory FROM archive_media_directories
                UNION SELECT source,board,thread,media_directory FROM archive_threads
                """))
            using (SqliteDataReader rows = owners.ExecuteReader())
                while (rows.Read())
                    if (new ResourceId(rows.GetString(0), rows.GetString(1), rows.GetString(2)) != id
                        && selectedPaths.Contains(CanonicalDeletionDirectory(rows.GetString(3))))
                        throw new InvalidDataException("The media directory has another archive owner.");
            DeleteRows(connection, transaction, id, "media_decisions", "source_id", "board_id", "thread_id");
            DeleteRows(connection, transaction, id, "archive_attachments");
            DeleteRows(connection, transaction, id, "archive_posts");
            DeleteRows(connection, transaction, id, "archive_media_directories");
            DeleteRows(connection, transaction, id, "archive_threads");
            return true;
        }, token);
}
