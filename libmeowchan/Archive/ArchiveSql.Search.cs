using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using Microsoft.Data.Sqlite;

namespace MeowChan.Archive;

internal static partial class ArchiveSql
{
    internal static ArchiveThreadPage SearchPage(SqliteConnection connection, SqliteTransaction transaction,
        ArchiveThreadQuery query, int offset, int limit, CancellationToken cancellationToken)
    {
        bool indexed = GetPragma(connection, "user_version") >= 2;
        string text = query.Text.Trim();
        // FTS trigram has no matches for one/two-character terms. Those terms, and passive
        // schema-1 reads, use a cancellable literal scan instead of inventing empty results.
        bool fullText = indexed && System.Globalization.StringInfo.ParseCombiningCharacters(text).Length >= 3;
        connection.CreateFunction<string?, bool>("meowchan_contains", value =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return (value ?? string.Empty).Contains(text, StringComparison.OrdinalIgnoreCase);
        });
        connection.CreateCollation("meowchan_case", StringComparer.OrdinalIgnoreCase.Compare);
        string threadText = indexed ? "t.search_text" : "t.source||char(10)||t.board||char(10)||t.thread||char(10)||meowchan_text(t.title)";
        string postText = indexed ? "p.search_text" : "p.post||char(10)||meowchan_text(p.subject)||char(10)||meowchan_text(p.author)||char(10)||meowchan_text(p.html)";
        string match = text.Length == 0 ? "1" : fullText ? """
            (t.rowid IN (SELECT rowid FROM archive_thread_search WHERE archive_thread_search MATCH $match)
              OR t.rowid IN (SELECT t2.rowid FROM archive_post_search f JOIN archive_posts p ON p.rowid=f.rowid
                JOIN archive_threads t2 ON t2.source=p.source AND t2.board=p.board AND t2.thread=p.thread
                WHERE archive_post_search MATCH $match))
            """ : $"(meowchan_contains({threadText}) OR EXISTS (SELECT 1 FROM archive_posts p WHERE p.source=t.source AND p.board=t.board AND p.thread=t.thread AND meowchan_contains({postText})))";
        string where = $"""
            WHERE ($sourceFilter IS NULL OR t.source=$sourceFilter) AND ($boardFilter IS NULL OR t.board=$boardFilter)
              AND ($stateFilter IS NULL OR t.source_state=$stateFilter) AND ($captureFilter IS NULL OR t.capture_enabled=$captureFilter)
              AND ($postsFilter IS NULL OR EXISTS(SELECT 1 FROM archive_posts p WHERE p.source=t.source AND p.board=t.board AND p.thread=t.thread)=$postsFilter)
              AND {match}
            """;
        string order = query.Sort switch
        {
            ArchiveThreadSort.OldestFirst => "t.first_seen_at ASC,t.source,t.board,t.thread",
            ArchiveThreadSort.Title => "meowchan_text(t.title) COLLATE meowchan_case ASC,t.source,t.board,t.thread",
            _ => "COALESCE(t.last_captured_at,t.first_seen_at) DESC,t.source,t.board,t.thread"
        };
        void Parameters(SqliteCommand command)
        {
            command.Parameters.AddWithValue("$sourceFilter", (object?)query.Source ?? DBNull.Value);
            command.Parameters.AddWithValue("$boardFilter", (object?)query.Board ?? DBNull.Value);
            command.Parameters.AddWithValue("$stateFilter", query.SourceState.HasValue ? (int)query.SourceState.Value : DBNull.Value);
            command.Parameters.AddWithValue("$captureFilter", query.CaptureEnabled.HasValue ? query.CaptureEnabled.Value ? 1 : 0 : DBNull.Value);
            command.Parameters.AddWithValue("$postsFilter", query.HasPosts.HasValue ? query.HasPosts.Value ? 1 : 0 : DBNull.Value);
            if (fullText && text.Length > 0) command.Parameters.AddWithValue("$match", "\"" + text.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"");
        }
        using SqliteCommand total = Command(connection, transaction, "SELECT count(*) FROM archive_threads t " + where);
        Parameters(total);
        int count = checked(Convert.ToInt32(total.ExecuteScalar(), CultureInfo.InvariantCulture));
        cancellationToken.ThrowIfCancellationRequested();
        using SqliteCommand page = Command(connection, transaction, $"SELECT {SummaryColumns} FROM archive_threads t {where} ORDER BY {order} LIMIT $limit OFFSET $offset");
        Parameters(page);
        page.Parameters.AddWithValue("$limit", limit);
        page.Parameters.AddWithValue("$offset", offset);
        using SqliteDataReader rows = page.ExecuteReader();
        List<ArchiveThreadSummary> items = new List<ArchiveThreadSummary>();
        while (rows.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            items.Add(ReadSummaryRow(rows));
        }
        return new ArchiveThreadPage(Array.AsReadOnly(items.ToArray()), count, offset);
    }
}
