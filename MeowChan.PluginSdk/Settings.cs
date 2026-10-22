namespace MeowChan.PluginSdk;

public enum SettingKind { Text, Integer, Boolean, Enum, Uri }
public enum SettingScope { Plugin, Source, FilterRule }

public sealed record SettingsSchema(string Version, IReadOnlyList<SettingDefinition> Fields);

/// <summary>Enum option values and keys are stable identities, not translated labels.</summary>
public sealed record SettingOption(string Value, string Label);

public sealed record SettingDefinition(string Key, SettingScope Scope, SettingKind Kind,
    string Label, string? Help = null, bool IsRequired = false, SettingValue? Default = null,
    long? Minimum = null, long? Maximum = null, int? MaximumLength = null,
    IReadOnlyList<SettingOption>? Options = null);

/// <summary>
/// Exactly the matching payload is set. Text contains Text, Enum and absolute URI values;
/// Integer and Boolean use their own fields. Missing value entries mean unset, never reset.
/// </summary>
public sealed record SettingValue(string Key, SettingKind Kind, string? Text = null,
    long? Integer = null, bool? Boolean = null);

public sealed record SettingsSnapshot(string SchemaVersion, IReadOnlyList<SettingValue> Values);

public sealed record FieldValidationError(string FieldKey, string Code, string Message);

public sealed record SettingsValidationRequest(SettingScope Scope, SettingsSnapshot Settings,
    string? SourceId = null, string? FilterKey = null);
