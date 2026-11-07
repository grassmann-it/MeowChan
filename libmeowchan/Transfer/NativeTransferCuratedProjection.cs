using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using MeowChan.Archive;
using MeowChan.Favorites;

namespace MeowChan.Transfer;

/// <summary>
/// Pure public descriptor slice, not a writer, byte-verification receipt or admission
/// token. No IO, callbacks, stores, private projections, decoders or network access.
/// Caller must still bind/recheck the source snapshot and verify payload bytes.
/// </summary>
internal static class NativeTransferCuratedProjection
{
    internal static NativeTransferCatalog Project(NativeTransferCatalog source, CuratedExportSelection selection)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(selection);
        // This descriptor-only consumer accepts the existing Files selection as
        // its explicit original-occurrence contract. HTML and requested previews
        // require their own producer; never silently report either as fulfilled.
        if (selection.Mode != CuratedExportMode.Files || selection.PreviewsRequested) throw Invalid();
        ValidateDescriptors(source);
        var chosen = new Dictionary<(ResourceId Resource, string Post, int Position, string Revision), FavoriteAssetKey>();
        foreach (CuratedExportOriginal original in selection.Originals)
        {
            if (original.Status != CuratedExportStatus.OriginalIncluded) continue;
            if (original.Asset is null || original.Occurrences.IsEmpty) throw Invalid();
            foreach (CuratedExportOccurrence occurrence in original.Occurrences)
            {
                if (occurrence.IsLegacy || occurrence.Reference is not { } reference
                    || occurrence.AssetId != original.Asset.AssetId
                    || occurrence.Manifest.Directory != original.Asset.Manifest.Directory
                    || occurrence.Manifest.Generation != original.Asset.Manifest.Generation
                    || reference.Resource != original.Entry.Key.Thread) throw Invalid();
                var identity = (reference.Resource, reference.PostId, reference.Position, reference.RevisionKey);
                if (!chosen.TryAdd(identity, original.Entry.Key)) throw Invalid();
            }
        }

        var scope = selection.Threads.ToHashSet();
        var visited = new HashSet<ResourceId>();
        var found = new HashSet<(ResourceId, string, int, string)>();
        var threads = ImmutableArray.CreateBuilder<TransferThread>();
        foreach (TransferThread thread in source.Threads)
        {
            if (!scope.Contains(thread.Id)) continue;
            visited.Add(thread.Id);
            var posts = ImmutableArray.CreateBuilder<TransferPost>();
            foreach (TransferPost post in thread.Posts)
            {
                var attachments = post.Attachments.Select(a => ProjectAttachment(thread.Id, post.Metadata.PostId, a)).ToImmutableArray();
                // Preserve the captured observation verbatim: curation changes only
                // binary status, not its presence, order, revision or provider metadata.
                posts.Add(post with { Attachments = attachments });
            }
            threads.Add(thread with
            {
                Posts = posts.ToImmutable(),
                RetainedBindings = thread.RetainedBindings.Select(r => r with
                { Attachment = ProjectAttachment(thread.Id, r.PostId, r.Attachment) }).ToImmutableArray()
            });
        }
        if (!scope.SetEquals(visited) || found.Count != chosen.Count) throw Invalid();
        NativeTransferCatalog result = source with { Threads = threads.ToImmutable() };
        ValidateDescriptors(result);
        return result;

        TransferAttachment ProjectAttachment(ResourceId resource, string post, TransferAttachment attachment)
        {
            // This original-only selection does not select derived bytes. Preserve
            // their exact source linkage and kind, but deliberately omit payloads;
            // unavailable remains unavailable rather than claiming successful output.
            attachment = attachment with
            {
                DerivedRepresentations = attachment.DerivedRepresentations.Select(d =>
                    d.Status == TransferContentStatus.Contained
                        ? d with { Status = TransferContentStatus.Omitted, PayloadSha256 = null } : d).ToImmutableArray()
            };
            var metadata = attachment.Metadata;
            var identity = (resource, post, metadata.Position, metadata.AttachmentRevisionKey ?? "");
            if (!chosen.TryGetValue(identity, out FavoriteAssetKey? key))
                // No preview contract in this consumer: never infer a safe small
                // preview from an opaque variant or a shared payload hash.
                return attachment with { Status = TransferContentStatus.Omitted, PayloadSha256 = null, History = null };
            if (!found.Add(identity) || attachment.Role != TransferMediaRole.Original
                || metadata.MediaId != key.MediaIdentifier || metadata.Variant != key.Variant
                || attachment.Status == TransferContentStatus.Contained && attachment.PayloadSha256 != key.Sha256
                || attachment.OriginalByteVersion is { } version && version.Sha256 != key.Sha256)
                throw Invalid();
            return attachment;
        }
    }

    // Same descriptor-only validation boundary as NativePrivateMetadataComparison:
    // serialization checks the actual DTO shape; validator checks public metadata,
    // identities, revisions and presence without inventing measured payload bytes.
    // This deliberately does not attest the original contained bytes/history.
    private static void ValidateDescriptors(NativeTransferCatalog catalog)
    {
        _ = NativeTransferCatalogSerializer.Serialize(catalog);
        TransferAttachment Descriptor(TransferAttachment a) => a with
        {
            Status = a.Status == TransferContentStatus.Contained ? TransferContentStatus.Unavailable : a.Status,
            PayloadSha256 = a.Status == TransferContentStatus.Contained ? null : a.PayloadSha256,
            History = a.Status == TransferContentStatus.Contained ? null : a.History,
            DerivedRepresentations = a.DerivedRepresentations.Select(d =>
                d.Status == TransferContentStatus.Contained
                    ? d with { Status = TransferContentStatus.Unavailable, PayloadSha256 = null } : d).ToImmutableArray()
        };
        NativeTransferCatalog descriptors = catalog with
        {
            Threads = catalog.Threads.Select(t => t with
            {
                Posts = t.Posts.Select(p => p with
                { Attachments = p.Attachments.Select(Descriptor).ToImmutableArray() }).ToImmutableArray(),
                RetainedBindings = t.RetainedBindings.Select(r => r with
                { Attachment = Descriptor(r.Attachment) }).ToImmutableArray()
            }).ToImmutableArray()
        };
        byte[] bytes = NativeTransferCatalogSerializer.Serialize(descriptors);
        var envelope = new NativeTransferEnvelope("curated-descriptors", NativeTransferCatalogSerializer.ContentSchema(descriptors),
            "", new(bytes.Length, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()), []);
        if (!NativeTransferCatalogValidator.Inspect(bytes, envelope).IsCatalogValid) throw Invalid();
    }

    private static InvalidDataException Invalid() => new("Invalid curated native descriptor projection.");
}
