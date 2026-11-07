using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using MeowChan.Storage;
using MeowChan.Transfer;

namespace MeowChan.Archive;

public sealed partial class ArchiveCatalog
{
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<NativeMediaImportPlan, object> _issuedNativeMediaPlans = new();

    internal async Task<NativeMediaImportPreparation> PrepareNativeMediaImportAsync(NativeTransferImportStage stage,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stage);
        cancellationToken.ThrowIfCancellationRequested();
        // The opaque stage was fully validated at creation. Snapshot its immutable package once;
        // do not reopen/re-inspect its source or read fresh metadata after issuing the plan.
        NativeTransferPackage package = stage.Package;
        // These original-only materializers cannot preserve nested representations or
        // original byte-version evidence. Reject the admitted wire schema before any
        // target lookup, even when hashes alias or the DTO could downlevel.
        if (package.IsChanV4) return new(null, NativeMediaImportError.UnsupportedPackage);
        if (package.Catalog.Threads.Length != 1) return new(null, NativeMediaImportError.UnsupportedPackage);
        TransferThread thread = package.Catalog.Threads[0];
        if (!thread.RetainedBindings.IsEmpty || thread.Posts.Any(post => post.AttachmentsWereNull))
            return new(null, NativeMediaImportError.UnsupportedPackage);
        if (thread.PageUrl == null) return new(null, NativeMediaImportError.MissingPageUrl);
        var attachments = ImmutableArray.CreateBuilder<NativeMediaImportAttachment>();
        HashSet<string> keys = new(StringComparer.Ordinal), payloads = new(StringComparer.Ordinal);
        foreach (TransferPost post in thread.Posts)
        foreach (TransferAttachment attachment in post.Attachments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CapturedAttachment raw = attachment.Metadata;
            if (attachment.Role != TransferMediaRole.Original || attachment.Status != TransferContentStatus.Contained
                || raw.Variant != "original" || raw.MediaId == null || raw.Url == null || raw.PlannedPath != null
                || attachment.PayloadSha256 == null || !package.Envelope.MeasuredPayloads.ContainsKey(attachment.PayloadSha256))
                return new(null, NativeMediaImportError.UnsupportedPackage);
            MediaProviderMetadata provider = new(raw.MediaId, raw.Variant, raw.Url, raw.OriginalFileName,
                raw.ExpectedSize, raw.ExpectedMd5, raw.ExpectedSha256)
                { ExpectedSha1 = raw.ExpectedSha1, ProviderSizeHint = raw.ProviderSizeHint };
            string key = MediaRevisionKeys.CreateAttachmentKey(thread.Id, post.Metadata.PostId, raw.Position, provider);
            if (!keys.Add(key)) return new(null, NativeMediaImportError.UnsupportedPackage);
            payloads.Add(attachment.PayloadSha256);
            attachments.Add(new(post.Metadata.PostId, key, attachment, provider));
        }
        // Whole-package gate, not merely the candidate producer's selected-key gate. No hidden
        // payload, private extension, preview, null collection or retained binding is dropped.
        if (attachments.Count == 0 || payloads.Count != package.Envelope.Payloads.Length
            || package.Envelope.Payloads.Any(payload => !payloads.Contains(payload.Sha256)))
            return new(null, NativeMediaImportError.UnsupportedPackage);
        ImmutableArray<NativeMediaImportAttachment> frozen = attachments.ToImmutable();
        return await ReadAsync(connection =>
        {
            using SqliteTransaction transaction = connection.BeginTransaction(deferred: true);
            (string instance, long generation) = ArchiveSql.ReadImportFence(connection, transaction);
            if (!ArchiveSql.MediaImportResourceAbsent(connection, transaction, thread.Id))
                return new NativeMediaImportPreparation(null, NativeMediaImportError.ResourceExists);
            Guid operation = Guid.NewGuid();
            string parent = MediaImportOperationsParent(operation);
            string request = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                "native-new-media-v1\0" + operation.ToString("D") + "\0" + package.Envelope.Fingerprint + "\0"
                + instance + "\0" + generation.ToString(CultureInfo.InvariantCulture)))).ToLowerInvariant();
            cancellationToken.ThrowIfCancellationRequested(); transaction.Commit();
            NativeMediaImportPlan plan = new(this, stage, package, instance, generation, operation, request, parent, frozen);
            _issuedNativeMediaPlans.Add(plan, new object());
            return new NativeMediaImportPreparation(plan, NativeMediaImportError.None);
        }, cancellationToken).ConfigureAwait(false);
    }

    internal async Task<NativeMediaImportCommitResult> CommitNativeMediaImportAsync(NativeMediaImportPlan plan, MediaStore store,
        CancellationToken cancellationToken = default, Action<NativeMediaImportPhase>? checkpoint = null, Action? beforeAdmission = null)
    {
        ArgumentNullException.ThrowIfNull(plan); ArgumentNullException.ThrowIfNull(store);
        if (!ReferenceEquals(plan.Owner, this) || !_issuedNativeMediaPlans.TryGetValue(plan, out _))
            return new(NativeMediaImportCommitStatus.StalePlan);
        try
        {
            // Replay has priority over cancellation, owner disposal, freshness and ALL filesystem
            // access. Deletion/recreation and lost files cannot revoke historical SQL success.
            NativeMediaImportCommitResult? replay = await ReadNativeMediaImportReceiptAsync(plan.OperationId,
                plan.RequestSha256, CancellationToken.None).ConfigureAwait(false);
            if (replay != null) return replay;
            beforeAdmission?.Invoke(); // Narrow observer after the negative read, outside the SQL/ProfileLease lock.
            NativeMediaImportCommitStatus admission = await WriteAsync((connection, transaction) =>
            {
                // The first independent lookup can race with committed-only operation cleanup.
                // Recheck the content-free receipt in this same admission transaction before
                // evaluating operation metadata or the now-advanced content fence.
                if (ArchiveSql.ReadMediaImportReceipt(connection, transaction, plan.OperationId, plan.RequestSha256) != null)
                    return NativeMediaImportCommitStatus.Committed;
                NativeMediaImportOperation? existing = ArchiveSql.ReadMediaImportOperation(connection, transaction, plan.OperationId, plan.RequestSha256);
                if (existing != null) return existing.State == NativeMediaImportOperationState.Committed
                    ? NativeMediaImportCommitStatus.Committed : NativeMediaImportCommitStatus.ReconciliationRequired;
                if (!MediaImportFresh(connection, transaction, plan)) return NativeMediaImportCommitStatus.StalePlan;
                // This transaction's actual INSERT winner is the only filesystem admission.
                // Existing Preparing/Prepared rows never grant another attempt, even to this plan.
                return ArchiveSql.ClaimMediaImport(connection, transaction, plan)
                    ? NativeMediaImportCommitStatus.Inactive : NativeMediaImportCommitStatus.ReconciliationRequired;
            }, cancellationToken).ConfigureAwait(false);
            if (admission == NativeMediaImportCommitStatus.Committed)
                return (await ReadNativeMediaImportReceiptAsync(plan.OperationId, plan.RequestSha256, CancellationToken.None).ConfigureAwait(false))
                    ?? new(NativeMediaImportCommitStatus.Unknown);
            if (admission != NativeMediaImportCommitStatus.Inactive) return new(admission);
            checkpoint?.Invoke(NativeMediaImportPhase.PreparingSaved);
            cancellationToken.ThrowIfCancellationRequested();
            CreateMediaImportOperationsParent(plan.OperationsParent);
            checkpoint?.Invoke(NativeMediaImportPhase.OperationsParentCreated);
            PassiveMediaImportCandidate candidate = await store.MaterializePassiveImportCandidateAsync(plan, cancellationToken,
                directory => checkpoint?.Invoke(NativeMediaImportPhase.CandidateDirectoryCreated)).ConfigureAwait(false);
            checkpoint?.Invoke(NativeMediaImportPhase.CandidateReady);
            string bindingsHash = ArchiveSql.MediaImportBindingsHash(candidate);
            await WriteAsync((connection, transaction) =>
            {
                ArchiveSql.MarkMediaImportPrepared(connection, transaction, plan, candidate, bindingsHash);
                return true;
            }, cancellationToken).ConfigureAwait(false);
            checkpoint?.Invoke(NativeMediaImportPhase.PreparedSaved);
            return await store.WithVerifiedPassiveImportCandidateAsync(plan, candidate, async () =>
            {
                checkpoint?.Invoke(NativeMediaImportPhase.CandidateVerified);
                NativeMediaImportCommitResult result = await WriteAsync((connection, transaction) =>
                {
                    NativeMediaImportCommitResult? receipt = ArchiveSql.ReadMediaImportReceipt(connection, transaction, plan.OperationId, plan.RequestSha256);
                    if (receipt != null) return receipt;
                    if (!ArchiveSql.MediaImportOperationMatches(connection, transaction, plan, NativeMediaImportOperationState.Prepared, candidate, bindingsHash)
                        || !MediaImportFresh(connection, transaction, plan))
                        return new NativeMediaImportCommitResult(NativeMediaImportCommitStatus.StalePlan);
                    ArchiveSql.InsertMediaImportContent(connection, transaction, plan, candidate);
                    ArchiveSql.FinishMediaImport(connection, transaction, plan);
                    checkpoint?.Invoke(NativeMediaImportPhase.BeforeSqlCommit);
                    return new NativeMediaImportCommitResult(NativeMediaImportCommitStatus.Committed,
                        1, plan.Thread.Posts.Length, plan.Attachments.Length);
                }, cancellationToken).ConfigureAwait(false);
                if (result.Status == NativeMediaImportCommitStatus.Committed) checkpoint?.Invoke(NativeMediaImportPhase.SqlCommitted);
                return result;
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            // Includes cancellation/owner disposal and observer/lease errors AFTER a possible
            // commit. Never use the cancelled token or the disposed owner for reconciliation.
            try
            {
                return await ReadMediaImportIndependentAsync(connection =>
                {
                    // Receipt, operation and resource-wide claims must share one snapshot.
                    // A missing receipt alone says nothing about a durable candidate/claim.
                    using SqliteTransaction transaction = connection.BeginTransaction(deferred: true);
                    NativeMediaImportCommitResult? receipt = ArchiveSql.ReadMediaImportReceipt(connection, transaction,
                        plan.OperationId, plan.RequestSha256);
                    if (receipt != null)
                    {
                        transaction.Commit();
                        return receipt with { Diagnostic = error.GetType().Name };
                    }
                    var fence = ArchiveSql.ReadImportFence(connection, transaction);
                    NativeMediaImportOperation? operation = ArchiveSql.ReadMediaImportOperation(connection, transaction,
                        plan.OperationId, plan.RequestSha256);
                    NativeMediaImportCommitStatus status;
                    if (fence.Item1 != plan.Instance || operation?.State == NativeMediaImportOperationState.Committed)
                        status = NativeMediaImportCommitStatus.Unknown;
                    else if (operation != null || ArchiveSql.HasUnresolvedMediaOperation(connection, transaction, plan.Resource))
                        status = NativeMediaImportCommitStatus.ReconciliationRequired;
                    else
                        // Absence is safe only on the original unchanged new-resource target.
                        // Replacement/drift is not proof that this attempt never persisted.
                        status = MediaImportFresh(connection, transaction, plan)
                            ? NativeMediaImportCommitStatus.Inactive : NativeMediaImportCommitStatus.Unknown;
                    transaction.Commit();
                    return new NativeMediaImportCommitResult(status, Diagnostic: error.GetType().Name);
                }, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception reconciliationError)
            {
                return new(NativeMediaImportCommitStatus.Unknown,
                    Diagnostic: error.GetType().Name + "/receipt:" + reconciliationError.GetType().Name);
            }
        }
    }

    private static bool MediaImportFresh(SqliteConnection connection, SqliteTransaction transaction, NativeMediaImportPlan plan)
    {
        (string instance, long generation) = ArchiveSql.ReadImportFence(connection, transaction);
        return instance == plan.Instance && generation == plan.Generation
            && ArchiveSql.MediaImportResourceAbsent(connection, transaction, plan.Resource);
    }

    // Read-only and independent of the original ProfileLease/ArchiveCatalog lifetime. SQLite
    // performs its own bounded lock wait. Errors propagate: unreadable is never "no receipt".
    internal Task<NativeMediaImportCommitResult?> ReadNativeMediaImportReceiptAsync(Guid operationId, string requestSha256,
        CancellationToken cancellationToken = default)
    {
        ArchiveSql.ValidateMediaImportIdentity(operationId, requestSha256);
        return ReadMediaImportIndependentAsync(connection => ArchiveSql.ReadMediaImportReceipt(connection, null,
            operationId, requestSha256), cancellationToken);
    }

    internal Task<NativeMediaImportOperationPage> ReadNativeMediaImportOperationsPageAsync(Guid? afterOperationId = null,
        CancellationToken cancellationToken = default)
        => ReadMediaImportIndependentAsync(connection => ArchiveSql.ReadMediaImportOperationsPage(connection, afterOperationId), cancellationToken);

    private Task<T> ReadMediaImportIndependentAsync<T>(Func<SqliteConnection, T> read, CancellationToken token)
        => Task.Run(() =>
        {
            token.ThrowIfCancellationRequested();
            using SqliteConnection connection = ArchiveSql.OpenConnection(DatabasePath, SqliteOpenMode.ReadOnly);
            ArchiveSql.ValidateSchema(connection);
            if (ArchiveSql.GetPragma(connection, "user_version") < 7) throw new InvalidDataException("Media import operations require schema 7.");
            T result = read(connection);
            token.ThrowIfCancellationRequested();
            return result;
        }, token);

    private string MediaImportOperationsParent(Guid operation)
        => Path.Combine(Path.GetDirectoryName(DatabasePath)!, "import-media-operations", operation.ToString("N"));

    private static void CreateMediaImportOperationsParent(string parent)
    {
        ArchiveExportMetadataFile.RejectLinks(parent);
        if (Directory.Exists(parent) || File.Exists(parent))
            throw new InvalidDataException("The media import operations parent is already occupied.");
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(parent);
        else Directory.CreateDirectory(parent, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        ArchiveExportMetadataFile.RejectLinks(parent);
        if (Directory.EnumerateFileSystemEntries(parent).Any()) throw new InvalidDataException("The media import parent is not empty.");
    }

    internal async Task<NativeMediaImportOperationInspection> InspectNativeMediaImportOperationAsync(Guid operationId,
        string requestSha256, MediaStore store, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArchiveSql.ValidateMediaImportIdentity(operationId, requestSha256);
        var evidence = await ReadMediaImportIndependentAsync(connection =>
        {
            // A single read transaction binds metadata, receipt and expected binding/key hashes.
            using SqliteTransaction transaction = connection.BeginTransaction(deferred: true);
            NativeMediaImportCommitResult? storedReceipt = ArchiveSql.ReadMediaImportReceipt(connection, transaction, operationId, requestSha256);
            NativeMediaImportOperation? operation = ArchiveSql.ReadMediaImportOperation(connection, transaction, operationId, requestSha256);
            using SqliteCommand command = ArchiveSql.Command(connection, transaction,
                "SELECT revision_keys_json,bindings_sha256 FROM archive_media_import_operations WHERE operation_id=$op");
            command.Parameters.AddWithValue("$op", operationId.ToString("D"));
            using SqliteDataReader reader = command.ExecuteReader();
            string? keys = null, bindings = null;
            if (reader.Read()) { keys = reader.GetString(0); bindings = reader.IsDBNull(1) ? null : reader.GetString(1); }
            reader.Close(); transaction.Commit();
            return (Operation: operation, Keys: keys, Bindings: bindings, Receipt: storedReceipt);
        }, cancellationToken).ConfigureAwait(false);
        NativeMediaImportOperation? op = evidence.Operation;
        NativeMediaImportCommitResult? receipt = evidence.Receipt;
        if (op == null) return new(null, NativeMediaImportFilesystemStatus.MissingParent, [], receipt);
        var children = ImmutableArray.CreateBuilder<NativeMediaImportChildInspection>();
        try
        {
            // Never follow SQL/package supplied arbitrary paths or recursively search other roots.
            if (!MediaImportPathsEqual(op.OperationsParent, MediaImportOperationsParent(operationId)))
                throw new InvalidDataException("Invalid media import operations parent.");
            ArchiveExportMetadataFile.RejectLinks(op.OperationsParent);
            if (!Directory.Exists(op.OperationsParent))
                return new(op, File.Exists(op.OperationsParent) ? NativeMediaImportFilesystemStatus.Invalid
                    : NativeMediaImportFilesystemStatus.MissingParent, [], receipt);
            foreach (string child in Directory.EnumerateFileSystemEntries(op.OperationsParent))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (children.Count == 32) throw new InvalidDataException("The media import parent exceeds its inspection bound.");
                ArchiveExportMetadataFile.RejectLinks(child);
                bool empty = Directory.Exists(child) && !Directory.EnumerateFileSystemEntries(child).Any();
                bool journalMissing = Directory.Exists(child) && !File.Exists(Path.Combine(child, ".meowchan-import-candidate.json"));
                PassiveMediaCandidateInspection inspected = await store.InspectPassiveImportCandidateAsync(child, cancellationToken).ConfigureAwait(false);
                PassiveMediaImportCandidate? candidate = inspected.Candidate;
                bool matches = candidate != null && candidate.OperationId == op.OperationId && candidate.Resource == op.Resource
                    && candidate.PackageFingerprint == op.PackageFingerprint && candidate.ImportedAtUtc == op.ImportedAtUtc
                    && (op.CandidateDirectory == null || MediaImportPathsEqual(candidate.Directory, op.CandidateDirectory))
                    && (op.ManifestGeneration == null || candidate.ManifestGeneration == op.ManifestGeneration)
                    && System.Text.Json.JsonSerializer.Serialize(candidate.Bindings.Select(b => b.Reference.RevisionKey).OrderBy(k => k, StringComparer.Ordinal)) == evidence.Keys
                    && (evidence.Bindings == null || ArchiveSql.MediaImportBindingsHash(candidate) == evidence.Bindings);
                children.Add(new(child, inspected.Status, matches, empty, journalMissing));
            }
            return new(op, children.Count == 0 ? NativeMediaImportFilesystemStatus.EmptyParent : NativeMediaImportFilesystemStatus.Inspected,
                children.ToImmutable(), receipt);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or NotSupportedException
            or System.Security.SecurityException or ArgumentException)
        { return new(op, NativeMediaImportFilesystemStatus.Invalid, children.ToImmutable(), receipt); }
        // No repair, resume, replacement candidate, deletion or activation on discovery.
    }

    private static bool MediaImportPathsEqual(string first, string second)
        => string.Equals(first, second, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
