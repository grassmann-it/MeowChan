using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using Microsoft.Data.Sqlite;

namespace MeowChan.Archive;

internal static partial class ArchiveSql
{
    internal static ArchiveFilenamePage SearchFilenames(SqliteConnection connection, SqliteTransaction transaction,
        ArchiveFilenameSearch query, int offset, int limit, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Same CLR literal comparison as PrepareSearch/meowchan_contains. SQLite NOCASE/LIKE
        // are not Unicode OrdinalIgnoreCase. Two arguments keep query text parameter-bound.
        connection.CreateFunction<string?, string, bool>("meowchan_filename_contains", (value, text) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return value != null && value.Contains(text, StringComparison.OrdinalIgnoreCase);
        });
        string revision = GetPragma(connection, "user_version", transaction) >= 4 ? "a.attachment_revision_key" : "NULL";
        const string from = """
             FROM archive_attachments a
             JOIN archive_posts p ON p.source=a.source AND p.board=a.board AND p.thread=a.thread AND p.post=a.post
             JOIN archive_threads t ON t.source=a.source AND t.board=a.board AND t.thread=a.thread
             WHERE a.original_name IS NOT NULL AND meowchan_filename_contains(a.original_name,$text)
            """;
        using SqliteCommand total = Command(connection, transaction, "SELECT count(*)" + from);
        total.Parameters.AddWithValue("$text", query.Text);
        long count = Convert.ToInt64(total.ExecuteScalar(), CultureInfo.InvariantCulture);
        cancellationToken.ThrowIfCancellationRequested();
        using SqliteCommand page = Command(connection, transaction,
            "SELECT a.source,a.board,a.thread,a.post,a.position," + revision + """
            ,a.variant,a.original_name,p.is_deleted,a.is_deleted,t.revision
            """ + from + """
             ORDER BY a.source COLLATE BINARY,a.board COLLATE BINARY,a.thread COLLATE BINARY,
                 a.post COLLATE BINARY,a.position
             LIMIT $limit OFFSET $offset
            """);
        page.Parameters.AddWithValue("$text", query.Text);
        page.Parameters.AddWithValue("$limit", limit);
        page.Parameters.AddWithValue("$offset", offset);
        using SqliteDataReader rows = page.ExecuteReader();
        List<ArchiveFilenameHit> items = new();
        while (rows.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            items.Add(new(new(rows.GetString(0), rows.GetString(1), rows.GetString(2)), rows.GetString(3),
                rows.GetInt32(4), rows.IsDBNull(5) ? null : rows.GetString(5), rows.GetString(6), rows.GetString(7),
                rows.GetInt32(8) != 0, rows.GetInt32(9) != 0, rows.GetInt64(10)));
        }
        cancellationToken.ThrowIfCancellationRequested();
        return new(Array.AsReadOnly(items.ToArray()), count, offset);
    }
}
