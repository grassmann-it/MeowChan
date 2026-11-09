using System;
using System.Collections.Immutable;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Text;
using MeowChan.Favorites;
using MeowChan.Persistence;

namespace MeowChan.Transfer;

/// <summary>Passive selected private values, never a profile lease, consent receipt or apply command.</summary>
internal sealed class NativePrivateMetadataProjection
{
    internal NativePrivateMetadataProjection(ImmutableArray<ResourceId> threads,
        NativePrivateMetadataSelection selection, ImmutableArray<ResourceId> threadFavorites,
        ImmutableArray<FavoriteAssetKey> mediaFavorites, ImmutableArray<PostBookmark> postBookmarksAndNotes,
        ImmutableArray<FavoriteEntry>? mediaFavoriteEntries = null)
    {
        Threads = threads;
        Selection = selection;
        ThreadFavorites = threadFavorites;
        MediaFavorites = mediaFavorites;
        PostBookmarksAndNotes = postBookmarksAndNotes;
        MediaFavoriteEntries = mediaFavoriteEntries is { IsDefault: false } entries
            ? entries : [];
    }

    internal ImmutableArray<ResourceId> Threads { get; }
    internal NativePrivateMetadataSelection Selection { get; }
    internal ImmutableArray<ResourceId> ThreadFavorites { get; }
    internal ImmutableArray<FavoriteAssetKey> MediaFavorites { get; }
    // Optional aligned source values. An empty array means that this passive
    // projection predates the sidecar or deliberately has no media entries; it
    // never means that a length or filename may be invented by a receiver.
    internal ImmutableArray<FavoriteEntry> MediaFavoriteEntries { get; }
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
        var mediaEntries = ImmutableArray.CreateBuilder<FavoriteEntry>(snapshot.MediaFavorites.Length);
        foreach (FavoriteEntry favorite in snapshot.MediaFavorites)
        {
            cancellationToken.ThrowIfCancellationRequested();
            mediaEntries.Add(favorite);
        }
        ImmutableArray<FavoriteEntry> entries = mediaEntries.ToImmutable();
        var result = new NativePrivateMetadataProjection(snapshot.Threads, snapshot.Selection,
            snapshot.ThreadFavorites, entries.Select(entry => entry.Key).ToImmutableArray(),
            snapshot.PostBookmarksAndNotes, entries);
        NativePrivateMetadataCodec.Validate(result, cancellationToken);
        ValidateMediaFavoriteEntries(result, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return result;
    }

    // A receiving caller has to choose again. Incoming presence never implies opt-in.
    internal NativePrivateMetadataProjection Select(NativePrivateMetadataSelection selection = default,
        CancellationToken cancellationToken = default)
    {
        NativePrivateMetadataCodec.Validate(this, cancellationToken);
        ValidateMediaFavoriteEntries(this, cancellationToken);
        if (selection.ThreadFavorites && !Selection.ThreadFavorites
            || selection.MediaFavorites && !Selection.MediaFavorites
            || selection.PostBookmarksAndNotes && !Selection.PostBookmarksAndNotes)
            throw new InvalidDataException("The requested private metadata category is not present.");
        cancellationToken.ThrowIfCancellationRequested();
        ImmutableArray<FavoriteEntry> selectedMediaEntries = [];
        if (selection.MediaFavorites && !MediaFavoriteEntries.IsEmpty)
        {
            HashSet<FavoriteAssetKey> selectedKeys = (selection.MediaFavorites ? MediaFavorites : []).ToHashSet();
            selectedMediaEntries = MediaFavoriteEntries.Where(entry => selectedKeys.Contains(entry.Key)).ToImmutableArray();
        }
        return new(Threads, selection,
            selection.ThreadFavorites ? ThreadFavorites : [],
            selection.MediaFavorites ? MediaFavorites : [],
            selection.PostBookmarksAndNotes ? PostBookmarksAndNotes : [],
            selectedMediaEntries);
    }

    // Used only by the request codec to reattach its aligned, separately-bound
    // media metadata to the identity-only passive payload.
    internal NativePrivateMetadataProjection WithMediaFavoriteEntries(
        ImmutableArray<FavoriteEntry> mediaFavoriteEntries,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = new NativePrivateMetadataProjection(Threads, Selection, ThreadFavorites,
            MediaFavorites, PostBookmarksAndNotes, mediaFavoriteEntries);
        NativePrivateMetadataCodec.Validate(result, cancellationToken);
        ValidateMediaFavoriteEntries(result, cancellationToken);
        return result;
    }

    internal static void ValidateMediaFavoriteEntries(NativePrivateMetadataProjection value,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(value);
        cancellationToken.ThrowIfCancellationRequested();
        if (value.MediaFavoriteEntries.IsDefault)
            throw new InvalidDataException("The private media favorite metadata is uninitialized.");
        if (!value.Selection.MediaFavorites && !value.MediaFavoriteEntries.IsEmpty)
            throw new InvalidDataException("Media favorite metadata is present for an unselected category.");
        if (value.MediaFavoriteEntries.IsEmpty)
            return;
        if (value.MediaFavoriteEntries.Length != value.MediaFavorites.Length)
            throw new InvalidDataException("The private media favorite metadata is incomplete.");
        HashSet<FavoriteAssetKey> keys = value.MediaFavorites.ToHashSet();
        HashSet<FavoriteAssetKey> seen = new();
        foreach (FavoriteEntry entry in value.MediaFavoriteEntries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry is null || entry.Key is null || entry.Length < 0
                || !keys.Contains(entry.Key) || !seen.Add(entry.Key))
                throw new InvalidDataException("The private media favorite metadata is invalid.");
            try { FavoriteStore.Validate(entry.Key); }
            catch (ArgumentException) { throw new InvalidDataException("The private media favorite metadata is invalid."); }
            if (entry.OriginalFileName is not null)
            {
                try { _ = StrictUtf8.GetByteCount(entry.OriginalFileName); }
                catch (EncoderFallbackException)
                { throw new InvalidDataException("The private media favorite metadata is invalid."); }
            }
        }
        if (seen.Count != keys.Count)
            throw new InvalidDataException("The private media favorite metadata is incomplete.");
    }

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
}
