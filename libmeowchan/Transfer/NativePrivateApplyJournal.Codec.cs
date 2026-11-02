using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace MeowChan.Transfer;

internal sealed partial class NativePrivateApplyJournal
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    private sealed record JournalClaim(Guid OperationId, string RequestSha256, string ContentPlanSha256,
        string RequestBytesSha256, int RequestLength, string SelectedPayloadSha256, int SelectedPayloadLength,
        NativePrivateMetadataSelection Selection)
    {
        internal static JournalClaim From(NativePrivateApplyRequest request, byte[] bytes)
            => new(request.OperationId, request.RequestSha256, request.ContentPlanSha256, Hash(bytes), bytes.Length,
                request.SelectedPayloadSha256, request.SelectedPayloadLength, request.Selection);
    }
    private sealed record JournalEvent(Guid OperationId, string RequestSha256, int Sequence, string PreviousHash,
        NativePrivateApplyOperationObservation OperationObservation, ImmutableArray<NativePrivateApplyStepObservation> Steps,
        NativePrivateApplyTerminalReceipt? Receipt);
    private sealed record JournalReceipt(NativePrivateApplyTerminalReceipt Value, int EventCount, string LastHash);

    private static byte[] WriteJson(Action<Utf8JsonWriter> write)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) { write(writer); writer.Flush(); }
        byte[] bytes = stream.ToArray(); CheckRecordSize(bytes); return bytes;
    }

    private static void WriteIdentity(Utf8JsonWriter writer, Guid operation, string requestHash)
    {
        writer.WriteNumber("schema", 1);
        writer.WriteString("operationId", operation.ToString("N"));
        writer.WriteString("requestSha256", requestHash);
    }

    private static byte[] SerializeClaim(JournalClaim value) => WriteJson(writer =>
    {
        writer.WriteStartObject(); WriteIdentity(writer, value.OperationId, value.RequestSha256);
        writer.WriteString("contentPlanSha256", value.ContentPlanSha256);
        writer.WriteString("requestBytesSha256", value.RequestBytesSha256); writer.WriteNumber("requestLength", value.RequestLength);
        writer.WriteString("selectedPayloadSha256", value.SelectedPayloadSha256); writer.WriteNumber("selectedPayloadLength", value.SelectedPayloadLength);
        WriteSelection(writer, value.Selection); writer.WriteEndObject();
    });

    private static byte[] SerializeEvent(JournalEvent value) => WriteJson(writer =>
    {
        writer.WriteStartObject(); WriteIdentity(writer, value.OperationId, value.RequestSha256);
        writer.WriteNumber("sequence", value.Sequence); writer.WriteString("previousRecordSha256", value.PreviousHash);
        writer.WriteNumber("operationObservation", (int)value.OperationObservation);
        writer.WritePropertyName("steps"); writer.WriteStartArray();
        foreach (NativePrivateApplyStepObservation step in value.Steps) writer.WriteNumberValue((int)step);
        writer.WriteEndArray(); writer.WritePropertyName("receipt");
        if (value.Receipt is null) writer.WriteNullValue(); else WriteReceiptBody(writer, value.Receipt);
        writer.WriteEndObject();
    });

    private static byte[] SerializeReceipt(JournalReceipt receipt) => WriteJson(writer =>
    {
        writer.WriteStartObject(); WriteIdentity(writer, receipt.Value.OperationGuid, receipt.Value.RequestSha256);
        writer.WriteNumber("sequence", receipt.EventCount); writer.WriteString("previousRecordSha256", receipt.LastHash);
        writer.WritePropertyName("receipt"); WriteReceiptBody(writer, receipt.Value); writer.WriteEndObject();
    });

    private static void WriteReceiptBody(Utf8JsonWriter writer, NativePrivateApplyTerminalReceipt value)
    {
        writer.WriteStartObject(); writer.WriteNumber("phase", (int)value.Phase); WriteSelection(writer, value.Selection);
        writer.WritePropertyName("steps"); writer.WriteStartArray();
        foreach (NativePrivateApplyRecoveryStep step in value.Steps)
        {
            writer.WriteStartObject(); writer.WriteNumber("storeKind", (int)step.StoreKind);
            writer.WriteNumber("phase", (int)step.Phase); writer.WriteEndObject();
        }
        writer.WriteEndArray(); writer.WriteEndObject();
    }

    private static void WriteSelection(Utf8JsonWriter writer, NativePrivateMetadataSelection value)
    {
        writer.WritePropertyName("selection"); writer.WriteStartObject();
        writer.WriteBoolean("threadFavorites", value.ThreadFavorites); writer.WriteBoolean("mediaFavorites", value.MediaFavorites);
        writer.WriteBoolean("postBookmarksAndNotes", value.PostBookmarksAndNotes); writer.WriteEndObject();
    }

    private static T ReadJson<T>(byte[] bytes, Func<JsonElement, T> parse)
    {
        CheckRecordSize(bytes);
        try
        {
            // JsonDocument rejects BOM too; check it explicitly for clarity.
            if (bytes.Length >= 3 && bytes[0] == 0xef && bytes[1] == 0xbb && bytes[2] == 0xbf) throw Invalid();
            _ = StrictUtf8.GetCharCount(bytes);
            using JsonDocument document = JsonDocument.Parse(bytes, new JsonDocumentOptions
            { MaxDepth = 16, AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow });
            return parse(document.RootElement);
        }
        catch (Exception error) when (error is JsonException or DecoderFallbackException or ArgumentException
            or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException)
        { throw Invalid(); }
    }

    private static JournalClaim ParseClaim(byte[] bytes) => ReadJson(bytes, root =>
    {
        Fields(root, "schema", "operationId", "requestSha256", "contentPlanSha256", "requestBytesSha256", "requestLength",
            "selectedPayloadSha256", "selectedPayloadLength", "selection");
        (Guid id, string hash) = ParseIdentity(root);
        return new JournalClaim(id, hash, HashText(root, "contentPlanSha256"), HashText(root, "requestBytesSha256"),
            Range(root, "requestLength", 1, NativePrivateApplyRequestCodec.MaximumDocumentBytes),
            HashText(root, "selectedPayloadSha256"), Range(root, "selectedPayloadLength", 1, NativePrivateMetadataCodec.MaximumDocumentBytes),
            ParseSelection(root.GetProperty("selection")));
    });

    private static JournalEvent ParseEvent(byte[] bytes) => ReadJson(bytes, root =>
    {
        Fields(root, "schema", "operationId", "requestSha256", "sequence", "previousRecordSha256", "operationObservation", "steps", "receipt");
        (Guid id, string hash) = ParseIdentity(root);
        int sequence = Range(root, "sequence", 1, MaximumEvents);
        string previous = ParsePrevious(root, sequence - 1);
        int observation = Number(root, "operationObservation");
        if (!Enum.IsDefined((NativePrivateApplyOperationObservation)observation)) throw Invalid();
        JsonElement array = root.GetProperty("steps");
        if (array.ValueKind != JsonValueKind.Array || array.GetArrayLength() > 3) throw Invalid();
        var steps = ImmutableArray.CreateBuilder<NativePrivateApplyStepObservation>();
        foreach (JsonElement element in array.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt32(out int value)
                || !Enum.IsDefined((NativePrivateApplyStepObservation)value)) throw Invalid();
            steps.Add((NativePrivateApplyStepObservation)value);
        }
        JsonElement body = root.GetProperty("receipt");
        NativePrivateApplyTerminalReceipt? receipt = body.ValueKind == JsonValueKind.Null ? null : ParseReceiptBody(body, id, hash);
        return new JournalEvent(id, hash, sequence, previous, (NativePrivateApplyOperationObservation)observation, steps.ToImmutable(), receipt);
    });

    private static JournalReceipt ParseReceipt(byte[] bytes) => ReadJson(bytes, root =>
    {
        Fields(root, "schema", "operationId", "requestSha256", "sequence", "previousRecordSha256", "receipt");
        (Guid id, string hash) = ParseIdentity(root);
        int count = Range(root, "sequence", 0, MaximumEvents);
        string previous = ParsePrevious(root, count);
        NativePrivateApplyTerminalReceipt value = ParseReceiptBody(root.GetProperty("receipt"), id, hash);
        CheckFinalReceipt(value);
        return new JournalReceipt(value, count, previous);
    });

    private static NativePrivateApplyTerminalReceipt ParseReceiptBody(JsonElement body, Guid id, string hash)
    {
        Fields(body, "phase", "selection", "steps");
        int phase = Number(body, "phase");
        if (!Enum.IsDefined((NativePrivateApplyOperationPhase)phase)) throw Invalid();
        NativePrivateMetadataSelection selection = ParseSelection(body.GetProperty("selection"));
        JsonElement array = body.GetProperty("steps");
        if (array.ValueKind != JsonValueKind.Array || array.GetArrayLength() > 3) throw Invalid();
        var steps = ImmutableArray.CreateBuilder<NativePrivateApplyRecoveryStep>();
        foreach (JsonElement item in array.EnumerateArray())
        {
            Fields(item, "storeKind", "phase");
            int kind = Number(item, "storeKind"), stepPhase = Number(item, "phase");
            if (!Enum.IsDefined((NativePrivateApplyStoreKind)kind) || !Enum.IsDefined((NativePrivateApplyStepPhase)stepPhase)) throw Invalid();
            steps.Add(new((NativePrivateApplyStoreKind)kind, (NativePrivateApplyStepPhase)stepPhase));
        }
        return new(id, hash, selection, (NativePrivateApplyOperationPhase)phase, steps.ToImmutable());
    }

    private static string ParsePrevious(JsonElement element, int previousSequence)
    {
        string previous = Text(element, "previousRecordSha256");
        if (previousSequence == 0 ? previous.Length != 0 : !IsHash(previous)) throw Invalid();
        return previous;
    }

    private static (Guid, string) ParseIdentity(JsonElement root)
    {
        if (Number(root, "schema") != 1) throw Invalid();
        string text = Text(root, "operationId"); Guid id = ParseOperationName(text);
        return (id, HashText(root, "requestSha256"));
    }

    private static NativePrivateMetadataSelection ParseSelection(JsonElement value)
    {
        Fields(value, "threadFavorites", "mediaFavorites", "postBookmarksAndNotes");
        return new(Boolean(value, "threadFavorites"), Boolean(value, "mediaFavorites"), Boolean(value, "postBookmarksAndNotes"));
    }
    private static void Fields(JsonElement element, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object) throw Invalid();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonProperty property in element.EnumerateObject())
            if (!seen.Add(property.Name) || !names.Contains(property.Name, StringComparer.Ordinal)) throw Invalid();
        if (seen.Count != names.Length) throw Invalid();
    }
    private static string Text(JsonElement element, string name)
        => element.GetProperty(name).ValueKind == JsonValueKind.String ? element.GetProperty(name).GetString()! : throw Invalid();
    private static string HashText(JsonElement element, string name)
    { string text = Text(element, name); if (!IsHash(text)) throw Invalid(); return text; }
    private static bool IsHash(string text) => text.Length == 64 && text.All(value => value is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static int Number(JsonElement element, string name)
        => element.GetProperty(name).ValueKind == JsonValueKind.Number && element.GetProperty(name).TryGetInt32(out int value) ? value : throw Invalid();
    private static int Range(JsonElement element, string name, int min, int max)
    { int value = Number(element, name); if (value < min || value > max) throw Invalid(); return value; }
    private static bool Boolean(JsonElement element, string name)
        => element.GetProperty(name).ValueKind is JsonValueKind.True or JsonValueKind.False ? element.GetProperty(name).GetBoolean() : throw Invalid();
}
