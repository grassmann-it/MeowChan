using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using MeowChan.Archive;
using MeowChan.Storage;

namespace MeowChan.Transfer;

internal enum TransferArchiveError
{
    None, MissingThread, InvalidArchive, InvalidCatalog, InvalidMedia, SelectionLimit,
    UnrepresentedAssets, StaleSnapshot, WriteFailure, IoFailure
}
internal sealed record TransferArchiveFreezeResult(NativeTransferArchiveSnapshot? Snapshot, TransferArchiveError Error)
{
    internal bool IsFrozen => Snapshot != null && Error == TransferArchiveError.None;
}
internal sealed record TransferArchiveWriteResult(NativeTransferPackage? Package, TransferArchiveError Error,
    TransferWriteError WriteError = TransferWriteError.None, long? BytesWritten = null)
{
    internal bool IsComplete => Package != null && Error == TransferArchiveError.None;
}
// No open media handles or operational authority. Private paths are deliberately not in ToString.
internal sealed class NativeTransferArchiveSnapshot
{
    internal NativeTransferArchiveSnapshot(ArchiveReader reader, ArchivedThreadView view, byte[] metadata,
        NativeTransferCatalog catalog, ImmutableArray<MediaAssetSnapshot> manifests,
        ImmutableArray<TransferPayloadSource> payloads, Guid packageId)
    { Reader = reader; View = view; Metadata = ImmutableArray.CreateRange(metadata); Catalog = catalog; Manifests = manifests; Payloads = payloads; PackageId = packageId; }
    internal ArchiveReader Reader { get; }
    internal ArchivedThreadView View { get; }
    internal ImmutableArray<byte> Metadata { get; }
    internal NativeTransferCatalog Catalog { get; }
    internal ImmutableArray<MediaAssetSnapshot> Manifests { get; }
    internal ImmutableArray<TransferPayloadSource> Payloads { get; }
    internal Guid PackageId { get; }
}

/// <summary>
/// Single-thread offline archive-to-stream integration. Does not publish a destination,
/// mutate a profile or open a provider. Any failed/cancelled stage must be discarded by caller.
/// Current observations plus exact historical manifest bindings; unbound retained assets still reject.
/// </summary>
internal static class NativeTransferArchiveExporter
{
    private const int MaxDirectories = 32, MaxAssets = 100_000, MaxPayloads = 9998;
    private const long MaxPayload = 1024L * 1024 * 1024, MaxTotal = 2L * 1024 * 1024 * 1024;
    private const long MaxLegacyComparisons = 1_000_000;
    private const int MaxBindingVisits = 100_000;

    internal static async Task<TransferArchiveFreezeResult> FreezeAsync(ArchiveReader reader, ResourceId id,
        DateTimeOffset exportedAtUtc, Guid packageId, bool includeOriginals = true, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reader); ArgumentNullException.ThrowIfNull(id);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            if (packageId == Guid.Empty) return FreezeFail(TransferArchiveError.InvalidArchive);
            ArchiveExportMetadataFile.RejectLinks(reader.DatabasePath);
            ArchivedThreadView? view = await reader.ReadNativeTransferAsync(id, cancellationToken).ConfigureAwait(false);
            if (view == null) return FreezeFail(TransferArchiveError.MissingThread);
            TransferThread thread = NativeTransferArchiveProjection.From(view, cancellationToken);
            NativeTransferCatalog metadataCatalog = new(exportedAtUtc, [thread]);
            byte[] metadata = NativeTransferCatalogSerializer.Serialize(metadataCatalog, cancellationToken);
            // Same validator's metadata-only profile. This is not a fabricated payload measurement.
            NativeTransferEnvelope metadataEnvelope = new(packageId.ToString("D"), "chan-v1", "",
                new(metadata.Length, Hash(metadata)), []);
            if (!NativeTransferCatalogValidator.Inspect(metadata, metadataEnvelope, cancellationToken).IsCatalogValid)
                return FreezeFail(TransferArchiveError.InvalidCatalog);
            if (view.MediaDirectories.Count > MaxDirectories) return FreezeFail(TransferArchiveError.SelectionLimit);
            ImmutableArray<MediaAssetSnapshot>.Builder manifests = ImmutableArray.CreateBuilder<MediaAssetSnapshot>();
            int assetCount = 0;
            if (includeOriginals)
            {
                foreach (string directory in view.MediaDirectories.Distinct(StringComparer.Ordinal))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    MediaAssetSnapshot manifest = await ReadManifestAsync(directory, cancellationToken).ConfigureAwait(false);
                    if (manifest.Status == MediaManifestStatus.Corrupt) return FreezeFail(TransferArchiveError.InvalidMedia);
                    assetCount = checked(assetCount + manifest.Count);
                    if (assetCount > MaxAssets) return FreezeFail(TransferArchiveError.SelectionLimit);
                    manifests.Add(manifest);
                }
            }
            ImmutableArray<MediaAssetSnapshot> frozenManifests = manifests.ToImmutable();
            // A binding is a real historical occurrence, not an old captured post.
            // Plan/preflight all extra provider metadata before opening any media bytes.
            HashSet<string> currentKeys = thread.Posts.SelectMany(p => p.Attachments)
                .Select(a => a.Metadata.AttachmentRevisionKey).OfType<string>().ToHashSet(StringComparer.Ordinal);
            Dictionary<string, (string PostId, TransferAttachment Attachment, ResolvedMediaAttachment Resolved)> retainedPlans = new(StringComparer.Ordinal);
            int bindingVisits = 0, occurrenceCount = thread.Posts.Sum(p => p.Attachments.Length);
            foreach (MediaAssetSnapshot manifest in frozenManifests)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (manifest.AttachmentBindings.Count > MaxBindingVisits - bindingVisits)
                    return FreezeFail(TransferArchiveError.SelectionLimit);
                bindingVisits += manifest.AttachmentBindings.Count;
                foreach (MediaAttachmentBinding binding in manifest.AttachmentBindings.Values)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (binding.Reference.Resource != id) continue;
                    if (binding.Provider.Variant != "original") return FreezeFail(TransferArchiveError.UnrepresentedAssets);
                    bool additional = !currentKeys.Contains(binding.Reference.RevisionKey) && !retainedPlans.ContainsKey(binding.Reference.RevisionKey);
                    if (additional && ++occurrenceCount > 100_000) return FreezeFail(TransferArchiveError.SelectionLimit);
                    CapturedAttachment raw = RetainedMetadata(binding);
                    // Even current and duplicate keys must agree across every frozen root.
                    ResolvedMediaAttachment? resolved = MediaAttachmentResolver.Resolve(frozenManifests, id, binding.Reference.PostId, raw);
                    if (resolved == null || resolved.Binding != binding) return FreezeFail(TransferArchiveError.InvalidMedia);
                    if (!additional) continue;
                    retainedPlans.Add(binding.Reference.RevisionKey, (binding.Reference.PostId,
                        new(raw, TransferMediaRole.Original, TransferContentStatus.Unavailable, null, null), resolved));
                }
            }
            if (retainedPlans.Count != 0)
            {
                NativeTransferCatalog preflight = metadataCatalog with { Threads = [thread with
                {
                    RetainedBindings = retainedPlans.OrderBy(p => p.Key, StringComparer.Ordinal)
                        .Select(p => new TransferRetainedBinding(p.Value.PostId, p.Value.Attachment)).ToImmutableArray()
                }] };
                byte[] bytes = NativeTransferCatalogSerializer.Serialize(preflight, cancellationToken);
                NativeTransferEnvelope envelope = new(packageId.ToString("D"), "chan-v2", "", new(bytes.Length, Hash(bytes)), []);
                if (!NativeTransferCatalogValidator.Inspect(bytes, envelope, cancellationToken).IsCatalogValid)
                    return FreezeFail(TransferArchiveError.InvalidCatalog);
            }
            HashSet<(string Directory, string AssetId)> represented = new();
            Dictionary<string, TransferPayloadSource> payloads = new(StringComparer.Ordinal);
            ImmutableArray<TransferPost>.Builder posts = ImmutableArray.CreateBuilder<TransferPost>(thread.Posts.Length);
            long total = 0, legacyWork = 0;
            TransferArchiveVerificationCache verification = new();
            foreach (TransferPost post in thread.Posts)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ImmutableArray<TransferAttachment>.Builder attachments = ImmutableArray.CreateBuilder<TransferAttachment>(post.Attachments.Length);
                foreach (TransferAttachment attachment in post.Attachments)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    TransferAttachment selected = attachment;
                    if (!includeOriginals) selected = attachment with { Status = TransferContentStatus.Omitted };
                    else if (attachment.Metadata.MediaId != null)
                    {
                        // Reuse exact resolver, with an explicit work bound for its legacy scan.
                        if (attachment.Metadata.AttachmentRevisionKey == null)
                        {
                            legacyWork = checked(legacyWork + assetCount);
                            if (legacyWork > MaxLegacyComparisons) return FreezeFail(TransferArchiveError.SelectionLimit);
                        }
                        ResolvedMediaAttachment? resolved = MediaAttachmentResolver.Resolve(frozenManifests, id, post.Metadata.PostId, attachment.Metadata);
                        if (resolved != null)
                        {
                            selected = await SelectContentAsync(attachment, resolved).ConfigureAwait(false);
                        }
                    }
                    attachments.Add(selected);
                }
                posts.Add(post with { Attachments = attachments.ToImmutable() });
            }
            ImmutableArray<TransferRetainedBinding>.Builder retained = ImmutableArray.CreateBuilder<TransferRetainedBinding>(retainedPlans.Count);
            foreach (var plan in retainedPlans.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Value))
            {
                cancellationToken.ThrowIfCancellationRequested();
                TransferAttachment selected = await SelectContentAsync(plan.Attachment, plan.Resolved).ConfigureAwait(false);
                retained.Add(new(plan.PostId, selected));
            }
            foreach (MediaAssetSnapshot manifest in frozenManifests)
                for (int offset = 0; offset < manifest.Count; offset += 500)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    foreach (MediaAssetReference asset in manifest.GetPage(offset, 500))
                        if (!represented.Contains((manifest.Directory, asset.AssetId))) return FreezeFail(TransferArchiveError.UnrepresentedAssets);
                }
            NativeTransferArchiveSnapshot snapshot = new(reader, view, metadata,
                metadataCatalog with { Threads = [thread with { Posts = posts.ToImmutable(), RetainedBindings = retained.ToImmutable() }] }, frozenManifests,
                payloads.Values.OrderBy(p => p.Descriptor.Sha256, StringComparer.Ordinal).ToImmutableArray(), packageId);
            if (!await IsCurrentAsync(snapshot, cancellationToken).ConfigureAwait(false)) return FreezeFail(TransferArchiveError.StaleSnapshot);
            return new(snapshot, TransferArchiveError.None);

            async Task<TransferAttachment> SelectContentAsync(TransferAttachment attachment, ResolvedMediaAttachment resolved)
            {
                cancellationToken.ThrowIfCancellationRequested();
                MediaAssetSnapshot manifest = resolved.Snapshot; MediaAssetReference asset = resolved.Asset;
                represented.Add((manifest.Directory, asset.AssetId));
                if (asset.Length > MaxPayload) throw new FreezeException(TransferArchiveError.SelectionLimit);
                TransferAssetVerification availability = await verification.VerifyAsync(manifest, asset, cancellationToken).ConfigureAwait(false);
                if (availability == TransferAssetVerification.Limit) throw new FreezeException(TransferArchiveError.SelectionLimit);
                if (availability == TransferAssetVerification.Unavailable)
                    return attachment with { Status = TransferContentStatus.Unavailable, PayloadSha256 = null, History = null };
                TransferHistoricalIntegrity? history = resolved.Binding is { } binding ? HistoricalObservation(binding) : null;
                if (payloads.TryGetValue(asset.Sha256, out TransferPayloadSource? previous))
                {
                    if (previous.Descriptor.Length != asset.Length) throw new FreezeException(TransferArchiveError.InvalidMedia);
                }
                else
                {
                    total = checked(total + asset.Length);
                    if (payloads.Count >= MaxPayloads || total > MaxTotal) throw new FreezeException(TransferArchiveError.SelectionLimit);
                    payloads.Add(asset.Sha256, new(new(asset.Length, asset.Sha256), token =>
                    {
                        token.ThrowIfCancellationRequested();
                        ArchiveExportMetadataFile.RejectLinks(asset.Path);
                        return manifest.OpenVerifiedReadAsync(asset.AssetId, token);
                    }));
                }
                return attachment with { Status = TransferContentStatus.Contained, PayloadSha256 = asset.Sha256, History = history };
            }
        }
        catch (FreezeException e) { return FreezeFail(e.Error); }
        catch (Exception e) when (e is InvalidDataException or FormatException) { return FreezeFail(TransferArchiveError.InvalidArchive); }
        catch (Exception e) when (Environmental(e)) { return FreezeFail(TransferArchiveError.IoFailure); }
    }

    internal static async Task<TransferArchiveWriteResult> WriteAsync(NativeTransferArchiveSnapshot snapshot,
        Stream staging, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot); ArgumentNullException.ThrowIfNull(staging);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            if (!await IsCurrentAsync(snapshot, cancellationToken).ConfigureAwait(false)) return new(null, TransferArchiveError.StaleSnapshot);
            TransferWriteResult result = await NativeTransferPackageWriter.WriteAsync(staging, snapshot.Catalog,
                snapshot.Payloads, snapshot.PackageId, cancellationToken).ConfigureAwait(false);
            if (!result.IsComplete) return new(null, TransferArchiveError.WriteFailure, result.Error);
            if (!await IsCurrentAsync(snapshot, cancellationToken).ConfigureAwait(false)) return new(null, TransferArchiveError.StaleSnapshot);
            cancellationToken.ThrowIfCancellationRequested();
            return new(result.Package, TransferArchiveError.None, BytesWritten: result.BytesWritten);
        }
        catch (Exception e) when (e is InvalidDataException or FormatException) { return new(null, TransferArchiveError.InvalidMedia); }
        catch (Exception e) when (Environmental(e)) { return new(null, TransferArchiveError.IoFailure); }
    }

    internal static async Task<bool> IsCurrentAsync(NativeTransferArchiveSnapshot snapshot, CancellationToken token)
    {
        ArchiveExportMetadataFile.RejectLinks(snapshot.Reader.DatabasePath);
        ArchivedThreadView? view = await snapshot.Reader.ReadNativeTransferAsync(snapshot.View.Summary.Id, token).ConfigureAwait(false);
        if (view == null || view.Summary != snapshot.View.Summary || !view.MediaDirectories.SequenceEqual(snapshot.View.MediaDirectories, StringComparer.Ordinal)) return false;
        byte[] current = NativeTransferCatalogSerializer.Serialize(new(snapshot.Catalog.ExportedAtUtc,
            [NativeTransferArchiveProjection.From(view, token)]), token);
        if (!current.AsSpan().SequenceEqual(snapshot.Metadata.AsSpan())) return false;
        foreach (MediaAssetSnapshot manifest in snapshot.Manifests)
        {
            token.ThrowIfCancellationRequested();
            MediaAssetSnapshot latest = await ReadManifestAsync(manifest.Directory, token).ConfigureAwait(false);
            if (latest.Status != manifest.Status || latest.Generation != manifest.Generation) return false;
        }
        return true;
    }
    internal static TransferHistoricalIntegrity? HistoricalObservation(MediaAttachmentBinding binding)
    {
        if (binding.Acceptance.Kind == MediaAcceptanceKind.PassiveImport)
        {
            // Local import provenance is not historical source permission and is never exported.
            if (binding.PassiveImport?.History is not { } imported) return null;
            return new(imported.Kind switch
            {
                MediaAcceptanceKind.Strict => "strict",
                MediaAcceptanceKind.ExplicitProviderMismatch => "provider-mismatch",
                _ => throw new InvalidDataException("Unsupported imported historical observation.")
            }, imported.AcceptedAtUtc, imported.SizeMismatch, imported.Md5Mismatch);
        }
        return new(binding.Acceptance.Kind switch
        {
            MediaAcceptanceKind.Strict => "strict",
            MediaAcceptanceKind.ExplicitProviderMismatch => "provider-mismatch",
            _ => throw new InvalidDataException("Unsupported media provenance.")
        }, binding.Acceptance.AcceptedAtUtc, binding.Acceptance.SizeMismatch, binding.Acceptance.Md5Mismatch);
    }

    // This surrogate records only the binding's exact provider/reference tuple. It is never
    // put into current Posts and carries no invented post ordinal/time/deletion observation.
    internal static CapturedAttachment RetainedMetadata(MediaAttachmentBinding binding)
    {
        MediaProviderMetadata p = binding.Provider;
        return new(binding.Reference.Position, p.MediaId, p.Variant, p.Url, p.OriginalFileName,
            ExpectedSize: p.ExpectedSize, ExpectedMd5: p.ExpectedMd5)
        {
            ExpectedSha1 = p.ExpectedSha1, ExpectedSha256 = p.ExpectedSha256,
            ProviderSizeHint = p.ProviderSizeHint, AttachmentRevisionKey = binding.Reference.RevisionKey
        };
    }
    private sealed class FreezeException(TransferArchiveError error) : Exception { internal TransferArchiveError Error => error; }
    private static Task<MediaAssetSnapshot> ReadManifestAsync(string directory, CancellationToken token)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(directory) || !Path.IsPathFullyQualified(directory))
                throw new InvalidDataException("The stored media root is invalid.");
            directory = Path.GetFullPath(directory);
        }
        catch (ArgumentException) { throw new InvalidDataException("The stored media root is invalid."); }
        ArchiveExportMetadataFile.RejectLinks(directory);
        ArchiveExportMetadataFile.RejectLinks(Path.Combine(directory, ".meowchan-manifest.json"));
        ArchiveExportMetadataFile.RejectLinks(Path.Combine(directory, ".ochan-manifest.json"));
        return MediaAssetReader.OpenAsync(directory, token);
    }
    private static TransferArchiveFreezeResult FreezeFail(TransferArchiveError error) => new(null, error);
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static bool Environmental(Exception e) => e is IOException or UnauthorizedAccessException or NotSupportedException or ObjectDisposedException or OverflowException or Microsoft.Data.Sqlite.SqliteException;
}
