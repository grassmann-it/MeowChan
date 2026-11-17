using System;
using System.Collections.Generic;

namespace MeowChan.Desktop.Plugins;

/// <summary>
/// Session-scope presentation fact: the exact running build of a plugin was observed failing with <c>sdk-mismatch</c>
/// (its binary was compiled against another MeowChan.PluginSdk). It is evidence for the UI only: it never selects,
/// repairs, unpins or trusts a build, and it ends with the session. A later start runs another build or the same
/// one again, which is observed anew.
/// </summary>
internal sealed class PluginSdkMismatchLedger
{
    private readonly object _sync = new();
    private readonly HashSet<(string PluginId, string ExecutionRevision)> _observed = new();

    /// <summary>Raised (on any thread) when a new plugin build was recorded.</summary>
    internal event Action? Changed;

    /// <summary>Records one observation; true when it is new for this session.</summary>
    internal bool Record(string pluginId, string? executionRevision)
    {
        if (string.IsNullOrWhiteSpace(pluginId) || string.IsNullOrWhiteSpace(executionRevision)) return false;
        bool added;
        lock (_sync) added = _observed.Add((pluginId, executionRevision));
        if (added)
        {
            try { Changed?.Invoke(); }
            catch (Exception) { } // Presentation only: an observer failure never reaches the worker call.
        }
        return added;
    }

    internal bool Has(string pluginId, string? executionRevision)
    {
        if (string.IsNullOrWhiteSpace(pluginId) || string.IsNullOrWhiteSpace(executionRevision)) return false;
        lock (_sync) return _observed.Contains((pluginId, executionRevision));
    }
}
