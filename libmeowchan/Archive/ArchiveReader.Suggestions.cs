using System;
using System.Threading;
using System.Threading.Tasks;

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
}
