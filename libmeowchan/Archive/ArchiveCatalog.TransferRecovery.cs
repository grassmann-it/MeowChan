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
            // One scalar, one SQL snapshot, no operation JSON or payload materialization.
            // Content-free historical receipts may outlive operation cleanup; only a
            // surviving operation needs its own exact, count-consistent receipt.
            using var command = ArchiveSql.Command(connection, transaction, """
                SELECT EXISTS (
                    SELECT 1 FROM archive_media_import_operations AS o
                    WHERE o.state IS NOT 'Committed' OR NOT EXISTS (
                        SELECT 1 FROM archive_media_import_receipts AS r
                        WHERE r.operation_id = o.operation_id COLLATE BINARY
                          AND r.request_sha256 = o.request_sha256 COLLATE BINARY
                          AND r.post_count = o.post_count
                          AND r.attachment_count = o.attachment_count
                    )
                ) OR EXISTS (
                    SELECT 1 FROM archive_media_merge_operations AS o
                    WHERE o.state IS NOT 'Committed' OR NOT EXISTS (
                        SELECT 1 FROM archive_media_merge_receipts AS r
                        WHERE r.operation_id = o.operation_id COLLATE BINARY
                          AND r.request_sha256 = o.request_sha256 COLLATE BINARY
                          AND r.new_posts = o.new_posts AND r.updated_posts = o.updated_posts
                          AND r.new_attachments = o.new_attachments AND r.new_bindings = o.new_bindings
                          AND r.conflicts = o.conflicts
                    )
                );
                """);
            cancellationToken.ThrowIfCancellationRequested();
            bool result = (long)command.ExecuteScalar()! != 0;
            cancellationToken.ThrowIfCancellationRequested();
            transaction.Commit();
            return result;
        }, cancellationToken).ConfigureAwait(false);
        if (unresolved)
            throw new InvalidDataException("Native media transfer reconciliation is required.");
    }
}
