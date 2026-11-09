using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using MeowChan.Favorites;
using MeowChan.Persistence;

namespace MeowChan.Transfer;

// Passive scalar data only. These DTOs are not a live profile/catalog/lifecycle
// reservation, consent, prepared store batch, commit proof or executable plan.
internal sealed class NativePrivateApplyRequestBinding(
    long packageLength, string packageSha256, string publicCatalogSha256,
    long privateLength, string privateSha256, Guid profileIncarnation, string targetFenceSha256)
{
    internal long PackageLength { get; } = packageLength;
    internal string PackageSha256 { get; } = packageSha256;
    internal string PublicCatalogSha256 { get; } = publicCatalogSha256;
    internal long PrivateLength { get; } = privateLength;
    internal string PrivateSha256 { get; } = privateSha256;
    internal Guid ProfileIncarnation { get; } = profileIncarnation;
    internal string TargetFenceSha256 { get; } = targetFenceSha256;
}

// Missing is a distinct state, not SHA256(empty) or a store-specific sentinel.
// Legacy existing images can lack an instance; a changed after-image cannot.
internal sealed class NativePrivateApplyStoreImage(
    bool exists, int schema, Guid? instance, long revision, long byteLength, string? sha256)
{
    internal bool Exists { get; } = exists;
    internal int Schema { get; } = schema;
    internal Guid? Instance { get; } = instance;
    internal long Revision { get; } = revision;
    internal long ByteLength { get; } = byteLength;
    internal string? Sha256 { get; } = sha256;
}

internal sealed class NativePrivateApplyStoreFence(
    NativePrivateApplyStoreKind kind, NativePrivateApplyStoreImage before,
    NativePrivateApplyStoreImage expectedAfter, NativePrivateApplyStoreImage? beforeBackup = null)
{
    internal NativePrivateApplyStoreKind Kind { get; } = kind;
    internal NativePrivateApplyStoreImage Before { get; } = before;
    internal NativePrivateApplyStoreImage ExpectedAfter { get; } = expectedAfter;
    internal NativePrivateApplyStoreImage? BeforeBackup { get; } = beforeBackup;
}

/// <summary>
/// Bounded, selected-only request data and deterministic identity. The separate
/// codec/journal carry no store mutation capability or active import admission.
/// </summary>
internal sealed class NativePrivateApplyRequest
{
    // The canonical selected payload has the existing 16MiB wire cap. Scalar
    // fences and row classifications add at most 4MiB of bounded encoding work.
    internal const int MaximumRequestBytes = NativePrivateMetadataCodec.MaximumDocumentBytes + 4 * 1024 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    private NativePrivateApplyRequest(Guid operationId, NativePrivateApplyRequestBinding binding,
        NativePrivateMetadataProjection selectedIncoming,
        ImmutableArray<NativePrivateThreadFavoriteComparison> threadRows,
        ImmutableArray<NativePrivateMediaFavoriteComparison> mediaRows,
        ImmutableArray<NativePrivateBookmarkComparison> noteRows,
        ImmutableArray<NativePrivateApplyStoreFence> fences, string payloadHash, int payloadLength,
        string contentPlanHash, string requestHash)
    {
        OperationId = operationId; Binding = binding; SelectedIncoming = selectedIncoming;
        ThreadFavorites = threadRows; MediaFavorites = mediaRows; PostBookmarksAndNotes = noteRows;
        StoreFences = fences; SelectedPayloadSha256 = payloadHash; SelectedPayloadLength = payloadLength;
        ContentPlanSha256 = contentPlanHash; RequestSha256 = requestHash;
    }

    internal Guid OperationId { get; }
    internal NativePrivateApplyRequestBinding Binding { get; }
    internal NativePrivateMetadataProjection SelectedIncoming { get; }
    internal NativePrivateMetadataSelection Selection => SelectedIncoming.Selection;
    internal ImmutableArray<NativePrivateThreadFavoriteComparison> ThreadFavorites { get; }
    internal ImmutableArray<NativePrivateMediaFavoriteComparison> MediaFavorites { get; }
    internal ImmutableArray<FavoriteEntry> MediaFavoriteEntries => SelectedIncoming.MediaFavoriteEntries;
    internal ImmutableArray<NativePrivateBookmarkComparison> PostBookmarksAndNotes { get; }
    internal ImmutableArray<NativePrivateApplyStoreFence> StoreFences { get; }
    internal string SelectedPayloadSha256 { get; }
    internal int SelectedPayloadLength { get; }
    internal string ContentPlanSha256 { get; }
    internal string RequestSha256 { get; }

    internal static NativePrivateApplyRequest Create(Guid operationId,
        NativePrivateMetadataComparisonResult comparison, NativePrivateApplyRequestBinding binding,
        ImmutableArray<NativePrivateApplyStoreFence> storeFences, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            if (operationId == Guid.Empty || comparison is null || binding is null) throw Invalid();
            ValidateBinding(binding);
            NativePrivateMetadataProjection source = comparison.SelectedIncoming;
            if (source is null || source.Selection != comparison.Selection) throw Invalid();
            NativePrivateMetadataCodec.Validate(source, cancellationToken);
            NativePrivateMetadataProjection.ValidateMediaFavoriteEntries(source, cancellationToken);
            // Sorting is explicit ordinal component ordering, not a culture/path
            // comparer or delimiter-concatenated key. No original unfiltered DTO retained.
            var threadOrder = Comparer<ResourceId>.Create(CompareThread);
            var mediaOrder = Comparer<FavoriteAssetKey>.Create(CompareMedia);
            var bookmarkOrder = Comparer<PostBookmarkKey>.Create(CompareBookmark);
            ImmutableArray<FavoriteEntry> mediaEntries = source.MediaFavoriteEntries.Length == 0
                ? [] : source.MediaFavoriteEntries.OrderBy(entry => entry.Key, mediaOrder).ToImmutableArray();
            var selected = new NativePrivateMetadataProjection(source.Threads.Order(threadOrder).ToImmutableArray(),
                source.Selection, source.ThreadFavorites.Order(threadOrder).ToImmutableArray(),
                source.MediaFavorites.Order(mediaOrder).ToImmutableArray(),
                source.PostBookmarksAndNotes.OrderBy(b => b.Key, bookmarkOrder).ToImmutableArray(),
                mediaEntries);
            cancellationToken.ThrowIfCancellationRequested();
            ValidateRows(comparison, selected, cancellationToken);
            ValidateFences(storeFences, selected.Selection, comparison);
            ImmutableArray<NativePrivateThreadFavoriteComparison> threads = comparison.ThreadFavorites
                .OrderBy(r => r.Target, threadOrder).ToImmutableArray();
            ImmutableArray<NativePrivateMediaFavoriteComparison> media = comparison.MediaFavorites
                .OrderBy(r => r.Target, mediaOrder).ToImmutableArray();
            ImmutableArray<NativePrivateBookmarkComparison> notes = comparison.PostBookmarksAndNotes
                .OrderBy(r => r.Target, bookmarkOrder).ToImmutableArray();
            byte[] payload = NativePrivateMetadataCodec.Serialize(selected, cancellationToken);
            string payloadHash = Hash(payload);
            string contentPlanHash, requestHash;
            // No canonical buffer containing old target data or whole-store images.
            using (var digest = new RequestDigest(cancellationToken))
            {
                digest.Text("meowchan-private-apply-content-plan-v2");
                digest.Number(binding.PackageLength); digest.Text(binding.PackageSha256);
                digest.Text(binding.PublicCatalogSha256); digest.Number(binding.PrivateLength);
                digest.Text(binding.PrivateSha256); digest.Text(binding.ProfileIncarnation.ToString("D"));
                digest.Text(binding.TargetFenceSha256); digest.Bytes(payload);
                digest.Number(threads.Length);
                foreach (var row in threads) { digest.Thread(row.Target); digest.Number((int)row.Disposition); }
                digest.Number(media.Length);
                foreach (var row in media)
                {
                    digest.Thread(row.Target.Thread); digest.Text(row.Target.MediaIdentifier);
                    digest.Text(row.Target.Variant); digest.Text(row.Target.Sha256);
                    digest.MediaEntry(row.IncomingEntry); digest.Number((int)row.Disposition);
                }
                digest.Number(notes.Length);
                foreach (var row in notes)
                {
                    digest.Thread(row.Target.Thread); digest.Text(row.Target.Post);
                    // The exact incoming note is already in the selected payload.
                    digest.Number((int)row.Disposition);
                }
                digest.Number(storeFences.Length);
                foreach (var fence in storeFences)
                {
                    digest.Number((int)fence.Kind); digest.Image(fence.Before);
                    digest.Number(fence.BeforeBackup is null ? 0 : 1);
                    if (fence.BeforeBackup is not null) digest.Image(fence.BeforeBackup);
                }
                contentPlanHash = digest.Finish();
            }
            // A future atomic store marker may contain OperationId + ContentPlanSha256,
            // never RequestSha256: after-image bytes contain that marker, so binding
            // the final request hash in it would introduce a circular hash dependency.
            using (var digest = new RequestDigest(cancellationToken))
            {
                digest.Text("meowchan-private-apply-request-v2"); digest.Text(contentPlanHash);
                digest.Number(storeFences.Length);
                foreach (var fence in storeFences)
                { digest.Number((int)fence.Kind); digest.Image(fence.ExpectedAfter); }
                requestHash = digest.Finish();
            }
            cancellationToken.ThrowIfCancellationRequested();
            return new(operationId, binding, selected, threads, media, notes, storeFences,
                payloadHash, payload.Length, contentPlanHash, requestHash);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or InvalidDataException or OverflowException)
        { throw Invalid(); }
    }

    private static void ValidateBinding(NativePrivateApplyRequestBinding value)
    {
        if (value.PackageLength is <= 0 or > 2L * 1024 * 1024 * 1024
            || value.PrivateLength is <= 0 or > NativePrivateMetadataCodec.MaximumDocumentBytes
            || value.PrivateLength > value.PackageLength || value.ProfileIncarnation == Guid.Empty
            || !LowerHash(value.PackageSha256) || !LowerHash(value.PublicCatalogSha256)
            || !LowerHash(value.PrivateSha256) || !LowerHash(value.TargetFenceSha256)) throw Invalid();
    }

    private static void ValidateRows(NativePrivateMetadataComparisonResult value,
        NativePrivateMetadataProjection selected, CancellationToken token)
    {
        if (value.ThreadFavorites.IsDefault || value.MediaFavorites.IsDefault || value.PostBookmarksAndNotes.IsDefault
            || value.ThreadFavorites.Length != selected.ThreadFavorites.Length
            || value.MediaFavorites.Length != selected.MediaFavorites.Length
            || value.PostBookmarksAndNotes.Length != selected.PostBookmarksAndNotes.Length) throw Invalid();
        HashSet<ResourceId> threads = selected.ThreadFavorites.ToHashSet();
        foreach (var row in value.ThreadFavorites)
        {
            token.ThrowIfCancellationRequested();
            if (row is null || !threads.Remove(row.Target) || !ValidDisposition(row.Disposition, false)) throw Invalid();
        }
        HashSet<FavoriteAssetKey> media = selected.MediaFavorites.ToHashSet();
        Dictionary<FavoriteAssetKey, FavoriteEntry> mediaEntries = selected.MediaFavoriteEntries
            .ToDictionary(entry => entry.Key);
        foreach (var row in value.MediaFavorites)
        {
            token.ThrowIfCancellationRequested();
            if (row is null || !media.Remove(row.Target) || !ValidDisposition(row.Disposition, false)) throw Invalid();
            if (selected.MediaFavoriteEntries.IsEmpty)
            {
                if (row.IncomingEntry is not null) throw Invalid();
            }
            else if (row.IncomingEntry is null
                || row.IncomingEntry.Key != row.Target
                || !mediaEntries.TryGetValue(row.Target, out FavoriteEntry? expected)
                || !expected.Equals(row.IncomingEntry)) throw Invalid();
        }
        Dictionary<PostBookmarkKey, string> notes = selected.PostBookmarksAndNotes.ToDictionary(n => n.Key, n => n.Note);
        foreach (var row in value.PostBookmarksAndNotes)
        {
            token.ThrowIfCancellationRequested();
            if (row is null || row.Target is null || !notes.Remove(row.Target, out string? note)
                || !string.Equals(row.IncomingNote, note, StringComparison.Ordinal)
                || !ValidDisposition(row.Disposition, true)) throw Invalid();
        }
    }

    private static bool ValidDisposition(NativePrivateMetadataDisposition value, bool notes)
        => Enum.IsDefined(value) && (notes || value != NativePrivateMetadataDisposition.Conflict);

    private static void ValidateFences(ImmutableArray<NativePrivateApplyStoreFence> fences, NativePrivateMetadataSelection selection,
        NativePrivateMetadataComparisonResult comparison)
    {
        if (fences.IsDefault || fences.Length > 3) throw Invalid();
        NativePrivateApplyStoreKind[] expected = [NativePrivateApplyStoreKind.ThreadFavorites,
            NativePrivateApplyStoreKind.MediaFavorites, NativePrivateApplyStoreKind.PostBookmarksAndNotes];
        int index = 0;
        foreach (var kind in expected)
        {
            bool chosen = kind switch
            {
                NativePrivateApplyStoreKind.ThreadFavorites => selection.ThreadFavorites,
                NativePrivateApplyStoreKind.MediaFavorites => selection.MediaFavorites,
                _ => selection.PostBookmarksAndNotes
            };
            if (!chosen) continue;
            if (index >= fences.Length || fences[index] is not { } fence || fence.Kind != kind) throw Invalid();
            index++;
            long cap = kind == NativePrivateApplyStoreKind.PostBookmarksAndNotes
                ? PostBookmarkStore.MaximumDocumentBytes : FavoriteStore.MaximumBytes;
            ValidateImage(fence.Before, cap); ValidateImage(fence.ExpectedAfter, cap);
            if (kind == NativePrivateApplyStoreKind.PostBookmarksAndNotes)
            {
                if (fence.BeforeBackup is not null) throw Invalid(); // Never a note-history backup.
            }
            else
            {
                if (fence.BeforeBackup is null) throw Invalid();
                ValidateImage(fence.BeforeBackup, cap);
            }
            bool hasAddition = kind switch
            {
                NativePrivateApplyStoreKind.ThreadFavorites => comparison.ThreadFavorites.Any(r => r.Disposition == NativePrivateMetadataDisposition.Add),
                NativePrivateApplyStoreKind.MediaFavorites => comparison.MediaFavorites.Any(r => r.Disposition == NativePrivateMetadataDisposition.Add),
                _ => comparison.PostBookmarksAndNotes.Any(r => r.Disposition == NativePrivateMetadataDisposition.Add)
            };
            if (!hasAddition && !SameImage(fence.Before, fence.ExpectedAfter)) throw Invalid();
            if (SameImage(fence.Before, fence.ExpectedAfter)) continue;
            NativePrivateApplyStoreImage after = fence.ExpectedAfter, before = fence.Before;
            if (!after.Exists || after.Instance is null || after.Revision != checked(before.Revision + 1)
                || before.Instance is Guid old && old != after.Instance) throw Invalid();
        }
        if (index != fences.Length) throw Invalid();
    }

    private static void ValidateImage(NativePrivateApplyStoreImage image, long cap)
    {
        if (image is null) throw Invalid();
        if (!image.Exists)
        {
            if (image.Schema != 0 || image.Instance is not null || image.Revision != 0
                || image.ByteLength != 0 || image.Sha256 is not null) throw Invalid();
        }
        else if (image.Schema is < 1 or > 255 || image.Instance == Guid.Empty || image.Revision < 0
            || image.ByteLength <= 0 || image.ByteLength > cap || !LowerHash(image.Sha256)) throw Invalid();
    }

    private static bool SameImage(NativePrivateApplyStoreImage a, NativePrivateApplyStoreImage b)
        => a.Exists == b.Exists && a.Schema == b.Schema && a.Instance == b.Instance && a.Revision == b.Revision
            && a.ByteLength == b.ByteLength && string.Equals(a.Sha256, b.Sha256, StringComparison.Ordinal);
    private static bool LowerHash(string? value) => value is { Length: 64 }
        && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static InvalidDataException Invalid() => new("The private apply request is invalid.");
    private static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static int CompareThread(ResourceId? a, ResourceId? b)
    {
        int result = string.CompareOrdinal(a!.Source, b!.Source);
        if (result == 0) result = string.CompareOrdinal(a.Board, b.Board);
        return result == 0 ? string.CompareOrdinal(a.Thread, b.Thread) : result;
    }
    private static int CompareMedia(FavoriteAssetKey? a, FavoriteAssetKey? b)
    {
        int result = CompareThread(a!.Thread, b!.Thread);
        if (result == 0) result = string.CompareOrdinal(a.MediaIdentifier, b.MediaIdentifier);
        if (result == 0) result = string.CompareOrdinal(a.Variant, b.Variant);
        return result == 0 ? string.CompareOrdinal(a.Sha256, b.Sha256) : result;
    }
    private static int CompareBookmark(PostBookmarkKey? a, PostBookmarkKey? b)
    {
        int result = CompareThread(a!.Thread, b!.Thread);
        return result == 0 ? string.CompareOrdinal(a.Post, b.Post) : result;
    }

    // Versioned, length-delimited UTF8/binary fields prevent delimiter ambiguity.
    // Every integer is signed 64-bit big-endian; each byte/string field has its
    // byte length first. Hashing is cancellable and capped; no IO/callbacks.
    private sealed class RequestDigest(CancellationToken token) : IDisposable
    {
        private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        private int _bytes;
        internal void Number(long value)
        {
            Span<byte> bytes = stackalloc byte[8]; BinaryPrimitives.WriteInt64BigEndian(bytes, value); Append(bytes);
        }
        internal void Text(string value) => Bytes(StrictUtf8.GetBytes(value));
        internal void Bytes(ReadOnlySpan<byte> bytes) { Number(bytes.Length); Append(bytes); }
        internal void Thread(ResourceId id) { Text(id.Source); Text(id.Board); Text(id.Thread!); }
        internal void Image(NativePrivateApplyStoreImage image)
        {
            Number(image.Exists ? 1 : 0); Number(image.Schema); Text(image.Instance?.ToString("D") ?? "");
            Number(image.Revision); Number(image.ByteLength); Text(image.Sha256 ?? "");
        }
        internal void MediaEntry(FavoriteEntry? entry)
        {
            Number(entry is null ? 0 : 1);
            if (entry is null) return;
            Number(entry.Length);
            Number(entry.OriginalFileName is null ? 0 : 1);
            if (entry.OriginalFileName is not null) Text(entry.OriginalFileName);
        }
        private void Append(ReadOnlySpan<byte> bytes)
        {
            token.ThrowIfCancellationRequested();
            if (bytes.Length > MaximumRequestBytes - _bytes) throw Invalid();
            _bytes += bytes.Length; _hash.AppendData(bytes);
        }
        internal string Finish() => Convert.ToHexString(_hash.GetHashAndReset()).ToLowerInvariant();
        public void Dispose() => _hash.Dispose();
    }
}
