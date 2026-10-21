using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeowChan.Interfaces;
using Microsoft.Data.Sqlite;

namespace MeowChan.Archive;

public sealed partial class ArchiveReader
{
    /// <summary>Counts local incoming quotes for one bounded reader page in a single read transaction.</summary>
    public Task<IReadOnlyDictionary<string, int>> GetBacklinkCountsAsync(ResourceId thread,
        IReadOnlyList<string> postIds, CancellationToken cancellationToken = default)
    {
        ArchiveValidation.ValidateId(thread);
        ArgumentNullException.ThrowIfNull(postIds);
        if (postIds.Count > 500) throw new ArgumentOutOfRangeException(nameof(postIds));
        foreach (string postId in postIds) ValidatePostId(new ArchivePostId(thread, postId));
        if (postIds.Count == 0)
            return Task.FromResult<IReadOnlyDictionary<string, int>>(new ReadOnlyDictionary<string, int>(new Dictionary<string, int>()));
        string[] ids = postIds.Distinct(StringComparer.Ordinal).ToArray();
        return ReadAsync((connection, transaction) => ArchiveSql.ReadBacklinkCounts(connection, transaction, thread, ids,
            cancellationToken), cancellationToken);
    }
}

internal static partial class ArchiveSql
{
    internal static IReadOnlyDictionary<string, int> ReadBacklinkCounts(SqliteConnection connection,
        SqliteTransaction transaction, ResourceId thread, IReadOnlyList<string> postIds, CancellationToken token)
    {
        Dictionary<string, int> result = postIds.ToDictionary(id => id, _ => 0, StringComparer.Ordinal);
        if (GetPragma(connection, "user_version") == 1)
        {
            // One legacy scan for the entire visible page, never one scan per post.
            int total = ReadPage(connection, transaction, 0, 1).TotalCount;
            for (int start = 0; start < total; start += 500)
            {
                foreach (ArchiveThreadSummary summary in ReadPage(connection, transaction, start, 500).Items)
                {
                    token.ThrowIfCancellationRequested();
                    ArchivedThreadView view = ReadThread(connection, transaction, summary.Id)!;
                    foreach (CapturedPost post in view.Posts)
                    {
                        token.ThrowIfCancellationRequested();
                        foreach (ArchiveQuote quote in ArchiveQuoteParser.Parse(summary.Id, summary.Url, post))
                            if (quote.Target is { } target && target.Thread == thread && result.ContainsKey(target.PostId))
                                result[target.PostId]++;
                    }
                }
            }
            return new ReadOnlyDictionary<string, int>(result);
        }
        using SqliteCommand command = Command(connection, transaction, $"""
            SELECT target_post, count(*) FROM archive_quotes
            WHERE target_source=$source AND target_board=$board AND target_thread=$thread
              AND target_post IN ({string.Join(",", Enumerable.Range(0, postIds.Count).Select(index => "$p" + index))})
            GROUP BY target_post
            """);
        AddId(command, thread);
        for (int index = 0; index < postIds.Count; index++) command.Parameters.AddWithValue("$p" + index, postIds[index]);
        using SqliteDataReader rows = command.ExecuteReader();
        while (rows.Read())
        {
            token.ThrowIfCancellationRequested();
            result[rows.GetString(0)] = checked(Convert.ToInt32(rows.GetInt64(1), CultureInfo.InvariantCulture));
        }
        return new ReadOnlyDictionary<string, int>(result);
    }
}
