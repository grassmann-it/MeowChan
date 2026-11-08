using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace MeowChan.Archive;

public sealed partial class ArchiveCatalog
{
    // Startup gate only, after catalog migration and before producer restoration. A
    // restart must not turn an interrupted transfer into permission to write again.
    // This neither retries an operation nor inspects/deletes its candidate files.
    internal async Task EnsureNativeMediaRecoveryClearAsync(CancellationToken cancellationToken = default)
    {
        bool unresolved = await ReadAsync(connection =>
        {
            using var transaction = connection.BeginTransaction(deferred: true);
            ArchiveSql.ValidateSchema(connection, transaction);
            bool result = ArchiveSql.HasNativeMediaTransferRecoveryBlocker(
                connection, transaction, cancellationToken);
            transaction.Commit();
            return result;
        }, cancellationToken).ConfigureAwait(false);
        if (unresolved)
            throw new InvalidDataException("Native media transfer reconciliation is required.");
    }
}
