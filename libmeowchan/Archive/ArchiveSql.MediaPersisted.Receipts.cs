using System;
using System.IO;
using System.Threading;
using MeowChan.Transfer;
using Microsoft.Data.Sqlite;

namespace MeowChan.Archive;

internal static partial class ArchiveSql
{
    // Receipts are content-free historical evidence. In particular, reading an orphan
    // must not require an operation row or project a receipt into a public success.
    private const string PersistedMediaReceiptIdentityProjection = """
        operation_id, typeof(operation_id) AS _type_operation_id,
        length(CAST(operation_id AS BLOB)) AS _bytes_operation_id,
        request_sha256, typeof(request_sha256) AS _type_request_sha256,
        length(CAST(request_sha256 AS BLOB)) AS _bytes_request_sha256,
        plan_version
        """;

    internal static NativeMediaPersistedImportReceipt? ReadPersistedMediaImportReceipt(
        SqliteConnection connection, SqliteTransaction? transaction, Guid operation, string request,
        int planVersion, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ValidateMediaImportIdentity(operation, request);
        ValidatePersistedMediaReceiptVersionArgument(planVersion);
        if (transaction is null)
        {
            using SqliteTransaction snapshot = connection.BeginTransaction(deferred: true);
            return ReadPersistedMediaImportReceipt(connection, snapshot, operation, request, planVersion, ct);
        }

        // Schema and receipt are observed in the same caller-owned (or above-owned)
        // snapshot. No commit, schema restamp, repair or operation lookup takes place.
        RequirePersistedMediaSchema(connection, transaction);
        ct.ThrowIfCancellationRequested();
        using SqliteCommand command = Command(connection, transaction, $"""
            SELECT {PersistedMediaReceiptIdentityProjection},
                post_count, attachment_count, evidence_additions,
                derived_descriptor_additions, contained_derived_additions, original_binding_count
            FROM archive_media_import_receipts WHERE operation_id=$op
            """);
        command.Parameters.AddWithValue("$op", operation.ToString("D"));
        using SqliteDataReader reader = command.ExecuteReader();
        bool found = reader.Read();
        ct.ThrowIfCancellationRequested();
        if (!found) return null;

        NativeMediaReceiptIdentity identity = ReadPersistedMediaReceiptIdentity(reader, operation, request, planVersion);
        NativeMediaImportCounts counts = new(
            ReadPersistedMediaCount(reader, "post_count"),
            ReadPersistedMediaCount(reader, "attachment_count"),
            ReadPersistedMediaCount(reader, "evidence_additions"),
            ReadPersistedMediaCount(reader, "derived_descriptor_additions"),
            ReadPersistedMediaCount(reader, "contained_derived_additions"),
            ReadPersistedMediaCount(reader, "original_binding_count"));
        ValidatePersistedMediaReceiptCounters(identity.PlanVersion, counts.EvidenceAdditions,
            counts.DerivedDescriptorAdditions, counts.ContainedDerivedAdditions);
        if (identity.PlanVersion == 1 && (counts.AttachmentCount == 0 || counts.OriginalBindingCount != 0))
            throw new InvalidDataException("A legacy media import receipt has invalid counters.");
        ct.ThrowIfCancellationRequested();
        return new(identity, counts);
    }

    internal static NativeMediaPersistedMergeReceipt? ReadPersistedMediaMergeReceipt(
        SqliteConnection connection, SqliteTransaction? transaction, Guid operation, string request,
        int planVersion, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ValidateMediaMergeIdentity(operation, request);
        ValidatePersistedMediaReceiptVersionArgument(planVersion);
        if (transaction is null)
        {
            using SqliteTransaction snapshot = connection.BeginTransaction(deferred: true);
            return ReadPersistedMediaMergeReceipt(connection, snapshot, operation, request, planVersion, ct);
        }

        RequirePersistedMediaSchema(connection, transaction);
        ct.ThrowIfCancellationRequested();
        using SqliteCommand command = Command(connection, transaction, $"""
            SELECT {PersistedMediaReceiptIdentityProjection},
                new_posts, updated_posts, new_attachments, new_bindings, conflicts,
                evidence_additions, derived_descriptor_additions, contained_derived_additions
            FROM archive_media_merge_receipts WHERE operation_id=$op
            """);
        command.Parameters.AddWithValue("$op", operation.ToString("D"));
        using SqliteDataReader reader = command.ExecuteReader();
        bool found = reader.Read();
        ct.ThrowIfCancellationRequested();
        if (!found) return null;

        NativeMediaReceiptIdentity identity = ReadPersistedMediaReceiptIdentity(reader, operation, request, planVersion);
        NativeMediaMergeCounts counts = new(
            ReadPersistedMediaCount(reader, "new_posts"),
            ReadPersistedMediaCount(reader, "updated_posts"),
            ReadPersistedMediaCount(reader, "new_attachments"),
            ReadPersistedMediaCount(reader, "new_bindings"),
            ReadPersistedMediaCount(reader, "conflicts"),
            ReadPersistedMediaCount(reader, "evidence_additions"),
            ReadPersistedMediaCount(reader, "derived_descriptor_additions"),
            ReadPersistedMediaCount(reader, "contained_derived_additions"));
        ValidatePersistedMediaReceiptCounters(identity.PlanVersion, counts.EvidenceAdditions,
            counts.DerivedDescriptorAdditions, counts.ContainedDerivedAdditions);
        ct.ThrowIfCancellationRequested();
        return new(identity, counts);
    }

    // Pure comparisons of previously decoded evidence, not recovery/Apply gates.
    // Record equality includes every counter, including the version-two additions.
    internal static bool PersistedMediaImportReceiptMatches(NativeMediaPersistedImportOperation operation,
        NativeMediaPersistedImportReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(receipt);
        return operation.State == NativeMediaImportOperationState.Committed
            && PersistedMediaReceiptIdentityMatches(operation.Identity, receipt.Identity)
            && operation.Counts == receipt.Counts;
    }

    internal static bool PersistedMediaMergeReceiptMatches(NativeMediaPersistedMergeOperation operation,
        NativeMediaPersistedMergeReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(receipt);
        return operation.State == NativeMediaImportOperationState.Committed
            && PersistedMediaReceiptIdentityMatches(operation.Identity, receipt.Identity)
            && operation.Counts == receipt.Counts;
    }

    private static bool PersistedMediaReceiptIdentityMatches(NativeMediaPersistedIdentity operation,
        NativeMediaReceiptIdentity receipt)
        => operation.OperationId == receipt.OperationId
            && string.Equals(operation.RequestSha256, receipt.RequestSha256, StringComparison.Ordinal)
            && operation.PlanVersion == receipt.PlanVersion;

    private static void ValidatePersistedMediaReceiptVersionArgument(int planVersion)
    {
        if (planVersion is not (1 or 2))
            throw new ArgumentOutOfRangeException(nameof(planVersion), "A media receipt requires plan version 1 or 2.");
    }

    private static NativeMediaReceiptIdentity ReadPersistedMediaReceiptIdentity(SqliteDataReader reader,
        Guid operation, string request, int planVersion)
    {
        // The dedicated projection supplies the shared reader's type/byte aliases.
        // Bounds are checked before either stored string is materialized.
        string storedOperation = ReadPersistedMediaText(reader, "operation_id", maximumBytes: 36)!;
        string storedRequest = ReadPersistedMediaText(reader, "request_sha256", maximumBytes: 64)!;
        int storedVersion = ReadPersistedMediaCount(reader, "plan_version");
        if (storedVersion is not (1 or 2))
            throw new InvalidDataException("A media receipt has an invalid plan version.");
        if (!Guid.TryParseExact(storedOperation, "D", out Guid parsedOperation)
            || parsedOperation == Guid.Empty
            || !string.Equals(storedOperation, parsedOperation.ToString("D"), StringComparison.Ordinal)
            || !IsMediaMergeHash(storedRequest))
            throw new InvalidDataException("A media receipt has an invalid persisted identity.");
        if (parsedOperation != operation || !string.Equals(storedRequest, request, StringComparison.Ordinal)
            || storedVersion != planVersion)
            throw new InvalidDataException("The media receipt has different input or plan version.");
        return new(parsedOperation, storedRequest, storedVersion);
    }

    private static void ValidatePersistedMediaReceiptCounters(int planVersion, int evidenceAdditions,
        int derivedDescriptorAdditions, int containedDerivedAdditions)
    {
        if (containedDerivedAdditions > derivedDescriptorAdditions)
            throw new InvalidDataException("A media receipt has invalid derived counters.");
        if (planVersion == 1 && (evidenceAdditions != 0 || derivedDescriptorAdditions != 0
            || containedDerivedAdditions != 0))
            throw new InvalidDataException("A legacy media receipt has version-two counters.");
    }
}
