using System;
using System.Collections.Generic;
using System.Linq;
using MeowChan.Desktop.Localization;
using MeowChan.PluginHost.Configuration;
using MeowChan.PluginSdk;

namespace MeowChan.Desktop.Plugins;

/// <summary>Editable, next-start-only configuration for a bundled declaration review.</summary>
internal sealed class BundledPluginChangeDraft
{
    internal PluginDescription Description { get; }
    internal PluginSettingsDraft Plugin { get; }
    internal IReadOnlyDictionary<string, PluginSettingsDraft> Sources { get; }
    internal IReadOnlyList<(string SourceId, SettingsSnapshot Settings)> RemovedSources { get; }
    internal bool RemovedValuesAcknowledged { get; private set; }

    internal bool NeedsRemovedValueAcknowledgement => !RemovedValuesAcknowledged &&
        (Plugin.UnmappedValues.Count > 0 || Sources.Values.Any(draft => draft.UnmappedValues.Count > 0) || RemovedSources.Count > 0);

    internal BundledPluginChangeDraft(BundledPluginChangeReview review)
    {
        Description = review.NewDescription;
        Plugin = new(review.NewDescription.Settings, SettingScope.Plugin, review.PreviousConfiguration.PluginSettings);
        Sources = review.NewDescription.Sources.ToDictionary(source => source.SourceId,
            source => new PluginSettingsDraft(source.Settings, SettingScope.Source,
                review.PreviousConfiguration.SourceSettings.GetValueOrDefault(source.SourceId)), StringComparer.Ordinal);
        RemovedSources = review.PreviousConfiguration.SourceSettings
            .Where(pair => pair.Value.Values.Count > 0 && !Sources.ContainsKey(pair.Key))
            .Select(pair => (pair.Key, pair.Value)).ToArray();
    }

    internal void AcknowledgeRemovedValues()
    {
        Plugin.AcknowledgeRemovedFields();
        foreach (PluginSettingsDraft source in Sources.Values) source.AcknowledgeRemovedFields();
        RemovedValuesAcknowledged = true;
    }

    internal PluginConfiguration Configuration(out IReadOnlyList<PluginConfigurationFieldError> errors)
    {
        SettingsSnapshot plugin = Plugin.Snapshot(out IReadOnlyList<FieldValidationError> pluginErrors);
        List<PluginConfigurationFieldError> all = new(pluginErrors.Select(error => new PluginConfigurationFieldError(string.Empty, error)));
        if (!RemovedValuesAcknowledged)
        {
            if (Plugin.UnmappedValues.Count > 0)
                all.Add(new(string.Empty, new FieldValidationError(string.Empty, "removed_setting",
                    Texts.T("plugins.removedSettings"))));
            foreach ((string source, PluginSettingsDraft draft) in Sources)
                if (draft.UnmappedValues.Count > 0)
                    all.Add(new(source, new FieldValidationError(string.Empty, "removed_setting",
                        Texts.T("plugins.removedSettings"))));
            foreach ((string source, _) in RemovedSources)
                all.Add(new(source, new FieldValidationError(string.Empty, "removed_source",
                    Texts.T("plugins.removedSettings"))));
        }

        Dictionary<string, SettingsSnapshot> sources = new(StringComparer.Ordinal);
        foreach ((string source, PluginSettingsDraft draft) in Sources)
        {
            SettingsSnapshot settings = draft.Snapshot(out IReadOnlyList<FieldValidationError> sourceErrors);
            sources[source] = PluginSettingsMigration.WithDefaults(draft.Schema, settings);
            all.AddRange(sourceErrors.Select(error => new PluginConfigurationFieldError(source, error)));
        }
        errors = all;
        return new("bundled-review-" + Guid.NewGuid().ToString("N"),
            PluginSettingsMigration.WithDefaults(Description.Settings, plugin), sources);
    }
}
