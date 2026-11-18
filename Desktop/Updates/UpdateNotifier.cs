using System;
using System.Collections.Generic;

namespace MeowChan.Desktop.Updates;

/// <summary>
/// Decides when a found update is announced with a toast. The quiet footer notice and tray entry follow the
/// view itself; this only guards the one interruption: each version at most once per app start (so "Later" holds
/// until the next start), never while checks are disabled, only on the user's own request when automatic checks are
/// off, and never when the user turned information notifications off. A skipped version never reaches the view.
/// </summary>
internal sealed class UpdateNotifier
{
    private readonly HashSet<string> _announced = new(StringComparer.Ordinal);

    /// <returns>The update to announce now, or null.</returns>
    internal UpdateInfo? Evaluate(UpdateView view, bool automaticChecks, bool informationAllowed)
    {
        if (view.EnvironmentDisabled || view.Update is not { } update) return null;
        bool foundByManualCheck = view.ManualResult is { Status: UpdateCheckStatus.Available };
        if (!automaticChecks && !foundByManualCheck) return null;
        if (!informationAllowed) return null;
        return _announced.Add(update.Version.ToString()) ? update : null;
    }
}
