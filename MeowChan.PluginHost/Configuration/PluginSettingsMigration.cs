using MeowChan.PluginHost.Activation;
using MeowChan.PluginSdk;

namespace MeowChan.PluginHost.Configuration;

/// <summary>Pure migration preview. Incompatible values are reported and the previous release remains untouched.</summary>
public static class PluginSettingsMigration
{
    /// <summary>Rule parameters remain owned by the Core rule store, atomically with its other rule fields.</summary>
    public static IReadOnlyList<ValidationIssue> ValidateFilterRuleSettings(PluginDescription description, string filterKey, SettingsSnapshot settings)
    {
        FilterDescription? filter = description.Filters.SingleOrDefault(item => item.Key == filterKey);
        return filter is null ? [new("filterKey", "unknown_filter", "The filter capability is not declared.")]
            : SdkValidation.ValidateSettings(filter.Settings, settings, SettingScope.FilterRule);
    }

    public static ConfigurationMigrationResult Migrate(PluginReleaseSnapshot? previous, PluginDescription description)
    {
        List<ValidationIssue> issues = SdkValidation.ValidateDescription(description).ToList();
        if (issues.Count != 0) return new(null, issues);
        SettingsSnapshot plugin = MigrateSettings(previous?.Description.Settings, previous?.Configuration.PluginSettings,
            description.Settings, SettingScope.Plugin, "plugin", issues);
        Dictionary<string, SettingsSnapshot> sources = new(StringComparer.Ordinal);
        foreach (SourceDescription source in description.Sources)
        {
            SettingsSnapshot? oldValues = null;
            previous?.Configuration.SourceSettings.TryGetValue(source.SourceId, out oldValues);
            SettingsSchema? oldSchema = previous?.Description.Sources.FirstOrDefault(item => item.SourceId == source.SourceId)?.Settings;
            sources[source.SourceId] = MigrateSettings(oldSchema, oldValues, source.Settings, SettingScope.Source,
                "source." + source.SourceId, issues);
        }
        if (previous is not null)
            foreach ((string sourceId, SettingsSnapshot values) in previous.Configuration.SourceSettings)
                if (!sources.ContainsKey(sourceId) && values.Values.Count != 0)
                    issues.Add(new("source." + sourceId, "removed_source", "Saved source settings require explicit correction because the source was removed."));
        PluginConfiguration configuration = new(Guid.NewGuid().ToString("N"), plugin, sources);
        issues.AddRange(SdkValidation.ValidateConfiguration(description, configuration));
        return new(issues.Count == 0 ? configuration : null, issues);
    }

    public static SettingsSnapshot WithDefaults(SettingsSchema schema, SettingsSnapshot values)
    {
        List<SettingValue> result = values.Values.ToList();
        foreach (SettingDefinition field in schema.Fields)
            if (field.Default is not null && !result.Any(value => value.Key == field.Key)) result.Add(field.Default);
        return new(schema.Version, result);
    }

    private static SettingsSnapshot MigrateSettings(SettingsSchema? oldSchema, SettingsSnapshot? oldValues,
        SettingsSchema schema, SettingScope scope, string path, List<ValidationIssue> issues)
    {
        List<SettingValue> values = new();
        foreach (SettingValue value in oldValues?.Values ?? Array.Empty<SettingValue>())
        {
            SettingDefinition? field = schema.Fields.FirstOrDefault(item => item.Key == value.Key);
            if (field is null)
                issues.Add(new(path + "." + value.Key, "removed_setting", "Saved setting was removed; explicitly correct this configuration. The old release retains its value."));
            else if (field.Kind != value.Kind || oldSchema?.Fields.FirstOrDefault(item => item.Key == value.Key)?.Scope != field.Scope)
                issues.Add(new(path + "." + value.Key, "incompatible_setting", "Setting type or scope changed; an explicit correction is required."));
            else values.Add(value);
        }
        SettingsSnapshot result = WithDefaults(schema, new(schema.Version, values));
        issues.AddRange(SdkValidation.ValidateSettings(schema, result, scope)
            .Select(issue => issue with { Path = path + "." + issue.Path }));
        return result;
    }
}
