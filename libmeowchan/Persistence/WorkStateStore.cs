namespace MeowChan.Persistence;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using MeowChan.Diagnostics;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using MeowChan.Storage;

/// <summary>Authoritative watch intent and retry state. All writes are serialized by the profile lease.</summary>
public interface IWorkStateStore
{
    IReadOnlyList<WorkResource> LoadResources();
    IReadOnlyList<MediaWork> LoadMedia(string url, long generation);
    WorkResource CommitIntent(WorkResource resource);
    WorkResource? CommitEnd(string url, long generation, ThreadSourceStateValue source, DateTimeOffset observedAt, string? finalSnapshotJson = null);
    WorkResource? CommitState(WorkResource resource);
    bool CommitMedia(MediaWork media);
}

/// <summary>Persisted source state is independent of a provider instance and the post catalog.</summary>
public enum ThreadSourceStateValue { Active, Archived, NotFound }
public enum WorkIntent { Watching, Suppressed, Completed, Repairing }
public enum MediaWorkStatus { Ready, Waiting, Succeeded, NeedsIntervention, Expired, Excluded }

public sealed record WorkResource
{
    public required string Url { get; init; }
    public required string OutputRoot { get; init; }
    public required long Generation { get; init; }
    public required WorkIntent Intent { get; init; }
    public required bool IsThread { get; init; }
    public bool Discovered { get; init; }
    public ThreadSourceStateValue Source { get; init; }
    public DateTimeOffset? EndObservedAt { get; init; }
    public bool CapturePending { get; init; }
    public string? FinalSnapshotJson { get; init; }
}

public sealed record MediaWork
{
    public required string ResourceUrl { get; init; }
    public required long Generation { get; init; }
    public required string MediaIdentifier { get; init; }
    /// <summary>The source attachment variant, independent of its raw media identifier.</summary>
    public string Variant { get; init; } = "original";
    public required string DestinationPath { get; init; }
    public required string DownloadUrl { get; init; }
    public string? ExpectedMd5 { get; init; }
    public string? ExpectedSha256 { get; init; }
    public string? ExpectedSha1 { get; init; }
    public MeowChan.Storage.MediaProviderSizeHint? ProviderSizeHint { get; init; }
    public string? AcquisitionRevisionKey { get; init; }
    private IReadOnlyList<AttachmentRevisionReference> _attachmentReferences = MediaAttachmentReferences.Empty;
    public IReadOnlyList<AttachmentRevisionReference> AttachmentReferences
    {
        get => _attachmentReferences;
        init => _attachmentReferences = MediaAttachmentReferences.CopyOf(value);
    }
    public string? CommittedAssetId { get; init; }
    public long? ExpectedSize { get; init; }
    public string? OriginalFileName { get; init; }
    public string? LegacyPath { get; init; }
    public MediaWorkStatus Status { get; init; }
    public int Attempts { get; init; }
    public int ConsecutiveNotFound { get; init; }
    public DateTimeOffset? LastAttemptAt { get; init; }
    public DateTimeOffset? DueAt { get; init; }
    public string? Failure { get; init; }
    public int? HttpStatus { get; init; }
    public bool ManualAttempt { get; init; }
}

/// <summary>SQLite work journal under the same exclusive lease as the session and archive.</summary>
public sealed class WorkStateStore : IWorkStateStore, IConditionalWorkStateStore
{
    private readonly ProfileLease _lease;
    internal object CoordinationIdentity => _lease;
    internal ProfileLease ProfileLease => _lease;
    // Instance-scoped deterministic test observer. Unset in production; invoked
    // only after a use has been accepted, and conveys no token or write authority.
    internal Action? AdmittedWriteObserver { get; set; }
    internal T RunAdmitted<T>(ProfileAdmissionToken admission, Func<T> operation) =>
        _lease.Admission.RunWithToken(admission, () =>
        {
            AdmittedWriteObserver?.Invoke();
            return operation();
        }, ProfileAdmissionKind.Startup, ProfileAdmissionKind.Ordinary);
    private long _resourcePointReads, _mediaPointReads, _resourceBatchReads, _mediaBatchReads, _materializedRows;
    internal WorkStoreQueryCounts QueryCounts => new(Interlocked.Read(ref _resourcePointReads), Interlocked.Read(ref _mediaPointReads),
        Interlocked.Read(ref _resourceBatchReads), Interlocked.Read(ref _mediaBatchReads), Interlocked.Read(ref _materializedRows));
    private readonly string _databasePath;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public WorkStateStore(string profileDirectory, ProfileLease lease) : this(lease)
    {
        using ProfileAdmissionToken admission = _lease.Admission.EnterStoreWriter();
        InitializeAdmitted(profileDirectory, admission);
    }

    // Borrow the caller's exact Startup/Ordinary reservation. Never cache or dispose it.
    internal WorkStateStore(string profileDirectory, ProfileLease lease, ProfileAdmissionToken admission) : this(lease)
    {
        InitializeAdmitted(profileDirectory, admission);
    }

    private WorkStateStore(ProfileLease lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        _lease = lease;
        _databasePath = Path.Combine(lease.ProfileDirectory, "work-state.sqlite");
    }

    private void InitializeAdmitted(string profileDirectory, ProfileAdmissionToken admission) =>
        _lease.RunStoreWrite(admission, () =>
        {
            // Validate the path and live lease inside the use-pin, before opening SQLite.
            // In particular no preliminary lease-monitor wait may precede the pin.
            _lease.EnsureMatches(profileDirectory);
            using SqliteConnection db = Open();
            Initialize(db);
            return true;
        });

    public IReadOnlyList<WorkResource> LoadResources() => _lease.RunExclusive(() => LoadResourcesCore(unreadable: null));

    // Restore and the presentation cache read every row they can and report the others by key.
    // Unreadable rows stay in place: LoadResources, point reads and archive cleanup keep
    // rejecting them, so a row of unknown state is never treated as absent or overwritten.
    internal WorkResourceLoad LoadReadableResources() => _lease.RunExclusive(() =>
    {
        List<UnreadableWorkRow> unreadable = new();
        IReadOnlyList<WorkResource> resources = LoadResourcesCore(unreadable);
        return new WorkResourceLoad(resources, unreadable);
    });

    private IReadOnlyList<WorkResource> LoadResourcesCore(List<UnreadableWorkRow>? unreadable)
    {
        Interlocked.Increment(ref _resourceBatchReads);
        using SqliteConnection db = OpenReadOnly();
        using SqliteCommand command = db.CreateCommand();
        command.CommandText = "SELECT url,payload FROM work_resources ORDER BY url";
        using SqliteDataReader reader = command.ExecuteReader();
        List<WorkResource> result = new();
        while (reader.Read())
        {
            string url = reader.GetString(0);
            WorkResource resource;
            try
            {
                resource = Read<WorkResource>(reader.GetString(1));
                if (resource.Url != url) throw new InvalidDataException("Work resource key differs from its payload.");
            }
            catch (Exception exception) when (unreadable != null && exception is InvalidDataException or JsonException)
            {
                unreadable.Add(new UnreadableWorkRow(url, exception.Message));
                continue;
            }
            Interlocked.Increment(ref _materializedRows);
            result.Add(resource);
        }
        PerformanceDiagnostics.Record("work.sql", "resource-batch", value: result.Count);
        if (unreadable is { Count: > 0 }) PerformanceDiagnostics.Record("work.sql", "resource-unreadable", value: unreadable.Count);
        return result;
    }

    public IReadOnlyList<MediaWork> LoadMedia(string url, long generation) => _lease.RunExclusive(() =>
    {
        Interlocked.Increment(ref _mediaBatchReads);
        using SqliteConnection db = OpenReadOnly();
        using SqliteCommand command = db.CreateCommand();
        command.CommandText = "SELECT media_id,variant,target,payload FROM work_media WHERE resource_url=$url AND generation=$generation ORDER BY media_id,variant,target";
        command.Parameters.AddWithValue("$url", url);
        command.Parameters.AddWithValue("$generation", generation);
        using SqliteDataReader reader = command.ExecuteReader();
        List<MediaWork> result = new();
        while (reader.Read())
        {
            MediaWork media = Read<MediaWork>(reader.GetString(3));
            if (media.ResourceUrl != url || media.Generation != generation || media.MediaIdentifier != reader.GetString(0)
                || media.Variant != reader.GetString(1) || media.DestinationPath != reader.GetString(2)) throw new InvalidDataException("Media work key differs from its payload.");
            Interlocked.Increment(ref _materializedRows);
            result.Add(media);
        }
        PerformanceDiagnostics.Record("work.sql", "media-batch", watch: url, epoch: generation, value: result.Count);
        return (IReadOnlyList<MediaWork>)result;
    });

    WorkResource? IConditionalWorkStateStore.FindResource(string url) => _lease.RunExclusive(() =>
    {
        using SqliteConnection db = OpenReadOnly();
        using SqliteTransaction transaction = db.BeginTransaction(deferred: true);
        return FindResource(db, transaction, url);
    });

    MediaWork? IConditionalWorkStateStore.FindMedia(MediaWorkKey key) => _lease.RunExclusive(() =>
    {
        using SqliteConnection db = OpenReadOnly();
        using SqliteTransaction transaction = db.BeginTransaction(deferred: true);
        return FindMedia(db, transaction, key);
    });

    bool IConditionalWorkStateStore.TryCommitMedia(MediaWork next, MediaWork? expected)
    {
        using ProfileAdmissionToken admission = _lease.Admission.EnterStoreWriter();
        return TryCommitMediaAdmitted(next, expected, admission);
    }

    internal bool TryCommitMediaAdmitted(MediaWork next, MediaWork? expected, ProfileAdmissionToken admission) =>
        _lease.RunStoreWrite(admission, () =>
        {
            bool result = TryCommitMediaCore(next, expected);
            if (result) WorkStateAccess.For(this).MarkDirty(next.ResourceUrl);
            return result;
        });

    // Internal continuation only: the caller must already hold an accepted
    // Startup/Ordinary use-pin. WorkStateAccess uses this after its outer
    // RunAdmitted has pinned the borrowed token; reacquiring the token here
    // would reject an already accepted operation after Dispose requests release.
    internal bool TryCommitMediaCore(MediaWork next, MediaWork? expected) => _lease.RunExclusive(() =>
    {
        Validate(next);
        using SqliteConnection db = Open();
        using SqliteTransaction transaction = db.BeginTransaction();
        WorkResource? owner = FindResource(db, transaction, next.ResourceUrl);
        if (owner == null || owner.Generation != next.Generation || owner.Intent == WorkIntent.Suppressed
            || !WorkStateComparison.Equal(FindMedia(db, transaction, MediaWorkKey.Of(next)), expected)) return false;
        WriteMedia(db, transaction, next);
        transaction.Commit();
        return true;
    });

    public WorkResource CommitIntent(WorkResource resource)
    {
        using ProfileAdmissionToken admission = _lease.Admission.EnterStoreWriter();
        return CommitIntentAdmitted(resource, admission);
    }

    internal WorkResource CommitIntentAdmitted(WorkResource resource, ProfileAdmissionToken admission) =>
        RunAdmitted(admission, () =>
        {
            using IDisposable fence = WorkStateAccess.For(this).Enter(resource.Url);
            WorkResource result = CommitIntentCore(resource);
            WorkStateAccess.For(this).MarkDirty(resource.Url);
            return result;
        });
    WorkResource IConditionalWorkStateStore.CommitIntentUnderFence(WorkResource resource)
    {
        using ProfileAdmissionToken admission = _lease.Admission.EnterStoreWriter();
        return CommitIntentUnderFence(resource, admission);
    }
    internal WorkResource CommitIntentUnderFence(WorkResource resource, ProfileAdmissionToken admission) =>
        _lease.RunStoreWrite(admission, () => CommitIntentCore(resource));

    public WorkResource? CommitState(WorkResource resource)
    {
        using ProfileAdmissionToken admission = _lease.Admission.EnterStoreWriter();
        return CommitStateAdmitted(resource, admission);
    }

    internal WorkResource? CommitStateAdmitted(WorkResource resource, ProfileAdmissionToken admission) =>
        RunAdmitted(admission, () =>
        {
            using IDisposable fence = WorkStateAccess.For(this).Enter(resource.Url);
            WorkResource? result = CommitStateCore(resource);
            if (result != null) WorkStateAccess.For(this).MarkDirty(resource.Url);
            return result;
        });
    WorkResource? IConditionalWorkStateStore.CommitStateUnderFence(WorkResource resource)
    {
        using ProfileAdmissionToken admission = _lease.Admission.EnterStoreWriter();
        return CommitStateUnderFence(resource, admission);
    }
    internal WorkResource? CommitStateUnderFence(WorkResource resource, ProfileAdmissionToken admission) =>
        _lease.RunStoreWrite(admission, () => CommitStateCore(resource));

    public WorkResource? CommitEnd(string url, long generation, ThreadSourceStateValue source,
        DateTimeOffset observedAt, string? finalSnapshotJson = null)
    {
        using ProfileAdmissionToken admission = _lease.Admission.EnterStoreWriter();
        return CommitEndAdmitted(url, generation, source, observedAt, finalSnapshotJson, admission);
    }

    internal WorkResource? CommitEndAdmitted(string url, long generation, ThreadSourceStateValue source,
        DateTimeOffset observedAt, string? finalSnapshotJson, ProfileAdmissionToken admission) =>
        RunAdmitted(admission, () =>
        {
            using IDisposable fence = WorkStateAccess.For(this).Enter(url);
            WorkResource? result = CommitEndCore(url, generation, source, observedAt, finalSnapshotJson);
            if (result != null) WorkStateAccess.For(this).MarkDirty(url);
            return result;
        });

    WorkResource? IConditionalWorkStateStore.CommitEndUnderFence(string url, long generation,
        ThreadSourceStateValue source, DateTimeOffset observedAt, string? finalSnapshotJson)
    {
        using ProfileAdmissionToken admission = _lease.Admission.EnterStoreWriter();
        return CommitEndUnderFence(url, generation, source, observedAt, finalSnapshotJson, admission);
    }
    internal WorkResource? CommitEndUnderFence(string url, long generation, ThreadSourceStateValue source,
        DateTimeOffset observedAt, string? finalSnapshotJson, ProfileAdmissionToken admission) =>
        _lease.RunStoreWrite(admission, () => CommitEndCore(url, generation, source, observedAt, finalSnapshotJson));

    public bool CommitMedia(MediaWork media)
    {
        using ProfileAdmissionToken admission = _lease.Admission.EnterStoreWriter();
        return CommitMediaAdmitted(media, admission);
    }

    internal bool CommitMediaAdmitted(MediaWork media, ProfileAdmissionToken admission) =>
        RunAdmitted(admission, () =>
        {
            using IDisposable fence = WorkStateAccess.For(this).Enter(media.ResourceUrl);
            bool result = CommitMediaCore(media);
            if (result) WorkStateAccess.For(this).MarkDirty(media.ResourceUrl);
            return result;
        });

    // Internal continuation only; caller must already hold an accepted
    // Startup/Ordinary use-pin and the resource fence.
    internal WorkResource CommitIntentCore(WorkResource resource) => _lease.RunExclusive(() =>
    {
        Validate(resource);
        using SqliteConnection db = Open();
        using SqliteTransaction transaction = db.BeginTransaction();
        WorkResource? previous = FindResource(db, transaction, resource.Url);
        if (previous != null && resource.Generation <= previous.Generation)
            throw new InvalidOperationException("Watch generation must advance when intent changes.");
        WriteResource(db, transaction, resource);
        transaction.Commit();
        return resource;
    });

    // Internal continuation only: caller must already hold a Startup/Ordinary use-pin
    // and the resource fence. WorkStateAccess keeps that pin across its point read
    // and this transaction; reacquiring the borrowed token here would reject an
    // already accepted operation when Dispose requests release during the read.
    internal WorkResource? CommitEndCore(string url, long generation, ThreadSourceStateValue source, DateTimeOffset observedAt, string? finalSnapshotJson = null)
        => _lease.RunExclusive(() =>
    {
        if (!Enum.IsDefined(source) || source == ThreadSourceStateValue.Active) throw new ArgumentOutOfRangeException(nameof(source));
        using SqliteConnection db = Open();
        using SqliteTransaction transaction = db.BeginTransaction();
        WorkResource? previous = FindResource(db, transaction, url);
        if (previous == null || previous.Generation != generation || previous.Intent != WorkIntent.Watching) return null;
        WorkResource next = previous with
        {
            Source = source,
            EndObservedAt = previous.EndObservedAt ?? observedAt,
            CapturePending = previous.Source == ThreadSourceStateValue.Active || previous.CapturePending,
            FinalSnapshotJson = finalSnapshotJson ?? previous.FinalSnapshotJson
        };
        WriteResource(db, transaction, next);
        transaction.Commit();
        return next;
    });

    // Internal continuation only; caller must already hold an accepted
    // Startup/Ordinary use-pin and the resource fence.
    internal WorkResource? CommitStateCore(WorkResource resource) => _lease.RunExclusive(() =>
    {
        Validate(resource);
        using SqliteConnection db = Open();
        using SqliteTransaction transaction = db.BeginTransaction();
        WorkResource? previous = FindResource(db, transaction, resource.Url);
        if (previous == null || previous.Generation != resource.Generation) return null;
        if (previous.Intent == WorkIntent.Suppressed && resource.Intent != WorkIntent.Suppressed) return null;
        if (previous.EndObservedAt != null && resource.EndObservedAt != previous.EndObservedAt)
            throw new InvalidOperationException("The first observed source end is immutable within a generation.");
        WriteResource(db, transaction, resource);
        transaction.Commit();
        return resource;
    });

    private bool CommitMediaCore(MediaWork media) => _lease.RunExclusive(() =>
    {
        Validate(media);
        using SqliteConnection db = Open();
        using SqliteTransaction transaction = db.BeginTransaction();
        WorkResource? owner = FindResource(db, transaction, media.ResourceUrl);
        if (owner == null || owner.Generation != media.Generation || owner.Intent == WorkIntent.Suppressed) return false;
        WriteMedia(db, transaction, media);
        transaction.Commit();
        return true;
    });

    private MediaWork? FindMedia(SqliteConnection db, SqliteTransaction transaction, MediaWorkKey key)
    {
        Interlocked.Increment(ref _mediaPointReads);
        using SqliteCommand command = db.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT payload FROM work_media WHERE resource_url=$url AND generation=$generation AND media_id=$id AND variant=$variant AND target=$target";
        command.Parameters.AddWithValue("$url", key.Url);
        command.Parameters.AddWithValue("$generation", key.Generation);
        command.Parameters.AddWithValue("$id", key.MediaId);
        command.Parameters.AddWithValue("$variant", key.Variant);
        command.Parameters.AddWithValue("$target", key.Target);
        object? payload = command.ExecuteScalar();
        if (payload == null) return null;
        Interlocked.Increment(ref _materializedRows);
        MediaWork media = Read<MediaWork>((string)payload);
        if (MediaWorkKey.Of(media) != key) throw new InvalidDataException("Media work key differs from its payload.");
        return media;
    }

    private static void WriteMedia(SqliteConnection db, SqliteTransaction transaction, MediaWork media)
    {
        using SqliteCommand command = db.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO work_media(resource_url,generation,media_id,variant,target,payload) VALUES($url,$generation,$id,$variant,$target,$payload) ON CONFLICT(resource_url,generation,media_id,variant,target) DO UPDATE SET payload=excluded.payload";
        command.Parameters.AddWithValue("$url", media.ResourceUrl);
        command.Parameters.AddWithValue("$generation", media.Generation);
        command.Parameters.AddWithValue("$id", media.MediaIdentifier);
        command.Parameters.AddWithValue("$variant", media.Variant);
        command.Parameters.AddWithValue("$target", media.DestinationPath);
        command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(media, Json));
        command.ExecuteNonQuery();
    }

    // Retention (user decision 11.11.2026): only media rows of a generation lower than their resource's
    // current generation are removed. Every LoadMedia/FindMedia names the owner's current generation
    // (restore, work cache, queue binding, retries and archive cleanup), and generations only advance,
    // so no reader can observe them again. The owner's generation is read exactly as restore reads it;
    // resource rows always stay (they fix the generation number), and media of a missing or unreadable
    // resource row is of unknown state and stays. Media payloads are not parsed: a corrupt media row is
    // kept or removed by its key columns like any other. A verified copy is written before the first
    // DELETE; without it nothing is deleted. One backup is kept next to the journal: a later effective
    // run replaces it only after its own copy is complete and verified. No schema change, no new index:
    // the primary key prefix (resource_url,generation) serves both statements.
    internal const string PruneBackupFileName = "work-state.before-prune.sqlite";

    // Instance-scoped deterministic test observer, unset in production: invoked inside the open
    // deletion transaction after each owner's DELETE, with the number of owners processed so far.
    internal Action<int>? PruneDeleteObserver { get; set; }

    // Startup only: Registry calls this under its gate before any watch is bound, so no generation
    // changes meanwhile; the lease monitor excludes every other store operation for the whole run.
    internal WorkJournalPrune PruneSupersededMedia(ProfileAdmissionToken? admission)
    {
        if (admission != null) return _lease.RunStoreWrite(admission, PruneSupersededMediaCore);
        using ProfileAdmissionToken local = _lease.Admission.EnterStoreWriter();
        return _lease.RunStoreWrite(local, PruneSupersededMediaCore);
    }

    private WorkJournalPrune PruneSupersededMediaCore()
    {
        string backup = Path.Combine(_lease.ProfileDirectory, PruneBackupFileName);
        string partial = backup + ".tmp";
        try
        {
            using SqliteConnection db = Open();
            // IMMEDIATE: the reserved lock admits readers (the copy below) but no other writer,
            // so the copy and the deletion see the same rows. Disposal without commit rolls back.
            using SqliteTransaction transaction = db.BeginTransaction();
            List<(string Url, long Generation)> owners = new();
            using (SqliteCommand command = db.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = "SELECT url,payload FROM work_resources ORDER BY url";
                using SqliteDataReader reader = command.ExecuteReader();
                while (reader.Read())
                {
                    string url = reader.GetString(0);
                    try
                    {
                        WorkResource resource = Read<WorkResource>(reader.GetString(1));
                        if (resource.Url == url) owners.Add((url, resource.Generation));
                    }
                    catch (Exception exception) when (exception is InvalidDataException or JsonException) { }
                }
            }
            long superseded = 0;
            using (SqliteCommand count = db.CreateCommand())
            {
                count.Transaction = transaction;
                count.CommandText = "SELECT COUNT(*) FROM work_media WHERE resource_url=$url AND generation<$generation";
                SqliteParameter url = count.Parameters.Add("$url", SqliteType.Text), generation = count.Parameters.Add("$generation", SqliteType.Integer);
                foreach ((string Url, long Generation) owner in owners)
                {
                    url.Value = owner.Url; generation.Value = owner.Generation;
                    superseded += (long)count.ExecuteScalar()!;
                }
            }
            if (superseded == 0)
            {
                PerformanceDiagnostics.Record("work.sql", "media-prune", value: 0);
                return new WorkJournalPrune(0, null);
            }
            (long Media, long Resources) expected = CountRows(db, transaction);
            try
            {
                File.Delete(partial);
                using (SqliteConnection source = OpenReadOnly())
                using (SqliteCommand copy = source.CreateCommand())
                {
                    copy.CommandText = "VACUUM INTO $path";
                    copy.Parameters.AddWithValue("$path", partial);
                    copy.ExecuteNonQuery();
                }
                // VACUUM INTO does not sync its output; the copy must be durable before rows go.
                using (FileStream written = new(partial, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) written.Flush(flushToDisk: true);
                // Only a copy that opens, passes the quick check and holds every row may stand in for them.
                using (SqliteConnection copied = new(new SqliteConnectionStringBuilder
                    { DataSource = partial, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString()))
                {
                    copied.Open();
                    using SqliteCommand check = copied.CreateCommand();
                    check.CommandText = "PRAGMA quick_check";
                    if (check.ExecuteScalar() as string != "ok" || CountRows(copied, null) != expected)
                        throw new InvalidDataException("The written copy does not match the work journal.");
                }
                File.Move(partial, backup, overwrite: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SqliteException or InvalidDataException)
            {
                try { File.Delete(partial); } catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException) { }
                PerformanceDiagnostics.Record("work.sql", "media-prune-backup-failed", value: superseded);
                return new WorkJournalPrune(0, $"The work journal backup '{backup}' could not be written ({exception.Message}); " +
                    $"{superseded} superseded media rows were kept.");
            }
            long removed = 0;
            using (SqliteCommand delete = db.CreateCommand())
            {
                delete.Transaction = transaction;
                delete.CommandText = "DELETE FROM work_media WHERE resource_url=$url AND generation<$generation";
                SqliteParameter url = delete.Parameters.Add("$url", SqliteType.Text), generation = delete.Parameters.Add("$generation", SqliteType.Integer);
                int processed = 0;
                foreach ((string Url, long Generation) owner in owners)
                {
                    url.Value = owner.Url; generation.Value = owner.Generation;
                    removed += delete.ExecuteNonQuery();
                    PruneDeleteObserver?.Invoke(++processed);
                }
            }
            // Unreachable under the reserved lock; if it ever differs, roll back rather than guess.
            if (removed != superseded)
                return new WorkJournalPrune(0, "Superseded work journal media changed during pruning; every row was kept.");
            transaction.Commit();
            PerformanceDiagnostics.Record("work.sql", "media-prune", value: removed);
            return new WorkJournalPrune(removed, null);
        }
        catch (SqliteException exception)
        {
            // The transaction rolled back: every row is still in place.
            PerformanceDiagnostics.Record("work.sql", "media-prune-failed", value: 0);
            return new WorkJournalPrune(0, $"Superseded work journal media could not be removed ({exception.Message}); every row was kept.");
        }
    }

    private static (long Media, long Resources) CountRows(SqliteConnection db, SqliteTransaction? transaction)
    {
        using SqliteCommand command = db.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT (SELECT COUNT(*) FROM work_media),(SELECT COUNT(*) FROM work_resources)";
        using SqliteDataReader reader = command.ExecuteReader();
        reader.Read();
        return (reader.GetInt64(0), reader.GetInt64(1));
    }

    private SqliteConnection OpenReadOnly() => Open(SqliteOpenMode.ReadOnly);

    // Only admitted runtime/initialization cores open a writable connection.
    private SqliteConnection Open() => Open(SqliteOpenMode.ReadWriteCreate);

    private SqliteConnection Open(SqliteOpenMode mode)
    {
        // Operations own short-lived native connections. Pooling would retain database
        // handles beyond the profile lease and prevent moving/deleting a closed profile on Windows.
        SqliteConnection db = new(new SqliteConnectionStringBuilder
        { DataSource = _databasePath, Mode = mode, Pooling = false }.ToString());
        try
        {
            db.Open();
            return db;
        }
        catch
        {
            db.Dispose();
            throw;
        }
    }

    // PRAGMA user_version marks the journal layout. 0 is every journal written before the marker;
    // its layout is still detected by columns below. A newer marker fails before any change, so
    // this build never rewrites a layout it does not know. Builds before the marker ignore it.
    private const long SchemaVersion = 1;

    private static void Initialize(SqliteConnection db)
    {
        using SqliteTransaction transaction = db.BeginTransaction();
        using SqliteCommand command = db.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "PRAGMA user_version";
        long version = (long)command.ExecuteScalar()!;
        if (version > SchemaVersion)
            throw new InvalidDataException($"The work journal uses schema version {version}; this version supports up to {SchemaVersion}.");
        command.CommandText = "CREATE TABLE IF NOT EXISTS work_resources(url TEXT PRIMARY KEY,payload TEXT NOT NULL); " +
            "CREATE TABLE IF NOT EXISTS work_media(resource_url TEXT NOT NULL,generation INTEGER NOT NULL,media_id TEXT NOT NULL," +
            "variant TEXT NOT NULL DEFAULT 'original',target TEXT NOT NULL,payload TEXT NOT NULL,PRIMARY KEY(resource_url,generation,media_id,variant,target));";
        command.ExecuteNonQuery();
        command.CommandText = "PRAGMA table_info(work_media)";
        bool hasVariant = false;
        using (SqliteDataReader columns = command.ExecuteReader())
            while (columns.Read()) hasVariant |= string.Equals(columns.GetString(1), "variant", StringComparison.Ordinal);
        if (!hasVariant)
        {
            // SQLite cannot extend a primary key in place. Migrate the table in the same
            // atomic transaction; legacy payloads deserialize with Variant=original.
            command.CommandText = "ALTER TABLE work_media RENAME TO work_media_legacy; " +
                "CREATE TABLE work_media(resource_url TEXT NOT NULL,generation INTEGER NOT NULL,media_id TEXT NOT NULL," +
                "variant TEXT NOT NULL DEFAULT 'original',target TEXT NOT NULL,payload TEXT NOT NULL,PRIMARY KEY(resource_url,generation,media_id,variant,target)); " +
                "INSERT INTO work_media(resource_url,generation,media_id,variant,target,payload) " +
                "SELECT resource_url,generation,media_id,'original',target,payload FROM work_media_legacy; " +
                "DROP TABLE work_media_legacy;";
            command.ExecuteNonQuery();
        }
        if (version < SchemaVersion)
        {
            // Stamped in the same transaction as any layout migration above.
            command.CommandText = "PRAGMA user_version=" + SchemaVersion;
            command.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    private WorkResource? FindResource(SqliteConnection db, SqliteTransaction transaction, string url)
    {
        Interlocked.Increment(ref _resourcePointReads);
        using SqliteCommand command = db.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT payload FROM work_resources WHERE url=$url";
        command.Parameters.AddWithValue("$url", url);
        object? payload = command.ExecuteScalar();
        if (payload == null) return null;
        Interlocked.Increment(ref _materializedRows);
        WorkResource resource = Read<WorkResource>((string)payload);
        if (resource.Url != url) throw new InvalidDataException("Work resource key differs from its payload.");
        return resource;
    }

    private static void WriteResource(SqliteConnection db, SqliteTransaction transaction, WorkResource resource)
    {
        using SqliteCommand command = db.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO work_resources(url,payload) VALUES($url,$payload) ON CONFLICT(url) DO UPDATE SET payload=excluded.payload";
        command.Parameters.AddWithValue("$url", resource.Url);
        command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(resource, Json));
        command.ExecuteNonQuery();
    }

    private static T Read<T>(string payload)
    {
        T value = JsonSerializer.Deserialize<T>(payload, Json) ?? throw new InvalidDataException("A work state row contains null.");
        if (value is WorkResource resource) Validate(resource);
        if (value is MediaWork media) Validate(media);
        return value;
    }
    internal static void Validate(WorkResource resource)
    {
        if (!Uri.TryCreate(resource.Url, UriKind.Absolute, out _) || !Path.IsPathFullyQualified(resource.OutputRoot) || resource.Generation < 1
            || !Enum.IsDefined(resource.Intent) || !Enum.IsDefined(resource.Source))
            throw new InvalidDataException("Invalid durable watch intent.");
    }
    internal static void Validate(MediaWork media)
    {
        if (!Uri.TryCreate(media.ResourceUrl, UriKind.Absolute, out _) || !Uri.TryCreate(media.DownloadUrl, UriKind.Absolute, out _)
            || !Path.IsPathFullyQualified(media.DestinationPath) || string.IsNullOrEmpty(media.MediaIdentifier)
            || string.IsNullOrWhiteSpace(media.Variant) || media.Generation < 1
            || !Enum.IsDefined(media.Status) || media.Attempts < 0 || media.ConsecutiveNotFound < 0 || media.ExpectedSize < 0
            || media.HttpStatus is < 100 or > 599
            || media.DueAt != null && media.LastAttemptAt != null && media.DueAt < media.LastAttemptAt)
            throw new InvalidDataException("Invalid durable media work.");
        if (media.ExpectedSha256 != null && !MediaIntegrityValidation.IsSha256(media.ExpectedSha256)
            || media.CommittedAssetId != null && !MediaIntegrityValidation.IsSha256(media.CommittedAssetId)
            || media.AttachmentReferences == null)
            throw new InvalidDataException("Invalid durable media integrity metadata.");
        MediaProviderMetadata provider = new(media.MediaIdentifier, media.Variant, media.DownloadUrl, media.OriginalFileName,
            media.ExpectedSize, media.ExpectedMd5, media.ExpectedSha256) { ExpectedSha1 = media.ExpectedSha1, ProviderSizeHint = media.ProviderSizeHint };
        // Older work descriptors preserve raw provider MD5 until transfer validation.
        // New optional evidence is strict without making historical payloads unreadable.
        MediaIntegrityValidation.ValidateSha1(media.ExpectedSha1);
        MediaIntegrityValidation.ValidateSizeHint(media.ProviderSizeHint);
        foreach (AttachmentRevisionReference reference in media.AttachmentReferences)
            if (reference == null || reference.RevisionKey != MediaRevisionKeys.CreateAttachmentKey(reference.Resource, reference.PostId, reference.Position, provider)
                || media.AcquisitionRevisionKey != MediaRevisionKeys.CreateAcquisitionKey(reference.Resource, provider))
                throw new InvalidDataException("Invalid durable attachment reference.");
    }
}

internal sealed record UnreadableWorkRow(string Url, string Reason);
internal sealed record WorkJournalPrune(long Removed, string? Warning);
internal sealed record WorkResourceLoad(IReadOnlyList<WorkResource> Resources, IReadOnlyList<UnreadableWorkRow> Unreadable);

internal readonly record struct WorkStoreQueryCounts(long ResourcePointReads, long MediaPointReads,
    long ResourceBatchReads, long MediaBatchReads, long MaterializedRows);
