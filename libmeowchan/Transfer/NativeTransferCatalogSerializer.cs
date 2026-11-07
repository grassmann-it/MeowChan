using System;
using System.Buffers;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using MeowChan.Archive;
using MeowChan.Interfaces;

namespace MeowChan.Transfer;

/// <summary>Explicit passive wire projection, not package validation or admission.
/// The package writer must validate the resulting catalog against the measured package bytes.
/// This layer neither authenticates public references nor recomputes revision keys/history.
/// </summary>
internal static class NativeTransferCatalogSerializer
{
    private const int Cap = 16 * 1024 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static InvalidDataException Bad() => new("Unsupported transfer catalog projection.");

    internal static string ContentSchema(NativeTransferCatalog catalog) => ContentSchema(catalog, default);

    internal static string ContentSchema(NativeTransferCatalog catalog, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (catalog is null || catalog.Threads.IsDefault || catalog.Threads.Length > 1000) throw Bad();
        bool retained = false, unattestedKeyedContent = false, representations = false;
        int posts = 0, attachments = 0;
        foreach (TransferThread thread in catalog.Threads)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (thread is null || thread.Posts.IsDefault || thread.RetainedBindings.IsDefault
                || thread.Posts.Length > 100000 - posts) throw Bad();
            posts += thread.Posts.Length;
            retained |= !thread.RetainedBindings.IsEmpty;
            foreach (TransferPost post in thread.Posts)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (post is null || post.Attachments.IsDefault || post.Attachments.Length > 100000 - attachments) throw Bad();
                attachments += post.Attachments.Length;
                foreach (TransferAttachment attachment in post.Attachments)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    unattestedKeyedContent |= NeedsV3(attachment);
                    representations |= CheckRepresentations(attachment, ref attachments, cancellationToken);
                }
            }
            if (thread.RetainedBindings.Length > 100000 - attachments) throw Bad();
            attachments += thread.RetainedBindings.Length;
            foreach (TransferRetainedBinding binding in thread.RetainedBindings)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (binding is null) throw Bad();
                unattestedKeyedContent |= NeedsV3(binding.Attachment);
                representations |= CheckRepresentations(binding.Attachment, ref attachments, cancellationToken);
            }
        }
        return representations ? "chan-v4" : unattestedKeyedContent ? "chan-v3" : retained ? "chan-v2" : "chan-v1";
    }

    internal static byte[] Serialize(NativeTransferCatalog catalog, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            string schema = ContentSchema(catalog, cancellationToken);
            bool withRetainedBindings = schema is "chan-v2" or "chan-v3" or "chan-v4";
            bool withRepresentations = schema == "chan-v4";
            bool allowUnattestedHistory = schema is "chan-v3" or "chan-v4";
            using CappedBuffer output = new(cancellationToken);
            using (Utf8JsonWriter w = new(output))
            {
                int posts = 0, attachments = 0;
                w.WriteStartObject(); Text(w, "schema", schema); Date(w, "exportedAtUtc", catalog.ExportedAtUtc);
                w.WriteStartArray("threads");
                foreach (TransferThread t in catalog.Threads)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (t is null || t.Id is null || t.Posts.IsDefault || t.Posts.Length > 100000 - posts) throw Bad();
                    posts += t.Posts.Length;
                    HashSet<string>? revisions = withRetainedBindings ? new(StringComparer.Ordinal) : null;
                    w.WriteStartObject(); w.WriteStartObject("id");
                    Required(w, "source", t.Id.Source); Required(w, "board", t.Id.Board); Required(w, "thread", t.Id.Thread); w.WriteEndObject();
                    Text(w, "pageUrl", t.PageUrl); Required(w, "title", t.Title);
                    Text(w, "sourceState", t.SourceState switch { ThreadSourceState.Active => "active", ThreadSourceState.Archived => "archived", ThreadSourceState.NotFound => "not-found", _ => throw Bad() });
                    Date(w, "firstSeenAtUtc", t.FirstSeenAtUtc); Date(w, "capturedAtUtc", t.CapturedAtUtc);
                    Number(w, "captureRevision", t.CaptureRevision); Boolean(w, "decimalPostOrder", t.DecimalPostOrder);
                    w.WriteStartArray("posts");
                    foreach (TransferPost p in t.Posts)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (p is null || p.Metadata is null || p.Attachments.IsDefault || p.Attachments.Length > 100000 - attachments) throw Bad();
                        attachments += p.Attachments.Length;
                        CapturedPost m = p.Metadata;
                        int bits = (int)m.Fields;
                        bool Has(PostFieldPresence f) => (m.Fields & f) != 0;
                        if (bits < 0 || bits > (int)PostFieldPresence.All
                            || !Has(PostFieldPresence.PostedAt) && m.PostedAt != null
                            || !Has(PostFieldPresence.Subject) && m.Subject != null
                            || !Has(PostFieldPresence.Author) && m.Author != null
                            || !Has(PostFieldPresence.Html) && m.Html != null
                            || !Has(PostFieldPresence.IsDeleted) && m.IsDeleted) throw Bad();
                        bool isNull = m.Attachments is null;
                        // Metadata null is authoritative even for hand-built DTOs without the parser flag.
                        // An absent bit uses the parser's canonical empty array, never wire null.
                        if (p.AttachmentsWereNull && (!isNull || !Has(PostFieldPresence.Attachments)) || isNull && p.Attachments.Length != 0
                            || !Has(PostFieldPresence.Attachments) && p.Attachments.Length != 0) throw Bad();
                        if (!isNull)
                        {
                            if (m.Attachments is ImmutableArray<CapturedAttachment> list && list.IsDefault
                                || m.Attachments!.Count != p.Attachments.Length) throw Bad();
                            for (int i = 0; i < p.Attachments.Length; i++)
                            {
                                cancellationToken.ThrowIfCancellationRequested();
                                if (p.Attachments[i] is null || m.Attachments[i] is null || m.Attachments[i] != p.Attachments[i].Metadata) throw Bad();
                            }
                        }
                        w.WriteStartObject(); Required(w, "id", m.PostId); Number(w, "ordinal", m.Ordinal); Number(w, "fields", bits);
                        Date(w, "postedAtUtc", m.PostedAt); Text(w, "subject", m.Subject); Text(w, "author", m.Author); Text(w, "html", m.Html);
                        w.WriteBoolean("isDeleted", m.IsDeleted);
                        if (isNull && Has(PostFieldPresence.Attachments)) w.WriteNull("attachments");
                        else
                        {
                            w.WriteStartArray("attachments");
                            foreach (TransferAttachment a in p.Attachments)
                            {
                                cancellationToken.ThrowIfCancellationRequested();
                                CheckRevision(a, revisions);
                                Attachment(w, a, allowUnattestedHistory, withRepresentations, cancellationToken);
                            }
                            w.WriteEndArray();
                        }
                        w.WriteEndObject();
                    }
                    w.WriteEndArray();
                    // All threads carry the field in v2/v3/v4, including when their retained set is empty.
                    if (withRetainedBindings)
                    {
                        if (t.RetainedBindings.Length > 100000 - attachments) throw Bad();
                        attachments += t.RetainedBindings.Length;
                        w.WriteStartArray("retainedBindings");
                        foreach (TransferRetainedBinding binding in t.RetainedBindings)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            if (binding is null) throw Bad();
                            CheckRevision(binding.Attachment, revisions);
                            w.WriteStartObject(); Required(w, "postId", binding.PostId);
                            w.WritePropertyName("attachment"); Attachment(w, binding.Attachment, allowUnattestedHistory, withRepresentations, cancellationToken, retained: true);
                            w.WriteEndObject();
                        }
                        w.WriteEndArray();
                    }
                    w.WriteEndObject();
                }
                w.WriteEndArray(); w.WriteEndObject(); w.Flush();
            }
            cancellationToken.ThrowIfCancellationRequested();
            return output.ToArray();
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or OverflowException or IndexOutOfRangeException)
        { throw Bad(); }
    }

    private static bool CheckRepresentations(TransferAttachment attachment, ref int count, CancellationToken ct)
    {
        if (attachment is null || attachment.DerivedRepresentations.IsDefault
            || attachment.DerivedRepresentations.Length > 2 || attachment.DerivedRepresentations.Length > 100000 - count) throw Bad();
        count += attachment.DerivedRepresentations.Length;
        if (NativeTransferCatalogValidator.ValidateRepresentations(attachment, ct) != TransferCatalogError.None) throw Bad();
        return attachment.OriginalByteVersion != null || !attachment.DerivedRepresentations.IsEmpty;
    }

    private static bool NeedsV3(TransferAttachment attachment)
    {
        if (attachment is null || attachment.Metadata is null) throw Bad();
        return attachment.Status == TransferContentStatus.Contained
            && attachment.Metadata.AttachmentRevisionKey != null && attachment.History is null;
    }

    private static void CheckRevision(TransferAttachment a, HashSet<string>? revisions)
    {
        if (a is null || a.Metadata is null) throw Bad();
        if (revisions != null && a.Metadata.AttachmentRevisionKey is { } key && !revisions.Add(key)) throw Bad();
    }

    private static void Attachment(Utf8JsonWriter w, TransferAttachment a, bool allowUnattestedHistory, bool withRepresentations, CancellationToken ct, bool retained = false)
    {
        if (a is null || a.Metadata is null) throw Bad();
        CapturedAttachment m = a.Metadata;
        if (retained && (m.IsDeleted || m.PlannedPath != null || m.MediaId is null || m.Url is null || m.AttachmentRevisionKey is null
            || m.Variant != "original" || a.Role != TransferMediaRole.Original
            || a.Status is not (TransferContentStatus.Contained or TransferContentStatus.Unavailable or TransferContentStatus.Omitted))) throw Bad();
        if (m.MediaId is null && !m.IsDeleted
            || a.Status == TransferContentStatus.Contained && (a.PayloadSha256 is null || m.MediaId is null
                || (m.AttachmentRevisionKey is null ? a.History != null : a.History is null && !allowUnattestedHistory))) throw Bad();
        w.WriteStartObject(); Number(w, "position", m.Position); Text(w, "mediaId", m.MediaId); Required(w, "variant", m.Variant);
        Text(w, "url", m.Url); Text(w, "originalFileName", m.OriginalFileName); Number(w, "expectedSize", m.ExpectedSize);
        Text(w, "expectedMd5", m.ExpectedMd5); Text(w, "expectedSha1", m.ExpectedSha1); Text(w, "expectedSha256", m.ExpectedSha256);
        if (m.ProviderSizeHint is not { } h) w.WriteNull("sizeHint");
        else
        {
            if ((int)h.Precision is < 0 or > 2) throw Bad();
            w.WriteStartObject("sizeHint"); Required(w, "rawValue", h.RawValue); Required(w, "unitLabel", h.UnitLabel);
            Number(w, "precision", (int)h.Precision); w.WriteEndObject();
        }
        // The surrogate is not a post observation: the retained wire has no deletion field.
        if (!retained) w.WriteBoolean("isDeleted", m.IsDeleted);
        Text(w, "revisionKey", m.AttachmentRevisionKey);
        Text(w, "role", a.Role switch { TransferMediaRole.Original => "original", TransferMediaRole.Preview => "preview", _ => throw Bad() });
        Text(w, "status", a.Status switch
        {
            TransferContentStatus.Contained => "contained", TransferContentStatus.Omitted => "omitted", TransferContentStatus.Unknown => "unknown",
            TransferContentStatus.Unavailable => "unavailable", TransferContentStatus.Failed => "failed", TransferContentStatus.Cancelled => "cancelled", _ => throw Bad()
        });
        Text(w, "payloadSha256", a.PayloadSha256);
        if (a.Status != TransferContentStatus.Contained && (a.PayloadSha256 != null || a.History != null)) throw Bad();
        if (a.History is not { } history) w.WriteNull("history");
        else
        {
            if (history.Kind is not ("strict" or "provider-mismatch")
                || (history.Kind == "strict") == (history.SizeMismatch || history.Md5Mismatch)) throw Bad();
            w.WriteStartObject("history"); Text(w, "kind", history.Kind); Date(w, "acceptedAtUtc", history.AcceptedAtUtc);
            w.WriteBoolean("sizeMismatch", history.SizeMismatch); w.WriteBoolean("md5Mismatch", history.Md5Mismatch); w.WriteEndObject();
        }
        if (withRepresentations)
        {
            w.WritePropertyName("originalByteVersion");
            if (a.OriginalByteVersion is { } original) ByteVersion(w, original);
            else w.WriteNullValue();
            w.WriteStartArray("derivedRepresentations");
            foreach (TransferDerivedRepresentation item in a.DerivedRepresentations)
            {
                ct.ThrowIfCancellationRequested();
                w.WriteStartObject();
                Text(w, "kind", item.Kind switch
                {
                    TransferDerivedRepresentationKind.Preview => "preview",
                    TransferDerivedRepresentationKind.VideoPosterPng => "video-poster-png", _ => throw Bad()
                });
                Required(w, "sourceRevisionKey", item.SourceRevisionKey);
                w.WritePropertyName("sourceByteVersion"); ByteVersion(w, item.SourceByteVersion);
                Text(w, "status", item.Status switch
                {
                    TransferContentStatus.Contained => "contained", TransferContentStatus.Omitted => "omitted",
                    TransferContentStatus.Unavailable => "unavailable", _ => throw Bad()
                });
                Text(w, "payloadSha256", item.PayloadSha256); w.WriteEndObject();
            }
            w.WriteEndArray();
        }
        w.WriteEndObject();
    }
    private static void ByteVersion(Utf8JsonWriter w, TransferOriginalByteVersion value)
    {
        w.WriteStartObject(); Number(w, "length", value.Length); Required(w, "sha256", value.Sha256);
        Required(w, "md5", value.Md5); Text(w, "sha1", value.Sha1); w.WriteEndObject();
    }
    private static void Required(Utf8JsonWriter w, string name, string? value)
    { if (value is null) throw Bad(); Text(w, name, value); }
    private static void Text(Utf8JsonWriter w, string name, string? value)
    {
        if (value != null && (value.Length > 1024 * 1024 || StrictUtf8.GetByteCount(value) > 1024 * 1024)) throw Bad();
        if (value is null) w.WriteNull(name);
        else { w.WritePropertyName(name); w.WriteStringValue(JsonEncodedText.Encode(value)); }
    }
    private static void Number(Utf8JsonWriter w, string name, long? value)
    { if (value < 0) throw Bad(); if (value.HasValue) w.WriteNumber(name, value.Value); else w.WriteNull(name); }
    private static void Boolean(Utf8JsonWriter w, string name, bool? value)
    { if (value.HasValue) w.WriteBoolean(name, value.Value); else w.WriteNull(name); }
    private static void Date(Utf8JsonWriter w, string name, DateTimeOffset? value)
    {
        if (value.HasValue && value.Value.UtcDateTime == DateTime.MinValue) throw Bad();
        Text(w, name, value?.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture));
    }
    // Utf8JsonWriter can buffer an entire document when backed by a Stream. Instead, each
    // Advance commits to capped storage before it grows. Scratch reservations are independently
    // bounded. Text pre-encodes to escaped UTF-8 so a conservative UTF-16 transcoding
    // reservation cannot reject otherwise supported fields (at most 6 MiB plus token overhead).
    // A conservative reservation near the cap is allowed; only actual Advance bytes consume it.
    private sealed class CappedBuffer(CancellationToken cancellationToken) : IBufferWriter<byte>, IDisposable
    {
        private const int MaxReservation = 8 * 1024 * 1024;
        private readonly MemoryStream storage = new();
        private byte[] scratch = Array.Empty<byte>();

        public Memory<byte> GetMemory(int sizeHint = 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (sizeHint < 0 || sizeHint > MaxReservation) throw Bad();
            int size = Math.Max(4096, sizeHint);
            if (scratch.Length < size) scratch = new byte[size];
            return scratch;
        }
        public Span<byte> GetSpan(int sizeHint = 0) => GetMemory(sizeHint).Span;
        public void Advance(int count)
        {
            // A writer-disposal flush must not mask cancellation with a pending cap failure.
            cancellationToken.ThrowIfCancellationRequested();
            if (count < 0 || count > scratch.Length || count > Cap - storage.Length) throw Bad();
            int end = checked((int)storage.Length + count);
            if (end > storage.Capacity)
                storage.Capacity = Math.Min(Cap, Math.Max(end, Math.Max(4096, storage.Capacity * 2)));
            storage.Write(scratch.AsSpan(0, count));
        }
        internal byte[] ToArray() => storage.ToArray();
        public void Dispose() => storage.Dispose();
    }
}
