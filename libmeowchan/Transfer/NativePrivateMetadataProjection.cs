using System;
using System.Collections.Immutable;
using System.IO;
using System.Threading;
using MeowChan.Favorites;
using MeowChan.Persistence;

namespace MeowChan.Transfer;

/// <summary>Passive selected private values, never a profile lease, consent receipt or apply command.</summary>
internal sealed class NativePrivateMetadataProjection
{
    internal NativePrivateMetadataProjection(ImmutableArray<ResourceId> threads,
        NativePrivateMetadataSelection selection, ImmutableArray<ResourceId> threadFavorites,
        ImmutableArray<FavoriteAssetKey> mediaFavorites, ImmutableArray<PostBookmark> postBookmarksAndNotes)
    {
        Threads = threads;
        Selection = selection;
        ThreadFavorites = threadFavorites;
        MediaFavorites = mediaFavorites;
        PostBookmarksAndNotes = postBookmarksAndNotes;
    }

    internal ImmutableArray<ResourceId> Threads { get; }
    internal NativePrivateMetadataSelection Selection { get; }
    internal ImmutableArray<ResourceId> ThreadFavorites { get; }
    internal ImmutableArray<FavoriteAssetKey> MediaFavorites { get; }
    internal ImmutableArray<PostBookmark> PostBookmarksAndNotes { get; }

    internal static NativePrivateMetadataProjection FromSnapshot(NativePrivateMetadataSnapshot snapshot,
        ProfileLease owner, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(owner);
        cancellationToken.ThrowIfCancellationRequested();
        // The source snapshot owns immutable values. Only the freshness check touches
        // the profile lease; copying and validating the passive projection must not
        // hold the writer monitor for the full entry/text budget.
        if (!snapshot.IsCurrent(owner, cancellationToken))
            throw new InvalidDataException("The private metadata snapshot is no longer current.");
        cancellationToken.ThrowIfCancellationRequested();
        var media = ImmutableArray.CreateBuilder<FavoriteAssetKey>(snapshot.MediaFavorites.Length);
        foreach (FavoriteEntry favorite in snapshot.MediaFavorites)
        {
            cancellationToken.ThrowIfCancellationRequested();
            media.Add(favorite.Key);
        }
        var result = new NativePrivateMetadataProjection(snapshot.Threads, snapshot.Selection,
            snapshot.ThreadFavorites, media.ToImmutable(), snapshot.PostBookmarksAndNotes);
        NativePrivateMetadataCodec.Validate(result, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return result;
    }

    // A receiving caller has to choose again. Incoming presence never implies opt-in.
    internal NativePrivateMetadataProjection Select(NativePrivateMetadataSelection selection = default,
        CancellationToken cancellationToken = default)
    {
        NativePrivateMetadataCodec.Validate(this, cancellationToken);
        if (selection.ThreadFavorites && !Selection.ThreadFavorites
            || selection.MediaFavorites && !Selection.MediaFavorites
            || selection.PostBookmarksAndNotes && !Selection.PostBookmarksAndNotes)
            throw new InvalidDataException("The requested private metadata category is not present.");
        cancellationToken.ThrowIfCancellationRequested();
        return new(Threads, selection,
            selection.ThreadFavorites ? ThreadFavorites : [],
            selection.MediaFavorites ? MediaFavorites : [],
            selection.PostBookmarksAndNotes ? PostBookmarksAndNotes : []);
    }
}
