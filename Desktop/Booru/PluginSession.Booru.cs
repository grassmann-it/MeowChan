using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeowChan.Booru;
using MeowChan.Desktop.Booru;
using MeowChan.Desktop.Plugins;
using MeowChan.PluginHost.Activation;
using MeowChan.PluginHost.Configuration;
using MeowChan.PluginHost.Contracts;
using MeowChan.PluginHost.Http;
using MeowChan.PluginHost.Sources.Booru;
using MeowChan.PluginSdk;

namespace MeowChan.Desktop;

// The plugin-runtime half of the Danbooru composition. A post-search source is never a chan board adapter
// (see StartCoreAsync); instead its plugin's worker is started with booru host services, and the same
// services instance is handed to the desktop composition so the host-side search service and the worker
// share one job registry and one bounded metadata transport.
internal sealed partial class PluginSession
{
    private readonly Dictionary<string, BooruHostServices> _booruServices = new(StringComparer.Ordinal);
    private BooruProviderBinding? _booru;
    private string? _booruPluginId;

    /// <summary>The Danbooru binding, when an active plugin declares the Danbooru post-search source; otherwise null.</summary>
    internal BooruProviderBinding? Booru { get { lock (_state) return _booru; } }

    // Called from StartCoreAsync for each declared post-search source, in activation order.
    private void RegisterBooruSource(PluginActivationSnapshot activation, PluginDescription description, SourceDescription source,
        IReadOnlyList<(PluginActivationSnapshot Activation, PluginDescription Description)> descriptions, PluginProfileSnapshot activated)
    {
        // Only the Danbooru composition exists; other post-search sources stay without a host composition.
        if (!string.Equals(source.SourceId, DanbooruPostReference.ProviderId, StringComparison.Ordinal)) return;
        string[] owners = descriptions.Where(item => item.Description.Sources.Any(value => value.SourceId == source.SourceId))
            .Select(item => item.Activation.PluginId).Distinct(StringComparer.Ordinal).ToArray();
        if (owners.Length > 1 && (!activated.SelectedSourceOwners.TryGetValue(source.SourceId, out string? selected)
            || selected != activation.PluginId)) return;
        string pluginId = activation.PluginId;
        lock (_state) if (_booru != null) return;
        BooruHostHttp booruHttp = new(_transport, _httpPolicy, description.Sources,
            maximumCooldownProvider: () => TimeSpan.FromHours(_config.BooruRetryAfterCooldownHours));
        BooruHostServices services = new(new HostServices(GetPluginHttp(description), _filterStages, pluginId), booruHttp);
        BooruProviderBinding binding = new(source, services, _transport, _httpPolicy,
            token => ResolveBooruWorkerAsync(pluginId, token), () => IsBooruPluginEnabled(pluginId), _logger)
        {
            PluginId = pluginId,
            SdkMismatchObserved = () => SdkMismatches.Record(pluginId, RunningExecutionRevision(pluginId))
        };
        lock (_state)
        {
            _services.Add(booruHttp);
            _booruServices[pluginId] = services;
            _booruPluginId = pluginId;
            _booru = binding;
        }
    }

    /// <summary>Session-scope sdk-mismatch observations of running plugin builds (presentation only).</summary>
    internal PluginSdkMismatchLedger SdkMismatches { get; } = new();

    /// <summary>The build this session runs for the plugin (fixed at startup), or null when none runs.</summary>
    internal string? RunningExecutionRevision(string pluginId)
    {
        lock (_state)
            return !_disposed && _active.TryGetValue(pluginId, out PluginActivationSnapshot? activation)
                ? activation.Active?.ExecutionRevision : null;
    }

    private bool TryGetBooruServices(string pluginId, out IPluginHostServices services)
    {
        lock (_state)
        {
            if (_booruServices.TryGetValue(pluginId, out BooruHostServices? booru)) { services = booru; return true; }
        }
        services = null!;
        return false;
    }

    private bool IsBooruPluginEnabled(string pluginId)
    {
        lock (_state)
            return !_disposed && _active.TryGetValue(pluginId, out PluginActivationSnapshot? activation)
                && activation.Enabled && activation.Active != null;
    }

    /// <summary>The running worker, started on demand; null while disabled, stopped or not startable.</summary>
    private async Task<IPluginWorkerClient?> ResolveBooruWorkerAsync(string pluginId, CancellationToken cancellationToken)
    {
        if (!IsBooruPluginEnabled(pluginId)) return null;
        try { return await GetWorkerAsync(pluginId, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is PluginWorkerException or OperationCanceledException or InvalidOperationException
            or IOException or UnauthorizedAccessException or ObjectDisposedException or System.ComponentModel.Win32Exception)
        {
            _logger.Warning(exception, "The Danbooru plugin worker is not available");
            return null;
        }
    }

    // Called after OnLifecycleChanged applied the new state, outside _state.
    private void NotifyBooruLifecycle(string pluginId)
    {
        BooruProviderBinding? binding;
        lock (_state) binding = _booruPluginId == pluginId ? _booru : null;
        if (binding == null) return;
        try { binding.RaiseAvailabilityChanged(); }
        catch (Exception exception) { _logger.Error(exception, "Danbooru availability observer failed"); }
    }
}
