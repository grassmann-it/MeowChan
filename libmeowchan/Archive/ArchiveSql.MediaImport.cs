using System;
using System.Collections.Immutable;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using MeowChan.Transfer;
using MeowChan.Storage;
using MeowChan.Interfaces;

namespace MeowChan.Archive;

internal static partial class ArchiveSql
{
    // No thread FK and no content-fence triggers: receipts survive delete/recreate, and an
    // inactive preparation must not invalidate its own content fence.
    internal static void CreateMediaImportSchema(SqliteConnection connection, SqliteTransaction transaction)
    {
        using SqliteCommand command = Command(connection, transaction, """
            CREATE TABLE IF NOT EXISTS archive_media_import_operations (
                operation_id TEXT NOT NULL PRIMARY KEY CHECK(length(operation_id)=36),
                request_sha256 TEXT NOT NULL CHECK(length(request_sha256)=64),
                package_fingerprint TEXT NOT NULL CHECK(length(package_fingerprint)=64),
                catalog_instance TEXT NOT NULL, catalog_generation INTEGER NOT NULL CHECK(catalog_generation>=0),
                source TEXT NOT NULL, board TEXT NOT NULL, thread TEXT NOT NULL,
                operations_parent TEXT NOT NULL UNIQUE, imported_at TEXT NOT NULL,
                state TEXT NOT NULL CHECK(state IN ('Preparing','Prepared','Committed')),
                revision_keys_json TEXT NOT NULL CHECK(length(CAST(revision_keys_json AS BLOB))<=8388608),
                candidate_directory TEXT, manifest_generation TEXT, bindings_sha256 TEXT,
                post_count INTEGER NOT NULL CHECK(post_count>=0), attachment_count INTEGER NOT NULL CHECK(attachment_count>0),
                CHECK((state='Preparing' AND candidate_directory IS NULL AND manifest_generation IS NULL AND bindings_sha256 IS NULL)
                    OR (state IN ('Prepared','Committed') AND candidate_directory IS NOT NULL
                        AND length(manifest_generation)=64 AND length(bindings_sha256)=64)));
            CREATE TABLE IF NOT EXISTS archive_media_import_receipts (
                operation_id TEXT NOT NULL PRIMARY KEY CHECK(length(operation_id)=36),
                request_sha256 TEXT NOT NULL CHECK(length(request_sha256)=64),
                post_count INTEGER NOT NULL CHECK(post_count>=0),
                attachment_count INTEGER NOT NULL CHECK(attachment_count>0)
            );
            """);
        command.ExecuteNonQuery();
        // Validate both tables while this same migration/creation transaction is open. A
        // colliding malformed table therefore rolls back the new tables and schema version.
        ValidateMediaImportSchema(connection, transaction);
    }

    internal static void ValidateMediaImportSchema(SqliteConnection connection)
        => ValidateMediaImportSchema(connection, null);

    private static void ValidateMediaImportSchema(SqliteConnection connection, SqliteTransaction? transaction)
    {
        using SqliteCommand command = Command(connection, transaction, """
            SELECT operation_id,request_sha256,package_fingerprint,catalog_instance,catalog_generation,
                source,board,thread,operations_parent,imported_at,state,revision_keys_json,
                candidate_directory,manifest_generation,bindings_sha256,post_count,attachment_count
            FROM archive_media_import_operations LIMIT 0;
            SELECT operation_id,request_sha256,post_count,attachment_count
            FROM archive_media_import_receipts LIMIT 0;
            """);
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.NextResult()) { }
    }

    internal static bool MediaImportResourceAbsent(SqliteConnection connection, SqliteTransaction transaction, ResourceId id)
    {
        // Also reject orphaned content in a damaged DB rather than use an upsert as repair.
        foreach (string table in new[] { "archive_threads", "archive_posts", "archive_attachments",
            "archive_media_directories", "archive_source_contracts", "archive_quotes" })
        {
            using SqliteCommand command = Command(connection, transaction,
                $"SELECT 1 FROM {table} WHERE source=$source AND board=$board AND thread=$thread LIMIT 1");
            AddId(command, id);
            if (command.ExecuteScalar() != null) return false;
        }
        using SqliteCommand decisions = Command(connection, transaction,
            "SELECT 1 FROM media_decisions WHERE source_id=$source AND board_id=$board AND thread_id=$thread LIMIT 1");
        AddId(decisions, id);
        return decisions.ExecuteScalar() == null;
    }

    internal static string MediaImportBindingsHash(PassiveMediaImportCandidate candidate)
        => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(
            candidate.Bindings.OrderBy(binding => binding.Reference.RevisionKey, StringComparer.Ordinal)))).ToLowerInvariant();

    internal static bool ClaimMediaImport(SqliteConnection connection, SqliteTransaction transaction, NativeMediaImportPlan plan)
    {
        RequireLegacyMediaTransferRecords(connection, transaction);
        using SqliteCommand command = Command(connection, transaction, """
            INSERT INTO archive_media_import_operations(operation_id,request_sha256,package_fingerprint,
                catalog_instance,catalog_generation,source,board,thread,operations_parent,imported_at,state,
                revision_keys_json,post_count,attachment_count)
            SELECT $op,$request,$fingerprint,$instance,$generation,$source,$board,$thread,$parent,$at,'Preparing',$keys,$posts,$attachments
            WHERE NOT EXISTS (
                SELECT 1 FROM archive_media_import_operations
                WHERE source=$source AND board=$board AND thread=$thread AND state IN ('Preparing','Prepared'))
              AND NOT EXISTS (
                SELECT 1 FROM archive_media_merge_operations
                WHERE source=$source AND board=$board AND thread=$thread AND state IN ('Preparing','Prepared'))
            ON CONFLICT(operation_id) DO NOTHING
            """);
        AddMediaImportPlan(command, plan);
        // The predicate and insert share the same write transaction: a second plan for this
        // resource cannot acquire an active reservation. Historical Committed rows neither
        // reserve the resource nor stand in for this operation's exact receipt.
        return command.ExecuteNonQuery() == 1;
    }

    private static void AddMediaImportPlan(SqliteCommand command, NativeMediaImportPlan plan)
    {
        AddId(command, plan.Resource);
        command.Parameters.AddWithValue("$op", plan.OperationId.ToString("D"));
        command.Parameters.AddWithValue("$request", plan.RequestSha256);
        command.Parameters.AddWithValue("$fingerprint", plan.Package.Envelope.Fingerprint);
        command.Parameters.AddWithValue("$instance", plan.Instance);
        command.Parameters.AddWithValue("$generation", plan.Generation);
        command.Parameters.AddWithValue("$parent", plan.OperationsParent);
        command.Parameters.AddWithValue("$at", Date(plan.ImportedAtUtc));
        command.Parameters.AddWithValue("$keys", plan.RevisionKeysJson);
        command.Parameters.AddWithValue("$posts", plan.Thread.Posts.Length);
        command.Parameters.AddWithValue("$attachments", plan.Attachments.Length);
    }

    private const string MediaOperationColumns = """
        operation_id,request_sha256,package_fingerprint,catalog_instance,catalog_generation,
        source,board,thread,operations_parent,state,imported_at,candidate_directory,manifest_generation,post_count,attachment_count
        """;

    internal static NativeMediaImportOperation? ReadMediaImportOperation(SqliteConnection connection,
        SqliteTransaction? transaction, Guid operation, string request)
    {
        ValidateMediaImportIdentity(operation, request);
        if (transaction is null)
        {
            using SqliteTransaction snapshot = connection.BeginTransaction(deferred: true);
            return ReadMediaImportOperation(connection, snapshot, operation, request);
        }
        using SqliteCommand command = Command(connection, transaction,
            $"SELECT {MediaOperationColumns},{MediaPlanVersionColumn(connection, transaction)} FROM archive_media_import_operations WHERE operation_id=$op");
        command.Parameters.AddWithValue("$op", operation.ToString("D"));
        using SqliteDataReader reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        NativeMediaImportOperation found = ReadMediaImportOperationRow(reader);
        if (found.RequestSha256 != request) throw new InvalidDataException("The media import operation has different input.");
        return found;
    }

    private static NativeMediaImportOperation ReadMediaImportOperationRow(SqliteDataReader reader)
    {
        RequireLegacyMediaPlanRow(reader);
        return new(Guid.ParseExact(reader.GetString(0), "D"), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetInt64(4),
            new(reader.GetString(5), reader.GetString(6), reader.GetString(7)), reader.GetString(8),
            Enum.Parse<NativeMediaImportOperationState>(reader.GetString(9), ignoreCase: false), ParseDate(reader.GetString(10)),
            NullableString(reader, 11), NullableString(reader, 12), reader.GetInt32(13), reader.GetInt32(14));
    }

    internal static NativeMediaImportOperationPage ReadMediaImportOperationsPage(SqliteConnection connection, Guid? after)
    {
        // Pin the schema header and rows together; a concurrent 9→10 migration must
        // not make the literal legacy-version projection describe a new v2 row.
        using SqliteTransaction transaction = connection.BeginTransaction(deferred: true);
        using SqliteCommand command = Command(connection, transaction,
            $"SELECT {MediaOperationColumns},{MediaPlanVersionColumn(connection, transaction)} FROM archive_media_import_operations WHERE operation_id>$after ORDER BY operation_id LIMIT 33");
        command.Parameters.AddWithValue("$after", after?.ToString("D") ?? "");
        using SqliteDataReader reader = command.ExecuteReader();
        var items = ImmutableArray.CreateBuilder<NativeMediaImportOperation>();
        Guid? next = null;
        while (reader.Read())
        {
            if (items.Count == 32) { next = items[31].OperationId; break; }
            items.Add(ReadMediaImportOperationRow(reader));
        }
        return new(items.ToImmutable(), next);
    }

    internal static void ValidateMediaImportIdentity(Guid operation, string request)
    {
        if (operation == Guid.Empty || request is not { Length: 64 }
            || request.Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
            throw new ArgumentException("Invalid media import receipt identity.");
    }

    internal static NativeMediaImportCommitResult? ReadMediaImportReceipt(SqliteConnection connection,
        SqliteTransaction? transaction, Guid operation, string request)
    {
        ValidateMediaImportIdentity(operation, request);
        if (transaction is null)
        {
            using SqliteTransaction snapshot = connection.BeginTransaction(deferred: true);
            return ReadMediaImportReceipt(connection, snapshot, operation, request);
        }
        using SqliteCommand command = Command(connection, transaction, $"""
            SELECT request_sha256,post_count,attachment_count,{MediaPlanVersionColumn(connection, transaction)} FROM archive_media_import_receipts
            WHERE operation_id=$op
            """);
        command.Parameters.AddWithValue("$op", operation.ToString("D"));
        using SqliteDataReader reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        RequireLegacyMediaPlanRow(reader);
        if (reader.GetString(0) != request) throw new InvalidDataException("The media import receipt has different input.");
        return new(NativeMediaImportCommitStatus.Committed, 1, reader.GetInt32(1), reader.GetInt32(2), IsReplay: true);
    }

    internal static bool MediaImportOperationMatches(SqliteConnection connection, SqliteTransaction transaction,
        NativeMediaImportPlan plan, NativeMediaImportOperationState state, PassiveMediaImportCandidate? candidate = null, string? bindingsHash = null)
    {
        RequireLegacyMediaOperation(connection, transaction, merge: false, plan.OperationId);
        using SqliteCommand command = Command(connection, transaction, """
            SELECT 1 FROM archive_media_import_operations WHERE operation_id=$op AND request_sha256=$request
                AND package_fingerprint=$fingerprint AND catalog_instance=$instance AND catalog_generation=$generation
                AND source=$source AND board=$board AND thread=$thread AND operations_parent=$parent AND imported_at=$at
                AND revision_keys_json=$keys AND post_count=$posts AND attachment_count=$attachments AND state=$state
                AND (($candidate IS NULL AND candidate_directory IS NULL AND manifest_generation IS NULL AND bindings_sha256 IS NULL)
                    OR (candidate_directory=$candidate AND manifest_generation=$manifest AND bindings_sha256=$bindings))
            """);
        AddMediaImportPlan(command, plan);
        command.Parameters.AddWithValue("$state", state.ToString());
        command.Parameters.AddWithValue("$candidate", (object?)candidate?.Directory ?? DBNull.Value);
        command.Parameters.AddWithValue("$manifest", (object?)candidate?.ManifestGeneration ?? DBNull.Value);
        command.Parameters.AddWithValue("$bindings", (object?)bindingsHash ?? DBNull.Value);
        return command.ExecuteScalar() != null;
    }

    internal static void MarkMediaImportPrepared(SqliteConnection connection, SqliteTransaction transaction,
        NativeMediaImportPlan plan, PassiveMediaImportCandidate candidate, string bindingsHash)
    {
        if (!MediaImportOperationMatches(connection, transaction, plan, NativeMediaImportOperationState.Preparing))
            throw new InvalidDataException("The media import preparation changed.");
        using SqliteCommand command = Command(connection, transaction, """
            UPDATE archive_media_import_operations SET state='Prepared',candidate_directory=$candidate,
                manifest_generation=$manifest,bindings_sha256=$bindings WHERE operation_id=$op AND state='Preparing'
            """);
        command.Parameters.AddWithValue("$op", plan.OperationId.ToString("D"));
        command.Parameters.AddWithValue("$candidate", candidate.Directory);
        command.Parameters.AddWithValue("$manifest", candidate.ManifestGeneration);
        command.Parameters.AddWithValue("$bindings", bindingsHash);
        if (command.ExecuteNonQuery() != 1) throw new InvalidDataException("The media import preparation changed.");
    }

    internal static void FinishMediaImport(SqliteConnection connection, SqliteTransaction transaction, NativeMediaImportPlan plan)
    {
        RequireLegacyMediaOperation(connection, transaction, merge: false, plan.OperationId);
        using (SqliteCommand receipt = Command(connection, transaction, """
            INSERT INTO archive_media_import_receipts(operation_id,request_sha256,post_count,attachment_count)
            SELECT operation_id,request_sha256,post_count,attachment_count
            FROM archive_media_import_operations
            WHERE operation_id=$op AND request_sha256=$request AND state='Prepared'
            """))
        {
            receipt.Parameters.AddWithValue("$op", plan.OperationId.ToString("D"));
            receipt.Parameters.AddWithValue("$request", plan.RequestSha256);
            if (receipt.ExecuteNonQuery() != 1) throw new InvalidDataException("The media import operation changed.");
        }
        using SqliteCommand command = Command(connection, transaction,
            "UPDATE archive_media_import_operations SET state='Committed' WHERE operation_id=$op AND request_sha256=$request AND state='Prepared'");
        command.Parameters.AddWithValue("$op", plan.OperationId.ToString("D"));
        command.Parameters.AddWithValue("$request", plan.RequestSha256);
        if (command.ExecuteNonQuery() != 1) throw new InvalidDataException("The media import operation changed.");
    }

    internal static void DeleteCommittedMediaImportOperations(SqliteConnection connection,
        SqliteTransaction transaction, ResourceId id)
    {
        RequireLegacyMediaTransferRecords(connection, transaction);
        using SqliteCommand command = Command(connection, transaction, """
            DELETE FROM archive_media_import_operations
            WHERE source=$source AND board=$board AND thread=$thread AND state='Committed'
            """);
        AddId(command, id);
        command.ExecuteNonQuery();
    }

    internal static void InsertMediaImportContent(SqliteConnection connection, SqliteTransaction transaction,
        NativeMediaImportPlan plan, PassiveMediaImportCandidate candidate)
    {
        // Content mutation is an Apply boundary, not merely a helper after preparation.
        // Keep the v2 gate adjacent to the first INSERT so a future caller cannot
        // materialize a v2 plan before FinishMediaImport rejects its receipt.
        RequireLegacyMediaOperationForApply(connection, transaction, merge: false, plan.OperationId);
        TransferThread thread = plan.Thread;
        using (SqliteCommand command = Command(connection, transaction, """
            INSERT INTO archive_threads(source,board,thread,url,title,media_directory,source_state,capture_enabled,
                first_seen_at,last_captured_at,revision,search_text)
            VALUES($source,$board,$thread,$url,$title,$directory,$state,0,$first,NULL,0,$search)
            """))
        {
            AddId(command, thread.Id);
            command.Parameters.AddWithValue("$url", thread.PageUrl!);
            command.Parameters.AddWithValue("$title", thread.Title);
            command.Parameters.AddWithValue("$directory", candidate.Directory);
            command.Parameters.AddWithValue("$state", (int)(thread.SourceState == ThreadSourceState.NotFound ? ThreadSourceState.NotFound : ThreadSourceState.Archived));
            // Local archive admission time, not the package's historical first observation.
            command.Parameters.AddWithValue("$first", Date(plan.ImportedAtUtc));
            command.Parameters.AddWithValue("$search", thread.Id.Source + "\n" + thread.Id.Board + "\n" + thread.Id.Thread + "\n" + ArchiveText.PlainText(thread.Title));
            command.ExecuteNonQuery();
        }
        using (SqliteCommand directory = Command(connection, transaction, """
            INSERT INTO archive_media_directories(source,board,thread,directory) VALUES($source,$board,$thread,$directory)
            """))
        {
            AddId(directory, thread.Id); directory.Parameters.AddWithValue("$directory", candidate.Directory); directory.ExecuteNonQuery();
        }
        if (thread.DecimalPostOrder is { } order)
        {
            using SqliteCommand command = Command(connection, transaction, """
                INSERT INTO archive_source_contracts(source,board,thread,decimal_post_order) VALUES($source,$board,$thread,$order)
                """);
            AddId(command, thread.Id); command.Parameters.AddWithValue("$order", order ? 1 : 0); command.ExecuteNonQuery();
        }
        var keys = plan.Attachments.ToDictionary(a => (a.PostId, a.Incoming.Metadata.Position), a => a.RevisionKey);
        foreach (TransferPost incoming in thread.Posts)
        {
            CapturedPost post = incoming.Metadata with { Attachments = incoming.Attachments.Select(a => a.Metadata with
                { AttachmentRevisionKey = keys[(incoming.Metadata.PostId, a.Metadata.Position)], PlannedPath = null }).ToImmutableArray() };
            InsertMediaImportPost(connection, transaction, thread.Id, post);
            InsertMediaImportQuotes(connection, transaction, thread.Id, thread.PageUrl!, post);
        }
    }

    private static void InsertMediaImportPost(SqliteConnection connection, SqliteTransaction transaction, ResourceId id, CapturedPost post)
    {
        using SqliteCommand command = ArchiveSql.Command(connection, transaction, """
            INSERT INTO archive_posts(source,board,thread,post,ordinal,fields,posted_at,subject,author,html,is_deleted,search_text)
            VALUES($source,$board,$thread,$post,$ordinal,$fields,$posted,$subject,$author,$html,$deleted,$search)
            """);
        ArchiveSql.AddId(command, id);
        command.Parameters.AddWithValue("$post", post.PostId);
        command.Parameters.AddWithValue("$ordinal", post.Ordinal);
        command.Parameters.AddWithValue("$fields", (int)post.Fields);
        command.Parameters.AddWithValue("$posted", post.PostedAt.HasValue ? ArchiveSql.Date(post.PostedAt.Value) : DBNull.Value);
        command.Parameters.AddWithValue("$subject", (object?)post.Subject ?? DBNull.Value);
        command.Parameters.AddWithValue("$author", (object?)post.Author ?? DBNull.Value);
        command.Parameters.AddWithValue("$html", (object?)post.Html ?? DBNull.Value);
        command.Parameters.AddWithValue("$search", post.PostId + "\n" + ArchiveText.PlainText(post.Subject) + "\n" + ArchiveText.PlainText(post.Author) + "\n" + ArchiveText.PlainText(post.Html));
        command.Parameters.AddWithValue("$deleted", post.IsDeleted ? 1 : 0);
        command.ExecuteNonQuery();
        foreach (CapturedAttachment attachment in post.Attachments ?? Array.Empty<CapturedAttachment>())
        {
            using SqliteCommand insert = ArchiveSql.Command(connection, transaction, """
                INSERT INTO archive_attachments(source,board,thread,post,position,media_id,variant,url,original_name,planned_path,expected_size,expected_md5,is_deleted,expected_sha256,attachment_revision_key,expected_sha1,provider_size_hint_json)
                VALUES($source,$board,$thread,$post,$position,$media,$variant,$url,$name,$path,$size,$md5,$deleted,$sha,$attachmentRevision,$sha1,$sizeHint)
                """);
            ArchiveSql.AddId(insert, id);
            insert.Parameters.AddWithValue("$post", post.PostId);
            insert.Parameters.AddWithValue("$position", attachment.Position);
            insert.Parameters.AddWithValue("$media", (object?)attachment.MediaId ?? DBNull.Value);
            insert.Parameters.AddWithValue("$variant", attachment.Variant);
            insert.Parameters.AddWithValue("$url", (object?)attachment.Url ?? DBNull.Value);
            insert.Parameters.AddWithValue("$name", (object?)attachment.OriginalFileName ?? DBNull.Value);
            insert.Parameters.AddWithValue("$path", (object?)attachment.PlannedPath ?? DBNull.Value);
            insert.Parameters.AddWithValue("$size", (object?)attachment.ExpectedSize ?? DBNull.Value);
            insert.Parameters.AddWithValue("$md5", (object?)attachment.ExpectedMd5 ?? DBNull.Value);
            insert.Parameters.AddWithValue("$sha", (object?)attachment.ExpectedSha256 ?? DBNull.Value);
            insert.Parameters.AddWithValue("$sha1", (object?)attachment.ExpectedSha1 ?? DBNull.Value);
            insert.Parameters.AddWithValue("$sizeHint", (object?)ArchiveSql.WriteSizeHint(attachment.ProviderSizeHint) ?? DBNull.Value);
            insert.Parameters.AddWithValue("$attachmentRevision", (object?)attachment.AttachmentRevisionKey ?? DBNull.Value);
            insert.Parameters.AddWithValue("$deleted", attachment.IsDeleted ? 1 : 0);
            insert.ExecuteNonQuery();
        }
        WritePostAttachmentPresence(connection, transaction, id, post);
    }

    private static void InsertMediaImportQuotes(SqliteConnection connection, SqliteTransaction transaction, ResourceId id, string url, CapturedPost post)
    {
        IReadOnlyList<ArchiveQuote> quotes = ArchiveQuoteParser.Parse(id, url, post);
        for (int position = 0; position < quotes.Count; position++)
        {
            ArchiveQuote quote = quotes[position];
            using SqliteCommand insert = Command(connection, transaction, """
                INSERT INTO archive_quotes(source,board,thread,post,position,target_source,target_board,target_thread,target_post,label,url,is_external)
                VALUES($source,$board,$thread,$post,$position,$ts,$tb,$tt,$tp,$label,$url,$external)
                """);
            AddId(insert, id);
            insert.Parameters.AddWithValue("$post", post.PostId);
            insert.Parameters.AddWithValue("$position", position);
            insert.Parameters.AddWithValue("$ts", (object?)quote.Target?.Thread.Source ?? DBNull.Value);
            insert.Parameters.AddWithValue("$tb", (object?)quote.Target?.Thread.Board ?? DBNull.Value);
            insert.Parameters.AddWithValue("$tt", (object?)quote.Target?.Thread.Thread ?? DBNull.Value);
            insert.Parameters.AddWithValue("$tp", (object?)quote.Target?.PostId ?? DBNull.Value);
            insert.Parameters.AddWithValue("$label", quote.Label);
            insert.Parameters.AddWithValue("$url", (object?)quote.Url ?? DBNull.Value);
            insert.Parameters.AddWithValue("$external", quote.IsExternal ? 1 : 0);
            insert.ExecuteNonQuery();
        }
    }
}
