using System;
using Microsoft.Data.Sqlite;

namespace MeowChan.Archive;

internal static partial class ArchiveSql
{
    /// <summary>
    /// Checks one primary-key row after the import fence matches, without loading text,
    /// counting posts or materializing the post/attachment graph. The global fence covers
    /// changes to rows and the root set; filesystem generations are checked separately.
    /// </summary>
    internal static bool IsMediaMergeTargetFresh(SqliteConnection connection, SqliteTransaction transaction,
        ResourceId id, long expectedRevision, string expectedPrimaryDirectory)
    {
        ArgumentNullException.ThrowIfNull(expectedPrimaryDirectory);
        using SqliteCommand command = Command(connection, transaction, """
            SELECT EXISTS (
                SELECT 1 FROM archive_threads
                WHERE source=$source AND board=$board AND thread=$thread
                    AND revision=$revision AND media_directory=$directory COLLATE BINARY
            )
            """);
        AddId(command, id);
        command.Parameters.AddWithValue("$revision", expectedRevision);
        command.Parameters.AddWithValue("$directory", expectedPrimaryDirectory);
        return Convert.ToInt64(command.ExecuteScalar()) == 1;
    }
}
