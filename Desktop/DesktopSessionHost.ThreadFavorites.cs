using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MeowChan.Favorites;
using MeowChan.Persistence;

namespace MeowChan.Desktop;

// Presentation-only state, never serialized; keep existing numeric values stable.
internal enum ThreadFavoriteLoadState { Ready = 0, Unavailable = 1, Loading = 2 }
// Unavailable never supplies an empty protection set. Cleanup callers must require Ready.
internal sealed record ThreadFavoriteLoadSnapshot(ThreadFavoriteLoadState State,
    ThreadFavoriteSnapshot? Snapshot, string? Error);

internal sealed partial class DesktopSessionHost
{
    private readonly object _threadFavoritesSync = new();
    private ThreadFavoriteStore? _threadFavoritesStore;
    private ThreadFavoriteSnapshot? _threadFavorites;

    private ThreadFavoriteLoadSnapshot _publishedThreadFavorites = new(ThreadFavoriteLoadState.Loading, null, null);
    internal ThreadFavoriteLoadSnapshot GetPublishedThreadFavoriteSnapshot() => Volatile.Read(ref _publishedThreadFavorites);

    /// <summary>Revalidates the complete on-disk protection set, independent of filters, pages and media.</summary>
    internal ThreadFavoriteLoadSnapshot GetThreadFavoriteSnapshot()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_threadFavoritesSync)
        {
            try
            {
                _threadFavoritesStore ??= new(_profileLease.ProfileDirectory, _profileLease);
                _threadFavorites = _threadFavoritesStore.ReadSnapshot();
                var published = new ThreadFavoriteLoadSnapshot(ThreadFavoriteLoadState.Ready, _threadFavorites, null);
                Volatile.Write(ref _publishedThreadFavorites, published);
                return published;
            }
            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
            {
                var unavailable = new ThreadFavoriteLoadSnapshot(ThreadFavoriteLoadState.Unavailable, null, error.Message);
                Volatile.Write(ref _publishedThreadFavorites, unavailable);
                return unavailable;
            }
        }
    }

    internal IReadOnlyList<ResourceId> GetThreadFavorites()
    {
        ThreadFavoriteLoadSnapshot loaded = GetThreadFavoriteSnapshot();
        return loaded.Snapshot?.Threads ?? throw new InvalidDataException(loaded.Error);
    }

    internal Task<ThreadFavoriteCommit> SetThreadFavoriteAsync(ResourceId thread, bool favorite,
        CancellationToken token = default)
        => RunInteractiveWithAdmissionAsync(nameof(SetThreadFavoriteAsync), thread.ToString(), admission => SetThreadFavoriteAdmittedCoreAsync(thread, favorite, admission, token));

    private async Task<ThreadFavoriteCommit> SetThreadFavoriteAdmittedCoreAsync(ResourceId thread, bool favorite,
        ProfileAdmissionToken admission, CancellationToken token = default)
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
                commit = _threadFavoritesStore.SetAdmitted(thread, favorite, admission, token);
                _threadFavorites = commit.Snapshot;
                Volatile.Write(ref _publishedThreadFavorites, new(ThreadFavoriteLoadState.Ready, commit.Snapshot, null));
            }
            if (commit.Changed) SignalChanged();
            return commit;
        }
        finally { _commands.Release(); }
    }
}
