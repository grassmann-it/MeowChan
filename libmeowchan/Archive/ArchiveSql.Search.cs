using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using Microsoft.Data.Sqlite;

namespace MeowChan.Archive;

internal static partial class ArchiveSql
{
    internal static IReadOnlyList<ResourceId> SearchIds(SqliteConnection connection, SqliteTransaction transaction,
        ArchiveSearchRequest request, CancellationToken cancellationToken)
        => ReadAllSearchRows(connection, transaction, request, "t.source,t.board,t.thread",
            rows => new ResourceId(rows.GetString(0), rows.GetString(1), rows.GetString(2)), cancellationToken);

    internal static IReadOnlyList<ArchiveThreadSummary> SearchSummaries(SqliteConnection connection, SqliteTransaction transaction,
        ArchiveSearchRequest request, CancellationToken cancellationToken)
        => ReadAllSearchRows(connection, transaction, request, SummaryColumns, ReadSummaryRow, cancellationToken);

    private static IReadOnlyList<T> ReadAllSearchRows<T>(SqliteConnection connection, SqliteTransaction transaction,
        ArchiveSearchRequest request, string columns, Func<SqliteDataReader, T> readRow, CancellationToken cancellationToken)
    {
        // A full result is a single ordered stream in the caller's read snapshot. Reusing
        // SearchPage here would repeat COUNT and sort/discard every preceding OFFSET for each
        // 500 rows. ID consumers also do not need titles, dates or correlated post counts.
        ArchiveSearchCompiler compiled = PrepareSearch(connection, request, cancellationToken);
        using SqliteCommand command = Command(connection, transaction,
            $"SELECT {columns} FROM archive_threads t {compiled.Where} ORDER BY {compiled.Order}");
        compiled.Bind(command);
        cancellationToken.ThrowIfCancellationRequested();
        using SqliteDataReader rows = command.ExecuteReader();
        List<T> items = new();
        while (rows.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            items.Add(readRow(rows));
        }
        cancellationToken.ThrowIfCancellationRequested();
        return Array.AsReadOnly(items.ToArray());
    }

    private static ArchiveSearchCompiler PrepareSearch(SqliteConnection connection,
        ArchiveSearchRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        bool indexed = GetPragma(connection, "user_version") >= 2;
        string text = request.Spec.Text.Trim();
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
        // Supported catalogs write canonical O-format UTC. Normalize any valid offset-based
        // legacy O value without mutating the passive catalog, preserving all seven fraction digits.
        // Invalid dates fail visibly instead of silently disappearing from a date-filtered result.
        if (request.DateWindow != null)
            connection.CreateFunction<string, string>("meowchan_utc", value =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Date(DateTimeOffset.ParseExact(value, "O", CultureInfo.InvariantCulture));
            });
        return new ArchiveSearchCompiler(request, match, fullText);
    }

    internal static ArchiveThreadPage SearchPage(SqliteConnection connection, SqliteTransaction transaction,
        ArchiveSearchRequest request, int offset, int limit, CancellationToken cancellationToken)
    {
        ArchiveSearchCompiler compiled = PrepareSearch(connection, request, cancellationToken);
        string where = compiled.Where;
        string order = compiled.Order;
        using SqliteCommand total = Command(connection, transaction, "SELECT count(*) FROM archive_threads t " + where);
        compiled.Bind(total);
        int count = checked(Convert.ToInt32(total.ExecuteScalar(), CultureInfo.InvariantCulture));
        cancellationToken.ThrowIfCancellationRequested();
        using SqliteCommand page = Command(connection, transaction, $"SELECT {SummaryColumns} FROM archive_threads t {where} ORDER BY {order} LIMIT $limit OFFSET $offset");
        compiled.Bind(page);
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
