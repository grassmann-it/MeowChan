using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using MeowChan.Transfer;

namespace MeowChan.Archive;

public sealed partial class ArchiveCatalog
{
    // The constructible transport DTO is not a capability. Only object-identical plans actually
    // issued here can mutate this owner. Weak keys do not retain abandoned UI previews forever.
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<NativeTextImportPlan, object> _issuedNativeTextPlans = new();

    // A private technical vertical slice, deliberately not exposed to the Desktop/import API.
    // Full native import additionally requires passive media staging, FS recovery and conflict UI.
    internal async Task<NativeTextImportPreparation> PrepareNativeTextImportAsync(Stream source,
        CancellationToken cancellationToken = default)
    {
        TransferPackageInspection inspected = await NativeTransferPackageValidator.InspectAsync(source, cancellationToken).ConfigureAwait(false);
        if (!inspected.IsPackageValid) return new(null, NativeTextImportError.InvalidPackage);
        NativeTransferPackage package = inspected.Package!;
        // The public-only importer has no receiving consent/private apply contract.
        // Never silently discard a required private component or admit its fingerprint
        // to durable operation/candidate state while claiming the whole package imported.
        if (package.Envelope.PrivateMetadata != null)
            return new(null, NativeTextImportError.InvalidPackage);
        // This slice owns all catalog bytes; it never reads a possibly changed source again.
        NativeTransferCatalog incoming = package.Catalog;
        if (!package.Envelope.Payloads.IsEmpty || incoming.Threads.Any(thread => !thread.RetainedBindings.IsEmpty
            || thread.Posts.Any(post => !post.Attachments.IsEmpty || post.AttachmentsWereNull)))
            return new(null, NativeTextImportError.MediaNotSupported);
        return await ReadAsync(connection =>
        {
            using SqliteTransaction transaction = connection.BeginTransaction(deferred: true);
            (string instance, long generation) = ArchiveSql.ReadImportFence(connection, transaction);
            ImmutableArray<NativeTextImportDelta>.Builder deltas = ImmutableArray.CreateBuilder<NativeTextImportDelta>();
            long work = 0, textBytes = 0;
            foreach (TransferThread thread in incoming.Threads)
            {
                cancellationToken.ThrowIfCancellationRequested();
                // Bound the *combined selected target* before materializing any target posts.
                // The untrusted incoming catalog has its independent GP10 byte/count limits.
                foreach ((string table, string[] columns, long rowCap) in new (string, string[], long)[] {
                    ("archive_threads", ["source", "board", "thread", "url", "title", "media_directory", "first_seen_at", "last_captured_at"], 1),
                    ("archive_posts", ["post", "posted_at", "subject", "author", "html"], 100_000),
                    ("archive_attachments", ["post", "media_id", "variant", "url", "original_name", "planned_path", "expected_md5", "expected_sha256", "attachment_revision_key", "expected_sha1", "provider_size_hint_json"], 100_000),
                    ("archive_media_directories", ["directory"], 32) })
                {
                    using SqliteCommand budget = ArchiveSql.Command(connection, transaction,
                        "SELECT count(*),COALESCE(sum(" + string.Join("+", columns.Select(column => "COALESCE(length(CAST(" + column + " AS BLOB)),0)"))
                        + $"),0) FROM {table} WHERE source=$source AND board=$board AND thread=$thread");
                    ArchiveSql.AddId(budget, thread.Id);
                    using SqliteDataReader counts = budget.ExecuteReader(); counts.Read();
                    long rows = counts.GetInt64(0);
                    work = checked(work + rows); textBytes = checked(textBytes + counts.GetInt64(1));
                    if (rows > rowCap || work > 201_000 || textBytes > 32L * 1024 * 1024)
                        return new NativeTextImportPreparation(null, NativeTextImportError.TargetBudgetExceeded);
                }
                ArchivedThreadView? target = ArchiveSql.ReadThread(connection, transaction, thread.Id);
                if (target == null && thread.PageUrl == null)
                    return new NativeTextImportPreparation(null, NativeTextImportError.MissingPageUrl);
                Dictionary<string, CapturedPost> posts = target?.Posts.ToDictionary(p => p.PostId, StringComparer.Ordinal) ?? new(StringComparer.Ordinal);
                ImmutableArray<CapturedPost>.Builder added = ImmutableArray.CreateBuilder<CapturedPost>(), updated = ImmutableArray.CreateBuilder<CapturedPost>();
                bool conflicts = target != null && (thread.PageUrl != target.Summary.Url || thread.Title != target.Summary.Title
                    || thread.DecimalPostOrder != target.DecimalPostOrder);
                foreach (TransferPost post in thread.Posts)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!posts.TryGetValue(post.Metadata.PostId, out CapturedPost? previous)) added.Add(post.Metadata);
                    else
                    {
                        CapturedPost merged = MergeNativeTextPost(previous, post.Metadata, out bool changed, out bool conflict);
                        conflicts |= conflict;
                        if (changed) updated.Add(merged);
                    }
                }
                // Store each thread's complete incoming variant, not N copies of a 1000-thread
                // package or only a dangling external reference. This remains passive wire JSON.
                byte[] bytes = NativeTransferCatalogSerializer.Serialize(new(incoming.ExportedAtUtc, [thread]), cancellationToken);
                deltas.Add(new(thread, target == null, added.ToImmutable(), updated.ToImmutable(), conflicts,
                    ImmutableArray.CreateRange(bytes), Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()));
            }
            cancellationToken.ThrowIfCancellationRequested(); transaction.Commit();
            NativeTextImportPlan plan = new(this, instance, generation, incoming.ExportedAtUtc, deltas.ToImmutable(),
                Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(
                    package.Envelope.Fingerprint + "\0" + instance + "\0" + generation.ToString(System.Globalization.CultureInfo.InvariantCulture)))).ToLowerInvariant());
            _issuedNativeTextPlans.Add(plan, new object());
            return new NativeTextImportPreparation(plan, NativeTextImportError.None);
        }, cancellationToken).ConfigureAwait(false);
    }

    internal Task<NativeTextImportCommitResult> CommitNativeTextImportAsync(NativeTextImportPlan plan,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return WriteAsync((connection, transaction) =>
        {
            if (!ReferenceEquals(plan.Owner, this) || !_issuedNativeTextPlans.TryGetValue(plan, out _))
                return new NativeTextImportCommitResult(NativeTextImportCommitStatus.StalePlan);
            NativeTextImportCommitResult? replay = ReadNativeTextImportReceipt(connection, transaction, plan.OperationId, plan.RequestSha256);
            if (replay != null) return replay;
            (string instance, long generation) = ArchiveSql.ReadImportFence(connection, transaction);
            if (plan.Instance != instance || plan.Generation != generation)
                return new NativeTextImportCommitResult(NativeTextImportCommitStatus.StalePlan);
            // Exact replay was reconciled before freshness checks: it reports history without
            // mutating or resurrecting even a subsequently deleted/recreated target.
            int newThreads = 0, newPosts = 0, updatedPosts = 0, conflicts = 0;
            DateTimeOffset importedAt = DateTimeOffset.UtcNow;
            foreach (NativeTextImportDelta delta in plan.Deltas)
            {
                cancellationToken.ThrowIfCancellationRequested();
                TransferThread incoming = delta.Incoming;
                if (delta.IsNew)
                {
                    // No directory creation/registration to exporter-supplied paths. Root belongs
                    // to this profile and is just inert metadata until the future media seam.
                    string directory = Path.Combine(Path.GetDirectoryName(DatabasePath)!, "import-media",
                        Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(
                            incoming.Id.Source + "\0" + incoming.Id.Board + "\0" + incoming.Id.Thread))).ToLowerInvariant());
                    Register(connection, transaction, new(incoming.Id, incoming.PageUrl!, incoming.Title,
                        directory, false, incoming.SourceState == MeowChan.Interfaces.ThreadSourceState.NotFound
                            ? MeowChan.Interfaces.ThreadSourceState.NotFound : MeowChan.Interfaces.ThreadSourceState.Archived), importedAt);
                    ArchiveSql.WritePostOrder(connection, transaction, incoming.Id, incoming.DecimalPostOrder);
                    newThreads++;
                }
                ArchiveThreadSummary summary = ArchiveSql.ReadSummary(connection, transaction, incoming.Id)!;
                foreach (CapturedPost added in delta.Added)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    WritePost(connection, transaction, incoming.Id, added);
                    ArchiveSql.WriteQuotes(connection, transaction, summary, added); newPosts++;
                }
                foreach (CapturedPost updated in delta.Updated)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    // Do not delete/reinsert existing attachments just to update text; their rows,
                    // keys, bindings, decisions and original/preview provenance remain untouched.
                    WriteNativeTextPost(connection, transaction, incoming.Id, updated);
                    ArchiveSql.WriteQuotes(connection, transaction, summary, updated); updatedPosts++;
                }
                if (delta.Added.Length != 0 || delta.Updated.Length != 0)
                {
                    using SqliteCommand bump = ArchiveSql.Command(connection, transaction,
                        "UPDATE archive_threads SET revision=revision+1 WHERE source=$source AND board=$board AND thread=$thread");
                    ArchiveSql.AddId(bump, incoming.Id); bump.ExecuteNonQuery();
                }
                if (delta.HasConflicts) conflicts++;
                using SqliteCommand candidate = ArchiveSql.Command(connection, transaction, """
                    INSERT INTO archive_import_candidates(source,board,thread,sha256,imported_at,exported_at,has_conflicts,catalog_bytes)
                    VALUES($source,$board,$thread,$sha,$imported,$exported,$conflicts,$bytes)
                    ON CONFLICT(source,board,thread,sha256) DO UPDATE SET has_conflicts=1
                    WHERE excluded.has_conflicts=1 AND archive_import_candidates.has_conflicts=0
                    """);
                ArchiveSql.AddId(candidate, incoming.Id);
                candidate.Parameters.AddWithValue("$sha", delta.Sha256);
                candidate.Parameters.AddWithValue("$imported", ArchiveSql.Date(importedAt));
                candidate.Parameters.AddWithValue("$exported", ArchiveSql.Date(plan.ExportedAtUtc));
                candidate.Parameters.AddWithValue("$conflicts", delta.HasConflicts ? 1 : 0);
                candidate.Parameters.AddWithValue("$bytes", delta.CatalogBytes.ToArray()); candidate.ExecuteNonQuery();
            }
            using SqliteCommand receipt = ArchiveSql.Command(connection, transaction, """
                INSERT INTO archive_import_receipts(operation_id,request_sha256,new_threads,new_posts,updated_posts,conflicting_threads)
                VALUES($operation,$request,$threads,$posts,$updated,$conflicts)
                """);
            receipt.Parameters.AddWithValue("$operation", plan.OperationId.ToString("D"));
            receipt.Parameters.AddWithValue("$request", plan.RequestSha256);
            receipt.Parameters.AddWithValue("$threads", newThreads); receipt.Parameters.AddWithValue("$posts", newPosts);
            receipt.Parameters.AddWithValue("$updated", updatedPosts); receipt.Parameters.AddWithValue("$conflicts", conflicts);
            receipt.ExecuteNonQuery();
            return new NativeTextImportCommitResult(NativeTextImportCommitStatus.Committed, newThreads, newPosts, updatedPosts, conflicts);
        }, cancellationToken);
    }

    // A durable lookup after restart; the caller must retain BOTH values, not infer success from
    // package ID or filesystem state. Receipts contain only counts, no deleted thread identities.
    internal Task<NativeTextImportCommitResult?> ReadNativeTextImportReceiptAsync(Guid operationId, string requestSha256,
        CancellationToken cancellationToken = default)
        => ReadAsync(connection => ReadNativeTextImportReceipt(connection, null, operationId, requestSha256), cancellationToken);

    private static NativeTextImportCommitResult? ReadNativeTextImportReceipt(SqliteConnection connection,
        SqliteTransaction? transaction, Guid operationId, string requestSha256)
    {
        if (operationId == Guid.Empty || requestSha256 == null || requestSha256.Length != 64
            || requestSha256.Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
            throw new ArgumentException("Invalid import receipt identity.");
        using SqliteCommand command = ArchiveSql.Command(connection, transaction, """
            SELECT request_sha256,new_threads,new_posts,updated_posts,conflicting_threads FROM archive_import_receipts WHERE operation_id=$operation
            """);
        command.Parameters.AddWithValue("$operation", operationId.ToString("D"));
        using SqliteDataReader reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        if (reader.GetString(0) != requestSha256) throw new InvalidDataException("The import operation identity has different input.");
        return new(NativeTextImportCommitStatus.Committed, reader.GetInt32(1), reader.GetInt32(2), reader.GetInt32(3), reader.GetInt32(4), true);
    }

    // Convenience for small internal selections; never silently clips. Larger selections use
    // the explicit keyset page API, which can recover every retained candidate after restart.
    internal async Task<ImmutableArray<NativeTextImportCandidate>> ReadNativeTextImportCandidatesAsync(ResourceId id,
        CancellationToken cancellationToken = default)
    {
        NativeTextImportCandidatePage page = await ReadNativeTextImportCandidatesPageAsync(id, null, cancellationToken).ConfigureAwait(false);
        if (page.NextSha256 != null) throw new InvalidDataException("Use paginated import candidate retrieval for this selection.");
        return page.Items;
    }

    // Stable-key traversal, not a frozen database snapshot: simultaneous new variants before the
    // cursor require a refreshed scan. No deletion or arbitrary data is inferred from a page.
    internal Task<NativeTextImportCandidatePage> ReadNativeTextImportCandidatesPageAsync(ResourceId id,
        string? afterSha256 = null, CancellationToken cancellationToken = default)
    {
        ArchiveValidation.ValidateId(id);
        if (afterSha256 != null && (afterSha256.Length != 64
            || afterSha256.Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f'))))
            throw new ArgumentException("Invalid candidate cursor.");
        return ReadAsync(connection =>
        {
            using SqliteCommand command = ArchiveSql.Command(connection, null, """
                SELECT sha256,imported_at,exported_at,has_conflicts,catalog_bytes FROM archive_import_candidates
                WHERE source=$source AND board=$board AND thread=$thread AND sha256>$after ORDER BY sha256 LIMIT 33
                """);
            ArchiveSql.AddId(command, id); command.Parameters.AddWithValue("$after", afterSha256 ?? "");
            using SqliteDataReader reader = command.ExecuteReader();
            ImmutableArray<NativeTextImportCandidate>.Builder found = ImmutableArray.CreateBuilder<NativeTextImportCandidate>();
            long bytes = 0; string? next = null;
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                long length = reader.GetBytes(4, 0, null, 0, 0);
                if (length > 16L * 1024 * 1024) throw new InvalidDataException("An import candidate exceeds its storage limit.");
                if (found.Count == 32 || bytes + length > 32L * 1024 * 1024)
                { next = found[found.Count - 1].Sha256; break; }
                bytes += length;
                byte[] content = (byte[])reader[4];
                if (Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant() != reader.GetString(0))
                    throw new InvalidDataException("An import candidate failed its content check.");
                found.Add(new(reader.GetString(0), DateTimeOffset.Parse(reader.GetString(1), System.Globalization.CultureInfo.InvariantCulture),
                    DateTimeOffset.Parse(reader.GetString(2), System.Globalization.CultureInfo.InvariantCulture), reader.GetInt32(3) == 1,
                    ImmutableArray.CreateRange(content)));
            }
            return new NativeTextImportCandidatePage(found.ToImmutable(), next);
        }, cancellationToken);
    }

    private static CapturedPost MergeNativeTextPost(CapturedPost target, CapturedPost incoming,
        out bool changed, out bool conflict)
    {
        bool differs = target.Ordinal != incoming.Ordinal;
        PostFieldPresence fields = target.Fields;
        T? Merge<T>(PostFieldPresence field, T? old, T? next)
        {
            if ((incoming.Fields & field) == 0) return old;
            if ((target.Fields & field) != 0)
            { if (!EqualityComparer<T?>.Default.Equals(old, next)) differs = true; return old; }
            fields |= field; return next;
        }
        DateTimeOffset? posted = Merge(PostFieldPresence.PostedAt, target.PostedAt, incoming.PostedAt);
        string? subject = Merge(PostFieldPresence.Subject, target.Subject, incoming.Subject);
        string? author = Merge(PostFieldPresence.Author, target.Author, incoming.Author);
        string? html = Merge(PostFieldPresence.Html, target.Html, incoming.Html);
        bool deleted = Merge(PostFieldPresence.IsDeleted, target.IsDeleted, incoming.IsDeleted);
        if ((incoming.Fields & PostFieldPresence.Attachments) != 0)
        {
            if ((target.Fields & PostFieldPresence.Attachments) == 0) fields |= PostFieldPresence.Attachments;
            else if ((target.Attachments?.Count ?? 0) != 0) differs = true;
        }
        changed = fields != target.Fields;
        conflict = differs;
        // Only previously absent fields were filled, so changed follows presence, not dates or
        // partial equality that could accidentally replace an explicit null/empty target value.
        return target with { Fields = fields, PostedAt = posted, Subject = subject, Author = author, Html = html, IsDeleted = deleted };
    }

    private static void WriteNativeTextPost(SqliteConnection connection, SqliteTransaction transaction, ResourceId id, CapturedPost post)
    {
        using SqliteCommand command = ArchiveSql.Command(connection, transaction, """
            UPDATE archive_posts SET fields=$fields,posted_at=$posted,subject=$subject,author=$author,html=$html,
                is_deleted=$deleted,search_text=$search
            WHERE source=$source AND board=$board AND thread=$thread AND post=$post
            """);
        ArchiveSql.AddId(command, id);
        command.Parameters.AddWithValue("$post", post.PostId); command.Parameters.AddWithValue("$fields", (int)post.Fields);
        command.Parameters.AddWithValue("$posted", post.PostedAt.HasValue ? ArchiveSql.Date(post.PostedAt.Value) : DBNull.Value);
        command.Parameters.AddWithValue("$subject", (object?)post.Subject ?? DBNull.Value);
        command.Parameters.AddWithValue("$author", (object?)post.Author ?? DBNull.Value);
        command.Parameters.AddWithValue("$html", (object?)post.Html ?? DBNull.Value);
        command.Parameters.AddWithValue("$deleted", post.IsDeleted ? 1 : 0);
        command.Parameters.AddWithValue("$search", post.PostId + "\n" + ArchiveText.PlainText(post.Subject) + "\n" + ArchiveText.PlainText(post.Author) + "\n" + ArchiveText.PlainText(post.Html));
        if (command.ExecuteNonQuery() != 1) throw new InvalidDataException("The archive import target changed.");
    }
}
