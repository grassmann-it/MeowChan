using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using MeowChan.Interfaces;
using MeowChan.Storage;
using MeowChan.Transfer;
using Microsoft.Data.Sqlite;

namespace MeowChan.Archive;

internal static partial class ArchiveSql
{
    // These SQL helpers consume an owner-issued plan, not an execution grant reconstructed
    // from persisted evidence. The existing coordinator owns Exclusive admission, the fresh
    // content fence and the candidate verification lease through the final SQL transaction.
    // Never begin/commit a writer transaction here or route v2 through legacy key helpers.
    internal static bool ClaimMediaImportV2(SqliteConnection connection, SqliteTransaction transaction,
        NativeMediaImportPlan plan)
    {
        NativeMediaImportV2State representation = RequireMediaImportV2Write(connection, transaction, plan);
        // History wins over a new reservation, even after operation cleanup or deletion of
        // the imported thread. A version/request collision is rejected by the typed reader.
        if (ReadPersistedMediaImportReceipt(connection, transaction, plan.OperationId, plan.RequestSha256, 2) != null)
            return false;
        // Check the whole profile in THIS claim transaction, before publishing a fresh
        // reservation. Valid committed v2 import history may coexist; unfinished or
        // incompatible v2 evidence must not be discovered only after content was committed.
        // Keep this out of later phases, where our own Preparing/Prepared row is expected.
        if (HasIncompatibleMediaTransferHistory(connection, transaction)) return false;
        using SqliteCommand command = Command(connection, transaction, """
            INSERT INTO archive_media_import_operations(
                operation_id,request_sha256,package_fingerprint,catalog_instance,catalog_generation,
                source,board,thread,operations_parent,imported_at,state,revision_keys_json,
                plan_version,full_selection_json,full_selection_sha256,full_member_count,catalog_bytes,
                post_count,attachment_count,evidence_additions,derived_descriptor_additions,
                contained_derived_additions,original_binding_count)
            SELECT $op,$request,$fingerprint,$instance,$generation,
                $source,$board,$thread,$parent,$at,'Preparing','[]',
                2,$selection,$selectionHash,$members,$catalog,
                $posts,$attachments,$evidence,$derived,$containedDerived,$originalBindings
            WHERE NOT EXISTS (
                SELECT 1 FROM archive_media_import_operations
                WHERE source=$source AND board=$board AND thread=$thread AND state IN ('Preparing','Prepared'))
              AND NOT EXISTS (
                SELECT 1 FROM archive_media_merge_operations
                WHERE source=$source AND board=$board AND thread=$thread AND state IN ('Preparing','Prepared'))
              AND NOT EXISTS (SELECT 1 FROM archive_media_import_receipts WHERE operation_id=$op)
            ON CONFLICT(operation_id) DO NOTHING
            """);
        AddMediaImportV2Plan(command, plan, representation);
        // The predicate and insertion share the caller's transaction. Only an actual fresh
        // INSERT winner can proceed to materialization; an existing row is never a retry grant.
        if (command.ExecuteNonQuery() != 1) return false;
        NativeMediaPersistedImportOperation? preparing = ReadPersistedMediaImportOperation(
            connection, transaction, plan.OperationId, plan.RequestSha256);
        if (preparing == null || !representation.MatchesOperation(preparing, NativeMediaImportOperationState.Preparing))
            throw new InvalidDataException("The representation import claim differs from the issued plan.");
        return true;
    }

    internal static NativeMediaPersistedImportOperation MarkMediaImportPreparedV2(SqliteConnection connection,
        SqliteTransaction transaction, NativeMediaImportPlan plan, PassiveMediaImportCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        NativeMediaImportV2State representation = RequireMediaImportV2Write(connection, transaction, plan);
        RequireNoMediaImportV2Receipt(connection, transaction, plan);
        if (!MediaImportCandidateMatchesV2(plan, representation, candidate)
            || !MediaImportOperationMatchesV2(connection, transaction, plan, NativeMediaImportOperationState.Preparing))
            throw new InvalidDataException("The representation import preparation changed.");
        using (SqliteCommand command = Command(connection, transaction, """
            UPDATE archive_media_import_operations
            SET state='Prepared',candidate_directory=$candidate,manifest_generation=$manifest,
                full_inventory_sha256=$inventory
            WHERE operation_id=$op AND request_sha256=$request AND plan_version=2 AND state='Preparing'
            """))
        {
            command.Parameters.AddWithValue("$op", plan.OperationId.ToString("D"));
            command.Parameters.AddWithValue("$request", plan.RequestSha256);
            command.Parameters.AddWithValue("$candidate", candidate.Directory);
            command.Parameters.AddWithValue("$manifest", candidate.ManifestGeneration);
            command.Parameters.AddWithValue("$inventory", representation.Inventory.Sha256);
            if (command.ExecuteNonQuery() != 1)
                throw new InvalidDataException("The representation import preparation changed.");
        }
        NativeMediaPersistedImportOperation? prepared = ReadPersistedMediaImportOperation(
            connection, transaction, plan.OperationId, plan.RequestSha256);
        if (prepared == null || !representation.MatchesOperation(prepared, NativeMediaImportOperationState.Prepared)
            || prepared.Full.Directory != candidate.Directory || prepared.Full.ManifestGeneration != candidate.ManifestGeneration)
            throw new InvalidDataException("The prepared representation import differs from its candidate.");
        return prepared;
    }

    internal static bool MediaImportOperationMatchesV2(SqliteConnection connection, SqliteTransaction transaction,
        NativeMediaImportPlan plan, NativeMediaImportOperationState state, PassiveMediaImportCandidate? candidate = null)
    {
        NativeMediaImportV2State representation = RequireMediaImportV2Write(connection, transaction, plan);
        if (!Enum.IsDefined(state)) throw new ArgumentOutOfRangeException(nameof(state));
        NativeMediaPersistedImportOperation? operation = ReadPersistedMediaImportOperation(
            connection, transaction, plan.OperationId, plan.RequestSha256);
        // The bounded reader checks schema/version, canonical selection, inventory provenance
        // and counts. MatchesOperation also binds every identity field and authentic catalog byte.
        if (operation == null || !representation.MatchesOperation(operation, state)) return false;
        if (state == NativeMediaImportOperationState.Preparing) return candidate == null;
        if (!MediaImportV2ImmediateChild(plan.OperationsParent, operation.Full.Directory)) return false;
        return candidate == null || MediaImportCandidateMatchesV2(plan, representation, candidate)
            && operation.Full.Directory == candidate.Directory
            && operation.Full.ManifestGeneration == candidate.ManifestGeneration;
    }

    internal static void InsertMediaImportContentV2(SqliteConnection connection, SqliteTransaction transaction,
        NativeMediaImportPlan plan, PassiveMediaImportCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        _ = RequireMediaImportV2Write(connection, transaction, plan);
        RequireNoMediaImportV2Receipt(connection, transaction, plan);
        // Re-read the complete prepared evidence in THIS write transaction before even the
        // thread INSERT. A candidate DTO alone is not filesystem verification or SQL admission.
        if (!MediaImportOperationMatchesV2(connection, transaction, plan, NativeMediaImportOperationState.Prepared, candidate)
            || !MediaImportResourceAbsent(connection, transaction, plan.Resource))
            throw new InvalidDataException("The representation import target or preparation changed.");
        TransferThread thread = plan.Thread;
        if (thread.PageUrl == null)
            throw new InvalidDataException("The representation import has no public thread URL.");
        using (SqliteCommand command = Command(connection, transaction, """
            INSERT INTO archive_threads(source,board,thread,url,title,media_directory,source_state,capture_enabled,
                first_seen_at,last_captured_at,revision,search_text)
            VALUES($source,$board,$thread,$url,$title,$directory,$state,0,$first,NULL,0,$search)
            """))
        {
            AddId(command, thread.Id);
            command.Parameters.AddWithValue("$url", thread.PageUrl);
            command.Parameters.AddWithValue("$title", thread.Title);
            command.Parameters.AddWithValue("$directory", candidate.Directory);
            command.Parameters.AddWithValue("$state", (int)(thread.SourceState == ThreadSourceState.NotFound
                ? ThreadSourceState.NotFound : ThreadSourceState.Archived));
            command.Parameters.AddWithValue("$first", Date(plan.ImportedAtUtc));
            command.Parameters.AddWithValue("$search", thread.Id.Source + "\n" + thread.Id.Board + "\n"
                + thread.Id.Thread + "\n" + ArchiveText.PlainText(thread.Title));
            command.ExecuteNonQuery();
        }
        // A nonempty semantic Full selection owns a root even when it contains no original
        // bindings (evidence-only, derived-only payloads, or exclusively retained observations).
        using (SqliteCommand directory = Command(connection, transaction, """
            INSERT INTO archive_media_directories(source,board,thread,directory)
            VALUES($source,$board,$thread,$directory)
            """))
        {
            AddId(directory, thread.Id);
            directory.Parameters.AddWithValue("$directory", candidate.Directory);
            directory.ExecuteNonQuery();
        }
        if (thread.DecimalPostOrder is { } order)
        {
            using SqliteCommand command = Command(connection, transaction, """
                INSERT INTO archive_source_contracts(source,board,thread,decimal_post_order)
                VALUES($source,$board,$thread,$order)
                """);
            AddId(command, thread.Id);
            command.Parameters.AddWithValue("$order", order ? 1 : 0);
            command.ExecuteNonQuery();
        }
        foreach (TransferPost incoming in thread.Posts)
        {
            // Preserve the source presence bits, scalar nulls and exact metadata revision
            // keys; never infer keys or local original availability from derived payloads.
            // Wire null remains null here and in the authentic persisted catalog document.
            CapturedPost post = incoming.Metadata with
            {
                Attachments = incoming.AttachmentsWereNull ? null
                    : incoming.Attachments.Select(a => a.Metadata).ToImmutableArray()
            };
            InsertMediaImportPost(connection, transaction, thread.Id, post);
            InsertMediaImportQuotes(connection, transaction, thread.Id, thread.PageUrl, post);
        }
        // RetainedBindings live only in the registered candidate inventory and catalog bytes.
        // They must not create synthetic posts or current attachment rows.
    }

    internal static void FinishMediaImportV2(SqliteConnection connection, SqliteTransaction transaction,
        NativeMediaImportPlan plan, PassiveMediaImportCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        NativeMediaImportV2State representation = RequireMediaImportV2Write(connection, transaction, plan);
        RequireNoMediaImportV2Receipt(connection, transaction, plan);
        if (!MediaImportOperationMatchesV2(connection, transaction, plan, NativeMediaImportOperationState.Prepared, candidate))
            throw new InvalidDataException("The representation import operation changed.");
        // Content, receipt and transition all belong to the SAME caller-owned transaction.
        // No conflict-ignore/upsert: any failed assertion must unwind it, not publish partial success.
        using (SqliteCommand receipt = Command(connection, transaction, """
            INSERT INTO archive_media_import_receipts(operation_id,request_sha256,plan_version,
                post_count,attachment_count,evidence_additions,derived_descriptor_additions,
                contained_derived_additions,original_binding_count)
            SELECT operation_id,request_sha256,plan_version,
                post_count,attachment_count,evidence_additions,derived_descriptor_additions,
                contained_derived_additions,original_binding_count
            FROM archive_media_import_operations
            WHERE operation_id=$op AND request_sha256=$request AND plan_version=2 AND state='Prepared'
            """))
        {
            receipt.Parameters.AddWithValue("$op", plan.OperationId.ToString("D"));
            receipt.Parameters.AddWithValue("$request", plan.RequestSha256);
            if (receipt.ExecuteNonQuery() != 1)
                throw new InvalidDataException("The representation import receipt could not be recorded.");
        }
        using (SqliteCommand command = Command(connection, transaction, """
            UPDATE archive_media_import_operations SET state='Committed'
            WHERE operation_id=$op AND request_sha256=$request AND plan_version=2 AND state='Prepared'
            """))
        {
            command.Parameters.AddWithValue("$op", plan.OperationId.ToString("D"));
            command.Parameters.AddWithValue("$request", plan.RequestSha256);
            if (command.ExecuteNonQuery() != 1)
                throw new InvalidDataException("The representation import operation changed.");
        }
        NativeMediaPersistedImportOperation? committed = ReadPersistedMediaImportOperation(
            connection, transaction, plan.OperationId, plan.RequestSha256);
        NativeMediaPersistedImportReceipt? saved = ReadPersistedMediaImportReceipt(
            connection, transaction, plan.OperationId, plan.RequestSha256, 2);
        if (committed == null || saved == null
            || !representation.MatchesOperation(committed, NativeMediaImportOperationState.Committed)
            || committed.Full.Directory != candidate.Directory || committed.Full.ManifestGeneration != candidate.ManifestGeneration
            || !PersistedMediaImportReceiptMatches(committed, saved))
            throw new InvalidDataException("The committed representation import evidence differs.");
    }

    internal static NativeMediaImportCommitResult? ReadMediaImportReceiptV2(SqliteConnection connection,
        SqliteTransaction? transaction, Guid operation, string request)
    {
        // Receipt-first reconciliation must work without a surviving operation, stage,
        // candidate directory, or fresh content fence. The typed reader owns a snapshot only
        // when the caller did not supply one; it rejects v1 and request/version collisions.
        NativeMediaPersistedImportReceipt? receipt = ReadPersistedMediaImportReceipt(
            connection, transaction, operation, request, 2);
        if (receipt == null) return null;
        NativeMediaImportCounts counts = receipt.Counts;
        return new(NativeMediaImportCommitStatus.Committed, 1, counts.PostCount, counts.AttachmentCount,
            IsReplay: true, EvidenceAdditions: counts.EvidenceAdditions,
            DerivedDescriptorAdditions: counts.DerivedDescriptorAdditions,
            ContainedDerivedAdditions: counts.ContainedDerivedAdditions,
            OriginalBindingCount: counts.OriginalBindingCount);
    }

    private static NativeMediaImportV2State RequireMediaImportV2Write(SqliteConnection connection,
        SqliteTransaction transaction, NativeMediaImportPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.PlanVersion != 2 || plan.Representation is not { } representation)
            throw new InvalidDataException("A representation import requires a version-two plan.");
        ValidateMediaImportIdentity(plan.OperationId, plan.RequestSha256);
        if (representation.Identity != new NativeMediaPersistedIdentity(2, plan.OperationId, plan.RequestSha256,
                plan.Package.Envelope.Fingerprint, plan.Instance, plan.Generation, plan.Resource,
                plan.OperationsParent, plan.ImportedAtUtc)
            || representation.Selection.IsEmpty || representation.Materialization.Resource != plan.Resource
            || !representation.CatalogBytes.AsSpan().SequenceEqual(plan.Package.Envelope.CatalogBytes.AsSpan())
            || representation.CatalogBytes.Length > PersistedMediaCatalogBytes)
            throw new InvalidDataException("Invalid representation import plan evidence.");
        // This validates storage, not owner authority. Only the coordinator can check its
        // exact issued-instance registry and live admission; no persisted DTO substitutes for it.
        MediaTransferRequireTransaction10(connection, transaction);
        // V2 writes are only valid against the current additive schema. Keep this
        // entry check in the same transaction as the media-table validation so a
        // caller cannot route a v2 write through a schema-10 snapshot.
        if (GetPragma(connection, "user_version", transaction) < 11)
            throw new InvalidDataException("Representation imports require schema 11.");
        ValidatePostAttachmentPresenceSchema11(connection, transaction);
        ValidateMediaTransferSchema10(connection, transaction);
        return representation;
    }

    private static void AddMediaImportV2Plan(SqliteCommand command, NativeMediaImportPlan plan,
        NativeMediaImportV2State representation)
    {
        AddId(command, plan.Resource);
        command.Parameters.AddWithValue("$op", plan.OperationId.ToString("D"));
        command.Parameters.AddWithValue("$request", plan.RequestSha256);
        command.Parameters.AddWithValue("$fingerprint", plan.Package.Envelope.Fingerprint);
        command.Parameters.AddWithValue("$instance", plan.Instance);
        command.Parameters.AddWithValue("$generation", plan.Generation);
        command.Parameters.AddWithValue("$parent", plan.OperationsParent);
        command.Parameters.AddWithValue("$at", Date(plan.ImportedAtUtc));
        command.Parameters.AddWithValue("$selection", representation.Selection.CanonicalJson);
        command.Parameters.AddWithValue("$selectionHash", representation.Selection.Sha256);
        command.Parameters.AddWithValue("$members", representation.Selection.Members.Length);
        command.Parameters.AddWithValue("$catalog", representation.CatalogBytes.ToArray());
        command.Parameters.AddWithValue("$posts", representation.Counts.PostCount);
        command.Parameters.AddWithValue("$attachments", representation.Counts.AttachmentCount);
        command.Parameters.AddWithValue("$evidence", representation.Counts.EvidenceAdditions);
        command.Parameters.AddWithValue("$derived", representation.Counts.DerivedDescriptorAdditions);
        command.Parameters.AddWithValue("$containedDerived", representation.Counts.ContainedDerivedAdditions);
        command.Parameters.AddWithValue("$originalBindings", representation.Counts.OriginalBindingCount);
    }

    private static void RequireNoMediaImportV2Receipt(SqliteConnection connection, SqliteTransaction transaction,
        NativeMediaImportPlan plan)
    {
        if (ReadPersistedMediaImportReceipt(connection, transaction, plan.OperationId, plan.RequestSha256, 2) != null)
            throw new InvalidDataException("The representation import already has a historical receipt.");
    }

    private static bool MediaImportCandidateMatchesV2(NativeMediaImportPlan plan,
        NativeMediaImportV2State representation, PassiveMediaImportCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        return !candidate.Bindings.IsDefault && !candidate.OriginalEvidence.IsDefault
            && !candidate.DerivedRepresentations.IsDefault && IsMediaMergeHash(candidate.ManifestGeneration)
            && MediaImportV2ImmediateChild(plan.OperationsParent, candidate.Directory)
            && representation.MatchesCandidate(candidate);
    }

    private static bool MediaImportV2ImmediateChild(string parent, string? directory)
    {
        // Lexical containment only, no filesystem reads. Link/byte/journal verification is
        // the storage lease's responsibility and must remain held through the SQL callback.
        if (string.IsNullOrWhiteSpace(parent) || string.IsNullOrWhiteSpace(directory)
            || !Path.IsPathFullyQualified(parent) || !Path.IsPathFullyQualified(directory)) return false;
        try
        {
            StringComparison paths = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            string canonicalParent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent));
            string canonicalDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
            return string.Equals(canonicalParent, Path.TrimEndingDirectorySeparator(parent), paths)
                && string.Equals(canonicalDirectory, Path.TrimEndingDirectorySeparator(directory), paths)
                && string.Equals(Path.GetDirectoryName(canonicalDirectory), canonicalParent, paths);
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or IOException)
        {
            return false;
        }
    }
}
