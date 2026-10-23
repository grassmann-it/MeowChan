using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MeowChan.Desktop.Localization;
using MeowChan.PluginSdk;
using MeowChan.Rules;

namespace MeowChan;

public partial class RulesPage
{
    private sealed record TemplateFilterValidation(string? Message, IReadOnlyList<FieldValidationError> Errors);

    // The editor needs field keys; preview can use the same validation's summary.
    private async Task<TemplateFilterValidation> ValidateTemplateFilterFieldsAsync(RuleTemplate template)
    {
        string? local = TemplateCompatibilityError(template);
        if (local != null) return new(local, Array.Empty<FieldValidationError>());
        if (template.ImageFilter is not { Mode: MediaImageFilterMode.Plugin } filter)
            return new(null, Array.Empty<FieldValidationError>());
        if (_pluginServices == null) return new(Texts.T("templates.incompatible"), Array.Empty<FieldValidationError>());
        var release = _pluginProfile!.Plugins.First(p => p.PluginId == filter.PluginId && p.Enabled).Active!;
        var description = release.Description.Filters.First(f => f.Key == filter.FilterKey);
        SettingsSnapshot settings = CreateRuleParametersDraft(description.Settings, filter.Parameters).Snapshot(out var errors);
        try
        {
            if (errors.Count == 0)
            {
                await using var worker = await _pluginServices.StartTestWorkerAsync(release.Artifact, release.Configuration, _filterLifetime.Token);
                errors = await worker.ValidateSettingsAsync(new(SettingScope.FilterRule, settings, FilterKey: filter.FilterKey), _filterLifetime.Token);
            }
            return new(errors.Count == 0 ? null : string.Join("; ", errors.Select(e => e.Message)), errors);
        }
        catch (Exception exception) { return new(exception.Message, Array.Empty<FieldValidationError>()); }
    }
}
