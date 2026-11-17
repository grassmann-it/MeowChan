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
    public ShortcutBindingsSection Clone() => new(UnifyMediaOpen().Raw);
    public IEnumerable<JsonProperty> Overrides => Raw.ValueKind == JsonValueKind.Object && Raw.TryGetProperty("Overrides", out var o) && o.ValueKind == JsonValueKind.Object ? o.EnumerateObject() : Enumerable.Empty<JsonProperty>();
    public bool HasOverride(string id) => Overrides.Any(p => p.Name == id);
    public ShortcutBindingsSection Set(string id, IReadOnlyList<ShortcutGesture>? gestures)
    {
        if (!IsEditable) throw new InvalidOperationException("This shortcut schema is read-only.");
        id = CommandCatalog.CanonicalId(id);
        var root = JsonNode.Parse(UnifyMediaOpen().Raw.GetRawText())!.AsObject();
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
    /// <summary>The accessibility switch for the standard single-character shortcuts; on unless the profile says false.
    /// Stored as an additive top-level field, so the schema version stays 1 and older builds keep it as unknown data.</summary>
    public bool SingleKeyShortcuts => !(Raw.ValueKind == JsonValueKind.Object && Raw.TryGetProperty("SingleKeyShortcuts", out var v) && v.ValueKind == JsonValueKind.False);
    public ShortcutBindingsSection WithSingleKeyShortcuts(bool enabled)
    {
        if (!IsEditable) throw new InvalidOperationException("This shortcut schema is read-only.");
        var root = JsonNode.Parse(Raw.GetRawText())!.AsObject();
        if (enabled) root.Remove("SingleKeyShortcuts"); else root["SingleKeyShortcuts"] = false;
        using var document = JsonDocument.Parse(root.ToJsonString());
        return new(document.RootElement);
    }
    /// <summary>Whether a saved binding belongs to a command that was removed on purpose.</summary>
    public bool HasRetiredOverrides => Overrides.Any(p => EffectiveShortcutBindings.RetiredCommands.Contains(p.Name));
    /// <summary>The section without the bindings of retired commands; everything else, unknown ids included, stays verbatim.</summary>
    public ShortcutBindingsSection WithoutRetired()
    {
        if (!IsEditable) return this;
        var result = UnifyMediaOpen();
        if (!result.HasRetiredOverrides) return result;
        foreach (string id in EffectiveShortcutBindings.RetiredCommands) result = result.Set(id, null);
        return result;
    }
    /// <summary>Migrate the former Media-only name. An explicit gallery choice wins
    /// if both exist; otherwise retain the old Media choice, including Unbound.
    /// Unknown schema/data is never rewritten.</summary>
    internal ShortcutBindingsSection UnifyMediaOpen()
    {
        if (!IsEditable || !HasOverride("media.open")) return this;
        var root = JsonNode.Parse(Raw.GetRawText())!.AsObject();
        var entries = root["Overrides"]!.AsObject();
        if (!HasOverride("gallery.open")) entries["gallery.open"] = entries["media.open"]?.DeepClone();
        entries.Remove("media.open");
        using var document = JsonDocument.Parse(root.ToJsonString());
        return new(document.RootElement);
    }

    public ShortcutBindingsSection ResetKnown()
    {
        var result = UnifyMediaOpen();
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
