using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeowChan.Archive;
using MeowChan.Storage;

namespace MeowChan.Transfer;

internal static partial class NativeTransferArchiveExporter
{
    internal static Task<TransferArchiveFreezeResult> FreezeAsync(ArchiveReader reader, ResourceId id,
        DateTimeOffset exportedAtUtc, Guid packageId, NativeTransferArchiveMediaOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(id);
        return FreezeAsync(reader, new NativeTransferArchiveSelection([id]), exportedAtUtc, packageId, options, cancellationToken);
    }

    internal static async Task<TransferArchiveFreezeResult> FreezeAsync(ArchiveReader reader,
        NativeTransferArchiveSelection selection, DateTimeOffset exportedAtUtc, Guid packageId,
        NativeTransferArchiveMediaOptions options, CancellationToken cancellationToken = default,
        NativeTransferArchiveMetadataLimits? metadataLimits = null)
    {
        ArgumentNullException.ThrowIfNull(reader); ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(options); cancellationToken.ThrowIfCancellationRequested();
        if (options.IncludeDerived && !options.ReadInventory) return FreezeFail(TransferArchiveError.InvalidArchive);
        if (!options.ReadInventory)
            return await FreezeAsync(reader, selection, exportedAtUtc, packageId, options.IncludeOriginals,
                cancellationToken, metadataLimits).ConfigureAwait(false);
        try
        {
            if (packageId == Guid.Empty) return FreezeFail(TransferArchiveError.InvalidArchive);
            ArchiveExportMetadataFile.RejectLinks(reader.DatabasePath);
            metadataLimits ??= NativeTransferArchiveMetadataLimits.Default;
            if (!await PreflightMetadataAsync(reader, selection.Threads, metadataLimits, cancellationToken).ConfigureAwait(false))
                return FreezeFail(TransferArchiveError.SelectionLimit);
            MetadataBudget metadataBudget = new(metadataLimits, exportedAtUtc, cancellationToken);
            var views = ImmutableArray.CreateBuilder<ArchivedThreadView>();
            var metadataThreads = ImmutableArray.CreateBuilder<TransferThread>();
            long posts = 0, occurrences = 0;
            foreach (ResourceId id in selection.Threads)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ArchivedThreadView? view = await reader.ReadNativeTransferAsync(id, cancellationToken).ConfigureAwait(false);
                if (view == null) return FreezeFail(TransferArchiveError.MissingThread);
                if (!metadataBudget.TryReserveView(view, cancellationToken)) return FreezeFail(TransferArchiveError.SelectionLimit);
                TransferThread thread = NativeTransferArchiveProjection.From(view, cancellationToken);
                if (!metadataBudget.TryReserveCatalog(thread, cancellationToken)) return FreezeFail(TransferArchiveError.SelectionLimit);
                posts = checked(posts + thread.Posts.Length);
                occurrences = checked(occurrences + thread.Posts.Sum(p => (long)p.Attachments.Length));
                if (posts > 100_000 || occurrences > 100_000) return FreezeFail(TransferArchiveError.SelectionLimit);
                views.Add(view); metadataThreads.Add(thread);
            }
            NativeTransferCatalog metadataCatalog = new(exportedAtUtc, metadataThreads.ToImmutable());
            byte[] metadata = NativeTransferCatalogSerializer.Serialize(metadataCatalog, cancellationToken);
            if (!ValidateDescriptors(metadataCatalog)) return FreezeFail(TransferArchiveError.InvalidCatalog);
            string[] directories = views.SelectMany(v => v.MediaDirectories).Distinct(StringComparer.Ordinal).ToArray();
            if (directories.Length > MaxDirectories) return FreezeFail(TransferArchiveError.SelectionLimit);
            var manifests = ImmutableArray.CreateBuilder<MediaAssetSnapshot>();
            Dictionary<string, MediaAssetSnapshot> byRoot = new(StringComparer.Ordinal);
            var registeredRoots = views.ToDictionary(v => v.Summary.Id,
                v => v.MediaDirectories.ToHashSet(StringComparer.Ordinal));
            long inventoryVisits = 0;
            foreach (string directory in directories)
            {
                cancellationToken.ThrowIfCancellationRequested();
                MediaAssetSnapshot manifest = await ReadManifestAsync(directory, cancellationToken).ConfigureAwait(false);
                if (manifest.Status == MediaManifestStatus.Corrupt) return FreezeFail(TransferArchiveError.InvalidMedia);
                inventoryVisits = checked(inventoryVisits + InventoryCount(manifest));
                if (inventoryVisits > 100_000) return FreezeFail(TransferArchiveError.SelectionLimit);
                // Complete registered roots only, not inactive conflict/candidate directories.
                // A root shared with an unselected resource cannot be silently truncated.
                bool HasRegisteredOwner(ResourceId resource) => registeredRoots.TryGetValue(resource, out var roots)
                    && roots.Contains(directory);
                if (manifest.AttachmentBindings.Values.Any(b => !HasRegisteredOwner(b.Reference.Resource))
                    || manifest.OriginalEvidence.Any(e => !HasRegisteredOwner(e.Occurrence.Resource))
                    || manifest.DerivedRepresentations.Any(d => !HasRegisteredOwner(d.Parent.Resource)))
                    return FreezeFail(TransferArchiveError.UnrepresentedAssets);
                manifests.Add(manifest); byRoot.Add(directory, manifest);
            }
            var plans = new List<NativeTransferMediaInventoryProjection.Plan>();
            long projectionVisits = 0;
            occurrences = 0;
            for (int i = 0; i < views.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ImmutableArray<MediaAssetSnapshot> threadRoots = views[i].MediaDirectories.Distinct(StringComparer.Ordinal)
                    .Select(root => byRoot[root]).ToImmutableArray();
                projectionVisits = checked(projectionVisits + threadRoots.Sum(InventoryCount));
                if (projectionVisits > 100_000) return FreezeFail(TransferArchiveError.SelectionLimit);
                NativeTransferMediaInventoryProjection.Plan plan = NativeTransferMediaInventoryProjection.Create(
                    metadataThreads[i], threadRoots, cancellationToken);
                occurrences = checked(occurrences + plan.Members.Sum(m => 1L + m.Attachment.DerivedRepresentations.Length));
                if (occurrences > 100_000) return FreezeFail(TransferArchiveError.SelectionLimit);
                plans.Add(plan);
            }
            // This includes current AND historical evidence, even nonselected byte roles.
            // No manufactured measurement dictionary or credential-stripping revision change.
            NativeTransferCatalog preflight = new(exportedAtUtc, plans.Select(p => p.Thread).ToImmutableArray());
            if (!ValidateDescriptors(preflight)) return FreezeFail(TransferArchiveError.InvalidCatalog);
            HashSet<(string Root, string Asset)> represented = new();
            foreach (var plan in plans)
            foreach (var member in plan.Members)
            foreach (var origin in member.Originals)
                represented.Add((origin.Manifest.Directory, origin.Original!.AssetId));
            foreach (MediaAssetSnapshot manifest in manifests)
            for (int offset = 0; offset < manifest.Count; offset += 500)
            {
                cancellationToken.ThrowIfCancellationRequested();
                foreach (MediaAssetReference asset in manifest.GetPage(offset, 500))
                    if (!represented.Contains((manifest.Directory, asset.AssetId))) return FreezeFail(TransferArchiveError.UnrepresentedAssets);
            }
            Dictionary<string, (TransferBlobDescriptor Descriptor, List<NativeTransferInventoryOrigin> Origins)> payloads = new(StringComparer.Ordinal);
            HashSet<NativeTransferInventoryOrigin> selectedOrigins = new();
            TransferArchiveVerificationCache verification = new();
            long payloadBytes = 0;
            var completed = ImmutableArray.CreateBuilder<TransferThread>();
            foreach (var plan in plans)
            {
                var selected = new Dictionary<(string Post, int Position, string? Key), TransferAttachment>();
                foreach (var member in plan.Members)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    TransferAttachment attachment = member.Attachment;
                    if (!options.IncludeOriginals) attachment = attachment with { Status = TransferContentStatus.Omitted };
                    else if (!member.Originals.IsEmpty)
                    {
                        if (await SelectOriginsAsync(member.Originals).ConfigureAwait(false))
                        {
                            NativeTransferInventoryOrigin origin = member.Originals[0];
                            MediaAttachmentBinding? binding = attachment.Metadata.AttachmentRevisionKey is { } key
                                ? origin.Manifest.FindAttachmentBinding(key) : null;
                            attachment = attachment with { Status = TransferContentStatus.Contained, PayloadSha256 = origin.Sha256,
                                History = binding == null ? null : HistoricalObservation(binding) };
                        }
                        else attachment = attachment with { Status = TransferContentStatus.Unavailable, PayloadSha256 = null, History = null };
                    }
                    var derived = ImmutableArray.CreateBuilder<TransferDerivedRepresentation>();
                    foreach (TransferDerivedRepresentation descriptor in attachment.DerivedRepresentations)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        TransferDerivedRepresentation chosen = descriptor;
                        var origins = member.Derived[descriptor.Kind];
                        if (!options.IncludeDerived) chosen = descriptor with { Status = TransferContentStatus.Omitted, PayloadSha256 = null };
                        else if (!origins.IsEmpty)
                            chosen = await SelectOriginsAsync(origins).ConfigureAwait(false)
                                ? descriptor with { Status = TransferContentStatus.Contained, PayloadSha256 = origins[0].Sha256 }
                                : descriptor with { Status = TransferContentStatus.Unavailable, PayloadSha256 = null };
                        derived.Add(chosen);
                    }
                    attachment = attachment with { DerivedRepresentations = derived.ToImmutable() };
                    selected.Add((member.PostId, attachment.Metadata.Position, attachment.Metadata.AttachmentRevisionKey), attachment);
                }
                completed.Add(plan.Thread with
                {
                    Posts = plan.Thread.Posts.Select(post => post with { Attachments = post.Attachments.Select(a =>
                        selected[(post.Metadata.PostId, a.Metadata.Position, a.Metadata.AttachmentRevisionKey)]).ToImmutableArray() }).ToImmutableArray(),
                    RetainedBindings = plan.Thread.RetainedBindings.Select(retained => new TransferRetainedBinding(retained.PostId,
                        selected[(retained.PostId, retained.Attachment.Metadata.Position, retained.Attachment.Metadata.AttachmentRevisionKey)])).ToImmutableArray()
                });
            }
            NativeTransferCatalog content = new(exportedAtUtc, completed.ToImmutable());
            if (!NativeTransferPackageWriter.MeasureBudget(content, payloads.Values.Select(v => v.Descriptor).ToArray(),
                    packageId, cancellationToken).WithinContainerCap) return FreezeFail(TransferArchiveError.SelectionLimit);
            var sources = payloads.OrderBy(p => p.Key, StringComparer.Ordinal).Select(pair =>
            {
                // Copy closures to immutable arrays before yielding the owner-issued snapshot.
                ImmutableArray<NativeTransferInventoryOrigin> origins = pair.Value.Origins.ToImmutableArray();
                return new TransferPayloadSource(pair.Value.Descriptor, token => OpenInventoryPayloadAsync(origins, token));
            }).ToImmutableArray();
            NativeTransferArchiveSnapshot snapshot = new(reader, views[0], metadata, content, manifests.ToImmutable(), sources, packageId)
            { Views = views.ToImmutable(), MetadataLimits = metadataLimits, InventoryOrigins = selectedOrigins.ToImmutableArray() };
            if (!await IsCurrentAsync(snapshot, cancellationToken).ConfigureAwait(false)) return FreezeFail(TransferArchiveError.StaleSnapshot);
            return new(snapshot, TransferArchiveError.None);

            bool ValidateDescriptors(NativeTransferCatalog catalog)
            {
                byte[] bytes = NativeTransferCatalogSerializer.Serialize(catalog, cancellationToken);
                if (bytes.Length > metadataLimits.CatalogBytes) return false;
                NativeTransferEnvelope envelope = new(packageId.ToString("D"), NativeTransferCatalogSerializer.ContentSchema(catalog), "",
                    new(bytes.Length, Hash(bytes)), []);
                return NativeTransferCatalogValidator.Inspect(bytes, envelope, cancellationToken).IsCatalogValid;
            }
            async Task<bool> SelectOriginsAsync(ImmutableArray<NativeTransferInventoryOrigin> origins)
            {
                bool available = true;
                foreach (NativeTransferInventoryOrigin origin in origins)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (origin.Length > MaxPayload) throw new FreezeException(TransferArchiveError.SelectionLimit);
                    TransferAssetVerification result;
                    try { result = await VerifyOriginAsync(verification, origin, cancellationToken).ConfigureAwait(false); }
                    catch (InvalidDataException) { throw new FreezeException(TransferArchiveError.InvalidMedia); }
                    if (result == TransferAssetVerification.Limit) throw new FreezeException(TransferArchiveError.SelectionLimit);
                    if (result != TransferAssetVerification.Available) available = false;
                }
                if (!available) return false; // Never substitute a hash twin for a missing exact source.
                foreach (NativeTransferInventoryOrigin origin in origins)
                {
                    if (!payloads.TryGetValue(origin.Sha256, out var existing))
                    {
                        payloadBytes = checked(payloadBytes + origin.Length);
                        if (payloads.Count >= MaxPayloads || payloadBytes > MaxTotal) throw new FreezeException(TransferArchiveError.SelectionLimit);
                        existing = (new(origin.Length, origin.Sha256), []); payloads.Add(origin.Sha256, existing);
                    }
                    if (existing.Descriptor.Length != origin.Length) throw new FreezeException(TransferArchiveError.InvalidMedia);
                    if (selectedOrigins.Add(origin)) existing.Origins.Add(origin);
                }
                return true;
            }
        }
        catch (FreezeException error) { return FreezeFail(error.Error); }
        catch (Exception error) when (error is InvalidDataException or FormatException) { return FreezeFail(TransferArchiveError.InvalidArchive); }
        catch (Exception error) when (Environmental(error)) { return FreezeFail(TransferArchiveError.IoFailure); }
    }

    private static long InventoryCount(MediaAssetSnapshot manifest) => (long)manifest.Count + manifest.AttachmentBindings.Count
        + manifest.OriginalEvidence.Length + manifest.DerivedRepresentations.Length;
    private static Task<TransferAssetVerification> VerifyOriginAsync(TransferArchiveVerificationCache verification,
        NativeTransferInventoryOrigin origin, CancellationToken token) => origin.Original is { } asset
        ? verification.VerifyAsync(origin.Manifest, asset, token)
        : verification.VerifyDerivedAsync(origin.Manifest, origin.Derived!, token);

    private static async Task<bool> VerifyInventoryOriginsAsync(ImmutableArray<NativeTransferInventoryOrigin> origins, CancellationToken token)
    {
        if (origins.IsEmpty) return true;
        TransferArchiveVerificationCache verification = new();
        foreach (var origin in origins)
            if (await VerifyOriginAsync(verification, origin, token).ConfigureAwait(false) != TransferAssetVerification.Available) return false;
        return true;
    }

    private static async Task<Stream> OpenInventoryPayloadAsync(ImmutableArray<NativeTransferInventoryOrigin> origins, CancellationToken token)
    {
        Stream? selected = null;
        try
        {
            foreach (NativeTransferInventoryOrigin origin in origins)
            {
                Stream opened = await origin.OpenAsync(token).ConfigureAwait(false);
                if (selected == null) selected = opened;
                else await opened.DisposeAsync().ConfigureAwait(false);
            }
            token.ThrowIfCancellationRequested();
            return selected ?? throw new InvalidDataException("A selected inventory payload has no physical origin.");
        }
        catch { if (selected != null) await selected.DisposeAsync().ConfigureAwait(false); throw; }
    }
}
