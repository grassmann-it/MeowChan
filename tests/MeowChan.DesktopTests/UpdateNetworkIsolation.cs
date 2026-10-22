using System.Runtime.CompilerServices;

namespace MeowChan.DesktopTests;

internal static class UpdateNetworkIsolation
{
    // Ordinary DesktopSessionHost fixtures must stay offline even when a test lasts over 30 seconds.
    // Update tests explicitly inject their HTTP handler and disable-environment delegate.
    [ModuleInitializer]
    internal static void DisableProductionUpdateTransport() =>
        Environment.SetEnvironmentVariable("MEOWCHAN_DISABLE_UPDATE_CHECK", "1");
}
