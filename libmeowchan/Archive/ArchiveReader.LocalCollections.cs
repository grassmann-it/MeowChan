using System;
using System.IO;
using System.Security;
using System.Threading;
using System.Threading.Tasks;
using MeowChan.Storage;
using Microsoft.Data.Sqlite;

namespace MeowChan.Archive;

public sealed partial class ArchiveReader
{
    /// <summary>Ordinary reader lifetime; the receipt itself needs no current content or payload.</summary>
    internal Task<LocalCollectionImportReceipt?> ReadLocalImportReceiptAsync(
        LocalImportReceiptKey key, CancellationToken ct)
    {
        LocalImportCodec.ValidateReceiptKey(key);
        return Task.Run(() =>
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            return ReadLocalReceiptAtPath(DatabasePath, key, ct);
        }, ct);
    }

    /// <summary>
    /// Independent reconciliation seam. The caller supplies its fixed trusted archive.db path,
    /// not a path from persisted input. Needs no old reader, owner, profile lease or live producer.
    /// Null is only point-in-time absence in the same compatible v9 target, not NotCommitted.
    /// </summary>
    internal static Task<LocalCollectionImportReceipt?> ReadLocalImportReceiptAtPathAsync(
        string trustedDatabasePath, LocalImportReceiptKey key, CancellationToken ct)
    {
        LocalImportCodec.ValidateReceiptKey(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(trustedDatabasePath);
        return Task.Run(() => ReadLocalReceiptAtPath(trustedDatabasePath, key, ct), ct);
    }

    private static LocalCollectionImportReceipt? ReadLocalReceiptAtPath(
        string trustedDatabasePath, LocalImportReceiptKey key, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            using SqliteConnection connection = ArchiveSql.OpenConnection(trustedDatabasePath, SqliteOpenMode.ReadOnly);
            using SqliteTransaction tx = connection.BeginTransaction(deferred: true);
            // The SQL helper binds header, manifest, strict fence and receipt to this one tx.
            // Do not use the old Catalog.ReadAsync or construct/open a reader first.
            LocalCollectionImportReceipt? receipt = ArchiveSql.ReadLocalImportReceipt(connection, tx, key, new LocalImportCodec(), ct);
            ct.ThrowIfCancellationRequested();
            tx.Commit();
            ct.ThrowIfCancellationRequested();
            return receipt;
        }
        catch (Exception e) when (ct.IsCancellationRequested && e is SqliteException or IOException)
        { throw new OperationCanceledException(ct); }
        catch (LocalImportReadException) { throw; }
        catch (SqliteException e)
        {
            // CANTOPEN alone is not proof of absence (permissions/directories can cause it).
            if (e.SqliteErrorCode == 14) RequireLocalDatabaseNotMissing(trustedDatabasePath);
            throw new LocalImportReadException(e.SqliteErrorCode is 5 or 6 ? LocalImportError.Busy : LocalImportError.IoFailure);
        }
        catch (FileNotFoundException) { throw new FileNotFoundException("The local receipt database was not found."); }
        catch (DirectoryNotFoundException) { throw new FileNotFoundException("The local receipt database was not found."); }
        catch (IOException) { throw new LocalImportReadException(LocalImportError.IoFailure); }
        catch (UnauthorizedAccessException) { throw new LocalImportReadException(LocalImportError.IoFailure); }
        catch (SecurityException) { throw new LocalImportReadException(LocalImportError.IoFailure); }
    }

    private static void RequireLocalDatabaseNotMissing(string path)
    {
        // This probe only classifies a failed ReadOnly open. It grants no authority and
        // proves neither receipt absence nor that a potentially earlier write has ended.
        try { _ = File.GetAttributes(path); }
        catch (FileNotFoundException) { throw new FileNotFoundException("The local receipt database was not found."); }
        catch (DirectoryNotFoundException) { throw new FileNotFoundException("The local receipt database was not found."); }
        catch (IOException) { throw new LocalImportReadException(LocalImportError.IoFailure); }
        catch (UnauthorizedAccessException) { throw new LocalImportReadException(LocalImportError.IoFailure); }
        catch (SecurityException) { throw new LocalImportReadException(LocalImportError.IoFailure); }
    }

    // R1 metadata reads have their own narrow worker: the strict bounded v9 fence must
    // precede the shared schema validator, unlike the older generic ReadAsync callback.
    internal Task<LocalCollectionPage> ListLocalCollectionsAsync(int offset, int limit, CancellationToken ct)
    {
        LocalImportCodec.ValidatePage(offset, limit);
        return ReadLocalMetadataAsync((c, tx) => ArchiveSql.ReadLocalCollectionPage(c, tx, offset, limit, ct), ct);
    }

    internal Task<LocalCollectionSnapshot?> ReadLocalCollectionAsync(LocalCollectionId id, CancellationToken ct)
    {
        if (id.Value == Guid.Empty) throw new ArgumentException("A nonempty local collection is required.", nameof(id));
        return ReadLocalMetadataAsync((c, tx) => ArchiveSql.ReadLocalCollection(c, tx, id,
            Path.GetDirectoryName(DatabasePath)!, new LocalImportCodec(), ct), ct);
    }

    private Task<T> ReadLocalMetadataAsync<T>(Func<SqliteConnection, SqliteTransaction, T> read, CancellationToken ct)
        => Task.Run(() =>
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            ct.ThrowIfCancellationRequested();
            try
            {
                using SqliteConnection connection = ArchiveSql.OpenConnection(DatabasePath, SqliteOpenMode.ReadOnly);
                using SqliteTransaction tx = connection.BeginTransaction(deferred: true);
                // Both SQL helpers validate in this tx, including the legacy empty-result arm.
                T value = read(connection, tx);
                ct.ThrowIfCancellationRequested();
                tx.Commit();
                ct.ThrowIfCancellationRequested();
                return value;
            }
            catch (Exception) when (ct.IsCancellationRequested) { throw new OperationCanceledException(ct); }
            catch (LocalImportReadException) { throw; }
            catch (SqliteException e)
            {
                if (e.SqliteErrorCode == 14) RequireLocalDatabaseNotMissing(DatabasePath);
                throw new LocalImportReadException(e.SqliteErrorCode is 5 or 6 ? LocalImportError.Busy : LocalImportError.IoFailure);
            }
            catch (FileNotFoundException) { throw new FileNotFoundException("The local collection database was not found."); }
            catch (DirectoryNotFoundException) { throw new FileNotFoundException("The local collection database was not found."); }
            catch (IOException) { throw new LocalImportReadException(LocalImportError.IoFailure); }
            catch (UnauthorizedAccessException) { throw new LocalImportReadException(LocalImportError.IoFailure); }
            catch (SecurityException) { throw new LocalImportReadException(LocalImportError.IoFailure); }
        }, ct);
}
