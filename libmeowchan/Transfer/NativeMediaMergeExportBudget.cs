using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading;
using MeowChan.Archive;
using MeowChan.Storage;

namespace MeowChan.Transfer;

/// <summary>
/// Builds the same current/retained wire representation that a later native export would
/// observe after a merge, without opening payload streams. The merge admission must account for
/// retained bindings as well as the new current occurrences; a current-only catalog is not a
/// sufficient container-budget proof.
/// </summary>
internal static class NativeMediaMergeExportBudget
{
    internal static NativeTransferPackageWriter.TransferPackageBudget Measure(
        NativeTransferPackage package, IReadOnlyList<NativeMediaImportAttachment> inputs,
        ArchivedThreadView target,
        IReadOnlyList<MediaAssetSnapshot> targetSnapshots, ISet<string> safeKeys, Guid packageId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(targetSnapshots);
        ArgumentNullException.ThrowIfNull(safeKeys);
        cancellationToken.ThrowIfCancellationRequested();

        TransferThread projected = NativeTransferArchiveProjection.From(target, cancellationToken);
        // Current and retained observations can share a slot, but never a revision key.
        Dictionary<string, NativeMediaImportAttachment> incoming =
            inputs.ToDictionary(input => input.RevisionKey, StringComparer.Ordinal);
        HashSet<string> currentKeys = projected.Posts.SelectMany(post => post.Attachments)
            .Select(attachment => attachment.Metadata.AttachmentRevisionKey)
            .OfType<string>().ToHashSet(StringComparer.Ordinal);
        Dictionary<string, TransferBlobDescriptor> payloads = new(StringComparer.Ordinal);
        HashSet<(string Directory, string AssetId)> represented = new();
        // The eventual exporter scans the added safe assets too, even though the old
        // legacy associations below deliberately resolve against the unchanged target roots.
        long legacyWork = 0, targetAssets = targetSnapshots.Sum(snapshot => (long)snapshot.Count) + safeKeys.Count;
        ImmutableArray<TransferPost>.Builder posts = ImmutableArray.CreateBuilder<TransferPost>(projected.Posts.Length);

        foreach (TransferPost post in projected.Posts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ImmutableArray<TransferAttachment>.Builder attachments = ImmutableArray.CreateBuilder<TransferAttachment>(post.Attachments.Length);
            foreach (TransferAttachment attachment in post.Attachments)
            {
                cancellationToken.ThrowIfCancellationRequested();
                TransferAttachment selected = attachment;
                string? key = attachment.Metadata.AttachmentRevisionKey;
                if (key != null && safeKeys.Contains(key))
                {
                    if (!incoming.TryGetValue(key, out NativeMediaImportAttachment? input)
                        || input.PostId != post.Metadata.PostId || input.Incoming.Metadata.Position != attachment.Metadata.Position || input.Incoming.PayloadSha256 is not { } payload
                        || !package.Envelope.MeasuredPayloads.TryGetValue(payload, out TransferMeasuredBlob? measured))
                        throw new InvalidDataException("A safe merge occurrence has no measured payload.");
                    selected = attachment with
                    {
                        Status = TransferContentStatus.Contained,
                        PayloadSha256 = measured.Sha256,
                        History = input.Incoming.History
                    };
                    AddPayload(payloads, new(measured.Length, measured.Sha256), cancellationToken);
                }
                else if (attachment.Metadata.MediaId != null)
                {
                    if (attachment.Metadata.AttachmentRevisionKey == null
                        && (legacyWork = checked(legacyWork + targetAssets)) > 1_000_000)
                        throw new InvalidDataException("The native export legacy-resolution budget was exceeded.");
                    ResolvedMediaAttachment? resolved = MediaAttachmentResolver.Resolve(targetSnapshots,
                        target.Summary.Id, post.Metadata.PostId, attachment.Metadata);
                    if (resolved != null)
                    {
                        represented.Add((resolved.Snapshot.Directory, resolved.Asset.AssetId));
                        selected = attachment with
                        {
                            Status = TransferContentStatus.Contained,
                            PayloadSha256 = resolved.Asset.Sha256,
                            History = resolved.Binding == null ? null : NativeTransferArchiveExporter.HistoricalObservation(resolved.Binding)
                        };
                        AddPayload(payloads, new(resolved.Asset.Length, resolved.Asset.Sha256), cancellationToken);
                    }
                }
                attachments.Add(selected);
            }
            posts.Add(post with { Attachments = attachments.ToImmutable() });
        }

        HashSet<string> retainedKeys = new(StringComparer.Ordinal);
        ImmutableArray<TransferRetainedBinding>.Builder retained = ImmutableArray.CreateBuilder<TransferRetainedBinding>();
        foreach (MediaAssetSnapshot snapshot in targetSnapshots.OrderBy(value => value.Directory, StringComparer.Ordinal))
        foreach (MediaAttachmentBinding binding in snapshot.AttachmentBindings.Values.OrderBy(value => value.Reference.RevisionKey, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (binding.Reference.Resource != target.Summary.Id) continue;
            if (binding.Provider.Variant != "original")
                throw new InvalidDataException("A merge binding has no native export role.");
            CapturedAttachment metadata = NativeTransferArchiveExporter.RetainedMetadata(binding);
            ResolvedMediaAttachment? resolved = MediaAttachmentResolver.Resolve(targetSnapshots,
                target.Summary.Id, binding.Reference.PostId, metadata);
            if (resolved == null || resolved.Binding != binding)
                throw new InvalidDataException("A retained merge binding cannot be represented by native export.");
            if (currentKeys.Contains(binding.Reference.RevisionKey) || !retainedKeys.Add(binding.Reference.RevisionKey)) continue;
            represented.Add((resolved.Snapshot.Directory, resolved.Asset.AssetId));
            TransferAttachment selected = new(metadata, TransferMediaRole.Original,
                TransferContentStatus.Contained, resolved.Asset.Sha256, NativeTransferArchiveExporter.HistoricalObservation(binding));
            retained.Add(new(binding.Reference.PostId, selected));
            AddPayload(payloads, new(resolved.Asset.Length, resolved.Asset.Sha256), cancellationToken);
        }

        foreach (NativeMediaImportAttachment input in inputs.OrderBy(value => value.RevisionKey, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!safeKeys.Contains(input.RevisionKey) || currentKeys.Contains(input.RevisionKey)) continue;
            if (!retainedKeys.Add(input.RevisionKey) || input.Incoming.PayloadSha256 is not { } payload
                || !package.Envelope.MeasuredPayloads.TryGetValue(payload, out TransferMeasuredBlob? measured))
                throw new InvalidDataException("A safe retained merge occurrence has no unique measured payload.");
            // Match RetainedMetadata(binding), not caller raw current/path fields. New passive
            // bindings retain only incoming historical claims; local import time is NOT history.
            CapturedAttachment metadata = input.Incoming.Metadata with
                { PlannedPath = null, AttachmentRevisionKey = input.RevisionKey };
            retained.Add(new(input.PostId, new(metadata, TransferMediaRole.Original,
                TransferContentStatus.Contained, measured.Sha256, input.Incoming.History)));
            AddPayload(payloads, new(measured.Length, measured.Sha256), cancellationToken);
        }

        foreach (MediaAssetSnapshot snapshot in targetSnapshots)
            for (int offset = 0; offset < snapshot.Count; offset += 500)
            foreach (MediaAssetReference asset in snapshot.GetPage(offset, 500))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!represented.Contains((snapshot.Directory, asset.AssetId)))
                    throw new InvalidDataException("A target asset cannot be represented by native export.");
            }

        NativeTransferCatalog catalog = new(package.Catalog.ExportedAtUtc,
            [projected with { Posts = posts.ToImmutable(), RetainedBindings = retained
                .OrderBy(value => value.Attachment.Metadata.AttachmentRevisionKey, StringComparer.Ordinal).ToImmutableArray() }]);
        return NativeTransferPackageWriter.MeasureBudget(catalog, payloads.Values.ToArray(), packageId, cancellationToken);
    }

    private static void AddPayload(Dictionary<string, TransferBlobDescriptor> payloads,
        TransferBlobDescriptor descriptor, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (payloads.TryGetValue(descriptor.Sha256, out TransferBlobDescriptor? existing))
        {
            if (existing.Length != descriptor.Length)
                throw new InvalidDataException("A native export payload has conflicting lengths.");
            return;
        }
        payloads.Add(descriptor.Sha256, descriptor);
    }
}

/// <summary>Physical target verification bytes are independent of transfer-payload SHA dedup.</summary>
internal static class NativeMediaMergePhysicalBudget
{
    private const long GiB = 1024L * 1024 * 1024;

    internal static bool WithinLimit(long existingPhysicalBytes, IEnumerable<TransferBlobDescriptor> newRootPayloads,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(newRootPayloads);
        cancellationToken.ThrowIfCancellationRequested();
        if (existingPhysicalBytes < 0 || existingPhysicalBytes > 2 * GiB) return false;
        Dictionary<string, long> payloads = new(StringComparer.Ordinal);
        long total = existingPhysicalBytes;
        foreach (TransferBlobDescriptor payload in newRootPayloads)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (payload.Length < 0 || payload.Length > GiB) return false;
            if (!MediaIntegrityValidation.IsSha256(payload.Sha256))
                throw new InvalidDataException("Invalid physical merge payload budget.");
            if (payloads.TryGetValue(payload.Sha256, out long length))
            {
                if (length != payload.Length) throw new InvalidDataException("Conflicting physical merge payload lengths.");
                continue;
            }
            payloads.Add(payload.Sha256, payload.Length);
            if (payload.Length > 2 * GiB - total) return false;
            total += payload.Length;
        }
        return true;
    }
}
