using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Threading;
using MeowChan.Archive;
using MeowChan.Favorites;
using MeowChan.Persistence;

namespace MeowChan.Transfer;

// An explicit selection, never inferred from a package, media filter or previous import.
// default(NativePrivateMetadataSelection) intentionally selects no private data.
internal readonly record struct NativePrivateMetadataSelection(
    bool ThreadFavorites = false, bool MediaFavorites = false, bool PostBookmarksAndNotes = false);

/// <summary>
/// Read-only, selected-only input for a future native export planner. Not a wire
/// object or import command. Owner authority and full-store fences stay local.
/// </summary>
internal sealed class NativePrivateMetadataSnapshot
{
    internal const int MaximumThreads = 1_000;
    private readonly ProfileLease _owner;
    private readonly StoreFence? _threadFence;
    private readonly StoreFence? _mediaFence;
    private readonly StoreFence? _bookmarkFence;

    // Only scalar fences are retained, not full snapshots of out-of-scope private entries.
    private readonly record struct StoreFence(Guid? Instance, long Revision, string Fingerprint);

    private NativePrivateMetadataSnapshot(ProfileLease owner, NativePrivateMetadataSelection selection,
        ImmutableArray<ResourceId> threads, ImmutableArray<ResourceId> threadFavorites,
        ImmutableArray<FavoriteEntry> mediaFavorites, ImmutableArray<PostBookmark> bookmarks,
        StoreFence? threadFence, StoreFence? mediaFence, StoreFence? bookmarkFence)
    {
        _owner = owner;
        Selection = selection;
        Threads = threads;
        ThreadFavorites = threadFavorites;
        MediaFavorites = mediaFavorites;
        PostBookmarksAndNotes = bookmarks;
        _threadFence = threadFence;
        _mediaFence = mediaFence;
        _bookmarkFence = bookmarkFence;
    }

    internal NativePrivateMetadataSelection Selection { get; }
    internal ImmutableArray<ResourceId> Threads { get; }
    internal ImmutableArray<ResourceId> ThreadFavorites { get; }
    internal ImmutableArray<FavoriteEntry> MediaFavorites { get; }
    internal ImmutableArray<PostBookmark> PostBookmarksAndNotes { get; }

    internal static NativePrivateMetadataSnapshot Capture(ProfileLease owner, IEnumerable<ResourceId> threads,
        NativePrivateMetadataSelection selection = default, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(threads);
        cancellationToken.ThrowIfCancellationRequested();
        var frozen = ImmutableArray.CreateBuilder<ResourceId>();
        HashSet<ResourceId> scope = new();
        foreach (ResourceId thread in threads)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ArchiveValidation.ValidateId(thread);
            ThreadFavoriteStore.Validate(thread); // Includes strict Unicode validation.
            if (frozen.Count >= MaximumThreads || !scope.Add(thread))
                throw new ArgumentException("The private metadata thread selection is too large or contains duplicate identities.", nameof(threads));
            frozen.Add(thread);
        }
        ImmutableArray<ResourceId> threadScope = frozen.ToImmutable();
        // Existing stores cooperate on this reentrant synchronous monitor. Never
        // await, read media bytes, contact providers or invoke bookmark cleanup here.
        return owner.RunExclusive(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            StoreFence? threadFence = null, mediaFence = null, bookmarkFence = null;
            ImmutableArray<ResourceId> favorites = [];
            ImmutableArray<FavoriteEntry> media = [];
            ImmutableArray<PostBookmark> bookmarks = [];
            if (selection.ThreadFavorites)
            {
                ThreadFavoriteSnapshot read = new ThreadFavoriteStore(owner.ProfileDirectory, owner).ReadSnapshot();
                threadFence = new(null, read.Revision, read.Fingerprint);
                favorites = Project(read.Threads, id => scope.Contains(id), cancellationToken);
            }
            if (selection.MediaFavorites)
            {
                FavoriteStoreSnapshot read = new FavoriteStore(owner.ProfileDirectory, owner).ReadTransferSnapshot();
                mediaFence = new(read.InstanceId, read.Revision, read.Fingerprint);
                media = Project(read.Entries, entry => scope.Contains(entry.Key.Thread), cancellationToken);
            }
            if (selection.PostBookmarksAndNotes)
            {
                PostBookmarkTransferSnapshot read = PostBookmarkStore.ReadTransferSnapshot(owner.ProfileDirectory, owner);
                bookmarkFence = new(null, read.Revision, read.Fingerprint);
                bookmarks = Project(read.Bookmarks, entry => scope.Contains(entry.Key.Thread), cancellationToken);
            }
            cancellationToken.ThrowIfCancellationRequested();
            return new NativePrivateMetadataSnapshot(owner, selection, threadScope, favorites, media, bookmarks,
                threadFence, mediaFence, bookmarkFence);
        });
    }

    /// <summary>
    /// Rechecks only selected stores under the original live lease. A malformed
    /// selected store throws rather than being mistaken for a current empty store.
    /// A new store object under the same lease is fine; another lease is not.
    /// </summary>
    internal bool IsCurrent(ProfileLease owner, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);
        cancellationToken.ThrowIfCancellationRequested();
        if (!ReferenceEquals(owner, _owner)) return false;
        return owner.RunExclusive(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_threadFence is StoreFence threadFence)
            {
                ThreadFavoriteSnapshot read = new ThreadFavoriteStore(owner.ProfileDirectory, owner).ReadSnapshot();
                if (threadFence != new StoreFence(null, read.Revision, read.Fingerprint)) return false;
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (_mediaFence is StoreFence mediaFence)
            {
                FavoriteStoreSnapshot read = new FavoriteStore(owner.ProfileDirectory, owner).ReadTransferSnapshot();
                if (mediaFence != new StoreFence(read.InstanceId, read.Revision, read.Fingerprint)) return false;
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (_bookmarkFence is StoreFence bookmarkFence)
            {
                PostBookmarkTransferSnapshot read = PostBookmarkStore.ReadTransferSnapshot(owner.ProfileDirectory, owner);
                if (bookmarkFence != new StoreFence(null, read.Revision, read.Fingerprint)) return false;
            }
            cancellationToken.ThrowIfCancellationRequested();
            return true;
        });
    }

    private static ImmutableArray<T> Project<T>(IEnumerable<T> values, Func<T, bool> included, CancellationToken token)
    {
        var result = ImmutableArray.CreateBuilder<T>();
        foreach (T value in values)
        {
            token.ThrowIfCancellationRequested();
            if (included(value)) result.Add(value);
        }
        return result.ToImmutable();
    }
}
