using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using MeowChan.Archive;
using MeowChan.Interfaces;
using MeowChan.Storage;

namespace MeowChan.Transfer;

internal static class NativeTransferCatalogValidator
{
    private const int MaxCatalog = 16 * 1024 * 1024, MaxText = 1024 * 1024;
    private static readonly string[] ThreadFields =
        ["id", "pageUrl", "title", "sourceState", "firstSeenAtUtc", "capturedAtUtc", "captureRevision", "decimalPostOrder", "posts"];
    private static readonly string[] PostFields =
        ["id", "ordinal", "fields", "postedAtUtc", "subject", "author", "html", "isDeleted", "attachments"];
    private static readonly string[] V2ThreadFields = [.. ThreadFields, "retainedBindings"];
    private static readonly string[] RetainedAttachmentFields =
        ["position", "mediaId", "variant", "url", "originalFileName", "expectedSize", "expectedMd5", "expectedSha1", "expectedSha256",
            "sizeHint", "revisionKey", "role", "status", "payloadSha256", "history"];
    private static readonly string[] CurrentAttachmentFields = [.. RetainedAttachmentFields, "isDeleted"];
    private static readonly string[] V4CurrentAttachmentFields = [.. CurrentAttachmentFields, "originalByteVersion", "derivedRepresentations"];
    private static readonly string[] V4RetainedAttachmentFields = [.. RetainedAttachmentFields, "originalByteVersion", "derivedRepresentations"];
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    internal static TransferCatalogInspection Inspect(ReadOnlyMemory<byte> bytes, NativeTransferEnvelope envelope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        cancellationToken.ThrowIfCancellationRequested();
        if (bytes.Length > MaxCatalog) return Fail(TransferCatalogError.CatalogLimit);
        try
        {
            // Even callers of this lower layer must bind the exact catalog, not merely pass a previous envelope.
            if (bytes.Length != envelope.Catalog.Length || Hash(bytes.Span) != envelope.Catalog.Sha256)
                return Fail(TransferCatalogError.InvalidReference);
            _ = StrictUtf8.GetCharCount(bytes.Span);
            using JsonDocument document = JsonDocument.Parse(bytes, new() { MaxDepth = 24 });
            JsonElement root = document.RootElement;
            Fields(root, "schema", "exportedAtUtc", "threads");
            string schema = Text(root, "schema")!;
            if (schema is not ("chan-v1" or "chan-v2" or "chan-v3" or "chan-v4") || envelope.ContentSchema != schema) throw Bad(TransferCatalogError.UnsupportedSchema);
            bool withRetainedBindings = schema is "chan-v2" or "chan-v3" or "chan-v4";
            DateTimeOffset exportedAt = Date(root, "exportedAtUtc")!.Value;
            JsonElement threads = Array(root, "threads", 1000);
            Budget budget = new(envelope, cancellationToken);
            HashSet<ResourceId> identities = new();
            HashSet<string> referenced = new(StringComparer.Ordinal);
            ImmutableArray<TransferThread>.Builder result = ImmutableArray.CreateBuilder<TransferThread>();
            foreach (JsonElement thread in threads.EnumerateArray())
            {
                cancellationToken.ThrowIfCancellationRequested();
                TransferThread item = Thread(thread, envelope, referenced, budget, withRetainedBindings, cancellationToken);
                if (!identities.Add(item.Id)) throw Bad(TransferCatalogError.DuplicateIdentity);
                result.Add(item);
            }
            if (referenced.Count != envelope.Payloads.Length || envelope.Payloads.Any(p => !referenced.Contains(p.Sha256)))
                throw Bad(TransferCatalogError.InvalidReference);
            return new(new(exportedAt, result.ToImmutable()), TransferCatalogError.None);
        }
        catch (CatalogException e) { return Fail(e.Error); }
        catch (Exception e) when (e is JsonException or DecoderFallbackException or InvalidOperationException or FormatException or OverflowException)
        { return Fail(TransferCatalogError.InvalidJson); }
    }
    // Only metadata shape/count admission, NOT a catalog result and never fabricated measurements.
    // Package inspection calls this on its captured catalog before reading any payload bytes.
    internal static TransferCatalogError PreflightV4ReferenceBudget(ReadOnlyMemory<byte> bytes, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (bytes.Length > MaxCatalog) return TransferCatalogError.CatalogLimit;
        try
        {
            _ = StrictUtf8.GetCharCount(bytes.Span);
            using JsonDocument document = JsonDocument.Parse(bytes, new() { MaxDepth = 24 });
            JsonElement root = document.RootElement;
            Fields(root, "schema", "exportedAtUtc", "threads");
            if (Text(root, "schema") != "chan-v4") return TransferCatalogError.UnsupportedSchema;
            int posts = 0, references = 0;
            foreach (JsonElement thread in Array(root, "threads", 1000).EnumerateArray())
            {
                ct.ThrowIfCancellationRequested(); Fields(thread, V2ThreadFields);
                JsonElement current = Array(thread, "posts", 100000);
                Charge(ref posts, current.GetArrayLength());
                foreach (JsonElement post in current.EnumerateArray())
                {
                    ct.ThrowIfCancellationRequested(); Fields(post, PostFields);
                    if (post.GetProperty("attachments").ValueKind == JsonValueKind.Null) continue;
                    foreach (JsonElement attachment in Array(post, "attachments", 100000).EnumerateArray())
                        CountAttachment(attachment, retained: false);
                }
                foreach (JsonElement retained in Array(thread, "retainedBindings", 100000).EnumerateArray())
                {
                    ct.ThrowIfCancellationRequested(); Fields(retained, "postId", "attachment");
                    CountAttachment(retained.GetProperty("attachment"), retained: true);
                }
            }
            ct.ThrowIfCancellationRequested();
            return TransferCatalogError.None;

            void CountAttachment(JsonElement attachment, bool retained)
            {
                ct.ThrowIfCancellationRequested();
                Fields(attachment, retained ? V4RetainedAttachmentFields : V4CurrentAttachmentFields);
                Charge(ref references, 1 + Array(attachment, "derivedRepresentations", 2).GetArrayLength());
            }
            static void Charge(ref int count, int add)
            {
                if (add > 100000 - count) throw Bad(TransferCatalogError.BudgetExceeded);
                count += add;
            }
        }
        catch (CatalogException e) { return e.Error; }
        catch (Exception e) when (e is JsonException or DecoderFallbackException or InvalidOperationException or FormatException or OverflowException)
        { return TransferCatalogError.InvalidJson; }
    }

    private static TransferThread Thread(JsonElement node, NativeTransferEnvelope envelope, HashSet<string> referenced, Budget budget, bool withRetainedBindings, CancellationToken ct)
    {
        Fields(node, withRetainedBindings ? V2ThreadFields : ThreadFields);
        JsonElement identity = node.GetProperty("id");
        Fields(identity, "source", "board", "thread");
        ResourceId id = new(Identifier(identity, "source")!, Identifier(identity, "board")!, Identifier(identity, "thread")!);
        string? pageUrl = Text(node, "pageUrl", true);
        if (pageUrl != null && !TransferPublicReferences.IsThreadPage(pageUrl, id)) throw Bad(TransferCatalogError.UnsafeUrl);
        string title = Text(node, "title")!;
        ThreadSourceState sourceState = Text(node, "sourceState") switch
        {
            "active" => ThreadSourceState.Active, "archived" => ThreadSourceState.Archived,
            "not-found" => ThreadSourceState.NotFound, _ => throw Bad(TransferCatalogError.InvalidField)
        };
        DateTimeOffset firstSeen = Date(node, "firstSeenAtUtc")!.Value;
        DateTimeOffset? captured = Date(node, "capturedAtUtc", true);
        long? revision = Number(node, "captureRevision", true);
        bool? order = Boolean(node, "decimalPostOrder", true);
        HashSet<string> ids = new(StringComparer.Ordinal);
        // v1 retains its historical admission behavior; v2/v3/v4 bind both collections
        // to a single per-thread revision identity set, not payload identity.
        HashSet<string>? revisions = withRetainedBindings ? new(StringComparer.Ordinal) : null;
        ImmutableArray<TransferPost>.Builder posts = ImmutableArray.CreateBuilder<TransferPost>();
        foreach (JsonElement post in Array(node, "posts", 100_000).EnumerateArray())
        {
            ct.ThrowIfCancellationRequested(); budget.AddPost();
            TransferPost item = Post(post, id, envelope, referenced, budget, revisions, ct);
            if (!ids.Add(item.Metadata.PostId)) throw Bad(TransferCatalogError.DuplicateIdentity);
            posts.Add(item);
        }
        ImmutableArray<TransferRetainedBinding>.Builder retained = ImmutableArray.CreateBuilder<TransferRetainedBinding>();
        if (withRetainedBindings)
        {
            foreach (JsonElement binding in Array(node, "retainedBindings", 100_000).EnumerateArray())
            {
                ct.ThrowIfCancellationRequested(); budget.AddAttachment();
                Fields(binding, "postId", "attachment");
                string postId = Identifier(binding, "postId")!;
                TransferAttachment attachment = Attachment(binding.GetProperty("attachment"), id, postId, envelope, referenced, budget, ct, retained: true);
                AddRevision(attachment, revisions);
                retained.Add(new(postId, attachment));
            }
        }
        return new(id, pageUrl, title, sourceState, firstSeen, captured, revision, order, posts.ToImmutable())
            { RetainedBindings = retained.ToImmutable() };
    }
    private static TransferPost Post(JsonElement node, ResourceId resource, NativeTransferEnvelope envelope, HashSet<string> referenced, Budget budget, HashSet<string>? revisions, CancellationToken ct)
    {
        Fields(node, PostFields);
        string id = Identifier(node, "id")!;
        long ordinal = Number(node, "ordinal")!.Value;
        long bits = Number(node, "fields")!.Value;
        if (bits > (int)PostFieldPresence.All) throw Bad(TransferCatalogError.InvalidPresence);
        PostFieldPresence fields = (PostFieldPresence)bits;
        DateTimeOffset? posted = Date(node, "postedAtUtc", true);
        string? subject = Text(node, "subject", true), author = Text(node, "author", true), html = Text(node, "html", true);
        bool deleted = Boolean(node, "isDeleted")!.Value;
        JsonElement attachmentNodes = node.GetProperty("attachments");
        bool nullAttachments = attachmentNodes.ValueKind == JsonValueKind.Null;
        if (!nullAttachments) attachmentNodes = Array(node, "attachments", 100_000);
        bool Has(PostFieldPresence flag) => fields.HasFlag(flag);
        if (!Has(PostFieldPresence.PostedAt) && posted != null || !Has(PostFieldPresence.Subject) && subject != null
            || !Has(PostFieldPresence.Author) && author != null || !Has(PostFieldPresence.Html) && html != null
            || !Has(PostFieldPresence.IsDeleted) && deleted || !Has(PostFieldPresence.Attachments) && (nullAttachments || attachmentNodes.GetArrayLength() != 0))
            throw Bad(TransferCatalogError.InvalidPresence);
        if (html != null && !TransferPublicReferences.IsInertHtml(html)) throw Bad(TransferCatalogError.UnsafeHtml);
        HashSet<int> positions = new();
        ImmutableArray<TransferAttachment>.Builder attachments = ImmutableArray.CreateBuilder<TransferAttachment>();
        foreach (JsonElement attachment in nullAttachments ? Enumerable.Empty<JsonElement>() : attachmentNodes.EnumerateArray())
        {
            ct.ThrowIfCancellationRequested(); budget.AddAttachment();
            TransferAttachment item = Attachment(attachment, resource, id, envelope, referenced, budget, ct);
            AddRevision(item, revisions);
            if (!positions.Add(item.Metadata.Position)) throw Bad(TransferCatalogError.DuplicateIdentity);
            attachments.Add(item);
        }
        ImmutableArray<TransferAttachment> frozen = attachments.ToImmutable();
        CapturedPost post = new(id, ordinal, fields, posted, subject, author, html, deleted,
            nullAttachments ? null : frozen.Select(a => a.Metadata).ToImmutableArray());
        return new(post, frozen) { AttachmentsWereNull = nullAttachments };
    }
    private static void AddRevision(TransferAttachment attachment, HashSet<string>? revisions)
    {
        if (revisions != null && attachment.Metadata.AttachmentRevisionKey is { } key && !revisions.Add(key))
            throw Bad(TransferCatalogError.DuplicateIdentity);
    }
    private static TransferAttachment Attachment(JsonElement node, ResourceId resource, string post, NativeTransferEnvelope envelope, HashSet<string> referenced, Budget budget, CancellationToken ct, bool retained = false)
    {
        bool v4 = envelope.ContentSchema == "chan-v4";
        Fields(node, v4 ? (retained ? V4RetainedAttachmentFields : V4CurrentAttachmentFields)
            : (retained ? RetainedAttachmentFields : CurrentAttachmentFields));
        JsonElement derivedNodes = default;
        if (v4)
        {
            derivedNodes = Array(node, "derivedRepresentations", 2);
            // Charge references before constructing/resolving any nested DTO, even hash aliases.
            budget.AddAttachments(derivedNodes.GetArrayLength());
        }
        long number = Number(node, "position")!.Value;
        if (number > int.MaxValue) throw Bad(TransferCatalogError.InvalidField);
        int position = (int)number;
        string? mediaId = Identifier(node, "mediaId", true);
        string variant = Identifier(node, "variant")!;
        bool deleted = !retained && Boolean(node, "isDeleted")!.Value;
        if (mediaId == null && !deleted) throw Bad(TransferCatalogError.InvalidField);
        string? url = Text(node, "url", true);
        if (url != null && !TransferPublicReferences.IsPublicUrl(url)) throw Bad(TransferCatalogError.UnsafeUrl);
        string? name = Text(node, "originalFileName", true);
        if (name != null && !SafeFileName(name)) throw Bad(TransferCatalogError.InvalidField);
        long? expectedSize = Number(node, "expectedSize", true);
        string? md5 = Digest(node, "expectedMd5", 16), sha1 = Digest(node, "expectedSha1", 20), sha256 = Text(node, "expectedSha256", true);
        if (sha256 != null && !MediaIntegrityValidation.IsSha256(sha256)) throw Bad(TransferCatalogError.InvalidField);
        MediaProviderSizeHint? hint = Hint(node.GetProperty("sizeHint"));
        string? revision = Text(node, "revisionKey", true);
        if (retained && (mediaId == null || url == null || revision == null || variant != "original"))
            throw Bad(TransferCatalogError.InvalidField);
        CapturedAttachment metadata = new(position, mediaId, variant, url, name, null, expectedSize, md5, deleted)
            { ExpectedSha1 = sha1, ExpectedSha256 = sha256, ProviderSizeHint = hint, AttachmentRevisionKey = revision };
        MediaProviderMetadata? provider = mediaId != null && url != null ? new(mediaId, variant, url, name, expectedSize, md5, sha256)
            { ExpectedSha1 = sha1, ProviderSizeHint = hint } : null;
        if (revision != null && (provider == null || revision != MediaRevisionKeys.CreateAttachmentKey(resource, post, position, provider)))
            throw Bad(TransferCatalogError.InvalidRevision);
        TransferMediaRole role = Text(node, "role") switch
        { "original" => TransferMediaRole.Original, "preview" => TransferMediaRole.Preview, _ => throw Bad(TransferCatalogError.InvalidField) };
        TransferContentStatus status = Text(node, "status") switch
        {
            "contained" => TransferContentStatus.Contained, "omitted" => TransferContentStatus.Omitted, "unknown" => TransferContentStatus.Unknown,
            "unavailable" => TransferContentStatus.Unavailable, "failed" => TransferContentStatus.Failed,
            "cancelled" => TransferContentStatus.Cancelled, _ => throw Bad(TransferCatalogError.InvalidField)
        };
        if (retained && (role != TransferMediaRole.Original
            || status is not (TransferContentStatus.Contained or TransferContentStatus.Unavailable or TransferContentStatus.Omitted)))
            throw Bad(TransferCatalogError.InvalidField);
        string? payload = Text(node, "payloadSha256", true);
        TransferHistoricalIntegrity? history = History(node.GetProperty("history"));
        if (status != TransferContentStatus.Contained)
        {
            if (payload != null || history != null) throw Bad(TransferCatalogError.InvalidReference);
        }
        else
        {
            if (!LowerHash(payload) || mediaId == null || !envelope.MeasuredPayloads.TryGetValue(payload!, out TransferMeasuredBlob? actual)
                || !budget.PayloadLengths.TryGetValue(payload!, out long expectedLength) || expectedLength != actual.Length) throw Bad(TransferCatalogError.InvalidReference);
            referenced.Add(payload!);
            if (revision == null)
            {
                if (history != null) throw Bad(TransferCatalogError.InvalidHistory);
            }
            else
            {
                if (provider == null) throw Bad(TransferCatalogError.InvalidHistory);
                if (history is null)
                {
                    // v3/v4 can carry an unattested keyed observation. Actual package
                    // bytes above remain mandatory; provider digests are not an acceptance claim.
                    if (envelope.ContentSchema is not ("chan-v3" or "chan-v4")) throw Bad(TransferCatalogError.InvalidHistory);
                }
                else
                {
                    // A supplied history is never downgraded to an unattested observation.
                    ProviderIntegrityAssessment assessment = ProviderIntegrityAssessment.Assess(provider, new(actual.Length, actual.Md5, actual.Sha256) { Sha1 = actual.Sha1 });
                    if (assessment.Sha1Mismatch || assessment.Sha256Mismatch || assessment.SizeMismatch != history.SizeMismatch || assessment.Md5Mismatch != history.Md5Mismatch
                        || assessment.IsStrictMatch != (history.Kind == "strict")) throw Bad(TransferCatalogError.InvalidHistory);
                }
            }
        }
        TransferAttachment result = new(metadata, role, status, payload, history);
        if (!v4) return result;
        TransferOriginalByteVersion? original = node.GetProperty("originalByteVersion").ValueKind == JsonValueKind.Null
            ? null : ByteVersion(node.GetProperty("originalByteVersion"));
        ImmutableArray<TransferDerivedRepresentation>.Builder derived = ImmutableArray.CreateBuilder<TransferDerivedRepresentation>();
        foreach (JsonElement item in derivedNodes.EnumerateArray())
        {
            ct.ThrowIfCancellationRequested();
            Fields(item, "kind", "sourceRevisionKey", "sourceByteVersion", "status", "payloadSha256");
            TransferDerivedRepresentationKind kind = Text(item, "kind") switch
            {
                "preview" => TransferDerivedRepresentationKind.Preview,
                "video-poster-png" => TransferDerivedRepresentationKind.VideoPosterPng,
                _ => throw Bad(TransferCatalogError.InvalidField)
            };
            TransferContentStatus derivedStatus = Text(item, "status") switch
            {
                "contained" => TransferContentStatus.Contained, "omitted" => TransferContentStatus.Omitted,
                "unavailable" => TransferContentStatus.Unavailable, _ => throw Bad(TransferCatalogError.InvalidField)
            };
            derived.Add(new(kind, Text(item, "sourceRevisionKey")!, ByteVersion(item.GetProperty("sourceByteVersion")),
                derivedStatus, Text(item, "payloadSha256", true)));
        }
        result = result with { OriginalByteVersion = original, DerivedRepresentations = derived.ToImmutable() };
        TransferCatalogError error = ValidateRepresentations(result, ct);
        if (error != TransferCatalogError.None) throw Bad(error);
        if (original != null && status == TransferContentStatus.Contained)
        {
            TransferMeasuredBlob actual = envelope.MeasuredPayloads[payload!];
            // Null historical SHA1 stays null; any recorded SHA1 must match, never be enriched silently.
            if (original.Length != actual.Length || original.Sha256 != actual.Sha256 || original.Md5 != actual.Md5
                || original.Sha1 != null && original.Sha1 != actual.Sha1) throw Bad(TransferCatalogError.InvalidReference);
        }
        foreach (TransferDerivedRepresentation item in result.DerivedRepresentations)
        {
            ct.ThrowIfCancellationRequested();
            if (item.Status != TransferContentStatus.Contained) continue;
            if (!envelope.MeasuredPayloads.TryGetValue(item.PayloadSha256!, out TransferMeasuredBlob? actual)
                || !budget.PayloadLengths.TryGetValue(item.PayloadSha256!, out long length)
                || actual.Length != length || actual.Sha256 != item.PayloadSha256)
                throw Bad(TransferCatalogError.InvalidReference);
            referenced.Add(item.PayloadSha256!);
        }
        return result;
    }

    private static TransferOriginalByteVersion ByteVersion(JsonElement node)
    {
        Fields(node, "length", "sha256", "md5", "sha1");
        TransferOriginalByteVersion result = new(Number(node, "length")!.Value, Text(node, "sha256")!,
            Text(node, "md5")!, Text(node, "sha1", true));
        if (!ValidByteVersion(result)) throw Bad(TransferCatalogError.InvalidField);
        return result;
    }

    // Shared strict DTO rules for the serializer and parser. No inferred source, payload
    // measurements, provider history, or authority: those are separate admission checks.
    internal static TransferCatalogError ValidateRepresentations(TransferAttachment attachment, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (attachment is null || attachment.Metadata is null || attachment.DerivedRepresentations.IsDefault
            || attachment.DerivedRepresentations.Length > 2) return TransferCatalogError.InvalidField;
        TransferOriginalByteVersion? original = attachment.OriginalByteVersion;
        if (original == null && attachment.DerivedRepresentations.IsEmpty) return TransferCatalogError.None;
        if (attachment.Role != TransferMediaRole.Original || attachment.Metadata.Variant != "original"
            || original == null || !ValidByteVersion(original)) return TransferCatalogError.InvalidField;
        if (attachment.Metadata.AttachmentRevisionKey == null) return TransferCatalogError.InvalidRevision;
        // A contained original already names its exact bytes on the wire. Reject a
        // contradictory recorded hash before the writer opens payload sources; the
        // package reader still checks every recorded digest/length against measurement.
        if (attachment.Status == TransferContentStatus.Contained && original.Sha256 != attachment.PayloadSha256)
            return TransferCatalogError.InvalidReference;
        int previousKind = -1;
        foreach (TransferDerivedRepresentation item in attachment.DerivedRepresentations)
        {
            ct.ThrowIfCancellationRequested();
            if (item is null || item.Kind is not (TransferDerivedRepresentationKind.Preview or TransferDerivedRepresentationKind.VideoPosterPng)
                || item.Status is not (TransferContentStatus.Contained or TransferContentStatus.Omitted or TransferContentStatus.Unavailable))
                return TransferCatalogError.InvalidField;
            // Unique kinds in canonical order, not a last-write-wins representation map.
            if ((int)item.Kind <= previousKind) return TransferCatalogError.DuplicateIdentity;
            previousKind = (int)item.Kind;
            if (item.SourceRevisionKey != attachment.Metadata.AttachmentRevisionKey || item.SourceByteVersion != original)
                return TransferCatalogError.InvalidReference;
            if (item.Status == TransferContentStatus.Contained ? !LowerHash(item.PayloadSha256) : item.PayloadSha256 != null)
                return TransferCatalogError.InvalidReference;
        }
        return TransferCatalogError.None;
    }

    private static bool ValidByteVersion(TransferOriginalByteVersion value) => value.Length >= 0 && LowerHash(value.Sha256)
        && CanonicalDigest(value.Md5, 16) && (value.Sha1 == null || CanonicalDigest(value.Sha1, 20));
    private static bool CanonicalDigest(string? value, int length)
    {
        if (value == null || value.Length != ((length + 2) / 3) * 4) return false;
        Span<byte> decoded = stackalloc byte[20];
        return Convert.TryFromBase64String(value, decoded, out int count) && count == length
            && Convert.ToBase64String(decoded[..count]) == value;
    }

    private static MediaProviderSizeHint? Hint(JsonElement node)
    {
        if (node.ValueKind == JsonValueKind.Null) return null;
        Fields(node, "rawValue", "unitLabel", "precision");
        long precision = Number(node, "precision")!.Value;
        if (precision > 2) throw Bad(TransferCatalogError.InvalidField);
        MediaProviderSizeHint result = new(Text(node, "rawValue")!, Text(node, "unitLabel")!, (MediaSizePrecision)precision);
        try { MediaIntegrityValidation.ValidateSizeHint(result); }
        catch (InvalidDataException) { throw Bad(TransferCatalogError.InvalidField); }
        return result;
    }
    private static TransferHistoricalIntegrity? History(JsonElement node)
    {
        if (node.ValueKind == JsonValueKind.Null) return null;
        Fields(node, "kind", "acceptedAtUtc", "sizeMismatch", "md5Mismatch");
        string kind = Text(node, "kind")!;
        if (kind is not ("strict" or "provider-mismatch")) throw Bad(TransferCatalogError.InvalidHistory);
        return new(kind, Date(node, "acceptedAtUtc")!.Value, Boolean(node, "sizeMismatch")!.Value, Boolean(node, "md5Mismatch")!.Value);
    }
    private static bool SafeFileName(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 256 || value is "." or ".." || value.IndexOfAny(['/', '\\', ':']) >= 0
            || HasIdentifierControls(value)) return false;
        string device = value.Split('.')[0].TrimEnd(' ').ToUpperInvariant();
        return device is not ("CON" or "PRN" or "AUX" or "NUL" or "CLOCK$")
            && !(device.Length == 4 && (device.StartsWith("COM", StringComparison.Ordinal) || device.StartsWith("LPT", StringComparison.Ordinal)) && device[3] is >= '0' and <= '9' or '¹' or '²' or '³');
    }
    private static bool HasIdentifierControls(string value)
    {
        foreach (Rune rune in value.EnumerateRunes())
            if (Rune.GetUnicodeCategory(rune) is UnicodeCategory.Control or UnicodeCategory.Format) return true;
        return false;
    }
    private static string? Digest(JsonElement node, string name, int length)
    {
        string? value = Text(node, name, true);
        if (value == null) return null;
        byte[] bytes = Convert.FromBase64String(value);
        if (bytes.Length != length) throw Bad(TransferCatalogError.InvalidField);
        return value;
    }
    private static string? Identifier(JsonElement node, string name, bool nullable = false)
    {
        string? value = Text(node, name, nullable);
        if (value == null) return null;
        if (string.IsNullOrWhiteSpace(value) || value.Length > 256 || HasIdentifierControls(value))
            throw Bad(TransferCatalogError.InvalidField);
        return value;
    }
    private static string? Text(JsonElement node, string name, bool nullable = false)
    {
        JsonElement value = node.GetProperty(name);
        if (nullable && value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.String) throw Bad(TransferCatalogError.InvalidField);
        string result = value.GetString()!;
        if (StrictUtf8.GetByteCount(result) > MaxText) throw Bad(TransferCatalogError.BudgetExceeded);
        return result;
    }
    private static DateTimeOffset? Date(JsonElement node, string name, bool nullable = false)
    {
        string? value = Text(node, name, nullable);
        if (value == null) return null;
        if (!DateTimeOffset.TryParseExact(value, "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTimeOffset date) || date == default)
            throw Bad(TransferCatalogError.InvalidField);
        return date;
    }
    private static long? Number(JsonElement node, string name, bool nullable = false)
    {
        JsonElement value = node.GetProperty(name);
        if (nullable && value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out long result) || result < 0) throw Bad(TransferCatalogError.InvalidField);
        return result;
    }
    private static bool? Boolean(JsonElement node, string name, bool nullable = false)
    {
        JsonElement value = node.GetProperty(name);
        if (nullable && value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw Bad(TransferCatalogError.InvalidField);
        return value.GetBoolean();
    }
    private static JsonElement Array(JsonElement node, string name, int max)
    {
        JsonElement value = node.GetProperty(name);
        if (value.ValueKind != JsonValueKind.Array) throw Bad(TransferCatalogError.InvalidField);
        if (value.GetArrayLength() > max) throw Bad(TransferCatalogError.BudgetExceeded);
        return value;
    }
    private static void Fields(JsonElement node, params string[] fields)
    {
        if (node.ValueKind != JsonValueKind.Object) throw Bad(TransferCatalogError.InvalidField);
        HashSet<string> remaining = new(fields, StringComparer.Ordinal);
        foreach (JsonProperty field in node.EnumerateObject())
            if (!remaining.Remove(field.Name)) throw Bad(TransferCatalogError.InvalidField);
        if (remaining.Count != 0) throw Bad(TransferCatalogError.InvalidField);
    }
    private static bool LowerHash(string? value) => value is { Length: 64 } && value.All(c => c is >= 'a' and <= 'f' or >= '0' and <= '9');
    private static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();
    private static TransferCatalogInspection Fail(TransferCatalogError error) => new(null, error);
    private static CatalogException Bad(TransferCatalogError error) => new(error);
    private sealed class CatalogException(TransferCatalogError error) : Exception { internal TransferCatalogError Error => error; }
    private sealed class Budget
    {
        internal Dictionary<string, long> PayloadLengths { get; } = new(StringComparer.Ordinal);
        internal Budget(NativeTransferEnvelope envelope, CancellationToken ct)
        {
            foreach (TransferBlobDescriptor blob in envelope.Payloads)
            {
                ct.ThrowIfCancellationRequested();
                if (!PayloadLengths.TryAdd(blob.Sha256, blob.Length)) throw Bad(TransferCatalogError.InvalidReference);
            }
        }
        private int posts, attachments;
        internal void AddPost() { if (++posts > 100_000) throw Bad(TransferCatalogError.BudgetExceeded); }
        internal void AddAttachment() => AddAttachments(1);
        internal void AddAttachments(int count)
        {
            if (count < 0 || count > 100_000 - attachments) throw Bad(TransferCatalogError.BudgetExceeded);
            attachments += count;
        }
    }
}
