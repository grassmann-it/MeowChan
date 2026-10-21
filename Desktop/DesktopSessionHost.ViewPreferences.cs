using System;
using System.Threading.Tasks;

namespace MeowChan.Desktop;

internal sealed partial class DesktopSessionHost
{
    /// <summary>Persists a direct view preference against the latest live configuration.</summary>
    internal async Task SaveViewPreferencesAsync(Action<Config> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        await _commands.WaitAsync().ConfigureAwait(false);
        try
        {
            EnsureReady();
            Config candidate = Config.Clone();
            change(candidate);
            candidate.Validate();
            candidate.SaveConfig(_profileLease.ProfileDirectory);
            Config.CopyFrom(candidate);
        }
        finally { _commands.Release(); }
        SignalChanged();
    }
}
