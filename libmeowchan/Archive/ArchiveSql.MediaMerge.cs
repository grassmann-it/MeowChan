using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using MeowChan.Storage;
using MeowChan.Transfer;

namespace MeowChan.Archive;

internal static partial class ArchiveSql
{
    // This is deliberately a separate operation/receipt pair from the schema-7 new-thread
    // importer. In particular, a merge receipt must never be interpreted as a new-thread receipt.
    internal static void CreateMediaMergeSchema(SqliteConnection connection, SqliteTransaction transaction)
    {
        using SqliteCommand schema = Command(connection, transaction, """
            CREATE TABLE IF NOT EXISTS archive_media_merge_operations (
                operation_id TEXT NOT NULL PRIMARY KEY CHECK(length(operation_id)=36),
                request_sha256 TEXT NOT NULL CHECK(length(request_sha256)=64),
                package_fingerprint TEXT NOT NULL CHECK(length(package_fingerprint)=64),
                catalog_instance TEXT NOT NULL CHECK(length(catalog_instance)=36),
                catalog_generation INTEGER NOT NULL CHECK(catalog_generation>=0),
                source TEXT NOT NULL, board TEXT NOT NULL, thread TEXT NOT NULL,
                operations_parent TEXT NOT NULL UNIQUE, imported_at TEXT NOT NULL,
                state TEXT NOT NULL CHECK(state IN ('Preparing','Prepared','Committed')),
                roots_json TEXT NOT NULL CHECK(length(CAST(roots_json AS BLOB))<=8388608),
                full_keys_json TEXT NOT NULL CHECK(length(CAST(full_keys_json AS BLOB))<=8388608),
                safe_keys_json TEXT NOT NULL CHECK(length(CAST(safe_keys_json AS BLOB))<=8388608),
                catalog_bytes BLOB NOT NULL CHECK(length(catalog_bytes)<=16777216),
                full_directory TEXT, safe_directory TEXT,
                full_manifest_generation TEXT, safe_manifest_generation TEXT,
                full_bindings_sha256 TEXT, safe_bindings_sha256 TEXT,
                new_posts INTEGER NOT NULL CHECK(new_posts>=0),
                updated_posts INTEGER NOT NULL CHECK(updated_posts>=0),
                new_attachments INTEGER NOT NULL CHECK(new_attachments>=0),
                new_bindings INTEGER NOT NULL CHECK(new_bindings>=0),
                conflicts INTEGER NOT NULL CHECK(conflicts>=0),
                CHECK((state='Preparing' AND full_directory IS NULL AND safe_directory IS NULL
                    AND full_manifest_generation IS NULL AND safe_manifest_generation IS NULL
                    AND full_bindings_sha256 IS NULL AND safe_bindings_sha256 IS NULL)
                    OR (state IN ('Prepared','Committed') AND full_directory IS NOT NULL
                    AND full_manifest_generation IS NOT NULL AND length(full_manifest_generation)=64
                    AND full_bindings_sha256 IS NOT NULL AND length(full_bindings_sha256)=64
                    AND ((new_bindings=0 AND safe_directory IS NULL AND safe_manifest_generation IS NULL
                        AND safe_bindings_sha256 IS NULL)
                        OR (new_bindings>0 AND safe_directory IS NOT NULL
                        AND safe_manifest_generation IS NOT NULL AND length(safe_manifest_generation)=64
                        AND safe_bindings_sha256 IS NOT NULL AND length(safe_bindings_sha256)=64))))
            );
            CREATE TABLE IF NOT EXISTS archive_media_merge_receipts (
                operation_id TEXT NOT NULL PRIMARY KEY CHECK(length(operation_id)=36),
                request_sha256 TEXT NOT NULL CHECK(length(request_sha256)=64),
                new_posts INTEGER NOT NULL CHECK(new_posts>=0),
                updated_posts INTEGER NOT NULL CHECK(updated_posts>=0),
                new_attachments INTEGER NOT NULL CHECK(new_attachments>=0),
                new_bindings INTEGER NOT NULL CHECK(new_bindings>=0),
                conflicts INTEGER NOT NULL CHECK(conflicts>=0)
            );
            """);
        schema.ExecuteNonQuery();
        // Validate through the same transaction. A colliding malformed table therefore cannot
        // make the schema version advance while leaving an unusable merge journal behind.
        ValidateMediaMergeSchema(connection, transaction);
    }

    internal static void ValidateMediaMergeSchema(SqliteConnection connection)
        => ValidateMediaMergeSchema(connection, null);

    private static void ValidateMediaMergeSchema(SqliteConnection connection, SqliteTransaction? transaction)
    {
        using SqliteCommand columns = Command(connection, transaction, $"""
            SELECT {MediaMergeOperationColumns} FROM archive_media_merge_operations LIMIT 0;
            SELECT operation_id,request_sha256,new_posts,updated_posts,new_attachments,new_bindings,conflicts
            FROM archive_media_merge_receipts LIMIT 0;
            """);
        using SqliteDataReader reader = columns.ExecuteReader();
        while (reader.NextResult()) { }
    }

    internal static bool ClaimMediaMerge(SqliteConnection connection, SqliteTransaction transaction,
        NativeMediaMergePlan plan)
    {
        ValidateMediaMergePlanIdentity(plan);
        RequireLegacyMediaTransferRecords(connection, transaction);
        using SqliteCommand command = Command(connection, transaction, """
            INSERT INTO archive_media_merge_operations(
                operation_id,request_sha256,package_fingerprint,catalog_instance,catalog_generation,
                source,board,thread,operations_parent,imported_at,state,
                roots_json,full_keys_json,safe_keys_json,catalog_bytes,
                new_posts,updated_posts,new_attachments,new_bindings,conflicts)
            SELECT $op,$request,$fingerprint,$instance,$generation,
                $source,$board,$thread,$parent,$at,'Preparing',
                $roots,$fullKeys,$safeKeys,$catalog,
                $addedPosts,$updatedPosts,$attachments,$bindings,$conflicts
            WHERE NOT EXISTS (
                SELECT 1 FROM archive_media_merge_operations
                WHERE source=$source AND board=$board AND thread=$thread
                    AND state IN ('Preparing','Prepared'))
              AND NOT EXISTS (
                SELECT 1 FROM archive_media_import_operations
                WHERE source=$source AND board=$board AND thread=$thread
                    AND state IN ('Preparing','Prepared'))
              AND NOT EXISTS (SELECT 1 FROM archive_media_merge_receipts WHERE operation_id=$op)
            ON CONFLICT(operation_id) DO NOTHING
            """);
        AddMediaMergePlan(command, plan);
        // A committed historical row neither reserves the resource nor authorizes a new
        // operation. An unresolved row, including one from the other import family, does.
        return command.ExecuteNonQuery() == 1;
    }

    // Read-only resource-wide blocker before target-root I/O. ClaimMediaMerge keeps
    // the identical predicate in its write transaction as the final race fence.
    internal static bool HasUnresolvedMediaOperation(SqliteConnection connection, SqliteTransaction? transaction,
        ResourceId resource)
    {
        ArgumentNullException.ThrowIfNull(resource);
        if (HasIncompatibleMediaTransferHistory(connection, transaction)) return true;
        using SqliteCommand command = Command(connection, transaction, """
            SELECT EXISTS (
                SELECT 1 FROM archive_media_merge_operations
                WHERE source=$source AND board=$board AND thread=$thread AND state IN ('Preparing','Prepared')
            ) OR EXISTS (
                SELECT 1 FROM archive_media_import_operations
                WHERE source=$source AND board=$board AND thread=$thread AND state IN ('Preparing','Prepared')
            )
            """);
        AddId(command, resource);
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) != 0;
    }

    internal static NativeMediaMergeResult? ReadMediaMergeReceipt(SqliteConnection connection,
        SqliteTransaction? transaction, Guid operation, string request)
    {
        ValidateMediaMergeIdentity(operation, request);
        if (transaction is null)
        {
            using SqliteTransaction snapshot = connection.BeginTransaction(deferred: true);
            return ReadMediaMergeReceipt(connection, snapshot, operation, request);
        }
        using SqliteCommand command = Command(connection, transaction, $"""
            SELECT request_sha256,new_posts,updated_posts,new_attachments,new_bindings,conflicts,{MediaPlanVersionColumn(connection, transaction)}
            FROM archive_media_merge_receipts WHERE operation_id=$op
            """);
        command.Parameters.AddWithValue("$op", operation.ToString("D"));
        using SqliteDataReader reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        RequireLegacyMediaPlanRow(reader);
        if (reader.GetString(0) != request)
            throw new InvalidDataException("The media merge receipt has different input.");
        ValidateStoredHash(reader.GetString(0), "receipt request");
        int newPosts = ReadNonnegativeCount(reader, 1, "receipt new posts");
        int updatedPosts = ReadNonnegativeCount(reader, 2, "receipt updated posts");
        int newAttachments = ReadNonnegativeCount(reader, 3, "receipt new attachments");
        int newBindings = ReadNonnegativeCount(reader, 4, "receipt new bindings");
        int conflicts = ReadNonnegativeCount(reader, 5, "receipt conflicts");
        return new NativeMediaMergeResult(NativeMediaImportCommitStatus.Committed,
            newPosts, updatedPosts, newAttachments, newBindings, conflicts, IsReplay: true);
    }

    internal static NativeMediaMergeOperation? ReadMediaMergeOperation(SqliteConnection connection,
        SqliteTransaction? transaction, Guid operation, string request)
    {
        ValidateMediaMergeIdentity(operation, request);
        if (transaction is null)
        {
            using SqliteTransaction snapshot = connection.BeginTransaction(deferred: true);
            return ReadMediaMergeOperation(connection, snapshot, operation, request);
        }
        using SqliteCommand command = Command(connection, transaction,
            $"SELECT {MediaMergeOperationColumns},{MediaPlanVersionColumn(connection, transaction)} FROM archive_media_merge_operations WHERE operation_id=$op");
        command.Parameters.AddWithValue("$op", operation.ToString("D"));
        using SqliteDataReader reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        NativeMediaMergeOperation found = ReadMediaMergeOperationRow(reader);
        if (found.RequestSha256 != request)
            throw new InvalidDataException("The media merge operation has different input.");
        return found;
    }

    internal static NativeMediaMergeOperationPage ReadMediaMergeOperationsPage(SqliteConnection connection,
        Guid? after)
    {
        if (after == Guid.Empty) throw new ArgumentException("Invalid media merge cursor.", nameof(after));
        using SqliteTransaction transaction = connection.BeginTransaction(deferred: true);
        using SqliteCommand command = Command(connection, transaction,
            $"SELECT {MediaMergeOperationColumns},{MediaPlanVersionColumn(connection, transaction)} FROM archive_media_merge_operations "
            + "WHERE operation_id>$after ORDER BY operation_id LIMIT 33");
        command.Parameters.AddWithValue("$after", after?.ToString("D") ?? string.Empty);
        using SqliteDataReader reader = command.ExecuteReader();
        ImmutableArray<NativeMediaMergeOperation>.Builder found = ImmutableArray.CreateBuilder<NativeMediaMergeOperation>();
        long catalogBytes = 0;
        Guid? next = null;
        while (reader.Read())
        {
            if (found.Count == 32)
            {
                next = found[31].OperationId;
                break;
            }
            RequireLegacyMediaPlanRow(reader); // Reject v2 before materializing any catalog/selection.
            long length = reader.GetBytes(14, 0, null, 0, 0);
            if (length > 16L * 1024 * 1024)
                throw new InvalidDataException("A media merge catalog exceeds its storage limit.");
            if (catalogBytes + length > 32L * 1024 * 1024)
            {
                if (found.Count == 0)
                    throw new InvalidDataException("A media merge page exceeds its catalog byte limit.");
                next = found[found.Count - 1].OperationId;
                break;
            }
            catalogBytes = checked(catalogBytes + length);
            found.Add(ReadMediaMergeOperationRow(reader));
        }
        return new NativeMediaMergeOperationPage(found.ToImmutable(), next);
    }

    internal static void MarkMediaMergePrepared(SqliteConnection connection, SqliteTransaction transaction,
        NativeMediaMergePlan plan, PassiveMediaImportCandidate full, PassiveMediaImportCandidate? safe)
    {
        ValidateMediaMergePlanIdentity(plan);
        if (!MediaMergePreparingOperationMatches(connection, transaction, plan))
            throw new InvalidDataException("The media merge preparation changed.");
        ValidateMediaMergeCandidates(plan, full, safe);
        using SqliteCommand command = Command(connection, transaction, """
            UPDATE archive_media_merge_operations SET
                state='Prepared',full_directory=$fullDirectory,safe_directory=$safeDirectory,
                full_manifest_generation=$fullGeneration,safe_manifest_generation=$safeGeneration,
                full_bindings_sha256=$fullBindings,safe_bindings_sha256=$safeBindings
            WHERE operation_id=$op AND request_sha256=$request AND state='Preparing'
            """);
        command.Parameters.AddWithValue("$op", plan.OperationId.ToString("D"));
        command.Parameters.AddWithValue("$request", plan.RequestSha256);
        AddMediaMergeRoots(command, full, safe);
        if (command.ExecuteNonQuery() != 1)
            throw new InvalidDataException("The media merge preparation changed.");
    }

    internal static bool MediaMergeOperationMatches(SqliteConnection connection, SqliteTransaction transaction,
        NativeMediaMergePlan plan, PassiveMediaImportCandidate full, PassiveMediaImportCandidate? safe)
    {
        ValidateMediaMergePlanIdentity(plan);
        ValidateMediaMergeCandidates(plan, full, safe);
        RequireLegacyMediaOperation(connection, transaction, merge: true, plan.OperationId);
        using SqliteCommand command = Command(connection, transaction, """
            SELECT 1 FROM archive_media_merge_operations
            WHERE operation_id=$op AND request_sha256=$request
              AND package_fingerprint=$fingerprint AND catalog_instance=$instance AND catalog_generation=$generation
              AND source=$source AND board=$board AND thread=$thread
              AND operations_parent=$parent AND imported_at=$at
              AND state='Prepared'
              AND roots_json=$roots AND full_keys_json=$fullKeys AND safe_keys_json=$safeKeys
              AND catalog_bytes=$catalog
              AND new_posts=$addedPosts AND updated_posts=$updatedPosts
              AND new_attachments=$attachments AND new_bindings=$bindings AND conflicts=$conflicts
              AND full_directory IS $fullDirectory AND safe_directory IS $safeDirectory
              AND full_manifest_generation IS $fullGeneration
              AND safe_manifest_generation IS $safeGeneration
              AND full_bindings_sha256=$fullBindings AND safe_bindings_sha256 IS $safeBindings
            """);
        AddMediaMergePlan(command, plan);
        AddMediaMergeRoots(command, full, safe);
        return command.ExecuteScalar() != null;
    }

    internal static void ApplyMediaMergeContent(SqliteConnection connection, SqliteTransaction transaction,
        NativeMediaMergePlan plan, PassiveMediaImportCandidate full, PassiveMediaImportCandidate? safe)
    {
        if (!MediaMergeOperationMatches(connection, transaction, plan, full, safe))
            throw new InvalidDataException("The media merge operation changed.");
        ArchiveThreadSummary summary = ReadSummary(connection, transaction, plan.Resource)
            ?? throw new InvalidDataException("The media merge target disappeared.");
        bool changed = false;

        foreach (CapturedPost post in plan.AddedPosts)
        {
            // Passive merge rows never carry a filesystem path from the staged candidate.
            // The path is resolved later from the registered media root(s).
            CapturedPost normalized = post with
            {
                Attachments = post.Attachments == null ? null
                    : post.Attachments.Select(a => a with { PlannedPath = null }).ToImmutableArray()
            };
            InsertMediaImportPost(connection, transaction, plan.Resource, normalized);
            WriteQuotes(connection, transaction, summary, post);
            changed = true;
        }
        foreach (CapturedPost post in plan.UpdatedPosts)
        {
            // Update scalar fields and null-presence without replacing existing attachments.
            // Attachment rows are handled by the insert-only loop, which clears null-presence.
            UpdateMediaMergePostText(connection, transaction, plan.Resource, post);
            WriteQuotes(connection, transaction, summary, post);
            changed = true;
        }
        foreach (NativeMediaMergeAddedAttachment item in plan.AddedAttachments)
        {
            InsertMediaMergeAttachment(connection, transaction, plan.Resource, item.PostId, item.Attachment);
            changed = true;
        }
        if (safe != null)
        {
            using SqliteCommand root = Command(connection, transaction, """
                INSERT INTO archive_media_directories(source,board,thread,directory)
                VALUES($source,$board,$thread,$directory) ON CONFLICT DO NOTHING
                """);
            AddId(root, plan.Resource);
            root.Parameters.AddWithValue("$directory", safe.Directory);
            changed |= root.ExecuteNonQuery() == 1;
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

    internal static void FinishMediaMerge(SqliteConnection connection, SqliteTransaction transaction,
        NativeMediaMergePlan plan)
    {
        ValidateMediaMergePlanIdentity(plan);
        NativeMediaMergeOperation? stored = ReadMediaMergeOperation(connection, transaction, plan.OperationId, plan.RequestSha256);
        if (stored?.State != NativeMediaImportOperationState.Prepared)
            throw new InvalidDataException("The media merge operation changed.");
        using SqliteCommand receipt = Command(connection, transaction, """
            INSERT INTO archive_media_merge_receipts(
                operation_id,request_sha256,new_posts,updated_posts,new_attachments,new_bindings,conflicts)
            SELECT operation_id,request_sha256,new_posts,updated_posts,new_attachments,new_bindings,conflicts
            FROM archive_media_merge_operations
            WHERE operation_id=$op AND request_sha256=$request AND state='Prepared'
              AND package_fingerprint=$fingerprint AND catalog_instance=$instance AND catalog_generation=$generation
              AND source=$source AND board=$board AND thread=$thread
              AND operations_parent=$parent AND imported_at=$at
              AND roots_json=$roots AND full_keys_json=$fullKeys AND safe_keys_json=$safeKeys AND catalog_bytes=$catalog
              AND new_posts=$addedPosts AND updated_posts=$updatedPosts
              AND new_attachments=$attachments AND new_bindings=$bindings AND conflicts=$conflicts
            """);
        AddMediaMergePlan(receipt, plan);
        if (receipt.ExecuteNonQuery() != 1)
            throw new InvalidDataException("The media merge operation changed.");
        using SqliteCommand complete = Command(connection, transaction, """
            UPDATE archive_media_merge_operations SET state='Committed'
            WHERE operation_id=$op AND request_sha256=$request AND state='Prepared'
            """);
        complete.Parameters.AddWithValue("$op", plan.OperationId.ToString("D"));
        complete.Parameters.AddWithValue("$request", plan.RequestSha256);
        if (complete.ExecuteNonQuery() != 1)
            throw new InvalidDataException("The media merge operation changed.");
    }

    internal static void DeleteCommittedMediaMergeOperations(SqliteConnection connection,
        SqliteTransaction transaction, ResourceId id)
    {
        RequireLegacyMediaTransferRecords(connection, transaction);
        using SqliteCommand command = Command(connection, transaction, """
            DELETE FROM archive_media_merge_operations
            WHERE source=$source AND board=$board AND thread=$thread AND state='Committed'
            """);
        AddId(command, id);
        command.ExecuteNonQuery();
    }

    private const string MediaMergeOperationColumns = """
        operation_id,request_sha256,package_fingerprint,catalog_instance,catalog_generation,
        source,board,thread,operations_parent,state,imported_at,
        roots_json,full_keys_json,safe_keys_json,catalog_bytes,
        full_directory,safe_directory,full_manifest_generation,safe_manifest_generation,
        full_bindings_sha256,safe_bindings_sha256,
        new_posts,updated_posts,new_attachments,new_bindings,conflicts
        """;

    private static void AddMediaMergePlan(SqliteCommand command, NativeMediaMergePlan plan)
    {
        AddId(command, plan.Resource);
        command.Parameters.AddWithValue("$op", plan.OperationId.ToString("D"));
        command.Parameters.AddWithValue("$request", plan.RequestSha256);
        command.Parameters.AddWithValue("$fingerprint", plan.Package.Envelope.Fingerprint);
        command.Parameters.AddWithValue("$instance", plan.Instance);
        command.Parameters.AddWithValue("$generation", plan.Generation);
        command.Parameters.AddWithValue("$parent", plan.OperationsParent);
        command.Parameters.AddWithValue("$at", Date(plan.ImportedAtUtc));
        command.Parameters.AddWithValue("$roots", plan.RootsJson);
        command.Parameters.AddWithValue("$fullKeys", plan.FullKeysJson);
        command.Parameters.AddWithValue("$safeKeys", plan.SafeKeysJson);
        command.Parameters.AddWithValue("$catalog", plan.CatalogBytes.ToArray());
        command.Parameters.AddWithValue("$addedPosts", plan.AddedPosts.Length);
        command.Parameters.AddWithValue("$updatedPosts", plan.UpdatedPosts.Length);
        command.Parameters.AddWithValue("$attachments", plan.NewAttachmentCount);
        command.Parameters.AddWithValue("$bindings", plan.SafeKeys.Length);
        command.Parameters.AddWithValue("$conflicts", plan.Conflicts);
    }

    private static void AddMediaMergeRoots(SqliteCommand command, PassiveMediaImportCandidate full,
        PassiveMediaImportCandidate? safe)
    {
        command.Parameters.AddWithValue("$fullDirectory", full.Directory);
        command.Parameters.AddWithValue("$safeDirectory", (object?)safe?.Directory ?? DBNull.Value);
        command.Parameters.AddWithValue("$fullGeneration", full.ManifestGeneration);
        command.Parameters.AddWithValue("$safeGeneration", (object?)safe?.ManifestGeneration ?? DBNull.Value);
        command.Parameters.AddWithValue("$fullBindings", MediaImportBindingsHash(full));
        command.Parameters.AddWithValue("$safeBindings", safe == null ? (object)DBNull.Value : MediaImportBindingsHash(safe));
    }

    private static bool MediaMergePreparingOperationMatches(SqliteConnection connection,
        SqliteTransaction transaction, NativeMediaMergePlan plan)
    {
        RequireLegacyMediaOperation(connection, transaction, merge: true, plan.OperationId);
        using SqliteCommand command = Command(connection, transaction, """
            SELECT 1 FROM archive_media_merge_operations
            WHERE operation_id=$op AND request_sha256=$request
              AND package_fingerprint=$fingerprint AND catalog_instance=$instance AND catalog_generation=$generation
              AND source=$source AND board=$board AND thread=$thread
              AND operations_parent=$parent AND imported_at=$at
              AND state='Preparing'
              AND roots_json=$roots AND full_keys_json=$fullKeys AND safe_keys_json=$safeKeys
              AND catalog_bytes=$catalog
              AND new_posts=$addedPosts AND updated_posts=$updatedPosts
              AND new_attachments=$attachments AND new_bindings=$bindings AND conflicts=$conflicts
              AND full_directory IS NULL AND safe_directory IS NULL
              AND full_manifest_generation IS NULL AND safe_manifest_generation IS NULL
              AND full_bindings_sha256 IS NULL AND safe_bindings_sha256 IS NULL
            """);
        AddMediaMergePlan(command, plan);
        return command.ExecuteScalar() != null;
    }

    private static NativeMediaMergeOperation ReadMediaMergeOperationRow(SqliteDataReader reader)
    {
        RequireLegacyMediaPlanRow(reader);
        Guid operation;
        try { operation = Guid.ParseExact(reader.GetString(0), "D"); }
        catch (Exception error) when (error is FormatException or OverflowException)
        { throw new InvalidDataException("A media merge operation has an invalid identity.", error); }
        if (operation == Guid.Empty || reader.GetString(0) != operation.ToString("D"))
            throw new InvalidDataException("A media merge operation has an invalid identity.");
        string request = reader.GetString(1);
        ValidateMediaMergeIdentity(operation, request);
        string packageFingerprint = reader.GetString(2);
        if (!IsMediaMergeHash(packageFingerprint))
            throw new InvalidDataException("A media merge operation has an invalid package fingerprint.");
        string instance = reader.GetString(3);
        if (!Guid.TryParseExact(instance, "D", out Guid instanceId) || instanceId == Guid.Empty)
            throw new InvalidDataException("A media merge operation has an invalid catalog instance.");
        long generation = reader.GetInt64(4);
        if (generation < 0) throw new InvalidDataException("A media merge operation has an invalid catalog generation.");
        ResourceId resource = new(reader.GetString(5), reader.GetString(6), reader.GetString(7));
        ArchiveValidation.ValidateId(resource);
        string parent = reader.GetString(8);
        if (string.IsNullOrWhiteSpace(parent))
            throw new InvalidDataException("A media merge operation has an invalid operations parent.");
        NativeMediaImportOperationState state = ParseMediaMergeState(reader.GetString(9));
        _ = ParseDate(reader.GetString(10));
        _ = ReadMergeRoots(reader.GetString(11));
        ImmutableArray<string> fullKeys = ReadMergeKeys(reader.GetString(12), "full");
        ImmutableArray<string> safeKeys = ReadMergeKeys(reader.GetString(13), "safe");
        HashSet<string> fullSet = fullKeys.ToHashSet(StringComparer.Ordinal);
        if (fullKeys.IsEmpty || safeKeys.Any(key => !fullSet.Contains(key)))
            throw new InvalidDataException("A media merge operation has invalid key subset data.");
        long catalogLength = reader.GetBytes(14, 0, null, 0, 0);
        if (catalogLength <= 0 || catalogLength > 16L * 1024 * 1024)
            throw new InvalidDataException("A media merge catalog exceeds its storage limit.");
        byte[] catalog = (byte[])reader[14];
        string? fullDirectory = NullableString(reader, 15);
        string? safeDirectory = NullableString(reader, 16);
        string? fullGeneration = NullableString(reader, 17);
        string? safeGeneration = NullableString(reader, 18);
        string? fullBindings = NullableString(reader, 19);
        string? safeBindings = NullableString(reader, 20);
        ValidateStoredRoot(fullDirectory, fullGeneration);
        ValidateStoredRoot(safeDirectory, safeGeneration);
        ValidateStoredHash(fullBindings, "full binding");
        ValidateStoredHash(safeBindings, "safe binding");
        _ = ReadNonnegativeCount(reader, 21, "new posts");
        _ = ReadNonnegativeCount(reader, 22, "updated posts");
        _ = ReadNonnegativeCount(reader, 23, "new attachments");
        int newBindings = ReadNonnegativeCount(reader, 24, "new bindings");
        int conflicts = ReadNonnegativeCount(reader, 25, "conflicts");
        if (newBindings != safeKeys.Length)
            throw new InvalidDataException("A media merge operation has inconsistent binding counts.");
        bool hasSafe = safeKeys.Length != 0;
        if (state == NativeMediaImportOperationState.Preparing)
        {
            if (fullDirectory != null || safeDirectory != null || fullGeneration != null || safeGeneration != null
                || fullBindings != null || safeBindings != null)
                throw new InvalidDataException("A preparing media merge operation has prepared roots.");
        }
        else
        {
            if (fullDirectory == null || fullGeneration == null || fullBindings == null
                || hasSafe != (safeDirectory != null && safeGeneration != null && safeBindings != null)
                || !hasSafe && (safeDirectory != null || safeGeneration != null || safeBindings != null))
                throw new InvalidDataException("A prepared media merge operation has invalid roots.");
        }
        StringComparison paths = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (fullDirectory != null && safeDirectory != null && string.Equals(fullDirectory, safeDirectory, paths)
            && (conflicts != 0 || !fullKeys.SequenceEqual(safeKeys, StringComparer.Ordinal)
                || fullGeneration != safeGeneration || fullBindings != safeBindings))
            throw new InvalidDataException("A media merge operation reuses an unsafe full root.");
        return new(operation, request, resource, parent, state, fullDirectory, safeDirectory,
            fullGeneration, safeGeneration, ImmutableArray.CreateRange(catalog), fullKeys, safeKeys);
    }

    private static NativeMediaImportOperationState ParseMediaMergeState(string value)
        => value switch
        {
            "Preparing" => NativeMediaImportOperationState.Preparing,
            "Prepared" => NativeMediaImportOperationState.Prepared,
            "Committed" => NativeMediaImportOperationState.Committed,
            _ => throw new InvalidDataException("A media merge operation has an invalid state.")
        };

    private static void ValidateMediaMergePlanIdentity(NativeMediaMergePlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ValidateMediaMergeIdentity(plan.OperationId, plan.RequestSha256);
        ArchiveValidation.ValidateId(plan.Resource);
        if (!Guid.TryParseExact(plan.Instance, "D", out Guid instance) || instance == Guid.Empty || plan.Generation < 0)
            throw new ArgumentException("Invalid media merge catalog identity.", nameof(plan));
        if (plan.CatalogBytes.Length > 16 * 1024 * 1024)
            throw new InvalidDataException("The media merge catalog exceeds its storage limit.");
        if (plan.FullKeys.IsDefaultOrEmpty || plan.SafeKeys.IsDefault)
            throw new InvalidDataException("Invalid frozen media merge selections.");
        HashSet<string> fullSet = plan.FullKeys.ToHashSet(StringComparer.Ordinal);
        if (!IsMediaMergeHash(plan.Package.Envelope.Fingerprint) || string.IsNullOrWhiteSpace(plan.OperationsParent)
            || plan.ImportedAtUtc == default || plan.Conflicts < 0
            || plan.SafeKeys.Any(key => !fullSet.Contains(key))
            || plan.FullKeys.Zip(plan.FullKeys.Skip(1), (left, right) => StringComparer.Ordinal.Compare(left, right) >= 0).Any(bad => bad)
            || plan.SafeKeys.Zip(plan.SafeKeys.Skip(1), (left, right) => StringComparer.Ordinal.Compare(left, right) >= 0).Any(bad => bad)
            || plan.FullKeys.Any(key => !IsMediaMergeRevisionKey(key))
            || plan.SafeKeys.Any(key => !IsMediaMergeRevisionKey(key)))
            throw new InvalidDataException("Invalid frozen media merge identity data.");
        NativeMediaMergeRoot[] planRoots = ReadMergeRoots(plan.RootsJson);
        if (!planRoots.SequenceEqual(plan.Roots, EqualityComparer<NativeMediaMergeRoot>.Default)
            || !ReadMergeKeys(plan.FullKeysJson, "full").SequenceEqual(plan.FullKeys, StringComparer.Ordinal)
            || !ReadMergeKeys(plan.SafeKeysJson, "safe").SequenceEqual(plan.SafeKeys, StringComparer.Ordinal))
            throw new InvalidDataException("The media merge key documents differ from the frozen selections.");
    }

    internal static void ValidateMediaMergeIdentity(Guid operation, string request)
    {
        if (operation == Guid.Empty || !IsMediaMergeHash(request))
            throw new ArgumentException("Invalid media merge identity.");
    }

    private static void ValidateMediaMergeCandidates(NativeMediaMergePlan plan,
        PassiveMediaImportCandidate full, PassiveMediaImportCandidate? safe)
    {
        ValidateMediaMergeCandidate(plan, full, plan.FullKeys);
        if (safe == null)
        {
            if (!plan.SafeKeys.IsEmpty) throw new InvalidDataException("The safe media merge candidate is missing.");
            return;
        }
        if (plan.SafeKeys.IsEmpty) throw new InvalidDataException("An empty safe selection cannot register a media root.");
        ValidateMediaMergeCandidate(plan, safe, plan.SafeKeys);
        StringComparison paths = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (string.Equals(full.Directory, safe.Directory, paths)
            && (!plan.ReuseFullAsSafe || full.ManifestGeneration != safe.ManifestGeneration
                || MediaImportBindingsHash(full) != MediaImportBindingsHash(safe)))
            throw new InvalidDataException("The full media merge candidate is not a safe root.");
    }

    private static void ValidateMediaMergeCandidate(NativeMediaMergePlan plan,
        PassiveMediaImportCandidate candidate, ImmutableArray<string> keys)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ValidateStoredRoot(candidate.Directory, candidate.ManifestGeneration);
        if (string.IsNullOrWhiteSpace(candidate.Directory) || !IsMediaMergeHash(candidate.ManifestGeneration)
            || candidate.OperationId != plan.OperationId || candidate.Resource != plan.Resource
            || candidate.PackageFingerprint != plan.Package.Envelope.Fingerprint
            || candidate.ImportedAtUtc != plan.ImportedAtUtc || candidate.Bindings.IsDefault
            || candidate.Bindings.Length != keys.Length
            || !candidate.Bindings.Select(b => b.Reference.RevisionKey).OrderBy(k => k, StringComparer.Ordinal)
                .SequenceEqual(keys.OrderBy(k => k, StringComparer.Ordinal), StringComparer.Ordinal))
            throw new InvalidDataException("The media merge candidate differs from the issued plan.");
    }

    private static void ValidateStoredRoot(string? directory, string? generation)
    {
        if ((directory == null) != (generation == null))
            throw new InvalidDataException("A media merge candidate root is incomplete.");
        if (directory != null && (string.IsNullOrWhiteSpace(directory) || !IsMediaMergeHash(generation)))
            throw new InvalidDataException("A media merge candidate root is invalid.");
    }

    private static void ValidateStoredHash(string? value, string name)
    {
        if (value != null && !IsMediaMergeHash(value))
            throw new InvalidDataException("A media merge operation has an invalid " + name + " hash.");
    }

    private static bool IsMediaMergeHash(string? value)
        => value is { Length: 64 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool IsMediaMergeRevisionKey(string? value)
        => value is { Length: 71 }
            && (value.StartsWith("att-v1:", StringComparison.Ordinal)
                || value.StartsWith("att-v2:", StringComparison.Ordinal))
            && IsMediaMergeHash(value[7..]);

    private static int ReadNonnegativeCount(SqliteDataReader reader, int ordinal, string name)
    {
        if (reader.GetValue(ordinal) is not long value || value < 0 || value > int.MaxValue)
            throw new InvalidDataException("A media merge operation has an invalid " + name + " count.");
        return (int)value;
    }

    private static NativeMediaMergeRoot[] ReadMergeRoots(string json)
    {
        ValidateMergeJson(json, "roots");
        NativeMediaMergeRoot[] roots;
        try { roots = JsonSerializer.Deserialize<NativeMediaMergeRoot[]>(json) ?? throw new InvalidDataException(); }
        catch (JsonException error) { throw new InvalidDataException("A media merge roots document is invalid.", error); }
        StringComparer comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        if (roots.Any(root => root == null || string.IsNullOrWhiteSpace(root.Directory) || !IsMediaMergeHash(root.Generation))
            || roots.Select(root => root.Directory).Distinct(comparer).Count() != roots.Length)
            throw new InvalidDataException("A media merge roots document is invalid.");
        return roots;
    }

    private static void ValidateMergeJson(string json, string name)
    {
        if (string.IsNullOrWhiteSpace(json) || Encoding.UTF8.GetByteCount(json) > 8 * 1024 * 1024)
            throw new InvalidDataException("A media merge " + name + " document is invalid.");
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind is not (JsonValueKind.Array or JsonValueKind.Object))
                throw new InvalidDataException("A media merge " + name + " document is invalid.");
        }
        catch (JsonException error) { throw new InvalidDataException("A media merge " + name + " document is invalid.", error); }
    }

    private static ImmutableArray<string> ReadMergeKeys(string json, string name)
    {
        ValidateMergeJson(json, name + " keys");
        string[] values;
        try { values = JsonSerializer.Deserialize<string[]>(json) ?? throw new InvalidDataException(); }
        catch (JsonException error) { throw new InvalidDataException("A media merge " + name + " key document is invalid.", error); }
        if (values.Any(value => string.IsNullOrWhiteSpace(value))
            || values.Distinct(StringComparer.Ordinal).Count() != values.Length
            || values.Any(value => !IsMediaMergeRevisionKey(value))
            || values.Zip(values.Skip(1), (left, right) => StringComparer.Ordinal.Compare(left, right) >= 0).Any(outOfOrder => outOfOrder))
            throw new InvalidDataException("A media merge " + name + " key document is invalid.");
        return ImmutableArray.CreateRange(values);
    }

    private static void UpdateMediaMergePostText(SqliteConnection connection, SqliteTransaction transaction,
        ResourceId id, CapturedPost post)
    {
        using SqliteCommand command = Command(connection, transaction, """
            UPDATE archive_posts SET fields=$fields,posted_at=$posted,subject=$subject,author=$author,
                html=$html,is_deleted=$deleted,search_text=$search
            WHERE source=$source AND board=$board AND thread=$thread AND post=$post
            """);
        AddId(command, id);
        command.Parameters.AddWithValue("$post", post.PostId);
        command.Parameters.AddWithValue("$fields", (int)post.Fields);
        command.Parameters.AddWithValue("$posted", post.PostedAt.HasValue ? Date(post.PostedAt.Value) : DBNull.Value);
        command.Parameters.AddWithValue("$subject", (object?)post.Subject ?? DBNull.Value);
        command.Parameters.AddWithValue("$author", (object?)post.Author ?? DBNull.Value);
        command.Parameters.AddWithValue("$html", (object?)post.Html ?? DBNull.Value);
        command.Parameters.AddWithValue("$deleted", post.IsDeleted ? 1 : 0);
        command.Parameters.AddWithValue("$search", post.PostId + "\n" + ArchiveText.PlainText(post.Subject) + "\n"
            + ArchiveText.PlainText(post.Author) + "\n" + ArchiveText.PlainText(post.Html));
        if (command.ExecuteNonQuery() != 1)
            throw new InvalidDataException("The media merge post disappeared.");
        WritePostAttachmentPresence(connection, transaction, id, post);
    }

    private static void InsertMediaMergeAttachment(SqliteConnection connection, SqliteTransaction transaction,
        ResourceId id, string postId, CapturedAttachment attachment)
    {
        using SqliteCommand command = Command(connection, transaction, """
            INSERT INTO archive_attachments(source,board,thread,post,position,media_id,variant,url,original_name,planned_path,expected_size,expected_md5,width,height,is_deleted,expected_sha256,attachment_revision_key,expected_sha1,provider_size_hint_json)
            VALUES($source,$board,$thread,$post,$position,$media,$variant,$url,$name,NULL,$size,$md5,$width,$height,$deleted,$sha,$attachmentRevision,$sha1,$sizeHint)
            """);
        AddId(command, id);
        command.Parameters.AddWithValue("$post", postId);
        command.Parameters.AddWithValue("$position", attachment.Position);
        command.Parameters.AddWithValue("$media", (object?)attachment.MediaId ?? DBNull.Value);
        command.Parameters.AddWithValue("$variant", attachment.Variant);
        command.Parameters.AddWithValue("$url", (object?)attachment.Url ?? DBNull.Value);
        command.Parameters.AddWithValue("$name", (object?)attachment.OriginalFileName ?? DBNull.Value);
        command.Parameters.AddWithValue("$size", (object?)attachment.ExpectedSize ?? DBNull.Value);
        command.Parameters.AddWithValue("$md5", (object?)attachment.ExpectedMd5 ?? DBNull.Value);
        command.Parameters.AddWithValue("$width", (object?)attachment.Width ?? DBNull.Value);
        command.Parameters.AddWithValue("$height", (object?)attachment.Height ?? DBNull.Value);
        command.Parameters.AddWithValue("$deleted", attachment.IsDeleted ? 1 : 0);
        command.Parameters.AddWithValue("$sha", (object?)attachment.ExpectedSha256 ?? DBNull.Value);
        command.Parameters.AddWithValue("$attachmentRevision", (object?)attachment.AttachmentRevisionKey ?? DBNull.Value);
        command.Parameters.AddWithValue("$sha1", (object?)attachment.ExpectedSha1 ?? DBNull.Value);
        command.Parameters.AddWithValue("$sizeHint", (object?)WriteSizeHint(attachment.ProviderSizeHint) ?? DBNull.Value);
        command.ExecuteNonQuery();
        ClearPostAttachmentPresence(connection, transaction, id, postId);
    }
}
