using System;
using System.IO;
using System.Linq;
using System.Collections.Immutable;
using MeowChan.Storage;
using MeowChan.Transfer;
using Microsoft.Data.Sqlite;

namespace MeowChan.Archive;

internal static partial class ArchiveSql
{
    // Caller owns Exclusive, the fresh catalog fence, and verified filesystem leases
    // THROUGH this transaction. Persisted evidence is never an execution capability.
    internal static bool ClaimMediaMergeV2(SqliteConnection connection, SqliteTransaction transaction,
        NativeMediaMergePlan plan)
    {
        NativeMediaMergeV2State state = RequireMediaMergeV2Write(connection, transaction, plan);
        if (ReadPersistedMediaMergeReceipt(connection, transaction, plan.OperationId, plan.RequestSha256, 2) != null)
            return false;
        if (HasIncompatibleMediaTransferHistory(connection, transaction)) return false;
        using SqliteCommand command = Command(connection, transaction, """
            INSERT INTO archive_media_merge_operations(
                operation_id,request_sha256,package_fingerprint,catalog_instance,catalog_generation,
                source,board,thread,operations_parent,imported_at,state,roots_json,
                full_keys_json,safe_keys_json,catalog_bytes,plan_version,
                full_selection_json,full_selection_sha256,full_member_count,
                safe_selection_json,safe_selection_sha256,safe_member_count,
                new_posts,updated_posts,new_attachments,new_bindings,conflicts,
                evidence_additions,derived_descriptor_additions,contained_derived_additions)
            SELECT $op,$request,$fingerprint,$instance,$generation,
                $source,$board,$thread,$parent,$at,'Preparing',$roots,'[]','[]',$catalog,2,
                $fullSelection,$fullHash,$fullCount,$safeSelection,$safeHash,$safeCount,
                $posts,$updated,$attachments,$bindings,$conflicts,$evidence,$derived,$contained
            WHERE NOT EXISTS (SELECT 1 FROM archive_media_merge_operations
                WHERE source=$source AND board=$board AND thread=$thread AND state IN ('Preparing','Prepared'))
              AND NOT EXISTS (SELECT 1 FROM archive_media_import_operations
                WHERE source=$source AND board=$board AND thread=$thread AND state IN ('Preparing','Prepared'))
              AND NOT EXISTS (SELECT 1 FROM archive_media_merge_receipts WHERE operation_id=$op)
            ON CONFLICT(operation_id) DO NOTHING
            """);
        AddId(command, plan.Resource);
        AddMediaMergeV2Identity(command, plan);
        command.Parameters.AddWithValue("$fingerprint", plan.Package.Envelope.Fingerprint);
        command.Parameters.AddWithValue("$instance", plan.Instance);
        command.Parameters.AddWithValue("$generation", plan.Generation);
        command.Parameters.AddWithValue("$parent", plan.OperationsParent);
        command.Parameters.AddWithValue("$at", Date(plan.ImportedAtUtc));
        command.Parameters.AddWithValue("$roots", state.RootsJson);
        command.Parameters.AddWithValue("$catalog", state.CatalogBytes.ToArray());
        command.Parameters.AddWithValue("$fullSelection", state.FullSelection.CanonicalJson);
        command.Parameters.AddWithValue("$fullHash", state.FullSelection.Sha256);
        command.Parameters.AddWithValue("$fullCount", state.FullSelection.Members.Length);
        command.Parameters.AddWithValue("$safeSelection", state.SafeSelection.IsEmpty ? DBNull.Value : (object)state.SafeSelection.CanonicalJson);
        command.Parameters.AddWithValue("$safeHash", state.SafeSelection.IsEmpty ? DBNull.Value : (object)state.SafeSelection.Sha256);
        command.Parameters.AddWithValue("$safeCount", state.SafeSelection.Members.Length);
        command.Parameters.AddWithValue("$posts", state.Counts.NewPosts);
        command.Parameters.AddWithValue("$updated", state.Counts.UpdatedPosts);
        command.Parameters.AddWithValue("$attachments", state.Counts.NewAttachments);
        command.Parameters.AddWithValue("$bindings", state.Counts.NewBindings);
        command.Parameters.AddWithValue("$conflicts", state.Counts.Conflicts);
        command.Parameters.AddWithValue("$evidence", state.Counts.EvidenceAdditions);
        command.Parameters.AddWithValue("$derived", state.Counts.DerivedDescriptorAdditions);
        command.Parameters.AddWithValue("$contained", state.Counts.ContainedDerivedAdditions);
        // Only an actual INSERT winner may materialize. No resume grant from old rows.
        if (command.ExecuteNonQuery() != 1) return false;
        if (!MediaMergeOperationMatchesV2(connection, transaction, plan, NativeMediaImportOperationState.Preparing))
            throw new InvalidDataException("The representation merge claim differs from the issued plan.");
        return true;
    }

    internal static NativeMediaPersistedMergeOperation MarkMediaMergePreparedV2(SqliteConnection connection,
        SqliteTransaction transaction, NativeMediaMergePlan plan, PassiveMediaImportCandidate full,
        PassiveMediaImportCandidate? safe)
    {
        NativeMediaMergeV2State state = RequireMediaMergeV2Write(connection, transaction, plan);
        RequireNoMediaMergeV2Receipt(connection, transaction, plan);
        if (!MediaMergeCandidatesMatchV2(plan, state, full, safe)
            || !MediaMergeOperationMatchesV2(connection, transaction, plan, NativeMediaImportOperationState.Preparing))
            throw new InvalidDataException("The representation merge preparation changed.");
        using (SqliteCommand command = Command(connection, transaction, """
            UPDATE archive_media_merge_operations SET state='Prepared',
                full_directory=$full,safe_directory=$safe,
                full_manifest_generation=$fullGeneration,safe_manifest_generation=$safeGeneration,
                full_inventory_sha256=$fullInventory,safe_inventory_sha256=$safeInventory
            WHERE operation_id=$op AND request_sha256=$request AND plan_version=2 AND state='Preparing'
            """))
        {
            AddMediaMergeV2Identity(command, plan);
            command.Parameters.AddWithValue("$full", full.Directory);
            command.Parameters.AddWithValue("$safe", (object?)safe?.Directory ?? DBNull.Value);
            command.Parameters.AddWithValue("$fullGeneration", full.ManifestGeneration);
            command.Parameters.AddWithValue("$safeGeneration", (object?)safe?.ManifestGeneration ?? DBNull.Value);
            command.Parameters.AddWithValue("$fullInventory", state.FullInventory.Sha256);
            command.Parameters.AddWithValue("$safeInventory", (object?)state.SafeInventory?.Sha256 ?? DBNull.Value);
            if (command.ExecuteNonQuery() != 1)
                throw new InvalidDataException("The representation merge preparation changed.");
        }
        if (!MediaMergeOperationMatchesV2(connection, transaction, plan, NativeMediaImportOperationState.Prepared, full, safe))
            throw new InvalidDataException("The prepared representation merge differs from its candidates.");
        return ReadPersistedMediaMergeOperation(connection, transaction, plan.OperationId, plan.RequestSha256)
            ?? throw new InvalidDataException("The prepared representation merge disappeared.");
    }

    internal static bool MediaMergeOperationMatchesV2(SqliteConnection connection, SqliteTransaction transaction,
        NativeMediaMergePlan plan, NativeMediaImportOperationState phase,
        PassiveMediaImportCandidate? full = null, PassiveMediaImportCandidate? safe = null)
    {
        NativeMediaMergeV2State state = RequireMediaMergeV2Write(connection, transaction, plan);
        if (!Enum.IsDefined(phase)) throw new ArgumentOutOfRangeException(nameof(phase));
        NativeMediaPersistedMergeOperation? row = ReadPersistedMediaMergeOperation(
            connection, transaction, plan.OperationId, plan.RequestSha256);
        if (row == null || !state.MatchesOperation(row, phase)) return false;
        if (phase == NativeMediaImportOperationState.Preparing) return full == null && safe == null;
        if (!MediaImportV2ImmediateChild(plan.OperationsParent, row.Full.Directory)
            || !state.SafeSelection.IsEmpty && !MediaImportV2ImmediateChild(plan.OperationsParent, row.Safe.Directory)) return false;
        StringComparison paths = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (row.Safe.Directory != null && string.Equals(row.Full.Directory, row.Safe.Directory, paths)
            && !state.ReuseFullAsSafe) return false;
        if (full == null) return safe == null;
        return MediaMergeCandidatesMatchV2(plan, state, full, safe)
            && row.Full.Directory == full.Directory && row.Full.ManifestGeneration == full.ManifestGeneration
            && row.Safe.Directory == safe?.Directory && row.Safe.ManifestGeneration == safe?.ManifestGeneration;
    }

    internal static void ApplyMediaMergeContentV2(SqliteConnection connection, SqliteTransaction transaction,
        NativeMediaMergePlan plan, PassiveMediaImportCandidate full, PassiveMediaImportCandidate? safe)
    {
        ArgumentNullException.ThrowIfNull(full);
        _ = RequireMediaMergeV2Write(connection, transaction, plan);
        RequireNoMediaMergeV2Receipt(connection, transaction, plan);
        if (!MediaMergeOperationMatchesV2(connection, transaction, plan, NativeMediaImportOperationState.Prepared, full, safe))
            throw new InvalidDataException("The representation merge operation changed.");
        ArchiveThreadSummary summary = ReadSummary(connection, transaction, plan.Resource)
            ?? throw new InvalidDataException("The media merge target disappeared.");
        bool changed = false;
        foreach (CapturedPost post in plan.AddedPosts)
        {
            CapturedPost normalized = post with
            {
                Attachments = post.Attachments == null ? null
                    : post.Attachments.Select(a => a with { PlannedPath = null }).ToImmutableArray()
            };
            InsertMediaImportPost(connection, transaction, plan.Resource, normalized);
            WriteQuotes(connection, transaction, summary, post);
            changed = true;
        }
        // These are the planner's prospective non-destructive enrichments, NOT raw
        // incoming posts. Existing attachments/private metadata are never replaced.
        foreach (CapturedPost post in plan.UpdatedPosts)
        {
            UpdateMediaMergePostText(connection, transaction, plan.Resource, post);
            WriteQuotes(connection, transaction, summary, post);
            changed = true;
        }
        foreach (NativeMediaMergeAddedAttachment item in plan.AddedAttachments)
        {
            InsertMediaMergeAttachment(connection, transaction, plan.Resource, item.PostId, item.Attachment);
            changed = true;
        }
        // Semantic Safe may contain zero original bindings. Full alone is never active.
        if (safe != null)
        {
            using SqliteCommand root = Command(connection, transaction, """
                INSERT INTO archive_media_directories(source,board,thread,directory)
                VALUES($source,$board,$thread,$directory)
                """);
            AddId(root, plan.Resource);
            root.Parameters.AddWithValue("$directory", safe.Directory);
            if (root.ExecuteNonQuery() != 1)
                throw new InvalidDataException("The safe representation root could not be registered.");
            changed = true;
        }
        if (changed)
        {
            using SqliteCommand bump = Command(connection, transaction, """
                UPDATE archive_threads SET revision=revision+1
                WHERE source=$source AND board=$board AND thread=$thread
                """);
            AddId(bump, plan.Resource);
            if (bump.ExecuteNonQuery() != 1)
                throw new InvalidDataException("The media merge target disappeared.");
        }
    }

    internal static void FinishMediaMergeV2(SqliteConnection connection, SqliteTransaction transaction,
        NativeMediaMergePlan plan, PassiveMediaImportCandidate full, PassiveMediaImportCandidate? safe)
    {
        ArgumentNullException.ThrowIfNull(full);
        _ = RequireMediaMergeV2Write(connection, transaction, plan);
        RequireNoMediaMergeV2Receipt(connection, transaction, plan);
        if (!MediaMergeOperationMatchesV2(connection, transaction, plan, NativeMediaImportOperationState.Prepared, full, safe))
            throw new InvalidDataException("The representation merge operation changed.");
        // Content, counts, receipt and terminal status are one caller-owned transaction.
        using (SqliteCommand receipt = Command(connection, transaction, """
            INSERT INTO archive_media_merge_receipts(operation_id,request_sha256,plan_version,
                new_posts,updated_posts,new_attachments,new_bindings,conflicts,
                evidence_additions,derived_descriptor_additions,contained_derived_additions)
            SELECT operation_id,request_sha256,plan_version,
                new_posts,updated_posts,new_attachments,new_bindings,conflicts,
                evidence_additions,derived_descriptor_additions,contained_derived_additions
            FROM archive_media_merge_operations
            WHERE operation_id=$op AND request_sha256=$request AND plan_version=2 AND state='Prepared'
            """))
        {
            AddMediaMergeV2Identity(receipt, plan);
            if (receipt.ExecuteNonQuery() != 1)
                throw new InvalidDataException("The representation merge receipt could not be recorded.");
        }
        using (SqliteCommand command = Command(connection, transaction, """
            UPDATE archive_media_merge_operations SET state='Committed'
            WHERE operation_id=$op AND request_sha256=$request AND plan_version=2 AND state='Prepared'
            """))
        {
            AddMediaMergeV2Identity(command, plan);
            if (command.ExecuteNonQuery() != 1)
                throw new InvalidDataException("The representation merge operation changed.");
        }
        if (!MediaMergeOperationMatchesV2(connection, transaction, plan, NativeMediaImportOperationState.Committed, full, safe))
            throw new InvalidDataException("The committed representation merge differs.");
        NativeMediaPersistedMergeOperation? committed = ReadPersistedMediaMergeOperation(
            connection, transaction, plan.OperationId, plan.RequestSha256);
        NativeMediaPersistedMergeReceipt? saved = ReadPersistedMediaMergeReceipt(
            connection, transaction, plan.OperationId, plan.RequestSha256, 2);
        if (committed == null || saved == null || !PersistedMediaMergeReceiptMatches(committed, saved))
            throw new InvalidDataException("The committed representation merge receipt differs.");
    }

    internal static NativeMediaMergeResult? ReadMediaMergeReceiptV2(SqliteConnection connection,
        SqliteTransaction? transaction, Guid operation, string request)
    {
        // No stage, operation, target or filesystem dependency on receipt-first replay.
        NativeMediaPersistedMergeReceipt? receipt = ReadPersistedMediaMergeReceipt(connection, transaction, operation, request, 2);
        if (receipt == null) return null;
        NativeMediaMergeCounts counts = receipt.Counts;
        return new(NativeMediaImportCommitStatus.Committed, counts.NewPosts, counts.UpdatedPosts,
            counts.NewAttachments, counts.NewBindings, counts.Conflicts, IsReplay: true,
            EvidenceAdditions: counts.EvidenceAdditions, DerivedDescriptorAdditions: counts.DerivedDescriptorAdditions,
            ContainedDerivedAdditions: counts.ContainedDerivedAdditions);
    }

    private static NativeMediaMergeV2State RequireMediaMergeV2Write(SqliteConnection connection,
        SqliteTransaction transaction, NativeMediaMergePlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.PlanVersion != 2 || plan.Representation is not { } state)
            throw new InvalidDataException("A representation merge requires a version-two plan.");
        ValidateMediaMergeIdentity(plan.OperationId, plan.RequestSha256);
        if (state.Identity != new NativeMediaPersistedIdentity(2, plan.OperationId, plan.RequestSha256,
                plan.Package.Envelope.Fingerprint, plan.Instance, plan.Generation, plan.Resource,
                plan.OperationsParent, plan.ImportedAtUtc)
            || state.Materialization.Resource != plan.Resource || state.FullSelection.IsEmpty
            || !state.CatalogBytes.AsSpan().SequenceEqual(plan.Package.Envelope.CatalogBytes.AsSpan())
            || state.CatalogBytes.Length > PersistedMediaCatalogBytes
            || state.RootsJson != plan.RootsJson || !state.Roots.SequenceEqual(plan.Roots)
            || state.Counts.NewPosts != plan.AddedPosts.Length || state.Counts.UpdatedPosts != plan.UpdatedPosts.Length
            || state.Counts.NewAttachments != plan.NewAttachmentCount || state.Counts.Conflicts != plan.Conflicts)
            throw new InvalidDataException("Invalid representation merge plan evidence.");
        MediaTransferRequireTransaction10(connection, transaction);
        if (GetPragma(connection, "user_version", transaction) < 11)
            throw new InvalidDataException("Representation merges require schema 11.");
        ValidatePostAttachmentPresenceSchema11(connection, transaction);
        ValidateMediaTransferSchema10(connection, transaction);
        return state;
    }

    private static bool MediaMergeCandidatesMatchV2(NativeMediaMergePlan plan, NativeMediaMergeV2State state,
        PassiveMediaImportCandidate full, PassiveMediaImportCandidate? safe)
    {
        ArgumentNullException.ThrowIfNull(full);
        bool Matches(PassiveMediaImportCandidate candidate, bool isSafe)
            => !candidate.Bindings.IsDefault && !candidate.OriginalEvidence.IsDefault
                && !candidate.DerivedRepresentations.IsDefault && IsMediaMergeHash(candidate.ManifestGeneration)
                && MediaImportV2ImmediateChild(plan.OperationsParent, candidate.Directory)
                && state.MatchesCandidate(candidate, isSafe);
        if (!Matches(full, false) || (safe == null) != state.SafeSelection.IsEmpty) return false;
        if (safe == null) return true;
        if (!Matches(safe, true)) return false;
        StringComparison paths = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return !string.Equals(full.Directory, safe.Directory, paths)
            || state.ReuseFullAsSafe && full.ManifestGeneration == safe.ManifestGeneration;
    }

    private static void RequireNoMediaMergeV2Receipt(SqliteConnection connection, SqliteTransaction transaction,
        NativeMediaMergePlan plan)
    {
        if (ReadPersistedMediaMergeReceipt(connection, transaction, plan.OperationId, plan.RequestSha256, 2) != null)
            throw new InvalidDataException("The representation merge already has a historical receipt.");
    }

    private static void AddMediaMergeV2Identity(SqliteCommand command, NativeMediaMergePlan plan)
    {
        command.Parameters.AddWithValue("$op", plan.OperationId.ToString("D"));
        command.Parameters.AddWithValue("$request", plan.RequestSha256);
    }
}
