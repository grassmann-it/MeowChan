using System;
using System.IO;
using System.Text.Json;
using System.Threading;

namespace MeowChan.Storage;

/// <summary>
/// Checks only the passive inventory's JSON shape before typed deserialization.
/// Existing manifest fields and all inventory semantics are validated by the caller.
/// </summary>
internal static class MediaRepresentationWire
{
    private const int MaximumRecords = 100000;
    private static readonly string[] EvidenceFields =
        ["Occurrence", "Metadata", "IncomingStatus", "OriginalByteVersion", "Origin"];
    private static readonly string[] DerivedFields =
        ["Parent", "SourceVersion", "Kind", "IncomingStatus", "Blob", "Origin"];
    private static readonly string[] OccurrenceFields =
        ["Resource", "PostId", "Position", "OriginalRevisionKey"];
    private static readonly string[] ResourceFields = ["Source", "Board", "Thread"];
    private static readonly string[] MetadataFields =
        ["Position", "MediaId", "Variant", "Url", "OriginalFileName", "PlannedPath",
         "ExpectedSize", "ExpectedMd5", "IsDeleted", "ExpectedSha256", "ExpectedSha1",
         "ProviderSizeHint", "AttachmentRevisionKey"];
    private static readonly string[] SizeHintFields = ["RawValue", "UnitLabel", "Precision"];
    private static readonly string[] IntegrityFields = ["Length", "Md5", "Sha256", "Sha1"];
    private static readonly string[] OriginFields = ["OperationId", "PackageFingerprint", "ImportedAtUtc"];
    private static readonly string[] BlobFields = ["RelativeFileName", "Actual"];

    internal static void Validate(JsonElement root, int sourceVersion, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (root.ValueKind != JsonValueKind.Object) throw Invalid();
        bool hasEvidence = root.TryGetProperty("OriginalEvidence", out JsonElement evidence);
        bool hasDerived = root.TryGetProperty("DerivedRepresentations", out JsonElement derived);
        if (sourceVersion != 6)
        {
            // Do not let legacy projection silently erase even empty/null inventories.
            if (hasEvidence || hasDerived) throw Invalid();
            return;
        }
        // Unsupported future versions are rejected by the caller before this check.
        if (!hasEvidence || !hasDerived || evidence.ValueKind != JsonValueKind.Array
            || derived.ValueKind != JsonValueKind.Array) throw Invalid();
        if ((long)evidence.GetArrayLength() + derived.GetArrayLength() > MaximumRecords) throw Invalid();
        foreach (JsonElement item in evidence.EnumerateArray())
        {
            Fields(item, EvidenceFields, ct);
            Occurrence(item.GetProperty("Occurrence"), ct);
            Metadata(item.GetProperty("Metadata"), ct);
            Int(item.GetProperty("IncomingStatus"));
            JsonElement original = item.GetProperty("OriginalByteVersion");
            if (original.ValueKind != JsonValueKind.Null) Integrity(original, ct);
            Origin(item.GetProperty("Origin"), ct);
        }
        foreach (JsonElement item in derived.EnumerateArray())
        {
            Fields(item, DerivedFields, ct);
            Occurrence(item.GetProperty("Parent"), ct);
            Integrity(item.GetProperty("SourceVersion"), ct);
            Int(item.GetProperty("Kind"));
            Int(item.GetProperty("IncomingStatus"));
            JsonElement blob = item.GetProperty("Blob");
            if (blob.ValueKind != JsonValueKind.Null)
            {
                Fields(blob, BlobFields, ct);
                Text(blob.GetProperty("RelativeFileName"));
                Integrity(blob.GetProperty("Actual"), ct);
            }
            Origin(item.GetProperty("Origin"), ct);
        }
        ct.ThrowIfCancellationRequested();
    }

    internal static bool HasReadableProperty(ReadOnlySpan<byte> bytes, CancellationToken ct)
    {
        // Called only after a syntax failure. Any recoverable property is enough
        // to deny automatic repair, even if it precedes the schema/inventory keys.
        // Do not guess JSON identity with a text search or recover a partial DTO.
        // Unstructured legacy garbage retains its existing backup/rebuild path.
        Utf8JsonReader reader = new(bytes);
        try
        {
            while (reader.Read())
            {
                ct.ThrowIfCancellationRequested();
                if (reader.TokenType == JsonTokenType.PropertyName) return true;
            }
        }
        catch (JsonException) { }
        ct.ThrowIfCancellationRequested();
        return false;
    }

    private static void Occurrence(JsonElement value, CancellationToken ct)
    {
        Fields(value, OccurrenceFields, ct);
        JsonElement resource = value.GetProperty("Resource");
        Fields(resource, ResourceFields, ct);
        Text(resource.GetProperty("Source"));
        Text(resource.GetProperty("Board"));
        NullableText(resource.GetProperty("Thread"));
        Text(value.GetProperty("PostId"));
        Int(value.GetProperty("Position"));
        NullableText(value.GetProperty("OriginalRevisionKey"));
    }

    private static void Metadata(JsonElement value, CancellationToken ct)
    {
        Fields(value, MetadataFields, ct);
        Int(value.GetProperty("Position"));
        NullableText(value.GetProperty("MediaId"));
        Text(value.GetProperty("Variant"));
        NullableText(value.GetProperty("Url"));
        NullableText(value.GetProperty("OriginalFileName"));
        NullableText(value.GetProperty("PlannedPath"));
        JsonElement size = value.GetProperty("ExpectedSize");
        if (size.ValueKind != JsonValueKind.Null) Long(size);
        NullableText(value.GetProperty("ExpectedMd5"));
        JsonValueKind deleted = value.GetProperty("IsDeleted").ValueKind;
        if (deleted is not (JsonValueKind.True or JsonValueKind.False)) throw Invalid();
        NullableText(value.GetProperty("ExpectedSha256"));
        NullableText(value.GetProperty("ExpectedSha1"));
        NullableText(value.GetProperty("AttachmentRevisionKey"));
        JsonElement hint = value.GetProperty("ProviderSizeHint");
        if (hint.ValueKind != JsonValueKind.Null)
        {
            Fields(hint, SizeHintFields, ct);
            Text(hint.GetProperty("RawValue"));
            Text(hint.GetProperty("UnitLabel"));
            Int(hint.GetProperty("Precision"));
        }
    }

    private static void Integrity(JsonElement value, CancellationToken ct)
    {
        Fields(value, IntegrityFields, ct);
        Long(value.GetProperty("Length"));
        Text(value.GetProperty("Md5"));
        Text(value.GetProperty("Sha256"));
        NullableText(value.GetProperty("Sha1"));
    }

    private static void Origin(JsonElement value, CancellationToken ct)
    {
        Fields(value, OriginFields, ct);
        Text(value.GetProperty("OperationId"));
        Text(value.GetProperty("PackageFingerprint"));
        JsonElement time = value.GetProperty("ImportedAtUtc");
        if (time.ValueKind != JsonValueKind.String || !time.TryGetDateTimeOffset(out _)) throw Invalid();
    }

    private static void Fields(JsonElement value, string[] names, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (value.ValueKind != JsonValueKind.Object) throw Invalid();
        // Every field is required, including nullable fields. The bitset also fails
        // closed on duplicates if this helper is ever used without the outer walker.
        uint seen = 0;
        foreach (JsonProperty property in value.EnumerateObject())
        {
            ct.ThrowIfCancellationRequested();
            int index = Array.IndexOf(names, property.Name);
            if (index < 0 || (seen & (1u << index)) != 0) throw Invalid();
            seen |= 1u << index;
        }
        if (seen != (1u << names.Length) - 1) throw Invalid();
    }

    private static void Text(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.String) throw Invalid();
    }

    private static void NullableText(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Null) Text(value);
    }

    private static void Int(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out _)) throw Invalid();
    }

    private static void Long(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out _)) throw Invalid();
    }

    private static InvalidDataException Invalid() => new("Invalid passive media inventory shape.");
}
