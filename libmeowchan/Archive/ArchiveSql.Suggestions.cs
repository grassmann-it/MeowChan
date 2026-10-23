using System;
using System.Collections.Generic;
using System.Threading;
using Microsoft.Data.Sqlite;

namespace MeowChan.Archive;

internal static partial class ArchiveSql
{
    internal static ArchiveIdentityPage<string> ReadSourceIdentities(SqliteConnection connection, SqliteTransaction transaction, int limit, CancellationToken token)
    {
        if (limit < 1 || limit > 256) throw new ArgumentOutOfRangeException(nameof(limit));
        connection.CreateCollation("meowchan_identity", StringComparer.Ordinal.Compare);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT source FROM (SELECT DISTINCT source FROM archive_threads) ORDER BY source COLLATE meowchan_identity LIMIT $limit";
        command.Parameters.AddWithValue("$limit", limit + 1);
        using var cancel = token.Register(command.Cancel);
        using var reader = command.ExecuteReader();
        var items = new List<string>(limit);
        while (reader.Read())
        {
            token.ThrowIfCancellationRequested();
            if (items.Count == limit) return new(items, true);
            items.Add(reader.GetString(0));
        }
        token.ThrowIfCancellationRequested();
        return new(items, false);
    }

    internal static ArchiveIdentityPage<ArchiveBoardIdentity> ReadBoardIdentities(SqliteConnection connection, SqliteTransaction transaction, string? source, int limit, CancellationToken token)
    {
        if (limit < 1 || limit > 2048) throw new ArgumentOutOfRangeException(nameof(limit));
        connection.CreateCollation("meowchan_identity", StringComparer.Ordinal.Compare);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT source,board FROM (SELECT DISTINCT source,board FROM archive_threads "
            + (source == null ? "" : "WHERE source=$source ")
            + ") ORDER BY source COLLATE meowchan_identity,board COLLATE meowchan_identity LIMIT $limit";
        if (source != null) command.Parameters.AddWithValue("$source", source);
        command.Parameters.AddWithValue("$limit", limit + 1);
        using var cancel = token.Register(command.Cancel);
        using var reader = command.ExecuteReader();
        var items = new List<ArchiveBoardIdentity>(limit);
        while (reader.Read())
        {
            token.ThrowIfCancellationRequested();
            if (items.Count == limit) return new(items, true);
            items.Add(new(reader.GetString(0), reader.GetString(1)));
        }
        token.ThrowIfCancellationRequested();
        return new(items, false);
    }
}
