using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace MeowChan.Archive;

/// <summary>
/// Passive local catalog reader. It needs no provider, session, writer or network connection, and
/// never creates a database, acquires a profile lease, migrates a schema or repairs stored content.
/// </summary>
public sealed partial class ArchiveReader : IAsyncDisposable
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

    /// <summary>Filters the local catalog in one read transaction; never opens media directories.</summary>
    public Task<ArchiveThreadPage> SearchThreadsAsync(ArchiveThreadQuery query, int offset = 0, int limit = 100,
        CancellationToken cancellationToken = default)
    {
        return SearchThreadsBySpecAsync(ArchiveSearchRequest.Create(ArchiveSearchSpec.FromLegacy(query)), offset, limit, cancellationToken);
    }

    /// <summary>Freezes the complete ordered matching identities in one local read transaction.</summary>
    public Task<IReadOnlyList<ResourceId>> SelectThreadIdsAsync(ArchiveThreadQuery query,
        CancellationToken cancellationToken = default)
        => SelectThreadIdsBySpecAsync(ArchiveSearchRequest.Create(ArchiveSearchSpec.FromLegacy(query)), cancellationToken);

    /// <summary>Freezes complete matching local thread metadata without accessing media directories.</summary>
    public Task<IReadOnlyList<ArchiveThreadSummary>> SelectThreadSummariesAsync(ArchiveThreadQuery query,
        CancellationToken cancellationToken = default)
        => SelectThreadSummariesBySpecAsync(ArchiveSearchRequest.Create(ArchiveSearchSpec.FromLegacy(query)), cancellationToken);

    /// <summary>Uses the request's frozen date window. WorkFilter is evaluated separately by the desktop.</summary>
    public Task<ArchiveThreadPage> SearchThreadsBySpecAsync(ArchiveSearchRequest request, int offset = 0, int limit = 100,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArchiveValidation.ValidatePage(offset, limit);
        return ReadAsync((connection, transaction) => ArchiveSql.SearchPage(connection, transaction, request, offset, limit, cancellationToken), cancellationToken);
    }

    /// <summary>Uses the same predicate as count and paging in one local read transaction.</summary>
    public Task<IReadOnlyList<ResourceId>> SelectThreadIdsBySpecAsync(ArchiveSearchRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ReadAsync((connection, transaction) => ArchiveSql.SearchIds(connection, transaction, request, cancellationToken), cancellationToken);
    }

    /// <summary>Freezes all matching metadata; the desktop applies its captured WorkFilter membership.</summary>
    public Task<IReadOnlyList<ArchiveThreadSummary>> SelectThreadSummariesBySpecAsync(ArchiveSearchRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ReadAsync((connection, transaction) => ArchiveSql.SearchSummaries(connection, transaction, request, cancellationToken), cancellationToken);
    }

    /// <summary>Captures posts and attachment metadata in one transaction; its pages never change afterward.</summary>
    public Task<ArchivedThreadView?> ReadThreadAsync(ResourceId id, CancellationToken cancellationToken = default)
    {
        ArchiveValidation.ValidateId(id);
        return ReadAsync((connection, transaction) => ArchiveSql.ReadThread(connection, transaction, id), cancellationToken);
    }

    internal Task<ArchiveExportSnapshot?> ReadExportAsync(ResourceId id, CancellationToken cancellationToken)
    {
        ArchiveValidation.ValidateId(id);
        return ReadAsync((connection, transaction) =>
        {
            ArchivedThreadView? view = ArchiveSql.ReadThread(connection, transaction, id);
            return view == null ? null : new ArchiveExportSnapshot(view,
                ArchiveCatalog.ReadDecisions(connection, transaction, id));
        }, cancellationToken);
    }

    public Task<IReadOnlyList<string>> GetMediaDirectoriesAsync(ResourceId id, CancellationToken cancellationToken = default)
    {
        ArchiveValidation.ValidateId(id);
        return ReadAsync((connection, transaction) => ArchiveSql.ReadMediaDirectories(connection, transaction, id), cancellationToken);
    }

    public Task<ArchiveQuotePage> GetQuotesAsync(ArchivePostId post, int offset = 0, int limit = 100,
        CancellationToken cancellationToken = default) => QuotePageAsync(post, false, offset, limit, cancellationToken);

    public Task<ArchiveQuotePage> GetBacklinksAsync(ArchivePostId post, int offset = 0, int limit = 100,
        CancellationToken cancellationToken = default) => QuotePageAsync(post, true, offset, limit, cancellationToken);

    private Task<ArchiveQuotePage> QuotePageAsync(ArchivePostId post, bool backlinks, int offset, int limit, CancellationToken cancellationToken)
    {
        ValidatePostId(post);
        ArchiveValidation.ValidatePage(offset, limit);
        return ReadAsync((connection, transaction) => ArchiveSql.ReadQuotes(connection, transaction, post, backlinks, offset, limit, cancellationToken), cancellationToken);
    }

    /// <summary>Resolves a quote against captured local posts only. Missing/external targets never trigger HTTP.</summary>
    public Task<ArchiveQuoteResolution> ResolveQuoteAsync(ArchiveQuote quote, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(quote);
        ValidatePostId(quote.Origin);
        if (quote.Target == null)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new ArchiveQuoteResolution(quote,
                quote.IsExternal ? ArchiveQuoteState.External : ArchiveQuoteState.Unresolved));
        }
        ValidatePostId(quote.Target);
        return ReadAsync((connection, transaction) =>
        {
            ArchivedThreadView? view = ArchiveSql.ReadThread(connection, transaction, quote.Target.Thread);
            if (view == null) return new ArchiveQuoteResolution(quote, ArchiveQuoteState.MissingThread);
            int index = view.FindPostIndex(quote.Target.PostId);
            return index < 0 ? new ArchiveQuoteResolution(quote, ArchiveQuoteState.MissingPost, view.Summary) { View = view }
                : new ArchiveQuoteResolution(quote, ArchiveQuoteState.Captured, view.Summary, view.Posts[index], index) { View = view };
        }, cancellationToken);
    }

    private static void ValidatePostId(ArchivePostId post)
    {
        ArgumentNullException.ThrowIfNull(post);
        ArchiveValidation.ValidateId(post.Thread);
        if (string.IsNullOrWhiteSpace(post.PostId) || post.PostId.Length > 256 || post.PostId.Any(char.IsControl))
            throw new ArgumentException("A post identity must be nonempty and contain no control characters.", nameof(post));
    }

    private Task<T> ReadAsync<T>(Func<SqliteConnection, SqliteTransaction, T> read, CancellationToken cancellationToken)
        => Task.Run(() =>
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            cancellationToken.ThrowIfCancellationRequested();
            using SqliteConnection connection = ArchiveSql.OpenConnection(DatabasePath, SqliteOpenMode.ReadOnly);
            ArchiveSql.ValidateSchema(connection);
            using SqliteTransaction transaction = connection.BeginTransaction(deferred: true);
            T value;
            try { value = read(connection, transaction); }
            catch (Exception) when (cancellationToken.IsCancellationRequested) { throw new OperationCanceledException(cancellationToken); }
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
