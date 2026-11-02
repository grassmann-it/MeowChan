using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Threading;
using MeowChan.Archive;

namespace MeowChan.Transfer;

// Counts are occurrences, NOT unique assets, provider observations or completed imports.
internal sealed record TransferImportStatusCounts(int Contained, int Omitted, int Unknown,
    int Unavailable, int Failed, int Cancelled)
{
    internal int Total => Contained + Omitted + Unknown + Unavailable + Failed + Cancelled;
}
internal sealed record TransferImportThreadPreview(ResourceId Id, int Posts,
    TransferImportStatusCounts CurrentOriginals, TransferImportStatusCounts CurrentPreviews,
    TransferImportStatusCounts RetainedBindings);
internal sealed record TransferImportPreview(ImmutableArray<TransferImportThreadPreview> Threads, int Posts,
    TransferImportStatusCounts CurrentOriginals, TransferImportStatusCounts CurrentPreviews,
    TransferImportStatusCounts RetainedBindings, int UniquePayloads, long UniquePayloadBytes);
internal enum TransferImportPreviewError { None, InvalidPackage }
internal sealed record TransferImportPreviewInspection(TransferImportPreview? Preview, TransferImportPreviewError Error)
{
    internal bool HasPreview => Preview != null && Error == TransferImportPreviewError.None;
}

/// <summary>
/// Side-effect-free, bounded inventory of a previously validated passive package; not an import/merge plan.
/// PRECONDITION: input comes from a successful NativeTransferPackageValidator inspection of exclusively
/// owned bytes. Neither internal DTOs nor their fingerprint are validation capabilities. This helper
/// can check captured catalog/DTO consistency, but cannot remeasure binary bytes or authenticate claims.
/// A future import must independently stage/revalidate bytes and fence destination state before commit.
/// </summary>
internal static class NativeTransferImportPreview
{
    private static readonly TransferEnvelopeLimits Caps = new();

    internal static TransferImportPreviewInspection Create(NativeTransferPackage? package,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (package is null || package.PrivateMetadata != null || package.Catalog is null || package.Catalog.Threads.IsDefault
            || package.Catalog.Threads.Length > 1000 || !CheckEnvelope(package.Envelope, cancellationToken))
            return Invalid();

        // Exactly one bounded parse, using the existing privacy/presence/revision/history/reference
        // validator. Never serialize/reparse per thread/attachment or cache trust by package ID/hash.
        TransferCatalogInspection checkedCatalog = NativeTransferCatalogValidator.Inspect(
            package.Envelope.CatalogBytes.AsMemory(), package.Envelope, cancellationToken);
        if (!checkedCatalog.IsCatalogValid
            || !SameCatalog(package.Catalog, checkedCatalog.Catalog!, cancellationToken)) return Invalid();

        NativeTransferCatalog catalog = checkedCatalog.Catalog!;
        ImmutableArray<TransferImportThreadPreview>.Builder threads = ImmutableArray.CreateBuilder<TransferImportThreadPreview>(catalog.Threads.Length);
        StatusCounter originals = new(), previews = new(), retained = new();
        int posts = 0;
        foreach (TransferThread thread in catalog.Threads)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StatusCounter threadOriginals = new(), threadPreviews = new(), threadRetained = new();
            foreach (TransferPost post in thread.Posts)
            {
                cancellationToken.ThrowIfCancellationRequested();
                foreach (TransferAttachment attachment in post.Attachments)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (attachment.Role == TransferMediaRole.Original)
                    { originals.Add(attachment.Status); threadOriginals.Add(attachment.Status); }
                    else
                    { previews.Add(attachment.Status); threadPreviews.Add(attachment.Status); }
                }
            }
            foreach (TransferRetainedBinding binding in thread.RetainedBindings)
            {
                cancellationToken.ThrowIfCancellationRequested();
                // chan-v2/v3 permit only original retained bindings. Do not inflate current originals
                // or manufacture historical posts, even if binding.PostId is absent from Posts.
                retained.Add(binding.Attachment.Status); threadRetained.Add(binding.Attachment.Status);
            }
            posts += thread.Posts.Length;
            threads.Add(new(thread.Id, thread.Posts.Length, threadOriginals.Freeze(), threadPreviews.Freeze(), threadRetained.Freeze()));
        }

        long payloadBytes = 0;
        foreach (TransferBlobDescriptor payload in package.Envelope.Payloads)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Admission proves each digest occurs once here and is referenced. Count globally,
            // not once per role/thread/current/retained occurrence. No estimates from ExpectedSize.
            payloadBytes += payload.Length;
        }
        cancellationToken.ThrowIfCancellationRequested();
        return new(new(threads.ToImmutable(), posts, originals.Freeze(), previews.Freeze(), retained.Freeze(),
            package.Envelope.Payloads.Length, payloadBytes), TransferImportPreviewError.None);
    }

    private static bool CheckEnvelope(NativeTransferEnvelope? envelope, CancellationToken ct)
    {
        // This public-only inventory must not signal readiness for a component
        // whose receiving consent/apply/recovery path is not implemented yet.
        if (envelope is null || envelope.EnvelopeVersion != 1 || envelope.PrivateMetadata != null
            || envelope.PrivateMetadataBytes.IsDefault || !envelope.PrivateMetadataBytes.IsEmpty
            || envelope.ContentSchema is not ("chan-v1" or "chan-v2" or "chan-v3")
            || !Guid.TryParseExact(envelope.PackageId, "D", out Guid id) || id == Guid.Empty
            || !LowerHash(envelope.Fingerprint) || envelope.Catalog is null
            || envelope.Catalog.Length < 0 || envelope.Catalog.Length > Caps.CatalogBytes
            || !LowerHash(envelope.Catalog.Sha256) || envelope.CatalogBytes.IsDefault
            || envelope.CatalogBytes.Length != envelope.Catalog.Length
            || envelope.Payloads.IsDefault || envelope.Payloads.Length > Caps.Entries - 2
            || envelope.MeasuredPayloads is null || envelope.MeasuredPayloads.Count != envelope.Payloads.Length) return false;
        HashSet<string> hashes = new(StringComparer.Ordinal);
        long total = envelope.Catalog.Length;
        foreach (TransferBlobDescriptor blob in envelope.Payloads)
        {
            ct.ThrowIfCancellationRequested();
            if (blob is null || !LowerHash(blob.Sha256) || blob.Length < 0 || blob.Length > Caps.PayloadBytes
                || !hashes.Add(blob.Sha256)) return false;
            total += blob.Length; // <=9998 * 1GiB; no Int64 overflow even for direct DTOs.
            if (total > Caps.ContainerBytes) return false;
        }
        foreach (KeyValuePair<string, TransferMeasuredBlob> entry in envelope.MeasuredPayloads)
        {
            ct.ThrowIfCancellationRequested();
            TransferMeasuredBlob value = entry.Value;
            if (!hashes.Contains(entry.Key) || value is null || value.Sha256 != entry.Key
                || !Digest(value.Md5, 16) || !Digest(value.Sha1, 20)) return false;
        }
        // Catalogvalidator checks descriptor/measured lengths and referenced hashes against every
        // contained occurrence. It also rejects unreferenced payloads and altered catalog bytes.
        return true;
    }

    private static bool SameCatalog(NativeTransferCatalog actual, NativeTransferCatalog expected, CancellationToken ct)
    {
        if (actual.ExportedAtUtc != expected.ExportedAtUtc || actual.Threads.Length != expected.Threads.Length) return false;
        for (int i = 0; i < expected.Threads.Length; i++)
        {
            ct.ThrowIfCancellationRequested();
            TransferThread a = actual.Threads[i], b = expected.Threads[i];
            if (a is null || a.Posts.IsDefault || a.RetainedBindings.IsDefault
                || a.Posts.Length != b.Posts.Length || a.RetainedBindings.Length != b.RetainedBindings.Length
                || (a with { Posts = default, RetainedBindings = default }) != (b with { Posts = default, RetainedBindings = default })) return false;
            for (int j = 0; j < b.Posts.Length; j++)
            {
                ct.ThrowIfCancellationRequested();
                TransferPost p = a.Posts[j], q = b.Posts[j];
                if (p is null || p.Metadata is null || p.Attachments.IsDefault
                    || p.Attachments.Length != q.Attachments.Length || p.AttachmentsWereNull != q.AttachmentsWereNull
                    || (p.Metadata with { Attachments = null }) != (q.Metadata with { Attachments = null })) return false;
                // Do not enumerate arbitrary caller-provided IReadOnlyList implementations. Validator
                // output has either null or a nondefault immutable metadata projection, and no paths.
                if (q.Metadata.Attachments is null)
                {
                    if (p.Metadata.Attachments != null) return false;
                }
                else
                {
                    if (p.Metadata.Attachments is not ImmutableArray<CapturedAttachment> metadata
                        || metadata.IsDefault || metadata.Length != q.Attachments.Length) return false;
                    for (int k = 0; k < metadata.Length; k++)
                    {
                        ct.ThrowIfCancellationRequested();
                        if (metadata[k] != q.Attachments[k].Metadata) return false;
                    }
                }
                for (int k = 0; k < q.Attachments.Length; k++)
                {
                    ct.ThrowIfCancellationRequested();
                    if (p.Attachments[k] != q.Attachments[k]) return false;
                }
            }
            for (int j = 0; j < b.RetainedBindings.Length; j++)
            {
                ct.ThrowIfCancellationRequested();
                if (a.RetainedBindings[j] != b.RetainedBindings[j]) return false;
            }
        }
        return true;
    }

    private static bool LowerHash(string? value)
    {
        if (value is not { Length: 64 }) return false;
        foreach (char c in value) if (c is not (>= 'a' and <= 'f' or >= '0' and <= '9')) return false;
        return true;
    }
    private static bool Digest(string? value, int length)
    {
        if (value is null || value.Length != (length + 2) / 3 * 4) return false;
        Span<byte> buffer = stackalloc byte[20];
        return Convert.TryFromBase64String(value, buffer, out int written) && written == length;
    }
    private static TransferImportPreviewInspection Invalid() => new(null, TransferImportPreviewError.InvalidPackage);

    private sealed class StatusCounter
    {
        private int contained, omitted, unknown, unavailable, failed, cancelled;
        internal void Add(TransferContentStatus status)
        {
            switch (status)
            {
                case TransferContentStatus.Contained: contained++; break;
                case TransferContentStatus.Omitted: omitted++; break;
                case TransferContentStatus.Unknown: unknown++; break;
                case TransferContentStatus.Unavailable: unavailable++; break;
                case TransferContentStatus.Failed: failed++; break;
                case TransferContentStatus.Cancelled: cancelled++; break;
                default: throw new InvalidOperationException("Unvalidated transfer status.");
            }
        }
        internal TransferImportStatusCounts Freeze() => new(contained, omitted, unknown, unavailable, failed, cancelled);
    }
}
