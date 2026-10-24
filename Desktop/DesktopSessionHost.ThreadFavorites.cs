using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MeowChan.Favorites;

namespace MeowChan.Desktop;

internal enum ThreadFavoriteLoadState { Ready, Unavailable }
// Unavailable never supplies an empty protection set. Cleanup callers must require Ready.
internal sealed record ThreadFavoriteLoadSnapshot(ThreadFavoriteLoadState State,
    ThreadFavoriteSnapshot? Snapshot, string? Error);

internal sealed partial class DesktopSessionHost
{
    private readonly object _threadFavoritesSync = new();
    private ThreadFavoriteStore? _threadFavoritesStore;
    private ThreadFavoriteSnapshot? _threadFavorites;

    /// <summary>Revalidates the complete on-disk protection set, independent of filters, pages and media.</summary>
    internal ThreadFavoriteLoadSnapshot GetThreadFavoriteSnapshot()
    {
        lock (_lifecycle)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            lock (_threadFavoritesSync)
            {
                try
                {
                    _threadFavoritesStore ??= new(_profileLease.ProfileDirectory, _profileLease);
                    _threadFavorites = _threadFavoritesStore.ReadSnapshot();
                    return new(ThreadFavoriteLoadState.Ready, _threadFavorites, null);
                }
                catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
                { return new(ThreadFavoriteLoadState.Unavailable, null, error.Message); }
            }
        }
    }

    internal IReadOnlyList<ResourceId> GetThreadFavorites()
    {
        ThreadFavoriteLoadSnapshot loaded = GetThreadFavoriteSnapshot();
        return loaded.Snapshot?.Threads ?? throw new InvalidDataException(loaded.Error);
    }

    internal async Task<ThreadFavoriteCommit> SetThreadFavoriteAsync(ResourceId thread, bool favorite,
        CancellationToken token = default)
    {
        ThreadFavoriteStore.Validate(thread);
        await _commands.WaitAsync(token).ConfigureAwait(false);
        try
        {
            EnsureReady();
            ThreadFavoriteCommit commit;
            lock (_threadFavoritesSync)
            {
                _threadFavoritesStore ??= new(_profileLease.ProfileDirectory, _profileLease);
                commit = _threadFavoritesStore.Set(thread, favorite, token);
                _threadFavorites = commit.Snapshot;
            }
            if (commit.Changed) SignalChanged();
            return commit;
        }
        finally { _commands.Release(); }
    }
}
