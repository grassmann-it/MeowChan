using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using MeowChan.Interfaces;

namespace MeowChan.Archive;

public sealed partial class ArchiveCatalog
{
    /// <summary>Previews existing selections and directories without changing catalog or media files.</summary>
    public Task<ArchiveDeletionPlan> PrepareDeletionAsync(IReadOnlyCollection<ResourceId> ids,
        CancellationToken cancellationToken = default)
    {
        ResourceId[] selected = CopySelection(ids);
        return ReadAsync(connection =>
        {
            using SqliteTransaction transaction = connection.BeginTransaction(deferred: true);
            IReadOnlyList<ArchiveDeletedThread> threads = ReadSelected(connection, transaction, selected, cancellationToken);
            HashSet<ResourceId> selectedIds = new HashSet<ResourceId>(selected);
            List<string> shared = new List<string>();
            StringComparer paths = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
            HashSet<string> retainedDirectories = new HashSet<string>(paths);
            using (SqliteCommand owners = ArchiveSql.Command(connection, transaction, """
                SELECT source,board,thread,directory FROM archive_media_directories
                UNION SELECT source,board,thread,media_directory FROM archive_threads
                """))
            using (SqliteDataReader rows = owners.ExecuteReader())
                while (rows.Read())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    ResourceId owner = new ResourceId(rows.GetString(0), rows.GetString(1), rows.GetString(2));
                    if (!selectedIds.Contains(owner)) retainedDirectories.Add(CanonicalDeletionDirectory(rows.GetString(3)));
                }
            foreach (string directory in threads.SelectMany(item => item.MediaDirectories).Distinct(StringComparer.Ordinal))
                if (retainedDirectories.Contains(CanonicalDeletionDirectory(directory))) shared.Add(directory);
            cancellationToken.ThrowIfCancellationRequested();
            transaction.Commit();
            return new ArchiveDeletionPlan(threads, Array.AsReadOnly(shared.ToArray()));
        }, cancellationToken);
    }

    /// <summary>
    /// Removes the selected thread identities and all of their catalog-owned data in one transaction.
    /// Returns the existing threads and their historical media directories for caller-owned file cleanup.
    /// References from retained posts to removed threads remain as unresolved quotes.
    /// </summary>
    public Task<IReadOnlyList<ArchiveDeletedThread>> DeleteThreadsAsync(IReadOnlyCollection<ResourceId> ids,
        CancellationToken cancellationToken = default)
    {
        ResourceId[] selected = CopySelection(ids);
        return WriteAsync<IReadOnlyList<ArchiveDeletedThread>>((connection, transaction) =>
        {
            IReadOnlyList<ArchiveDeletedThread> deleted = ReadSelected(connection, transaction, selected, cancellationToken);
            foreach (ResourceId id in selected)
            {
                cancellationToken.ThrowIfCancellationRequested();
                // media_decisions has its own identity columns and no foreign key to archive_threads.
                DeleteRows(connection, transaction, id, "media_decisions", "source_id", "board_id", "thread_id");
                DeleteRows(connection, transaction, id, "archive_attachments");
                // Deleting posts cascades origin quotes; FTS delete triggers remove their indexed text.
                DeleteRows(connection, transaction, id, "archive_posts");
                DeleteRows(connection, transaction, id, "archive_media_directories");
                // Its FTS delete trigger removes the thread's indexed title and identity.
                DeleteRows(connection, transaction, id, "archive_threads");
            }
            return deleted;
        }, cancellationToken);
    }

    private static string CanonicalDeletionDirectory(string directory)
    {
        try { return Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)); }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        { return directory; } // Invalid media paths are blocked by the caller's file inventory.
    }

    private static ResourceId[] CopySelection(IReadOnlyCollection<ResourceId> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        ResourceId[] selected = ids.ToArray();
        foreach (ResourceId id in selected) ArchiveValidation.ValidateId(id);
        return selected.Distinct().ToArray();
    }

    private static IReadOnlyList<ArchiveDeletedThread> ReadSelected(SqliteConnection connection,
        SqliteTransaction transaction, IReadOnlyList<ResourceId> selected, CancellationToken cancellationToken)
    {
        List<ArchiveDeletedThread> threads = new List<ArchiveDeletedThread>();
        foreach (ResourceId id in selected)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ArchiveThreadSummary? summary = ArchiveSql.ReadSummary(connection, transaction, id);
            if (summary == null) continue;
            List<string> directories = ArchiveSql.ReadMediaDirectories(connection, transaction, id).ToList();
            if (!directories.Contains(summary.MediaDirectory, StringComparer.Ordinal))
                directories.Add(summary.MediaDirectory);
            threads.Add(new ArchiveDeletedThread(summary, Array.AsReadOnly(directories.ToArray())));
        }
        return Array.AsReadOnly(threads.ToArray());
    }

    private static void DeleteRows(SqliteConnection connection, SqliteTransaction transaction, ResourceId id,
        string table, string source = "source", string board = "board", string thread = "thread")
    {
        using SqliteCommand command = ArchiveSql.Command(connection, transaction,
            $"DELETE FROM {table} WHERE {source}=$source AND {board}=$board AND {thread}=$thread");
        ArchiveSql.AddId(command, id);
        command.ExecuteNonQuery();
    }
}
