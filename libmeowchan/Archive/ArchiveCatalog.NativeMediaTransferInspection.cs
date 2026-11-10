using System;
using System.Collections.Immutable;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MeowChan.Transfer;
using Microsoft.Data.Sqlite;

namespace MeowChan.Archive;

public sealed partial class ArchiveCatalog
{
    /// <summary>
    /// Reads bounded, path-free v2 import evidence. Import and receipt rows are observed
    /// in one read-only SQLite snapshot; this method never inspects files or authorizes Apply.
    /// </summary>
    internal Task<NativeMediaV2ImportOperationPage> ReadNativeMediaV2ImportOperationsPageAsync(
        Guid? afterOperationId = null, CancellationToken cancellationToken = default)
        => ReadAsync(connection =>
        {
            using SqliteTransaction transaction = connection.BeginTransaction(deferred: true);
            NativeMediaPersistedPage<NativeMediaPersistedImportOperation> page =
                ArchiveSql.ReadPersistedMediaImportV2OperationsPage(
                    connection, transaction, afterOperationId, cancellationToken);
            ImmutableArray<NativeMediaV2ImportOperationInspection>.Builder items =
                ImmutableArray.CreateBuilder<NativeMediaV2ImportOperationInspection>(page.Items.Length);
            foreach (NativeMediaPersistedImportOperation operation in page.Items)
            {
                cancellationToken.ThrowIfCancellationRequested();
                NativeMediaPersistedImportReceipt? receipt = ArchiveSql.ReadPersistedMediaImportReceipt(
                    connection, transaction, operation.Identity.OperationId,
                    operation.Identity.RequestSha256, planVersion: 2, cancellationToken);
                items.Add(NativeMediaV2TransferInspectionFactory.ImportOperation(operation, receipt));
            }
            cancellationToken.ThrowIfCancellationRequested();
            transaction.Commit();
            return new NativeMediaV2ImportOperationPage(items.ToImmutable(), page.NextOperationId);
        }, cancellationToken);

    /// <summary>Reads one v2 import operation and its receipt from one read-only snapshot.</summary>
    internal Task<NativeMediaV2ImportOperationInspection?> ReadNativeMediaV2ImportOperationAsync(
        Guid operationId, string requestSha256, CancellationToken cancellationToken = default)
    {
        ArchiveSql.ValidateMediaImportIdentity(operationId, requestSha256);
        return ReadAsync(connection =>
        {
            using SqliteTransaction transaction = connection.BeginTransaction(deferred: true);
            NativeMediaPersistedImportOperation? operation = ArchiveSql.ReadPersistedMediaImportV2Operation(
                connection, transaction, operationId, requestSha256, cancellationToken);
            if (operation is null)
            {
                transaction.Commit();
                return null;
            }
            NativeMediaPersistedImportReceipt? receipt = ArchiveSql.ReadPersistedMediaImportReceipt(
                connection, transaction, operationId, requestSha256, planVersion: 2, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            transaction.Commit();
            return NativeMediaV2TransferInspectionFactory.ImportOperation(operation, receipt);
        }, cancellationToken);
    }

    /// <summary>
    /// Reads a v2 import receipt. IsOrphan is true only when no operation row exists for
    /// the same operation/request identity; no filesystem or recovery inference is made.
    /// </summary>
    internal Task<NativeMediaV2ImportReceiptInspection?> ReadNativeMediaV2ImportReceiptAsync(
        Guid operationId, string requestSha256, CancellationToken cancellationToken = default)
    {
        ArchiveSql.ValidateMediaImportIdentity(operationId, requestSha256);
        return ReadAsync(connection =>
        {
            using SqliteTransaction transaction = connection.BeginTransaction(deferred: true);
            NativeMediaPersistedImportReceipt? receipt = ArchiveSql.ReadPersistedMediaImportReceipt(
                connection, transaction, operationId, requestSha256, planVersion: 2, cancellationToken);
            if (receipt is null)
            {
                transaction.Commit();
                return null;
            }
            NativeMediaPersistedImportOperation? operation = ArchiveSql.ReadPersistedMediaImportOperation(
                connection, transaction, operationId, requestSha256, cancellationToken);
            if (operation is not null && operation.Identity.PlanVersion != 2)
                throw new InvalidDataException("A v2 import receipt is paired with a non-v2 operation.");
            cancellationToken.ThrowIfCancellationRequested();
            transaction.Commit();
            return NativeMediaV2TransferInspectionFactory.ImportReceipt(receipt, operation is null);
        }, cancellationToken);
    }

    /// <summary>
    /// Reads bounded, path-free v2 merge evidence. Import and merge rows are deliberately
    /// separated so a merge cursor or receipt cannot cross into the import table.
    /// </summary>
    internal Task<NativeMediaV2MergeOperationPage> ReadNativeMediaV2MergeOperationsPageAsync(
        Guid? afterOperationId = null, CancellationToken cancellationToken = default)
        => ReadAsync(connection =>
        {
            using SqliteTransaction transaction = connection.BeginTransaction(deferred: true);
            NativeMediaPersistedPage<NativeMediaPersistedMergeOperation> page =
                ArchiveSql.ReadPersistedMediaMergeV2OperationsPage(
                    connection, transaction, afterOperationId, cancellationToken);
            ImmutableArray<NativeMediaV2MergeOperationInspection>.Builder items =
                ImmutableArray.CreateBuilder<NativeMediaV2MergeOperationInspection>(page.Items.Length);
            foreach (NativeMediaPersistedMergeOperation operation in page.Items)
            {
                cancellationToken.ThrowIfCancellationRequested();
                NativeMediaPersistedMergeReceipt? receipt = ArchiveSql.ReadPersistedMediaMergeReceipt(
                    connection, transaction, operation.Identity.OperationId,
                    operation.Identity.RequestSha256, planVersion: 2, cancellationToken);
                items.Add(NativeMediaV2TransferInspectionFactory.MergeOperation(operation, receipt));
            }
            cancellationToken.ThrowIfCancellationRequested();
            transaction.Commit();
            return new NativeMediaV2MergeOperationPage(items.ToImmutable(), page.NextOperationId);
        }, cancellationToken);

    /// <summary>Reads one v2 merge operation and its receipt from one read-only snapshot.</summary>
    internal Task<NativeMediaV2MergeOperationInspection?> ReadNativeMediaV2MergeOperationAsync(
        Guid operationId, string requestSha256, CancellationToken cancellationToken = default)
    {
        ArchiveSql.ValidateMediaMergeIdentity(operationId, requestSha256);
        return ReadAsync(connection =>
        {
            using SqliteTransaction transaction = connection.BeginTransaction(deferred: true);
            NativeMediaPersistedMergeOperation? operation = ArchiveSql.ReadPersistedMediaMergeV2Operation(
                connection, transaction, operationId, requestSha256, cancellationToken);
            if (operation is null)
            {
                transaction.Commit();
                return null;
            }
            NativeMediaPersistedMergeReceipt? receipt = ArchiveSql.ReadPersistedMediaMergeReceipt(
                connection, transaction, operationId, requestSha256, planVersion: 2, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            transaction.Commit();
            return NativeMediaV2TransferInspectionFactory.MergeOperation(operation, receipt);
        }, cancellationToken);
    }

    /// <summary>
    /// Reads a v2 merge receipt and reports whether its operation row is absent. A receipt
    /// is historical evidence only; this method never turns it into a replay or Apply grant.
    /// </summary>
    internal Task<NativeMediaV2MergeReceiptInspection?> ReadNativeMediaV2MergeReceiptAsync(
        Guid operationId, string requestSha256, CancellationToken cancellationToken = default)
    {
        ArchiveSql.ValidateMediaMergeIdentity(operationId, requestSha256);
        return ReadAsync(connection =>
        {
            using SqliteTransaction transaction = connection.BeginTransaction(deferred: true);
            NativeMediaPersistedMergeReceipt? receipt = ArchiveSql.ReadPersistedMediaMergeReceipt(
                connection, transaction, operationId, requestSha256, planVersion: 2, cancellationToken);
            if (receipt is null)
            {
                transaction.Commit();
                return null;
            }
            NativeMediaPersistedMergeOperation? operation = ArchiveSql.ReadPersistedMediaMergeOperation(
                connection, transaction, operationId, requestSha256, cancellationToken);
            if (operation is not null && operation.Identity.PlanVersion != 2)
                throw new InvalidDataException("A v2 merge receipt is paired with a non-v2 operation.");
            cancellationToken.ThrowIfCancellationRequested();
            transaction.Commit();
            return NativeMediaV2TransferInspectionFactory.MergeReceipt(receipt, operation is null);
        }, cancellationToken);
    }
}
