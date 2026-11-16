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
            (rows, _) => new ResourceId(rows.GetString(0), rows.GetString(1), rows.GetString(2)), cancellationToken);

    internal static IReadOnlyList<ArchiveThreadSummary> SearchSummaries(SqliteConnection connection, SqliteTransaction transaction,
        ArchiveSearchRequest request, CancellationToken cancellationToken)
        => ReadAllSearchRows(connection, transaction, request, SummaryColumns, (rows, _) => ReadSummaryRow(rows), cancellationToken);

    /// <summary>All matches in order with their match tier (null unless the request ranks by tier).</summary>
    internal static IReadOnlyList<ArchiveRankedThread> SearchRankedSummaries(SqliteConnection connection, SqliteTransaction transaction,
        ArchiveSearchRequest request, CancellationToken cancellationToken)
        => ReadAllSearchRows(connection, transaction, request, SummaryColumns,
            (rows, tiered) => new ArchiveRankedThread(ReadSummaryRow(rows), tiered ? ReadTier(rows, 12) : null), cancellationToken);

    private static ArchiveSearchMatchTier ReadTier(SqliteDataReader rows, int ordinal)
    {
        ArchiveSearchMatchTier tier = (ArchiveSearchMatchTier)rows.GetInt32(ordinal);
        return Enum.IsDefined(tier) ? tier : throw new System.IO.InvalidDataException("Unknown search match tier.");
    }

    private static IReadOnlyList<T> ReadAllSearchRows<T>(SqliteConnection connection, SqliteTransaction transaction,
        ArchiveSearchRequest request, string columns, Func<SqliteDataReader, bool, T> readRow, CancellationToken cancellationToken)
    {
        // A full result is a single ordered stream in the caller's read snapshot. Reusing
        // SearchPage here would repeat COUNT and sort/discard every preceding OFFSET for each
        // 500 rows. ID consumers also do not need titles, dates or correlated post counts.
        ArchiveSearchCompiler compiled = PrepareSearch(connection, transaction, request, cancellationToken);
        using SqliteCommand command = Command(connection, transaction,
            $"{compiled.With}SELECT {columns}{compiled.TierColumn} FROM archive_threads t {compiled.Where} ORDER BY {compiled.Order}");
        compiled.Bind(command);
        cancellationToken.ThrowIfCancellationRequested();
        using SqliteDataReader rows = command.ExecuteReader();
        List<T> items = new();
        while (rows.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            items.Add(readRow(rows, compiled.Tiered));
        }
        cancellationToken.ThrowIfCancellationRequested();
        return Array.AsReadOnly(items.ToArray());
    }

    private static ArchiveSearchCompiler PrepareSearch(SqliteConnection connection, SqliteTransaction transaction,
        ArchiveSearchRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        bool indexed = GetPragma(connection, "user_version", transaction) >= 2;
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
        // The opening post is the thread's first retained post in reader order (provider order, then id): one
        // indexed lookup in archive_post_order, the same post the list preview shows.
        const string OpeningPost = "(SELECT o.rowid FROM archive_posts o WHERE o.source=t.source AND o.board=t.board AND o.thread=t.thread ORDER BY o.ordinal,o.post LIMIT 1)";
        ArchiveSearchScope scope = request.Spec.Scope;
        string with = "", match, inThread, inOpening;
        if (text.Length == 0) { match = "1"; inThread = inOpening = "0"; }
        else
        {
            string inAnyPost;
            if (fullText)
            {
                // Each FTS match is materialized once per statement and probed by rowid, however often the
                // predicate and the tier refer to it.
                with = """
                    WITH search_thread_hits(rowid) AS MATERIALIZED (SELECT rowid FROM archive_thread_search WHERE archive_thread_search MATCH $match),
                    search_post_hits(rowid) AS MATERIALIZED (SELECT rowid FROM archive_post_search WHERE archive_post_search MATCH $match)

                    """;
                inThread = "t.rowid IN search_thread_hits";
                inOpening = OpeningPost + " IN search_post_hits";
                inAnyPost = """
                    t.rowid IN (SELECT t2.rowid FROM search_post_hits h JOIN archive_posts p ON p.rowid=h.rowid
                      JOIN archive_threads t2 ON t2.source=p.source AND t2.board=p.board AND t2.thread=p.thread)
                    """;
            }
            else
            {
                inThread = $"meowchan_contains({threadText})";
                inOpening = $"EXISTS (SELECT 1 FROM archive_posts p WHERE p.rowid={OpeningPost} AND meowchan_contains({postText}))";
                inAnyPost = $"EXISTS (SELECT 1 FROM archive_posts p WHERE p.source=t.source AND p.board=t.board AND p.thread=t.thread AND meowchan_contains({postText}))";
            }
            match = scope switch
            {
                ArchiveSearchScope.Title => inThread,
                ArchiveSearchScope.TitleAndOpeningPost => $"({inThread} OR {inOpening})",
                _ => $"({inThread} OR {inAnyPost})"
            };
        }
        // Tier 0: the thread record (title, source, board, number); 1: the opening post; 2: only other posts.
        // Every row already matched, so a row that is neither 0 nor 1 matched in another post.
        string? tier = request.Spec.RanksByMatchTier ? $"(CASE WHEN {inThread} THEN 0 WHEN {inOpening} THEN 1 ELSE 2 END)" : null;
        // Supported catalogs write canonical O-format UTC. Normalize any valid offset-based
        // legacy O value without mutating the passive catalog, preserving all seven fraction digits.
        // Invalid dates fail visibly instead of silently disappearing from a date-filtered result.
        if (request.DateWindow != null)
            connection.CreateFunction<string, string>("meowchan_utc", value =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Date(DateTimeOffset.ParseExact(value, "O", CultureInfo.InvariantCulture));
            });
        return new ArchiveSearchCompiler(request, with, match, tier, fullText);
    }

    internal static ArchiveThreadPage SearchPage(SqliteConnection connection, SqliteTransaction transaction,
        ArchiveSearchRequest request, int offset, int limit, CancellationToken cancellationToken)
    {
        ArchiveSearchCompiler compiled = PrepareSearch(connection, transaction, request, cancellationToken);
        string where = compiled.Where;
        string order = compiled.Order;
        int count;
        IReadOnlyList<int>? tierCounts = null;
        if (compiled.Tiered)
        {
            // One grouped count gives the total and each tier's size in the same snapshot as the page.
            using SqliteCommand tiers = Command(connection, transaction,
                $"{compiled.With}SELECT match_tier,count(*) FROM (SELECT 1{compiled.TierColumn} FROM archive_threads t {where}) GROUP BY match_tier");
            compiled.Bind(tiers);
            int[] counts = new int[3];
            using (SqliteDataReader grouped = tiers.ExecuteReader())
                while (grouped.Read())
                    counts[(int)ReadTier(grouped, 0)] = checked(Convert.ToInt32(grouped.GetInt64(1), CultureInfo.InvariantCulture));
            count = checked(counts[0] + counts[1] + counts[2]);
            tierCounts = Array.AsReadOnly(counts);
        }
        else
        {
            using SqliteCommand total = Command(connection, transaction, $"{compiled.With}SELECT count(*) FROM archive_threads t " + where);
            compiled.Bind(total);
            count = checked(Convert.ToInt32(total.ExecuteScalar(), CultureInfo.InvariantCulture));
        }
        cancellationToken.ThrowIfCancellationRequested();
        using SqliteCommand page = Command(connection, transaction, $"{compiled.With}SELECT {SummaryColumns}{compiled.TierColumn} FROM archive_threads t {where} ORDER BY {order} LIMIT $limit OFFSET $offset");
        compiled.Bind(page);
        page.Parameters.AddWithValue("$limit", limit);
        page.Parameters.AddWithValue("$offset", offset);
        using SqliteDataReader rows = page.ExecuteReader();
        List<ArchiveThreadSummary> items = new List<ArchiveThreadSummary>();
        List<ArchiveSearchMatchTier> itemTiers = new();
        while (rows.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            items.Add(ReadSummaryRow(rows));
            if (compiled.Tiered) itemTiers.Add(ReadTier(rows, 12));
        }
        return new ArchiveThreadPage(Array.AsReadOnly(items.ToArray()), count, offset)
        {
            MatchTiers = compiled.Tiered ? Array.AsReadOnly(itemTiers.ToArray()) : null,
            MatchTierCounts = tierCounts
        };
    }
}
