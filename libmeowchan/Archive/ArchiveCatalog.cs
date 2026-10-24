using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using MeowChan.Interfaces;
using MeowChan.Persistence;

namespace MeowChan.Archive;

/// <summary>
/// Local metadata writer, independent of provider and UI lifetimes. The caller owns its ProfileLease.
/// SQLite work runs on the pool and is serialized by that lease; media files may live elsewhere.
/// </summary>
public sealed partial class ArchiveCatalog : IAsyncDisposable
{
    private readonly ProfileLease _lease;
    private bool _disposed;
    private ArchiveCatalog(string databasePath, ProfileLease lease) { DatabasePath = databasePath; _lease = lease; }
    public string DatabasePath { get; }

    public static Task<ArchiveCatalog> OpenAsync(string profileDirectory, ProfileLease lease,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lease);
        string directory = ProfileLease.NormalizeDirectory(profileDirectory);
        return Task.Run(() => lease.RunExclusive(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            lease.EnsureMatches(directory);
            string path = Path.Combine(directory, "archive.db");
            using SqliteConnection connection = ArchiveSql.OpenConnection(path, SqliteOpenMode.ReadWriteCreate);
            int version = ArchiveSql.GetPragma(connection, "user_version");
            int application = ArchiveSql.GetPragma(connection, "application_id");
            if (version == 0 && application == 0)
            {
                using SqliteCommand existing = connection.CreateCommand();
                existing.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' LIMIT 1";
                if (existing.ExecuteScalar() != null) throw new InvalidDataException("The archive path contains an unrelated database.");
                using SqliteTransaction transaction = connection.BeginTransaction();
                ArchiveSql.CreateContentSchema(connection, transaction);
                CreateDecisionSchema(connection, transaction);
                ArchiveSql.CreateSearchSchema(connection, transaction);
                ArchiveSql.CreateSourceContractSchema(connection, transaction);
                ArchiveSql.CreateIntegritySchema(connection, transaction);
                using SqliteCommand schema = ArchiveSql.Command(connection, transaction,
                    $"PRAGMA application_id={ArchiveSql.ApplicationId}; PRAGMA user_version={ArchiveSql.SchemaVersion};");
                schema.ExecuteNonQuery();
                cancellationToken.ThrowIfCancellationRequested();
                transaction.Commit();
            }
            if ((version is 1 or 2 or 3) && application == ArchiveSql.ApplicationId)
            {
                ArchiveSql.ValidateSchema(connection);
                using SqliteTransaction migration = connection.BeginTransaction();
                if (version == 1) ArchiveSql.MigrateSearchSchema(connection, migration, cancellationToken);
                if (version <= 2) ArchiveSql.CreateSourceContractSchema(connection, migration);
                ArchiveSql.CreateIntegritySchema(connection, migration);
                using SqliteCommand schema = ArchiveSql.Command(connection, migration, $"PRAGMA user_version={ArchiveSql.SchemaVersion};");
                schema.ExecuteNonQuery();
                cancellationToken.ThrowIfCancellationRequested();
                migration.Commit();
            }
            ArchiveSql.ValidateSchema(connection);
            return new ArchiveCatalog(path, lease);
        }), cancellationToken);
    }

    /// <summary>Registers media-only threads too. Re-registration preserves an existing capture preference.</summary>
    public Task<ArchiveThreadSummary> RegisterThreadAsync(ArchiveThreadRegistration registration,
        CancellationToken cancellationToken = default)
    {
        ArchiveValidation.ValidateRegistration(registration);
        return WriteAsync((connection, transaction) =>
        {
            Register(connection, transaction, registration, DateTimeOffset.UtcNow);
            return ArchiveSql.ReadSummary(connection, transaction, registration.Id)!;
        }, cancellationToken);
    }

    public Task<bool?> GetCaptureEnabledAsync(ResourceId id, CancellationToken cancellationToken = default)
    {
        ArchiveValidation.ValidateId(id);
        return ReadAsync<bool?>(connection => ArchiveSql.ReadSummary(connection, null, id)?.CaptureEnabled, cancellationToken);
    }

    public Task SetCaptureEnabledAsync(ResourceId id, bool enabled, CancellationToken cancellationToken = default)
    {
        ArchiveValidation.ValidateId(id);
        return WriteAsync((connection, transaction) =>
        {
            using SqliteCommand command = ArchiveSql.Command(connection, transaction,
                "UPDATE archive_threads SET capture_enabled=$enabled, revision=revision+1 WHERE source=$source AND board=$board AND thread=$thread");
            ArchiveSql.AddId(command, id);
            command.Parameters.AddWithValue("$enabled", enabled ? 1 : 0);
            if (command.ExecuteNonQuery() != 1) throw new KeyNotFoundException("The archive thread is not registered.");
            return true;
        }, cancellationToken);
    }

    /// <summary>Records observed source availability without claiming a new content capture or changing posts.</summary>
    public Task UpdateSourceStateAsync(ResourceId id, ThreadSourceState sourceState, CancellationToken cancellationToken = default)
    {
        ArchiveValidation.ValidateId(id);
        if (!Enum.IsDefined(sourceState)) throw new ArgumentOutOfRangeException(nameof(sourceState));
        return WriteAsync((connection, transaction) =>
        {
            using SqliteCommand command = ArchiveSql.Command(connection, transaction,
                "UPDATE archive_threads SET source_state=$state, revision=revision+1 WHERE source=$source AND board=$board AND thread=$thread");
            ArchiveSql.AddId(command, id);
            command.Parameters.AddWithValue("$state", (int)sourceState);
            if (command.ExecuteNonQuery() != 1) throw new KeyNotFoundException("The archive thread is not registered.");
            return true;
        }, cancellationToken);
    }

    /// <summary>
    /// Explicitly commits a successful content observation. Capture preference controls callers' admission,
    /// not this explicit operation. Posts and missing fields are retained; this never commits media bytes.
    /// </summary>
    public Task CaptureAsync(CapturedThreadSnapshot snapshot, CancellationToken cancellationToken = default)
        => CapturePreservingRevisionsAsync(StampObservedAttachments(snapshot), cancellationToken);

    // Runtime restores/replays are not new observations. Their explicit legacy null keys
    // and previously frozen revisions must survive persistence without reclassification.
    internal Task CapturePreservingRevisionsAsync(CapturedThreadSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        CapturedThreadSnapshot captured = ArchiveValidation.CopySnapshot(snapshot);
        return WriteAsync((connection, transaction) =>
        {
            ArchiveThreadSummary? before = ArchiveSql.ReadSummary(connection, transaction, captured.Id);
            bool urlChanged = before != null && before.Url != captured.Url;
            Register(connection, transaction, new ArchiveThreadRegistration(captured.Id, captured.Url,
                captured.Title, captured.MediaDirectory, true, captured.SourceState), captured.CapturedAt, observedContent: true);
            ArchiveSql.WritePostOrder(connection, transaction, captured.Id, captured.DecimalPostOrder);
            ArchivedThreadView current = ArchiveSql.ReadThread(connection, transaction, captured.Id)!;
            Dictionary<string, CapturedPost> prior = current.Posts.ToDictionary(post => post.PostId, StringComparer.Ordinal);
            foreach (CapturedPost next in captured.Posts)
            {
                cancellationToken.ThrowIfCancellationRequested();
                CapturedPost merged = prior.TryGetValue(next.PostId, out CapturedPost? previous)
                    ? ArchiveCaptureMerge.MergePost(previous, next) : next;
                WritePost(connection, transaction, captured.Id, merged);
                if (previous == null || previous.Html != merged.Html || urlChanged)
                    ArchiveSql.WriteQuotes(connection, transaction, current.Summary, merged);
            }
            if (urlChanged)
            {
                HashSet<string> observed = new HashSet<string>(captured.Posts.Select(post => post.PostId), StringComparer.Ordinal);
                foreach (CapturedPost retained in current.Posts.Where(post => !observed.Contains(post.PostId)))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    ArchiveSql.WriteQuotes(connection, transaction, current.Summary, retained);
                }
            }
            using SqliteCommand update = ArchiveSql.Command(connection, transaction,
                "UPDATE archive_threads SET last_captured_at=$captured, revision=revision+1 WHERE source=$source AND board=$board AND thread=$thread");
            ArchiveSql.AddId(update, captured.Id);
            update.Parameters.AddWithValue("$captured", ArchiveSql.Date(captured.CapturedAt));
            update.ExecuteNonQuery();
            return true;
        }, cancellationToken);
    }

    internal Task<T> WriteAsync<T>(Func<SqliteConnection, SqliteTransaction, T> operation,
        CancellationToken cancellationToken = default)
        => Task.Run(() => _lease.RunExclusive(() =>
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            using SqliteConnection connection = ArchiveSql.OpenConnection(DatabasePath, SqliteOpenMode.ReadWrite);
            ArchiveSql.ValidateSchema(connection);
            using SqliteTransaction transaction = connection.BeginTransaction();
            T result = operation(connection, transaction);
            cancellationToken.ThrowIfCancellationRequested();
            transaction.Commit();
            return result;
        }), cancellationToken);

    internal Task<T> ReadAsync<T>(Func<SqliteConnection, T> operation, CancellationToken cancellationToken = default)
        => Task.Run(() => _lease.RunExclusive(() =>
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            using SqliteConnection connection = ArchiveSql.OpenConnection(DatabasePath, SqliteOpenMode.ReadOnly);
            ArchiveSql.ValidateSchema(connection);
            T result = operation(connection);
            cancellationToken.ThrowIfCancellationRequested();
            return result;
        }), cancellationToken);

    public ValueTask DisposeAsync()
        => new ValueTask(Task.Run(() => _lease.RunExclusive(() => { _disposed = true; return true; })));

    private static void Register(SqliteConnection connection, SqliteTransaction transaction,
        ArchiveThreadRegistration registration, DateTimeOffset firstSeenAt, bool observedContent = false)
    {
        using SqliteCommand command = ArchiveSql.Command(connection, transaction, """
            INSERT INTO archive_threads(source,board,thread,url,title,media_directory,source_state,capture_enabled,first_seen_at,revision,search_text)
            VALUES($source,$board,$thread,$url,$title,$directory,$state,$capture,$first,0,$search)
            ON CONFLICT(source,board,thread) DO UPDATE SET url=excluded.url,
                title=CASE WHEN $observed=1 OR archive_threads.last_captured_at IS NULL THEN excluded.title ELSE archive_threads.title END,
                search_text=CASE WHEN $observed=1 OR archive_threads.last_captured_at IS NULL THEN excluded.search_text ELSE archive_threads.search_text END,
                media_directory=excluded.media_directory,
                source_state=CASE WHEN $observed=1 OR archive_threads.last_captured_at IS NULL THEN excluded.source_state ELSE archive_threads.source_state END
            """);
        ArchiveSql.AddId(command, registration.Id);
        command.Parameters.AddWithValue("$url", registration.Url);
        command.Parameters.AddWithValue("$title", registration.Title);
        command.Parameters.AddWithValue("$search", registration.Id.Source + "\n" + registration.Id.Board + "\n" + registration.Id.Thread + "\n" + ArchiveText.PlainText(registration.Title));
        command.Parameters.AddWithValue("$directory", registration.MediaDirectory);
        command.Parameters.AddWithValue("$state", (int)registration.SourceState);
        command.Parameters.AddWithValue("$capture", registration.CaptureEnabled ? 1 : 0);
        command.Parameters.AddWithValue("$first", ArchiveSql.Date(firstSeenAt));
        command.Parameters.AddWithValue("$observed", observedContent ? 1 : 0);
        command.ExecuteNonQuery();
        using SqliteCommand directory = ArchiveSql.Command(connection, transaction, """
            INSERT INTO archive_media_directories(source,board,thread,directory)
            VALUES($source,$board,$thread,$directory) ON CONFLICT DO NOTHING
            """);
        ArchiveSql.AddId(directory, registration.Id);
        directory.Parameters.AddWithValue("$directory", registration.MediaDirectory);
        directory.ExecuteNonQuery();
    }

    private static void WritePost(SqliteConnection connection, SqliteTransaction transaction, ResourceId id, CapturedPost post)
    {
        using SqliteCommand command = ArchiveSql.Command(connection, transaction, """
            INSERT INTO archive_posts(source,board,thread,post,ordinal,fields,posted_at,subject,author,html,is_deleted,search_text)
            VALUES($source,$board,$thread,$post,$ordinal,$fields,$posted,$subject,$author,$html,$deleted,$search)
            ON CONFLICT(source,board,thread,post) DO UPDATE SET ordinal=excluded.ordinal,fields=excluded.fields,
                posted_at=excluded.posted_at,subject=excluded.subject,author=excluded.author,html=excluded.html,is_deleted=excluded.is_deleted,search_text=excluded.search_text
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
        using SqliteCommand clear = ArchiveSql.Command(connection, transaction,
            "DELETE FROM archive_attachments WHERE source=$source AND board=$board AND thread=$thread AND post=$post");
        ArchiveSql.AddId(clear, id);
        clear.Parameters.AddWithValue("$post", post.PostId);
        clear.ExecuteNonQuery();
        foreach (CapturedAttachment attachment in post.Attachments ?? Array.Empty<CapturedAttachment>())
        {
            using SqliteCommand insert = ArchiveSql.Command(connection, transaction, """
                INSERT INTO archive_attachments(source,board,thread,post,position,media_id,variant,url,original_name,planned_path,expected_size,expected_md5,is_deleted,expected_sha256,attachment_revision_key)
                VALUES($source,$board,$thread,$post,$position,$media,$variant,$url,$name,$path,$size,$md5,$deleted,$sha,$attachmentRevision)
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
            insert.Parameters.AddWithValue("$attachmentRevision", (object?)attachment.AttachmentRevisionKey ?? DBNull.Value);
            insert.Parameters.AddWithValue("$deleted", attachment.IsDeleted ? 1 : 0);
            insert.ExecuteNonQuery();
        }
    }
}
