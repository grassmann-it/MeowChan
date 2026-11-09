using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using MeowChan.Archive;
using MeowChan.Favorites;
using MeowChan.Persistence;

namespace MeowChan.Transfer;

// A comparison is a passive candidate list. It never grants consent and never
// authorizes a store mutation. Keep this enum separate from commit/recovery states.
internal enum NativePrivateMetadataDisposition { Add, Identical, Conflict, MissingTarget }

internal sealed class NativePrivateThreadFavoriteComparison
{
    internal NativePrivateThreadFavoriteComparison(ResourceId target, NativePrivateMetadataDisposition disposition)
    { Target = target; Disposition = disposition; }
    internal ResourceId Target { get; }
    internal NativePrivateMetadataDisposition Disposition { get; }
}

internal sealed class NativePrivateMediaFavoriteComparison
{
    internal NativePrivateMediaFavoriteComparison(FavoriteAssetKey target,
        NativePrivateMetadataDisposition disposition, FavoriteEntry? incomingEntry = null)
    { Target = target; Disposition = disposition; IncomingEntry = incomingEntry; }
    internal FavoriteAssetKey Target { get; }
    // Optional only for legacy identity-only passive projections. A populated
    // projection carries the complete store value and never a fabricated fallback.
    internal FavoriteEntry? IncomingEntry { get; }
    internal NativePrivateMetadataDisposition Disposition { get; }
}

internal sealed class NativePrivateBookmarkComparison
{
    internal NativePrivateBookmarkComparison(PostBookmarkKey target, string incomingNote,
        NativePrivateMetadataDisposition disposition)
    { Target = target; IncomingNote = incomingNote; Disposition = disposition; }
    internal PostBookmarkKey Target { get; }
    // The selected incoming value is intentionally available for a future inert
    // conflict view/candidate store. The target value is never copied here.
    internal string IncomingNote { get; }
    internal NativePrivateMetadataDisposition Disposition { get; }
}

internal sealed class NativePrivateMetadataComparisonResult
{
    internal NativePrivateMetadataComparisonResult(NativePrivateMetadataSelection selection,
        NativePrivateMetadataProjection selectedIncoming,
        ImmutableArray<NativePrivateThreadFavoriteComparison> threadFavorites,
        ImmutableArray<NativePrivateMediaFavoriteComparison> mediaFavorites,
        ImmutableArray<NativePrivateBookmarkComparison> postBookmarksAndNotes)
    {
        Selection = selection;
        SelectedIncoming = selectedIncoming;
        ThreadFavorites = threadFavorites;
        MediaFavorites = mediaFavorites;
        PostBookmarksAndNotes = postBookmarksAndNotes;
    }

    internal NativePrivateMetadataSelection Selection { get; }
    internal NativePrivateMetadataProjection SelectedIncoming { get; }
    internal ImmutableArray<NativePrivateThreadFavoriteComparison> ThreadFavorites { get; }
    internal ImmutableArray<NativePrivateMediaFavoriteComparison> MediaFavorites { get; }
    internal ImmutableArray<NativePrivateBookmarkComparison> PostBookmarksAndNotes { get; }
}

/// <summary>
/// Compares an explicitly selected private projection with a passive local target
/// snapshot. This is deliberately not an import plan, consent record or store command.
/// </summary>
internal static class NativePrivateMetadataComparison
{
    internal static NativePrivateMetadataComparisonResult Analyze(
        NativePrivateMetadataProjection incoming,
        NativeTransferCatalog targetCatalog,
        NativePrivateMetadataProjection targetMetadata,
        NativePrivateMetadataSelection selection = default,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(incoming);
        ArgumentNullException.ThrowIfNull(targetCatalog);
        ArgumentNullException.ThrowIfNull(targetMetadata);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            // Validate before allocating indices. Error text from all inputs is replaced
            // below so private notes and source identities cannot become diagnostics.
            NativePrivateMetadataCodec.Validate(incoming, cancellationToken);
            NativePrivateMetadataCodec.Validate(targetMetadata, cancellationToken);
            NativePrivateMetadataProjection.ValidateMediaFavoriteEntries(incoming, cancellationToken);
            NativePrivateMetadataProjection.ValidateMediaFavoriteEntries(targetMetadata, cancellationToken);
            NativePrivateMetadataProjection selected = incoming.Select(selection, cancellationToken);
            NativePrivateMetadataProjection selectedTarget = targetMetadata.Select(selection, cancellationToken);
            ValidateTargetMetadata(targetCatalog, cancellationToken);

            HashSet<ResourceId> incomingThreads = incoming.Threads.ToHashSet();
            HashSet<ResourceId> targetThreads = targetMetadata.Threads.ToHashSet();
            if (!incomingThreads.SetEquals(targetThreads)) throw InvalidInput();

            HashSet<ResourceId> publicThreads = new();
            Dictionary<ResourceId, HashSet<string>> publicPosts = new();
            foreach (TransferThread thread in targetCatalog.Threads)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (thread is null || !incomingThreads.Contains(thread.Id) || !publicThreads.Add(thread.Id))
                    throw InvalidInput();
                HashSet<string> posts = new(StringComparer.Ordinal);
                foreach (TransferPost post in thread.Posts)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (post is null || !posts.Add(post.Metadata.PostId)) throw InvalidInput();
                }
                publicPosts.Add(thread.Id, posts);
            }

            HashSet<ResourceId> targetThreadFavorites = new();
            foreach (ResourceId favorite in selectedTarget.ThreadFavorites)
            {
                cancellationToken.ThrowIfCancellationRequested();
                targetThreadFavorites.Add(favorite);
            }
            ImmutableArray<NativePrivateThreadFavoriteComparison>.Builder threadRows = ImmutableArray.CreateBuilder<NativePrivateThreadFavoriteComparison>();
            if (selection.ThreadFavorites)
            {
                foreach (ResourceId favorite in selected.ThreadFavorites)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    NativePrivateMetadataDisposition disposition = publicThreads.Contains(favorite)
                        ? targetThreadFavorites.Contains(favorite)
                            ? NativePrivateMetadataDisposition.Identical
                            : NativePrivateMetadataDisposition.Add
                        : NativePrivateMetadataDisposition.MissingTarget;
                    threadRows.Add(new(favorite, disposition));
                }
            }

            HashSet<FavoriteAssetKey> targetMediaFavorites = new();
            foreach (FavoriteAssetKey favorite in selectedTarget.MediaFavorites)
            {
                cancellationToken.ThrowIfCancellationRequested();
                targetMediaFavorites.Add(favorite);
            }
            HashSet<FavoriteAssetKey> targetContainedMedia = selection.MediaFavorites
                ? ContainedMedia(targetCatalog, cancellationToken) : new();
            Dictionary<FavoriteAssetKey, FavoriteEntry> incomingMediaEntries = selected.MediaFavoriteEntries
                .ToDictionary(entry => entry.Key);
            ImmutableArray<NativePrivateMediaFavoriteComparison>.Builder mediaRows = ImmutableArray.CreateBuilder<NativePrivateMediaFavoriteComparison>();
            if (selection.MediaFavorites)
            {
                foreach (FavoriteAssetKey favorite in selected.MediaFavorites)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    NativePrivateMetadataDisposition disposition = targetContainedMedia.Contains(favorite)
                        ? targetMediaFavorites.Contains(favorite)
                            ? NativePrivateMetadataDisposition.Identical
                            : NativePrivateMetadataDisposition.Add
                        : NativePrivateMetadataDisposition.MissingTarget;
                    mediaRows.Add(new(favorite, disposition,
                        incomingMediaEntries.TryGetValue(favorite, out FavoriteEntry? entry) ? entry : null));
                }
            }

            Dictionary<PostBookmarkKey, string> targetBookmarks = new();
            foreach (PostBookmark bookmark in selectedTarget.PostBookmarksAndNotes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                targetBookmarks.Add(bookmark.Key, bookmark.Note);
            }
            ImmutableArray<NativePrivateBookmarkComparison>.Builder bookmarkRows = ImmutableArray.CreateBuilder<NativePrivateBookmarkComparison>();
            if (selection.PostBookmarksAndNotes)
            {
                foreach (PostBookmark bookmark in selected.PostBookmarksAndNotes)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    NativePrivateMetadataDisposition disposition;
                    if (!publicPosts.TryGetValue(bookmark.Key.Thread, out HashSet<string>? posts)
                        || !posts.Contains(bookmark.Key.Post))
                        disposition = NativePrivateMetadataDisposition.MissingTarget;
                    else if (!targetBookmarks.TryGetValue(bookmark.Key, out string? targetNote))
                        disposition = NativePrivateMetadataDisposition.Add;
                    else
                        disposition = string.Equals(targetNote, bookmark.Note, StringComparison.Ordinal)
                            ? NativePrivateMetadataDisposition.Identical
                            : NativePrivateMetadataDisposition.Conflict;
                    bookmarkRows.Add(new(bookmark.Key, bookmark.Note, disposition));
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            return new(selection, selected, threadRows.ToImmutable(), mediaRows.ToImmutable(), bookmarkRows.ToImmutable());
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) when (error is ArgumentException or InvalidDataException or InvalidOperationException
            or KeyNotFoundException or FormatException or OverflowException)
        {
            throw InvalidInput();
        }
    }

    private static void ValidateContainedDescriptorSyntax(NativeTransferCatalog catalog, CancellationToken token)
    {
        foreach (TransferThread thread in catalog.Threads)
        {
            token.ThrowIfCancellationRequested();
            foreach (TransferPost post in thread.Posts)
            {
                token.ThrowIfCancellationRequested();
                foreach (TransferAttachment attachment in post.Attachments)
                    ValidateContainedAttachment(attachment, token);
            }
            foreach (TransferRetainedBinding binding in thread.RetainedBindings)
                ValidateContainedAttachment(binding.Attachment, token);
        }

        static void ValidateContainedAttachment(TransferAttachment attachment, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (attachment.Status != TransferContentStatus.Contained) return;
            string? hash = attachment.PayloadSha256;
            if (hash is not { Length: 64 } || !hash.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f'))
                throw InvalidInput();
        }
    }

    // No fabricated payload measurements: validate public metadata through the
    // existing metadata-only profile, projecting contained claims to unavailable.
    // The original graph is bounded first and is never kept by the result.
    private static void ValidateTargetMetadata(NativeTransferCatalog catalog, CancellationToken token)
    {
        _ = NativeTransferCatalogSerializer.Serialize(catalog, token);
        ValidateContainedDescriptorSyntax(catalog, token);
        ImmutableArray<TransferThread>.Builder threads = ImmutableArray.CreateBuilder<TransferThread>();
        foreach (TransferThread thread in catalog.Threads)
        {
            token.ThrowIfCancellationRequested();
            ImmutableArray<TransferPost>.Builder posts = ImmutableArray.CreateBuilder<TransferPost>();
            foreach (TransferPost post in thread.Posts)
            {
                token.ThrowIfCancellationRequested();
                ImmutableArray<TransferAttachment>.Builder attachments = ImmutableArray.CreateBuilder<TransferAttachment>();
                foreach (TransferAttachment attachment in post.Attachments)
                {
                    token.ThrowIfCancellationRequested();
                    attachments.Add(MetadataOnly(attachment));
                }
                posts.Add(post with { Attachments = attachments.ToImmutable() });
            }
            ImmutableArray<TransferRetainedBinding>.Builder retained = ImmutableArray.CreateBuilder<TransferRetainedBinding>();
            foreach (TransferRetainedBinding binding in thread.RetainedBindings)
            {
                token.ThrowIfCancellationRequested();
                retained.Add(binding with { Attachment = MetadataOnly(binding.Attachment) });
            }
            threads.Add(thread with { Posts = posts.ToImmutable(), RetainedBindings = retained.ToImmutable() });
        }
        NativeTransferCatalog metadata = catalog with { Threads = threads.ToImmutable() };
        byte[] bytes = NativeTransferCatalogSerializer.Serialize(metadata, token);
        NativeTransferEnvelope envelope = new("", NativeTransferCatalogSerializer.ContentSchema(metadata, token), "",
            new(bytes.Length, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()), []);
        if (!NativeTransferCatalogValidator.Inspect(bytes, envelope, token).IsCatalogValid) throw InvalidInput();
        token.ThrowIfCancellationRequested();

        static TransferAttachment MetadataOnly(TransferAttachment attachment) => attachment with
        {
            Status = attachment.Status == TransferContentStatus.Contained
                ? TransferContentStatus.Unavailable : attachment.Status,
            PayloadSha256 = null,
            History = null
        };
    }

    private static HashSet<FavoriteAssetKey> ContainedMedia(NativeTransferCatalog catalog, CancellationToken token)
    {
        HashSet<FavoriteAssetKey> result = new();
        foreach (TransferThread thread in catalog.Threads)
        {
            token.ThrowIfCancellationRequested();
            foreach (TransferPost post in thread.Posts)
            {
                token.ThrowIfCancellationRequested();
                foreach (TransferAttachment attachment in post.Attachments)
                {
                    token.ThrowIfCancellationRequested();
                    Add(thread.Id, attachment, result);
                }
            }
            foreach (TransferRetainedBinding binding in thread.RetainedBindings)
            {
                token.ThrowIfCancellationRequested();
                Add(thread.Id, binding.Attachment, result);
            }
        }
        return result;

        static void Add(ResourceId thread, TransferAttachment attachment, HashSet<FavoriteAssetKey> values)
        {
            if (attachment is null || attachment.Role != TransferMediaRole.Original
                || attachment.Status != TransferContentStatus.Contained
                || attachment.Metadata?.MediaId is not string mediaId
                || attachment.PayloadSha256 is not string hash
                || hash.Length != 64 || !hash.All(c => c is >= 'a' and <= 'f' or >= '0' and <= '9')) return;
            values.Add(new FavoriteAssetKey(thread, mediaId, attachment.Metadata.Variant, hash));
        }
    }

    private static InvalidDataException InvalidInput() =>
        new("The private metadata comparison input is invalid.");
}
