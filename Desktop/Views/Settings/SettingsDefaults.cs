namespace MeowChan;

/// <summary>
/// The one source of defaults used by the settings reset surface.
///
/// This intentionally keeps a real <see cref="Config"/> created with its normal
/// constructor.  Reset code then names every editable field explicitly; it does
/// not discover or copy arbitrary properties from a foreign object.  The same
/// constructor also preserves Config's type conversion and default mapping (for
/// example the bandwidth draft is still represented in MiB/s by the editor).
/// </summary>
internal sealed class SettingsDefaults
{
    internal SettingsDefaults()
    {
        Values = new Config();
    }

    internal Config Values { get; }

    internal decimal BandwidthLimiterMiB => Values.BandwidthLimiter / (1024m * 1024m);
}
