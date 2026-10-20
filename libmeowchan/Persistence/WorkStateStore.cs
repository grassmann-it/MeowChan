namespace MeowChan.Persistence;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Data.Sqlite;

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
public enum MediaWorkStatus { Ready, Waiting, Succeeded, NeedsIntervention, Expired }

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
    public required string DestinationPath { get; init; }
    public required string DownloadUrl { get; init; }
    public string? ExpectedMd5 { get; init; }
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
public sealed class WorkStateStore : IWorkStateStore
{
    private readonly ProfileLease _lease;
    private readonly string _databasePath;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public WorkStateStore(string profileDirectory, ProfileLease lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        lease.EnsureMatches(profileDirectory);
        _lease = lease;
        _databasePath = Path.Combine(lease.ProfileDirectory, "work-state.sqlite");
        _lease.RunExclusive(() => { using SqliteConnection db = Open(); Initialize(db); return true; });
    }

    public IReadOnlyList<WorkResource> LoadResources() => _lease.RunExclusive(() =>
    {
        using SqliteConnection db = Open();
        using SqliteCommand command = db.CreateCommand();
        command.CommandText = "SELECT url,payload FROM work_resources ORDER BY url";
        using SqliteDataReader reader = command.ExecuteReader();
        List<WorkResource> result = new();
        while (reader.Read())
        {
            WorkResource resource = Read<WorkResource>(reader.GetString(1));
            if (resource.Url != reader.GetString(0)) throw new InvalidDataException("Work resource key differs from its payload.");
            result.Add(resource);
        }
        return (IReadOnlyList<WorkResource>)result;
    });

    public IReadOnlyList<MediaWork> LoadMedia(string url, long generation) => _lease.RunExclusive(() =>
    {
        using SqliteConnection db = Open();
        using SqliteCommand command = db.CreateCommand();
        command.CommandText = "SELECT media_id,target,payload FROM work_media WHERE resource_url=$url AND generation=$generation ORDER BY media_id,target";
        command.Parameters.AddWithValue("$url", url);
        command.Parameters.AddWithValue("$generation", generation);
        using SqliteDataReader reader = command.ExecuteReader();
        List<MediaWork> result = new();
        while (reader.Read())
        {
            MediaWork media = Read<MediaWork>(reader.GetString(2));
            if (media.ResourceUrl != url || media.Generation != generation || media.MediaIdentifier != reader.GetString(0)
                || media.DestinationPath != reader.GetString(1)) throw new InvalidDataException("Media work key differs from its payload.");
            result.Add(media);
        }
        return (IReadOnlyList<MediaWork>)result;
    });

    public WorkResource CommitIntent(WorkResource resource) => _lease.RunExclusive(() =>
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

    public WorkResource? CommitEnd(string url, long generation, ThreadSourceStateValue source, DateTimeOffset observedAt, string? finalSnapshotJson = null)
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

    public WorkResource? CommitState(WorkResource resource) => _lease.RunExclusive(() =>
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

    public bool CommitMedia(MediaWork media) => _lease.RunExclusive(() =>
    {
        Validate(media);
        using SqliteConnection db = Open();
        using SqliteTransaction transaction = db.BeginTransaction();
        WorkResource? owner = FindResource(db, transaction, media.ResourceUrl);
        if (owner == null || owner.Generation != media.Generation || owner.Intent == WorkIntent.Suppressed) return false;
        using SqliteCommand command = db.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO work_media(resource_url,generation,media_id,target,payload) VALUES($url,$generation,$id,$target,$payload) ON CONFLICT(resource_url,generation,media_id,target) DO UPDATE SET payload=excluded.payload";
        command.Parameters.AddWithValue("$url", media.ResourceUrl);
        command.Parameters.AddWithValue("$generation", media.Generation);
        command.Parameters.AddWithValue("$id", media.MediaIdentifier);
        command.Parameters.AddWithValue("$target", media.DestinationPath);
        command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(media, Json));
        command.ExecuteNonQuery();
        transaction.Commit();
        return true;
    });

    private SqliteConnection Open()
    {
        SqliteConnection db = new(new SqliteConnectionStringBuilder { DataSource = _databasePath, Mode = SqliteOpenMode.ReadWriteCreate }.ToString());
        db.Open();
        return db;
    }

    private static void Initialize(SqliteConnection db)
    {
        using SqliteCommand command = db.CreateCommand();
        command.CommandText = "CREATE TABLE IF NOT EXISTS work_resources(url TEXT PRIMARY KEY,payload TEXT NOT NULL); CREATE TABLE IF NOT EXISTS work_media(resource_url TEXT NOT NULL,generation INTEGER NOT NULL,media_id TEXT NOT NULL,target TEXT NOT NULL,payload TEXT NOT NULL,PRIMARY KEY(resource_url,generation,media_id,target));";
        command.ExecuteNonQuery();
    }

    private static WorkResource? FindResource(SqliteConnection db, SqliteTransaction transaction, string url)
    {
        using SqliteCommand command = db.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT payload FROM work_resources WHERE url=$url";
        command.Parameters.AddWithValue("$url", url);
        object? payload = command.ExecuteScalar();
        if (payload == null) return null;
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
            || !Path.IsPathFullyQualified(media.DestinationPath) || string.IsNullOrEmpty(media.MediaIdentifier) || media.Generation < 1
            || !Enum.IsDefined(media.Status) || media.Attempts < 0 || media.ConsecutiveNotFound < 0 || media.ExpectedSize < 0
            || media.HttpStatus is < 100 or > 599
            || media.DueAt != null && media.LastAttemptAt != null && media.DueAt < media.LastAttemptAt)
            throw new InvalidDataException("Invalid durable media work.");
    }
}
