using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using MeowChan.Favorites;
using MeowChan.Persistence;

namespace MeowChan.Transfer;

/// <summary>
/// Binds a passive private projection to an already validated public catalog. No receiving
/// selection, profile authority, store mutation or missing-content inference is implied.
/// </summary>
internal static class NativePrivateMetadataCatalogBinding
{
    internal static void Validate(NativeTransferCatalog catalog, NativePrivateMetadataProjection projection,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            if (catalog is null || projection is null || catalog.Threads.IsDefault)
                throw InvalidBinding();
            NativePrivateMetadataCodec.Validate(projection, ct);
            if (!projection.Selection.ThreadFavorites && !projection.Selection.MediaFavorites
                && !projection.Selection.PostBookmarksAndNotes)
                throw InvalidBinding();

            HashSet<ResourceId> publicThreads = new();
            foreach (TransferThread thread in catalog.Threads)
            {
                ct.ThrowIfCancellationRequested();
                if (thread is null || thread.Id is null || !publicThreads.Add(thread.Id))
                    throw InvalidBinding();
            }
            if (publicThreads.Count != projection.Threads.Length) throw InvalidBinding();
            foreach (ResourceId thread in projection.Threads)
            {
                ct.ThrowIfCancellationRequested();
                if (!publicThreads.Contains(thread)) throw InvalidBinding();
            }
            foreach (ResourceId favorite in projection.ThreadFavorites)
            {
                ct.ThrowIfCancellationRequested();
                if (!publicThreads.Contains(favorite)) throw InvalidBinding();
            }

            // Keep only outstanding requested identities, not public content or private note text.
            HashSet<(ResourceId Thread, string Post)> bookmarks = new();
            foreach (PostBookmark bookmark in projection.PostBookmarksAndNotes)
            {
                ct.ThrowIfCancellationRequested();
                bookmarks.Add((bookmark.Key.Thread, bookmark.Key.Post));
            }
            HashSet<(ResourceId Thread, string MediaId, string Variant, string Sha256)> media = new();
            foreach (FavoriteAssetKey favorite in projection.MediaFavorites)
            {
                ct.ThrowIfCancellationRequested();
                media.Add((favorite.Thread, favorite.MediaIdentifier, favorite.Variant, favorite.Sha256));
            }
            foreach (TransferThread thread in catalog.Threads)
            {
                ct.ThrowIfCancellationRequested();
                if (bookmarks.Count == 0 && media.Count == 0) break;
                foreach (TransferPost post in thread.Posts)
                {
                    ct.ThrowIfCancellationRequested();
                    bookmarks.Remove((thread.Id, post.Metadata.PostId));
                    if (media.Count == 0) continue;
                    foreach (TransferAttachment attachment in post.Attachments)
                    {
                        ct.ThrowIfCancellationRequested();
                        MatchMedia(thread.Id, attachment);
                    }
                }
                if (media.Count == 0) continue;
                foreach (TransferRetainedBinding retained in thread.RetainedBindings)
                {
                    ct.ThrowIfCancellationRequested();
                    // A retained binding is not a captured post and cannot satisfy a bookmark.
                    MatchMedia(thread.Id, retained.Attachment);
                }
            }
            if (bookmarks.Count != 0 || media.Count != 0) throw InvalidBinding();
            ct.ThrowIfCancellationRequested();

            void MatchMedia(ResourceId thread, TransferAttachment attachment)
            {
                if (attachment.Status == TransferContentStatus.Contained
                    && attachment.Metadata.MediaId is { } id && attachment.PayloadSha256 is { } hash)
                    media.Remove((thread, id, attachment.Metadata.Variant, hash));
            }
        }
        catch (Exception error) when (error is InvalidDataException or ArgumentException or InvalidOperationException)
        {
            // Never propagate values, codec diagnostics or an inner exception from private input.
            throw InvalidBinding();
        }
    }

    private static InvalidDataException InvalidBinding() =>
        new("The private metadata does not bind to the public catalog.");
}
