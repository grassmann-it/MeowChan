using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using MeowChan.PluginSdk;

namespace MeowChan.Desktop.Plugins;

/// <summary>A transient editor buffer. Only the revisioned host store persists settings.</summary>
internal sealed class PluginSettingsDraft
{
    private readonly Dictionary<string, string> _text = new(StringComparer.Ordinal);
    private readonly HashSet<string> _incompatible = new(StringComparer.Ordinal);
    private readonly HashSet<string> _set = new(StringComparer.Ordinal);
    internal SettingsSchema Schema { get; }
    internal SettingScope Scope { get; }
    internal bool Dirty { get; private set; }
    internal event Action? Changed;

    internal PluginSettingsDraft(SettingsSchema schema, SettingScope scope, SettingsSnapshot? saved)
    {
        Schema = schema;
        Scope = scope;
        foreach (SettingValue value in saved?.Values ?? Array.Empty<SettingValue>())
        {
            _set.Add(value.Key);
            _text[value.Key] = value.Kind switch
            {
                SettingKind.Integer => value.Integer?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                SettingKind.Boolean => value.Boolean == true ? "true" : "false",
                _ => value.Text ?? string.Empty
            };
        }
        foreach (SettingDefinition field in schema.Fields)
        {
            SettingValue? value = saved?.Values.FirstOrDefault(value => value.Key == field.Key) ?? field.Default;
            if (value == null) continue;
            _set.Add(field.Key);
            if (value.Kind != field.Kind) _incompatible.Add(field.Key);
            _text[field.Key] = value.Kind switch
            {
                SettingKind.Integer => value.Integer?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                SettingKind.Boolean => value.Boolean == true ? "true" : "false",
                _ => value.Text ?? string.Empty
            };
        }
    }

    internal IReadOnlyDictionary<string, string> UnmappedValues => _text.Where(pair => !Schema.Fields.Any(definition => definition.Key == pair.Key))
        .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
    internal void AcknowledgeRemovedFields()
    {
        foreach (string key in UnmappedValues.Keys) { _text.Remove(key); _set.Remove(key); }
        Dirty = true; Changed?.Invoke();
    }
    internal void SetInitialText(string key, string text) { _text[key] = text; _set.Add(key); }
    internal string Text(string key) => _text.GetValueOrDefault(key) ?? string.Empty;
    internal bool IsSet(string key) => _set.Contains(key);
    internal void Set(string key, string value, bool isSet = true)
    {
        if (Text(key) == value && IsSet(key) == isSet && !_incompatible.Contains(key)) return;
        _incompatible.Remove(key);
        _text[key] = value;
        if (isSet) _set.Add(key); else _set.Remove(key);
        Dirty = true;
        Changed?.Invoke();
    }

    internal SettingsSnapshot Snapshot(out IReadOnlyList<FieldValidationError> errors)
    {
        List<SettingValue> values = new();
        List<FieldValidationError> found = new();
        foreach (string key in UnmappedValues.Keys) found.Add(new(key, "removed_setting", Localization.Texts.T("plugins.removedField", key)));
        foreach (SettingDefinition field in Schema.Fields)
        {
            if (_incompatible.Contains(field.Key)) found.Add(new(field.Key, "incompatible_setting", Localization.Texts.T("plugins.incompatibleField")));
            if (!_set.Contains(field.Key)) continue;
            string text = Text(field.Key);
            if (field.Kind == SettingKind.Integer)
            {
                if (!long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long integer))
                    found.Add(new(field.Key, "integer", Localization.Texts.T("plugins.integerError")));
                else values.Add(new(field.Key, field.Kind, Integer: integer));
            }
            else if (field.Kind == SettingKind.Boolean)
            {
                if (!bool.TryParse(text, out bool boolean)) found.Add(new(field.Key, "boolean", Localization.Texts.T("plugins.booleanError")));
                else values.Add(new(field.Key, field.Kind, Boolean: boolean));
            }
            else values.Add(new(field.Key, field.Kind, Text: text));
        }
        SettingsSnapshot snapshot = new(Schema.Version, values);
        foreach (ValidationIssue issue in SdkValidation.ValidateSettings(Schema, snapshot, Scope))
        {
            // SDK paths retain values indexes; map to stable field identities before display.
            string? key = Schema.Fields.FirstOrDefault(field => issue.Path == "settings." + field.Key)?.Key;
            int left = issue.Path.IndexOf('[', StringComparison.Ordinal);
            int right = issue.Path.IndexOf(']', StringComparison.Ordinal);
            if (key == null && left >= 0 && right > left && issue.Path.StartsWith("settings.values", StringComparison.Ordinal)
                && int.TryParse(issue.Path.AsSpan(left + 1, right - left - 1), out int index) && index < values.Count)
                key = values[index].Key;
            found.Add(new(key ?? string.Empty, issue.Code, issue.Message));
        }
        errors = found;
        return snapshot;
    }
}
