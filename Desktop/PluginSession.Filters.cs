using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeowChan.Archive;
using MeowChan.Boards.Base;
using MeowChan.PluginHost.Activation;
using MeowChan.PluginHost.Configuration;
using MeowChan.PluginHost.Contracts;
using MeowChan.PluginHost.Filters;
using MeowChan.PluginHost.Sources;
using MeowChan.PluginSdk;
using MeowChan.Rules;

namespace MeowChan.Desktop;

internal sealed partial class PluginSession
{
    private PluginProfileSnapshot _grants = new(0, Array.Empty<PluginActivationSnapshot>(), Array.Empty<PluginGrantSnapshot>());

    internal void AttachContent(ThreadContentRuntime content) => _filterStages.Attach(content, ResolveFilterAsync);

    private void OnGrantChanged(PluginGrantChange change)
    {
        lock (_state) _grants = _grants with { Grants = _grants.Grants.Where(grant =>
            !(grant.PluginId == change.Grant.PluginId && grant.FilterKey == change.Grant.FilterKey && grant.Scope == change.Grant.Scope))
            .Append(change.Grant).ToArray() };
    }

    private async Task<FilterPluginBinding?> ResolveFilterAsync(FilterBindingRequest request, CancellationToken token)
    {
        MediaImageFilterRule rule = request.Decision.Selection.EffectiveRule.ImageFilter;
        if (rule.PluginId == null || rule.FilterKey == null || request.Item.Thread is not BaseThread thread) return null;
        PluginActivationSnapshot activation;
        PluginGrantAuthorizationSnapshot grant;
        FilterDescription? filter;
        lock (_state)
        {
            if (!_active.TryGetValue(rule.PluginId, out activation!) || !activation.Enabled || activation.Active == null) return null;
            filter = activation.Active.Description.Filters.SingleOrDefault(value => value.Key == rule.FilterKey);
            if (filter == null) return null;
            grant = Grant(rule.PluginId, rule.FilterKey, request);
            if (!grant.Allows(filter.RequiredAccess)) return null;
        }
        SettingsSnapshot settings = FilterRuleSettings.Create(rule, filter);
        if (SdkValidation.ValidateSettings(filter.Settings, settings, SettingScope.FilterRule).Count != 0) return null;
        IPluginWorkerClient worker = await GetWorkerAsync(rule.PluginId, token).ConfigureAwait(false);
        long generation = PluginRegistryAdapter.GetWatchGeneration(thread);
        PluginReleaseSnapshot release = activation.Active!;
        PluginOperationContext operation = new(thread.Url, generation, request.Decision.Selection.EffectiveRule.Revision, grant.Revision);
        bool Current()
        {
            lock (_state) return !_disposed && _active.TryGetValue(rule.PluginId, out PluginActivationSnapshot? current)
                && current.Enabled && current.LifecycleRevision == activation.LifecycleRevision && current.Active?.ExecutionRevision == release.ExecutionRevision
                && PluginRegistryAdapter.GetWatchGeneration(thread) == generation
                && Grant(rule.PluginId, rule.FilterKey, request).Revision == grant.Revision;
        }
        return new FilterPluginBinding(rule.PluginId, release.ExecutionRevision, release.Configuration.Revision,
            grant.Revision, operation, settings, Current, worker.EvaluateAsync);
    }

    private PluginGrantAuthorizationSnapshot Grant(string plugin, string key, FilterBindingRequest request) =>
        PluginGrantAuthorization.Resolve(_grants, plugin, key, request.Item.ImageBoard.Name,
            request.Item.Thread.Board.BoardCode, request.RuleId);

}
