using System;
using System.Threading;
using System.Threading.Tasks;

namespace MeowChan.Archive;

public sealed partial class ArchiveReader
{
    /// <summary>Passive Chan catalog original-name search. Empty text matches all known names.</summary>
    internal Task<ArchiveFilenamePage> SearchFilenamesAsync(ArchiveFilenameSearch query,
        int offset = 0, int limit = 100, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
        if (limit < 1 || limit > 500) throw new ArgumentOutOfRangeException(nameof(limit));
        return ReadAsync((connection, transaction) =>
            ArchiveSql.SearchFilenames(connection, transaction, query, offset, limit, cancellationToken), cancellationToken);
    }
}
