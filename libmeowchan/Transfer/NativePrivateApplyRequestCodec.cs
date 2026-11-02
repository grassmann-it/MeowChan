using System;
using System.Buffers;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace MeowChan.Transfer;

/// <summary>
/// Versioned selected-only local request encoding. Rehydration uses the existing
/// request validator/digests; neither JSON nor hashes confer store authority.
/// </summary>
internal static class NativePrivateApplyRequestCodec
{
    internal const int MaximumDocumentBytes = 20 * 1024 * 1024;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly JsonWriterOptions Options = new() { Indented = false, SkipValidation = false };

    internal static byte[] Serialize(NativePrivateApplyRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        byte[] selectedPayload = NativePrivateMetadataCodec.Serialize(request.SelectedIncoming, cancellationToken);
        using var output = new CappedWriter(cancellationToken, MaximumDocumentBytes);
        using (var writer = new Utf8JsonWriter(output, Options))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schema", 1);
            writer.WriteString("operationId", request.OperationId.ToString("N"));
            WriteBinding(writer, request.Binding);
            using JsonDocument payload = JsonDocument.Parse(selectedPayload);
            writer.WritePropertyName("payload");
            payload.RootElement.WriteTo(writer);
            WriteDispositions(writer, "threadDispositions", request.ThreadFavorites.Select(x => x.Disposition), cancellationToken);
            WriteDispositions(writer, "mediaDispositions", request.MediaFavorites.Select(x => x.Disposition), cancellationToken);
            WriteDispositions(writer, "bookmarkDispositions", request.PostBookmarksAndNotes.Select(x => x.Disposition), cancellationToken);
            writer.WritePropertyName("storeFences");
            writer.WriteStartArray();
            foreach (var fence in request.StoreFences)
            {
                cancellationToken.ThrowIfCancellationRequested();
                WriteFence(writer, fence);
            }
            writer.WriteEndArray();
            writer.WriteString("contentPlanSha256", request.ContentPlanSha256);
            writer.WriteString("requestSha256", request.RequestSha256);
            writer.WriteEndObject();
            writer.Flush();
        }
        cancellationToken.ThrowIfCancellationRequested();
        return output.ToArray();
    }

    internal static NativePrivateApplyRequest Deserialize(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (bytes.Length > MaximumDocumentBytes
            || bytes.Span.StartsWith(new byte[] { 0xef, 0xbb, 0xbf })) throw Invalid();
        try
        {
            _ = Utf8.GetCharCount(bytes.Span);
            cancellationToken.ThrowIfCancellationRequested();
            using JsonDocument doc = JsonDocument.Parse(bytes, new JsonDocumentOptions
            {
                MaxDepth = 16, AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow
            });
            JsonElement root = doc.RootElement;
            Fields(root, cancellationToken, "schema", "operationId", "binding", "payload",
                "threadDispositions", "mediaDispositions", "bookmarkDispositions", "storeFences",
                "contentPlanSha256", "requestSha256");
            if (Int(root, "schema") != 1) throw Invalid();
            Guid operation = ParseGuid(Text(root, "operationId"), compact: true);
            NativePrivateApplyRequestBinding binding = ParseBinding(root.GetProperty("binding"), cancellationToken);
            string payloadText = root.GetProperty("payload").GetRawText();
            if (Utf8.GetByteCount(payloadText) > NativePrivateMetadataCodec.MaximumDocumentBytes) throw Invalid();
            var inspection = NativePrivateMetadataCodec.Inspect(Utf8.GetBytes(payloadText), cancellationToken);
            if (!inspection.IsValid) throw Invalid();
            NativePrivateMetadataProjection selected = inspection.Projection!;
            var threads = ParseDispositions(root.GetProperty("threadDispositions"), selected.ThreadFavorites.Length, cancellationToken);
            var media = ParseDispositions(root.GetProperty("mediaDispositions"), selected.MediaFavorites.Length, cancellationToken);
            var notes = ParseDispositions(root.GetProperty("bookmarkDispositions"), selected.PostBookmarksAndNotes.Length, cancellationToken);
            var fences = ParseFences(root.GetProperty("storeFences"), cancellationToken);
            var comparison = new NativePrivateMetadataComparisonResult(selected.Selection, selected,
                selected.ThreadFavorites.Select((x, i) => new NativePrivateThreadFavoriteComparison(x, threads[i])).ToImmutableArray(),
                selected.MediaFavorites.Select((x, i) => new NativePrivateMediaFavoriteComparison(x, media[i])).ToImmutableArray(),
                selected.PostBookmarksAndNotes.Select((x, i) => new NativePrivateBookmarkComparison(x.Key, x.Note, notes[i])).ToImmutableArray());
            NativePrivateApplyRequest result = NativePrivateApplyRequest.Create(operation, comparison, binding, fences, cancellationToken);
            // Dispositions are aligned with the canonical payload identities, never
            // silently realigned to a differently ordered on-disk array.
            if (!NativePrivateMetadataCodec.Serialize(selected, cancellationToken).AsSpan().SequenceEqual(
                    NativePrivateMetadataCodec.Serialize(result.SelectedIncoming, cancellationToken))
                || !string.Equals(result.ContentPlanSha256, Text(root, "contentPlanSha256"), StringComparison.Ordinal)
                || !string.Equals(result.RequestSha256, Text(root, "requestSha256"), StringComparison.Ordinal)) throw Invalid();
            return result;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) when (error is JsonException or DecoderFallbackException or InvalidDataException
            or ArgumentException or InvalidOperationException or FormatException or OverflowException or KeyNotFoundException)
        { throw Invalid(); }
    }

    private static void WriteBinding(Utf8JsonWriter writer, NativePrivateApplyRequestBinding binding)
    {
        writer.WritePropertyName("binding");
        writer.WriteStartObject();
        writer.WriteNumber("packageLength", binding.PackageLength);
        writer.WriteString("packageSha256", binding.PackageSha256);
        writer.WriteString("publicCatalogSha256", binding.PublicCatalogSha256);
        writer.WriteNumber("privateLength", binding.PrivateLength);
        writer.WriteString("privateSha256", binding.PrivateSha256);
        writer.WriteString("profileIncarnation", binding.ProfileIncarnation.ToString("D"));
        writer.WriteString("targetFenceSha256", binding.TargetFenceSha256);
        writer.WriteEndObject();
    }

    private static void WriteDispositions(Utf8JsonWriter writer, string name,
        IEnumerable<NativePrivateMetadataDisposition> values, CancellationToken token)
    {
        writer.WritePropertyName(name);
        writer.WriteStartArray();
        foreach (var value in values)
        {
            token.ThrowIfCancellationRequested();
            writer.WriteNumberValue((int)value);
        }
        writer.WriteEndArray();
    }

    private static void WriteFence(Utf8JsonWriter writer, NativePrivateApplyStoreFence fence)
    {
        writer.WriteStartObject();
        writer.WriteNumber("kind", (int)fence.Kind);
        writer.WritePropertyName("before"); WriteImage(writer, fence.Before);
        writer.WritePropertyName("expectedAfter"); WriteImage(writer, fence.ExpectedAfter);
        writer.WritePropertyName("beforeBackup");
        if (fence.BeforeBackup is null) writer.WriteNullValue();
        else WriteImage(writer, fence.BeforeBackup);
        writer.WriteEndObject();
    }

    private static void WriteImage(Utf8JsonWriter writer, NativePrivateApplyStoreImage image)
    {
        writer.WriteStartObject();
        writer.WriteBoolean("exists", image.Exists);
        writer.WriteNumber("schema", image.Schema);
        writer.WriteString("instance", image.Instance?.ToString("D"));
        writer.WriteNumber("revision", image.Revision);
        writer.WriteNumber("byteLength", image.ByteLength);
        writer.WriteString("sha256", image.Sha256);
        writer.WriteEndObject();
    }

    private static NativePrivateApplyRequestBinding ParseBinding(JsonElement element, CancellationToken token)
    {
        Fields(element, token, "packageLength", "packageSha256", "publicCatalogSha256",
            "privateLength", "privateSha256", "profileIncarnation", "targetFenceSha256");
        return new(Long(element, "packageLength"), Text(element, "packageSha256"),
            Text(element, "publicCatalogSha256"), Long(element, "privateLength"),
            Text(element, "privateSha256"), ParseGuid(Text(element, "profileIncarnation"), compact: false),
            Text(element, "targetFenceSha256"));
    }

    private static ImmutableArray<NativePrivateMetadataDisposition> ParseDispositions(
        JsonElement element, int count, CancellationToken token)
    {
        if (element.ValueKind != JsonValueKind.Array || element.GetArrayLength() != count) throw Invalid();
        var result = ImmutableArray.CreateBuilder<NativePrivateMetadataDisposition>(count);
        foreach (var item in element.EnumerateArray())
        {
            token.ThrowIfCancellationRequested();
            int value = Int(item);
            if (!Enum.IsDefined((NativePrivateMetadataDisposition)value)) throw Invalid();
            result.Add((NativePrivateMetadataDisposition)value);
        }
        return result.ToImmutable();
    }

    private static ImmutableArray<NativePrivateApplyStoreFence> ParseFences(JsonElement element, CancellationToken token)
    {
        if (element.ValueKind != JsonValueKind.Array || element.GetArrayLength() > 3) throw Invalid();
        var result = ImmutableArray.CreateBuilder<NativePrivateApplyStoreFence>();
        foreach (var item in element.EnumerateArray())
        {
            Fields(item, token, "kind", "before", "expectedAfter", "beforeBackup");
            result.Add(new((NativePrivateApplyStoreKind)Int(item, "kind"),
                ParseImage(item.GetProperty("before"), token), ParseImage(item.GetProperty("expectedAfter"), token),
                item.GetProperty("beforeBackup").ValueKind == JsonValueKind.Null ? null
                    : ParseImage(item.GetProperty("beforeBackup"), token)));
        }
        return result.ToImmutable();
    }

    private static NativePrivateApplyStoreImage ParseImage(JsonElement element, CancellationToken token)
    {
        Fields(element, token, "exists", "schema", "instance", "revision", "byteLength", "sha256");
        string? instance = NullableText(element, "instance");
        return new(Bool(element, "exists"), Int(element, "schema"),
            instance is null ? null : ParseGuid(instance, compact: false), Long(element, "revision"),
            Long(element, "byteLength"), NullableText(element, "sha256"));
    }

    private static void Fields(JsonElement element, CancellationToken token, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object) throw Invalid();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            token.ThrowIfCancellationRequested();
            if (!seen.Add(property.Name) || !names.Contains(property.Name, StringComparer.Ordinal)) throw Invalid();
        }
        if (seen.Count != names.Length) throw Invalid();
    }

    private static string Text(JsonElement element, string name)
        => element.GetProperty(name).ValueKind == JsonValueKind.String ? element.GetProperty(name).GetString()! : throw Invalid();
    private static string? NullableText(JsonElement element, string name)
        => element.GetProperty(name).ValueKind == JsonValueKind.Null ? null : Text(element, name);
    private static long Long(JsonElement element, string name)
        => element.GetProperty(name).TryGetInt64(out long value) ? value : throw Invalid();
    private static int Int(JsonElement element, string name) => Int(element.GetProperty(name));
    private static int Int(JsonElement element) => element.TryGetInt32(out int value) ? value : throw Invalid();
    private static bool Bool(JsonElement element, string name)
        => element.GetProperty(name).ValueKind is JsonValueKind.True or JsonValueKind.False
            ? element.GetProperty(name).GetBoolean() : throw Invalid();

    private static Guid ParseGuid(string text, bool compact)
    {
        if (!Guid.TryParseExact(text, compact ? "N" : "D", out Guid value) || value == Guid.Empty) throw Invalid();
        return value;
    }

    private static InvalidDataException Invalid() => new("The private apply request codec input is invalid.");

    private sealed class CappedWriter(CancellationToken token, int cap) : IBufferWriter<byte>, IDisposable
    {
        private readonly MemoryStream _stream = new();
        private byte[] _scratch = Array.Empty<byte>();
        public Memory<byte> GetMemory(int sizeHint = 0)
        {
            token.ThrowIfCancellationRequested();
            if (sizeHint < 0 || sizeHint > cap) throw Invalid();
            int required = Math.Max(4096, sizeHint);
            if (_scratch.Length < required) _scratch = new byte[required];
            return _scratch;
        }
        public Span<byte> GetSpan(int sizeHint = 0) => GetMemory(sizeHint).Span;
        public void Advance(int count)
        {
            token.ThrowIfCancellationRequested();
            if (count < 0 || count > _scratch.Length || count > cap - _stream.Length) throw Invalid();
            int required = checked((int)_stream.Length + count);
            if (_stream.Capacity < required)
                _stream.Capacity = Math.Min(cap, Math.Max(required, Math.Max(4096, _stream.Capacity * 2)));
            _stream.Write(_scratch, 0, count);
        }
        public byte[] ToArray() => _stream.ToArray();
        public void Dispose() => _stream.Dispose();
    }
}
