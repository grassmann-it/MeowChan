using System;
using System.Collections.Generic;
using System.Linq;
using MeowChan.Desktop.Plugins;

namespace MeowChan.Desktop;

internal sealed partial class PluginSession
{
    /// <summary>
    /// Observes only clients owned by this production session. Manager snapshots also contain
    /// isolated tests, so matching one of those by build alone cannot establish production state.
    /// No worker is started; a lazy or not-yet-registered client simply has no observation.
    /// </summary>
    internal IReadOnlyList<PluginProductionWorkerSnapshot> GetProductionWorkerSnapshots()
    {
        lock (_state)
        {
            if (_disposed) return Array.Empty<PluginProductionWorkerSnapshot>();
            return _clients.Where(item => _active.TryGetValue(item.Key, out var activation) &&
                    activation.Enabled && activation.Active != null)
                .Select(item => new PluginProductionWorkerSnapshot(item.Key, item.Value.Snapshot))
                .Where(item => _active[item.PluginId].Active is { } release &&
                    item.Worker.Identity.ExecutionRevision == release.ExecutionRevision &&
                    item.Worker.Identity.ConfigurationRevision == release.Configuration.Revision)
                .ToArray();
        }
    }
}
