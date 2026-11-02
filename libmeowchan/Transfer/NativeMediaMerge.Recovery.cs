using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeowChan.Storage;
using MeowChan.Transfer;
using Microsoft.Data.Sqlite;

namespace MeowChan.Archive;

public sealed partial class ArchiveCatalog
{
    private const int MaximumMediaMergeRecoveryChildren = 32;

    /// <summary>
    /// Read-only discovery independent of the original catalog/ProfileLease lifetime. The schema
    /// reader returns at most 32 operations and 32 MiB of catalog bytes, with its exact continuation
    /// cursor. It does not infer missing operations from filesystem contents or migrate the DB.
    /// </summary>
    internal Task<NativeMediaMergeOperationPage> ReadNativeMediaMergeOperationsPageAsync(
        Guid? afterOperationId = null, CancellationToken cancellationToken = default)
        => ReadMediaMergeRecoveryAsync(connection => ArchiveSql.ReadMediaMergeOperationsPage(connection, afterOperationId),
            cancellationToken);

    /// <summary>
    /// Inspects only the deterministic local operation parent and at most 32 immediate children.
    /// SQL metadata/receipt are one read snapshot; child status is a later read-only filesystem
    /// observation, not permission to resume, publish or discard anything. A historical receipt
    /// remains present even when operation metadata or files have since been removed.
    /// </summary>
    internal async Task<NativeMediaMergeInspection> InspectNativeMediaMergeOperationAsync(Guid operationId,
        string requestSha256, MediaStore store, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArchiveSql.ValidateMediaMergeIdentity(operationId, requestSha256);
        MediaMergeRecoveryEvidence evidence = await ReadMediaMergeRecoveryAsync(connection =>
        {
            using SqliteTransaction transaction = connection.BeginTransaction(deferred: true);
            NativeMediaMergeResult? receipt = ArchiveSql.ReadMediaMergeReceipt(connection, transaction, operationId, requestSha256);
            NativeMediaMergeOperation? operation = ArchiveSql.ReadMediaMergeOperation(connection, transaction, operationId, requestSha256);
            string? fingerprint = null, fullBindings = null, safeBindings = null;
            DateTimeOffset? importedAtUtc = null;
            if (operation != null)
            {
                // The public recovery DTO intentionally omits these immutable provenance/hash
                // fields. Read them from the SAME snapshot, not a fresh query after releasing SQL.
                using SqliteCommand command = ArchiveSql.Command(connection, transaction, """
                    SELECT package_fingerprint,imported_at,full_bindings_sha256,safe_bindings_sha256
                    FROM archive_media_merge_operations WHERE operation_id=$op AND request_sha256=$request
                    """);
                command.Parameters.AddWithValue("$op", operationId.ToString("D"));
                command.Parameters.AddWithValue("$request", requestSha256);
                using SqliteDataReader reader = command.ExecuteReader();
                if (!reader.Read()) throw new InvalidDataException("The media merge recovery evidence is missing.");
                fingerprint = reader.GetString(0);
                importedAtUtc = DateTimeOffset.ParseExact(reader.GetString(1), "O", CultureInfo.InvariantCulture);
                fullBindings = reader.IsDBNull(2) ? null : reader.GetString(2);
                safeBindings = reader.IsDBNull(3) ? null : reader.GetString(3);
            }
            transaction.Commit();
            return new MediaMergeRecoveryEvidence(operation, receipt, fingerprint, importedAtUtc, fullBindings, safeBindings);
        }, cancellationToken).ConfigureAwait(false);

        NativeMediaMergeOperation? op = evidence.Operation;
        if (op == null) return new(null, NativeMediaImportFilesystemStatus.MissingParent, [], evidence.Receipt);
        var children = ImmutableArray.CreateBuilder<NativeMediaImportChildInspection>();
        try
        {
            // Never follow a free SQL/package path, and never search another operation's parent.
            string expectedParent = Path.Combine(Path.GetDirectoryName(DatabasePath)!,
                "merge-media-operations", operationId.ToString("N"));
            if (!MediaImportPathsEqual(op.OperationsParent, expectedParent))
                throw new InvalidDataException("Invalid media merge operations parent.");
            ArchiveExportMetadataFile.RejectLinks(expectedParent);
            FileAttributes attributes;
            try { attributes = File.GetAttributes(expectedParent); }
            catch (FileNotFoundException)
            { return new(op, NativeMediaImportFilesystemStatus.MissingParent, [], evidence.Receipt); }
            catch (DirectoryNotFoundException)
            { return new(op, NativeMediaImportFilesystemStatus.MissingParent, [], evidence.Receipt); }
            if ((attributes & FileAttributes.Directory) == 0
                || (attributes & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
                throw new InvalidDataException("The media merge parent is not an ordinary directory.");

            // Bound enumeration BEFORE any child hashing. A 33rd entry invalidates the inventory;
            // do not silently clip it or recursively walk unknown/orphan contents.
            List<string> paths = new(MaximumMediaMergeRecoveryChildren);
            foreach (string child in Directory.EnumerateFileSystemEntries(expectedParent))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (paths.Count == MaximumMediaMergeRecoveryChildren)
                    throw new InvalidDataException("The media merge parent exceeds its inspection bound.");
                paths.Add(child);
            }
            paths.Sort(StringComparer.Ordinal);
            foreach (string child in paths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ArchiveExportMetadataFile.RejectLinks(child);
                bool directory = Directory.Exists(child);
                // These flags diagnose mkdir-before-journal residue; they are NOT readiness or
                // ownership proof and never replace the existing native/type/lease inspector.
                bool empty = directory && !Directory.EnumerateFileSystemEntries(child).Any();
                bool journalMissing = directory && !File.Exists(Path.Combine(child, ".meowchan-import-candidate.json"));
                PassiveMediaCandidateInspection inspected = await store.InspectPassiveImportCandidateAsync(child, cancellationToken)
                    .ConfigureAwait(false);
                bool matches = inspected.Status == PassiveMediaCandidateStatus.Ready && inspected.Candidate != null
                    && MediaMergeRecoveryCandidateMatches(evidence, inspected.Candidate);
                children.Add(new(child, inspected.Status, matches, empty, journalMissing));
            }
            cancellationToken.ThrowIfCancellationRequested();
            return new(op, paths.Count == 0 ? NativeMediaImportFilesystemStatus.EmptyParent : NativeMediaImportFilesystemStatus.Inspected,
                children.ToImmutable(), evidence.Receipt);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException
            or NotSupportedException or System.Security.SecurityException or ArgumentException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new(op, NativeMediaImportFilesystemStatus.Invalid, children.ToImmutable(), evidence.Receipt);
        }
        // Deliberately no repair, SQL/manifest mutation, new roots, resume, deletion or activation.
    }

    private Task<T> ReadMediaMergeRecoveryAsync<T>(Func<SqliteConnection, T> read, CancellationToken token)
        => ReadMediaImportIndependentAsync(connection =>
        {
            // Existing helper uses SqliteOpenMode.ReadOnly, validates schema, propagates SQL/IO
            // errors and cancellation, and does not depend on a still-live catalog writer lease.
            if (ArchiveSql.GetPragma(connection, "user_version") < 8)
                throw new InvalidDataException("Media merge operations require schema 8.");
            return read(connection);
        }, token);

    private static bool MediaMergeRecoveryCandidateMatches(MediaMergeRecoveryEvidence evidence,
        PassiveMediaImportCandidate candidate)
    {
        NativeMediaMergeOperation op = evidence.Operation!;
        if (candidate.OperationId != op.OperationId || candidate.Resource != op.Resource
            || candidate.PackageFingerprint != evidence.PackageFingerprint || candidate.ImportedAtUtc != evidence.ImportedAtUtc
            || candidate.Bindings.IsDefault)
            return false;
        string[] keys = candidate.Bindings.Select(binding => binding.Reference.RevisionKey)
            .OrderBy(key => key, StringComparer.Ordinal).ToArray();
        bool fullKeys = keys.SequenceEqual(op.FullKeys, StringComparer.Ordinal);
        bool safeKeys = !op.SafeKeys.IsEmpty && keys.SequenceEqual(op.SafeKeys, StringComparer.Ordinal);
        if (op.State == NativeMediaImportOperationState.Preparing)
        {
            // A Ready child can precede the SQL Prepared marker. Associate only exact full/safe
            // selections and operation provenance; this does not authorize resuming the old fence.
            return fullKeys || safeKeys;
        }
        string hash = ArchiveSql.MediaImportBindingsHash(candidate);
        bool full = fullKeys && op.FullDirectory != null && MediaImportPathsEqual(candidate.Directory, op.FullDirectory)
            && candidate.ManifestGeneration == op.FullManifestGeneration && hash == evidence.FullBindingsSha256;
        bool safe = safeKeys && op.SafeDirectory != null && MediaImportPathsEqual(candidate.Directory, op.SafeDirectory)
            && candidate.ManifestGeneration == op.SafeManifestGeneration && hash == evidence.SafeBindingsSha256;
        // A reused full/safe root must satisfy BOTH SQL bindings, not merely one side of an alias.
        return op.FullDirectory != null && op.SafeDirectory != null && MediaImportPathsEqual(op.FullDirectory, op.SafeDirectory)
            ? full && safe : full || safe;
    }

    private sealed record MediaMergeRecoveryEvidence(NativeMediaMergeOperation? Operation, NativeMediaMergeResult? Receipt,
        string? PackageFingerprint, DateTimeOffset? ImportedAtUtc, string? FullBindingsSha256, string? SafeBindingsSha256);
}
