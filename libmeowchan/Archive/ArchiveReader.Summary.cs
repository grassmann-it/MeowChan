using System.Threading;
using System.Threading.Tasks;

namespace MeowChan.Archive;

public sealed partial class ArchiveReader
{
    /// <summary>Reads one thread's local metadata without loading posts or accessing media files.</summary>
    public Task<ArchiveThreadSummary?> ReadThreadSummaryAsync(ResourceId id, CancellationToken cancellationToken = default)
    {
        ArchiveValidation.ValidateId(id);
        return ReadAsync((connection, transaction) => ArchiveSql.ReadSummary(connection, transaction, id), cancellationToken);
    }
}
