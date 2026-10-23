using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace MeowChan.Desktop.Commands;

/// <summary>Retains unfamiliar and invalid values verbatim; editing is explicit and draft-only.</summary>
[JsonConverter(typeof(ShortcutBindingsSectionConverter))]
public sealed class ShortcutBindingsSection
{
    public JsonElement Raw { get; }
    public ShortcutBindingsSection() : this(Empty()) { }
    private static JsonElement Empty()
    { using var document = JsonDocument.Parse("{\"SchemaVersion\":1,\"Overrides\":{}}"); return document.RootElement.Clone(); }
    public ShortcutBindingsSection(JsonElement raw) => Raw = raw.Clone();
    public bool IsEditable => Raw.ValueKind == JsonValueKind.Object && Raw.TryGetProperty("SchemaVersion", out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int n) && n == 1;
    public ShortcutBindingsSection Clone() => new(Raw);
    public IEnumerable<JsonProperty> Overrides => Raw.ValueKind == JsonValueKind.Object && Raw.TryGetProperty("Overrides", out var o) && o.ValueKind == JsonValueKind.Object ? o.EnumerateObject() : Enumerable.Empty<JsonProperty>();
    public bool HasOverride(string id) => Overrides.Any(p => p.Name == id);
    public ShortcutBindingsSection Set(string id, IReadOnlyList<ShortcutGesture>? gestures)
    {
        if (!IsEditable) throw new InvalidOperationException("This shortcut schema is read-only.");
        var root = JsonNode.Parse(Raw.GetRawText())!.AsObject();
        var entries = root["Overrides"] as JsonObject;
        if (entries == null) { entries = new JsonObject(); root["Overrides"] = entries; }
        if (gestures is null) entries.Remove(id);
        else
        {
            var list = new JsonArray();
            foreach (var g in gestures)
            {
                var modifiers = new JsonArray();
                foreach (string m in g.Modifiers) modifiers.Add(m);
                var node = new JsonObject { ["Key"] = g.Key, ["Modifiers"] = modifiers };
                if (g.Symbol is not null) node["Symbol"] = g.Symbol;
                list.Add(node);
            }
            entries[id] = new JsonObject { ["Mode"] = gestures.Count == 0 ? "Unbound" : "Replace", ["Gestures"] = list };
        }
        using var document = JsonDocument.Parse(root.ToJsonString());
        return new(document.RootElement);
    }
    public ShortcutBindingsSection ResetKnown()
    {
        var result = this;
        foreach (var command in CommandCatalog.All) result = result.Set(command.Id, null);
        return result;
    }
}

public sealed class ShortcutBindingsSectionConverter : JsonConverter<ShortcutBindingsSection>
{
    public override bool HandleNull => true;
    public override ShortcutBindingsSection Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
    { using var doc = JsonDocument.ParseValue(ref reader); return new(doc.RootElement); }
    public override void Write(Utf8JsonWriter writer, ShortcutBindingsSection value, JsonSerializerOptions options) => value.Raw.WriteTo(writer);
}
