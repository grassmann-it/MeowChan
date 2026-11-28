using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace MeowChan.Archive;

public sealed partial class ArchiveReader
{
    internal const int MediaIndexPageSize = 200;

    /// <summary>Reads a page and its historical media directories in the same catalog snapshot.</summary>
    internal Task<(ArchiveThreadPage Page, IReadOnlyList<string>[]? Directories)> ReadMediaIndexPageAsync(int offset,
        CancellationToken cancellationToken)
    {
        ArchiveValidation.ValidatePage(offset, MediaIndexPageSize);
        return ReadAsync((connection, transaction) =>
        {
            ArchiveThreadPage page = ArchiveSql.ReadPage(connection, transaction, offset, MediaIndexPageSize);
            IReadOnlyList<string>[]? directories;
            try
            {
                directories = ReadMediaIndexDirectories(connection, transaction, page.Items, cancellationToken);
            }
            catch (Exception error) when (error is SqliteException or IOException or InvalidDataException or UnauthorizedAccessException)
            {
                // On a batch failure the caller can recover readable threads and report failures individually.
                directories = null;
            }
            return (page, directories);
        }, cancellationToken);
    }

    private static IReadOnlyList<string>[] ReadMediaIndexDirectories(SqliteConnection connection, SqliteTransaction transaction,
        IReadOnlyList<ArchiveThreadSummary> threads, CancellationToken cancellationToken)
    {
        if (threads.Count == 0) return [];

        string[] keys = new string[threads.Count];
        using SqliteCommand command = ArchiveSql.Command(connection, transaction, "");
        for (int index = 0; index < threads.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ResourceId id = threads[index].Id;
            ArchiveValidation.ValidateId(id);
            string suffix = index.ToString(CultureInfo.InvariantCulture);
            keys[index] = $"({suffix},$source{suffix},$board{suffix},$thread{suffix})";
            command.Parameters.AddWithValue("$source" + suffix, id.Source);
            command.Parameters.AddWithValue("$board" + suffix, id.Board);
            command.Parameters.AddWithValue("$thread" + suffix, id.Thread!);
        }
        command.CommandText = $"""
            WITH requested(ordinal,source,board,thread) AS (VALUES {string.Join(",", keys)})
            SELECT DISTINCT requested.ordinal,d.directory
            FROM requested CROSS JOIN archive_media_directories d
            WHERE d.source=requested.source AND d.board=requested.board AND d.thread=requested.thread
            ORDER BY requested.ordinal,d.directory
            """;
        List<string>?[] collected = new List<string>?[threads.Count];
        using SqliteDataReader rows = command.ExecuteReader();
        while (rows.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            (collected[rows.GetInt32(0)] ??= new List<string>()).Add(rows.GetString(1));
        }
        IReadOnlyList<string>[] result = new IReadOnlyList<string>[threads.Count];
        for (int index = 0; index < result.Length; index++)
            result[index] = collected[index] is { } folders ? folders.AsReadOnly() : Array.Empty<string>();
        return result;
    }
}
