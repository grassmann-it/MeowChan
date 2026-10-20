using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using Microsoft.Data.Sqlite;

namespace MeowChan.Archive;

internal static partial class ArchiveSql
{
    internal static ArchiveQuotePage ReadQuotes(SqliteConnection connection, SqliteTransaction transaction,
        ArchivePostId post, bool backlinks, int offset, int limit, CancellationToken cancellationToken)
    {
        if (GetPragma(connection, "user_version") == 1)
        {
            // A passive legacy reader neither migrates nor creates an index. This compatibility
            // path scans retained local posts; the active catalog writer upgrades it transactionally.
            List<ArchiveQuote> found = new List<ArchiveQuote>();
            if (!backlinks)
            {
                ArchivedThreadView? view = ReadThread(connection, transaction, post.Thread);
                CapturedPost? origin = view?.Posts.FirstOrDefault(item => item.PostId == post.PostId);
                if (view != null && origin != null) found.AddRange(ArchiveQuoteParser.Parse(post.Thread, view.Summary.Url, origin));
            }
            else
            {
                int total = ReadPage(connection, transaction, 0, 1).TotalCount;
                for (int start = 0; start < total; start += 500)
                {
                    foreach (ArchiveThreadSummary summary in ReadPage(connection, transaction, start, 500).Items)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        ArchivedThreadView view = ReadThread(connection, transaction, summary.Id)!;
                        foreach (CapturedPost item in view.Posts)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            found.AddRange(ArchiveQuoteParser.Parse(summary.Id, summary.Url, item).Where(quote => quote.Target == post));
                        }
                    }
                }
            }
            ArchiveQuote[] ordered = found.OrderBy(quote => quote.Origin.Thread.Source, StringComparer.Ordinal)
                .ThenBy(quote => quote.Origin.Thread.Board, StringComparer.Ordinal).ThenBy(quote => quote.Origin.Thread.Thread, StringComparer.Ordinal)
                .ThenBy(quote => quote.Origin.PostId, StringComparer.Ordinal).ToArray();
            return new ArchiveQuotePage(Array.AsReadOnly(ordered.Skip(offset).Take(limit).ToArray()), ordered.Length, offset);
        }
        string where = backlinks
            ? "WHERE target_source=$source AND target_board=$board AND target_thread=$thread AND target_post=$post"
            : "WHERE source=$source AND board=$board AND thread=$thread AND post=$post";
        using SqliteCommand totalCommand = Command(connection, transaction, "SELECT count(*) FROM archive_quotes " + where);
        AddId(totalCommand, post.Thread);
        totalCommand.Parameters.AddWithValue("$post", post.PostId);
        int count = checked(Convert.ToInt32(totalCommand.ExecuteScalar(), CultureInfo.InvariantCulture));
        using SqliteCommand page = Command(connection, transaction, $"""
            SELECT source,board,thread,post,target_source,target_board,target_thread,target_post,label,url,is_external
            FROM archive_quotes {where} ORDER BY source,board,thread,post,position LIMIT $limit OFFSET $offset
            """);
        AddId(page, post.Thread);
        page.Parameters.AddWithValue("$post", post.PostId);
        page.Parameters.AddWithValue("$limit", limit);
        page.Parameters.AddWithValue("$offset", offset);
        List<ArchiveQuote> items = new List<ArchiveQuote>();
        using SqliteDataReader rows = page.ExecuteReader();
        while (rows.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            ArchivePostId origin = new ArchivePostId(new ResourceId(rows.GetString(0), rows.GetString(1), rows.GetString(2)), rows.GetString(3));
            ArchivePostId? target = rows.IsDBNull(4) ? null
                : new ArchivePostId(new ResourceId(rows.GetString(4), rows.GetString(5), rows.GetString(6)), rows.GetString(7));
            items.Add(new ArchiveQuote(origin, target, rows.GetString(8), NullableString(rows, 9)) { IsExternal = rows.GetInt32(10) == 1 });
        }
        return new ArchiveQuotePage(Array.AsReadOnly(items.ToArray()), count, offset);
    }
}
