using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Text;
using System.Text.Json;

namespace MeowChan.Interop.Sidecars;

internal enum SidecarProfile { HydrusJsonTagsV1, GalleryDlDanbooruIdTagsV1, GrabberUnsupported }
internal enum SidecarDirection { Import, Export }
internal enum SidecarError
{
    None, UnsupportedProfile, UnsupportedDirection, PayloadTooLarge, InvalidUtf8,
    InvalidUnicode, BomNotAllowed, InvalidJson, DuplicateProperty, InvalidSchema,
    TooManyTags, TagTooLarge
}

// No raw input, exception messages, paths or foreign property names in diagnostics.
internal sealed record SidecarParseResult(SidecarError Error, ImmutableArray<string> Tags, long? ClaimId)
{
    internal bool Success => Error == SidecarError.None;
}

internal sealed record SidecarExportResult(SidecarError Error, ImmutableArray<byte> Utf8);

/// <summary>
/// Pure single-sidecar codec. Local contract: 1 MiB input/output, JSON depth 8,
/// 4096 tags, 4096 UTF-8 bytes per decoded tag. No BOM, null, extra/duplicate keys,
/// malformed Unicode, comments or trailing values. Tags retain ordinal order and duplicates.
/// Pair selection (including its 1000-pair budget), provenance and storage are outside this codec.
/// </summary>
internal static class PassiveSidecarCodec
{
    internal const int MaxPayloadBytes = 1024 * 1024;
    internal const int MaxDepth = 8;
    internal const int MaxTags = 4096;
    internal const int MaxTagBytes = 4096;
    internal const long MaxClaimId = 9007199254740991;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    private static SidecarParseResult Fail(SidecarError error) => new(error, ImmutableArray<string>.Empty, null);

    private static SidecarError CheckDirection(SidecarProfile profile, SidecarDirection direction)
    {
        if (profile != SidecarProfile.HydrusJsonTagsV1 && profile != SidecarProfile.GalleryDlDanbooruIdTagsV1)
            return SidecarError.UnsupportedProfile;
        if (direction != SidecarDirection.Import && direction != SidecarDirection.Export)
            return SidecarError.UnsupportedDirection;
        if (profile == SidecarProfile.GalleryDlDanbooruIdTagsV1 && direction == SidecarDirection.Export)
            return SidecarError.UnsupportedDirection;
        return SidecarError.None;
    }

    internal static SidecarParseResult Parse(ReadOnlyMemory<byte> utf8, SidecarProfile profile,
        SidecarDirection direction = SidecarDirection.Import)
    {
        SidecarError error = CheckDirection(profile, direction);
        if (error != SidecarError.None) return Fail(error);
        if (utf8.Length > MaxPayloadBytes) return Fail(SidecarError.PayloadTooLarge);
        // Bound before allocating, then validate and materialize only this owned image.
        // Copying is not an atomic observation of concurrently modified caller memory.
        byte[] snapshot = utf8.ToArray();
        ReadOnlySpan<byte> span = snapshot;
        if (span.StartsWith(new byte[] { 0xef, 0xbb, 0xbf })) return Fail(SidecarError.BomNotAllowed);
        try { _ = StrictUtf8.GetCharCount(span); }
        catch (DecoderFallbackException) { return Fail(SidecarError.InvalidUtf8); }
        try
        {
            // Inspect every token before schema dispatch, even late unknown property names.
            // GetString alone is not an assurance against replacement of escaped lone surrogates.
            var reader = new Utf8JsonReader(span, new JsonReaderOptions { MaxDepth = MaxDepth });
            var objects = new Stack<HashSet<string>>();
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.StartObject) objects.Push(new(StringComparer.Ordinal));
                else if (reader.TokenType == JsonTokenType.EndObject) objects.Pop();
                else if (reader.TokenType is JsonTokenType.String or JsonTokenType.PropertyName)
                {
                    if (!ValidEscapedUnicode(reader.ValueSpan)) return Fail(SidecarError.InvalidUnicode);
                    if (reader.TokenType == JsonTokenType.PropertyName && !objects.Peek().Add(reader.GetString()!))
                        return Fail(SidecarError.DuplicateProperty);
                }
            }
            using JsonDocument document = JsonDocument.Parse(snapshot, new JsonDocumentOptions { MaxDepth = MaxDepth });
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return Fail(SidecarError.InvalidSchema);
            JsonElement tags;
            long? claimId = null;
            if (profile == SidecarProfile.HydrusJsonTagsV1)
            {
                if (!HasExactly(root, "metadata") || !root.TryGetProperty("metadata", out var metadata) ||
                    metadata.ValueKind != JsonValueKind.Object || !HasExactly(metadata, "tags") ||
                    !metadata.TryGetProperty("tags", out tags)) return Fail(SidecarError.InvalidSchema);
            }
            else
            {
                if (!HasExactly(root, "id", "tags") || !root.TryGetProperty("id", out var id) ||
                    id.ValueKind != JsonValueKind.Number || !id.TryGetInt64(out long number) ||
                    number < 1 || number > MaxClaimId || !root.TryGetProperty("tags", out tags))
                    return Fail(SidecarError.InvalidSchema);
                claimId = number;
            }
            if (tags.ValueKind != JsonValueKind.Array) return Fail(SidecarError.InvalidSchema);
            if (tags.GetArrayLength() > MaxTags) return Fail(SidecarError.TooManyTags);
            var result = ImmutableArray.CreateBuilder<string>(tags.GetArrayLength());
            foreach (JsonElement tag in tags.EnumerateArray())
            {
                if (tag.ValueKind != JsonValueKind.String) return Fail(SidecarError.InvalidSchema);
                string value = tag.GetString()!;
                if (StrictUtf8.GetByteCount(value) > MaxTagBytes) return Fail(SidecarError.TagTooLarge);
                result.Add(value);
            }
            return new(SidecarError.None, result.ToImmutable(), claimId);
        }
        catch (JsonException) { return Fail(SidecarError.InvalidJson); }
        catch (DecoderFallbackException) { return Fail(SidecarError.InvalidUtf8); }
        catch (EncoderFallbackException) { return Fail(SidecarError.InvalidUnicode); }
    }

    private static bool HasExactly(JsonElement element, params string[] names)
    {
        int count = 0;
        foreach (JsonProperty property in element.EnumerateObject())
        {
            bool known = false;
            foreach (string name in names) if (property.NameEquals(name)) { known = true; break; }
            if (!known) return false;
            count++;
        }
        return count == names.Length;
    }

    private static bool ValidEscapedUnicode(ReadOnlySpan<byte> token)
    {
        for (int i = 0; i < token.Length; i++)
        {
            if (token[i] != (byte)'\\') continue;
            if (++i >= token.Length) return false;
            if (token[i] != (byte)'u') continue; // escaped backslashes skip their second byte
            int unit = HexUnit(token, i + 1);
            if (unit < 0) return false;
            i += 4;
            if (unit >= 0xdc00 && unit <= 0xdfff) return false;
            if (unit < 0xd800 || unit > 0xdbff) continue;
            if (i + 6 >= token.Length || token[i + 1] != (byte)'\\' || token[i + 2] != (byte)'u') return false;
            int low = HexUnit(token, i + 3);
            if (low < 0xdc00 || low > 0xdfff) return false;
            i += 6;
        }
        return true;
    }

    private static int HexUnit(ReadOnlySpan<byte> token, int start)
    {
        if (start + 4 > token.Length) return -1;
        int value = 0;
        for (int j = start; j < start + 4; j++)
        {
            int b = token[j];
            int digit = b >= '0' && b <= '9' ? b - '0' : b >= 'a' && b <= 'f' ? b - 'a' + 10 :
                b >= 'A' && b <= 'F' ? b - 'A' + 10 : -1;
            if (digit < 0) return -1;
            value = value * 16 + digit;
        }
        return value;
    }

    // Caller must explicitly choose tags; no model/private-field discovery or automatic mapping.
    // Fixed writer options/order yield deterministic compact JSON; exact strings are not normalized.
    internal static SidecarExportResult SerializeChosenTags(IReadOnlyList<string>? chosenTags,
        SidecarProfile profile = SidecarProfile.HydrusJsonTagsV1,
        SidecarDirection direction = SidecarDirection.Export)
    {
        SidecarExportResult Failed(SidecarError e) => new(e, ImmutableArray<byte>.Empty);
        SidecarError error = CheckDirection(profile, direction);
        if (error != SidecarError.None) return Failed(error);
        if (direction != SidecarDirection.Export || profile != SidecarProfile.HydrusJsonTagsV1)
            return Failed(SidecarError.UnsupportedDirection);
        if (chosenTags is null) return Failed(SidecarError.InvalidSchema);
        if (chosenTags.Count > MaxTags) return Failed(SidecarError.TooManyTags);
        // Snapshot the explicit input before writing; never allow UTF-16 replacement during encoding.
        var snapshot = new List<string>(chosenTags.Count);
        try
        {
            foreach (string tag in chosenTags)
            {
                if (tag is null) return Failed(SidecarError.InvalidSchema);
                if (StrictUtf8.GetByteCount(tag) > MaxTagBytes) return Failed(SidecarError.TagTooLarge);
                snapshot.Add(tag);
                if (snapshot.Count > MaxTags) return Failed(SidecarError.TooManyTags);
            }
        }
        catch (EncoderFallbackException) { return Failed(SidecarError.InvalidUnicode); }
        using var output = new MemoryStream(); // in-memory only, no filesystem access
        using (var writer = new Utf8JsonWriter(output))
        {
            writer.WriteStartObject(); writer.WriteStartObject("metadata"); writer.WriteStartArray("tags");
            foreach (string tag in snapshot)
            {
                writer.WriteStringValue(tag);
                // Flush for a bounded output check; at most one bounded tag may overshoot.
                writer.Flush();
                if (output.Length > MaxPayloadBytes) return Failed(SidecarError.PayloadTooLarge);
            }
            writer.WriteEndArray(); writer.WriteEndObject(); writer.WriteEndObject();
        }
        byte[] bytes = output.ToArray();
        SidecarParseResult validation = Parse(bytes, profile, SidecarDirection.Export);
        return validation.Success ? new(SidecarError.None, ImmutableArray.CreateRange(bytes)) : Failed(validation.Error);
    }
}
