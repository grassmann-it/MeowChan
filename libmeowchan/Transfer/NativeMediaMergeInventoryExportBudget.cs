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
/// Prospective complete-inventory re-export size, not physical storage admission or
/// byte verification. The caller owns target leases, verification and the SQL union.
/// No origin stream is opened, and the synthetic snapshot is never published.
/// </summary>
internal static class NativeMediaMergeInventoryExportBudget
{
    internal static NativeTransferPackageWriter.TransferPackageBudget Measure(
        NativeMediaMaterializationPlan materialization, ArchivedThreadView prospective,
        ImmutableArray<MediaAssetSnapshot> targetSnapshots, NativeMediaCandidateSelection safeSelection,
        Guid operationId, DateTimeOffset importedAtUtc, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(materialization);
        ArgumentNullException.ThrowIfNull(prospective);
        ArgumentNullException.ThrowIfNull(safeSelection);
        cancellationToken.ThrowIfCancellationRequested();
        if (targetSnapshots.IsDefault || prospective.Summary.Id != materialization.Resource)
            throw new InvalidDataException("Invalid prospective inventory export target.");
        materialization.ValidateSelection(safeSelection);
        foreach (var snapshot in targetSnapshots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (snapshot == null || snapshot.Status == MediaManifestStatus.Corrupt)
                throw new InvalidDataException("Invalid prospective target inventory.");
            // The shared projection filters by resource; the real single-thread exporter
            // rejects foreign inventory instead of silently exporting a partial root.
            foreach (var binding in snapshot.AttachmentBindings.Values)
            {
                cancellationToken.ThrowIfCancellationRequested();
                RequireResource(binding.Reference.Resource);
            }
            foreach (var evidence in snapshot.OriginalEvidence)
            {
                cancellationToken.ThrowIfCancellationRequested();
                RequireResource(evidence.Occurrence.Resource);
            }
            foreach (var derived in snapshot.DerivedRepresentations)
            {
                cancellationToken.ThrowIfCancellationRequested();
                RequireResource(derived.Parent.Resource);
            }
        }
        // CreateInventory preserves the exact nullable source tuple independently of
        // the measured payload tuple. Even a derived-only selection has parent evidence.
        NativeMediaCandidateInventory inventory = safeSelection.CreateInventory(operationId,
            materialization.Package.Envelope.Fingerprint, importedAtUtc);
        ImmutableArray<MediaAssetSnapshot> snapshots = safeSelection.IsEmpty ? targetSnapshots
            : targetSnapshots.Add(Snapshot(inventory, cancellationToken));
        var plan = NativeTransferMediaInventoryProjection.Create(
            NativeTransferArchiveProjection.From(prospective, cancellationToken), snapshots, cancellationToken);
        if (plan.Members.Sum(m => 1L + m.Attachment.DerivedRepresentations.Length) > 100_000)
            throw new InvalidDataException("The prospective export exceeds its member budget.");
        var represented = new HashSet<(MediaAssetSnapshot Manifest, string Asset)>();
        var payloads = new Dictionary<string, TransferBlobDescriptor>(StringComparer.Ordinal);
        var selected = new Dictionary<(string Post, int Position, string? Key), TransferAttachment>();
        foreach (var member in plan.Members)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TransferAttachment attachment = member.Attachment;
            foreach (var origin in member.Originals)
            {
                represented.Add((origin.Manifest, origin.Original!.AssetId));
                Add(origin);
            }
            if (!member.Originals.IsEmpty)
            {
                var origin = member.Originals[0];
                var binding = attachment.Metadata.AttachmentRevisionKey is { } key
                    ? origin.Manifest.FindAttachmentBinding(key) : null;
                attachment = attachment with { Status = TransferContentStatus.Contained,
                    PayloadSha256 = origin.Sha256,
                    History = binding == null ? null : NativeTransferArchiveExporter.HistoricalObservation(binding) };
            }
            attachment = attachment with { DerivedRepresentations = attachment.DerivedRepresentations.Select(d =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var origins = member.Derived[d.Kind];
                foreach (var origin in origins) Add(origin);
                return origins.IsEmpty ? d : d with { Status = TransferContentStatus.Contained, PayloadSha256 = origins[0].Sha256 };
            }).ToImmutableArray() };
            selected.Add((member.PostId, attachment.Metadata.Position, attachment.Metadata.AttachmentRevisionKey), attachment);
        }
        foreach (var snapshot in snapshots)
        for (int offset = 0; offset < snapshot.Count; offset += 500)
        foreach (var asset in snapshot.GetPage(offset, 500))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!represented.Contains((snapshot, asset.AssetId)))
                throw new InvalidDataException("A prospective asset has no portable inventory occurrence.");
        }
        TransferAttachment Chosen(string post, TransferAttachment a)
            => selected[(post, a.Metadata.Position, a.Metadata.AttachmentRevisionKey)];
        TransferThread complete = plan.Thread with
        {
            Posts = plan.Thread.Posts.Select(p => p with
                { Attachments = p.Attachments.Select(a => Chosen(p.Metadata.PostId, a)).ToImmutableArray() }).ToImmutableArray(),
            RetainedBindings = plan.Thread.RetainedBindings.Select(r => new TransferRetainedBinding(r.PostId,
                Chosen(r.PostId, r.Attachment))).ToImmutableArray()
        };
        return NativeTransferPackageWriter.MeasureBudget(new(importedAtUtc, [complete]),
            payloads.Values.OrderBy(p => p.Sha256, StringComparer.Ordinal).ToArray(), operationId, cancellationToken);

        void RequireResource(ResourceId resource)
        {
            if (resource != materialization.Resource)
                throw new InvalidDataException("A target inventory contains an unrepresented resource.");
        }

        void Add(NativeTransferInventoryOrigin origin)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var descriptor = new TransferBlobDescriptor(origin.Length, origin.Sha256);
            if (payloads.TryGetValue(descriptor.Sha256, out var previous) && previous.Length != descriptor.Length)
                throw new InvalidDataException("Conflicting prospective export payload lengths.");
            payloads[descriptor.Sha256] = descriptor;
        }
    }

    private static MediaAssetSnapshot Snapshot(NativeMediaCandidateInventory inventory, CancellationToken token)
    {
        // Labels only: never resolved, opened, registered, or returned to a caller.
        const string root = "prospective-safe-inventory";
        var assets = inventory.Bindings.Select(b =>
        {
            token.ThrowIfCancellationRequested();
            return new MediaAssetReference(b.AssetId, b.Provider.MediaId,
                b.Provider.Variant, b.Reference.RevisionKey[7..] + ".original.bin",
                root + "/" + b.Actual.Sha256 + ".original.bin", b.Actual.Length, b.Actual.Sha256,
                b.Provider.Url, b.Provider.OriginalFileName, true) { Md5 = b.Actual.Md5, Sha1 = b.Actual.Sha1 };
        });
        return new(root, MediaManifestStatus.Available, 6, inventory.Sha256, assets,
            attachmentBindings: inventory.Bindings.ToDictionary(b => b.Reference.RevisionKey, StringComparer.Ordinal),
            originalEvidence: inventory.OriginalEvidence, derivedRepresentations: inventory.DerivedRepresentations);
    }
}
