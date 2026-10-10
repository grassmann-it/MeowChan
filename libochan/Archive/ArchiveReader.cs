using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace oChan.Archive;

/// <summary>
/// Passive local catalog reader. It needs no provider, session, writer or network connection, and
/// never creates a database, acquires a profile lease, migrates a schema or repairs stored content.
/// </summary>
public sealed class ArchiveReader : IAsyncDisposable
{
    private int _disposed;
    private ArchiveReader(string path) { DatabasePath = path; }
    public string DatabasePath { get; }

    public static Task<ArchiveReader> OpenAsync(string profileDirectory, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileDirectory);
        string path = Path.Combine(Path.GetFullPath(profileDirectory), "archive.db");
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!File.Exists(path)) throw new FileNotFoundException("The profile has no archive catalog.", path);
            using SqliteConnection connection = ArchiveSql.OpenConnection(path, SqliteOpenMode.ReadOnly);
            ArchiveSql.ValidateSchema(connection);
            cancellationToken.ThrowIfCancellationRequested();
            return new ArchiveReader(path);
        }, cancellationToken);
    }

    /// <summary>Count and rows come from one read transaction; each page is an immutable point-in-time result.</summary>
    public Task<ArchiveThreadPage> ListThreadsAsync(int offset = 0, int limit = 100, CancellationToken cancellationToken = default)
    {
        ArchiveValidation.ValidatePage(offset, limit);
        return ReadAsync((connection, transaction) => ArchiveSql.ReadPage(connection, transaction, offset, limit), cancellationToken);
    }

    /// <summary>Captures posts and attachment metadata in one transaction; its pages never change afterward.</summary>
    public Task<ArchivedThreadView?> ReadThreadAsync(ResourceId id, CancellationToken cancellationToken = default)
    {
        ArchiveValidation.ValidateId(id);
        return ReadAsync((connection, transaction) => ArchiveSql.ReadThread(connection, transaction, id), cancellationToken);
    }

    public Task<IReadOnlyList<string>> GetMediaDirectoriesAsync(ResourceId id, CancellationToken cancellationToken = default)
    {
        ArchiveValidation.ValidateId(id);
        return ReadAsync((connection, transaction) => ArchiveSql.ReadMediaDirectories(connection, transaction, id), cancellationToken);
    }

    private Task<T> ReadAsync<T>(Func<SqliteConnection, SqliteTransaction, T> read, CancellationToken cancellationToken)
        => Task.Run(() =>
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            cancellationToken.ThrowIfCancellationRequested();
            using SqliteConnection connection = ArchiveSql.OpenConnection(DatabasePath, SqliteOpenMode.ReadOnly);
            ArchiveSql.ValidateSchema(connection);
            using SqliteTransaction transaction = connection.BeginTransaction(deferred: true);
            T value = read(connection, transaction);
            cancellationToken.ThrowIfCancellationRequested();
            transaction.Commit();
            return value;
        }, cancellationToken);

    /// <summary>Closes admission; already-running reads own their connections and may finish.</summary>
    public ValueTask DisposeAsync()
    {
        Interlocked.Exchange(ref _disposed, 1);
        return ValueTask.CompletedTask;
    }
}
