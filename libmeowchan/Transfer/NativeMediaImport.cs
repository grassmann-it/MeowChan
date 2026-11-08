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
using MeowChan.Persistence;
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
        if (package.Catalog.Threads.Length != 1 || package.PrivateMetadata != null)
            return new(null, NativeMediaImportError.UnsupportedPackage);
        TransferThread thread = package.Catalog.Threads[0];
        if (thread.PageUrl == null) return new(null, NativeMediaImportError.MissingPageUrl);
        NativeMediaMaterializationPlan? representation = null;
        if (package.IsChanV4)
        {
            representation = NativeMediaMaterializationPlan.Create(stage, thread.Id, cancellationToken);
            // Text-only/rootless v4 is not silently projected through the legacy path.
            if (representation.FullSelection.IsEmpty) return new(null, NativeMediaImportError.UnsupportedPackage);
        }
        else if (!thread.RetainedBindings.IsEmpty || thread.Posts.Any(post => post.AttachmentsWereNull))
            return new(null, NativeMediaImportError.UnsupportedPackage);
        var attachments = ImmutableArray.CreateBuilder<NativeMediaImportAttachment>();
        if (representation is null)
        {
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
        }
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
                (representation is null ? "native-new-media-v1\0" : "native-new-media-v2\0") + operation.ToString("D") + "\0" + package.Envelope.Fingerprint + "\0"
                + instance + "\0" + generation.ToString(CultureInfo.InvariantCulture)))).ToLowerInvariant();
            cancellationToken.ThrowIfCancellationRequested(); transaction.Commit();
            NativeMediaImportPlan plan = representation is null
                ? new(this, stage, package, instance, generation, operation, request, parent, frozen)
                : new(this, representation, instance, generation, operation, request, parent);
            _issuedNativeMediaPlans.Add(plan, new object());
            return new NativeMediaImportPreparation(plan, NativeMediaImportError.None);
        }, cancellationToken).ConfigureAwait(false);
    }

    internal NativeMediaImportV2State RequireIssuedNativeMediaImportV2Plan(ProfileAdmissionToken admission,
        NativeMediaImportPlan plan)
    {
        EnsureTransferAdmission(admission);
        ArgumentNullException.ThrowIfNull(plan);
        if (!ReferenceEquals(plan.Owner, this) || !_issuedNativeMediaPlans.TryGetValue(plan, out _)
            || plan.Representation is not { } representation)
            throw new InvalidOperationException("Representation import requires the exact owner-issued plan.");
        return representation;
    }

    // No tokenless profile mutation entry. The caller borrows the live lane's Exclusive
    // for the entire Task, including independent reconciliation and all FS disposals.
    internal Task<ProfileExclusiveOutcome<NativeMediaImportCommitResult>> CommitNativeMediaImportAdmittedAsync(
        ProfileAdmissionToken admission, NativeMediaImportPlan plan, MediaStore store,
        CancellationToken cancellationToken = default, Action<NativeMediaImportPhase>? checkpoint = null,
        Action? beforeAdmission = null)
    {
        EnsureTransferAdmission(admission);
        ArgumentNullException.ThrowIfNull(plan); ArgumentNullException.ThrowIfNull(store);
        if (!ReferenceEquals(plan.Owner, this) || !_issuedNativeMediaPlans.TryGetValue(plan, out _))
            return RejectUnissuedNativeMediaImportPlanAsync();
        return CommitNativeMediaImportAdmittedCoreAsync(admission, plan, store, cancellationToken, checkpoint, beforeAdmission);
    }

    private async Task<ProfileExclusiveOutcome<NativeMediaImportCommitResult>> RejectUnissuedNativeMediaImportPlanAsync()
    {
        // Never trust a constructed plan's operation identity, paths or proposed deltas.
        // Rejection has no effects, but safe resume still needs a clean persistent profile.
        try
        {
            bool blocked = await ReadMediaImportIndependentAsync(connection =>
            {
                using SqliteTransaction transaction = connection.BeginTransaction(deferred: true);
                bool result = HasNativeMediaImportRecoveryBlocker(connection, transaction);
                transaction.Commit();
                return result;
            }, CancellationToken.None).ConfigureAwait(false);
            NativeMediaImportCommitResult stale = new(NativeMediaImportCommitStatus.StalePlan);
            return blocked ? ProfileExclusiveOutcome<NativeMediaImportCommitResult>.RecoveryRequired(stale)
                : ProfileExclusiveOutcome<NativeMediaImportCommitResult>.SafeToResume(stale);
        }
        catch (Exception error)
        {
            return ProfileExclusiveOutcome<NativeMediaImportCommitResult>.RecoveryRequired(
                new(NativeMediaImportCommitStatus.Unknown, Diagnostic: error.GetType().Name));
        }
    }

    private async Task<ProfileExclusiveOutcome<NativeMediaImportCommitResult>> CommitNativeMediaImportAdmittedCoreAsync(
        ProfileAdmissionToken admission, NativeMediaImportPlan plan, MediaStore store, CancellationToken cancellationToken,
        Action<NativeMediaImportPhase>? checkpoint, Action? beforeAdmission)
    {
        bool claimAttempted = false, durableOperationObserved = false;
        NativeMediaImportCommitResult? completed = null;
        Exception? failure = null;
        try { completed = await ExecuteAsync().ConfigureAwait(false); }
        catch (Exception error) { failure = error; }
        // ExecuteAsync has unwound every candidate handle/callback before safety is attested.
        return await ReconcileNativeMediaImportOutcomeAsync(plan, completed, failure,
            claimAttempted || durableOperationObserved).ConfigureAwait(false);

        async Task<NativeMediaImportCommitResult> ExecuteAsync()
        {
            // Replay has priority over cancellation, freshness and ALL filesystem
            // access. Deletion/recreation and lost files cannot revoke historical SQL success.
            NativeMediaImportCommitResult? replay = await ReadNativeMediaImportReceiptAsync(plan.OperationId,
                plan.RequestSha256, plan.PlanVersion, CancellationToken.None).ConfigureAwait(false);
            if (replay != null) return replay;
            NativeMediaPersistedImportOperation? preparing = null;
            beforeAdmission?.Invoke(); // Narrow observer after the negative read, outside the SQL/ProfileLease lock.
            NativeMediaImportCommitStatus claimed = await WriteTransferAdmittedAsync(admission, (connection, transaction) =>
            {
                // The first independent lookup can race with committed-only operation cleanup.
                // Recheck the content-free receipt in this same admission transaction before
                // evaluating operation metadata or the now-advanced content fence.
                if (ReadMediaImportReceiptForPlan(connection, transaction, plan) != null)
                    return NativeMediaImportCommitStatus.Committed;
                NativeMediaImportOperationState? existing = ReadMediaImportStateForPlan(connection, transaction, plan);
                if (existing != null)
                {
                    durableOperationObserved = true;
                    return existing == NativeMediaImportOperationState.Committed
                        ? NativeMediaImportCommitStatus.Committed : NativeMediaImportCommitStatus.ReconciliationRequired;
                }
                if (!MediaImportFresh(connection, transaction, plan)) return NativeMediaImportCommitStatus.StalePlan;
                // This transaction's actual INSERT winner is the only filesystem admission.
                // Existing Preparing/Prepared rows never grant another attempt, even to this plan.
                claimAttempted = true; // BEFORE INSERT/Commit/return: a lost response cannot erase uncertainty.
                bool won = plan.PlanVersion == 1 ? ArchiveSql.ClaimMediaImport(connection, transaction, plan)
                    : ArchiveSql.ClaimMediaImportV2(connection, transaction, plan);
                if (won && plan.PlanVersion == 2)
                {
                    preparing = ArchiveSql.ReadPersistedMediaImportOperation(connection, transaction, plan.OperationId, plan.RequestSha256)
                        ?? throw new InvalidDataException("The claimed representation operation is missing.");
                    if (!plan.Representation!.MatchesOperation(preparing, NativeMediaImportOperationState.Preparing))
                        throw new InvalidDataException("The representation import claim changed.");
                }
                return won ? NativeMediaImportCommitStatus.Inactive : NativeMediaImportCommitStatus.ReconciliationRequired;
            }, cancellationToken).ConfigureAwait(false);
            if (claimed == NativeMediaImportCommitStatus.Committed)
                return (await ReadNativeMediaImportReceiptAsync(plan.OperationId, plan.RequestSha256, plan.PlanVersion, CancellationToken.None).ConfigureAwait(false))
                    ?? new(NativeMediaImportCommitStatus.Unknown);
            if (claimed != NativeMediaImportCommitStatus.Inactive) return new(claimed);
            checkpoint?.Invoke(NativeMediaImportPhase.PreparingSaved);
            cancellationToken.ThrowIfCancellationRequested();
            CreateMediaImportOperationsParent(plan.OperationsParent);
            checkpoint?.Invoke(NativeMediaImportPhase.OperationsParentCreated);
            PassiveMediaImportCandidate candidate = plan.PlanVersion == 1
                ? await store.MaterializePassiveImportCandidateAdmittedAsync(admission, plan, cancellationToken,
                    _ => checkpoint?.Invoke(NativeMediaImportPhase.CandidateDirectoryCreated)).ConfigureAwait(false)
                : await store.MaterializePassiveImportCandidateAdmittedAsync(admission, plan, preparing!, cancellationToken,
                    _ => checkpoint?.Invoke(NativeMediaImportPhase.CandidateDirectoryCreated)).ConfigureAwait(false);
            checkpoint?.Invoke(NativeMediaImportPhase.CandidateReady);
            string? bindingsHash = plan.PlanVersion == 1 ? ArchiveSql.MediaImportBindingsHash(candidate) : null;
            NativeMediaPersistedImportOperation? prepared = await WriteTransferAdmittedAsync(admission, (connection, transaction) =>
            {
                if (plan.PlanVersion == 2) return ArchiveSql.MarkMediaImportPreparedV2(connection, transaction, plan, candidate);
                ArchiveSql.MarkMediaImportPrepared(connection, transaction, plan, candidate, bindingsHash!);
                return (NativeMediaPersistedImportOperation?)null;
            }, cancellationToken).ConfigureAwait(false);
            checkpoint?.Invoke(NativeMediaImportPhase.PreparedSaved);
            // Both versions enter the same final transaction, under their version-specific
            // verifier's *held* existing candidate lease. No inspect/release/commit gap.
            return plan.PlanVersion == 1
                ? await store.WithVerifiedPassiveImportCandidateAdmittedAsync(admission, plan, candidate, CommitVerifiedAsync, cancellationToken).ConfigureAwait(false)
                : await store.WithVerifiedPassiveImportCandidateAdmittedAsync(admission, plan, prepared!, CommitVerifiedAsync, cancellationToken).ConfigureAwait(false);

            async Task<NativeMediaImportCommitResult> CommitVerifiedAsync()
            {
                checkpoint?.Invoke(NativeMediaImportPhase.CandidateVerified);
                NativeMediaImportCommitResult result = await WriteTransferAdmittedAsync(admission, (connection, transaction) =>
                {
                    NativeMediaImportCommitResult? receipt = ReadMediaImportReceiptForPlan(connection, transaction, plan);
                    if (receipt != null) return receipt;
                    bool matches = plan.PlanVersion == 1
                        ? ArchiveSql.MediaImportOperationMatches(connection, transaction, plan, NativeMediaImportOperationState.Prepared, candidate, bindingsHash!)
                        : ArchiveSql.MediaImportOperationMatchesV2(connection, transaction, plan, NativeMediaImportOperationState.Prepared, candidate);
                    if (!matches || !MediaImportFresh(connection, transaction, plan))
                        return new NativeMediaImportCommitResult(NativeMediaImportCommitStatus.StalePlan);
                    NativeMediaImportCommitResult committed;
                    if (plan.PlanVersion == 1)
                    {
                        ArchiveSql.InsertMediaImportContent(connection, transaction, plan, candidate);
                        ArchiveSql.FinishMediaImport(connection, transaction, plan);
                        committed = new(NativeMediaImportCommitStatus.Committed, 1, plan.Thread.Posts.Length, plan.Attachments.Length);
                    }
                    else
                    {
                        ArchiveSql.InsertMediaImportContentV2(connection, transaction, plan, candidate);
                        ArchiveSql.FinishMediaImportV2(connection, transaction, plan, candidate);
                        committed = plan.Representation!.CommittedResult();
                    }
                    checkpoint?.Invoke(NativeMediaImportPhase.BeforeSqlCommit);
                    return committed;
                }, cancellationToken).ConfigureAwait(false);
                if (result.Status == NativeMediaImportCommitStatus.Committed) checkpoint?.Invoke(NativeMediaImportPhase.SqlCommitted);
                return result;
            }
        }
    }

    private async Task<ProfileExclusiveOutcome<NativeMediaImportCommitResult>> ReconcileNativeMediaImportOutcomeAsync(
        NativeMediaImportPlan plan, NativeMediaImportCommitResult? completed, Exception? failure, bool claimMayHavePersisted)
    {
        NativeMediaImportCommitResult? historicalReceipt = null;
        try
        {
            return await ReadMediaImportIndependentAsync(connection =>
            {
                using SqliteTransaction transaction = connection.BeginTransaction(deferred: true);
                historicalReceipt = ReadMediaImportReceiptForPlan(connection, transaction, plan);
                // Same snapshot, both families, whole profile: a historical success does not
                // authorize resuming over a separate unfinished or receipt-inconsistent operation.
                bool blocked = HasNativeMediaImportRecoveryBlocker(connection, transaction);
                var fence = ArchiveSql.ReadImportFence(connection, transaction);
                if (historicalReceipt != null)
                {
                    NativeMediaImportCommitResult success = historicalReceipt with
                    {
                        IsReplay = completed?.Status == NativeMediaImportCommitStatus.Committed
                            ? completed.IsReplay : true,
                        Diagnostic = failure?.GetType().Name ?? completed?.Diagnostic
                    };
                    transaction.Commit();
                    // The receipt proves history, not permission for this owner to resume
                    // a replaced catalog. Generation drift after commit is legitimate.
                    return blocked || fence.Item1 != plan.Instance
                        ? ProfileExclusiveOutcome<NativeMediaImportCommitResult>.RecoveryRequired(success)
                        : ProfileExclusiveOutcome<NativeMediaImportCommitResult>.SafeToResume(success);
                }
                NativeMediaImportOperationState? operation = ReadMediaImportStateForPlan(connection, transaction, plan);
                NativeMediaImportCommitStatus status;
                bool safeToResume = false;
                if (fence.Item1 != plan.Instance || operation == NativeMediaImportOperationState.Committed)
                    status = NativeMediaImportCommitStatus.Unknown;
                else if (operation != null || blocked)
                    status = completed?.Status == NativeMediaImportCommitStatus.StalePlan
                        ? NativeMediaImportCommitStatus.StalePlan : NativeMediaImportCommitStatus.ReconciliationRequired;
                else if (claimMayHavePersisted || completed?.Status is NativeMediaImportCommitStatus.Committed
                    or NativeMediaImportCommitStatus.Unknown or NativeMediaImportCommitStatus.ReconciliationRequired)
                    // Even absent metadata cannot prove rollback after a possibly committed claim.
                    // The filesystem may already contain children whose SQL row disappeared.
                    status = NativeMediaImportCommitStatus.Unknown;
                else if (completed?.Status == NativeMediaImportCommitStatus.StalePlan)
                {
                    status = NativeMediaImportCommitStatus.StalePlan; // explicit pre-effect rejection
                    safeToResume = true;
                }
                else
                {
                    safeToResume = MediaImportFresh(connection, transaction, plan);
                    status = safeToResume ? NativeMediaImportCommitStatus.Inactive : NativeMediaImportCommitStatus.Unknown;
                }
                transaction.Commit();
                NativeMediaImportCommitResult result = new(status, Diagnostic: failure?.GetType().Name ?? completed?.Diagnostic);
                return safeToResume
                    ? ProfileExclusiveOutcome<NativeMediaImportCommitResult>.SafeToResume(result)
                    : ProfileExclusiveOutcome<NativeMediaImportCommitResult>.RecoveryRequired(result);
            }, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception reconciliationError)
        {
            // A receipt already read is historical success even if the blocker/read tail fails;
            // without a complete negative blocker proof the lane must nevertheless remain sealed.
            string diagnostic = (failure?.GetType().Name ?? completed?.Diagnostic ?? "reconciliation")
                + "/receipt:" + reconciliationError.GetType().Name;
            return ProfileExclusiveOutcome<NativeMediaImportCommitResult>.RecoveryRequired(
                historicalReceipt is null
                    ? new(NativeMediaImportCommitStatus.Unknown, Diagnostic: diagnostic)
                    : historicalReceipt with { Diagnostic = diagnostic });
        }
    }

    // Same supported-version consistency predicate as startup, on this independent
    // snapshot only, after candidate callbacks and SQL/lease consumers have unwound.
    private static bool HasNativeMediaImportRecoveryBlocker(SqliteConnection connection, SqliteTransaction transaction)
        => ArchiveSql.HasNativeMediaTransferRecoveryBlocker(connection, transaction);

    private static NativeMediaImportCommitResult? ReadMediaImportReceiptForPlan(SqliteConnection connection,
        SqliteTransaction transaction, NativeMediaImportPlan plan)
        => plan.PlanVersion == 1
            ? ArchiveSql.ReadMediaImportReceipt(connection, transaction, plan.OperationId, plan.RequestSha256)
            : ArchiveSql.ReadMediaImportReceiptV2(connection, transaction, plan.OperationId, plan.RequestSha256);

    private static NativeMediaImportOperationState? ReadMediaImportStateForPlan(SqliteConnection connection,
        SqliteTransaction transaction, NativeMediaImportPlan plan)
    {
        if (plan.PlanVersion == 1)
            return ArchiveSql.ReadMediaImportOperation(connection, transaction, plan.OperationId, plan.RequestSha256)?.State;
        NativeMediaPersistedImportOperation? operation = ArchiveSql.ReadPersistedMediaImportOperation(connection, transaction,
            plan.OperationId, plan.RequestSha256);
        if (operation != null && !plan.Representation!.MatchesOperation(operation, operation.State))
            throw new InvalidDataException("The persisted representation import differs from its issued plan.");
        return operation?.State;
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

    // Explicit versioned historical query. The legacy Guid/request overload remains
    // v1-only; a caller cannot accidentally erase v2 counters through that projection.
    internal Task<NativeMediaImportCommitResult?> ReadNativeMediaImportReceiptAsync(Guid operationId, string requestSha256,
        int planVersion, CancellationToken cancellationToken = default)
    {
        ArchiveSql.ValidateMediaImportIdentity(operationId, requestSha256);
        if (planVersion is not (1 or 2)) throw new ArgumentOutOfRangeException(nameof(planVersion));
        return ReadMediaImportIndependentAsync(connection => planVersion == 1
            ? ArchiveSql.ReadMediaImportReceipt(connection, null, operationId, requestSha256)
            : ArchiveSql.ReadMediaImportReceiptV2(connection, null, operationId, requestSha256), cancellationToken);
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
                // Journal1 operation identity cannot be proved by a Journal2 Bindings projection.
                bool matches = candidate != null && candidate.IsLegacyCandidate
                    && candidate.OperationId == op.OperationId && candidate.Resource == op.Resource
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
