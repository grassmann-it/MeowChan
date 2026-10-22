using System.Globalization;
using MeowChan.PluginSdk;
using MeowChan.Rules;

namespace MeowChan.PluginHost.Filters;

public static class FilterRuleSettings
{
    public static SettingsSnapshot Create(MediaImageFilterRule rule, FilterDescription description)
    {
        List<SettingValue> values = new();
        foreach (SettingDefinition field in description.Settings.Fields)
        {
            if (!rule.Parameters.TryGetValue(field.Key, out string? value))
            {
                if (field.Default != null) values.Add(field.Default);
                continue;
            }
            values.Add(field.Kind switch {
                SettingKind.Integer => new(field.Key, field.Kind, Integer: long.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture)),
                SettingKind.Boolean => new(field.Key, field.Kind, Boolean: bool.Parse(value)),
                _ => new(field.Key, field.Kind, Text: value)
            });
        }
        if (rule.Parameters.Keys.Any(key => description.Settings.Fields.All(field => field.Key != key)))
            throw new ArgumentException("The filter rule contains unknown fields; correct the retained values explicitly.");
        SettingsSnapshot snapshot = new(rule.SchemaVersion, values.AsReadOnly());
        IReadOnlyList<ValidationIssue> issues = SdkValidation.ValidateSettings(description.Settings, snapshot, SettingScope.FilterRule);
        if (issues.Count != 0) throw new ArgumentException(string.Join("; ", issues.Select(issue => issue.Path + ": " + issue.Message)));
        return snapshot;
    }
}
