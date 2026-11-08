using System;
using System.Buffers;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MeowChan.Archive;
using MeowChan.Storage;

namespace MeowChan.Transfer;

internal enum NativeMediaMemberRole { OriginalEvidence, OriginalPayload, Preview, VideoPosterPng }
internal sealed record NativeMediaMemberId(MediaOccurrence Occurrence, TransferOriginalByteVersion? SourceVersion,
    NativeMediaMemberRole Role);
internal sealed record NativeMediaMemberDescriptor(NativeMediaMemberId Id, CapturedAttachment Metadata, bool Retained,
    TransferContentStatus Status, string? PayloadSha256, TransferHistoricalIntegrity? History,
    TransferMeasuredBlob? MeasuredPayload);
internal sealed record NativeMediaSelectedMember(NativeMediaMemberDescriptor Descriptor, bool ContextOnly)
{
    internal NativeMediaMemberId Id => Descriptor.Id;
}
internal sealed record NativeMediaCandidatePayload(string FileName, TransferMeasuredBlob Measured);
internal sealed record NativeMediaCandidateInventory(ImmutableArray<MediaAttachmentBinding> Bindings,
    ImmutableArray<MediaOriginalEvidence> OriginalEvidence, ImmutableArray<MediaDerivedRepresentation> DerivedRepresentations,
    ImmutableArray<NativeMediaCandidatePayload> Payloads, string Sha256);

/// <summary>
/// Immutable, exact descriptor selection, not an authority to read files or publish roots.
/// Only OriginalPayload materializes Original bytes/bindings. A contained evidence descriptor
/// (including a context parent) records the sender's observation, never local availability.
/// </summary>
internal sealed class NativeMediaCandidateSelection
{
    internal const int MaximumJsonBytes = 8 * 1024 * 1024;
    internal const int MaximumMembers = 100_000;
    private const long MaximumPayloadBytes = 1024L * 1024 * 1024;
    private const string TimeFormat = "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'";
    private static readonly Encoding Utf8 = new UTF8Encoding(false, true);
    private readonly ImmutableDictionary<NativeMediaMemberId, NativeMediaSelectedMember> byId;

    internal NativeMediaCandidateSelection(IEnumerable<NativeMediaSelectedMember> members)
    {
        ArgumentNullException.ThrowIfNull(members);
        var values = new List<NativeMediaSelectedMember>();
        var lookup = ImmutableDictionary.CreateBuilder<NativeMediaMemberId, NativeMediaSelectedMember>();
        var measurements = new Dictionary<string, TransferMeasuredBlob>(StringComparer.Ordinal);
        long budget = 0;
        ResourceId? resource = null;
        foreach (NativeMediaSelectedMember member in members)
        {
            if (values.Count == MaximumMembers || member == null || member.Descriptor == null) throw Invalid();
            ValidateDescriptor(member.Descriptor);
            if (member.Descriptor.MeasuredPayload is { } measured)
            {
                if (measurements.TryGetValue(measured.Sha256, out TransferMeasuredBlob? previous) && previous != measured) throw Invalid();
                measurements[measured.Sha256] = measured;
            }
            if (member.ContextOnly && member.Id.Role != NativeMediaMemberRole.OriginalEvidence
                || resource != null && resource != member.Id.Occurrence.Resource || lookup.ContainsKey(member.Id)) throw Invalid();
            resource ??= member.Id.Occurrence.Resource;
            // Charge actual encoded entries before accumulating an arbitrarily large input vector.
            budget = checked(budget + Write(w => WriteMember(w, member)).Length + 1L);
            if (budget > MaximumJsonBytes) throw Invalid();
            lookup.Add(member.Id, member);
            values.Add(member);
        }
        values.Sort((a, b) => CompareId(a.Id, b.Id));
        Members = values.ToImmutableArray();
        byId = lookup.ToImmutable();
        ValidateParents();
        byte[] bytes = Write(w =>
        {
            w.WriteStartObject(); w.WriteNumber("version", 2); w.WriteStartArray("members");
            foreach (NativeMediaSelectedMember member in Members) WriteMember(w, member);
            w.WriteEndArray(); w.WriteEndObject();
        });
        CanonicalJson = Utf8.GetString(bytes);
        Sha256 = Hash(bytes);
    }

    internal ImmutableArray<NativeMediaSelectedMember> Members { get; }
    internal string CanonicalJson { get; }
    internal string Sha256 { get; }
    internal bool IsEmpty => Members.IsEmpty;
    internal static NativeMediaCandidateSelection Create(IEnumerable<NativeMediaSelectedMember> members) => new(members);

    /// <summary>Select exact identities, adding only the exact missing parent as context.</summary>
    internal NativeMediaCandidateSelection Select(IEnumerable<NativeMediaMemberId> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        var selected = new Dictionary<NativeMediaMemberId, NativeMediaSelectedMember>();
        foreach (NativeMediaMemberId id in ids)
        {
            if (selected.Count == MaximumMembers || id == null || !byId.TryGetValue(id, out var member)
                || member.ContextOnly || !selected.TryAdd(id, member)) throw Invalid();
        }
        foreach (NativeMediaSelectedMember member in selected.Values.ToArray())
        {
            if (member.Id.Role == NativeMediaMemberRole.OriginalEvidence) continue;
            NativeMediaMemberId parent = ParentId(member.Id);
            if (!selected.ContainsKey(parent)) selected.Add(parent, byId[parent] with { ContextOnly = true });
        }
        var result = new NativeMediaCandidateSelection(selected.Values);
        ValidateSubset(result);
        return result;
    }

    /// <summary>
    /// Descriptor equality is structural and includes null SHA1, current/retained and measured
    /// payload. The sole allowed flag change is an unselected parent's ContextOnly false→true.
    /// </summary>
    internal void ValidateSubset(NativeMediaCandidateSelection selection)
    {
        ArgumentNullException.ThrowIfNull(selection);
        foreach (NativeMediaSelectedMember member in selection.Members)
        {
            if (!byId.TryGetValue(member.Id, out NativeMediaSelectedMember? full)
                || full.Descriptor != member.Descriptor || full.ContextOnly && !member.ContextOnly) throw Invalid();
        }
        // Constructor validates dependency/context closure; no hash-only equality shortcut.
    }

    internal bool IsSubsetOf(NativeMediaCandidateSelection full)
    {
        ArgumentNullException.ThrowIfNull(full);
        try { full.ValidateSubset(this); return true; }
        catch (InvalidDataException) { return false; }
    }

    internal static NativeMediaCandidateSelection Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        try
        {
            if (json.Length == 0 || json.Length > MaximumJsonBytes || Utf8.GetByteCount(json) > MaximumJsonBytes) throw Invalid();
            byte[] bytes = Utf8.GetBytes(json);
            // Reject escaped lone surrogates before GetString can replace them.
            var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { MaxDepth = 16 });
            while (reader.Read())
                if (reader.TokenType is JsonTokenType.PropertyName or JsonTokenType.String) ValidateEscapes(reader.ValueSpan);
            using JsonDocument document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 16 });
            JsonElement root = document.RootElement;
            Fields(root, "version", "members");
            if (Long(root, "version") != 2) throw Invalid();
            JsonElement array = root.GetProperty("members");
            if (array.ValueKind != JsonValueKind.Array || array.GetArrayLength() > MaximumMembers) throw Invalid();
            var members = ImmutableArray.CreateBuilder<NativeMediaSelectedMember>(array.GetArrayLength());
            foreach (JsonElement node in array.EnumerateArray()) members.Add(ReadMember(node));
            var selection = new NativeMediaCandidateSelection(members.MoveToImmutable());
            // There is one wire spelling: no alternate ordering, whitespace, number forms,
            // duplicate fields, hidden unknown fields or normalization of nullable evidence.
            if (!string.Equals(json, selection.CanonicalJson, StringComparison.Ordinal)) throw Invalid();
            return selection;
        }
        catch (Exception error) when (error is JsonException or ArgumentException or InvalidOperationException
            or FormatException or KeyNotFoundException or OverflowException)
        { throw new InvalidDataException("Invalid canonical passive media member selection.", error); }
    }

    private void ValidateParents()
    {
        var referenced = new HashSet<NativeMediaMemberId>();
        var occurrences = new HashSet<MediaOccurrence>();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (NativeMediaSelectedMember member in Members)
        {
            NativeMediaMemberDescriptor d = member.Descriptor;
            if (member.Id.Role == NativeMediaMemberRole.OriginalEvidence)
            {
                // Manifest6 admits one exact parent per occurrence/revision, not multiple
                // competing source tuples hidden behind distinct member IDs.
                if (!occurrences.Add(member.Id.Occurrence)
                    || member.Id.Occurrence.OriginalRevisionKey is { } key && !keys.Add(key)) throw Invalid();
                continue;
            }
            NativeMediaMemberId parentId = ParentId(member.Id);
            if (!byId.TryGetValue(parentId, out NativeMediaSelectedMember? parent)
                || parent.Descriptor.Metadata != d.Metadata || parent.Descriptor.Retained != d.Retained) throw Invalid();
            referenced.Add(parentId);
            if (member.Id.Role == NativeMediaMemberRole.OriginalPayload)
            {
                NativeMediaMemberDescriptor evidence = parent.Descriptor;
                if (evidence.Status != TransferContentStatus.Contained || evidence.History != d.History
                    || evidence.PayloadSha256 != null && (evidence.PayloadSha256 != d.PayloadSha256
                        || evidence.MeasuredPayload != d.MeasuredPayload)) throw Invalid();
            }
        }
        foreach (NativeMediaSelectedMember member in Members)
            if (member.ContextOnly && !referenced.Contains(member.Id)) throw Invalid();
    }

    internal NativeMediaCandidateInventory CreateInventory(Guid operationId, string fingerprint, DateTimeOffset importedAt)
    {
        if (operationId == Guid.Empty || !LowerHash(fingerprint) || importedAt.UtcDateTime == DateTime.MinValue) throw Invalid();
        importedAt = importedAt.ToUniversalTime();
        var origin = new MediaPassiveObservation(operationId.ToString("D"), fingerprint, importedAt);
        var bindings = ImmutableArray.CreateBuilder<MediaAttachmentBinding>();
        var evidence = ImmutableArray.CreateBuilder<MediaOriginalEvidence>();
        var derived = ImmutableArray.CreateBuilder<MediaDerivedRepresentation>();
        var payloads = new SortedDictionary<string, TransferMeasuredBlob>(StringComparer.Ordinal);
        foreach (NativeMediaSelectedMember member in Members)
        {
            NativeMediaMemberDescriptor d = member.Descriptor;
            MediaOccurrence occurrence = d.Id.Occurrence;
            if (d.Id.Role == NativeMediaMemberRole.OriginalEvidence)
            {
                evidence.Add(new(occurrence, d.Metadata, (MediaObservedContentStatus)d.Status,
                    d.Id.SourceVersion is { } source ? Actual(source) : null, origin));
                continue;
            }
            if (d.Id.Role == NativeMediaMemberRole.OriginalPayload)
            {
                // Evidence/derived records never enter this branch, regardless of status.
                TransferMeasuredBlob measured = d.MeasuredPayload!;
                MediaProviderMetadata provider = Provider(d.Metadata);
                string key = occurrence.OriginalRevisionKey!;
                string name = measured.Sha256 + ".original.bin";
                string asset = AssetId(key[7..] + ".original.bin", provider.MediaId, name, measured.Sha256);
                MediaActualIntegrity actual = Actual(measured);
                ProviderIntegrityAssessment assessment = ProviderIntegrityAssessment.Assess(provider, actual);
                MediaImportedHistory? history = d.History is not { } h ? null : new(
                    h.Kind == "strict" ? MediaAcceptanceKind.Strict : MediaAcceptanceKind.ExplicitProviderMismatch,
                    h.AcceptedAtUtc, h.SizeMismatch, h.Md5Mismatch);
                var binding = new MediaAttachmentBinding(new(occurrence.Resource, occurrence.PostId, occurrence.Position, key),
                    MediaRevisionKeys.CreateAcquisitionKey(occurrence.Resource, provider), asset, provider, actual,
                    new(MediaAcceptanceKind.PassiveImport, assessment.SizeMismatch, assessment.Md5Mismatch, importedAt))
                { PassiveImport = new(operationId.ToString("D"), fingerprint, importedAt, key, history) };
                MediaIntegrityValidation.ValidateBinding(key, binding);
                bindings.Add(binding);
                AddPayload(name, measured);
                continue;
            }
            MediaDerivedBlob? blob = d.MeasuredPayload is not { } bytes ? null
                : new(bytes.Sha256 + ".derived.bin", Actual(bytes));
            derived.Add(new(occurrence, Actual(d.Id.SourceVersion!),
                d.Id.Role == NativeMediaMemberRole.Preview ? MediaDerivedKind.Preview : MediaDerivedKind.VideoPosterPng,
                d.Status switch
                {
                    TransferContentStatus.Contained => MediaDerivedStatus.Contained,
                    TransferContentStatus.Omitted => MediaDerivedStatus.Omitted,
                    TransferContentStatus.Unavailable => MediaDerivedStatus.Unavailable,
                    _ => throw Invalid()
                }, blob, origin));
            if (blob != null) AddPayload(blob.RelativeFileName, d.MeasuredPayload!);
        }
        var inventory = new NativeMediaCandidateInventory(bindings.ToImmutable(), evidence.ToImmutable(), derived.ToImmutable(),
            payloads.Select(p => new NativeMediaCandidatePayload(p.Key, p.Value)).ToImmutableArray(), "");
        return inventory with { Sha256 = Hash(Write(w => WriteInventory(w, inventory), 32 * 1024 * 1024)) };

        void AddPayload(string name, TransferMeasuredBlob measured)
        {
            if (payloads.TryGetValue(name, out TransferMeasuredBlob? previous) && previous != measured) throw Invalid();
            payloads[name] = measured;
        }
    }

    private static void ValidateDescriptor(NativeMediaMemberDescriptor d)
    {
        if (d.Id == null || d.Id.Occurrence == null || d.Id.Occurrence.Resource == null || d.Metadata == null
            || !Enum.IsDefined(d.Id.Role) || !Enum.IsDefined(d.Status)) throw Invalid();
        MediaOccurrence o = d.Id.Occurrence;
        CapturedAttachment m = d.Metadata;
        Identifier(o.Resource.Source); Identifier(o.Resource.Board); Identifier(o.Resource.Thread); Identifier(o.PostId);
        if (o.Position < 0 || m.Position != o.Position || m.Variant != "original" || m.PlannedPath != null
            || m.AttachmentRevisionKey != o.OriginalRevisionKey || m.ExpectedSize < 0
            || m.MediaId == null && !m.IsDeleted) throw Invalid();
        if (m.MediaId != null) Identifier(m.MediaId);
        if (m.Url != null && (!BoundedText(m.Url) || !TransferPublicReferences.IsPublicUrl(m.Url))) throw Invalid();
        if (m.OriginalFileName != null && !SafeFileName(m.OriginalFileName)) throw Invalid();
        if (m.ExpectedMd5 != null) Digest(m.ExpectedMd5, 16, canonical: false);
        if (m.ExpectedSha1 != null) Digest(m.ExpectedSha1, 20, canonical: false);
        if (m.ExpectedSha256 != null && !MediaIntegrityValidation.IsSha256(m.ExpectedSha256)) throw Invalid();
        MediaIntegrityValidation.ValidateSizeHint(m.ProviderSizeHint);
        if (m.ProviderSizeHint is { } hint && (!BoundedText(hint.RawValue) || !BoundedText(hint.UnitLabel))) throw Invalid();
        if (o.OriginalRevisionKey is { } key && (m.MediaId == null || m.Url == null
            || key != MediaRevisionKeys.CreateAttachmentKey(o.Resource, o.PostId, o.Position, Provider(m)))) throw Invalid();
        if (d.Id.SourceVersion is { } version)
        {
            if (o.OriginalRevisionKey == null) throw Invalid();
            ValidateVersion(version);
        }
        if (d.Retained && (o.OriginalRevisionKey == null || m.MediaId == null || m.Url == null || m.IsDeleted
            || (d.Id.Role is NativeMediaMemberRole.OriginalEvidence or NativeMediaMemberRole.OriginalPayload)
                && d.Status is not (TransferContentStatus.Contained or TransferContentStatus.Omitted or TransferContentStatus.Unavailable))) throw Invalid();
        bool isEvidence = d.Id.Role == NativeMediaMemberRole.OriginalEvidence;
        bool isOriginal = d.Id.Role == NativeMediaMemberRole.OriginalPayload;
        bool isDerived = !isEvidence && !isOriginal;
        if (isOriginal && (d.Status != TransferContentStatus.Contained || o.OriginalRevisionKey == null)
            || isDerived && (d.Id.SourceVersion == null || d.History != null
                || d.Status is not (TransferContentStatus.Contained or TransferContentStatus.Omitted or TransferContentStatus.Unavailable))) throw Invalid();
        if (d.Status != TransferContentStatus.Contained)
        {
            if (d.PayloadSha256 != null || d.MeasuredPayload != null || d.History != null) throw Invalid();
        }
        else if (d.PayloadSha256 != null || d.MeasuredPayload != null || !isEvidence)
        {
            if (!LowerHash(d.PayloadSha256) || d.MeasuredPayload is not { } measured
                || measured.Sha256 != d.PayloadSha256) throw Invalid();
            ValidateMeasured(measured);
            if (!isDerived && d.Id.SourceVersion is { } source && (source.Length != measured.Length
                || source.Sha256 != measured.Sha256 || source.Md5 != measured.Md5
                || source.Sha1 != null && source.Sha1 != measured.Sha1)) throw Invalid();
        }
        if (d.History is { } history)
        {
            if (isDerived || o.OriginalRevisionKey == null || d.MeasuredPayload == null
                || history.Kind is not ("strict" or "provider-mismatch")
                || history.AcceptedAtUtc.UtcDateTime == DateTime.MinValue) throw Invalid();
            ProviderIntegrityAssessment a = ProviderIntegrityAssessment.Assess(Provider(m), Actual(d.MeasuredPayload));
            if (a.Sha1Mismatch || a.Sha256Mismatch || a.SizeMismatch != history.SizeMismatch
                || a.Md5Mismatch != history.Md5Mismatch || a.IsStrictMatch != (history.Kind == "strict")) throw Invalid();
        }
    }

    private static NativeMediaMemberId ParentId(NativeMediaMemberId id) => id with { Role = NativeMediaMemberRole.OriginalEvidence };
    private static MediaProviderMetadata Provider(CapturedAttachment m) => new(m.MediaId!, "original", m.Url!,
        m.OriginalFileName, m.ExpectedSize, m.ExpectedMd5, m.ExpectedSha256)
    { ExpectedSha1 = m.ExpectedSha1, ProviderSizeHint = m.ProviderSizeHint };
    private static MediaActualIntegrity Actual(TransferOriginalByteVersion v) => new(v.Length, v.Md5, v.Sha256) { Sha1 = v.Sha1 };
    private static MediaActualIntegrity Actual(TransferMeasuredBlob v) => new(v.Length, v.Md5, v.Sha256) { Sha1 = v.Sha1 };
    private static void ValidateVersion(TransferOriginalByteVersion v)
    {
        if (v.Length < 0 || !LowerHash(v.Sha256)) throw Invalid();
        Digest(v.Md5, 16); if (v.Sha1 != null) Digest(v.Sha1, 20);
    }
    private static void ValidateMeasured(TransferMeasuredBlob v)
    {
        if (v.Length < 0 || v.Length > MaximumPayloadBytes || !LowerHash(v.Sha256)) throw Invalid();
        Digest(v.Md5, 16); Digest(v.Sha1, 20);
    }
    private static void Digest(string? value, int length, bool canonical = true)
    {
        if (value == null || !BoundedText(value) || canonical && value.Length != ((length + 2) / 3) * 4) throw Invalid();
        Span<byte> decoded = stackalloc byte[20];
        if (!Convert.TryFromBase64String(value, decoded, out int count) || count != length
            || canonical && Convert.ToBase64String(decoded[..count]) != value) throw Invalid();
    }
    private static bool LowerHash(string? value) => value is { Length: 64 }
        && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static bool BoundedText(string value) => value.Length <= 1_048_576 && Utf8.GetByteCount(value) <= 1_048_576;
    private static void Identifier(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 256 || !BoundedText(value) || Controls(value)) throw Invalid();
    }
    private static bool Controls(string value) => value.EnumerateRunes().Any(r => Rune.GetUnicodeCategory(r)
        is UnicodeCategory.Control or UnicodeCategory.Format);
    private static bool SafeFileName(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 256 || value is "." or ".."
            || value.IndexOfAny(['/', '\\', ':']) >= 0 || !BoundedText(value) || Controls(value)) return false;
        string device = value.Split('.')[0].TrimEnd(' ').ToUpperInvariant();
        return device is not ("CON" or "PRN" or "AUX" or "NUL" or "CLOCK$")
            && !(device.Length == 4 && (device.StartsWith("COM", StringComparison.Ordinal) || device.StartsWith("LPT", StringComparison.Ordinal))
                && device[3] is >= '0' and <= '9' or '¹' or '²' or '³');
    }

    private static int CompareId(NativeMediaMemberId a, NativeMediaMemberId b)
    {
        int c = string.CompareOrdinal(a.Occurrence.Resource.Source, b.Occurrence.Resource.Source);
        if (c != 0) return c;
        c = string.CompareOrdinal(a.Occurrence.Resource.Board, b.Occurrence.Resource.Board); if (c != 0) return c;
        c = string.CompareOrdinal(a.Occurrence.Resource.Thread, b.Occurrence.Resource.Thread); if (c != 0) return c;
        c = string.CompareOrdinal(a.Occurrence.PostId, b.Occurrence.PostId); if (c != 0) return c;
        c = a.Occurrence.Position.CompareTo(b.Occurrence.Position); if (c != 0) return c;
        c = string.CompareOrdinal(a.Occurrence.OriginalRevisionKey, b.Occurrence.OriginalRevisionKey); if (c != 0) return c;
        if (a.SourceVersion == null || b.SourceVersion == null)
        {
            c = (a.SourceVersion != null).CompareTo(b.SourceVersion != null); if (c != 0) return c;
        }
        else
        {
            c = a.SourceVersion.Length.CompareTo(b.SourceVersion.Length); if (c != 0) return c;
            c = string.CompareOrdinal(a.SourceVersion.Sha256, b.SourceVersion.Sha256); if (c != 0) return c;
            c = string.CompareOrdinal(a.SourceVersion.Md5, b.SourceVersion.Md5); if (c != 0) return c;
            c = string.CompareOrdinal(a.SourceVersion.Sha1, b.SourceVersion.Sha1); if (c != 0) return c;
        }
        return a.Role.CompareTo(b.Role);
    }

    private static void WriteMember(Utf8JsonWriter w, NativeMediaSelectedMember member)
    {
        NativeMediaMemberDescriptor d = member.Descriptor;
        w.WriteStartObject(); w.WriteStartObject("descriptor"); w.WriteStartObject("id");
        w.WritePropertyName("occurrence"); WriteOccurrence(w, d.Id.Occurrence);
        w.WritePropertyName("sourceVersion"); WriteVersion(w, d.Id.SourceVersion);
        w.WriteNumber("role", (int)d.Id.Role); w.WriteEndObject();
        w.WritePropertyName("metadata"); WriteMetadata(w, d.Metadata);
        w.WriteBoolean("retained", d.Retained); w.WriteNumber("status", (int)d.Status);
        w.WriteString("payloadSha256", d.PayloadSha256);
        w.WritePropertyName("history"); WriteHistory(w, d.History);
        w.WritePropertyName("measuredPayload"); WriteMeasured(w, d.MeasuredPayload);
        w.WriteEndObject(); w.WriteBoolean("contextOnly", member.ContextOnly); w.WriteEndObject();
    }
    private static void WriteOccurrence(Utf8JsonWriter w, MediaOccurrence o)
    {
        w.WriteStartObject(); w.WriteStartObject("resource");
        w.WriteString("source", o.Resource.Source); w.WriteString("board", o.Resource.Board); w.WriteString("thread", o.Resource.Thread);
        w.WriteEndObject(); w.WriteString("postId", o.PostId); w.WriteNumber("position", o.Position);
        w.WriteString("originalRevisionKey", o.OriginalRevisionKey); w.WriteEndObject();
    }
    private static void WriteMetadata(Utf8JsonWriter w, CapturedAttachment m)
    {
        w.WriteStartObject(); w.WriteNumber("position", m.Position); w.WriteString("mediaId", m.MediaId);
        w.WriteString("variant", m.Variant); w.WriteString("url", m.Url); w.WriteString("originalFileName", m.OriginalFileName);
        w.WriteString("plannedPath", m.PlannedPath);
        if (m.ExpectedSize is { } size) w.WriteNumber("expectedSize", size); else w.WriteNull("expectedSize");
        w.WriteString("expectedMd5", m.ExpectedMd5); w.WriteBoolean("isDeleted", m.IsDeleted);
        w.WriteString("expectedSha256", m.ExpectedSha256); w.WriteString("expectedSha1", m.ExpectedSha1);
        w.WritePropertyName("providerSizeHint");
        if (m.ProviderSizeHint is not { } hint) w.WriteNullValue();
        else
        {
            w.WriteStartObject(); w.WriteString("rawValue", hint.RawValue); w.WriteString("unitLabel", hint.UnitLabel);
            w.WriteNumber("precision", (int)hint.Precision); w.WriteEndObject();
        }
        w.WriteString("attachmentRevisionKey", m.AttachmentRevisionKey); w.WriteEndObject();
    }
    private static void WriteVersion(Utf8JsonWriter w, TransferOriginalByteVersion? v)
    {
        if (v == null) { w.WriteNullValue(); return; }
        w.WriteStartObject(); w.WriteNumber("length", v.Length); w.WriteString("sha256", v.Sha256);
        w.WriteString("md5", v.Md5); w.WriteString("sha1", v.Sha1); w.WriteEndObject();
    }
    private static void WriteActual(Utf8JsonWriter w, MediaActualIntegrity? v)
        => WriteVersion(w, v == null ? null : new(v.Length, v.Sha256, v.Md5, v.Sha1));
    private static void WriteMeasured(Utf8JsonWriter w, TransferMeasuredBlob? v)
        => WriteVersion(w, v == null ? null : new(v.Length, v.Sha256, v.Md5, v.Sha1));
    private static void WriteHistory(Utf8JsonWriter w, TransferHistoricalIntegrity? h)
    {
        if (h == null) { w.WriteNullValue(); return; }
        w.WriteStartObject(); w.WriteString("kind", h.Kind); w.WriteString("acceptedAtUtc", Time(h.AcceptedAtUtc));
        w.WriteBoolean("sizeMismatch", h.SizeMismatch); w.WriteBoolean("md5Mismatch", h.Md5Mismatch); w.WriteEndObject();
    }
    private static void WriteOrigin(Utf8JsonWriter w, MediaPassiveObservation origin)
    {
        w.WriteStartObject(); w.WriteString("operationId", origin.OperationId);
        w.WriteString("packageFingerprint", origin.PackageFingerprint); w.WriteString("importedAtUtc", Time(origin.ImportedAtUtc));
        w.WriteEndObject();
    }
    private static void WriteInventory(Utf8JsonWriter w, NativeMediaCandidateInventory inventory)
    {
        // Hash the actual projected inventory, not a self-declared selection/inventory hash.
        // No paths, roots or process-local DTO identities are used as inventory evidence.
        w.WriteStartObject(); w.WriteNumber("version", 2); w.WriteStartArray("bindings");
        foreach (MediaAttachmentBinding b in inventory.Bindings)
        {
            w.WriteStartObject(); w.WritePropertyName("occurrence");
            WriteOccurrence(w, new(b.Reference.Resource, b.Reference.PostId, b.Reference.Position, b.Reference.RevisionKey));
            w.WriteString("acquisitionRevisionKey", b.AcquisitionRevisionKey); w.WriteString("assetId", b.AssetId);
            w.WritePropertyName("provider");
            WriteMetadata(w, new(b.Reference.Position, b.Provider.MediaId, b.Provider.Variant, b.Provider.Url,
                b.Provider.OriginalFileName, null, b.Provider.ExpectedSize, b.Provider.ExpectedMd5)
            { ExpectedSha256 = b.Provider.ExpectedSha256, ExpectedSha1 = b.Provider.ExpectedSha1,
                ProviderSizeHint = b.Provider.ProviderSizeHint, AttachmentRevisionKey = b.Reference.RevisionKey });
            w.WritePropertyName("actual"); WriteActual(w, b.Actual);
            w.WriteStartObject("acceptance"); w.WriteNumber("kind", (int)b.Acceptance.Kind);
            w.WriteBoolean("sizeMismatch", b.Acceptance.SizeMismatch); w.WriteBoolean("md5Mismatch", b.Acceptance.Md5Mismatch);
            w.WriteString("acceptedAtUtc", Time(b.Acceptance.AcceptedAtUtc));
            w.WriteString("profileId", b.Acceptance.ProfileId); w.WriteString("pluginOwnerId", b.Acceptance.PluginOwnerId);
            w.WriteString("sourceId", b.Acceptance.SourceId);
            if (b.Acceptance.PolicyRevision is { } revision) w.WriteNumber("policyRevision", revision); else w.WriteNull("policyRevision");
            w.WriteEndObject();
            MediaPassiveImportRecord import = b.PassiveImport!;
            w.WriteStartObject("passiveImport"); w.WriteString("operationId", import.OperationId);
            w.WriteString("packageFingerprint", import.PackageFingerprint); w.WriteString("importedAtUtc", Time(import.ImportedAtUtc));
            w.WriteString("incomingRevisionKey", import.IncomingRevisionKey); w.WritePropertyName("history");
            WriteHistory(w, import.History is not { } h ? null : new(h.Kind == MediaAcceptanceKind.Strict ? "strict" : "provider-mismatch",
                h.AcceptedAtUtc, h.SizeMismatch, h.Md5Mismatch));
            w.WriteEndObject(); w.WriteEndObject();
        }
        w.WriteEndArray(); w.WriteStartArray("originalEvidence");
        foreach (MediaOriginalEvidence e in inventory.OriginalEvidence)
        {
            w.WriteStartObject(); w.WritePropertyName("occurrence"); WriteOccurrence(w, e.Occurrence);
            w.WritePropertyName("metadata"); WriteMetadata(w, e.Metadata); w.WriteNumber("incomingStatus", (int)e.IncomingStatus);
            w.WritePropertyName("originalByteVersion"); WriteActual(w, e.OriginalByteVersion);
            w.WritePropertyName("origin"); WriteOrigin(w, e.Origin); w.WriteEndObject();
        }
        w.WriteEndArray(); w.WriteStartArray("derivedRepresentations");
        foreach (MediaDerivedRepresentation d in inventory.DerivedRepresentations)
        {
            w.WriteStartObject(); w.WritePropertyName("parent"); WriteOccurrence(w, d.Parent);
            w.WritePropertyName("sourceVersion"); WriteActual(w, d.SourceVersion); w.WriteNumber("kind", (int)d.Kind);
            w.WriteNumber("incomingStatus", (int)d.IncomingStatus); w.WritePropertyName("blob");
            if (d.Blob is not { } blob) w.WriteNullValue();
            else
            {
                w.WriteStartObject(); w.WriteString("relativeFileName", blob.RelativeFileName);
                w.WritePropertyName("actual"); WriteActual(w, blob.Actual); w.WriteEndObject();
            }
            w.WritePropertyName("origin"); WriteOrigin(w, d.Origin); w.WriteEndObject();
        }
        w.WriteEndArray(); w.WriteStartArray("payloads");
        foreach (NativeMediaCandidatePayload payload in inventory.Payloads)
        {
            w.WriteStartObject(); w.WriteString("fileName", payload.FileName); w.WritePropertyName("measured");
            WriteMeasured(w, payload.Measured); w.WriteEndObject();
        }
        w.WriteEndArray(); w.WriteEndObject();
    }
    private static string AssetId(string requestedName, string mediaId, string fileName, string hash)
    {
        // Exact existing MediaStore.GetAssetId original variant encoding (default JSON writer).
        return Hash(Write(w =>
        {
            w.WriteStartArray(); w.WriteStringValue(requestedName); w.WriteStringValue(mediaId);
            w.WriteStringValue(fileName); w.WriteStringValue(hash); w.WriteEndArray();
        }));
    }

    private static NativeMediaSelectedMember ReadMember(JsonElement node)
    {
        Fields(node, "descriptor", "contextOnly");
        JsonElement d = node.GetProperty("descriptor");
        Fields(d, "id", "metadata", "retained", "status", "payloadSha256", "history", "measuredPayload");
        JsonElement id = d.GetProperty("id"); Fields(id, "occurrence", "sourceVersion", "role");
        var identity = new NativeMediaMemberId(ReadOccurrence(id.GetProperty("occurrence")), ReadVersion(id.GetProperty("sourceVersion")),
            (NativeMediaMemberRole)Int(id, "role"));
        var descriptor = new NativeMediaMemberDescriptor(identity, ReadMetadata(d.GetProperty("metadata")), Bool(d, "retained"),
            (TransferContentStatus)Int(d, "status"), OptionalText(d, "payloadSha256"), ReadHistory(d.GetProperty("history")),
            ReadMeasured(d.GetProperty("measuredPayload")));
        return new(descriptor, Bool(node, "contextOnly"));
    }
    private static MediaOccurrence ReadOccurrence(JsonElement node)
    {
        Fields(node, "resource", "postId", "position", "originalRevisionKey");
        JsonElement r = node.GetProperty("resource"); Fields(r, "source", "board", "thread");
        return new(new(Text(r, "source"), Text(r, "board"), Text(r, "thread")), Text(node, "postId"),
            Int(node, "position"), OptionalText(node, "originalRevisionKey"));
    }
    private static CapturedAttachment ReadMetadata(JsonElement node)
    {
        Fields(node, "position", "mediaId", "variant", "url", "originalFileName", "plannedPath", "expectedSize",
            "expectedMd5", "isDeleted", "expectedSha256", "expectedSha1", "providerSizeHint", "attachmentRevisionKey");
        MediaProviderSizeHint? hint = null;
        JsonElement h = node.GetProperty("providerSizeHint");
        if (h.ValueKind != JsonValueKind.Null)
        {
            Fields(h, "rawValue", "unitLabel", "precision");
            hint = new(Text(h, "rawValue"), Text(h, "unitLabel"), (MediaSizePrecision)Int(h, "precision"));
        }
        return new(Int(node, "position"), OptionalText(node, "mediaId"), Text(node, "variant"), OptionalText(node, "url"),
            OptionalText(node, "originalFileName"), OptionalText(node, "plannedPath"),
            node.GetProperty("expectedSize").ValueKind == JsonValueKind.Null ? null : Long(node, "expectedSize"),
            OptionalText(node, "expectedMd5"), Bool(node, "isDeleted"))
        { ExpectedSha256 = OptionalText(node, "expectedSha256"), ExpectedSha1 = OptionalText(node, "expectedSha1"),
            ProviderSizeHint = hint, AttachmentRevisionKey = OptionalText(node, "attachmentRevisionKey") };
    }
    private static TransferOriginalByteVersion? ReadVersion(JsonElement node)
    {
        if (node.ValueKind == JsonValueKind.Null) return null;
        Fields(node, "length", "sha256", "md5", "sha1");
        return new(Long(node, "length"), Text(node, "sha256"), Text(node, "md5"), OptionalText(node, "sha1"));
    }
    private static TransferMeasuredBlob? ReadMeasured(JsonElement node)
    {
        if (node.ValueKind == JsonValueKind.Null) return null;
        Fields(node, "length", "sha256", "md5", "sha1");
        return new(Long(node, "length"), Text(node, "sha256"), Text(node, "md5"), Text(node, "sha1"));
    }
    private static TransferHistoricalIntegrity? ReadHistory(JsonElement node)
    {
        if (node.ValueKind == JsonValueKind.Null) return null;
        Fields(node, "kind", "acceptedAtUtc", "sizeMismatch", "md5Mismatch");
        if (!DateTimeOffset.TryParseExact(Text(node, "acceptedAtUtc"), TimeFormat, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTimeOffset time)) throw Invalid();
        return new(Text(node, "kind"), time, Bool(node, "sizeMismatch"), Bool(node, "md5Mismatch"));
    }
    private static void Fields(JsonElement value, params string[] names)
    {
        if (value.ValueKind != JsonValueKind.Object) throw Invalid();
        var remaining = new HashSet<string>(names, StringComparer.Ordinal);
        foreach (JsonProperty property in value.EnumerateObject()) if (!remaining.Remove(property.Name)) throw Invalid();
        if (remaining.Count != 0) throw Invalid();
    }
    private static string Text(JsonElement node, string name)
    {
        JsonElement value = node.GetProperty(name);
        if (value.ValueKind != JsonValueKind.String) throw Invalid();
        string text = value.GetString()!;
        if (!BoundedText(text)) throw Invalid();
        return text;
    }
    private static string? OptionalText(JsonElement node, string name)
        => node.GetProperty(name).ValueKind == JsonValueKind.Null ? null : Text(node, name);
    private static long Long(JsonElement node, string name)
    {
        JsonElement value = node.GetProperty(name);
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out long number)) throw Invalid();
        return number; // Re-encoding equality additionally rejects exponent/fraction/-0 spellings.
    }
    private static int Int(JsonElement node, string name) => checked((int)Long(node, name));
    private static bool Bool(JsonElement node, string name) => node.GetProperty(name).ValueKind switch
    { JsonValueKind.True => true, JsonValueKind.False => false, _ => throw Invalid() };
    private static void ValidateEscapes(ReadOnlySpan<byte> raw)
    {
        for (int i = 0; i < raw.Length; i++)
        {
            if (raw[i] != (byte)'\\') continue;
            if (++i >= raw.Length) throw Invalid();
            if (raw[i] != (byte)'u') continue;
            int code = Hex4(raw, i + 1); i += 4;
            if (code is >= 0xdc00 and <= 0xdfff) throw Invalid();
            if (code is >= 0xd800 and <= 0xdbff)
            {
                if (i + 6 >= raw.Length || raw[i + 1] != (byte)'\\' || raw[i + 2] != (byte)'u') throw Invalid();
                int low = Hex4(raw, i + 3);
                if (low is < 0xdc00 or > 0xdfff) throw Invalid();
                i += 6;
            }
        }
    }
    private static int Hex4(ReadOnlySpan<byte> raw, int start)
    {
        if (start > raw.Length - 4) throw Invalid();
        int code = 0;
        for (int i = start; i < start + 4; i++)
        {
            int nibble = raw[i] switch
            {
                >= (byte)'0' and <= (byte)'9' => raw[i] - '0',
                >= (byte)'a' and <= (byte)'f' => raw[i] - 'a' + 10,
                >= (byte)'A' and <= (byte)'F' => raw[i] - 'A' + 10,
                _ => throw Invalid()
            };
            code = (code << 4) | nibble;
        }
        return code;
    }
    private static string Time(DateTimeOffset value) => value.UtcDateTime.ToString(TimeFormat, CultureInfo.InvariantCulture);
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static InvalidDataException Invalid() => new("Invalid passive media member selection.");
    private static byte[] Write(Action<Utf8JsonWriter> action, int cap = MaximumJsonBytes)
    {
        using var output = new CappedBuffer(cap);
        using (var writer = new Utf8JsonWriter(output)) { action(writer); writer.Flush(); }
        return output.ToArray();
    }
    // Bounded while writing, not only after an unbounded JsonSerializer allocation.
    private sealed class CappedBuffer(int cap) : IBufferWriter<byte>, IDisposable
    {
        private readonly MemoryStream bytes = new();
        private byte[] scratch = Array.Empty<byte>();
        public Memory<byte> GetMemory(int sizeHint = 0)
        {
            if (sizeHint < 0 || sizeHint > cap) throw Invalid();
            int size = Math.Min(cap, Math.Max(4096, sizeHint));
            if (scratch.Length < size) scratch = new byte[size];
            return scratch;
        }
        public Span<byte> GetSpan(int sizeHint = 0) => GetMemory(sizeHint).Span;
        public void Advance(int count)
        {
            if (count < 0 || count > scratch.Length || bytes.Length + count > cap) throw Invalid();
            long length = bytes.Length + count;
            if (bytes.Capacity < length) bytes.Capacity = (int)Math.Min(cap, Math.Max(length, Math.Max(4096L, 2L * bytes.Capacity)));
            bytes.Write(scratch, 0, count);
        }
        internal byte[] ToArray() => bytes.ToArray();
        public void Dispose() => bytes.Dispose();
    }
}
