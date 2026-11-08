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

// Each physical source stays distinct even when the package can deduplicate its bytes.
// None of these objects are serialized or exposed as a public error/receipt.
internal sealed record NativeTransferInventoryOrigin(MediaAssetSnapshot Manifest,
    MediaAssetReference? Original = null, MediaDerivedRepresentation? Derived = null)
{
    internal long Length => Original?.Length ?? Derived!.Blob!.Actual.Length;
    internal string Sha256 => Original?.Sha256 ?? Derived!.Blob!.Actual.Sha256;
    internal Task<Stream> OpenAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (Original is { } asset)
        {
            ArchiveExportMetadataFile.RejectLinks(asset.Path);
            return Manifest.OpenVerifiedReadAsync(asset.AssetId, token);
        }
        return Manifest.OpenVerifiedDerivedReadAsync(Derived!, token);
    }
}

// Metadata-only join; public URL/HTML validation must run on its complete descriptor
// projection BEFORE any of the physical sources are opened. Reused by re-export and
// available to the prospective merge budget: no filesystem scans or provider lookups.
internal static class NativeTransferMediaInventoryProjection
{
    internal sealed record Member(string PostId, bool Retained, TransferAttachment Attachment,
        ImmutableArray<NativeTransferInventoryOrigin> Originals,
        ImmutableDictionary<TransferDerivedRepresentationKind, ImmutableArray<NativeTransferInventoryOrigin>> Derived);
    internal sealed record Plan(TransferThread Thread, ImmutableArray<Member> Members);

    private sealed class Evidence
    {
        internal required MediaOriginalEvidence Value;
        internal readonly HashSet<MediaObservedContentStatus> ObservedStatuses = new();
        internal readonly Dictionary<MediaDerivedKind, List<(MediaAssetSnapshot Manifest, MediaDerivedRepresentation Member)>> Derived = new();
    }

    internal static Plan Create(TransferThread thread, ImmutableArray<MediaAssetSnapshot> manifests, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        Dictionary<MediaOccurrence, Evidence> evidence = new();
        Dictionary<string, (string PostId, CapturedAttachment Metadata)> bindings = new(StringComparer.Ordinal);
        long visits = 0;
        foreach (MediaAssetSnapshot manifest in manifests)
        {
            token.ThrowIfCancellationRequested();
            visits = checked(visits + manifest.Count + manifest.AttachmentBindings.Count
                + manifest.OriginalEvidence.Length + manifest.DerivedRepresentations.Length);
            if (visits > 100_000) throw new InvalidDataException("The inventory exceeds the bounded member budget.");
            foreach (MediaOriginalEvidence item in manifest.OriginalEvidence)
            {
                token.ThrowIfCancellationRequested();
                if (item.Occurrence.Resource != thread.Id) continue;
                if (evidence.TryGetValue(item.Occurrence, out Evidence? previous))
                {
                    // Local origins do not participate in the portable identity. Do not use
                    // CompatibleEvidence to erase a nullable recorded SHA1 or source version.
                    if (previous.Value.Metadata != item.Metadata || previous.Value.OriginalByteVersion != item.OriginalByteVersion)
                        throw InvalidUnion();
                }
                else evidence.Add(item.Occurrence, previous = new() { Value = item });
                previous.ObservedStatuses.Add(item.IncomingStatus);
            }
            foreach (MediaAttachmentBinding binding in manifest.AttachmentBindings.Values)
            {
                token.ThrowIfCancellationRequested();
                if (binding.Reference.Resource != thread.Id) continue;
                CapturedAttachment raw = NativeTransferArchiveExporter.RetainedMetadata(binding);
                if (raw.Variant != "original") throw InvalidUnion();
                if (bindings.TryGetValue(binding.Reference.RevisionKey, out var previous)
                    && (previous.PostId != binding.Reference.PostId || previous.Metadata != raw)) throw InvalidUnion();
                bindings[binding.Reference.RevisionKey] = (binding.Reference.PostId, raw);
            }
        }
        foreach (MediaAssetSnapshot manifest in manifests)
        foreach (MediaDerivedRepresentation item in manifest.DerivedRepresentations)
        {
            token.ThrowIfCancellationRequested();
            if (item.Parent.Resource != thread.Id) continue;
            if (!evidence.TryGetValue(item.Parent, out Evidence? parent) || parent.Value.OriginalByteVersion != item.SourceVersion)
                throw InvalidUnion();
            if (!parent.Derived.TryGetValue(item.Kind, out var origins)) parent.Derived.Add(item.Kind, origins = []);
            foreach (var previous in origins)
                if (previous.Member.SourceVersion != item.SourceVersion
                    || previous.Member.Blob is { } left && item.Blob is { } right && left.Actual != right.Actual)
                    throw InvalidUnion();
            origins.Add((manifest, item));
        }
        var members = ImmutableArray.CreateBuilder<Member>();
        HashSet<MediaOccurrence> current = new();
        HashSet<string> currentKeys = new(StringComparer.Ordinal);
        long legacyWork = 0;
        var posts = ImmutableArray.CreateBuilder<TransferPost>();
        foreach (TransferPost post in thread.Posts)
        {
            var attachments = ImmutableArray.CreateBuilder<TransferAttachment>();
            foreach (TransferAttachment attachment in post.Attachments)
            {
                token.ThrowIfCancellationRequested();
                MediaOccurrence occurrence = new(thread.Id, post.Metadata.PostId, attachment.Metadata.Position,
                    attachment.Metadata.AttachmentRevisionKey);
                if (!current.Add(occurrence)) throw InvalidUnion();
                if (occurrence.OriginalRevisionKey is { } key && !currentKeys.Add(key)) throw InvalidUnion();
                Member member = Join(post.Metadata.PostId, attachment, false);
                attachments.Add(member.Attachment); members.Add(member);
            }
            posts.Add(post with { Attachments = attachments.ToImmutable() });
        }
        var retained = ImmutableArray.CreateBuilder<TransferRetainedBinding>();
        Dictionary<MediaOccurrence, CapturedAttachment> historical = new();
        foreach (var pair in bindings)
        {
            token.ThrowIfCancellationRequested();
            if (currentKeys.Contains(pair.Key)) continue;
            historical.Add(new(thread.Id, pair.Value.PostId, pair.Value.Metadata.Position, pair.Key), pair.Value.Metadata);
        }
        foreach (var pair in evidence)
        {
            token.ThrowIfCancellationRequested();
            if (current.Contains(pair.Key)) continue;
            if (pair.Key.OriginalRevisionKey is { } key && currentKeys.Contains(key)) throw InvalidUnion();
            if (historical.TryGetValue(pair.Key, out CapturedAttachment? old) && old != pair.Value.Value.Metadata)
                throw InvalidUnion();
            historical[pair.Key] = pair.Value.Value.Metadata;
        }
        foreach (var pair in historical.OrderBy(p => p.Key.PostId, StringComparer.Ordinal)
                     .ThenBy(p => p.Key.Position).ThenBy(p => p.Key.OriginalRevisionKey, StringComparer.Ordinal))
        {
            token.ThrowIfCancellationRequested();
            // The retained wire cannot express unkeyed/deleted historical observations.
            // Never invent an ID, a post, a status or a provider to make them fit.
            CapturedAttachment raw = pair.Value;
            if (pair.Key.OriginalRevisionKey == null || raw.IsDeleted || raw.MediaId == null || raw.Url == null) throw InvalidUnion();
            Member member = Join(pair.Key.PostId, new(raw, TransferMediaRole.Original, TransferContentStatus.Unavailable, null, null), true);
            if (member.Attachment.Status is not (TransferContentStatus.Unavailable or TransferContentStatus.Omitted)) throw InvalidUnion();
            members.Add(member); retained.Add(new(pair.Key.PostId, member.Attachment));
        }
        return new(thread with { Posts = posts.ToImmutable(), RetainedBindings = retained.ToImmutable() }, members.ToImmutable());

        Member Join(string postId, TransferAttachment attachment, bool isRetained)
        {
            CapturedAttachment raw = attachment.Metadata;
            MediaOccurrence occurrence = new(thread.Id, postId, raw.Position, raw.AttachmentRevisionKey);
            evidence.TryGetValue(occurrence, out Evidence? observed);
            if (observed != null && observed.Value.Metadata with { IsDeleted = raw.IsDeleted } != raw) throw InvalidUnion();
            // Legacy SQL associations must stay unkeyed; no opportunistic remapping from
            // a matching media id/hash to an evidence occurrence or retained revision.
            if (raw.AttachmentRevisionKey == null)
            {
                legacyWork = checked(legacyWork + manifests.Sum(m => (long)m.Count));
                if (legacyWork > 1_000_000) throw new InvalidDataException("Legacy inventory resolution exceeds its work budget.");
                if (MediaAttachmentResolver.Resolve(manifests, thread.Id, postId, raw) == null
                    && manifests.Any(m => MediaAttachmentResolver.Resolve(m, thread.Id, postId, raw) != null)) throw InvalidUnion();
            }
            var originals = ImmutableArray.CreateBuilder<NativeTransferInventoryOrigin>();
            TransferHistoricalIntegrity? history = null;
            bool hasHistory = false;
            MediaActualIntegrity? actual = null;
            foreach (MediaAssetSnapshot manifest in manifests)
            {
                token.ThrowIfCancellationRequested();
                ResolvedMediaAttachment? resolved = MediaAttachmentResolver.Resolve(manifest, thread.Id, postId, raw);
                if (resolved == null)
                {
                    if (raw.AttachmentRevisionKey != null && manifest.FindAttachmentBinding(raw.AttachmentRevisionKey) != null) throw InvalidUnion();
                    continue;
                }
                MediaActualIntegrity thisActual = new(resolved.Asset.Length, resolved.Asset.Md5, resolved.Asset.Sha256) { Sha1 = resolved.Asset.Sha1 };
                if (actual != null && !MediaIntegrityValidation.CompatibleEvidence(actual, thisActual)) throw InvalidUnion();
                if (observed?.Value.OriginalByteVersion is { } version && !MediaIntegrityValidation.CompatibleEvidence(version, thisActual)) throw InvalidUnion();
                actual ??= thisActual;
                TransferHistoricalIntegrity? thisHistory = resolved.Binding == null ? null : NativeTransferArchiveExporter.HistoricalObservation(resolved.Binding);
                if (hasHistory && history != thisHistory) throw InvalidUnion();
                history = thisHistory; hasHistory = true;
                originals.Add(new(manifest, resolved.Asset));
            }
            TransferContentStatus status = observed == null ? attachment.Status
                : Status(CombineStatus(observed.ObservedStatuses));
            // Incoming Contained is not local availability. Byte selection happens later.
            if (status == TransferContentStatus.Contained) status = TransferContentStatus.Unavailable;
            var derivedOrigins = ImmutableDictionary.CreateBuilder<TransferDerivedRepresentationKind, ImmutableArray<NativeTransferInventoryOrigin>>();
            var derived = ImmutableArray.CreateBuilder<TransferDerivedRepresentation>();
            if (observed != null)
            foreach (var pair in observed.Derived.OrderBy(p => p.Key))
            {
                token.ThrowIfCancellationRequested();
                var kind = pair.Key switch { MediaDerivedKind.Preview => TransferDerivedRepresentationKind.Preview,
                    MediaDerivedKind.VideoPosterPng => TransferDerivedRepresentationKind.VideoPosterPng, _ => throw InvalidUnion() };
                var origins = pair.Value.Where(p => p.Member.Blob != null).Select(p => new NativeTransferInventoryOrigin(p.Manifest, Derived: p.Member)).ToImmutableArray();
                // Non-contained descriptors carry no byte promises; a selected contained
                // source is verified later, before it can become contained output.
                TransferContentStatus derivedStatus = pair.Value.Any(p => p.Member.IncomingStatus != MediaDerivedStatus.Omitted)
                    ? TransferContentStatus.Unavailable : TransferContentStatus.Omitted;
                derived.Add(new(kind, occurrence.OriginalRevisionKey!, Version(observed.Value.OriginalByteVersion!)!, derivedStatus, null));
                derivedOrigins.Add(kind, origins);
            }
            TransferAttachment descriptor = attachment with { OriginalByteVersion = Version(observed?.Value.OriginalByteVersion),
                DerivedRepresentations = derived.ToImmutable(), Status = status, PayloadSha256 = null, History = null };
            return new(postId, isRetained, descriptor, originals.ToImmutable(), derivedOrigins.ToImmutable());
        }
    }

    private static MediaObservedContentStatus CombineStatus(HashSet<MediaObservedContentStatus> observed)
    {
        // These statuses describe separate transfer observations, not source identity.
        // All exact metadata/version checks still apply above. Keep every recorded
        // observation in its original manifest and combine without depending on order.
        foreach (MediaObservedContentStatus value in observed) _ = Status(value);
        var included = observed.Where(s => s != MediaObservedContentStatus.Omitted).ToArray();
        if (included.Length == 0) return MediaObservedContentStatus.Omitted;
        if (included.Length == 1) return included[0];
        // The portable occurrence has one status, so differing past observations get
        // a neutral local descriptor rather than one invented "winning" failure.
        // This also permits a new preview with a ContextOnly parent to supplement
        // earlier Failed/Cancelled/Unknown evidence without claiming an original.
        // Only independently verified, selected original bytes yield Contained output.
        return MediaObservedContentStatus.Unavailable;
    }
    private static TransferContentStatus Status(MediaObservedContentStatus status) => status switch
    {
        MediaObservedContentStatus.Contained => TransferContentStatus.Contained,
        MediaObservedContentStatus.Omitted => TransferContentStatus.Omitted,
        MediaObservedContentStatus.Unknown => TransferContentStatus.Unknown,
        MediaObservedContentStatus.Unavailable => TransferContentStatus.Unavailable,
        MediaObservedContentStatus.Failed => TransferContentStatus.Failed,
        MediaObservedContentStatus.Cancelled => TransferContentStatus.Cancelled,
        _ => throw InvalidUnion()
    };
    private static TransferOriginalByteVersion? Version(MediaActualIntegrity? v) => v == null ? null : new(v.Length, v.Sha256, v.Md5, v.Sha1);
    private static InvalidDataException InvalidUnion() => new("The registered inventories do not have one lossless portable projection.");
}
