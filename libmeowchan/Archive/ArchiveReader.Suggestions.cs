using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MeowChan.Admission;

namespace MeowChan.Archive;

public sealed partial class ArchiveReader
{
    public Task<ArchiveIdentityPage<string>> ReadSourceIdentitiesAsync(int limit = 256, CancellationToken cancellationToken = default)
    {
        if (limit < 1 || limit > 256) throw new ArgumentOutOfRangeException(nameof(limit));
        return ReadAsync((connection, transaction) => ArchiveSql.ReadSourceIdentities(connection, transaction, limit, cancellationToken), cancellationToken);
    }

    public Task<ArchiveIdentityPage<ArchiveBoardIdentity>> ReadBoardIdentitiesAsync(string? source = null, int limit = 2048, CancellationToken cancellationToken = default)
    {
        if (limit < 1 || limit > 2048) throw new ArgumentOutOfRangeException(nameof(limit));
        if (source != null && (string.IsNullOrWhiteSpace(source) || source.Contains('\0'))) throw new ArgumentException("Invalid source identity.", nameof(source));
        return ReadAsync((connection, transaction) => ArchiveSql.ReadBoardIdentities(connection, transaction, source, limit, cancellationToken), cancellationToken);
    }

    internal const int BoardTitleHistoryMaximum = 50_000;

    internal Task<IReadOnlyList<BoardTitleHistory>> ReadBoardTitleHistoryAsync(string source, string board, int limit,
        CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > BoardTitleHistoryMaximum) throw new ArgumentOutOfRangeException(nameof(limit));
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(board);
        return ReadAsync((connection, transaction) => ArchiveSql.ReadBoardTitleHistory(connection, transaction,
            source, board, limit, cancellationToken), cancellationToken);
    }
}
