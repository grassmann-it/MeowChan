using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace MeowChan.Archive;

internal sealed record ReaderGroupCandidate(ResourceId Id, string Title, bool HasLocalPosts, bool Watched = false);
internal sealed record ReaderGroupCandidatePage(IReadOnlyList<ReaderGroupCandidate> Items, bool HasMore);

public sealed partial class ArchiveReader
{
    /// <summary>Bounded metadata only, deterministic ordinal order; never scans retained post text.</summary>
    internal Task<ReaderGroupCandidatePage> FindReaderGroupCandidatesAsync(string text, int limit = 50,
        CancellationToken cancellationToken = default)
    {
        if (text.Length > 512 || text.Contains('\0')) throw new ArgumentException("Search is limited to 512 characters.", nameof(text));
        if (limit < 1 || limit > 50) throw new ArgumentOutOfRangeException(nameof(limit));
        return ReadAsync((connection, transaction) =>
        {
            connection.CreateFunction<string?, bool>("group_matches", value =>
            { cancellationToken.ThrowIfCancellationRequested(); return (value ?? "").Contains(text, StringComparison.OrdinalIgnoreCase); });
            using SqliteCommand query = ArchiveSql.Command(connection, transaction, """
                SELECT t.source,t.board,t.thread,t.title,
                    EXISTS(SELECT 1 FROM archive_posts p WHERE p.source=t.source AND p.board=t.board AND p.thread=t.thread)
                FROM archive_threads t
                WHERE group_matches(t.source) OR group_matches(t.board) OR group_matches(t.thread) OR group_matches(t.title)
                   OR group_matches(t.source||'/'||t.board||'/'||t.thread)
                ORDER BY (t.source||'/'||t.board||'/'||t.thread=$text) DESC,t.source COLLATE BINARY,t.board COLLATE BINARY,t.thread COLLATE BINARY
                LIMIT $limit
                """);
            query.Parameters.AddWithValue("$text", text);
            query.Parameters.AddWithValue("$limit", limit + 1);
            using SqliteDataReader rows = query.ExecuteReader();
            List<ReaderGroupCandidate> items = new();
            while (rows.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                items.Add(new(new(rows.GetString(0), rows.GetString(1), rows.GetString(2)), rows.GetString(3), rows.GetInt32(4) != 0));
            }
            return new ReaderGroupCandidatePage(Array.AsReadOnly(items.Take(limit).ToArray()), items.Count > limit);
        }, cancellationToken);
    }

    internal Task<IReadOnlyList<ReaderGroupCandidate>> ResolveReaderGroupMembersAsync(IReadOnlyList<ResourceId> ids,
        CancellationToken cancellationToken = default)
    {
        if (ids.Count > 1000) throw new ArgumentOutOfRangeException(nameof(ids));
        foreach (ResourceId id in ids) ArchiveValidation.ValidateId(id);
        ResourceId[] frozen = ids.ToArray();
        return ReadAsync<IReadOnlyList<ReaderGroupCandidate>>((connection, transaction) =>
        {
            using SqliteCommand query = ArchiveSql.Command(connection, transaction, """
                SELECT t.title,EXISTS(SELECT 1 FROM archive_posts p WHERE p.source=t.source AND p.board=t.board AND p.thread=t.thread)
                FROM archive_threads t WHERE t.source=$source AND t.board=$board AND t.thread=$thread
                """);
            query.Parameters.AddWithValue("$source", ""); query.Parameters.AddWithValue("$board", ""); query.Parameters.AddWithValue("$thread", "");
            List<ReaderGroupCandidate> result = new();
            foreach (ResourceId id in frozen)
            {
                cancellationToken.ThrowIfCancellationRequested(); SetId(query, id);
                using SqliteDataReader row = query.ExecuteReader();
                result.Add(row.Read() ? new(id, row.GetString(0), row.GetInt32(1) != 0) : new(id, "", false));
            }
            return Array.AsReadOnly(result.ToArray());
        }, cancellationToken);
    }
}
